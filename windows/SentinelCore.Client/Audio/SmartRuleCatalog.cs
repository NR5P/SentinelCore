using System.Globalization;
using System.Text.Json;
using SentinelCore.Client.Models;

namespace SentinelCore.Client.Audio;

/// <summary>What kind of value a smart-playlist field takes, which decides the editor and the JSON.</summary>
public enum SmartValueKind
{
    Text,
    Number,
    Bool,
    Days,
    Codec,
    Library,
}

public sealed record SmartField(string Name, string Label, SmartValueKind Kind, IReadOnlyList<string> Ops);

public sealed record SmartOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The smart-playlist rule vocabulary of MUSIC_AND_PODCASTS.md §3: every field, the operators
/// each accepts, how values are typed on the wire, and the sentence a rule reads as. The editor is
/// driven from this table so it can never offer a combination the server would refuse.
/// </summary>
public static class SmartRuleCatalog
{
    private static readonly string[] TextOps = { "is", "isNot", "contains", "notContains" };
    private static readonly string[] NumberOps = { "is", "gte", "lte" };
    private static readonly string[] DayOps = { "inLast", "notInLast" };
    private static readonly string[] IsOps = { "is", "isNot" };

    public static IReadOnlyList<SmartField> Fields { get; } = new[]
    {
        new SmartField("title", "Title", SmartValueKind.Text, TextOps),
        new SmartField("artist", "Artist", SmartValueKind.Text, TextOps),
        new SmartField("album", "Album", SmartValueKind.Text, TextOps),
        new SmartField("genre", "Genre", SmartValueKind.Text, TextOps),
        new SmartField("composer", "Composer", SmartValueKind.Text, TextOps),
        new SmartField("year", "Year", SmartValueKind.Number, NumberOps),
        new SmartField("rating", "Rating", SmartValueKind.Number, NumberOps),
        new SmartField("playCount", "Play count", SmartValueKind.Number, NumberOps),
        new SmartField("durationSeconds", "Length (seconds)", SmartValueKind.Number, NumberOps),
        new SmartField("bpm", "BPM", SmartValueKind.Number, NumberOps),
        new SmartField("loved", "Loved", SmartValueKind.Bool, new[] { "is" }),
        new SmartField("added", "Added", SmartValueKind.Days, DayOps),
        new SmartField("lastPlayed", "Last played", SmartValueKind.Days, DayOps),
        new SmartField("codec", "Format", SmartValueKind.Codec, IsOps),
        new SmartField("library", "Library", SmartValueKind.Library, IsOps),
    };

    public static IReadOnlyList<SmartOption> Sorts { get; } = new[]
    {
        new SmartOption("random", "Random"),
        new SmartOption("title", "Title"),
        new SmartOption("artist", "Artist"),
        new SmartOption("album", "Album"),
        new SmartOption("year", "Year"),
        new SmartOption("added", "Date added"),
        new SmartOption("plays", "Play count"),
        new SmartOption("lastPlayed", "Last played"),
        new SmartOption("rating", "Rating"),
    };

    public static IReadOnlyList<string> Codecs { get; } = new[] { "flac", "alac", "mp3", "aac", "opus", "vorbis", "wav", "aiff", "wma", "ape", "wv", "dsd" };

    /// <summary>The cap the server applies to limit (0 = no limit).</summary>
    public const int MaxLimit = 5000;

    public static SmartField? Field(string name) => Fields.FirstOrDefault(field => field.Name == name);

    public static string OpLabel(string op, SmartValueKind kind) => (op, kind) switch
    {
        ("is", SmartValueKind.Number) => "is",
        ("gte", _) => "is at least",
        ("lte", _) => "is at most",
        ("is", _) => "is",
        ("isNot", _) => "is not",
        ("contains", _) => "contains",
        ("notContains", _) => "does not contain",
        ("inLast", SmartValueKind.Days) => "in the last (days)",
        ("notInLast", SmartValueKind.Days) => "not in the last (days)",
        _ => op,
    };

    /// <summary>A fresh rule for a field, with its first operator and a sensible value.</summary>
    public static SmartRule NewRule(string fieldName)
    {
        var field = Field(fieldName) ?? Fields[3];
        return new SmartRule
        {
            Field = field.Name,
            Op = field.Ops[0],
            Value = field.Kind switch
            {
                SmartValueKind.Bool => JsonSerializer.SerializeToElement(true),
                SmartValueKind.Number => JsonSerializer.SerializeToElement(field.Name == "rating" ? 4 : 0),
                SmartValueKind.Days => JsonSerializer.SerializeToElement(30),
                SmartValueKind.Codec => JsonSerializer.SerializeToElement("flac"),
                _ => JsonSerializer.SerializeToElement(""),
            },
        };
    }

    /// <summary>
    /// Turns what was typed into the wire value for the field's kind: numbers and days as JSON
    /// numbers, loved as a bool, everything else a string. Null when the text doesn't fit.
    /// </summary>
    public static JsonElement? ParseValue(SmartValueKind kind, string? text)
    {
        var trimmed = text?.Trim() ?? "";
        switch (kind)
        {
            case SmartValueKind.Number:
            case SmartValueKind.Days:
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
                if (kind == SmartValueKind.Days && number < 0) return null;
                return Math.Abs(number - Math.Round(number)) < 1e-9
                    ? JsonSerializer.SerializeToElement((long)Math.Round(number))
                    : JsonSerializer.SerializeToElement(number);
            case SmartValueKind.Bool:
                return trimmed.ToLowerInvariant() switch
                {
                    "true" or "yes" or "1" or "loved" => JsonSerializer.SerializeToElement(true),
                    "false" or "no" or "0" or "not loved" => JsonSerializer.SerializeToElement(false),
                    _ => null,
                };
            default:
                return trimmed.Length == 0 ? null : JsonSerializer.SerializeToElement(trimmed);
        }
    }

    /// <summary>The value as editable text.</summary>
    public static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetDouble().ToString("0.###", CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "",
    };

    /// <summary>Every problem with a set of rules, in words; empty when the server will accept them.</summary>
    public static List<string> Validate(SmartRules rules)
    {
        var problems = new List<string>();
        if (rules.Match is not ("all" or "any")) problems.Add("Match has to be all or any.");
        if (rules.Rules.Count == 0) problems.Add("Add at least one rule.");
        if (Sorts.All(sort => sort.Value != rules.Sort)) problems.Add("Pick how to sort it.");
        if (rules.Order is not ("asc" or "desc")) problems.Add("Order has to be ascending or descending.");
        if (rules.Limit < 0 || rules.Limit > MaxLimit) problems.Add($"The limit has to be between 0 (no limit) and {MaxLimit}.");

        for (var i = 0; i < rules.Rules.Count; i++)
        {
            var rule = rules.Rules[i];
            var field = Field(rule.Field);
            if (field is null)
            {
                problems.Add($"Rule {i + 1}: \"{rule.Field}\" isn't something a smart playlist can match on.");
                continue;
            }
            if (!field.Ops.Contains(rule.Op))
            {
                problems.Add($"Rule {i + 1}: {field.Label} can't use \"{rule.Op}\".");
                continue;
            }
            var ok = field.Kind switch
            {
                SmartValueKind.Number or SmartValueKind.Days => rule.Value.ValueKind == JsonValueKind.Number,
                SmartValueKind.Bool => rule.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => rule.Value.ValueKind == JsonValueKind.String && (rule.Value.GetString() ?? "").Trim().Length > 0,
            };
            if (!ok) problems.Add($"Rule {i + 1}: {field.Label} needs {ValueHint(field.Kind)}.");
        }
        return problems;
    }

    public static string ValueHint(SmartValueKind kind) => kind switch
    {
        SmartValueKind.Number => "a number",
        SmartValueKind.Days => "a number of days",
        SmartValueKind.Bool => "yes or no",
        SmartValueKind.Codec => "a format such as flac",
        SmartValueKind.Library => "a library",
        _ => "some text",
    };

    /// <summary>"Genre is Rock", "Rating is at least 4", "Last played not in the last 90 days".</summary>
    public static string Describe(SmartRule rule, Func<string, string>? libraryName = null)
    {
        var field = Field(rule.Field);
        if (field is null) return $"{rule.Field} {rule.Op} {ValueText(rule.Value)}";
        var value = ValueText(rule.Value);
        if (field.Kind == SmartValueKind.Library && libraryName is not null) value = libraryName(value);
        return field.Kind switch
        {
            SmartValueKind.Bool => rule.Value.ValueKind == JsonValueKind.True ? "Loved" : "Not loved",
            SmartValueKind.Days => $"{field.Label} {(rule.Op == "inLast" ? "in the last" : "not in the last")} {value} days",
            SmartValueKind.Text => $"{field.Label} {OpLabel(rule.Op, field.Kind)} \"{value}\"",
            _ => $"{field.Label} {OpLabel(rule.Op, field.Kind)} {value}",
        };
    }

    /// <summary>The whole definition in a sentence, for the playlist's header.</summary>
    public static string Summary(SmartRules rules, Func<string, string>? libraryName = null)
    {
        if (rules.Rules.Count == 0) return "No rules yet";
        var joiner = rules.Match == "any" ? " or " : " and ";
        var text = string.Join(joiner, rules.Rules.Select(rule => Describe(rule, libraryName)));
        var sort = Sorts.FirstOrDefault(option => option.Value == rules.Sort)?.Label ?? rules.Sort;
        var limit = rules.Limit > 0 ? $", up to {rules.Limit} tracks" : "";
        return $"{text} · sorted by {sort.ToLowerInvariant()}{limit}";
    }
}
