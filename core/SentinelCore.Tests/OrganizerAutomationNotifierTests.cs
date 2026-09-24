using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Board automation email, sent for real to a fake mail server, and the two bits of stored data
// that make "notify Terry" possible: which phone is whose, and each account's email address -
// including on databases that were created before either existed.
public sealed class OrganizerAutomationNotifierTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"automation-notifier-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _disposables = [];

    public OrganizerAutomationNotifierTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        try { TempDirectories.Delete(_directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(OrganizerAutomationNotifier Notifier, SqliteAccountStore Accounts)> NewNotifierAsync(object? email)
    {
        var settingsPath = Path.Combine(_directory, "appsettings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new { Email = email }), Ct);
        var accounts = new SqliteAccountStore(_directory);
        _disposables.Add(accounts);
        // Push is never reached by these tests; a dispatcher with nothing registered is enough.
        var dispatcher = new PushNotificationDispatcher(new EmptyDeviceStore(), null!, null!, null!, NullLogger<PushNotificationDispatcher>.Instance);
        return (new OrganizerAutomationNotifier(new AppSettingsFile(settingsPath), dispatcher, accounts, NullLogger<OrganizerAutomationNotifier>.Instance), accounts);
    }

    private static object MailServer(int port) => new
    {
        Enabled = false, // camera alert emails off - automation email must not care
        SmtpHost = "127.0.0.1",
        SmtpPort = port,
        EnableSsl = false,
        Username = "",
        AppPassword = "",
        FromAddress = "organizer@example.com",
        FromName = "Gate Camera",
        ToAddress = "",
        TimeoutSeconds = 15,
    };

    [Fact]
    public async Task Email_GoesToEachPersonSeparately_FromTheOrganizer()
    {
        using var server = new FakeSmtpServer();
        var (notifier, accounts) = await NewNotifierAsync(MailServer(server.Port));
        var terry = await accounts.CreateAsync("terry", "hash", AccountRole.Viewer, null, Ct);
        await accounts.UpdateEmailAsync(terry.Id, "terry@example.com", Ct);

        var result = await notifier.EmailAsync(["terry"], ["grandma@example.com"], "Home: Eggs", "Pick up eggs today.", Ct);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("Emailed terry@example.com, grandma@example.com.", result.Message);
        var conversations = await server.ConversationsAsync(2);
        Assert.Single(conversations, c => c.Contains("RCPT TO:<terry@example.com>") && !c.Contains("grandma"));
        Assert.Single(conversations, c => c.Contains("RCPT TO:<grandma@example.com>") && !c.Contains("terry@"));
        foreach (var conversation in conversations)
        {
            Assert.Contains("MAIL FROM:<organizer@example.com>", conversation);
            Assert.Contains("Subject: Home: Eggs", conversation);
            Assert.Contains("Pick up eggs today.", conversation);
            Assert.Contains("Sentinel Core Organizer", conversation);
            Assert.DoesNotContain("Gate Camera", conversation);
        }
    }

    [Fact]
    public async Task Email_ToSomeoneWithNoAddress_NamesThem_AndStillSendsToTheRest()
    {
        using var server = new FakeSmtpServer();
        var (notifier, accounts) = await NewNotifierAsync(MailServer(server.Port));
        await accounts.CreateAsync("kid", "hash", AccountRole.Viewer, null, Ct);

        var result = await notifier.EmailAsync(["kid"], ["mom@example.com"], "s", "b", Ct);

        Assert.True(result.Ok);
        Assert.Contains("kid has no email address on their account.", result.Message);
        Assert.Contains("RCPT TO:<mom@example.com>", Assert.Single(await server.ConversationsAsync(1)));
    }

    [Fact]
    public async Task Email_WithNobodyToSendTo_Fails_AndSaysWhy()
    {
        using var server = new FakeSmtpServer();
        var (notifier, accounts) = await NewNotifierAsync(MailServer(server.Port));
        await accounts.CreateAsync("kid", "hash", AccountRole.Viewer, null, Ct);

        var result = await notifier.EmailAsync(["kid"], [], "s", "b", Ct);

        Assert.False(result.Ok);
        Assert.Equal("Nobody to email. kid has no email address on their account.", result.Message);
    }

    [Fact]
    public async Task Email_WithNoMailServerSetUp_SendsNothing_AndSaysWhere()
    {
        var (notifier, _) = await NewNotifierAsync(null);

        var result = await notifier.EmailAsync([], ["a@example.com"], "s", "b", Ct);

        Assert.False(result.Ok);
        Assert.Contains("Settings → Email", result.Message);
        Assert.False((await notifier.ChannelsAsync(Ct)).EmailReady);
    }

    [Fact]
    public async Task Email_WhenTheServerRefusesARecipient_ReportsThat_AndStillSendsTheOthers()
    {
        using var server = new FakeSmtpServer(refuse: "bounce@example.com");
        var (notifier, _) = await NewNotifierAsync(MailServer(server.Port));

        var result = await notifier.EmailAsync([], ["bounce@example.com", "ok@example.com"], "s", "b", Ct);

        Assert.False(result.Ok);
        Assert.StartsWith("Emailed ok@example.com. Couldn't reach bounce@example.com", result.Message);
    }

    [Fact]
    public void EmailReady_NeedsAServerAndARealFromAddress_ButNotCameraEmailsOn()
    {
        Assert.False(OrganizerAutomationNotifier.EmailReady(null));
        Assert.False(OrganizerAutomationNotifier.EmailReady(new EmailOptions { SmtpHost = "smtp.example.com", Username = "apikey", AppPassword = "x" }));
        Assert.False(OrganizerAutomationNotifier.EmailReady(new EmailOptions { SmtpHost = "smtp.example.com", Username = "me@example.com" }));
        Assert.True(OrganizerAutomationNotifier.EmailReady(new EmailOptions { SmtpHost = "smtp.example.com", Username = "me@example.com", AppPassword = "x", Enabled = false }));
        Assert.True(OrganizerAutomationNotifier.EmailReady(new EmailOptions { SmtpHost = "relay.lan", FromAddress = "house@example.com" }));
    }

    // ---- accounts' email ------------------------------------------------------------------------

    [Fact]
    public async Task AccountEmail_IsSaved_Cleared_AndCarriedByARestore()
    {
        var accounts = new SqliteAccountStore(_directory);
        _disposables.Add(accounts);
        var terry = await accounts.CreateAsync("terry", "hash", AccountRole.Viewer, "Terry", Ct);
        Assert.Null(terry.Email);

        Assert.True(await accounts.UpdateEmailAsync(terry.Id, "  terry@example.com ", Ct));
        Assert.Equal("terry@example.com", (await accounts.GetByUsernameAsync("terry", Ct))!.Email);
        Assert.Equal("terry@example.com", (await accounts.GetAllAsync(Ct)).Single().Email);

        await accounts.RestoreAsync(terry with { Email = "restored@example.com" }, Ct);
        Assert.Equal("restored@example.com", (await accounts.GetByIdAsync(terry.Id, Ct))!.Email);

        Assert.True(await accounts.UpdateEmailAsync(terry.Id, "", Ct));
        Assert.Null((await accounts.GetByIdAsync(terry.Id, Ct))!.Email);
        Assert.False(await accounts.UpdateEmailAsync(Guid.NewGuid(), "x@example.com", Ct));
    }

    [Fact]
    public async Task AnAccountsDatabaseFromBeforeEmails_UpgradesAndKeepsEveryone()
    {
        using (var old = new SqliteConnection($"Data Source={Path.Combine(_directory, "accounts.db")};Pooling=False"))
        {
            old.Open();
            using var command = old.CreateCommand();
            command.CommandText = """
                CREATE TABLE Accounts (Id TEXT PRIMARY KEY, Username TEXT NOT NULL, PasswordHash TEXT NOT NULL, Role TEXT NOT NULL, DisplayName TEXT, CreatedAt TEXT NOT NULL);
                INSERT INTO Accounts VALUES ('0123456789abcdef0123456789abcdef', 'nathan', 'hash', 'Admin', NULL, '2026-01-01T00:00:00.0000000Z');
                """;
            command.ExecuteNonQuery();
        }

        var accounts = new SqliteAccountStore(_directory);
        _disposables.Add(accounts);
        var nathan = (await accounts.GetByUsernameAsync("nathan", Ct))!;
        Assert.Null(nathan.Email);
        await accounts.UpdateEmailAsync(nathan.Id, "n@example.com", Ct);
        Assert.Equal("n@example.com", (await accounts.GetByUsernameAsync("nathan", Ct))!.Email);
    }

    // ---- whose phone is whose -------------------------------------------------------------------

    private SqlitePushDeviceStore NewDeviceStore() =>
        new(Options.Create(new BridgeOptions { DataDirectory = _directory }), new FakeEnvironment());

    [Fact]
    public async Task APhone_RemembersWhoRegisteredIt_AndMovesToWhoeverRegistersItNext()
    {
        var store = NewDeviceStore();
        await store.UpsertAsync("apple", "token-1", "iPhone", Ct, "ios", username: "Terry");
        Assert.Equal("terry", (await store.GetAllAsync(Ct)).Single().Username);

        // Handed down to the kid, who signs in on it.
        await store.UpsertAsync("apple", "token-1", "iPhone", Ct, "ios", username: "kid");
        Assert.Equal("kid", (await store.GetAllAsync(Ct)).Single().Username);

        // Login turned off: nobody in particular.
        await store.UpsertAsync("apple", "token-1", "iPhone", Ct, "ios");
        Assert.Null((await store.GetAllAsync(Ct)).Single().Username);
    }

    [Fact]
    public async Task APushDatabaseFromBeforePhonesHadOwners_Upgrades_AndOldPhonesStillGetEverything()
    {
        using (var old = new SqliteConnection($"Data Source={Path.Combine(_directory, "push-devices.db")};Pooling=False"))
        {
            old.Open();
            using var command = old.CreateCommand();
            command.CommandText = """
                CREATE TABLE PushDevices (Id TEXT PRIMARY KEY, Platform TEXT NOT NULL, Token TEXT NOT NULL, DeviceName TEXT,
                    AppVariant TEXT NOT NULL DEFAULT 'ios', CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, UNIQUE (Platform, Token));
                INSERT INTO PushDevices VALUES ('0123456789abcdef0123456789abcdef', 'apple', 'old-token', 'Old iPhone', 'ios',
                    '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                """;
            command.ExecuteNonQuery();
        }

        var store = NewDeviceStore();
        var device = (await store.GetAllAsync(Ct)).Single();
        Assert.Equal("old-token", device.Token);
        Assert.Null(device.Username);
    }

    // ---- fakes ----------------------------------------------------------------------------------

    private sealed class EmptyDeviceStore : IPushDeviceStore
    {
        public Task<PushDevice> UpsertAsync(string platform, string token, string? deviceName, CancellationToken cancellationToken, string appVariant = "ios", string? username = null) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PushDevice>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PushDevice>>([]);
        public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public Task RemoveAsync(string platform, string token, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "SentinelCore.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // Enough SMTP for several conversations in a row, recording each. Can refuse one recipient
    // the way a real server refuses an address it doesn't know.
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string? _refuse;
        private readonly List<string> _conversations = [];
        private readonly CancellationTokenSource _stop = new();

        public FakeSmtpServer(string? refuse = null)
        {
            _refuse = refuse;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        public int Port { get; }

        public async Task<List<string>> ConversationsAsync(int count)
        {
            for (var i = 0; i < 100 && Count() < count; i++) await Task.Delay(50);
            lock (_conversations) return [.. _conversations];
        }

        private int Count()
        {
            lock (_conversations) return _conversations.Count;
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                using (client)
                {
                    var transcript = await ConverseAsync(client);
                    lock (_conversations) _conversations.Add(transcript);
                }
            }
        }

        private async Task<string> ConverseAsync(TcpClient client)
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            var transcript = new StringBuilder();
            await writer.WriteLineAsync("220 localhost ESMTP fake");
            while (await reader.ReadLineAsync() is { } line)
            {
                transcript.AppendLine(line);
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250-localhost");
                    await writer.WriteLineAsync("250 8BITMIME");
                }
                else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase) && _refuse is not null && line.Contains(_refuse, StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("550 No such user here");
                }
                else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase) || line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("RSET", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250 OK");
                }
                else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    while (await reader.ReadLineAsync() is { } body && body != ".") transcript.AppendLine(body);
                    await writer.WriteLineAsync("250 OK queued");
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 Bye");
                    break;
                }
                else
                {
                    await writer.WriteLineAsync("502 Command not implemented");
                }
            }
            return transcript.ToString();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
        }
    }
}
