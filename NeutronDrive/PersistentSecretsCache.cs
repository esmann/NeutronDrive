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

public sealed class PersistentSecretsCache : ISecretsCache, IDisposable
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
        _logger.LogDebug("Set {ValueLength}-byte value for key {CacheKey}", secretBytes.Length, cacheKey);
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
            _logger.LogDebug("Key {CacheKey} not found", cacheKey);
            result = default;
            return false;
        }

        _logger.LogDebug("Found {ValueLength}-byte value for {CacheKey}", secret.Bytes.Length, cacheKey);

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
            _logger.LogDebug("Group key {GroupCacheKey} not found", groupCacheKey);
            result = null;
            return false;
        }

        _logger.LogDebug("Found {Count} cache keys for {GroupCacheKey}", cacheKeys.Length, groupCacheKey);
        result = TransformEntries(cacheKeys, state, transform).ToList();
        return true;
    }

    public void Remove(CacheKey cacheKey)
    {
        _memoryCache.Remove(cacheKey);
        _keys.TryRemove(cacheKey, out _);
        _logger.LogDebug("Removed entry for key {CacheKey}", cacheKey);
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
                    _logger.LogInformation("Cache is empty, nothing to persist.");
                    if (File.Exists(_persistencePath))
                    {
                        File.Delete(_persistencePath);
                    }
                    return;
                }

                var json = JsonSerializer.Serialize(entries, _serializerOptions);
                File.WriteAllText(_persistencePath, json);
                _logger.LogInformation("Cache persisted to {Path}", _persistencePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist cache to {Path}", _persistencePath);
            }
        }
    }

    private void LoadFromFile()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_persistencePath))
            {
                _logger.LogInformation("Cache file not found at {Path}, starting with an empty cache.", _persistencePath);
                return;
            }

            try
            {
                var json = File.ReadAllText(_persistencePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.LogWarning("Cache file at {Path} is empty.", _persistencePath);
                    return;
                }

                var entries = JsonSerializer.Deserialize<Dictionary<CacheKey, JsonElement>>(json, _serializerOptions);

                if (entries is null)
                {
                    _logger.LogInformation("No entries found at {Path}.", _persistencePath);
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
                _logger.LogInformation("Cache loaded from {Path}", _persistencePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load cache from {Path}", _persistencePath);
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

    private sealed class Secret
    {
        public byte[] Bytes { get; set; }
        public byte Flags { get; set; }

        [JsonConstructor]
        public Secret(byte[] bytes, byte flags)
        {
            Bytes = bytes;
            Flags = flags;
        }
    }
}
