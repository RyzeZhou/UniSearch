using UniSearch.Core.Categories;
using UniSearch.Core.Fusion;

namespace UniSearch.Core.Sorting;

/// <summary>
/// 结果表的排序键。与列一一对应（<c>relevance</c> 例外：它对应"不点任何列头"的默认次序）。
/// 字符串名会落盘到 settings.json（<c>columns.sortKey</c>），<b>改名等于改配置格式</b>。
/// </summary>
public enum ResultSortKey
{
    /// <summary>相关度（Ranker 的分数），默认次序。</summary>
    Relevance,
    Name,
    Path,
    Extension,
    Size,
    Modified,
    Kind,
    Provider,
}

/// <summary>
/// 结果行排序。<b>放在 Core 而不是 Host</b>：它是纯函数、不碰 WPF，
/// 于是"按大小降序真的把最大的排最前""同名时次序稳定不抖动"这类事实可以被单元测试钉住，
/// 而不是靠人眼看列表。
/// </summary>
public static class ResultSort
{
    /// <summary>排序键的落盘名（与 <see cref="ResultSortKey"/> 的枚举名同源，但显式写死以免重命名破坏配置）。</summary>
    public static string ToConfigName(ResultSortKey key) => key switch
    {
        ResultSortKey.Relevance => "relevance",
        ResultSortKey.Name => "name",
        ResultSortKey.Path => "path",
        ResultSortKey.Extension => "extension",
        ResultSortKey.Size => "size",
        ResultSortKey.Modified => "modified",
        ResultSortKey.Kind => "kind",
        ResultSortKey.Provider => "provider",
        _ => "name",
    };

    public static bool TryParse(string? name, out ResultSortKey key)
    {
        switch ((name ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "relevance": key = ResultSortKey.Relevance; return true;
            case "name": key = ResultSortKey.Name; return true;
            case "path": key = ResultSortKey.Path; return true;
            case "extension": key = ResultSortKey.Extension; return true;
            case "size": key = ResultSortKey.Size; return true;
            case "modified": key = ResultSortKey.Modified; return true;
            case "kind": key = ResultSortKey.Kind; return true;
            case "provider": key = ResultSortKey.Provider; return true;
            default: key = ResultSortKey.Name; return false;
        }
    }

    /// <summary>
    /// 某个键的"首次点击"方向。文本类列默认升序（A→Z），
    /// 大小/时间/相关度默认降序 —— 点"大小"想看的是最大的那几个，不是 0 字节的那批。
    /// </summary>
    public static bool DefaultDescending(ResultSortKey key) =>
        key is ResultSortKey.Size or ResultSortKey.Modified or ResultSortKey.Relevance;

    /// <summary>
    /// 生成比较器。<b>末尾永远有名称 + 路径两级兜底键</b>：
    /// 没有它，同大小的文件在每次快照刷新时次序都可能不同（看起来像列表在乱跳）。
    /// </summary>
    public static Comparison<FusedResult> Comparison(ResultSortKey key, bool descending)
    {
        var sign = descending ? -1 : 1;
        return (a, b) =>
        {
            var c = ComparePrimary(key, a, b);
            if (c != 0) return sign * c;

            c = NaturalCompare(Title(a), Title(b));
            if (c != 0) return c;
            return string.CompareOrdinal(Path(a), Path(b));
        };
    }

    public static void Sort(List<FusedResult> items, ResultSortKey key, bool descending)
        => items.Sort(Comparison(key, descending));

    static int ComparePrimary(ResultSortKey key, FusedResult a, FusedResult b) => key switch
    {
        ResultSortKey.Relevance => a.Score.CompareTo(b.Score),
        ResultSortKey.Name => NaturalCompare(Title(a), Title(b)),
        ResultSortKey.Path => string.Compare(Path(a), Path(b), StringComparison.OrdinalIgnoreCase),
        ResultSortKey.Extension => string.Compare(Extension(a), Extension(b), StringComparison.OrdinalIgnoreCase),
        // 文件夹没有大小：给 -1 让它们稳定地聚在一端，而不是和 0 字节文件混在一起
        ResultSortKey.Size => SizeOf(a).CompareTo(SizeOf(b)),
        ResultSortKey.Modified => ModifiedOf(a).CompareTo(ModifiedOf(b)),
        ResultSortKey.Kind => KindOrder(a).CompareTo(KindOrder(b)),
        ResultSortKey.Provider => string.Compare(a.Display.ProviderId, b.Display.ProviderId, StringComparison.OrdinalIgnoreCase),
        _ => 0,
    };

    static string Title(FusedResult f) => f.Display.Title ?? string.Empty;

    static string Path(FusedResult f) => f.Display.Path ?? f.Display.Uri ?? string.Empty;

    static string Extension(FusedResult f) => f.Display.Extension ?? string.Empty;

    static long SizeOf(FusedResult f) => f.Display.IsFolder ? -1 : f.Display.SizeBytes ?? -1;

    static DateTimeOffset ModifiedOf(FusedResult f) => f.ModifiedAt ?? f.Display.ModifiedAt ?? DateTimeOffset.MinValue;

    static int KindOrder(FusedResult f) => CategoryEngine.OrderOf(f.CategoryId);

    /// <summary>
    /// 数字感知的字符串比较："文件2" 排在 "文件10" 前面。
    /// 纯 <c>Ordinal</c> 比较会得到相反的结果（'1' &lt; '2'），在文件名里这几乎总是错的。
    /// </summary>
    public static int NaturalCompare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var cx = x[i];
            var cy = y[j];

            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                // 跳过前导零后按数值长度比较，长度相同再逐位比 —— 避免解析成 long 时溢出
                var si = i; while (si < x.Length && x[si] == '0') si++;
                var sj = j; while (sj < y.Length && y[sj] == '0') sj++;
                var ei = si; while (ei < x.Length && char.IsDigit(x[ei])) ei++;
                var ej = sj; while (ej < y.Length && char.IsDigit(y[ej])) ej++;

                var lenX = ei - si;
                var lenY = ej - sj;
                if (lenX != lenY) return lenX - lenY;
                for (var k = 0; k < lenX; k++)
                {
                    var d = x[si + k] - y[sj + k];
                    if (d != 0) return d;
                }

                i = ei; j = ej;
                continue;
            }

            var ux = char.ToUpperInvariant(cx);
            var uy = char.ToUpperInvariant(cy);
            if (ux != uy) return ux - uy;
            i++; j++;
        }

        return (x.Length - i) - (y.Length - j);
    }
}
