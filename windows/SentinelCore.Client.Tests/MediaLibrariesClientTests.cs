using System.Net;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// The Movies &amp; TV admin routes: libraries, the folder picker and network sign-ins, against the
/// contract in core/docs/MOVIES.md.
/// </summary>
public class MediaLibrariesClientTests
{
    private static SentinelClient ClientFor(FakeServer server) =>
        new(() => server.BaseUrl, NullSentinelLog.Instance);

    private const string LibraryJson = """
        {"id":"l1","name":"Films","kind":"movies","path":"smb://nas/Movies","network":true,"itemCount":12,
         "createdAt":"2026-09-20T10:00:00Z","lastScanStartedAt":"2026-09-25T10:00:00Z",
         "lastScanFinishedAt":"2026-09-25T10:05:00Z","lastScanError":null}
        """;

    private const string ComputerJson = """
        {"id":"c1","name":"Den PC","host":"192.168.1.20","username":"sam","domain":"","hasPassword":true,
         "path":"smb://192.168.1.20","createdAt":"2026-09-20T10:00:00Z"}
        """;

    // ---------------------------------------------------------------- Libraries

    [Fact]
    public async Task Libraries_decode_including_the_network_flag()
    {
        using var server = new FakeServer().OnGet("/api/media/libraries", $"[{LibraryJson}]");
        using var client = ClientFor(server);

        var libraries = await client.MediaLibrariesAsync();

        var library = Assert.Single(libraries);
        Assert.Equal("l1", library.Id);
        Assert.True(library.Network);
        Assert.False(library.IsShows);
        Assert.Equal(12, library.ItemCount);
        Assert.NotNull(library.LastScanStartedAt);
        Assert.Null(library.LastScanError);
    }

    [Fact]
    public async Task Library_create_update_delete_and_scans_hit_the_contract_routes()
    {
        using var server = new FakeServer()
            .On("POST", "/api/media/libraries", LibraryJson, HttpStatusCode.Created)
            .On("PUT", "/api/media/libraries/l1", LibraryJson)
            .On("DELETE", "/api/media/libraries/l1", "", HttpStatusCode.NoContent)
            .On("POST", "/api/media/libraries/l1/scan", "{}", HttpStatusCode.Accepted)
            .On("POST", "/api/media/scan", "{}", HttpStatusCode.Accepted);
        using var client = ClientFor(server);

        var input = new MediaLibraryInput { Name = "TV", Kind = "shows", Path = "/mnt/tv" };
        var created = await client.CreateMediaLibraryAsync(input);
        await client.UpdateMediaLibraryAsync("l1", input);
        await client.DeleteMediaLibraryAsync("l1");
        await client.ScanMediaLibraryAsync("l1");
        await client.ScanAllMediaLibrariesAsync();

        Assert.Equal("Films", created.Name);
        Assert.Equal(
            new[]
            {
                "POST /api/media/libraries",
                "PUT /api/media/libraries/l1",
                "DELETE /api/media/libraries/l1",
                "POST /api/media/libraries/l1/scan",
                "POST /api/media/scan",
            },
            server.Requests);
        foreach (var body in server.Bodies.Take(2))
        {
            Assert.Contains("\"name\":\"TV\"", body);
            Assert.Contains("\"kind\":\"shows\"", body);
            Assert.Contains("\"path\":\"/mnt/tv\"", body);
        }
    }

    [Fact]
    public async Task A_library_the_server_rejects_throws_every_error()
    {
        using var server = new FakeServer().On(
            "POST", "/api/media/libraries",
            """{"errors":["Give the library a name.","/nope does not exist on the server."]}""",
            HttpStatusCode.BadRequest);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() =>
            client.CreateMediaLibraryAsync(new MediaLibraryInput { Path = "/nope" }));

        Assert.Equal(400, error.Status);
        Assert.Contains("Give the library a name.", error.Message);
        Assert.Contains("/nope does not exist", error.Message);
        Assert.False(error.NeedsLogin);
    }

    [Fact]
    public async Task Movies_turned_off_surfaces_the_servers_message()
    {
        using var server = new FakeServer().OnGet(
            "/api/media/libraries", """{"error":"Movies & TV is turned off in Settings."}""", HttpStatusCode.NotFound);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.MediaLibrariesAsync());

        Assert.Equal(404, error.Status);
        Assert.Equal("Movies & TV is turned off in Settings.", error.Message);
    }

    // ---------------------------------------------------------------- Folder picker

    [Fact]
    public async Task The_starting_page_is_the_bare_route_and_carries_computers()
    {
        using var server = new FakeServer().OnGet("/api/media/browse", $$"""
            {"path":"","parent":null,"kind":"roots","videoCount":0,
             "folders":[{"name":"/mnt/movies","path":"/mnt/movies","videoCount":3}],
             "computers":[{{ComputerJson}}]}
            """);
        using var client = ClientFor(server);

        var listing = await client.MediaBrowseAsync();

        Assert.Equal("GET /api/media/browse", server.Requests.Single());
        Assert.True(listing.IsRoots);
        Assert.Null(listing.Parent);
        Assert.False(listing.CanGoUp);
        Assert.False(listing.CanPick);
        Assert.Equal(3, Assert.Single(listing.Folders).VideoCount);
        var computer = Assert.Single(listing.Computers);
        Assert.Equal("Den PC", computer.DisplayName);
        Assert.Equal("smb://192.168.1.20", computer.Path);
        Assert.True(computer.HasPassword);
    }

    [Fact]
    public async Task A_network_folder_has_null_counts_and_is_pickable()
    {
        using var server = new FakeServer().OnGet("/api/media/browse", """
            {"path":"smb://nas/Movies","parent":"smb://nas","kind":"network","host":"nas",
             "folders":[{"name":"Films","path":"smb://nas/Movies/Films","videoCount":null}],"videoCount":4}
            """);
        using var client = ClientFor(server);

        var listing = await client.MediaBrowseAsync("smb://nas/Movies");

        Assert.Equal("GET /api/media/browse?path=smb%3A%2F%2Fnas%2FMovies", server.Requests.Single());
        Assert.Equal("network", listing.Kind);
        Assert.Equal("nas", listing.Host);
        Assert.Null(Assert.Single(listing.Folders).VideoCount);
        Assert.Equal(4, listing.VideoCount);
        Assert.True(listing.CanGoUp);
        Assert.True(listing.CanPick);
    }

    [Fact]
    public async Task A_computer_lists_shares_with_a_hint_and_is_not_pickable()
    {
        using var server = new FakeServer().OnGet("/api/media/browse", """
            {"path":"smb://nas","parent":"","kind":"computer","host":"nas","folders":[],"videoCount":0,
             "hint":"nas did not list any shared folders."}
            """);
        using var client = ClientFor(server);

        var listing = await client.MediaBrowseAsync("smb://nas");

        Assert.True(listing.IsComputer);
        Assert.Equal("", listing.Parent);
        Assert.True(listing.CanGoUp);      // "" is the starting page
        Assert.False(listing.CanPick);
        Assert.Equal("nas did not list any shared folders.", listing.Hint);
    }

    [Fact]
    public async Task A_folder_that_needs_a_sign_in_comes_back_as_data_not_an_exception()
    {
        using var server = new FakeServer().OnGet("/api/media/browse", """
            {"path":"smb://den-pc/Movies","parent":"smb://den-pc","kind":"network","host":"den-pc",
             "folders":[],"videoCount":0,"error":"den-pc turned down the sign-in.","needsLogin":true}
            """);
        using var client = ClientFor(server);

        var listing = await client.MediaBrowseAsync("smb://den-pc/Movies");

        Assert.Equal("den-pc turned down the sign-in.", listing.Error);
        Assert.True(listing.NeedsLogin);
        Assert.Equal("den-pc", listing.Host);
        Assert.False(listing.CanPick);
        Assert.Empty(listing.Folders);
    }

    [Fact]
    public async Task New_folder_posts_parent_and_name_and_returns_the_new_folders_page()
    {
        using var server = new FakeServer().On("POST", "/api/media/browse/folder", """
            {"path":"/mnt/media/TV Shows","parent":"/mnt/media","kind":"local","folders":[],"videoCount":0}
            """);
        using var client = ClientFor(server);

        var listing = await client.CreateMediaFolderAsync("/mnt/media", "  TV Shows ");

        Assert.Equal("POST /api/media/browse/folder", server.Requests.Single());
        Assert.Contains("\"parent\":\"/mnt/media\"", server.Bodies[0]);
        Assert.Contains("\"name\":\"TV Shows\"", server.Bodies[0]);
        Assert.Equal("/mnt/media/TV Shows", listing.Path);
        Assert.Equal("/mnt/media", listing.Parent);
        Assert.True(listing.CanPick);
        Assert.True(listing.CanCreateFolder);
        Assert.Empty(listing.Folders);
    }

    [Fact]
    public async Task New_folder_failure_throws_the_servers_message()
    {
        using var server = new FakeServer().On(
            "POST", "/api/media/browse/folder",
            """{"errors":["A folder name can't contain / or \\."]}""",
            HttpStatusCode.BadRequest);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() =>
            client.CreateMediaFolderAsync("smb://nas/Movies", "a/b"));

        Assert.Equal(400, error.Status);
        Assert.Equal("A folder name can't contain / or \\.", error.Message);
    }

    [Theory]
    [InlineData("roots", "", null, false)]
    [InlineData("computer", "smb://nas", null, false)]
    [InlineData("local", "/mnt", null, true)]
    [InlineData("network", "smb://nas/Movies", null, true)]
    [InlineData("network", "smb://nas/Movies", "nas turned down the sign-in.", false)]
    public void New_folder_is_offered_only_inside_a_folder_that_opened(string kind, string path, string? error, bool expected)
    {
        var listing = new MediaBrowseListing { Kind = kind, Path = path, Error = error };
        Assert.Equal(expected, listing.CanCreateFolder);
    }

    // ---------------------------------------------------------------- Network computers

    [Fact]
    public async Task Network_computers_sign_in_edit_forget_and_list()
    {
        var signIn = $$"""{"computer":{{ComputerJson}},"shares":[{"name":"Movies","path":"smb://192.168.1.20/Movies"}]}""";
        using var server = new FakeServer()
            .OnGet("/api/media/network", $"[{ComputerJson}]")
            .On("POST", "/api/media/network", signIn, HttpStatusCode.Created)
            .On("PUT", "/api/media/network/c1", signIn)
            .On("DELETE", "/api/media/network/c1", "", HttpStatusCode.NoContent);
        using var client = ClientFor(server);

        var saved = await client.MediaNetworkComputersAsync();
        var created = await client.SaveMediaNetworkComputerAsync(null, new MediaNetworkComputerInput
        {
            Host = "192.168.1.20", Name = "Den PC", Username = "sam", Password = "secret", Domain = "",
        });
        await client.SaveMediaNetworkComputerAsync("c1", new MediaNetworkComputerInput
        {
            Host = "192.168.1.20", Username = "sam", Password = "",
        });
        await client.DeleteMediaNetworkComputerAsync("c1");

        Assert.Equal("sam", Assert.Single(saved).SignInSummary);
        Assert.Equal("c1", created.Computer.Id);
        Assert.Equal("smb://192.168.1.20/Movies", Assert.Single(created.Shares).Path);
        Assert.Equal(
            new[]
            {
                "GET /api/media/network",
                "POST /api/media/network",
                "PUT /api/media/network/c1",
                "DELETE /api/media/network/c1",
            },
            server.Requests);
        Assert.Contains("\"host\":\"192.168.1.20\"", server.Bodies[1]);
        Assert.Contains("\"name\":\"Den PC\"", server.Bodies[1]);
        Assert.Contains("\"username\":\"sam\"", server.Bodies[1]);
        Assert.Contains("\"password\":\"secret\"", server.Bodies[1]);
        Assert.Contains("\"password\":\"\"", server.Bodies[2]);   // blank keeps the saved one
        Assert.DoesNotContain("\"name\"", server.Bodies[2]);       // no name typed, none sent
    }

    [Fact]
    public async Task A_failed_sign_in_says_whether_other_credentials_would_help()
    {
        using var server = new FakeServer().On(
            "POST", "/api/media/network",
            """{"errors":["den-pc turned down that username and password."],"needsLogin":true}""",
            HttpStatusCode.BadRequest);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() =>
            client.SaveMediaNetworkComputerAsync(null, new MediaNetworkComputerInput { Host = "den-pc", Username = "sam" }));

        Assert.Equal(400, error.Status);
        Assert.True(error.NeedsLogin);
        Assert.Equal("den-pc turned down that username and password.", error.Message);
    }

    [Fact]
    public async Task Discovery_decodes_saved_and_unsaved_computers()
    {
        using var server = new FakeServer().OnGet("/api/media/network/discover", """
            [{"name":"Living Room Mac","host":"Living-Room-Mac.local","address":"192.168.1.31",
              "path":"smb://Living-Room-Mac.local","savedId":null},
             {"name":"NAS","host":"nas.local","address":null,"path":"smb://nas.local","savedId":"c9"}]
            """);
        using var client = ClientFor(server);

        var found = await client.DiscoverMediaComputersAsync();

        Assert.Equal(2, found.Count);
        Assert.Equal("192.168.1.31", found[0].Address);
        Assert.Null(found[0].SavedId);
        Assert.Equal("smb://Living-Room-Mac.local", found[0].Path);
        Assert.Null(found[1].Address);
        Assert.Equal("c9", found[1].SavedId);
    }
}
