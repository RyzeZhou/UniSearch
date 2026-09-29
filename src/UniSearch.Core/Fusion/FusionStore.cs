using UniSearch.Core.Categories;
using UniSearch.Sdk.Model;

namespace UniSearch.Core.Fusion;

/// <summary>
/// 流式聚合期间的融合状态表。每来一批结果就 <see cref="Add"/>，UI 立刻能看到，
/// 后续 Provider 到达时<b>原地升级</b>已有行（补标签、换更权威的标题），而不是再插一行。
/// </summary>
public sealed class FusionStore
{
    readonly Dictionary<string, FusedResult> _byKey = new(StringComparer.Ordinal);
    readonly CategoryEngine _categories;

    public FusionStore(CategoryEngine? categories = null) => _categories = categories ?? new CategoryEngine();

    public int Count => _byKey.Count;
    public IEnumerable<FusedResult> Items => _byKey.Values;

    public FusedResult Add(SearchResult r)
    {
        if (!_byKey.TryGetValue(r.FusionKey, out var fused))
        {
            fused = new FusedResult
            {
                FusionKey = r.FusionKey,
                Display = r,
                CategoryId = _categories.Classify(r),
                ContentMatchCount = r.ContentMatchCount,
                ModifiedAt = r.ModifiedAt,
            };
            _byKey[r.FusionKey] = fused;
        }

        // 同一 Provider 反复命中：只保留更好的那条（名称命中优先，其次正文数多者优先）
        if (fused.Contributions.TryGetValue(r.ProviderId, out var existing))
        {
            if (IsBetter(r, existing)) { fused.Contributions[r.ProviderId] = r; if (ReferenceEquals(fused.Display, existing)) fused.Display = r; }
        }
        else fused.Contributions[r.ProviderId] = r;

        // —— 逐字段升级 —— 
        fused.NameMatched |= r.Match is not (MatchKind.None or MatchKind.Content);
        fused.ContentMatched |= r.Match.HasFlag(MatchKind.Content);
        if (r.ContentMatchCount is { } n)
            fused.ContentMatchCount = Math.Max(fused.ContentMatchCount ?? 0, n);
        if (r.Snippet is { Length: > 0 } s && (fused.Snippet is null || s.Length > fused.Snippet.Length)) fused.Snippet = s;
        if (r.ModifiedAt is { } mt && (fused.ModifiedAt is null || mt > fused.ModifiedAt)) fused.ModifiedAt = mt;

        // 标题权威性：知识对象（文献/笔记）> 文件名。路径与大小信息则始终偏向文件系统贡献。
        if (AuthorityOf(r) > AuthorityOf(fused.Display)) fused.Display = r;
        else if (fused.Display.Path is null && r.Path is not null)
            fused.Display = fused.Display with { Path = r.Path, SizeBytes = r.SizeBytes ?? fused.Display.SizeBytes };

        // 名称命中到了就把分类从“正文命中”纠正回实体本身。
        // <para>
        // <b>「正文命中」只对文件有意义</b>：它描述的是"文件名没中、里面的正文中了"这一组对立。
        // 知识对象（思源的笔记块、无路径的条目）没有这组对立 —— 内容就是它本身，
        // 硬套上去会让它们<b>全部丢掉自己的分类</b>：实测思源返回的 60 个块全落进「正文命中」，
        // 而那个分类在思源下被隐藏（它恒等于全部），于是标签栏只剩一个计数为 0 的「全部」。
        // </para>
        var byKind = _categories.Classify(fused.Display);
        fused.CategoryId = !fused.NameMatched && fused.ContentMatched
                           && fused.Display.Path is { Length: > 0 }
            ? CategoryIds.ContentMatches
            : byKind;

        return fused;
    }

    public bool TryGet(string fusionKey, out FusedResult fused) => _byKey.TryGetValue(fusionKey, out fused!);

    static bool IsBetter(SearchResult a, SearchResult b)
    {
        var na = NameRank(a.Match); var nb = NameRank(b.Match);
        if (na != nb) return na > nb;
        return (a.ContentMatchCount ?? 0) > (b.ContentMatchCount ?? 0);
    }

    static int NameRank(MatchKind m) => m switch
    {
        MatchKind.ExactName => 5,
        MatchKind.NamePrefix => 4,
        MatchKind.NameWord => 3,
        MatchKind.Metadata => 2,
        MatchKind.NameFuzzy or MatchKind.Path => 2,
        MatchKind.Content => 1,
        _ => 0,
    };

    /// <summary>谁有资格决定这一行的标题与图标。</summary>
    static int AuthorityOf(SearchResult r) => r.Kind switch
    {
        ResultKind.BibliographicItem => 60,
        ResultKind.Note => 55,
        ResultKind.Application => 50,
        ResultKind.Document => 40,
        ResultKind.Attachment => 35,
        ResultKind.Email or ResultKind.WebPage or ResultKind.Bookmark => 30,
        ResultKind.File => r.Match.HasFlag(MatchKind.Content) ? 10 : 20,
        _ => 15,
    };
}
