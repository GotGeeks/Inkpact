using System.Collections.Concurrent;

namespace Inkpact.Api.Sync;

/// <summary>
/// Tells long-lived connections that their session ended, so that logging out also closes the sockets of that session
/// at once. The session store raises the events; the sync hub listens. In memory, which is enough for the single
/// instance Inkpact runs as.
/// </summary>
public sealed class SessionEvents
{
    private readonly ConcurrentDictionary<Listener, byte> _listeners = new();

    /// <summary>Calls <paramref name="ended"/> once when the session ends. Dispose the result to stop listening.</summary>
    public IDisposable Listen(string sessionKey, Guid userId, Action ended)
    {
        var listener = new Listener(this, sessionKey, userId, ended);
        _listeners.TryAdd(listener, 0);
        return listener;
    }

    public void SessionEnded(string sessionKey) => Raise(listener => listener.SessionKey == sessionKey);

    public void AllSessionsEnded(Guid userId) => Raise(listener => listener.UserId == userId);

    private void Raise(Func<Listener, bool> matches)
    {
        foreach (var listener in _listeners.Keys.Where(matches))
        {
            if (_listeners.TryRemove(listener, out _))
            {
                listener.Ended();
            }
        }
    }

    // A class, not a record: two listeners are never the same one, whatever they hold.
    private sealed class Listener(SessionEvents owner, string sessionKey, Guid userId, Action ended) : IDisposable
    {
        public string SessionKey => sessionKey;

        public Guid UserId => userId;

        public void Ended() => ended();

        public void Dispose() => owner._listeners.TryRemove(this, out _);
    }
}
