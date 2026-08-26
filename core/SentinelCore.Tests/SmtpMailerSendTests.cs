using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using SentinelCore;
using Xunit;

namespace SentinelCore.Tests;

// The unit tests above judge SmtpMailer's decisions; this one makes it actually talk SMTP to a
// socket. It's the only thing that proves a MailMessage survives conversion to MimeKit and comes
// out the other end addressed correctly - and it covers the case that was flatly impossible
// before: a relay on your own network that offers neither encryption nor a login, which the old
// code broke by handing over credentials to a server that never asked for any.
public sealed class SmtpMailerSendTests
{
    [Fact]
    public async Task SendAsync_ToARelayThatWantsNoLogin_DeliversTheMessage()
    {
        using var server = new FakeSmtpServer();
        var accepted = server.AcceptOneAsync();

        var options = new EmailOptions
        {
            Enabled = true,
            SmtpHost = "127.0.0.1",
            SmtpPort = server.Port,
            EnableSsl = false,
            Username = string.Empty,
            AppPassword = string.Empty,
            FromAddress = "alerts@example.com",
            FromName = "Gate Camera",
            ToAddress = "owner@example.com",
            TimeoutSeconds = 15
        };

        using var message = new MailMessage
        {
            From = new MailAddress(SmtpMailer.FromAddressFor(options), options.FromName),
            Subject = "Person at gate",
            IsBodyHtml = true,
            Body = "<p>Someone is at the gate.</p>"
        };
        message.To.Add(options.ToAddress);

        await SmtpMailer.SendAsync(options, message, TestContext.Current.CancellationToken);

        var conversation = await accepted;
        Assert.Contains("MAIL FROM:<alerts@example.com>", conversation);
        Assert.Contains("RCPT TO:<owner@example.com>", conversation);
        Assert.Contains("Subject: Person at gate", conversation);
        Assert.Contains("Someone is at the gate.", conversation);

        // The point of the test: nothing was sent that a server with no AUTH would have refused.
        Assert.DoesNotContain("AUTH", conversation, StringComparison.Ordinal);
        Assert.DoesNotContain("STARTTLS", conversation, StringComparison.Ordinal);
    }

    // The alert email is a multipart/related: HTML that points at its two pictures by cid:, the
    // snapshot and the analysed-region crop. That structure is now built as a MailMessage and
    // converted to MimeKit on the way out, and a conversion that dropped a Content-Id or flattened
    // the parts would not fail anything - the mail would simply arrive with two broken images.
    [Fact]
    public void TheInlinePicturesSurviveTheConversionToMime()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"smtp-mime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var imagePath = Path.Combine(directory, "snapshot.jpg");
            File.WriteAllBytes(imagePath, [0xFF, 0xD8, 0xFF, 0xD9]);

            var options = new EmailOptions
            {
                AttachImage = true,
                Username = "sender@example.com",
                FromName = "Gate Camera",
                ToAddress = "owner@example.com"
            };
            var result = new AlertResult
            {
                Camera = "Gate",
                Profile = "gate",
                ImagePath = imagePath,
                Analysis = new VisionAnalysis { Summary = "Person at gate", Confidence = 0.9 }
            };

            using var message = EmailAlertAction.BuildMessage(
                options, result, new EmailAlertAction.EmailNotification("person", "Person at gate"),
                cropOverlay: [0xFF, 0xD8, 0xFF, 0xD9]);

            var mime = MimeKit.MimeMessage.CreateFromMailMessage(message);

            // Read back decoded rather than off the wire: the HTML travels quoted-printable, so
            // the raw bytes have soft line breaks through the middle of the cid: references.
            var html = mime.HtmlBody;
            Assert.NotNull(html);
            Assert.Contains("cid:gate-alert-image", html, StringComparison.Ordinal);
            Assert.Contains("cid:gate-alert-crop", html, StringComparison.Ordinal);

            // And the parts those references resolve against are still there, still images. The
            // html root of the multipart/related carries a generated Content-Id of its own, which
            // is why this looks at the image parts rather than at everything with an id.
            var pictures = mime.BodyParts
                .OfType<MimeKit.MimePart>()
                .Where(part => part.ContentId is "gate-alert-image" or "gate-alert-crop")
                .ToList();
            // Order in the tree is MimeKit's business - a cid: reference resolves by id, not position.
            Assert.Equal(
                ["gate-alert-crop", "gate-alert-image"],
                pictures.Select(part => part.ContentId).OrderBy(id => id, StringComparer.Ordinal));

            // Each carries its bytes and the same content type the MailMessage gave it - the
            // conversion is not allowed to quietly retype or drop one of them. (The snapshot is
            // the octet-stream that System.Net.Mail's LinkedResource(path) has always produced;
            // preserving that exactly is the point here, not improving on it.)
            Assert.All(pictures, part => Assert.NotNull(part.Content));
            Assert.Equal(
                message.AlternateViews.Single().LinkedResources
                    .Select(r => r.ContentType.MediaType).OrderBy(m => m, StringComparer.Ordinal),
                pictures.Select(part => part.ContentType.MimeType).OrderBy(m => m, StringComparer.Ordinal));

            // The "every picture arrived twice" bug in reverse: inline, and never also attached.
            Assert.Empty(mime.Attachments);

            Assert.Equal("Person at gate", mime.Subject);
            Assert.Equal("owner@example.com", Assert.Single(mime.To.Mailboxes).Address);
            Assert.Equal("Gate Camera", Assert.Single(mime.From.Mailboxes).Name);
        }
        finally
        {
            TempDirectories.Delete(directory);
        }
    }

    // Just enough of RFC 5321 to hold one conversation, deliberately advertising neither AUTH nor
    // STARTTLS so that a client offering either would be visible in the transcript.
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener;

        public FakeSmtpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public async Task<string> AcceptOneAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

            var transcript = new StringBuilder();
            await writer.WriteLineAsync("220 localhost ESMTP fake");

            while (await reader.ReadLineAsync() is { } line)
            {
                transcript.AppendLine(line);

                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250-localhost");
                    await writer.WriteLineAsync("250 8BITMIME");
                }
                else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250 OK");
                }
                else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    while (await reader.ReadLineAsync() is { } body && body != ".")
                        transcript.AppendLine(body);
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

        public void Dispose() => _listener.Dispose();
    }
}
