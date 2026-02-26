using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Proton.Sdk;
using Proton.Sdk.Cryptography;

namespace NeutronDrive;

public sealed partial class PersistentSecretsCache : ISecretsCache, IDisposable
{
    private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());
    private readonly ILogger<PersistentSecretsCache> _logger;
    private readonly string _persistencePath;
    private readonly Lock _fileLock = new();
    private readonly ConcurrentDictionary<CacheKey, byte> _keys = new();
    private readonly JsonSerializerOptions _serializerOptions;

    public PersistentSecretsCache(string persistencePath, ILogger<PersistentSecretsCache>? logger = null)
    {
        _persistencePath = persistencePath;
        _logger = logger ?? new NullLogger<PersistentSecretsCache>();
        _serializerOptions = new JsonSerializerOptions
        {
            Converters = { new CacheKeyJsonConverter() }
        };
        LoadFromFile();
    }

    // ... other methods are unchanged ...
    public void Set(CacheKey cacheKey, ReadOnlySpan<byte> secretBytes, byte flags, TimeSpan expiration)
    {
        using var entry = _memoryCache.CreateEntry(cacheKey);

        if (expiration != Timeout.InfiniteTimeSpan)
        {
            entry.AbsoluteExpirationRelativeToNow = expiration;
        }

        entry.Value = new Secret(secretBytes.ToArray(), flags);
        _keys.TryAdd(cacheKey, 0);
        LogSetValueLengthByteValueForKeyCacheKey(secretBytes.Length, cacheKey);
    }

    public void IncludeInGroup(CacheKey groupCacheKey, ReadOnlySpan<CacheKey> memberCacheKeys)
    {
        using var entry = _memoryCache.CreateEntry(groupCacheKey);
        entry.Value = memberCacheKeys.ToArray();
        _keys.TryAdd(groupCacheKey, 0);
    }

    public bool TryUse<TState, TResult>(
        CacheKey cacheKey,
        TState state,
        SecretTransform<TState, TResult> transform,
        [MaybeNullWhen(false)] out TResult result)
        where TResult : notnull
    {
        if (!_memoryCache.TryGetValue(cacheKey, out Secret? secret) || secret is null)
        {
            LogKeyCacheKeyNotFound(cacheKey);
            result = default;
            return false;
        }

        LogFoundValueLengthByteValueForCacheKey(secret.Bytes.Length, cacheKey);

        result = transform.Invoke(state, secret.Bytes, secret.Flags);
        return true;
    }

    public bool TryUseGroup<TState, TResult>(
        CacheKey groupCacheKey,
        TState state,
        SecretTransform<TState, TResult> transform,
        [MaybeNullWhen(false)] out List<TResult> result)
        where TResult : notnull
    {
        if (!_memoryCache.TryGetValue<CacheKey[]>(groupCacheKey, out var cacheKeys) || cacheKeys is null)
        {
            LogGroupKeyGroupCacheKeyNotFound(groupCacheKey);
            result = null;
            return false;
        }

        LogFoundCountCacheKeysForGroupCacheKey(cacheKeys.Length, groupCacheKey);
        result = TransformEntries(cacheKeys, state, transform).ToList();
        return true;
    }

    public void Remove(CacheKey cacheKey)
    {
        _memoryCache.Remove(cacheKey);
        _keys.TryRemove(cacheKey, out _);
        LogRemovedEntryForKeyCacheKey(cacheKey);
    }

    public void Dispose()
    {
        PersistToFile();
        _memoryCache.Dispose();
    }

    private void PersistToFile()
    {
        lock (_fileLock)
        {
            try
            {
                var entries = new Dictionary<CacheKey, object>();
                foreach (var key in _keys.Keys)
                {
                    if (_memoryCache.TryGetValue(key, out var value) && value != null)
                    {
                        entries[key] = value;
                    }
                }

                if (entries.Count == 0)
                {
                    LogCacheIsEmptyNothingToPersist();
                    if (File.Exists(_persistencePath))
                    {
                        File.Delete(_persistencePath);
                    }
                    return;
                }

                var json = JsonSerializer.Serialize(entries, _serializerOptions);
                File.WriteAllText(_persistencePath, json);
                LogCachePersistedToPath(_persistencePath);
            }
            catch (Exception ex)
            {
                LogFailedToPersistCacheToPath(ex, _persistencePath);
            }
        }
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
                var json = File.ReadAllText(_persistencePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    LogCacheFileAtPathIsEmpty(_persistencePath);
                    return;
                }

                var entries = JsonSerializer.Deserialize<Dictionary<CacheKey, JsonElement>>(json, _serializerOptions);

                if (entries is null)
                {
                    LogNoEntriesFoundAtPath(_persistencePath);
                    return;
                }

                foreach (var pair in entries)
                {
                    object? value = null;
                    if (pair.Value.ValueKind == JsonValueKind.Object)
                    {
                        value = pair.Value.Deserialize<Secret>(_serializerOptions);
                    }
                    else if (pair.Value.ValueKind == JsonValueKind.Array)
                    {
                        value = pair.Value.Deserialize<CacheKey[]>(_serializerOptions);
                    }

                    if (value != null)
                    {
                        _memoryCache.Set(pair.Key, value);
                        _keys.TryAdd(pair.Key, 0);
                    }
                }
                LogCacheLoadedFromPath(_persistencePath);
            }
            catch (Exception ex)
            {
                LogFailedToLoadCacheFromPath(ex, _persistencePath);
            }
        }
    }

    private IEnumerable<TResult> TransformEntries<TResult, TState>(CacheKey[] cacheKeys, TState state, SecretTransform<TState, TResult> transform)
        where TResult : notnull
    {
        foreach (var cacheKey in cacheKeys)
        {
            if (TryUse(cacheKey, state, transform, out var transformedSecret))
            {
                yield return transformedSecret;
            }
        }
    }

    [method: JsonConstructor]
    private sealed class Secret(byte[] bytes, byte flags)
    {
        public byte[] Bytes { get; init; } = bytes;
        public byte Flags { get; init; } = flags;
    }

    [LoggerMessage(LogLevel.Debug, "Set {valueLength}-byte value for key {cacheKey}")]
    partial void LogSetValueLengthByteValueForKeyCacheKey(int valueLength, CacheKey cacheKey);

    [LoggerMessage(LogLevel.Debug, "Key {cacheKey} not found")]
    partial void LogKeyCacheKeyNotFound(CacheKey cacheKey);

    [LoggerMessage(LogLevel.Debug, "Found {valueLength}-byte value for {cacheKey}")]
    partial void LogFoundValueLengthByteValueForCacheKey(int valueLength, CacheKey cacheKey);

    [LoggerMessage(LogLevel.Debug, "Group key {groupCacheKey} not found")]
    partial void LogGroupKeyGroupCacheKeyNotFound(CacheKey groupCacheKey);

    [LoggerMessage(LogLevel.Debug, "Found {count} cache keys for {groupCacheKey}")]
    partial void LogFoundCountCacheKeysForGroupCacheKey(int count, CacheKey groupCacheKey);

    [LoggerMessage(LogLevel.Debug, "Removed entry for key {cacheKey}")]
    partial void LogRemovedEntryForKeyCacheKey(CacheKey cacheKey);

    [LoggerMessage(LogLevel.Information, "Cache is empty, nothing to persist.")]
    partial void LogCacheIsEmptyNothingToPersist();

    [LoggerMessage(LogLevel.Information, "Cache persisted to {path}")]
    partial void LogCachePersistedToPath(string path);

    [LoggerMessage(LogLevel.Error, "Failed to persist cache to {path}")]
    partial void LogFailedToPersistCacheToPath(Exception e, string path);

    [LoggerMessage(LogLevel.Information, "Cache file not found at {path}, starting with an empty cache.")]
    partial void LogCacheFileNotFoundAtPathStartingWithAnEmptyCache(string path);

    [LoggerMessage(LogLevel.Information, "No entries found at {path}.")]
    partial void LogNoEntriesFoundAtPath(string path);

    [LoggerMessage(LogLevel.Warning, "Cache file at {path} is empty.")]
    partial void LogCacheFileAtPathIsEmpty(string path);

    [LoggerMessage(LogLevel.Information, "Cache loaded from {path}")]
    partial void LogCacheLoadedFromPath(string path);

    [LoggerMessage(LogLevel.Error, "Failed to load cache from {path}")]
    partial void LogFailedToLoadCacheFromPath(Exception e, string path);
}
