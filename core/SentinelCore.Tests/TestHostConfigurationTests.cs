extern alias WebHost;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// A guard on the guard: WebHostTestSettings only works if UseSetting actually outranks the Web
// project's own appsettings.json. That precedence is the thing three tests were silently getting
// wrong, and it is invisible in every other test - they fail with a confusing status code rather
// than saying "the host read the wrong file". This says it directly, on whatever machine it runs.
public sealed class TestHostConfigurationTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), $"host-config-{Guid.NewGuid():N}");
    private readonly string _settingsPath;

    public TestHostConfigurationTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, """{ "Bridge": { "ApiKey": "a-long-enough-stored-api-key" } }""");
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void TheTestHost_ReadsTheSettingsFileTheTestGaveIt_NotTheOneOnThisMachine()
    {
        using var factory = new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder.UseTestSettings(_settingsPath, _dataDirectory));

        // Forces the host to build; configuration isn't resolvable before that.
        using var client = factory.CreateClient();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        Assert.Equal(_settingsPath, configuration["MainAppSettingsPath"]);
        Assert.False(configuration.GetSection("InternetExposed").GetValue<bool>("Enabled"));
    }
}
