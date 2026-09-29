using UniSearch.Sdk.Model;

namespace UniSearch.Core.Categories;

/// <summary>
/// 决定一个结果属于哪个 UI 分类。<b>Provider 从不参与这个决定</b>（这是整个前端可长期扩展的关键约束）：
/// Provider 只声明 <see cref="SearchResult.Kind"/> / <c>Subtype</c> / <see cref="MatchKind"/>，
/// 分类规则集中在这里，可用 categories.json 覆盖，加插件不需要改 UI。
/// </summary>
public sealed class CategoryEngine
{
    readonly Dictionary<string, string> _extOverrides;

    public CategoryEngine(IReadOnlyDictionary<string, string>? extensionOverrides = null) =>
        _extOverrides = extensionOverrides is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(extensionOverrides, StringComparer.OrdinalIgnoreCase);

    /// <summary>分类 id（永远是 <see cref="CategoryIds.Defaults"/> 里的某个 Id，未知类型落到 more）。</summary>
    public string Classify(SearchResult r)
    {
        // 1) Provider 明确给出的高优先级语义类型优先于扩展名
        switch (r.Kind)
        {
            case ResultKind.Folder: return CategoryIds.Folders;
            case ResultKind.Application: return CategoryIds.Applications;
            case ResultKind.BibliographicItem: return CategoryIds.Literature;
            case ResultKind.Note: return CategoryIds.Notes;
            case ResultKind.Image: return CategoryIds.Images;
            case ResultKind.Video: return CategoryIds.Videos;
            case ResultKind.Audio: return CategoryIds.Music;
            case ResultKind.Bookmark:
            case ResultKind.HistoryEntry:
            case ResultKind.WebPage: return CategoryIds.Web;
            case ResultKind.CodeSymbol: return CategoryIds.Code;
            case ResultKind.ArchiveEntry: return CategoryIds.Archives;
            case ResultKind.Setting: return CategoryIds.Applications;
        }

        // 2) 有路径的对象按扩展名细分
        var ext = r.Extension ?? r.Subtype;
        if (ext is not null)
        {
            if (_extOverrides.TryGetValue(ext, out var custom)) return custom;
            if (CategoryIds.ExtensionMap.TryGetValue(ext, out var cat)) return cat;
        }

        // 3) Kind 兜底
        if (CategoryIds.KindMap.TryGetValue(r.Kind, out var byKind)) return byKind;

        // 4) 正文命中的纯文件
        if (r.Match.HasFlag(MatchKind.Content)) return CategoryIds.ContentMatches;

        return r.Path is null ? CategoryIds.More : CategoryIds.Files;
    }

    /// <summary>某个分类在顶栏上的展示名。</summary>
    public static string DisplayNameOf(string categoryId) => CategoryIds.Find(categoryId)?.DisplayName ?? categoryId;

    /// <summary>分类排序：内置表顺序；自定义分类排在 more 之前。</summary>
    public static int OrderOf(string categoryId) => CategoryIds.Find(categoryId)?.Order ?? 800;

    /// <summary>
    /// 某个内置分类在某个后端下**有没有意义**（无意义的不该出现在标签栏里）。
    /// <para>
    /// <b>刻意按三个已知后端逐个写死</b>（2026-09-28 用户拍板："目前还是按这三个特定的后端去各自设计"）。
    /// 没做成 <see cref="CategoryDescriptor"/> 上的通用 providers 字段：只有三个后端时，
    /// 那层抽象的间接性大于收益。等出现第四个非文件类后端，再把这张表提取成声明式字段。
    /// </para>
    /// <para>
    /// 与「筛选器按后端分叉」（<c>filters.json</c> 的 <c>providers</c>）是<b>两件事</b>：
    /// 那条管自定义筛选器，这条管这十几个内置分类。
    /// </para>
    /// </summary>
    /// <param name="categoryId">内置分类 id。</param>
    /// <param name="providerId">当前限定的后端；null = 没限定（那就都显示）。</param>
    public static bool AppliesToProvider(string categoryId, string? providerId) => providerId switch
    {
        // 内容检索后端：
        // · 「正文命中」恒等于「全部」（它本来就是全文搜索）→ 纯噪声
        // · 其余几个按扩展名的分类在这个索引上没有对应物
        // ⚠ 这一行跟着 AnyTXT 的**索引范围**走 —— 它现在只收 txt/md 这类文档；
        //   哪天索引里有了图片/视频，要把对应的项放开，否则那些结果点不到。
        "anytxt" => categoryId is not (CategoryIds.ContentMatches or CategoryIds.Folders or CategoryIds.Images
                                       or CategoryIds.Videos or CategoryIds.Music or CategoryIds.Applications
                                       or CategoryIds.Archives),

        // 文献库：没有目录、图片、视频、音乐、应用、压缩包这些概念
        // （附件理论上可以是任意文件类型，所以只掐掉"结构上就不存在"的那几个）
        "zotero" => categoryId is not (CategoryIds.Folders or CategoryIds.Images or CategoryIds.Videos
                                       or CategoryIds.Music or CategoryIds.Applications or CategoryIds.Archives),

        // 知识库笔记块：「正文命中」在这里同样近乎恒等于「全部」——
        // 思源返回的每一行都是"某个块的内容"，只有占比极小的文档块（5275 里 97 个）算标题命中。
        // 留着它只会让人以为"点一下能筛出正文"（其实什么也没筛掉），名字本身也误导。
        "siyuan" => categoryId is not (CategoryIds.ContentMatches or CategoryIds.Folders or CategoryIds.Images
                                       or CategoryIds.Videos or CategoryIds.Music or CategoryIds.Applications
                                       or CategoryIds.Archives),

        _ => true,
    };
}
