using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Contracts;
using UniSearch.Sdk.Model;

namespace UniSearch.Core.Selection;

public sealed record ProviderEntry(ISearchProvider Provider, ProviderDescriptor Descriptor)
{
    public bool Enabled { get; set; } = true;
    public string? DisabledReason { get; set; }
}

public enum SkipReason { NotEnabled, NoGlobalScope, CannotScopeToDirectory, CapabilityMismatch, HealthUnavailable, NotApplicableToKindFilter, NotApplicableToFacetFilter, NotInScope }

public sealed record ProviderSelection(IReadOnlyList<ProviderEntry> Selected, IReadOnlyList<(ProviderEntry Entry, SkipReason Reason)> Skipped);

/// <summary>
/// 调度决策。<b>不是“把查询广播给所有插件”</b>，而是先按上下文与能力过滤：
/// Explorer 目录内搜索时，不具备目录限定能力的 Provider（Zotero、书签、设置…）被直接跳过。
/// </summary>
public static class ProviderSelector
{
    public static ProviderSelection Select(IEnumerable<ProviderEntry> all, SearchContext ctx, SearchQuery q)
    {
        var picked = new List<ProviderEntry>();
        var skipped = new List<(ProviderEntry, SkipReason)>();

        foreach (var e in all.OrderBy(x => -x.Descriptor.Priority))
        {
            if (!e.Enabled) { skipped.Add((e, SkipReason.NotEnabled)); continue; }

            // 来源限定（左侧来源栏）：用户说了"就搜这个后端"，其余的直接不进调度。
            // 放在能力检查之前：这样状态条报的是"未选中该来源"，而不是某个能力不匹配的次要原因。
            if (q.HasProviderScope &&
                !q.ProviderScope!.Contains(e.Descriptor.Id, StringComparer.OrdinalIgnoreCase))
            {
                skipped.Add((e, SkipReason.NotInScope));
                continue;
            }

            if (ctx.IsDirectoryBounded)
            {
                if (e.Provider is not IFileSystemScopedProvider scoped || !scoped.CanScopeTo(ctx))
                {
                    skipped.Add((e, SkipReason.CannotScopeToDirectory));
                    continue;
                }
            }
            else if (e.Provider is not IGlobalScopeProvider g || !g.EnabledForGlobalScope(ctx))
            {
                skipped.Add((e, SkipReason.NoGlobalScope));
                continue;
            }

            // 显式类型过滤：Provider 声明能不能产出该 kind；扩展名过滤则要求它"能返回文件"或"支持类型筛选"。
            // 例如 ext:pdf 时不该去问 Zotero（它只能返回文献条目，问它纯属浪费并会污染结果）。
            if (!CanSatisfyTypeFilter(e.Descriptor.Capabilities, q))
            {
                skipped.Add((e, SkipReason.NotApplicableToKindFilter));
                continue;
            }

            // 只在用户明确要求搜正文时，才把不搜正文的后端排除掉（否则纯文件名查询也能用它）
            if (q.Filters.Extensions.Count > 0 && !e.Descriptor.Capabilities.Has(ProviderCapability.SupportsKindFilter))
            {
                // 不剔除，只降级：Core 会做后过滤。这里保留它。
            }

            // 值域过滤（标签这类"候选值来自后端"的条件）：只有能把它下推进自己查询的后端才该跑。
            // 刻意**不做前端近似过滤** —— 候选值是从 Zotero 拿的，拿它去匹配 Everything 的文件行
            // 只会得到空列表；用户看到的是"选了标签就什么都没有"，而真相是这个后端根本不懂标签。
            // 判据是"真的选了值"（HasFacetSelection），空壳不算 —— 否则把最后一个勾去掉的瞬间
            // 就会少一个后端，而用户什么都没改。
            if (q.Filters.HasFacetSelection && e.Provider is not IFacetProvider)
            {
                skipped.Add((e, SkipReason.NotApplicableToFacetFilter));
                continue;
            }

            picked.Add(e);
        }

        return new ProviderSelection(picked, skipped);
    }

    static bool CanProduce(ProviderCapability caps, IReadOnlyList<ResultKind> kinds) => kinds.Any(k => KindFitsCaps(caps, k));

    /// <summary>类型/扩展名过滤是否可能被该 Provider 满足。</summary>
    static bool CanSatisfyTypeFilter(ProviderCapability caps, SearchQuery q)
    {
        if (q.Filters.Kinds.Count > 0 && !CanProduce(caps, q.Filters.Kinds)) return false;
        if (q.Filters.Extensions.Count > 0 &&
            !caps.Has(ProviderCapability.ReturnsFiles) &&
            !caps.Has(ProviderCapability.SupportsKindFilter)) return false;
        return true;
    }

    /// <summary>
    /// 能力匹配。
    /// <para>
    /// <see cref="ProviderCapability.ReturnsFiles"/> 的语义是"能返回任意文件系统对象"，
    /// 因此它天然覆盖 文件/文档/图片/视频/音频/压缩包/代码 —— 否则一个"什么文件都能搜"的
    /// Everything 会被 <c>kind:pdf</c> 这种过滤排除掉（实际踩过的坑）。
    /// 细分位是给"只产出某一类"的后端用的（例如只返回图片的索引器）。
    /// </para>
    /// </summary>
    static bool KindFitsCaps(ProviderCapability caps, ResultKind k)
    {
        var fileLike = caps.Has(ProviderCapability.ReturnsFiles);
        return k switch
        {
            ResultKind.File => fileLike,
            ResultKind.Folder => fileLike || caps.Has(ProviderCapability.ReturnsFolders),
            ResultKind.Document => fileLike || caps.Has(ProviderCapability.ReturnsDocuments),
            ResultKind.Image => fileLike || caps.Has(ProviderCapability.ReturnsImages),
            ResultKind.Video => fileLike || caps.Has(ProviderCapability.ReturnsVideos),
            ResultKind.Audio => fileLike || caps.Has(ProviderCapability.ReturnsAudio),
            ResultKind.ArchiveEntry => fileLike || caps.Has(ProviderCapability.ReturnsArchives),
            ResultKind.CodeSymbol => fileLike,
            ResultKind.Attachment => fileLike || caps.Has(ProviderCapability.ReturnsAttachments),
            ResultKind.Application => caps.Has(ProviderCapability.ReturnsApplications),
            ResultKind.BibliographicItem => caps.Has(ProviderCapability.ReturnsBibliographicItems),
            ResultKind.Note => caps.Has(ProviderCapability.ReturnsNotes),
            ResultKind.Email => caps.Has(ProviderCapability.ReturnsEmails),
            ResultKind.Bookmark or ResultKind.HistoryEntry or ResultKind.WebPage => caps.Has(ProviderCapability.ReturnsWebBookmarks),
            ResultKind.Setting => caps.Has(ProviderCapability.ReturnsSettings),
            _ => true,   // 未建模的类型不据此剔除
        };
    }
}
