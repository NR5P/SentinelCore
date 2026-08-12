extern alias WebHost;

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SentinelCore.Web;
using Xunit;
using AccountRole = SentinelCore.Web.AccountRole;
using IAccountStore = SentinelCore.Web.IAccountStore;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The whole point of SettingsSchema is that every client - web, iOS, macOS, Android - renders its
// settings forms from one server-side description instead of four hand-maintained copies. That
// only holds while the schema and the DTOs agree, and nothing in the compiler enforces it: adding
// a property to a DTO and forgetting the schema entry would leave a setting that saves fine over
// HTTP but appears in no UI on any platform, which is precisely the class of bug this design
// exists to prevent.
//
// So these tests are the enforcement. They fail the build rather than letting the four surfaces
// drift apart quietly.
public sealed class SettingsSchemaTests
{
    // Every field type a client is expected to know how to render. Adding one here means teaching
    // all three client renderers about it, so the set is deliberately small.
    private static readonly string[] KnownFieldTypes = ["text", "number", "checkbox", "password", "select", "csv"];

    // DTOs that intentionally have no schema section, each for its own reason. Listed explicitly
    // so that adding a new settings DTO without a section fails the test below instead of
    // silently joining this list.
    private static readonly Dictionary<string, string> DeliberatelyUnschemad = new()
    {
        ["AuthSettingsDto"] = "Auth is driven by the account endpoints, not an editable settings section.",
        ["LegacyAuthSettingsDto"] = "Read-only shape kept for the one-time pre-multi-account migration.",
        ["LicenseSettingsDto"] = "Bootstrap-only; the license UI is its own page, not a settings section.",
        ["HomeKitSettingsDto"] = "Rendered bespoke - the section includes a QR pairing code, not just fields."
    };

    private static string CamelCase(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    // Flattens a DTO's properties to the dotted camelCase paths the schema uses for its field
    // names - PushNotifications' nested Apple block becomes "apple.teamId" and so on. Leaf types
    // are exactly the ones the field-type mapping below understands; anything else that's a
    // class is recursed into, and anything else entirely is kept as a leaf so the type-check
    // test fails on it loudly instead of it vanishing from the comparison.
    private static Dictionary<string, Type> FlattenedProperties(Type dtoType, string prefix = "")
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var property in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = prefix + CamelCase(property.Name);
            var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            var isLeaf = underlying == typeof(string) || underlying == typeof(bool)
                || underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(double)
                || underlying == typeof(List<string>);

            if (!isLeaf && underlying.IsClass)
            {
                foreach (var (nestedName, nestedType) in FlattenedProperties(underlying, name + "."))
                    result[nestedName] = nestedType;
            }
            else
            {
                result[name] = underlying;
            }
        }
        return result;
    }

    [Fact]
    public void EverySchemaField_HasAMatchingDtoProperty_AndViceVersa()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            var dtoNames = FlattenedProperties(section.DtoType).Keys.ToHashSet(StringComparer.Ordinal);
            var schemaNames = section.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

            var missingFromSchema = dtoNames.Except(schemaNames).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var missingFromDto = schemaNames.Except(dtoNames).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            Assert.True(
                missingFromSchema.Length == 0,
                $"Section '{section.Key}' ({section.DtoType.Name}) has properties with no schema field, so they " +
                $"cannot be edited from any client: {string.Join(", ", missingFromSchema)}. Add them to " +
                "SettingsSchema.Sections.");

            Assert.True(
                missingFromDto.Length == 0,
                $"Section '{section.Key}' declares fields that {section.DtoType.Name} does not have, so saving them " +
                $"would silently discard the value: {string.Join(", ", missingFromDto)}.");
        }
    }

    [Fact]
    public void EverySettingsDto_EitherHasASection_OrIsListedAsDeliberatelyExcluded()
    {
        var allDtos = typeof(SettingsSchema).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("SettingsDto", StringComparison.Ordinal))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var schemad = SettingsSchema.Sections.Select(s => s.DtoType.Name).ToHashSet(StringComparer.Ordinal);
        var unaccountedFor = allDtos
            .Except(schemad)
            .Except(DeliberatelyUnschemad.Keys)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unaccountedFor.Length == 0,
            $"These settings DTOs have no schema section and are not listed as deliberate exclusions, so they are " +
            $"editable from no client at all: {string.Join(", ", unaccountedFor)}. Either add a section to " +
            "SettingsSchema or record why not in DeliberatelyUnschemad.");

        // The exclusion list itself must not rot - a DTO that was deleted or renamed should drop
        // out of it rather than sit there forever describing something that no longer exists.
        var staleExclusions = DeliberatelyUnschemad.Keys
            .Where(name => !allDtos.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            staleExclusions.Length == 0,
            $"These names are listed as deliberate schema exclusions but no such DTO exists: " +
            $"{string.Join(", ", staleExclusions)}.");
    }

    [Fact]
    public void EveryFieldType_IsOneTheClientsKnowHowToRender()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            foreach (var field in section.Fields)
            {
                Assert.True(
                    KnownFieldTypes.Contains(field.Type),
                    $"Field '{section.Key}.{field.Name}' has type '{field.Type}', which no client renderer knows. " +
                    $"Valid types: {string.Join(", ", KnownFieldTypes)}.");
            }
        }
    }

    // A "number" field bound to a string, or a "checkbox" bound to an int, would render an input
    // that writes back a value the DTO can't bind - the kind of mismatch that only shows up when
    // a user actually tries to save.
    [Fact]
    public void EveryFieldType_MatchesTheUnderlyingPropertyType()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            var properties = FlattenedProperties(section.DtoType);

            foreach (var field in section.Fields)
            {
                var underlying = properties[field.Name];

                var ok = field.Type switch
                {
                    "checkbox" => underlying == typeof(bool),
                    "number" => underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(double),
                    "text" or "password" or "select" => underlying == typeof(string),
                    "csv" => underlying == typeof(List<string>),
                    _ => false
                };

                Assert.True(
                    ok,
                    $"Field '{section.Key}.{field.Name}' is declared as '{field.Type}' but the DTO property is " +
                    $"{underlying.Name}.");
            }
        }
    }

    [Fact]
    public void SelectFields_HaveOptions_AndOtherFieldsDoNot()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            foreach (var field in section.Fields)
            {
                if (field.Type == "select")
                {
                    Assert.True(
                        field.Options is { Count: > 0 },
                        $"Select field '{section.Key}.{field.Name}' has no options, so it would render as an empty " +
                        "dropdown.");
                }
                else
                {
                    Assert.True(
                        field.Options is null,
                        $"Field '{section.Key}.{field.Name}' is type '{field.Type}' but carries select options.");
                }
            }
        }
    }

    // Duplicates here would be a genuine bug rather than a style problem: clients key their form
    // state by section key and field name, so a repeat would have two inputs writing one value.
    [Fact]
    public void SectionKeys_AndFieldNamesWithinASection_AreUnique()
    {
        var duplicateSections = SettingsSchema.Sections
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.True(
            duplicateSections.Length == 0,
            $"Duplicate section keys: {string.Join(", ", duplicateSections)}.");

        var duplicateDtos = SettingsSchema.Sections
            .GroupBy(s => s.DtoType)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.Name)
            .ToArray();

        Assert.True(
            duplicateDtos.Length == 0,
            $"These DTOs back more than one section, so a save through one would overwrite the other: " +
            $"{string.Join(", ", duplicateDtos)}.");

        foreach (var section in SettingsSchema.Sections)
        {
            var duplicateFields = section.Fields
                .GroupBy(f => f.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();

            Assert.True(
                duplicateFields.Length == 0,
                $"Section '{section.Key}' has duplicate field names: {string.Join(", ", duplicateFields)}.");
        }
    }

    [Fact]
    public void EverySection_PointsAtItsOwnSettingsEndpoint()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            Assert.StartsWith("/api/settings/", section.Endpoint, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(section.Title), $"Section '{section.Key}' has no title.");
            Assert.False(string.IsNullOrWhiteSpace(section.Description), $"Section '{section.Key}' has no description.");
        }

        var duplicateEndpoints = SettingsSchema.Sections
            .GroupBy(s => s.Endpoint, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.True(
            duplicateEndpoints.Length == 0,
            $"Two sections save to the same endpoint: {string.Join(", ", duplicateEndpoints)}.");
    }

    // A section whose group isn't one the clients know would sort to the bottom of every settings
    // page - present, editable, and filed under nothing. Cheap to typo, invisible until someone
    // goes looking for a setting that has quietly moved to the end.
    [Fact]
    public void EverySection_IsInAKnownGroup()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            Assert.True(
                SettingsGroups.Order.Contains(section.Group),
                $"Section '{section.Key}' is in group '{section.Group}', which is not one of " +
                $"SettingsGroups.Order: {string.Join(", ", SettingsGroups.Order)}.");
        }

        // The converse: a group nothing is in renders as an empty heading on every client.
        var populated = SettingsSchema.Sections.Select(s => s.Group).ToHashSet(StringComparer.Ordinal);
        var empty = SettingsGroups.Order.Where(g => !populated.Contains(g)).ToArray();
        Assert.True(
            empty.Length == 0,
            $"These groups have no sections and would render as empty headings: {string.Join(", ", empty)}.");
    }

    // Both of these name something the client has to go and do - restart a named service, or fetch
    // a second URL - so a wrong value fails at runtime on four platforms rather than here.
    [Fact]
    public void RestartServiceAndDataEndpoint_NameThingsThatExist()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            Assert.True(
                section.RestartService is "bridge" or "dashboard",
                $"Section '{section.Key}' restarts '{section.RestartService}', which is neither service.");

            if (section.DataEndpoint is not null)
            {
                Assert.StartsWith("/api/", section.DataEndpoint, StringComparison.Ordinal);
            }
        }
    }

    // Tips are what a client shows instead of the user having to read DEPLOYMENT.md. A blank one
    // is a field nobody outside this repo can understand.
    [Fact]
    public void EveryField_HasALabelAndATip()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            foreach (var field in section.Fields)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(field.Label),
                    $"Field '{section.Key}.{field.Name}' has no label.");
                Assert.False(
                    string.IsNullOrWhiteSpace(field.Tip),
                    $"Field '{section.Key}.{field.Name}' has no tip.");
            }
        }
    }

    [Fact]
    public void NumberFields_DeclareBounds_InTheRightOrder()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            foreach (var field in section.Fields.Where(f => f.Type == "number"))
            {
                Assert.True(
                    field.Min is not null && field.Max is not null,
                    $"Number field '{section.Key}.{field.Name}' has no min/max, so a client cannot validate it.");
                Assert.True(
                    field.Min < field.Max,
                    $"Number field '{section.Key}.{field.Name}' has min >= max.");
            }
        }
    }

    // The schema's bounds are what every client validates against before it sends anything; the
    // DTO's [Range] is what the server enforces on arrival. They are written out twice, in two
    // files, and nothing but this test stops them drifting - a schema max of 100 over a [Range(0,
    // 1)] would put an inline "that's fine" under a value the save then rejects, and the looser
    // direction is worse: a schema that permits less than the server does makes a legal setting
    // unreachable from every UI at once.
    [Fact]
    public void NumberFieldBounds_MatchTheDtoRangeAttribute()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            var properties = FlattenedPropertyInfos(section.DtoType);

            foreach (var field in section.Fields.Where(f => f.Type == "number"))
            {
                var property = properties[field.Name];
                var range = property.GetCustomAttribute<RangeAttribute>();

                Assert.True(
                    range is not null,
                    $"Number field '{section.Key}.{field.Name}' declares bounds {field.Min}-{field.Max} in the " +
                    $"schema, but {section.DtoType.Name}.{property.Name} has no [Range], so the server would " +
                    "accept anything a client chose to send. Add the matching [Range].");

                Assert.True(
                    Convert.ToDouble(range!.Minimum) == field.Min && Convert.ToDouble(range.Maximum) == field.Max,
                    $"Number field '{section.Key}.{field.Name}' says {field.Min}-{field.Max} in the schema but " +
                    $"[Range({range.Minimum}, {range.Maximum})] on {section.DtoType.Name}.{property.Name}. Clients " +
                    "validate against the schema and the server enforces the attribute, so these must agree.");
            }
        }
    }

    // Integer is derived rather than declared (see SettingsSchema.WithDerivedFlags); this pins the
    // derivation, because it is what a client uses to tell "3.7" in a port box from "3.7" in a
    // free-space-in-GB box - both are number fields with no step, and only one of them is wrong.
    [Fact]
    public void IntegerFlag_IsSetForExactlyTheWholeNumberFields()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            var properties = FlattenedProperties(section.DtoType);

            foreach (var field in section.Fields)
            {
                var underlying = properties[field.Name];
                var expected = field.Type == "number"
                    && (underlying == typeof(int) || underlying == typeof(long));

                Assert.True(
                    field.Integer == expected,
                    $"Field '{section.Key}.{field.Name}' binds to {underlying.Name} but has integer={field.Integer}. " +
                    "A client would reject whole numbers on a decimal field, or accept 3.7 on an integer one.");
            }
        }
    }

    // Same walk as FlattenedProperties, keeping the PropertyInfo so attributes can be read off it.
    private static Dictionary<string, PropertyInfo> FlattenedPropertyInfos(Type dtoType, string prefix = "")
    {
        var result = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        foreach (var property in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = prefix + CamelCase(property.Name);
            var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            var isLeaf = underlying == typeof(string) || underlying == typeof(bool)
                || underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(double)
                || underlying == typeof(List<string>);

            if (!isLeaf && underlying.IsClass)
            {
                foreach (var (nestedName, nestedProperty) in FlattenedPropertyInfos(underlying, name + "."))
                    result[nestedName] = nestedProperty;
            }
            else
            {
                result[name] = property;
            }
        }
        return result;
    }

    // Secret is derived from the field type rather than declared, so this pins the derivation:
    // Phase 3's masked read/write depends on exactly the password fields being the secret ones.
    [Fact]
    public void SecretFields_AreExactlyThePasswordFields()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            foreach (var field in section.Fields)
            {
                Assert.Equal(field.Type == "password", field.Secret);
            }
        }
    }

    // A lookup is a promise in the tip - "press Find and the coordinates fill in" - and LOCATION's
    // spent a release making exactly that promise with no button behind it anywhere. The parts a
    // unit test can hold to account are these: a lookup belongs to a text field (nothing else has
    // a query to send), and it has to say what its button is called and what it warns about.
    [Fact]
    public void LookupFields_AreTextFields_AndSayWhatTheirButtonDoes()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            foreach (var field in section.Fields.Where(f => f.Lookup is not null))
            {
                var lookup = field.Lookup!;
                Assert.True(
                    field.Type == "text",
                    $"Field '{section.Key}.{field.Name}' has a lookup but is type '{field.Type}'. " +
                    "A lookup sends the field's own text as the query, so only text fields can carry one.");
                Assert.False(
                    string.IsNullOrWhiteSpace(lookup.Label),
                    $"Lookup on '{section.Key}.{field.Name}' has no button label.");
                Assert.False(
                    string.IsNullOrWhiteSpace(lookup.Tip),
                    $"Lookup on '{section.Key}.{field.Name}' has no tip.");
                Assert.StartsWith("/api/", lookup.Endpoint, StringComparison.Ordinal);
            }
        }
    }
}

// The endpoint half: the schema must actually reach clients in a shape they can parse, and only
// when the caller is an admin. Same WebApplicationFactory setup as WebAuthorizationTests; see
// the note there on the extern alias.
public sealed class SettingsSchemaEndpointTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public SettingsSchemaEndpointTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"settings-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder
            .UseSetting("MainAppSettingsPath", _settingsPath)
            .UseSetting("CamerasDataDirectory", _dataDirectory)
            .UseSetting("Bridge:DataDirectory", _dataDirectory));

    [Fact]
    public async Task Schema_ServesEverySection_WithCamelCaseFieldsAndNoDtoTypeLeak()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/settings/schema", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var sections = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(SettingsSchema.Sections.Count, sections.Length);

        // The embedded broker hot-reloads, so the one setting still honestly flagged
        // restart-required is the server's own listen address.
        var server = sections.Single(s => s.GetProperty("key").GetString() == "server");
        var urls = server.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "urls");
        Assert.True(urls.GetProperty("requiresRestart").GetBoolean());

        var mqtt = sections.Single(s => s.GetProperty("key").GetString() == "mqtt");
        var fields = mqtt.GetProperty("fields").EnumerateArray().ToArray();
        var broker = fields.Single(f => f.GetProperty("name").GetString() == "runEmbeddedBroker");
        Assert.False(broker.GetProperty("requiresRestart").GetBoolean());

        var password = fields.Single(f => f.GetProperty("name").GetString() == "password");
        Assert.True(password.GetProperty("secret").GetBoolean());

        // Every client's "must be a whole number" check reads this key off the wire, so the name
        // and the camelCasing of it are part of the contract, not an implementation detail.
        var brokerPort = fields.Single(f => f.GetProperty("name").GetString() == "brokerPort");
        Assert.True(brokerPort.GetProperty("integer").GetBoolean());
        Assert.Equal(1, brokerPort.GetProperty("min").GetDouble());
        Assert.Equal(65535, brokerPort.GetProperty("max").GetDouble());

        // The other half of the same contract: a double-backed number must not be flagged, or a
        // client would refuse the 0.65 that a confidence threshold exists to hold.
        var discord = sections.Single(s => s.GetProperty("key").GetString() == "discord");
        var confidence = discord.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "minimumConfidence");
        Assert.False(confidence.GetProperty("integer").GetBoolean());

        // DtoType is server-internal wiring for the drift tests. Serializing it would leak
        // assembly-qualified type names to every client for no benefit.
        Assert.False(mqtt.TryGetProperty("dtoType", out _));
    }

    // Every lookup the schema advertises has to be a route that exists. The empty query is what
    // makes this checkable without a network: the endpoint rejects it before it would ever reach
    // a geocoder, so a 400 proves the route is really mapped while a 404 proves it isn't - which
    // is the failure a client would otherwise meet as a Find button that does nothing.
    [Fact]
    public async Task EveryLookupEndpointInTheSchema_IsMapped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var lookups = SettingsSchema.Sections
            .SelectMany(section => section.Fields
                .Where(field => field.Lookup is not null)
                .Select(field => (Section: section.Key, field.Name, field.Lookup!.Endpoint)))
            .ToArray();

        Assert.NotEmpty(lookups);

        foreach (var lookup in lookups)
        {
            using var response = await client.GetAsync(lookup.Endpoint, ct);
            Assert.True(
                response.StatusCode == HttpStatusCode.BadRequest,
                $"Lookup for '{lookup.Section}.{lookup.Name}' points at {lookup.Endpoint}, which answered " +
                $"{(int)response.StatusCode} to a query-less request instead of 400. A client's Find button " +
                "would do nothing.");
        }
    }

    [Fact]
    public async Task Schema_IsAdminOnly_OnceLoginIsEnabled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
            await accounts.CreateAsync("admin", PasswordHasher.Hash("correct horse battery"), AccountRole.Admin, null, ct);
            await accounts.CreateAsync("viewer", PasswordHasher.Hash("watch-only-please"), AccountRole.Viewer, null, ct);
        }

        var json = await File.ReadAllTextAsync(_settingsPath, ct);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        root["Auth"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true };
        await File.WriteAllTextAsync(_settingsPath, root.ToJsonString(), ct);

        using var anonymous = factory.CreateClient();
        using var unauthenticated = await anonymous.GetAsync("/api/settings/schema", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var viewerClient = factory.CreateClient();
        using var viewerLogin = await viewerClient.PostAsJsonAsync(
            "/api/auth/login", new { Username = "viewer", Password = "watch-only-please" }, ct);
        Assert.Equal(HttpStatusCode.OK, viewerLogin.StatusCode);
        using var forbidden = await viewerClient.GetAsync("/api/settings/schema", ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var adminClient = factory.CreateClient();
        using var adminLogin = await adminClient.PostAsJsonAsync(
            "/api/auth/login", new { Username = "admin", Password = "correct horse battery" }, ct);
        Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
        using var allowed = await adminClient.GetAsync("/api/settings/schema", ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }
}
