namespace SentinelCore.Web;

// Where each PTZ camera is, as far as this server can tell, and when it was last driven.
//
// This exists because motion detection on a PTZ camera is only meaningful when the camera is
// somewhere known. Two separate problems make that so, and the second is the one that bites:
//
//   While it is moving, frame-differencing is worthless - a pan changes every pixel in the frame,
//   which is orders of magnitude past any zone's minimum object size. Every move would trigger,
//   and the alert pipeline would then spend an AI call on motion-blurred scenery.
//
//   While it is parked somewhere *other* than home, detection is worse than useless: it is wrong
//   quietly. Trigger zones are polygons over the frame, not over the world, so a zone drawn around
//   the gate keeps watching that rectangle of picture after the camera has been panned to look at
//   something else - and now that rectangle is a completely different piece of reality. Nothing
//   errors. It just watches the wrong thing until somebody notices.
//
// The server can answer both because it is the only thing issuing PTZ commands: every move, stop
// and home goes through CameraPtzEndpoints. That is not airtight - the camera can also be driven
// from the vendor's own app, and this would not know - but it fails in the safe direction. An
// unobserved move leaves this believing the camera is home when it isn't, which the idle homing
// worker corrects within one idle period; and anything this is unsure about is treated as "not
// home", which turns detection off rather than leaving it pointed at the wrong scene.
public sealed class PtzActivityTracker
{
    public enum Position
    {
        // Nothing has driven this camera since the server started, so where it is pointing is
        // genuinely unknown. Deliberately not treated as Home: assuming home would be a guess, and
        // the cost of guessing wrong is a zone quietly watching the wrong scene. PtzHomingWorker
        // resolves it by sending the camera home on its first pass.
        Unknown,
        Home,
        Away
    }

    private sealed record State(Position Position, bool IsMoving, DateTimeOffset LastCommandAt);

    private readonly Dictionary<Guid, State> _cameras = [];
    private readonly object _gate = new();
    private readonly TimeProvider _time;

    public PtzActivityTracker(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    // A continuous move has started. The camera is now somewhere between two positions, and stays
    // "moving" until the matching stop - which is the whole reason PtzControlsView is careful that
    // a stop always follows a press.
    public void RecordMoveStarted(Guid cameraId) => Update(cameraId, Position.Away, isMoving: true);

    public void RecordStopped(Guid cameraId) => Update(cameraId, Position.Away, isMoving: false);

    // A goto-home is in flight. Still "moving" - the camera takes a second or two to travel - and
    // still not Home until it lands, so detection stays off for the journey.
    public void RecordHomingStarted(Guid cameraId) => Update(cameraId, Position.Away, isMoving: true);

    public void RecordHomeReached(Guid cameraId) => Update(cameraId, Position.Home, isMoving: false);

    // PtzTracker is following something. Position-wise this is no different from any other
    // away-from-home state, but it has to keep the idle clock fresh: a track that holds a subject
    // centred issues no move commands at all, and without this the homing worker would decide the
    // camera had been abandoned and drive it home mid-track.
    public void RecordTrackingTick(Guid cameraId) => Update(cameraId, Position.Away, isMoving: false);

    // A home command the camera refused. Position is whatever it was; what matters is clearing the
    // moving flag so a failed home doesn't wedge detection off forever.
    public void RecordHomingFailed(Guid cameraId)
    {
        lock (_gate)
        {
            var existing = Current(cameraId);
            _cameras[cameraId] = existing with { IsMoving = false, LastCommandAt = _time.GetUtcNow() };
        }
    }

    private void Update(Guid cameraId, Position position, bool isMoving)
    {
        lock (_gate)
        {
            _cameras[cameraId] = new State(position, isMoving, _time.GetUtcNow());
        }
    }

    // Whether trigger-zone detection should be running for this camera right now.
    //
    // `settle` is the pause after the last command before detection resumes. It covers the gap
    // between the camera reporting that it stopped and the picture actually being still - these
    // cameras overshoot slightly and the image takes a moment to settle - and without it the first
    // frames after a move are still full-frame motion.
    public bool IsSettledAtHome(Guid cameraId, TimeSpan settle, out string reason)
    {
        lock (_gate)
        {
            var state = Current(cameraId);
            if (state.IsMoving)
            {
                reason = "the camera is moving";
                return false;
            }

            if (state.Position != Position.Home)
            {
                reason = state.Position == Position.Unknown
                    ? "the camera has not been sent home since the server started"
                    : "the camera is not at its home position";
                return false;
            }

            var since = _time.GetUtcNow() - state.LastCommandAt;
            if (since < settle)
            {
                reason = "the camera is still settling";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    // Cameras that should be sent home: not there, not currently moving, and untouched for long
    // enough that nobody is still driving them.
    //
    // A camera whose position is Unknown skips the idle wait entirely. Nobody has driven it, so
    // there is nothing to interrupt, and waiting the full idle period would leave detection off for
    // that long after every server restart.
    public bool ShouldSendHome(Guid cameraId, TimeSpan idleFor)
    {
        lock (_gate)
        {
            var state = Current(cameraId);
            if (state.IsMoving || state.Position == Position.Home) return false;
            if (state.Position == Position.Unknown) return true;
            return _time.GetUtcNow() - state.LastCommandAt >= idleFor;
        }
    }

    public Position PositionOf(Guid cameraId)
    {
        lock (_gate) return Current(cameraId).Position;
    }

    public void Forget(Guid cameraId)
    {
        lock (_gate) _cameras.Remove(cameraId);
    }

    // Callers hold _gate.
    private State Current(Guid cameraId) =>
        _cameras.TryGetValue(cameraId, out var state)
            ? state
            : new State(Position.Unknown, IsMoving: false, DateTimeOffset.MinValue);
}
