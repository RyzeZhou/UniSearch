using UniSearch.Core.Categories;
using UniSearch.Sdk.Model;

namespace UniSearch.Core.Fusion;

/// <summary>
/// 同一实体在多个 Provider 中的命中合并后的结果。
/// UI 只看见 <b>一行</b>，但这一行知道自己被谁证实过：
/// <code>
/// 📚 Attention Is All You Need        ← Zotero 提供标题/作者
/// Vaswani et al. · 2017
/// PDF · D:\Papers\paper.pdf           ← Everything 提供路径/大小/时间
/// [文件名匹配] [正文 12 处] [Zotero]   ← 溯源标签
/// </code>
/// </summary>
public sealed class FusedResult
{
    public required string FusionKey { get; init; }

    /// <summary>决定这一行“是什么、叫什么”的主结果（见 <see cref="FusionStore.Add"/> 的选取规则）。</summary>
    public required SearchResult Display { get; set; }

    /// <summary>全部贡献，按 Provider 优先级降序。键 = providerId（同 Provider 多次命中只留最佳一条）。</summary>
    public readonly Dictionary<string, SearchResult> Contributions = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> ProviderIds => Contributions.Keys;

    /// <summary>是否至少有一个 Provider 命中了名称/标题。</summary>
    public bool NameMatched { get; set; }

    /// <summary>是否至少有一个 Provider 命中了正文。</summary>
    public bool ContentMatched { get; set; }

    /// <summary>正文命中次数（取各 Provider 最大值）。</summary>
    public int? ContentMatchCount { get; set; }

    public string? Snippet { get; set; }

    public string CategoryId { get; set; } = CategoryIds.More;

    /// <summary>所有贡献里最新的修改时间。</summary>
    public DateTimeOffset? ModifiedAt { get; set; }

    public double Score { get; set; }
    public double NameScore { get; set; }

    /// <summary>合并后的展示标签：命中方式 + 每一路来源 + 正文命中。</summary>
    public IReadOnlyList<ResultTag> BuildTags()
    {
        var tags = new List<ResultTag>(Display.Tags.Count + Contributions.Count + 2);
        tags.AddRange(Display.Tags);

        if (NameMatched && Display.Match != MatchKind.None)
            tags.Insert(0, new ResultTag(TagFor(Display.Match), ResultTagTone.Success));

        // 多后端证实的实体要标明“每一路都命中过”，**包括决定标题的那一路**；
        // 否则会出现“标题来自 Zotero，但 Zotero 标签不见了”的怪现象（单元测试会抓到）。
        var multi = Contributions.Count > 1;
        foreach (var c in Contributions.Values
                     .OrderByDescending(c => string.Equals(c.ProviderId, Display.ProviderId, StringComparison.Ordinal)))
        {
            var isSelf = string.Equals(c.ProviderId, Display.ProviderId, StringComparison.Ordinal);
            if (isSelf && !multi) continue;                        // 单一来源不必自报家门
            if (tags.Any(t => string.Equals(t.Label, c.ProviderId, StringComparison.Ordinal))) continue;
            tags.Add(new ResultTag(c.ProviderId, isSelf ? ResultTagTone.Accent : ResultTagTone.Info));
        }

        if (ContentMatched)
            tags.Add(new ResultTag(ContentMatchCount is > 1 ? $"正文 {ContentMatchCount} 处" : "正文命中", ResultTagTone.Accent));
        return tags;
    }

    static string TagFor(MatchKind m) => m switch
    {
        MatchKind.ExactName => "完全匹配",
        MatchKind.NamePrefix => "前缀匹配",
        MatchKind.NameWord => "名称匹配",
        MatchKind.NameFuzzy => "模糊匹配",
        MatchKind.Path => "路径匹配",
        MatchKind.Metadata => "元数据匹配",
        MatchKind.Usage => "常用地",
        _ => "已匹配",
    };
}
