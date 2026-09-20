using UniSearch.Core.Broker;
using UniSearch.Core.Categories;
using UniSearch.Core.Fusion;
using UniSearch.Host.Services;
using UniSearch.Sdk.Model;

namespace UniSearch.Host.ViewModels;

/// <summary>
/// Core 快照 → UI 视图模型。
/// 单独一层的目的：XAML 永远不直接绑定 FusedResult，
/// 这样"命中高亮怎么算、chip 显示什么、右列放大小还是日期"这类呈现决策集中可测。
/// </summary>
public static class SnapshotMapper
{
    /// <summary>
    /// 宿主启动时注入的图标调度器。为 null 时结果行退化为字体图标（可读性差，但不至于空白）。
    /// 用静态字段而不是构造参数，是为了让这一层保持纯函数、单元测试不需要真去问 Shell。
    /// </summary>
    public static IconLoader? Icons { get; set; }
    /// <summary>把 [[..]] 记法拆成纯文本 + 高亮区间（UI 用 Run 加粗，见移植自 ET 的 HighlightedText 思路）。</summary>
    public static (string Text, IReadOnlyList<(int Start, int Length)> Highlight) UnwrapHighlight(string? marked)
    {
        if (string.IsNullOrEmpty(marked)) return (string.Empty, []);
        if (!marked.Contains("[[")) return (marked, []);

        var sb = new System.Text.StringBuilder(marked.Length);
        var ranges = new List<(int, int)>();
        var i = 0;
        while (i < marked.Length)
        {
            var open = marked.IndexOf("[[", i, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(marked, i, marked.Length - i);
                break;
            }
            sb.Append(marked, i, open - i);
            var close = marked.IndexOf("]]", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                sb.Append(marked, open, marked.Length - open);
                break;
            }
            var len = close - (open + 2);
            sb.Append(marked, open + 2, len);
            ranges.Add((sb.Length - len, len));
            i = close + 2;
        }
        return (sb.ToString(), ranges);
    }

    public static ResultItemViewModel Map(FusedResult f)
    {
        var d = f.Display;
        // Everything 会自己回带 *命中* 标记；其它后端给纯标题 —— 这里统一成"标题 + 可选区间"。
        var (title, hl) = UnwrapHighlight(d.Title.Replace('*', '\u0001'));
        title = title.Replace('\u0001', '*');

        var chips = new List<ChipViewModel>(6);
        foreach (var t in f.BuildTags())
            chips.Add(new ChipViewModel(t.Label, t.Tone switch
            {
                ResultTagTone.Info => "Brush.Chip.Info",
                ResultTagTone.Success => "Brush.Chip.Success",
                ResultTagTone.Warning => "Brush.Chip.Warning",
                ResultTagTone.Danger => "Brush.Chip.Danger",
                ResultTagTone.Accent => "Brush.Chip.Accent",
                _ => "Brush.Chip.Neutral",
            }));

        var vm = new ResultItemViewModel
        {
            Source = d,
            Title = title.Length > 0 ? title : d.Title,
            Highlight = hl,
            Secondary = d.DisplaySecondary,
            MetaRight = Meta(d, f),
            Chips = chips,
            Glyph = GlyphFor(f.CategoryId),
            IconPath = d.Path,
            ThumbnailPath = d.Icon?.ImagePath,
            SnippetPreview = f.Snippet is { Length: > 0 } s ? UnwrapHighlight(s).Text : null,

            // ── 详细列视图的取值：在这里一次算好，XAML 里不再做格式化 ──
            FullPath = d.Path is { Length: > 0 } p ? p : d.Uri ?? string.Empty,
            ExtensionLabel = d.IsFolder ? string.Empty : d.Extension ?? string.Empty,
            SizeText = !d.IsFolder && d.SizeBytes is { } size ? Formatting.HumanSize(size) : string.Empty,
            // 融合后的修改时间比单个贡献里的更全（多后端证实时取最新），优先用它
            ModifiedText = (f.ModifiedAt ?? d.ModifiedAt) is { } mt ? Formatting.FullStamp(mt) : string.Empty,
            ProviderLabel = Pretty(d.ProviderId),
        };

        // 图标走调度器：缓存命中即刻设上，未命中丢后台线程（行上先显示字体图标兜底）
        Icons?.Load(vm, new IconKey(d.Extension, d.IsFolder, d.Kind == ResultKind.Application, d.Path));
        return vm;
    }

    /// <summary>右列：文件夹看子项数没有意义，文件看大小；有修改时间且不大时优先大小。</summary>
    static string? Meta(SearchResult d, FusedResult f)
    {
        if (d.SizeBytes is { } size && !d.IsFolder) return Formatting.HumanSize(size);
        if (f.ModifiedAt is { } mt) return Formatting.ShortDate(mt);
        return null;
    }

    public static string Pretty(string providerId) => providerId switch
    {
        "everything" => "Everything",
        "anytxt" => "AnyTXT",
        "zotero" => "Zotero",
        "windows-index" => "Windows 索引",
        "apps" => "应用",
        _ => providerId,
    };

    public static string GlyphFor(string categoryId) => categoryId switch
    {
        CategoryIds.All => "\uE71D",
        CategoryIds.Files or CategoryIds.Documents => "\uE8A5",
        CategoryIds.Folders => "\uE8B7",
        CategoryIds.Images => "\uEB9F",
        CategoryIds.Videos => "\uE786",
        CategoryIds.Music => "\uE8D6",
        CategoryIds.Applications => "\uECA5",
        CategoryIds.Literature => "\uE8F1",
        CategoryIds.Notes => "\uE70B",
        CategoryIds.ContentMatches => "\uE85C",
        CategoryIds.Archives => "\uF129",
        CategoryIds.Code => "\uE943",
        CategoryIds.Web => "\uE774",
        _ => "\uE712",
    };
}

/// <summary>紧凑界面用的极简格式化（不要在此处做本地化实验，文案另有 resx）。</summary>
public static class Formatting
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string HumanSize(long bytes)
    {
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{(long)v} {Units[u]}" : $"{v:0.#} {Units[u]}";
    }

    public static string ShortDate(DateTimeOffset dt)
    {
        var today = DateTime.Today;
        var d = dt.Date;
        if (d == today) return dt.ToString("HH:mm");
        if (d.Year == today.Year) return d.ToString("M月d日");
        return d.ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// "修改时间"列用的绝对时间戳。<b>不用 <see cref="ShortDate"/> 那种相对写法</b>：
    /// 列里要对齐比较（同一年显示 "9月16日"、跨年显示 "2025-12-31" 会让同一列参差不齐），
    /// 排序时人也看不出到底谁更新。
    /// </summary>
    public static string FullStamp(DateTimeOffset dt) => dt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
}
