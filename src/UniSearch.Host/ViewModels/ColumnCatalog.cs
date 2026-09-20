using CommunityToolkit.Mvvm.ComponentModel;

namespace UniSearch.Host.ViewModels;

/// <summary>一列的静态定义。<b>Key 会落盘</b>（settings.json 的 <c>columns.visible</c> / <c>widths</c>），改名等于改配置格式。</summary>
public sealed record ColumnSpec(string Key, string Header, double DefaultWidth, bool RightAlign = false, string? Tooltip = null);

/// <summary>
/// 结果表的列目录。列头文字、默认宽度、对齐方式只在这里定义一次 ——
/// code-behind 用它建 <c>GridViewColumn</c>，设置窗口用它显示"当前可见列"。
/// </summary>
public static class ColumnCatalog
{
    public const string Name = "name";
    public const string Path = "path";
    public const string Extension = "extension";
    public const string Size = "size";
    public const string Modified = "modified";
    public const string Kind = "kind";
    public const string Provider = "provider";

    /// <summary>全部可选列，<b>顺序即"恢复默认"后的顺序</b>。</summary>
    public static readonly ColumnSpec[] All =
    [
        // 名称列是"填充列"：窗口变宽时它吃掉剩余空间（见 MainWindow.AutoFitFillerColumn），
        // 所以默认宽度给得保守 —— 反正它会自己长大，给大了会把后面的列挤到视口外。
        new(Name, "名称", 180, Tooltip: "文件名（命中处高亮）。窗口变宽时它自动吃掉剩余空间"),
        new(Path, "路径", 230, Tooltip: "完整路径；没有路径的结果显示其 URI"),
        new(Extension, "扩展名", 70, Tooltip: "小写扩展名，不含点"),
        new(Size, "大小", 72, RightAlign: true, Tooltip: "文件夹没有大小，排序时集中在末端"),
        new(Modified, "修改时间", 136, Tooltip: "多后端证实时取最新的那个"),
        new(Kind, "类型", 68, Tooltip: "分类：文档 / 图片 / 文件夹…"),
        new(Provider, "来源", 80, Tooltip: "这条结果由哪个后端产出"),
    ];

    /// <summary>
    /// 默认可见列（首次运行、或用户点了"恢复默认列"）。
    /// 只放默认宽度加起来能塞进默认窗口的那几列 —— 再多就得靠水平滚动条，
    /// 而"一打开就看不到大小/时间"正是这轮要修掉的问题（Everything 的默认列也是这几列）。
    /// </summary>
    public static readonly string[] DefaultVisible =
        [Name, Path, Size, Modified, Kind];

    public static ColumnSpec? Find(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var c in All)
            if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }
}

/// <summary>
/// 一列的运行时状态（宽度、可见性、是否当前排序列）。
/// <para>
/// <see cref="Width"/> 是双向的：拖列头改的是 <c>GridViewColumn.Width</c>，值再回写到这里的
/// <see cref="Width"/> 并落盘。反过来设置里改了宽度，也要能推回 GridViewColumn。
/// </para>
/// </summary>
public sealed partial class ResultColumnViewModel : ObservableObject
{
    public ResultColumnViewModel(ColumnSpec spec, double width, bool isVisible)
    {
        Spec = spec;
        _width = width;
        _isVisible = isVisible;
    }

    public ColumnSpec Spec { get; }
    public string Key => Spec.Key;
    public string Header => Spec.Header;
    public string? Tooltip => Spec.Tooltip;
    public bool RightAlign => Spec.RightAlign;
    public double DefaultWidth => Spec.DefaultWidth;

    /// <summary>列宽（像素）。由列头拖拽或设置推入。</summary>
    [ObservableProperty]
    double _width;

    [ObservableProperty]
    bool _isVisible;

    /// <summary>排序方向指示（"▲" / "▼" / 空）。</summary>
    [ObservableProperty]
    string _sortGlyph = string.Empty;

    /// <summary>是否正在按这一列排序（列头文字加粗用）。</summary>
    [ObservableProperty]
    bool _isSorted;

    /// <summary>列头的对齐方式（"大小"这类数字列右对齐，和单元格保持一致）。</summary>
    public System.Windows.HorizontalAlignment HeaderAlign => RightAlign
        ? System.Windows.HorizontalAlignment.Right
        : System.Windows.HorizontalAlignment.Left;

    public string WidthText => $"{Math.Round(Width)} px";
}
