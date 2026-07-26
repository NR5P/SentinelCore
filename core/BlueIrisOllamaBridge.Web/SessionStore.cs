using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace BlueIrisOllamaBridge.Web;

// In-memory only, deliberately - a redeploy/restart logs everyone out rather than needing any
// session persistence story (no session file to secure, no stale-token cleanup on disk). For a
// single shared household login this tradeoff is worth the simplicity.
public sealed class SessionStore
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();

    public string CreateSession()
    {
        SweepExpired();
        var token = RandomNumberGenerator.GetHexString(64);
        _sessions[token] = DateTimeOffset.UtcNow.Add(SessionLifetime);
        return token;
    }

    // Sliding expiry - a session in active use never silently expires mid-session.
    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!_sessions.TryGetValue(token, out var expiry)) return false;
        if (expiry < DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        _sessions[token] = DateTimeOffset.UtcNow.Add(SessionLifetime);
        return true;
    }

    public void Invalidate(string? token)
    {
        if (!string.IsNullOrEmpty(token))
            _sessions.TryRemove(token, out _);
    }

    // Invalidates every session - used when the password/username changes, so a stolen-but-
    // still-open session doesn't survive a credential rotation meant to shut it out.
    public void InvalidateAll()
    {
        _sessions.Clear();
    }

    private void SweepExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, expiry) in _sessions)
        {
            if (expiry < now)
                _sessions.TryRemove(token, out _);
        }
    }
}
