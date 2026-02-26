using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NeutronDrive;

/// <summary>
/// Manages session persistence through the unified PersistentCache.
/// </summary>
public sealed partial class PersistentSessionCache
{
    private readonly PersistentCache _cache;
    private readonly ILogger<PersistentSessionCache> _logger;

    public PersistentSessionCache(PersistentCache cache, ILogger<PersistentSessionCache>? logger = null)
    {
        _cache = cache;
        _logger = logger ?? new NullLogger<PersistentSessionCache>();
    }

    /// <summary>
    /// Gets the persisted session, if any.
    /// </summary>
    public SessionStuff? Session => _cache.Session;

    /// <summary>
    /// Returns true if a persisted session exists.
    /// </summary>
    public bool HasSession => _cache.Session is not null;

    /// <summary>
    /// Saves session data to the cache and persists to disk.
    /// </summary>
    public void SaveSession(SessionStuff session)
    {
        _cache.Session = session;
        _cache.Save();
        LogSessionSaved(session.Id);
    }

    /// <summary>
    /// Clears the session from the cache and persists to disk.
    /// </summary>
    public void ClearSession()
    {
        _cache.Session = null;
        _cache.Save();
        LogSessionCleared();
    }

    [LoggerMessage(LogLevel.Information, "Session saved for session ID {sessionId}")]
    partial void LogSessionSaved(string sessionId);

    [LoggerMessage(LogLevel.Information, "Session cleared.")]
    partial void LogSessionCleared();
}

