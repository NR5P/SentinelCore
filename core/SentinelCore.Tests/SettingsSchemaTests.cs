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

    // SettingsSchema says a section exists and where to PUT it. SettingsSnapshot is where every
    // client reads that section's *current values* from, and nothing tied the two together.
    //
    // SCHEDULED RECORDING is what that cost: a schema entry and a working PUT, but no property on
    // the snapshot, so GET /api/settings never mentioned it. All four clients drew its two number
    // boxes empty, then refused to save them ("Keep Recordings For (days) must be a number"), so
    // the section was unreachable from every UI - and the config backup, which builds from the
    // same snapshot, omitted it silently, losing the free-space floor on every restore.
    //
    // The sibling test above enforces schema <-> DTO. This one enforces schema <-> snapshot, the
    // other half of the same promise.
    [Fact]
    public void EverySchemaSection_HasAPropertyOnTheSnapshotClientsReadItsValuesFrom()
    {
        var properties = typeof(SettingsSnapshot).GetProperties();

        foreach (var section in SettingsSchema.Sections)
        {
            // The one section that deliberately keeps its values outside the snapshot, and says so
            // in the schema so clients know to fetch them separately.
            if (section.DataEndpoint is not null) continue;

            var match = Array.Find(
                properties, p => JsonNamingPolicy.CamelCase.ConvertName(p.Name) == section.Key);

            Assert.True(
                match is not null,
                $"Section '{section.Key}' has no property of that name on SettingsSnapshot, so GET /api/settings " +
                $"never returns its values: every client renders the section blank and the config backup omits it. " +
                $"Add it to SettingsSnapshot, BuildSnapshotAsync and ApplySnapshotAsync, or give the section a " +
                $"DataEndpoint if its values genuinely live elsewhere.");

            Assert.True(
                match!.PropertyType == section.DtoType,
                $"Section '{section.Key}' is described by {section.DtoType.Name} but SettingsSnapshot.{match.Name} " +
                $"is a {match.PropertyType.Name}, so the values a client reads are not the ones the schema " +
                $"describes.");
        }
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

    // Same idea for the other half of "this box doesn't have to be typed from memory". An options
    // source replaces a text box with a dropdown on every client, so the pieces each client needs
    // to draw one - what to call the list, what to say when it comes back empty, where to get it -
    // all have to be there, and it only belongs on a field that was a text box to begin with.
    [Fact]
    public void OptionsSourceFields_AreTextFields_AndSayWhereTheirListComesFrom()
    {
        foreach (var section in SettingsSchema.Sections)
        {
            foreach (var field in section.Fields.Where(f => f.OptionsSource is not null))
            {
                var source = field.OptionsSource!;
                Assert.True(
                    field.Type == "text",
                    $"Field '{section.Key}.{field.Name}' has an options source but is type '{field.Type}'. " +
                    "A select already carries its own options, and nothing else can fall back to a text box.");
                Assert.False(
                    string.IsNullOrWhiteSpace(source.Label),
                    $"Options source on '{section.Key}.{field.Name}' has no name for its list.");
                Assert.False(
                    string.IsNullOrWhiteSpace(source.Tip),
                    $"Options source on '{section.Key}.{field.Name}' has no tip.");
                // The one message no client can write for itself: it has to name what to go and
                // do about an empty list, which is server-side knowledge.
                Assert.False(
                    string.IsNullOrWhiteSpace(source.EmptyMessage),
                    $"Options source on '{section.Key}.{field.Name}' has nothing to say when the list is empty.");
                Assert.StartsWith("/api/", source.Endpoint, StringComparison.Ordinal);
            }
        }
    }

    // The models Ollama reports, turned into the list a dropdown shows. Held to account here
    // because the two rules in it are invisible from the endpoint: the value has to be the tag
    // exactly as Ollama gives it (anything else saves a model name that doesn't exist), and the
    // order has to be alphabetical rather than the recently-modified order Ollama answers in.
    [Fact]
    public void OllamaModelList_KeepsTheExactTag_AndSortsForReading()
    {
        var options = OllamaModelEndpoints.ParseModels("""
            {
              "models": [
                { "name": "qwen2.5vl:7b", "size": 6000000000 },
                { "name": "gemma3:4b", "size": 3338801804 },
                { "name": "nosize" },
                { "size": 123 }
              ]
            }
            """);

        Assert.Equal(["gemma3:4b", "nosize", "qwen2.5vl:7b"], options.Select(o => o.Value));
        Assert.Equal("gemma3:4b - 3.1 GB", options[0].Label);
        // No size reported is no size shown, rather than "nosize - 0 GB".
        Assert.Equal("nosize", options[1].Label);
    }

    // An answer that isn't the shape expected is an empty list, not an exception: a client meeting
    // one falls back to its text box, which is the same place a client meeting an error lands.
    [Fact]
    public void OllamaModelList_IsEmpty_WhenTheAnswerIsntAModelList()
    {
        Assert.Empty(OllamaModelEndpoints.ParseModels("{}"));
        Assert.Empty(OllamaModelEndpoints.ParseModels("""{ "models": "none" }"""));
        Assert.Empty(OllamaModelEndpoints.ParseModels("[]"));
    }

    // The cloud counterpart, which has to map three different response shapes onto the one list a
    // dropdown shows. Each provider gets its own test because each has its own trap.

    // OpenAI's is the plain case - the id is the string the setting holds, and nothing is filtered,
    // because nothing in this response says which models can see images and the compatible
    // endpoints this mostly exists to serve wouldn't answer consistently if it did.
    [Fact]
    public void CloudModelList_OpenAi_KeepsTheIdAndSorts()
    {
        var options = CloudVisionModelEndpoints.ParseModels("openai", """
            {
              "data": [
                { "id": "gpt-4o-mini" },
                { "id": "dall-e-3" },
                { "id": "" },
                { "object": "model" }
              ]
            }
            """);

        Assert.Equal(["dall-e-3", "gpt-4o-mini"], options.Select(o => o.Value));
        Assert.Equal("gpt-4o-mini", options[1].Label);
    }

    // Anthropic's trap is display_name: it makes the list far easier to scan, but the id is still
    // the only thing that may be saved, so it belongs in the label and never in the value.
    [Fact]
    public void CloudModelList_Anthropic_SavesTheIdAndShowsTheDisplayName()
    {
        var options = CloudVisionModelEndpoints.ParseModels("anthropic", """
            {
              "data": [
                { "id": "claude-opus-4-1", "display_name": "Claude Opus 4.1" },
                { "id": "claude-haiku-4-5" }
              ]
            }
            """);

        Assert.Equal(["claude-haiku-4-5", "claude-opus-4-1"], options.Select(o => o.Value));
        Assert.Equal("claude-haiku-4-5", options[0].Label);
        Assert.Equal("claude-opus-4-1 - Claude Opus 4.1", options[1].Label);
    }

    // Google has both traps at once. The "models/" prefix must come off - GoogleVisionClient builds
    // "models/{Model}:generateContent" itself, so saving it whole yields "models/models/..." and a
    // 404 on every alert - and it is the one provider that says which models generateContent can be
    // called on, so the ones it can't are dropped rather than offered.
    [Fact]
    public void CloudModelList_Google_StripsThePrefixAndDropsWhatCantBeCalled()
    {
        var options = CloudVisionModelEndpoints.ParseModels("google", """
            {
              "models": [
                { "name": "models/gemini-2.0-flash", "displayName": "Gemini 2.0 Flash",
                  "supportedGenerationMethods": ["generateContent", "countTokens"] },
                { "name": "models/text-embedding-004", "displayName": "Text Embedding",
                  "supportedGenerationMethods": ["embedContent"] },
                { "name": "models/no-methods-listed" }
              ]
            }
            """);

        Assert.Equal(["gemini-2.0-flash", "no-methods-listed"], options.Select(o => o.Value));
        Assert.Equal("gemini-2.0-flash - Gemini 2.0 Flash", options[0].Label);
    }

    // Same contract as the Ollama list: an unexpected shape is an empty list, not an exception, so
    // a client lands on its text box rather than on an error it can do nothing about.
    [Fact]
    public void CloudModelList_IsEmpty_WhenTheAnswerIsntAModelList()
    {
        foreach (var provider in (string[])["openai", "anthropic", "google"])
        {
            Assert.Empty(CloudVisionModelEndpoints.ParseModels(provider, "{}"));
            Assert.Empty(CloudVisionModelEndpoints.ParseModels(provider, """{ "data": "none" }"""));
            Assert.Empty(CloudVisionModelEndpoints.ParseModels(provider, "[]"));
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
        File.WriteAllText(_settingsPath, WebHostTestSettings.LoginOffSettings);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder
            .UseSetting("MainAppSettingsPath", _settingsPath)
            .UseSetting("CamerasDataDirectory", _dataDirectory)
            .UseSetting("Bridge:DataDirectory", _dataDirectory));

    // A section nobody has ever saved is absent from appsettings.json and so reads back as null,
    // and a client rendering the schema then draws its fields empty. On a number field that is
    // not merely cosmetic: every client refuses to save a blank one, so LOGIN ALERTS could not be
    // switched on at all without the user inventing a cooldown the form never showed them - while
    // the server was using 15 the whole time. The read fills those sections in with the defaults
    // the server is actually running, which is what a settings form claims to be showing.
    [Fact]
    public async Task Settings_ReturnEverySchemaSection_EvenOnAConfigThatHasNeverSavedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // The settings file this host was pointed at is "{}" - nothing has ever been saved.
        var snapshot = await client.GetFromJsonAsync<JsonElement>("/api/settings", ct);

        foreach (var section in SettingsSchema.Sections)
        {
            if (section.DataEndpoint is not null) continue;

            Assert.True(
                snapshot.TryGetProperty(section.Key, out var value) && value.ValueKind != JsonValueKind.Null,
                $"Section '{section.Key}' came back missing or null on a config that has never saved it, so every " +
                $"client renders its fields empty - and refuses to save the number ones at all.");
        }

        // Spot-check that what came back is the real default rather than a zeroed struct: these
        // are the values the server uses when the section is absent.
        Assert.Equal(15, snapshot.GetProperty("loginAlerts").GetProperty("cooldownMinutes").GetInt32());
        Assert.Equal(14, snapshot.GetProperty("scheduledRecording").GetProperty("retentionDays").GetInt32());
        Assert.Equal(5, snapshot.GetProperty("scheduledRecording").GetProperty("minimumFreeGigabytes").GetDouble());
    }

    // The other side of that coin, and the reason the defaults are filled in on the read rather
    // than inside BuildSnapshotAsync itself: to restore, null has to keep meaning "this backup
    // does not mention the section, so leave the target's own value alone" (ApplySnapshotAsync).
    // Filling defaults into the export would turn every unconfigured section into an explicit
    // instruction to write that default - freezing today's defaults into a config restored years
    // from now, and for Server writing an empty Urls over whatever the machine was bound to.
    [Fact]
    public async Task BackupExport_LeavesUnsavedSectionsOut_RatherThanBakingInTodaysDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var settings = (await client.GetFromJsonAsync<JsonElement>("/api/backup/export", ct))
            .GetProperty("settings");

        Assert.Equal(JsonValueKind.Null, settings.GetProperty("loginAlerts").ValueKind);
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("scheduledRecording").ValueKind);
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("server").ValueKind);
    }

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

    // The same for an options source: a dropdown whose endpoint isn't mapped is a field that sits
    // there saying "reading installed models…" and then falls back to a text box, on every client
    // at once. Nothing here needs Ollama to be running - what's being checked is that the route
    // exists, so the pass condition is "answered like the endpoint" rather than any one status:
    // 200 with a list where Ollama is up, 502 with an explanation where it isn't, and a 404 only
    // if the route was never mapped.
    [Fact]
    public async Task EveryOptionsEndpointInTheSchema_IsMapped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var sources = SettingsSchema.Sections
            .SelectMany(section => section.Fields
                .Where(field => field.OptionsSource is not null)
                .Select(field => (Section: section.Key, field.Name, field.OptionsSource!.Endpoint)))
            .ToArray();

        Assert.NotEmpty(sources);

        foreach (var source in sources)
        {
            using var response = await client.GetAsync(source.Endpoint, ct);
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadGateway,
                $"Options source for '{source.Section}.{source.Name}' points at {source.Endpoint}, which " +
                $"answered {(int)response.StatusCode}. A client would show a dropdown that never fills in.");

            // Whichever way it went, it has to be something a client can read - a list to show, or
            // a sentence to print under the box that fell back.
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var key = response.StatusCode == HttpStatusCode.OK ? "options" : "error";
            Assert.True(
                document.RootElement.TryGetProperty(key, out _),
                $"{source.Endpoint} answered {(int)response.StatusCode} without a '{key}' to show.");
        }
    }

    // The keys every client reads an options source by. Checked on the wire rather than on the C#
    // object for the same reason as the neighbours here: a property that doesn't serialize, or
    // serializes under a name no client looks for, is invisible from inside the server and shows
    // up as a dropdown that never appears on three platforms at once.
    [Fact]
    public async Task Schema_CarriesTheOptionsSource_OnTheModelFields()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/settings/schema", ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var sections = document.RootElement.EnumerateArray().ToArray();

        // The vision box lists recommended models too (they download on save); the chat box only
        // what is installed.
        foreach (var (key, endpoint) in (ValueTuple<string, string>[])[("ollama", "/api/settings/ollama/vision-models"), ("telegramChat", "/api/settings/ollama/models")])
        {
            var model = sections.Single(s => s.GetProperty("key").GetString() == key)
                .GetProperty("fields").EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "model");

            // Still a text field on the wire: the dropdown is an offer a client makes on top of
            // one, and a client that can't reach the list has to be left with a box to type in.
            Assert.Equal("text", model.GetProperty("type").GetString());

            var source = model.GetProperty("optionsSource");
            Assert.Equal(endpoint, source.GetProperty("endpoint").GetString());
            Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("label").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("emptyMessage").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("typeItMessage").GetString()));
        }

        // CLOUD VISION's two boxes ride the same mechanism but point somewhere else, and the
        // model one is the reason the mechanism had to be more than Ollama's: it is the field
        // that has to name models nobody here has heard of.
        var cloudVision = sections.Single(s => s.GetProperty("key").GetString() == "cloudVision");
        var cloudFields = cloudVision.GetProperty("fields").EnumerateArray().ToArray();

        var cloudModel = cloudFields.Single(f => f.GetProperty("name").GetString() == "model");
        Assert.Equal("text", cloudModel.GetProperty("type").GetString());
        Assert.Equal(
            "/api/settings/cloud-vision/models",
            cloudModel.GetProperty("optionsSource").GetProperty("endpoint").GetString());

        var cloudBaseUrl = cloudFields.Single(f => f.GetProperty("name").GetString() == "baseUrl");
        Assert.Equal("text", cloudBaseUrl.GetProperty("type").GetString());
        Assert.Equal(
            "/api/settings/cloud-vision/base-urls",
            cloudBaseUrl.GetProperty("optionsSource").GetProperty("endpoint").GetString());

        // Each source says something different once someone picks "type a name instead" - which is
        // the whole reason that message moved out of the three client renderers and onto the wire.
        Assert.NotEqual(
            cloudModel.GetProperty("optionsSource").GetProperty("typeItMessage").GetString(),
            cloudBaseUrl.GetProperty("optionsSource").GetProperty("typeItMessage").GetString());

        // And the other half: a field with no list is sent without the key at all, so a client
        // can tell "no list here" from "a list I failed to read".
        var keepAlive = sections.Single(s => s.GetProperty("key").GetString() == "ollama")
            .GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "keepAlive");
        Assert.False(keepAlive.TryGetProperty("optionsSource", out var absent) &&
                     absent.ValueKind != JsonValueKind.Null);
    }

    // reloadAfter only means anything if it actually reaches the browser, and it is the one thing
    // standing between a test send and the page reloading over its own answer. Checked on the
    // wire rather than on the C# object because the bug it guards against - a property that
    // doesn't serialize, or serializes under a name no client reads - is invisible from inside.
    [Fact]
    public async Task ExtraActions_CarryReloadAfter_OnlyWhereTheClientHasToReload()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/settings/schema", ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var sections = document.RootElement.EnumerateArray().ToArray();

        // The setup wizard cannot reappear without a reload, so it is the one action that asks.
        var setupReset = sections.Single(s => s.GetProperty("key").GetString() == "bridge")
            .GetProperty("extraAction");
        Assert.Equal("/api/setup/reset", setupReset.GetProperty("endpoint").GetString());
        Assert.True(setupReset.GetProperty("reloadAfter").GetBoolean());

        // The test sends have something to report, and a reload would throw it away.
        foreach (var key in (string[])["email", "telegram", "webhook"])
        {
            var action = sections.Single(s => s.GetProperty("key").GetString() == key)
                .GetProperty("extraAction");
            Assert.Equal($"/api/notifications/test/{key}", action.GetProperty("endpoint").GetString());
            Assert.Equal("POST", action.GetProperty("method").GetString());
            Assert.False(action.GetProperty("reloadAfter").GetBoolean());
        }

        // Cloud vision's test is the same kind of button for the same kind of reason, and has the
        // same reason not to reload: what it reports is the provider's own answer.
        var cloudTest = sections.Single(s => s.GetProperty("key").GetString() == "cloudVision")
            .GetProperty("extraAction");
        Assert.Equal("/api/settings/cloud-vision/test", cloudTest.GetProperty("endpoint").GetString());
        Assert.Equal("POST", cloudTest.GetProperty("method").GetString());
        Assert.False(cloudTest.GetProperty("reloadAfter").GetBoolean());
    }

    // BaseUrl became a saved, [Required] field in this change, and the setup wizard has never sent
    // it - it offers a provider and a key and nothing else. Left to itself that would have made a
    // first run through the wizard's Cloud Vision path fail validation on a field the wizard has no
    // box for, which is the worst possible place to meet a new required field. The DTO's default
    // covers it; this is that guarantee, held where the wizard's payload can't drift away from it.
    [Fact]
    public async Task CloudVision_SavesFromTheWizardsPayload_WhichCarriesNoBaseUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Exactly what wizard.js posts - see saveVisionFields there.
        using var response = await client.PutAsJsonAsync("/api/settings/cloud-vision", new
        {
            enabled = true,
            provider = "openai",
            apiKey = "sk-test-key-not-real",
            model = "gpt-4o-mini",
            maximumRetries = 2,
            retryDelaySeconds = 3
        }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // And it lands as a usable address rather than an empty string that would fail on the
        // first alert - the wizard's user never saw a box to fill in.
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(_settingsPath, ct));
        var baseUrl = saved.RootElement.GetProperty("CloudVision").GetProperty("BaseUrl").GetString();
        Assert.True(Uri.TryCreate(baseUrl, UriKind.Absolute, out _), $"BaseUrl saved as '{baseUrl}'.");
    }

    // The same guarantee for the action buttons. What each one answers depends on what it does -
    // the setup reset really resets (harmlessly, against this test's temporary settings file),
    // while the test sends have to reach the main app and can't, so they fail somewhere in the
    // 5xx range. Neither outcome is the point: only 404 and 405 are, because those two are what a
    // button wired to a route nobody mapped looks like, and on the web that used to be a page
    // that silently reloaded itself.
    [Fact]
    public async Task EveryExtraActionEndpointInTheSchema_IsMapped()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var actions = SettingsSchema.Sections
            .Where(section => section.ExtraAction is not null)
            .Select(section => (Section: section.Key, Action: section.ExtraAction!))
            .ToArray();

        Assert.NotEmpty(actions);

        foreach (var (sectionKey, action) in actions)
        {
            using var request = new HttpRequestMessage(new HttpMethod(action.Method), action.Endpoint);
            using var response = await client.SendAsync(request, ct);

            Assert.True(
                response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed),
                $"Action '{action.Key}' on section '{sectionKey}' points at {action.Method} {action.Endpoint}, " +
                $"which answered {(int)response.StatusCode}. Its button would do nothing on every client.");
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
