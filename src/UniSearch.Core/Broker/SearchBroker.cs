using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using UniSearch.Core.Categories;
using UniSearch.Core.Fusion;
using UniSearch.Core.Matching;
using UniSearch.Core.Ranking;
using UniSearch.Core.Selection;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Contracts;
using UniSearch.Sdk.Model;

namespace UniSearch.Core.Broker;

/// <summary>
/// 搜索聚合器。整条流水线在这里：<br/>
/// Query + Context → 能力匹配选 Provider → 并行搜索 → 归一化 → 后过滤 → 去重/融合 → 分类 → 排序 → 快照
/// </summary>
public sealed class SearchBroker : IDisposable
{
    readonly IReadOnlyList<ProviderEntry> _providers;
    readonly CategoryEngine _categories;
    readonly Ranker _ranker;

    /// <summary>同一时刻只有一个活动请求；新请求会让旧请求的所有 Provider 立即收票。</summary>
    readonly object _gate = new();
    CancellationTokenSource? _current;
    readonly Dictionary<string, int> _priorityOf;

    long _requestCounter;

    public SearchBroker(IEnumerable<ProviderEntry> providers, Ranker? ranker = null,
                        CategoryEngine? categories = null, BrokerOptions? options = null)
    {
        _providers = providers.ToList();
        _ranker = ranker ?? new Ranker();
        _categories = categories ?? new CategoryEngine();
        Options = options ?? new BrokerOptions();
        _priorityOf = _providers.ToDictionary(p => p.Descriptor.Id, p => p.Descriptor.Priority, StringComparer.Ordinal);
    }

    /// <summary>
    /// 阈值。设置里一改就换一个新实例（引用赋值是原子的，正在跑的请求下一批就用上新值）——
    /// <b>不要改这个属性返回的对象</b>，它是共享的。
    /// </summary>
    public BrokerOptions Options { get; set; }

    public IReadOnlyList<ProviderEntry> Providers => _providers;

    /// <summary>跑一次搜索。每收到 Provider 增量就产出一个 <see cref="SearchSnapshot"/>，最后一批 <c>IsComplete=true</c>。</summary>
    public async IAsyncEnumerable<SearchSnapshot> RunAsync(
        SearchQuery query, SearchContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var mine = new CancellationTokenSource();
        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            _current = mine;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, mine.Token);
        var token = linked.Token;
        var started = Stopwatch.GetTimestamp();
        var store = new FusionStore(_categories);
        var outcomes = new Dictionary<string, ProviderOutcome>(StringComparer.Ordinal);
        var selection = ProviderSelector.Select(_providers, context, query);

        foreach (var (entry, reason) in selection.Skipped)
            outcomes[entry.Descriptor.Id] = new ProviderOutcome(entry.Descriptor.Id, entry.Descriptor.DisplayName,
                ProviderOutcomeState.Skipped, 0, 0, null, TimeSpan.Zero, BrokerInternal.SkipText(reason));

        var channel = Channel.CreateUnbounded<ProviderEvent>(new UnboundedChannelOptions { SingleReader = true });
        var pending = 0;
        foreach (var e in selection.Selected)
        {
            outcomes[e.Descriptor.Id] = new ProviderOutcome(e.Descriptor.Id, e.Descriptor.DisplayName, ProviderOutcomeState.Running, 0, 0, null, TimeSpan.Zero, null);
            Interlocked.Increment(ref pending);
            _ = Task.Run(() => ConsumeAsync(e, query, context, channel.Writer, token), CancellationToken.None);
        }

        var lastEmit = Stopwatch.GetTimestamp();
        var dirty = false;

        while (Volatile.Read(ref pending) > 0)
        {
            ProviderEvent ev;
            try { ev = await channel.Reader.ReadAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            switch (ev)
            {
                case BatchEvent b:
                    ApplyBatch(b, query, context, store, outcomes);
                    dirty = true;
                    break;
                case FaultEvent f:
                    outcomes[f.ProviderId] = new ProviderOutcome(f.ProviderId, f.DisplayName,
                        f.IsTimeout ? ProviderOutcomeState.Timeout : ProviderOutcomeState.Failed,
                        0, 0, null, Elapsed(started), f.Message);
                    dirty = true;
                    break;
                case DoneEvent d:
                    if (!outcomes.TryGetValue(d.ProviderId, out var old) || old.State is ProviderOutcomeState.Running)
                        outcomes[d.ProviderId] = new ProviderOutcome(d.ProviderId, d.DisplayName, ProviderOutcomeState.Done,
                            d.Returned, d.Kept, d.TotalAvailable, d.Elapsed, null);
                    Interlocked.Decrement(ref pending);
                    dirty = true;
                    break;
            }

            // 节流：Everything 一次可以回 5 万条，但 UI 不需要 5 万次重绘
            if (dirty && Stopwatch.GetElapsedTime(lastEmit) >= Options.SnapshotThrottle)
            {
                lastEmit = Stopwatch.GetTimestamp();
                dirty = false;
                yield return BuildSnapshot(query, context, store, outcomes, started, complete: false);
            }
        }

        yield return BuildSnapshot(query, context, store, outcomes, started, complete: true);
    }

    // ───────────────────────────────── 单个 Provider 的取流 ─────────────────────────────────

    async Task ConsumeAsync(ProviderEntry entry, SearchQuery query, SearchContext context,
                            ChannelWriter<ProviderEvent> writer, CancellationToken ct)
    {
        var id = entry.Descriptor.Id;
        var name = entry.Descriptor.DisplayName;
        var sw = Stopwatch.StartNew();
        var returned = 0;
        var kept = 0;
        int? total = null;
        using var perProvider = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perProvider.CancelAfter(query.Deadline > TimeSpan.Zero ? query.Deadline : Options.DefaultDeadline);

        // 该后端能否原生限定目录？能则不问它要范围表达式外的事，不能则由 Core 后过滤。
        var scopeNative = context.IsDirectoryBounded
            && entry.Provider is IFileSystemScopedProvider { } scoped
            && scoped.TranslateScope(context) is not null;

        try
        {
            await foreach (var batch in entry.Provider.SearchAsync(query, context, perProvider.Token).ConfigureAwait(false))
            {
                if (batch.RequestId != 0 && batch.RequestId != query.RequestId) continue;   // Provider 忘了丢弃过期结果，这里兜底
                perProvider.Token.ThrowIfCancellationRequested();
                returned += batch.Results.Count;
                total ??= batch.TotalAvailable;
                var keptNow = Filter(batch.Results, query, context, entry, scopeNative);
                kept += keptNow.Count;
                if (keptNow.Count > 0)
                    await writer.WriteAsync(new BatchEvent(batch.ProviderId, name, batch.RequestId, keptNow, batch.TotalAvailable), ct).ConfigureAwait(false);
                if (batch.IsLast) break;
            }
            await writer.WriteAsync(new DoneEvent(id, name, returned, kept, total, sw.Elapsed), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await writer.WriteAsync(new DoneEvent(id, name, returned, kept, total, sw.Elapsed), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await writer.WriteAsync(new FaultEvent(id, name, $"超时 > {(Options.DefaultDeadline.TotalMilliseconds):F0}ms，已降级", sw.Elapsed, true), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await writer.WriteAsync(new FaultEvent(id, name, ex.Message, sw.Elapsed, false), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Core 侧统一语义：Provider 没能力做的事（限定目录、扩展名、类型、隐藏属性）由这里补齐，
    /// 保证“同一个查询词在哪个后端上都得到同样可解释的结果集”。
    /// </summary>
    IReadOnlyList<SearchResult> Filter(IReadOnlyList<SearchResult> items, SearchQuery q, SearchContext ctx,
                                       ProviderEntry entry, bool scopeNative)
    {
        if (items.Count == 0) return items;
        var caps = entry.Descriptor.Capabilities;
        var list = new List<SearchResult>(items.Count);

        foreach (var r in items)
        {
            if (!scopeNative && ctx.IsDirectoryBounded && r.Path is not null &&
                !Canonicalization.IsWithinDirectory(r.Path, ctx.RootPath!)) continue;

            if (Options.FilterHiddenAndSystemByDefault && q.Filters.ExcludeHidden && IsHiddenOrSystem(r)) continue;

            // UI 顶部分类标签被点击：Core 做硬过滤（Provider 只把它当提示）
            if (q.ForcedCategory is { Length: > 0 } forced && forced != CategoryIds.All &&
                !string.Equals(_categories.Classify(r), forced, StringComparison.Ordinal)) continue;

            if (q.Filters.FoldersOnly && r.Kind != ResultKind.Folder) continue;

            if (q.Filters.Extensions.Count > 0 && !caps.Has(ProviderCapability.SupportsKindFilter))
            {
                var ext = r.Extension;
                if (ext is null || !q.Filters.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
            }

            if (q.Filters.Kinds.Count > 0 && !q.Filters.Kinds.Contains(r.Kind) && r.Kind != ResultKind.Unknown) continue;

            // 子类型：能下推的后端（Zotero 的 itemType=）已经在后端收敛过了，这里兜住不能下推的。
            if (q.Filters.Subtypes.Count > 0 &&
                (r.Subtype is not { Length: > 0 } sub ||
                 !q.Filters.Subtypes.Contains(sub, StringComparer.OrdinalIgnoreCase))) continue;

            if (q.Filters.MinSizeBytes is { } min && r.SizeBytes < min) continue;
            if (q.Filters.MaxSizeBytes is { } max && r.SizeBytes > max) continue;
            if (q.Filters.ModifiedAfter is { } after && r.ModifiedAt < after) continue;

            // 结构化查询交给后端自己解释，Core 不得再用词项做 AND 后过滤（否则会破坏 content: / (a|b) 语义）
            if (!q.IsStructured && q.Terms.Count > 0 && !NameMatcher.MatchesAll(r.Title, q.Terms) &&
                !r.Match.HasFlag(MatchKind.Content) && !NameMatcher.MatchesAll(r.Subtitle ?? "", q.Terms))
            {
                continue;   // 后端返回了完全不相干的东西（跨后端语义对齐）
            }

            list.Add(r);
        }
        return list;
    }

    static bool IsHiddenOrSystem(SearchResult r) =>
        r.Metadata.TryGetValue("attributes", out var v) && int.TryParse(v, out var attr) && (attr & (2 | 4)) != 0;

    // ───────────────────────────────── 融合 + 快照 ─────────────────────────────────

    void ApplyBatch(BatchEvent b, SearchQuery q, SearchContext ctx, FusionStore store,
                    Dictionary<string, ProviderOutcome> outcomes)
    {
        foreach (var r in b.Results)
        {
            var fused = store.Add(r);
            var name = NameMatcher.Score(r.Title, q.MatchTarget);
            if (name > fused.NameScore) fused.NameScore = Math.Max(0, name);
            fused.CategoryId = fused.NameMatched
                ? _categories.Classify(fused.Display)
                : _categories.Classify(fused.Display) == CategoryIds.ContentMatches
                    ? CategoryIds.ContentMatches
                    : fused.CategoryId;
        }

        if (!outcomes.TryGetValue(b.ProviderId, out var old) || old.State is ProviderOutcomeState.Running)
            outcomes[b.ProviderId] = old is null
                ? new ProviderOutcome(b.ProviderId, b.DisplayName, ProviderOutcomeState.Running, b.Results.Count, b.Results.Count, b.TotalAvailable, TimeSpan.Zero, null)
                : old with { State = ProviderOutcomeState.Running, Returned = old.Returned + b.Results.Count, Kept = old.Kept + b.Results.Count, TotalAvailable = b.TotalAvailable ?? old.TotalAvailable };
    }

    SearchSnapshot BuildSnapshot(SearchQuery q, SearchContext ctx, FusionStore store,
                                 Dictionary<string, ProviderOutcome> outcomes, long started, bool complete)
    {
        var ranked = new List<(FusedResult F, double S)>(store.Count);
        foreach (var f in store.Items)
        {
            var priority = f.ProviderIds.Max(id => _priorityOf.TryGetValue(id, out var p) ? p : 0);
            f.Score = _ranker.Score(f, q, ctx, priority);
            ranked.Add((f, f.Score));
        }

        // 单分类视图（点了某个标签）用更宽松的上限：用户已经明确说了"我就要这一类"，
        // 再按 24 条截断等于逼他翻页。这两个值以前都叫 PerCategoryLimit，其中 InFocus 那份
        // 定义了却从没被用过 —— 设置项引用了它才发现（现在真的生效了）。
        var inFocus = q.ForcedCategory is { Length: > 0 } fc && fc != CategoryIds.All;
        var groupLimit = inFocus ? Options.PerCategoryLimitInFocus : Options.PerCategoryLimit;

        var groups = ranked
            .GroupBy(x => x.F.CategoryId)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(x => x.S).Select(x => x.F).ToList();
                var limit = groupLimit;
                var trimmed = ordered.Count > limit;
                var shown = trimmed ? ordered.Take(limit).ToList() : ordered;
                var providers = shown.SelectMany(f => f.ProviderIds).Distinct(StringComparer.Ordinal).ToList();
                return new CategoryGroup(g.Key, CategoryEngine.DisplayNameOf(g.Key), shown, ordered.Count, trimmed, providers);
            })
            .OrderBy(g => CategoryEngine.OrderOf(g.CategoryId))
            .ToList();

        return new SearchSnapshot(q.RequestId, groups, outcomes.Values
            .OrderBy(o => o.State == ProviderOutcomeState.Skipped ? 1 : 0)
            .ThenBy(o => o.DisplayName).ToList(), store.Count, complete, Elapsed(started));
    }

    static TimeSpan Elapsed(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);

    public void Dispose() { lock (_gate) { _current?.Cancel(); _current?.Dispose(); _current = null; } }
}

// ───────────────────────────────── 事件（Provider → Broker 单向流） ─────────────────────────────────

abstract record ProviderEvent(string ProviderId, string DisplayName);

sealed record BatchEvent(string ProviderId, string DisplayName, long RequestId,
                         IReadOnlyList<SearchResult> Results, int? TotalAvailable)
    : ProviderEvent(ProviderId, DisplayName);

sealed record DoneEvent(string ProviderId, string DisplayName, int Returned, int Kept,
                        int? TotalAvailable, TimeSpan Elapsed)
    : ProviderEvent(ProviderId, DisplayName);

sealed record FaultEvent(string ProviderId, string DisplayName, string Message, TimeSpan Elapsed, bool IsTimeout)
    : ProviderEvent(ProviderId, DisplayName);

static partial class BrokerInternal
{
    /// <summary>把跳过原因翻译成人话 —— 状态条与诊断面板直接显示。</summary>
    public static string SkipText(SkipReason r) => r switch
    {
        SkipReason.NotEnabled => "已在设置中禁用",
        SkipReason.NoGlobalScope => "未声明全局搜索能力",
        SkipReason.CannotScopeToDirectory => "不支持限定目录，已在文件夹搜索中跳过",
        SkipReason.CapabilityMismatch => "能力与查询不匹配",
        SkipReason.HealthUnavailable => "后端不可用",
        SkipReason.NotApplicableToKindFilter => "无法产出所请求的结果类型",
        SkipReason.NotInScope => "不在当前来源范围内（左侧来源栏未选中它）",
        _ => r.ToString(),
    };
}
