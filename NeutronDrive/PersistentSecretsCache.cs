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
// TODO Harden/protect the cache file, e.g. by using encryption and/or file permissions, to prevent unauthorized access to secrets stored on disk.
public sealed partial class PersistentSecretsCache : ISecretsCache, IDisposable
{
    private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());
    private readonly ILogger<PersistentSecretsCache> _logger;
    private readonly PersistentCache _persistentCache;
    private readonly ConcurrentDictionary<CacheKey, byte> _keys = new();
    private readonly JsonSerializerOptions _serializerOptions;

    public PersistentSecretsCache(PersistentCache persistentCache, ILogger<PersistentSecretsCache>? logger = null)
    {
        _persistentCache = persistentCache;
        _logger = logger ?? new NullLogger<PersistentSecretsCache>();
        _serializerOptions = new JsonSerializerOptions
        {
            Converters = { new CacheKeyJsonConverter() }
        };
        LoadFromCache();
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
        PersistToCache();
        _memoryCache.Dispose();
    }

    private void PersistToCache()
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
                _persistentCache.SetSecretsEntries(new Dictionary<string, JsonElement>());
                _persistentCache.Save();
                return;
            }

            // Serialize secrets to a Dictionary<string, JsonElement> for storage in PersistentCache
            var json = JsonSerializer.Serialize(entries, _serializerOptions);
            var serializedEntries = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, _serializerOptions)
                                    ?? new Dictionary<string, JsonElement>();
            _persistentCache.SetSecretsEntries(serializedEntries);
            _persistentCache.Save();
            LogSecretsCachePersisted();
        }
        catch (Exception ex)
        {
            LogFailedToPersistSecretsCache(ex);
        }
    }

    private void LoadFromCache()
    {
        try
        {
            var entries = _persistentCache.SecretsEntries;

            if (entries.Count == 0)
            {
                LogNoSecretsEntriesFoundInCache();
                return;
            }

            foreach (var pair in entries)
            {
                var cacheKey = ParseCacheKey(pair.Key);
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
                    _memoryCache.Set(cacheKey, value);
                    _keys.TryAdd(cacheKey, 0);
                }
            }
            LogSecretsCacheLoaded();
        }
        catch (Exception ex)
        {
            LogFailedToLoadSecretsCache(ex);
        }
    }

    private static CacheKey ParseCacheKey(string stringValue)
    {
        var parts = stringValue.Split(':');
        return parts.Length switch
        {
            3 => new CacheKey(parts[0], parts[1], parts[2]),
            5 => new CacheKey(parts[0], parts[1], parts[2], parts[3], parts[4]),
            _ => throw new JsonException($"Invalid CacheKey format. Expected 3 or 5 parts, but got {parts.Length}.")
        };
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

    [LoggerMessage(LogLevel.Information, "Secrets cache persisted.")]
    partial void LogSecretsCachePersisted();

    [LoggerMessage(LogLevel.Error, "Failed to persist secrets cache.")]
    partial void LogFailedToPersistSecretsCache(Exception e);

    [LoggerMessage(LogLevel.Information, "No secrets entries found in cache.")]
    partial void LogNoSecretsEntriesFoundInCache();

    [LoggerMessage(LogLevel.Information, "Secrets cache loaded.")]
    partial void LogSecretsCacheLoaded();

    [LoggerMessage(LogLevel.Error, "Failed to load secrets cache.")]
    partial void LogFailedToLoadSecretsCache(Exception e);
}
