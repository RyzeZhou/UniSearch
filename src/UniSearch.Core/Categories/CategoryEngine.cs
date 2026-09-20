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
}
