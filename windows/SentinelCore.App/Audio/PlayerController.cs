using System.Windows.Threading;
using SentinelCore.Client;
using SentinelCore.Client.Audio;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Audio;

/// <summary>
/// The one player both tabs share (MUSIC_AND_PODCASTS.md §11): the queue, the sound engine,
/// Sound Check, speed, the sleep timer, reporting (played / progress / now-playing), the Windows
/// media controls, Play-on another speaker, and "Let others play here" (this PC as a speaker).
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs on the UI thread. The decisions - what comes next, how loud, when to
/// report - live in SentinelCore.Client (PlayQueue, SoundCheck, SleepTimer, ListenMeter,
/// IntervalGate, SpeakerSession), where they are unit-tested; this class wires them to the engine
/// and the network.
/// </para>
/// <para>
/// Two targets: this PC (the engine plays) or a remote speaker (every control becomes a speaker
/// command, and the screens show the speaker's reported state instead of the local one).
/// </para>
/// </remarks>
public sealed class PlayerController : IDisposable
{
    private const string VolumeKey = "audio.volume";
    private const string SoundCheckKey = "audio.soundCheck";
    private const string PodcastSpeedKey = "audio.podcastSpeed";
    private const string MusicSpeedKey = "audio.musicSpeed";
    private const string DataSaverKey = "audio.dataSaverKbps";
    private const string SpeakerKey = "audio.speaker.enabled";
    private const string AnnounceKey = "audio.speaker.announcements";

    private readonly AppModel _model;
    private readonly Dispatcher _dispatcher;
    private readonly AudioEngine _engine;
    private readonly MediaControls _controls;
    private readonly Announcer _announcer;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _remotePoll;
    private readonly ListenMeter _meter = new();
    private readonly IntervalGate _progressGate = new(TimeSpan.FromSeconds(15));
    private readonly IntervalGate _nowPlayingGate = new(TimeSpan.FromSeconds(30));
    private readonly IntervalGate _speakerStateGate = new(TimeSpan.FromSeconds(30));
    private readonly Dictionary<string, PodcastMySettings?> _showSettings = new();

    private bool _wantPlay;
    private bool _ducked;
    private DateTimeOffset? _duckUntil;
    private DateTimeOffset? _rampStart;
    private double _rampSeconds;
    private double? _earlyEnd;
    private (string Type, string Id)? _radioSeed;
    private bool _extendingRadio;
    private SpeakerSession? _speaker;
    private int _loadGeneration;
    private CancellationTokenSource? _briefing;

    public PlayerController(AppModel model, Dispatcher dispatcher)
    {
        _model = model;
        _dispatcher = dispatcher;
        _engine = new AudioEngine(model);
        _controls = new MediaControls(model, dispatcher);
        _announcer = new Announcer(model);
        Queue = new PlayQueue();
        DeviceId = AudioDevice.Id(model.Preferences);

        _engine.Ended += OnEnded;
        _engine.Failed += OnFailed;
        _engine.StateChanged += OnEngineState;

        _controls.PlayPressed += () => Play();
        _controls.PausePressed += Pause;
        _controls.NextPressed += () => _ = NextAsync();
        _controls.PreviousPressed += () => _ = PreviousAsync();
        _controls.StopPressed += Stop;
        _controls.SeekRequested += Seek;

        _tick = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        _remotePoll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _remotePoll.Tick += async (_, _) => await PollRemoteAsync();

        if (SpeakerEnabled) StartSpeaker();
    }

    /// <summary>The app's one player, made by the main window.</summary>
    public static PlayerController? Shared { get; set; }

    public PlayQueue Queue { get; }
    public SleepTimer Sleep { get; } = new();
    public string DeviceId { get; }
    public string DeviceName => AudioDevice.Name(_model.Preferences);
    public AppModel Model => _model;

    public QueueEntry? Current => Queue.Current;
    public bool HasItem => Current is not null || RemoteTarget is not null;

    /// <summary>A sentence about the last thing that went wrong (or the briefing being spoken); null when quiet.</summary>
    public string? Notice { get; private set; }

    /// <summary>Anything the screens show other than the position changed.</summary>
    public event Action? Changed;

    /// <summary>A few times a second: the position moved.</summary>
    public event Action? Ticked;

    private void RaiseChanged() => Changed?.Invoke();

    private void SetNotice(string? notice)
    {
        Notice = notice;
        RaiseChanged();
    }

    // ================================================================ Preferences

    public double UserVolume
    {
        get => _model.Preferences.GetDouble(VolumeKey, 0.8);
        set
        {
            _model.Preferences.Set(VolumeKey, Math.Clamp(value, 0, 1));
            if (RemoteTarget is not null) _ = SendRemoteAsync(new SpeakerCommand { Action = "volume", Volume = Math.Clamp(value, 0, 1) });
            ApplyVolume();
            PushSpeakerState(force: true);
            RaiseChanged();
        }
    }

    public bool SoundCheckEnabled
    {
        get => _model.Preferences.GetBool(SoundCheckKey, true);
        set
        {
            _model.Preferences.Set(SoundCheckKey, value);
            ApplyVolume();
            RaiseChanged();
        }
    }

    /// <summary>The speed a show plays at until it has one of its own.</summary>
    public double DefaultPodcastSpeed
    {
        get => PodcastPlayback.ClampSpeed(_model.Preferences.GetDouble(PodcastSpeedKey, 1.0));
        set => _model.Preferences.Set(PodcastSpeedKey, PodcastPlayback.ClampSpeed(value));
    }

    /// <summary>Data saver: a kbps cap for music streams (0 = original quality).</summary>
    public int DataSaverKbps
    {
        get => (int)_model.Preferences.GetDouble(DataSaverKey, 0);
        set => _model.Preferences.Set(DataSaverKey, Math.Max(0, value));
    }

    public double Speed { get; private set; } = 1.0;

    public bool IsEpisode => Current?.IsEpisode == true;

    // ================================================================ State the screens read

    public bool IsPlaying => RemoteTarget is not null ? RemoteState?.Playing == true : _engine.IsPlaying || (_wantPlay && _engine.IsBuffering);
    public bool IsBuffering => RemoteTarget is null && _engine.IsBuffering;

    public double Position => RemoteTarget is not null ? RemotePosition : _engine.Position;

    public double Duration =>
        RemoteTarget is not null
            ? RemoteState?.DurationSeconds ?? 0
            : _engine.Duration > 0 ? _engine.Duration : Current?.DurationSeconds ?? 0;

    public string? EngineUnavailable => _engine.Unavailable;

    // ================================================================ Starting things

    public Task PlayTracksAsync(IReadOnlyList<MusicTrack> tracks, int start = 0, bool shuffle = false, AudioContext? context = null) =>
        PlayAsync(tracks.Select(track => new QueueEntry(track)).ToList(), start, shuffle, context);

    public Task PlayEpisodesAsync(IReadOnlyList<PodcastEpisode> episodes, int start = 0, AudioContext? context = null, double? startPosition = null) =>
        PlayAsync(episodes.Select(episode => new QueueEntry(episode)).ToList(), start, false, context, startPosition);

    /// <summary>Replaces the queue and plays it - here, or on the speaker being played to.</summary>
    public async Task PlayAsync(List<QueueEntry> entries, int start = 0, bool shuffle = false, AudioContext? context = null, double? startPosition = null)
    {
        if (entries.Count == 0) return;
        CancelBriefing();
        if (context?.Type != "radio") _radioSeed = null;
        ReportLeaving(skipped: true);
        Queue.Load(entries, start, shuffle, context);

        if (RemoteTarget is not null)
        {
            var (queue, index) = Queue.ToWire();
            await SendRemoteAsync(new SpeakerCommand
            {
                Action = "play", Queue = queue, Index = index, PositionSeconds = startPosition ?? 0,
                Shuffle = false, Context = context, Volume = UserVolume,
            });
            RaiseChanged();
            return;
        }
        await StartCurrentAsync(startPosition, play: true);
    }

    public Task PlayMixAsync(string mixId) => RunAsync(async () =>
    {
        var mix = await _model.Client.MusicMixAsync(mixId);
        if (mix.Tracks.Count == 0) throw new InvalidOperationException("That mix is empty right now.");
        await PlayTracksAsync(mix.Tracks, 0, false, new AudioContext { Type = "mix", Id = mixId, Name = mix.Name });
    });

    /// <summary>An endless radio from a seed: it tops itself up as it nears the end.</summary>
    public Task StartRadioAsync(string seedType, string seedId, string name) => RunAsync(async () =>
    {
        var mix = await _model.Client.MusicRadioAsync(seedType, seedId);
        if (mix.Tracks.Count == 0) throw new InvalidOperationException("There isn't enough music like that for a radio yet.");
        await PlayTracksAsync(mix.Tracks, 0, false, new AudioContext { Type = "radio", Id = $"{seedType}:{seedId}", Name = string.IsNullOrEmpty(mix.Name) ? $"{name} Radio" : mix.Name });
        _radioSeed = (seedType, seedId);
    });

    public Task ShuffleAllAsync() => RunAsync(async () =>
    {
        var tracks = await _model.Client.MusicShuffleAllAsync();
        if (tracks.Count == 0) throw new InvalidOperationException("There's no music in the library yet.");
        await PlayTracksAsync(tracks, 0, false, new AudioContext { Type = "queue", Name = "Shuffle all" });
    });

    public void PlayNext(IEnumerable<QueueEntry> entries)
    {
        var wasEmpty = Queue.IsEmpty;
        Queue.PlayNext(entries);
        if (wasEmpty) _ = StartCurrentAsync(null, play: true);
        PushNowPlaying(force: true);
        RaiseChanged();
    }

    public void AddToQueue(IEnumerable<QueueEntry> entries)
    {
        var wasEmpty = Queue.IsEmpty;
        Queue.AddLast(entries);
        if (wasEmpty) _ = StartCurrentAsync(null, play: true);
        PushNowPlaying(force: true);
        RaiseChanged();
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            SetNotice(AppModel.UserFacingMessage(error));
        }
    }

    // ---------------------------------------------------------------- The engine

    private async Task StartCurrentAsync(double? startPosition, bool play)
    {
        var entry = Current;
        var generation = ++_loadGeneration;
        if (entry is null)
        {
            _engine.Stop();
            _controls.Clear();
            RaiseChanged();
            return;
        }
        if (RemoteTarget is not null) return;

        _wantPlay = play;
        _earlyEnd = null;
        Notice = null;
        double start = startPosition ?? 0;
        string path;
        if (entry.Episode is { } episode)
        {
            var settings = await ShowSettingsAsync(episode.PodcastId);
            if (generation != _loadGeneration) return;
            Speed = PodcastPlayback.SpeedFor(settings, DefaultPodcastSpeed);
            if (startPosition is null) start = PodcastPlayback.StartPosition(episode, settings);
            _earlyEnd = PodcastPlayback.EarlyEnd(episode.DurationSeconds, settings);
            path = episode.AudioPath;
        }
        else
        {
            Speed = PodcastPlayback.ClampSpeed(_model.Preferences.GetDouble(MusicSpeedKey, 1.0));
            path = SentinelClient.TrackStreamPath(entry.Track!, DataSaverKbps > 0 ? DataSaverKbps : null);
        }

        _meter.Reset(DateTimeOffset.UtcNow, play);
        _progressGate.Reset();
        var ok = await _engine.LoadAsync(path, start, play, Speed, EffectiveVolume());
        if (generation != _loadGeneration) return;
        if (!ok)
        {
            SetNotice(_engine.Unavailable ?? "The player couldn't start.");
            return;
        }

        _controls.SetItem(entry.Title, entry.IsEpisode ? entry.Subtitle : entry.Track?.ArtistLine,
            entry.IsEpisode ? entry.Subtitle : entry.Track?.Album, entry.ArtUrl, entry.IsEpisode);
        _controls.SetPlaying(play, true);
        if (entry.Track is { } track && play) Fire(() => _model.Client.ReportNowPlayingTrackAsync(track.Id), "now playing");
        PushNowPlaying(force: true);
        PushSpeakerState(force: true);
        MaybeExtendRadio();
        RaiseChanged();
    }

    /// <summary>The show's per-person settings (speed, skip intro/outro), fetched once per show.</summary>
    private async Task<PodcastMySettings?> ShowSettingsAsync(string? podcastId)
    {
        if (string.IsNullOrEmpty(podcastId)) return null;
        if (_showSettings.TryGetValue(podcastId, out var cached)) return cached;
        try
        {
            var show = await _model.Client.PodcastAsync(podcastId);
            _showSettings[podcastId] = show.MySettings;
            return show.MySettings;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Forgets a show's cached settings after they were edited, so the next episode uses the new ones.</summary>
    public void ShowSettingsChanged(string podcastId, PodcastMySettings? settings) => _showSettings[podcastId] = settings;

    private double SoundCheckFactor()
    {
        if (!SoundCheckEnabled || Current?.Track is not { } track) return 1.0;
        return SoundCheck.Factor(track.ReplayGain, Queue.IsAlbumInOrder);
    }

    private double FadeNow()
    {
        var now = DateTimeOffset.UtcNow;
        var fade = Sleep.Fade(now, _engine.Position, _earlyEnd ?? Duration);
        if (_rampStart is { } rampStart)
        {
            var ramp = SoundCheck.Ramp((now - rampStart).TotalSeconds, _rampSeconds);
            if (ramp >= 1) _rampStart = null;
            fade = Math.Min(fade, ramp);
        }
        return fade;
    }

    private double EffectiveVolume() => SoundCheck.Effective(UserVolume, SoundCheckFactor(), _ducked, FadeNow());

    private void ApplyVolume() => _engine.SetVolume(EffectiveVolume());

    private void OnEngineState()
    {
        var now = DateTimeOffset.UtcNow;
        if (_engine.IsPlaying) _meter.Resume(now);
        else _meter.Pause(now);
        _controls.SetPlaying(_engine.IsPlaying || _wantPlay, Current is not null);
        PushSpeakerState(force: true);
        RaiseChanged();
    }

    private void OnFailed(string message)
    {
        _model.Log($"Audio playback failed: {message}", DiagnosticLevel.Notice);
        SetNotice($"{Current?.Title}: {message}");
        _wantPlay = false;
    }

    private async void OnEnded()
    {
        var entry = Current;
        if (entry is null) return;
        ReportLeaving(skipped: false, completed: true);
        if (Sleep.Mode == SleepTimerMode.EndOfItem)
        {
            Sleep.Cancel();
            _wantPlay = false;
            _controls.SetPlaying(false, true);
            PushNowPlaying(force: true);
            RaiseChanged();
            return;
        }
        var next = Queue.Next(automatic: true);
        if (next is null)
        {
            _wantPlay = false;
            if (_radioSeed is not null && await ExtendRadioAsync() && Queue.Next(automatic: true) is not null)
            {
                await StartCurrentAsync(null, play: true);
                return;
            }
            PushNowPlaying(force: true);
            RaiseChanged();
            return;
        }
        await StartCurrentAsync(ReferenceEquals(next, entry) ? 0 : null, play: true);
    }

    // ================================================================ Controls

    public void TogglePlay()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void Play()
    {
        if (RemoteTarget is not null)
        {
            _ = SendRemoteAsync(new SpeakerCommand { Action = "resume" });
            return;
        }
        if (Current is null) return;
        _wantPlay = true;
        if (_engine.Duration <= 0 && !_engine.IsBuffering)
        {
            // Nothing loaded yet (a queue restored from another device): load at the saved spot.
            _ = StartCurrentAsync(_engine.Position > 0 ? _engine.Position : null, play: true);
            return;
        }
        _engine.Play();
        _controls.SetPlaying(true, true);
        PushNowPlaying(force: true);
        RaiseChanged();
    }

    public void Pause()
    {
        if (RemoteTarget is not null)
        {
            _ = SendRemoteAsync(new SpeakerCommand { Action = "pause" });
            return;
        }
        _wantPlay = false;
        _engine.Pause();
        _controls.SetPlaying(false, Current is not null);
        ReportProgress(force: true);
        PushNowPlaying(force: true);
        RaiseChanged();
    }

    public void Stop()
    {
        CancelBriefing();
        if (RemoteTarget is not null)
        {
            _ = SendRemoteAsync(new SpeakerCommand { Action = "stop" });
            return;
        }
        ReportLeaving(skipped: true);
        _wantPlay = false;
        _engine.Stop();
        Queue.Clear();
        Sleep.Cancel();
        _controls.Clear();
        Fire(() => _model.Client.DeleteNowPlayingAsync(DeviceId), "now playing");
        PushSpeakerState(force: true);
        RaiseChanged();
    }

    public void Seek(double seconds)
    {
        if (RemoteTarget is not null)
        {
            _ = SendRemoteAsync(new SpeakerCommand { Action = "seek", PositionSeconds = Math.Max(0, seconds) });
            _remoteBase = (Math.Max(0, seconds), DateTimeOffset.UtcNow);
            return;
        }
        var duration = Duration;
        _engine.Seek(duration > 0 ? Math.Clamp(seconds, 0, Math.Max(0, duration - 0.25)) : Math.Max(0, seconds));
        _controls.SetTimeline(_engine.Position, duration, force: true);
        PushNowPlaying(force: true);
        PushSpeakerState(force: true);
        Ticked?.Invoke();
    }

    public void SkipBy(double seconds) => Seek(PodcastPlayback.Skip(Position, seconds, Duration));

    public async Task NextAsync()
    {
        if (RemoteTarget is not null)
        {
            await SendRemoteAsync(new SpeakerCommand { Action = "next" });
            return;
        }
        if (Current is null) return;
        ReportLeaving(skipped: true);
        var next = Queue.Next(automatic: false);
        if (next is null && _radioSeed is not null && await ExtendRadioAsync()) next = Queue.Next(automatic: false);
        if (next is null)
        {
            Pause();
            Seek(0);
            return;
        }
        await StartCurrentAsync(null, play: true);
    }

    public async Task PreviousAsync()
    {
        if (RemoteTarget is not null)
        {
            await SendRemoteAsync(new SpeakerCommand { Action = "previous" });
            return;
        }
        var (entry, restart) = Queue.Previous(Position);
        if (entry is null) return;
        if (restart)
        {
            Seek(0);
            return;
        }
        ReportLeaving(skipped: true);
        await StartCurrentAsync(0, play: true);
    }

    public async Task JumpToAsync(int index)
    {
        ReportLeaving(skipped: true);
        if (Queue.JumpTo(index) is null) return;
        if (RemoteTarget is not null)
        {
            var (queue, wireIndex) = Queue.ToWire();
            await SendRemoteAsync(new SpeakerCommand { Action = "play", Queue = queue, Index = wireIndex, Context = Queue.Context });
            return;
        }
        await StartCurrentAsync(null, play: true);
    }

    public void RemoveAt(int index)
    {
        var wasCurrent = index == Queue.Index;
        if (wasCurrent) ReportLeaving(skipped: true);
        var next = Queue.RemoveAt(index);
        if (wasCurrent) _ = StartCurrentAsync(null, play: next is not null && _wantPlay);
        PushNowPlaying(force: true);
        RaiseChanged();
    }

    public void Move(int from, int to)
    {
        Queue.Move(from, to);
        PushNowPlaying(force: true);
        RaiseChanged();
    }

    public void ToggleShuffle()
    {
        Queue.SetShuffle(!Queue.Shuffle);
        ApplyVolume(); // album gain applies only in order
        PushNowPlaying(force: true);
        RaiseChanged();
    }

    public void CycleRepeat()
    {
        Queue.CycleRepeat();
        PushNowPlaying(force: true);
        RaiseChanged();
    }

    /// <summary>Changes speed now; for an episode it is remembered as that show's speed (§11).</summary>
    public void SetSpeed(double speed)
    {
        Speed = PodcastPlayback.ClampSpeed(speed);
        _engine.SetRate(Speed);
        if (Current?.Episode is { PodcastId: { Length: > 0 } podcastId })
        {
            var settings = _showSettings.TryGetValue(podcastId, out var cached) && cached is not null ? cached : new PodcastMySettings();
            settings.PlaybackSpeed = Speed;
            _showSettings[podcastId] = settings;
            Fire(() => _model.Client.SavePodcastMySettingsAsync(podcastId, settings), "show speed");
        }
        else
        {
            _model.Preferences.Set(MusicSpeedKey, Speed);
        }
        PushNowPlaying(force: true);
        RaiseChanged();
    }

    public void SetSleepMinutes(double minutes)
    {
        Sleep.StartMinutes(minutes, DateTimeOffset.UtcNow);
        RaiseChanged();
    }

    public void SetSleepEndOfItem()
    {
        Sleep.StartEndOfItem();
        RaiseChanged();
    }

    public void CancelSleep()
    {
        Sleep.Cancel();
        ApplyVolume();
        RaiseChanged();
    }

    // ================================================================ The tick

    private void OnTick()
    {
        var now = DateTimeOffset.UtcNow;
        if (_ducked && _duckUntil is { } until && now >= until)
        {
            _ducked = false;
            _duckUntil = null;
        }

        if (RemoteTarget is null && Current is not null)
        {
            if (Sleep.IsDue(now))
            {
                Sleep.Cancel();
                Pause();
            }

            if (_earlyEnd is { } end && _engine.IsPlaying && _engine.Position >= end)
            {
                _earlyEnd = null;
                OnEnded();
                return;
            }

            ApplyVolume();

            if (_engine.IsPlaying)
            {
                if (IsEpisode && _progressGate.TryPass(now)) ReportProgress(force: true);
                if (_nowPlayingGate.TryPass(now)) PushNowPlaying(force: true);
                if (_speakerStateGate.TryPass(now)) PushSpeakerState(force: true);
                _controls.SetTimeline(_engine.Position, Duration);
            }
        }
        Ticked?.Invoke();
    }

    // ================================================================ Reporting (§4, §6, §7)

    private static void Fire(Func<Task> call, string what) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await call().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // A missed report costs a play count or a few seconds of resume point; it must
                // never interrupt the music. 404 just means the tab was switched off.
                if (error is not SentinelServerException { Status: 404 })
                {
                    Shared?._model.Log($"Could not report {what}: {error.Message}", DiagnosticLevel.Notice);
                }
            }
        });

    /// <summary>
    /// The current item is being left - ended (<paramref name="completed"/>), skipped or replaced.
    /// Music gets a played report (§4); an episode gets its last progress (§6).
    /// </summary>
    private void ReportLeaving(bool skipped, bool completed = false)
    {
        if (RemoteTarget is not null) return;
        var entry = Current;
        if (entry is null) return;
        var now = DateTimeOffset.UtcNow;
        _meter.Pause(now);
        if (entry.Track is { } track)
        {
            var seconds = _meter.Seconds(now) * Speed;
            if (ListenMeter.WorthReporting(seconds))
            {
                var body = ListenMeter.Report(seconds, completed, skipped, now, DeviceName);
                Fire(() => _model.Client.ReportTrackPlayedAsync(track.Id, body), "a play");
            }
        }
        else if (entry.Episode is { } episode)
        {
            var duration = Duration;
            var position = completed ? Math.Max(duration, _engine.Position) : _engine.Position;
            ReportEpisode(episode.Id, position, duration);
        }
        _meter.Reset(now, playing: false);
    }

    private void ReportProgress(bool force)
    {
        if (RemoteTarget is not null || Current?.Episode is not { } episode) return;
        if (!force && !_progressGate.TryPass(DateTimeOffset.UtcNow)) return;
        ReportEpisode(episode.Id, _engine.Position, Duration);
    }

    private void ReportEpisode(string episodeId, double position, double duration)
    {
        if (position < 10 || duration <= 0) return; // the server saves nothing under 10 s
        var body = new EpisodeProgressInput { PositionSeconds = Math.Round(position, 1), DurationSeconds = Math.Round(duration, 1), Speed = Speed, DeviceName = DeviceName };
        Fire(() => _model.Client.ReportEpisodeProgressAsync(episodeId, body), "podcast progress");
    }

    private void PushNowPlaying(bool force)
    {
        if (RemoteTarget is not null || Current is null) return;
        if (!force && !_nowPlayingGate.TryPass(DateTimeOffset.UtcNow)) return;
        _nowPlayingGate.Mark(DateTimeOffset.UtcNow);
        var (queue, index) = Queue.ToWire();
        var body = new NowPlaying
        {
            DeviceId = DeviceId,
            DeviceName = DeviceName,
            Kind = IsEpisode ? "podcast" : "music",
            Queue = queue,
            Index = index,
            PositionSeconds = Math.Round(_engine.Position, 1),
            Playing = _wantPlay || _engine.IsPlaying,
            Shuffle = Queue.Shuffle,
            Repeat = PlayQueue.RepeatWire(Queue.Repeat),
            Speed = Speed,
            Context = Queue.Context,
        };
        Fire(() => _model.Client.PutNowPlayingAsync(body), "now playing");
    }

    // ================================================================ Continue from another device (§7)

    /// <summary>The other device worth offering to continue from, or null.</summary>
    public async Task<NowPlaying?> FindContinueAsync()
    {
        try
        {
            var devices = await _model.Client.NowPlayingAsync();
            return ContinueListening.Pick(devices, DeviceId, DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Takes over another device's queue at the same spot.</summary>
    public Task ContinueFromAsync(NowPlaying device) => RunAsync(async () =>
    {
        var resolved = await _model.Client.ResolveNowPlayingAsync(device.DeviceId);
        var entries = Entries(resolved.NowPlaying.Queue, resolved);
        if (entries.Count == 0) throw new InvalidOperationException("That device's queue is empty now.");
        var np = resolved.NowPlaying;
        var index = Math.Clamp(np.Index, 0, entries.Count - 1);
        Queue.Repeat = PlayQueue.ParseRepeat(np.Repeat);
        await PlayAsync(entries, index, false, np.Context, ContinueListening.EstimatedPosition(np, DateTimeOffset.UtcNow));
        if (np.Speed is > 0 and not 1.0 && IsEpisode) SetSpeed(np.Speed);
    });

    /// <summary>Queue refs resolved to playable entries, in order, dropping any the server no longer has.</summary>
    private static List<QueueEntry> Entries(IEnumerable<AudioQueueRef> refs, NowPlayingResolved resolved)
    {
        var tracks = resolved.Tracks.GroupBy(track => track.Id).ToDictionary(group => group.Key, group => group.First());
        var episodes = resolved.Episodes.GroupBy(episode => episode.Id).ToDictionary(group => group.Key, group => group.First());
        var entries = new List<QueueEntry>();
        foreach (var item in refs)
        {
            if (item.Type == "episode" && episodes.TryGetValue(item.Id, out var episode)) entries.Add(new QueueEntry(episode));
            else if (item.Type != "episode" && tracks.TryGetValue(item.Id, out var track)) entries.Add(new QueueEntry(track));
        }
        return entries;
    }

    /// <summary>
    /// Turns bare queue refs (a speaker command's queue) into full tracks and episodes. There is no
    /// "get these tracks" route, but now-playing resolve does exactly this - so the refs are put as
    /// this device's now-playing (which they are about to be) and resolved straight back.
    /// </summary>
    private async Task<List<QueueEntry>> ResolveRefsAsync(List<AudioQueueRef> refs, int index, AudioContext? context)
    {
        await _model.Client.PutNowPlayingAsync(new NowPlaying
        {
            DeviceId = DeviceId,
            DeviceName = DeviceName,
            Kind = refs.FirstOrDefault()?.Type == "episode" ? "podcast" : "music",
            Queue = refs,
            Index = index,
            Playing = true,
            Context = context,
            Repeat = "off",
            Speed = 1,
        });
        var resolved = await _model.Client.ResolveNowPlayingAsync(DeviceId);
        return Entries(refs, resolved);
    }

    // ================================================================ Radio

    private void MaybeExtendRadio()
    {
        if (_radioSeed is null || Queue.Upcoming.Count() > 3) return;
        _ = ExtendRadioAsync();
    }

    private async Task<bool> ExtendRadioAsync()
    {
        if (_radioSeed is not { } seed || _extendingRadio) return false;
        _extendingRadio = true;
        try
        {
            var more = await _model.Client.MusicRadioAsync(seed.Type, seed.Id, 50, Queue.Entries.Select(entry => entry.Id).Distinct());
            var fresh = more.Tracks.Where(track => Queue.Entries.All(entry => entry.Id != track.Id)).Select(track => new QueueEntry(track)).ToList();
            if (fresh.Count == 0) return false;
            Queue.AddLast(fresh);
            RaiseChanged();
            return true;
        }
        catch (Exception error)
        {
            _model.Log($"Could not extend the radio: {error.Message}", DiagnosticLevel.Notice);
            return false;
        }
        finally
        {
            _extendingRadio = false;
        }
    }

    // ================================================================ Morning briefing (§9)

    public bool IsBriefing => _briefing is not null;

    /// <summary>Speaks the briefing with the PC's voice, then plays its episodes as a queue.</summary>
    public Task PlayBriefingAsync() => RunAsync(async () =>
    {
        var briefing = await _model.Client.MorningBriefingAsync();
        CancelBriefing();
        if (RemoteTarget is not null)
        {
            await SendRemoteAsync(new SpeakerCommand { Action = "announce", Text = string.Join(" ", briefing.AllLines) });
            if (briefing.Episodes.Count > 0) await PlayEpisodesAsync(briefing.Episodes, 0, new AudioContext { Type = "queue", Name = "Morning Briefing" });
            return;
        }
        if (_engine.IsPlaying) Pause();
        var cancel = new CancellationTokenSource();
        _briefing = cancel;
        SetNotice("Morning Briefing…");
        try
        {
            await _announcer.SpeakAsync(briefing.AllLines, UserVolume, cancel.Token);
        }
        finally
        {
            if (_briefing == cancel) _briefing = null;
            SetNotice(null);
        }
        if (cancel.IsCancellationRequested) return;
        if (briefing.Episodes.Count > 0)
        {
            await PlayEpisodesAsync(briefing.Episodes, 0, new AudioContext { Type = "queue", Name = "Morning Briefing" });
        }
    });

    private void CancelBriefing()
    {
        if (_briefing is null) return;
        _briefing.Cancel();
        _briefing = null;
        _announcer.Stop();
    }

    // ================================================================ Play on another speaker (§8)

    public Speaker? RemoteTarget { get; private set; }
    public SpeakerState? RemoteState { get; private set; }
    private (double Position, DateTimeOffset At) _remoteBase;

    private double RemotePosition =>
        RemoteState is { Playing: true }
            ? _remoteBase.Position + (DateTimeOffset.UtcNow - _remoteBase.At).TotalSeconds
            : _remoteBase.Position;

    /// <summary>
    /// Moves playback to a speaker (null = back to this PC). What is playing here carries over: the
    /// speaker gets a play command for the same queue at the same spot, and this PC pauses.
    /// </summary>
    public async Task SetTargetAsync(Speaker? speaker)
    {
        if (speaker is null)
        {
            var last = RemoteState;
            RemoteTarget = null;
            RemoteState = null;
            _remotePoll.Stop();
            // Pick up where the speaker was when it is still on the same item.
            if (Current is not null && last?.ItemId == Current.Id) _ = StartCurrentAsync(RemotePositionOr(last), play: false);
            RaiseChanged();
            return;
        }

        if (speaker.DeviceId == DeviceId)
        {
            await SetTargetAsync(null);
            return;
        }

        var hadLocal = Current is not null;
        var position = _engine.Position;
        var playing = _engine.IsPlaying || _wantPlay;
        if (hadLocal)
        {
            ReportLeaving(skipped: false);
            _wantPlay = false;
            _engine.Pause();
            _controls.SetPlaying(false, true);
        }
        RemoteTarget = speaker;
        RemoteState = speaker.State;
        _remoteBase = (speaker.State?.PositionSeconds ?? 0, DateTimeOffset.UtcNow);
        if (hadLocal)
        {
            var (queue, index) = Queue.ToWire();
            await SendRemoteAsync(new SpeakerCommand
            {
                Action = "play", Queue = queue, Index = index, PositionSeconds = position, Shuffle = false, Context = Queue.Context,
            });
            // "play" always plays; something paused here arrives paused there too.
            if (!playing) await SendRemoteAsync(new SpeakerCommand { Action = "pause" });
        }
        _remotePoll.Start();
        RaiseChanged();
    }

    private double RemotePositionOr(SpeakerState? state) => state?.PositionSeconds ?? _engine.Position;

    private async Task SendRemoteAsync(SpeakerCommand command)
    {
        if (RemoteTarget is not { } speaker) return;
        try
        {
            await _model.Client.SendSpeakerCommandAsync(speaker.DeviceId, command);
            // A quick re-read, so the button flips without waiting a whole poll.
            _ = Task.Delay(600).ContinueWith(_ => _dispatcher.InvokeAsync(PollRemoteAsync), TaskScheduler.Default);
        }
        catch (Exception error)
        {
            SetNotice($"{speaker.Name}: {AppModel.UserFacingMessage(error)}");
        }
    }

    private async Task PollRemoteAsync()
    {
        if (RemoteTarget is not { } speaker) return;
        try
        {
            var speakers = await _model.Client.SpeakersAsync();
            var found = speakers.FirstOrDefault(candidate => candidate.DeviceId == speaker.DeviceId);
            if (RemoteTarget?.DeviceId != speaker.DeviceId) return;
            if (found is null)
            {
                SetNotice($"{speaker.Name} isn't available as a speaker any more.");
                await SetTargetAsync(null);
                return;
            }
            RemoteTarget = found;
            RemoteState = found.State;
            _remoteBase = (found.State?.PositionSeconds ?? 0, DateTimeOffset.UtcNow);
            Notice = found.Online ? null : $"{found.Name} is offline.";
            RaiseChanged();
        }
        catch (Exception)
        {
            // The next poll tries again.
        }
    }

    // ================================================================ Let others play here (§8)

    public bool SpeakerEnabled
    {
        get => _model.Preferences.GetBool(SpeakerKey, false);
        set
        {
            if (value == SpeakerEnabled && (_speaker is not null) == value) return;
            _model.Preferences.Set(SpeakerKey, value);
            if (value) StartSpeaker();
            else _ = StopSpeakerAsync();
            RaiseChanged();
        }
    }

    public bool AllowAnnouncements
    {
        get => _model.Preferences.GetBool(AnnounceKey, true);
        set
        {
            _model.Preferences.Set(AnnounceKey, value);
            if (_speaker is not null) Fire(_speaker.HeartbeatNowAsync, "speaker settings");
        }
    }

    public void RenameDevice(string name)
    {
        AudioDevice.SetName(_model.Preferences, string.IsNullOrWhiteSpace(name) ? AudioDevice.DefaultName() : name);
        if (_speaker is not null) Fire(_speaker.HeartbeatNowAsync, "speaker name");
        RaiseChanged();
    }

    public string? SpeakerHealth { get; private set; }

    private void StartSpeaker()
    {
        if (_speaker is not null) return;
        _speaker = new SpeakerSession(
            _model.Client,
            DeviceId,
            () => new SpeakerRegistration { Name = DeviceName, Kind = "windows", AllowAnnouncements = AllowAnnouncements, Volume = UserVolume },
            command => _dispatcher.InvokeAsync(() => ApplyCommandAsync(command)).Task.Unwrap(),
            _model);
        _speaker.HealthChanged += health => _dispatcher.BeginInvoke(() =>
        {
            if (SpeakerHealth == health) return;
            SpeakerHealth = health;
            RaiseChanged();
        });
        _speaker.Start();
        PushSpeakerState(force: true);
    }

    private async Task StopSpeakerAsync()
    {
        var session = _speaker;
        _speaker = null;
        SpeakerHealth = null;
        if (session is not null) await session.StopAsync(unregister: true);
    }

    /// <summary>Applies one command sent to this PC. Returns once it has fully happened (an announce once spoken).</summary>
    private async Task ApplyCommandAsync(DeliveredSpeakerCommand command)
    {
        _model.Log($"Speaker command from {command.From ?? "someone"}: {command.Action}");
        // Commands sent to this PC always act on this PC, even while it is playing to another speaker.
        if (RemoteTarget is not null && command.Action is "play") await SetTargetAsync(null);
        switch (command.Action)
        {
            case "play":
            {
                var refs = command.Queue ?? new List<AudioQueueRef>();
                if (refs.Count == 0) break;
                var entries = await ResolveRefsAsync(refs, command.Index ?? 0, command.Context);
                if (entries.Count == 0) break;
                if (command.Volume is { } volume) _model.Preferences.Set(VolumeKey, Math.Clamp(volume, 0, 1));
                if (command.RampSeconds is > 0)
                {
                    _rampStart = DateTimeOffset.UtcNow;
                    _rampSeconds = command.RampSeconds.Value;
                }
                await PlayAsync(entries, Math.Clamp(command.Index ?? 0, 0, entries.Count - 1), command.Shuffle == true,
                    command.Context, command.PositionSeconds is > 0 ? command.PositionSeconds : null);
                break;
            }
            case "pause":
                Pause();
                break;
            case "resume":
                Play();
                break;
            case "next":
                await NextAsync();
                break;
            case "previous":
                await PreviousAsync();
                break;
            case "seek":
                if (command.PositionSeconds is { } position) Seek(position);
                break;
            case "volume":
                if (command.Volume is { } level)
                {
                    if (command.RampSeconds is > 0)
                    {
                        _rampStart = DateTimeOffset.UtcNow;
                        _rampSeconds = command.RampSeconds.Value;
                    }
                    UserVolume = level;
                }
                break;
            case "stop":
                Stop();
                break;
            case "duck":
                _ducked = true;
                _duckUntil = DateTimeOffset.UtcNow.AddSeconds(30);
                ApplyVolume();
                break;
            case "unduck":
                _ducked = false;
                _duckUntil = null;
                ApplyVolume();
                break;
            case "announce":
                await AnnounceAsync(command.Text, command.Volume);
                break;
        }
        PushSpeakerState(force: true);
    }

    /// <summary>
    /// Ducks whatever is playing, speaks, then unducks (§8). With nothing playing it just speaks,
    /// at the command's volume when it gave one.
    /// </summary>
    private async Task AnnounceAsync(string? text, double? volume)
    {
        if (string.IsNullOrWhiteSpace(text) || !AllowAnnouncements) return;
        var wasDucked = _ducked;
        _ducked = true;
        ApplyVolume();
        try
        {
            await _announcer.SpeakAsync(text, volume ?? Math.Max(UserVolume, 0.6));
        }
        finally
        {
            _ducked = wasDucked;
            ApplyVolume();
        }
    }

    private DateTimeOffset _lastStatePush = DateTimeOffset.MinValue;

    private void PushSpeakerState(bool force)
    {
        if (_speaker is null) return;
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastStatePush < TimeSpan.FromSeconds(1)) return;
        _lastStatePush = now;
        _speakerStateGate.Mark(now);
        var entry = Current;
        var state = new SpeakerState
        {
            Playing = RemoteTarget is null && (_engine.IsPlaying || _wantPlay),
            Title = entry?.Title,
            Subtitle = entry?.IsEpisode == true ? entry.Subtitle : entry?.Track?.ArtistLine,
            ArtUrl = entry?.ArtUrl,
            PositionSeconds = Math.Round(_engine.Position, 1),
            DurationSeconds = Math.Round(Duration, 1),
            Volume = UserVolume,
            Kind = entry is null ? null : entry.IsEpisode ? "podcast" : "music",
            ItemId = entry?.Id,
        };
        var id = DeviceId;
        Fire(() => _model.Client.PutSpeakerStateAsync(id, state), "speaker state");
    }

    // ================================================================ Shutdown

    /// <summary>Saves where things were, unregisters the speaker, and lets go of the engine.</summary>
    public void Dispose()
    {
        _tick.Stop();
        _remotePoll.Stop();
        ReportProgress(force: true);
        PushNowPlaying(force: true);
        if (_speaker is { } speaker)
        {
            // Not unregistered: the PC going away is "offline after 3 minutes", and the switch
            // stays on for next time.
            _ = speaker.StopAsync(unregister: false);
        }
        _announcer.Dispose();
        _controls.Dispose();
        _engine.Dispose();
    }
}
