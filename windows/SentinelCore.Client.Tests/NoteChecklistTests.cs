using System.Text.Json.Nodes;
using SentinelCore.Client.Organizer;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// Note checklists, run against core/docs/note-checklist-cases.json - the same file the dashboard,
/// Apple and Android suites read, so a list ticked on this PC reads the same on a phone.
/// </summary>
public class NoteChecklistTests
{
    private static readonly Lazy<JsonObject> Cases = new(() =>
    {
        // The tests run from bin/..., deep inside the repo; walk up until the shared file turns up.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "core", "docs", "note-checklist-cases.json");
            if (File.Exists(path)) return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        }
        throw new FileNotFoundException("core/docs/note-checklist-cases.json not found above " + AppContext.BaseDirectory);
    });

    private static IEnumerable<JsonObject> Group(string name)
    {
        var group = Cases.Value[name] as JsonArray ?? throw new InvalidOperationException($"No \"{name}\" cases");
        Assert.NotEmpty(group);
        return group.Cast<JsonObject>();
    }

    private static string Str(JsonObject node, string key) => node[key]!.GetValue<string>();

    private static int Int(JsonObject node, string key) => node[key]!.GetValue<int>();

    [Fact]
    public void Progress()
    {
        foreach (var c in Group("progress"))
        {
            Assert.Equal((Int(c, "done"), Int(c, "total")), NoteChecklist.Progress(Str(c, "body")));
        }
    }

    [Fact]
    public void AddItem()
    {
        foreach (var c in Group("addItem"))
        {
            Assert.Equal(Str(c, "expect"), NoteChecklist.AddItem(Str(c, "body"), Str(c, "text")));
        }
    }

    [Fact]
    public void RemoveLine()
    {
        foreach (var c in Group("removeLine"))
        {
            Assert.Equal(Str(c, "expect"), NoteChecklist.RemoveLine(Str(c, "body"), Int(c, "line")));
        }
    }

    [Fact]
    public void ClearChecked()
    {
        foreach (var c in Group("clearChecked"))
        {
            Assert.Equal(Str(c, "expect"), NoteChecklist.ClearChecked(Str(c, "body")));
        }
    }

    [Fact]
    public void MakeChecklist()
    {
        foreach (var c in Group("makeChecklist"))
        {
            Assert.Equal(Str(c, "expect"), NoteChecklist.MakeChecklist(Str(c, "body")));
        }
    }

    [Fact]
    public void ContinueOnEnter()
    {
        foreach (var c in Group("continueOnEnter"))
        {
            var actual = NoteChecklist.ContinueOnEnter(Str(c, "line"));
            if (c["expect"] is not JsonObject expect)
            {
                Assert.Null(actual);
                continue;
            }
            Assert.NotNull(actual);
            if (expect["insert"] is { } insert)
            {
                Assert.Equal(insert.GetValue<string>(), actual!.Insert);
                Assert.False(actual.EndList);
            }
            else
            {
                Assert.True(actual!.EndList);
                Assert.Null(actual.Insert);
            }
        }
    }

    [Fact]
    public void ToggleFlipsOnlyThatItem()
    {
        const string body = "- [ ] milk\n- [x] eggs\n- [X] bread\nplain [ ] words\n```\n- [ ] code\n```";
        Assert.Equal("- [x] milk\n- [x] eggs\n- [X] bread\nplain [ ] words\n```\n- [ ] code\n```", NoteChecklist.Toggle(body, 0));
        Assert.Equal("- [ ] milk\n- [ ] eggs\n- [X] bread\nplain [ ] words\n```\n- [ ] code\n```", NoteChecklist.Toggle(body, 1));
        Assert.Equal("- [ ] milk\n- [x] eggs\n- [ ] bread\nplain [ ] words\n```\n- [ ] code\n```", NoteChecklist.Toggle(body, 2));
        // Not items: a box in the middle of prose, one inside code, and a line that isn't there.
        Assert.Equal(body, NoteChecklist.Toggle(body, 3));
        Assert.Equal(body, NoteChecklist.Toggle(body, 5));
        Assert.Equal(body, NoteChecklist.Toggle(body, 42));
        // Toggling twice gets back where it started.
        Assert.Equal(body, NoteChecklist.Toggle(NoteChecklist.Toggle(body, 0), 0));
    }

    [Fact]
    public void WindowsLineEndsComeOutAsNewlines()
    {
        // A WPF text box types "\r\n"; the synced body must match what the other surfaces write.
        Assert.Equal("- [ ] milk\n- [ ] eggs\n", NoteChecklist.AddItem("- [ ] milk\r\n", "eggs"));
        Assert.Equal("- [x] milk\n- [ ] eggs", NoteChecklist.Toggle("- [ ] milk\r\n- [ ] eggs", 0));
        Assert.Equal((1, 2), NoteChecklist.Progress("- [ ] milk\r\n- [x] eggs\r\n"));
        Assert.Equal("a\nb\nc", NoteChecklist.Normalize("a\r\nb\rc"));
    }
}
