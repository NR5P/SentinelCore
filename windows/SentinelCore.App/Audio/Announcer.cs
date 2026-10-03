using System.Speech.Synthesis;
using SentinelCore.Client;

namespace SentinelCore.App.Audio;

/// <summary>
/// The PC's speech voice, for speaker announcements (§8 "announce") and the Morning Briefing's
/// spoken lines (§9). One utterance at a time; <see cref="SpeakAsync"/> completes when the voice
/// has finished, which is what lets the speaker hold the next command until then.
/// </summary>
public sealed class Announcer : IDisposable
{
    private readonly AppModel _model;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SpeechSynthesizer? _voice;
    private TaskCompletionSource? _current;

    public Announcer(AppModel model) => _model = model;

    public bool IsSpeaking => _current is { Task.IsCompleted: false };

    /// <summary>Speaks the lines in order at <paramref name="volume"/> (0-1). Completes when done or stopped.</summary>
    public async Task SpeakAsync(IEnumerable<string> lines, double volume = 1.0, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                if (ct.IsCancellationRequested) return;
                await SpeakOneAsync(line, volume, ct).ConfigureAwait(true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SpeakAsync(string text, double volume = 1.0, CancellationToken ct = default) =>
        SpeakAsync(new[] { text }, volume, ct);

    private Task SpeakOneAsync(string text, double volume, CancellationToken ct)
    {
        try
        {
            _voice ??= CreateVoice();
            _voice.Volume = (int)Math.Round(Math.Clamp(volume, 0, 1) * 100);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _current = done;
            void Completed(object? sender, SpeakCompletedEventArgs args)
            {
                _voice!.SpeakCompleted -= Completed;
                done.TrySetResult();
            }
            _voice.SpeakCompleted += Completed;
            ct.Register(() =>
            {
                try { _voice?.SpeakAsyncCancelAll(); } catch (Exception) { }
                done.TrySetResult();
            });
            _voice.SpeakAsync(text);
            // A voice that never reports back (a broken audio device) must not hold a speaker forever.
            return Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(text.Length / 8.0 + 15, 15, 300))));
        }
        catch (Exception error)
        {
            _model.Log($"Could not speak an announcement: {error.Message}", DiagnosticLevel.Notice);
            return Task.CompletedTask;
        }
    }

    private static SpeechSynthesizer CreateVoice()
    {
        var voice = new SpeechSynthesizer();
        voice.SetOutputToDefaultAudioDevice();
        return voice;
    }

    public void Stop()
    {
        try
        {
            _voice?.SpeakAsyncCancelAll();
        }
        catch (Exception)
        {
            // Nothing to stop.
        }
        _current?.TrySetResult();
    }

    public void Dispose()
    {
        Stop();
        _voice?.Dispose();
        _voice = null;
    }
}
