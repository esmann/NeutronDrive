using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NeutronDrive;

/// <summary>
/// A unified persistent cache that stores both secrets and session data in a single JSON file.
/// </summary>
public sealed partial class PersistentCache : IDisposable
{
    private readonly string _persistencePath;
    private readonly ILogger<PersistentCache> _logger;
    private readonly Lock _fileLock = new();
    private readonly JsonSerializerOptions _serializerOptions;

    private PersistentCacheData _data = new();

    public PersistentCache(string persistencePath, ILogger<PersistentCache>? logger = null)
    {
        _persistencePath = persistencePath;
        _logger = logger ?? new NullLogger<PersistentCache>();
        _serializerOptions = new JsonSerializerOptions
        {
            Converters = { new CacheKeyJsonConverter() },
            WriteIndented = true
        };
        LoadFromFile();
    }

    /// <summary>
    /// Gets or sets the persisted session data.
    /// </summary>
    public SessionStuff? Session
    {
        get
        {
            lock (_fileLock)
            {
                return _data.Session;
            }
        }
        set
        {
            lock (_fileLock)
            {
                _data.Session = value;
            }
        }
    }

    /// <summary>
    /// Gets the persisted secrets entries.
    /// </summary>
    public Dictionary<string, JsonElement> SecretsEntries
    {
        get
        {
            lock (_fileLock)
            {
                return _data.Secrets ?? new Dictionary<string, JsonElement>();
            }
        }
    }

    /// <summary>
    /// Sets the secrets entries to be persisted.
    /// </summary>
    public void SetSecretsEntries(Dictionary<string, JsonElement> entries)
    {
        lock (_fileLock)
        {
            _data.Secrets = entries;
        }
    }

    /// <summary>
    /// Persists all data (session + secrets) to the file.
    /// </summary>
    public void Save()
    {
        lock (_fileLock)
        {
            try
            {
                var hasSession = _data.Session is not null;
                var hasSecrets = _data.Secrets is not null && _data.Secrets.Count > 0;

                if (!hasSession && !hasSecrets)
                {
                    LogCacheIsEmptyNothingToPersist();
                    if (File.Exists(_persistencePath))
                    {
                        File.Delete(_persistencePath);
                    }
                    return;
                }

                var json = JsonSerializer.Serialize(_data, _serializerOptions);
                var encrypted = CacheFileProtection.Encrypt(json);
                File.WriteAllBytes(_persistencePath, encrypted);
                CacheFileProtection.RestrictFilePermissions(_persistencePath);
                LogCachePersistedToPath(_persistencePath);
            }
            catch (Exception ex)
            {
                LogFailedToPersistCacheToPath(ex, _persistencePath);
            }
        }
    }

    /// <summary>
    /// Clears all persisted data and deletes the file.
    /// </summary>
    public void Clear()
    {
        lock (_fileLock)
        {
            _data = new PersistentCacheData();
            if (File.Exists(_persistencePath))
            {
                File.Delete(_persistencePath);
            }
            LogCacheCleared();
        }
    }

    public void Dispose()
    {
        Save();
    }

    private void LoadFromFile()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_persistencePath))
            {
                LogCacheFileNotFoundAtPathStartingWithAnEmptyCache(_persistencePath);
                return;
            }

            try
            {
                var fileBytes = File.ReadAllBytes(_persistencePath);
                if (fileBytes.Length == 0)
                {
                    LogCacheFileAtPathIsEmpty(_persistencePath);
                    return;
                }

                string json;
                try
                {
                    json = CacheFileProtection.Decrypt(fileBytes);
                }
                catch (CryptographicException)
                {
                    // Attempt to read as legacy unencrypted JSON for migration
                    json = System.Text.Encoding.UTF8.GetString(fileBytes);
                    LogCacheFileAtPathIsUnencryptedMigrating(_persistencePath);
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    LogCacheFileAtPathIsEmpty(_persistencePath);
                    return;
                }

                var data = JsonSerializer.Deserialize<PersistentCacheData>(json, _serializerOptions);
                if (data is null)
                {
                    LogNoEntriesFoundAtPath(_persistencePath);
                    return;
                }

                _data = data;
                LogCacheLoadedFromPath(_persistencePath);
            }
            catch (Exception ex)
            {
                LogFailedToLoadCacheFromPath(ex, _persistencePath);
            }
        }
    }

    [LoggerMessage(LogLevel.Information, "Cache is empty, nothing to persist.")]
    partial void LogCacheIsEmptyNothingToPersist();

    [LoggerMessage(LogLevel.Information, "Cache persisted to {path}")]
    partial void LogCachePersistedToPath(string path);

    [LoggerMessage(LogLevel.Error, "Failed to persist cache to {path}")]
    partial void LogFailedToPersistCacheToPath(Exception e, string path);

    [LoggerMessage(LogLevel.Information, "Cache file not found at {path}, starting with an empty cache.")]
    partial void LogCacheFileNotFoundAtPathStartingWithAnEmptyCache(string path);

    [LoggerMessage(LogLevel.Warning, "Cache file at {path} is empty.")]
    partial void LogCacheFileAtPathIsEmpty(string path);

    [LoggerMessage(LogLevel.Warning, "Cache file at {path} is unencrypted, migrating to encrypted format.")]
    partial void LogCacheFileAtPathIsUnencryptedMigrating(string path);

    [LoggerMessage(LogLevel.Information, "No entries found at {path}.")]
    partial void LogNoEntriesFoundAtPath(string path);

    [LoggerMessage(LogLevel.Information, "Cache loaded from {path}")]
    partial void LogCacheLoadedFromPath(string path);

    [LoggerMessage(LogLevel.Error, "Failed to load cache from {path}")]
    partial void LogFailedToLoadCacheFromPath(Exception e, string path);

    [LoggerMessage(LogLevel.Information, "Cache cleared.")]
    partial void LogCacheCleared();
}

/// <summary>
/// The data model for the persistent cache file.
/// </summary>
public sealed class PersistentCacheData
{
    [JsonPropertyName("session")]
    public SessionStuff? Session { get; set; }

    [JsonPropertyName("secrets")]
    public Dictionary<string, JsonElement>? Secrets { get; set; }
}

