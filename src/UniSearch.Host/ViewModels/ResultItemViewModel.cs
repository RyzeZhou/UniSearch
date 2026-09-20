using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using UniSearch.Core.Categories;
using UniSearch.Sdk.Model;
using SdkResult = UniSearch.Sdk.Model.SearchResult;

namespace UniSearch.Host.ViewModels;

/// <summary>chip：来源与命中方式标签。</summary>
public sealed record ChipViewModel(string Label, string BrushKey);

/// <summary>
/// 结果表的一行。
/// <para>
/// 平铺单表（Everything / 资源管理器"详细信息"口径）之后，一行要能同时供好几列取值：
/// 名称、路径、扩展名、大小、修改时间、类型、来源。这些值在<b>映射时一次算好</b>，
/// 不在 XAML 里做转换器 —— 列会被反复重绘（滚动、排序、快照刷新），
/// 每格都跑一次字符串格式化在几百行上是白烧 CPU。
/// </para>
/// </summary>
public sealed partial class ResultItemViewModel : ObservableObject
{
    public required SdkResult Source { get; init; }

    public required string Title { get; init; }

    /// <summary>标题里需要加粗+强调色的区间（由 [[..]] 记法解析而来）。</summary>
    public IReadOnlyList<(int Start, int Length)> Highlight { get; init; } = [];

    /// <summary>
    /// 以首个高亮区间切分成 prefix / highlight / suffix 三段，供 XAML 三个 Run 绑定。
    /// 多段高亮 MVP 退化为普通标题（UI-SPEC §6，M3 再做完整区间高亮）。
    /// </summary>
    public (string Prefix, string HighlightEnd, string Suffix) SplitTitle {
        get {
            var t = Title ?? string.Empty;
            if (Highlight.Count == 0) return (t, string.Empty, string.Empty);
            var (s, len) = Highlight[0];
            if (s >= t.Length) return (t, string.Empty, string.Empty);
            var end = Math.Min(s + len, t.Length);
            return (t[..s], t[s..end], t[end..]);
        }
    }

    public string TitlePrefix => SplitTitle.Prefix;
    public string TitleHighlight => SplitTitle.HighlightEnd;
    public string TitleSuffix => SplitTitle.Suffix;

    /// <summary>图标优先级：缩略图 &gt; shell 真图标 &gt; 字体图标兜底。</summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailPath);
    public bool HasIcon => Icon is not null;
    public bool UsesGlyph => !HasThumbnail && !HasIcon;

    /// <summary>
    /// Shell 真图标。<b>必须可通知</b>：图标由后台线程解析完再批量刷回来，
    /// 如果只是 init 属性，异步结果永远显示不出来。
    /// </summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(UsesGlyph));
            }
        }
    }
    ImageSource? _icon;

    /// <summary>
    /// 是否当前选中。<b>必须可通知</b>：↑↓ 移动选中时并不重建列表，
    /// 若只是普通自动属性，高亮会永远停在第一行不动。
    /// </summary>
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    bool _isSelected;

    // ─────────── 详细列视图的取值（映射时一次算好） ───────────

    /// <summary>"路径"列：完整路径；没有磁盘路径的结果（将来的书签/条目）显示其 URI。</summary>
    public required string FullPath { get; init; }

    /// <summary>"扩展名"列：小写扩展名（不含点）。文件夹留空 —— 写"文件夹"是类型列的活。</summary>
    public required string ExtensionLabel { get; init; }

    /// <summary>"大小"列：人类可读；文件夹没有大小，留空（排序时集中在末端，见 Core 的 ResultSort）。</summary>
    public required string SizeText { get; init; }

    /// <summary>"修改时间"列：绝对时间。多后端证实的条目取最新的那个（融合时已算好）。</summary>
    public required string ModifiedText { get; init; }

    /// <summary>"来源"列：产出这条结果的后端显示名。</summary>
    public required string ProviderLabel { get; init; }

    /// <summary>"类型"列：分类名（文档 / 图片 / 文件夹…）。</summary>
    public string KindName => CategoryEngine.DisplayNameOf(Source.Kind switch
    {
        ResultKind.Folder => CategoryIds.Folders,
        ResultKind.Application => CategoryIds.Applications,
        ResultKind.BibliographicItem => CategoryIds.Literature,
        ResultKind.Note => CategoryIds.Notes,
        _ => CategoryIds.Files,
    });

    public required string Secondary { get; init; }

    /// <summary>右对齐那一小列（旧单行布局的"大小或日期"）。列视图改用专门的列，这里留给预览/工具提示。</summary>
    public string? MetaRight { get; init; }

    public bool HasMetaRight => !string.IsNullOrEmpty(MetaRight);

    public required IReadOnlyList<ChipViewModel> Chips { get; init; }

    /// <summary>字体图标兜底（拿不到 shell 图标时用它）。</summary>
    public required string Glyph { get; init; }

    public string? IconPath { get; init; }

    /// <summary>缩略图（图片类结果）。null = 用图标。</summary>
    public string? ThumbnailPath { get; init; }

    public string Badge => Source.Extension?.ToUpperInvariant() ?? string.Empty;

    /// <summary>
    /// 父目录名。工具提示与预览面板用；列表里已有专门的"路径"列，
    /// 但它保留着是因为有些场景（窄窗口隐藏路径列）仍需要一个短标识。
    /// </summary>
    public string? ParentLabel
    {
        get
        {
            var p = Source.Path;
            if (string.IsNullOrEmpty(p)) return null;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(p);
                if (dir is null) return null;
                var name = System.IO.Path.GetFileName(dir.TrimEnd('\\', '/'));
                return name.Length > 0 ? name : dir;      // 盘符根目录时退回显示整串
            }
            catch { return null; }
        }
    }

    /// <summary>工具提示：路径 + 正文命中摘要（正文片段不进列，会撑破行高）。</summary>
    public string ToolTipText
    {
        get
        {
            var head = Source.Path ?? Source.Uri ?? Title;
            return SnippetPreview is { Length: > 0 } s ? $"{head}\n\n{s}" : head;
        }
    }

    public string? AutoCompleteText => Source.AutoCompleteText;

    public string CopyText => Source.CopyText ?? Source.Path ?? Source.Title;

    public bool CanPreview => !Source.DisablePreview;

    /// <summary>正文命中时把 snippet 显示在 ToolTip 里，避免占行高。</summary>
    public string? SnippetPreview { get; init; }
}
