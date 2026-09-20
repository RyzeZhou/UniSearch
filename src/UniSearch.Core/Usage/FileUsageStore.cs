using System.Text.Json;
using System.Text.Json.Serialization;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Core.Usage;

/// <summary>
/// 文件型 usage store。持久化到 <c>%LOCALAPPDATA%\UniSearch\usage.json</c>。
/// 半衰期 60 天：一次误点不会永久影响排序，但常用项也不会因为“上周没点”就消失。
/// </summary>
public sealed class FileUsageStore : IUsageStore, IDisposable
{
    /// <summary>查询维度的权重倍数（Flow Launcher 用的是 5）。</summary>
    public const double QueryScopeMultiplier = 5.0;

    /// <summary>衰减半衰期（天）。</summary>
    public const double HalfLifeDays = 60.0;

    /// <summary>每个键最多保留多少个查询桶，防止 usage.json 无界增长。</summary>
    const int MaxQueryBucketsPerKey = 16;

    sealed class Entry
    {
        public int Count { get; set; }
        public long LastTicks { get; set; }
        public Dictionary<string, int> ByQuery { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    readonly Dictionary<string, Entry> _entries;
    readonly string _path;
    readonly object _gate = new();
    readonly TimeProvider _time;
    bool _dirty;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public FileUsageStore(string dataDirectory, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _path = Path.Combine(dataDirectory, "usage.json");
        Directory.CreateDirectory(dataDirectory);
        _entries = Load(_path) ?? new(StringComparer.Ordinal);
    }

    public double GetWeight(string usageKey)
    {
        lock (_gate) return _entries.TryGetValue(usageKey, out var e) ? Decay(e.Count, e.LastTicks) : 0;
    }

    public double GetWeight(string usageKey, string? queryText)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(usageKey, out var e)) return 0;
            var global = Decay(e.Count, e.LastTicks);
            if (string.IsNullOrWhiteSpace(queryText)) return global;
            if (!e.ByQuery.TryGetValue(NormalizeQuery(queryText), out var qn)) return global;
            // 查询桶没有独立时间戳，按同一衰减曲线折算后加权叠加
            return global + Decay(qn, e.LastTicks) * QueryScopeMultiplier;
        }
    }

    public int GetActivationCount(string usageKey)
    {
        lock (_gate) return _entries.TryGetValue(usageKey, out var e) ? e.Count : 0;
    }

    public DateTimeOffset? LastActivatedAt(string usageKey)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(usageKey, out var e) || e.LastTicks == 0) return null;
            return new DateTimeOffset(e.LastTicks, TimeSpan.Zero).ToOffset(_time.GetLocalNow().Offset);
        }
    }

    public IReadOnlyList<string> GetTopKeys(string? kindPrefix, int take)
    {
        lock (_gate)
        {
            return _entries
                .Where(kv => kindPrefix is null || kv.Key.StartsWith(kindPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(kv => (kv.Key, W: Decay(kv.Value.Count, kv.Value.LastTicks)))
                .OrderByDescending(x => x.W)
                .Take(take)
                .Select(x => x.Key)
                .ToList();
        }
    }

    public void Record(string usageKey, string? queryText, string providerId, string action)
    {
        if (string.IsNullOrWhiteSpace(usageKey)) return;
        lock (_gate)
        {
            if (!_entries.TryGetValue(usageKey, out var e))
            {
                e = new Entry();
                _entries[usageKey] = e;
            }
            e.Count++;
            e.LastTicks = _time.GetUtcNow().Ticks;

            if (!string.IsNullOrWhiteSpace(queryText))
            {
                var key = NormalizeQuery(queryText!);
                e.ByQuery[key] = e.ByQuery.TryGetValue(key, out var n) ? n + 1 : 1;
                if (e.ByQuery.Count > MaxQueryBucketsPerKey)
                {
                    // 丢最少用的桶
                    foreach (var dead in e.ByQuery.OrderBy(kv => kv.Value).Take(e.ByQuery.Count - MaxQueryBucketsPerKey).Select(kv => kv.Key).ToList())
                        e.ByQuery.Remove(dead);
                }
            }
            _dirty = true;
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        Dictionary<string, Entry> snapshot;
        lock (_gate)
        {
            if (!_dirty) return;
            snapshot = _entries.ToDictionary(kv => kv.Key, kv => new Entry
            {
                Count = kv.Value.Count,
                LastTicks = kv.Value.LastTicks,
                ByQuery = new Dictionary<string, int>(kv.Value.ByQuery, StringComparer.OrdinalIgnoreCase),
            }, StringComparer.Ordinal);
            _dirty = false;
        }
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(snapshot, JsonOptions), ct).ConfigureAwait(false);
        File.Move(tmp, _path, overwrite: true);
    }

    public void Dispose()
    {
        try { FlushAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { /* 退出路径上忽略 */ }
    }

    double Decay(int count, long lastTicks)
    {
        if (count <= 0 || lastTicks == 0) return 0;
        var ageDays = Math.Max(0, (_time.GetUtcNow().Ticks - lastTicks) / (double)TimeSpan.TicksPerDay);
        return count * Math.Pow(0.5, ageDays / HalfLifeDays);
    }

    static string NormalizeQuery(string q) => q.Trim().ToLowerInvariant();

    static Dictionary<string, Entry>? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;   // 缓存坏了就从零开始，绝不让启动失败
        }
    }
}
