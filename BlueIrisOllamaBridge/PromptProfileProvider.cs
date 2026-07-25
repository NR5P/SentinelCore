using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

// Wraps the live-reloadable PromptProfiles configuration section so edits made through the
// settings UI (or a hand-edited appsettings.json) take effect on the next alert, no restart.
public interface IPromptProfileProvider
{
    IReadOnlyDictionary<string, PromptProfile> Current { get; }
}

public sealed class PromptProfileProvider(IOptionsMonitor<Dictionary<string, PromptProfile>> monitor) : IPromptProfileProvider
{
    public IReadOnlyDictionary<string, PromptProfile> Current => monitor.CurrentValue;
}
