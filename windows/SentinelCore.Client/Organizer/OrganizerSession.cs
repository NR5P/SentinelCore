using System.ComponentModel;
using SentinelCore.Client.Networking;

namespace SentinelCore.Client.Organizer;

/// <summary>
/// The Organizer in the app: this PC's engine, whether the section should exist, and keeping the
/// engine connected exactly while the app is.
/// </summary>
/// <remarks>
/// The engine is created up front and independent of the connection - this PC's copy is readable
/// and editable offline, before the server has been reached this launch. It is connected only
/// once the server has said the Organizer is on: a server too old to have one would otherwise get
/// a failing sync and a failing long poll every few seconds, all day.
/// </remarks>
public sealed class OrganizerSession : IDisposable
{
    private readonly AppModel _model;
    private readonly SqliteOrganizerStore? _ownedStore;
    private bool _enabled;
    private int _probing;

    public OrganizerSession(AppModel model, string? directory = null)
        : this(model, new SqliteOrganizerStore(directory ?? OrganizerDevice.DataDirectory()), ownsStore: true)
    {
    }

    public OrganizerSession(AppModel model, IOrganizerLocalStore store, bool ownsStore = false)
    {
        _model = model;
        if (ownsStore) _ownedStore = store as SqliteOrganizerStore;
        _enabled = OrganizerDevice.LastKnownEnabled(model.Preferences);
        Engine = new OrganizerEngine(store, OrganizerDevice.Id(model.Preferences), OrganizerDevice.Policy(model.Preferences));
        Engine.Changed += OnEngineChanged;
        _model.PropertyChanged += OnModelChanged;
    }

    public OrganizerEngine Engine { get; }

    /// <summary>Whether the section should be shown: the server's own answer once it has given
    /// one, otherwise what it said last time, so the Organizer is there offline too.</summary>
    public bool Enabled => _enabled;

    /// <summary>Raised when <see cref="Enabled"/> changes, on whatever thread noticed.</summary>
    public event Action? EnabledChanged;

    public void SetPolicy(OfflinePolicy policy)
    {
        OrganizerDevice.SavePolicy(_model.Preferences, policy);
        Engine.SetPolicy(policy);
    }

    /// <summary>Brings the engine in line with the connection. Called on every change the app
    /// model reports that could matter, and safe to call as often as that.</summary>
    public async Task ReconcileAsync()
    {
        var online = _model.ConnectionState == ConnectionState.Online && !_model.RequiresLogin;
        if (!online)
        {
            if (Engine.IsConnected) Engine.Disconnect();
            return;
        }
        if (Engine.IsConnected)
        {
            if (Engine.SyncState.TurnedOff)
            {
                Engine.Disconnect();
                SetEnabled(false);
            }
            return;
        }
        if (Interlocked.Exchange(ref _probing, 1) == 1) return;
        try
        {
            var (status, body) = await _model.Client.OrganizerRequestAsync(
                "GET", "/api/organizer/status", null, null, null, TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
            if (status is >= 200 and < 300 && OrganizerJson.Status(OrganizerJson.Parse(body)).Enabled)
            {
                SetEnabled(true);
                if (!Engine.IsConnected) Engine.Connect(new ClientOrganizerTransport(_model.Client));
            }
            else if (status is >= 200 and < 300 or 404)
            {
                // Switched off in the server's settings, or a server from before the Organizer.
                SetEnabled(false);
            }
        }
        catch (Exception)
        {
            // Unreachable just now: keep the last answer, and the next refresh asks again.
        }
        finally
        {
            Interlocked.Exchange(ref _probing, 0);
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppModel.ConnectionState) or nameof(AppModel.AuthStatus) or nameof(AppModel.LastUpdated))
        {
            _ = ReconcileAsync();
        }
    }

    private void OnEngineChanged()
    {
        if (Engine.Status is { } status && status.Enabled != _enabled) SetEnabled(status.Enabled);
        if (Engine.IsConnected && Engine.SyncState.TurnedOff) _ = ReconcileAsync();
    }

    private void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        OrganizerDevice.SaveLastKnownEnabled(_model.Preferences, enabled);
        EnabledChanged?.Invoke();
    }

    public void Dispose()
    {
        _model.PropertyChanged -= OnModelChanged;
        Engine.Changed -= OnEngineChanged;
        Engine.Dispose();
        _ownedStore?.Dispose();
    }
}
