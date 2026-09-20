namespace UniSearch.Sdk.Model;

/// <summary>
/// 实体融合依赖的规范化规则。<b>必须</b>与 tests/ 中的用例保持一致：
/// 跨 Provider 合并（Everything / AnyTXT / Zotero 附件）全部建立在它的输出上。
/// </summary>
/// <remarks>
/// 约定：本仓库源码中所有反斜杠一律写成 <c>\u005C</c> Unicode 转义，不使用逐字字符串。
/// 原因见 docs/16-ENV-PITFALLS.md —— 部分管道会把连续反斜杠折叠成单个，逐字串里的 <c>\\</c> 会被吃掉导致语法错误。
/// </remarks>
public static class Canonicalization
{
    /// <summary>目录分隔符。</summary>
    public const char Sep = '\u005C';

    static readonly string DoubleSep = "\u005C\u005C";
    static readonly string LongPathPrefix = "\u005C\u005C?\u005C";      // \\?\
    static readonly string LongPathUncPrefix = "\u005C??\u005C";        // \??\
    static readonly string DevicePrefix = "\u005C\u005C.\u005C";        // \\.\
    static readonly string MachinePrefix = "\u005CMachine\u005C";       // \Machine\

    /// <summary>去掉 <c>\\?\</c> / <c>\??\</c> 等前缀，统一分隔符，去掉结尾分隔符（盘根除外）。</summary>
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var p = path.Trim();

        if (p.StartsWith(LongPathPrefix, StringComparison.Ordinal)) p = p[4..];
        else if (p.StartsWith(LongPathUncPrefix, StringComparison.Ordinal)) p = p[4..];
        else if (p.StartsWith(MachinePrefix, StringComparison.Ordinal)) p = Sep + p;

        p = p.Replace('/', Sep);

        if (p.StartsWith(DevicePrefix, StringComparison.Ordinal)) return p;
        if (p.StartsWith(DoubleSep, StringComparison.Ordinal)) return TrimEnd(p);

        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
        {
            p = char.ToUpperInvariant(p[0]) + p[1..];
            if (p.Length == 2) return p + Sep;          // "C:"  -> "C:\"
            return TrimEnd(p);
        }

        // 相对路径无法参与融合：原样返回（仅去尾分隔符）
        return TrimEnd(p);

        static string TrimEnd(string s) => s.Length <= 3 ? s : s.TrimEnd(Sep);
    }

    /// <summary>大小写不敏感的完整路径键。<c>D:\A\B.TXT</c> 与 <c>d:\a\b.txt</c> 得到同一个键。</summary>
    public static string ToPathKey(string path) => NormalizePath(path);

    /// <summary>目录键：无尾部分隔符（盘根除外）。用于 <c>parent:</c> 约束与目录内命中判定。</summary>
    public static string ToDirectoryKey(string dir)
    {
        var p = NormalizePath(dir);
        if (IsDriveRoot(p)) return p;                                  // "C:\" 保留
        if (p.Length > 3 && p.EndsWith(Sep)) p = p.TrimEnd(Sep);
        return p;
    }

    /// <summary>小写、不含点的扩展名；无扩展名返回 null。</summary>
    public static string? GetExtension(string path)
    {
        var name = GetFileName(path);
        var i = name.LastIndexOf('.');
        if (i <= 0 || i == name.Length - 1) return null;
        return name[(i + 1)..].ToLowerInvariant();
    }

    public static string GetFileName(string path)
    {
        var p = NormalizePath(path);
        var i = p.LastIndexOf(Sep);
        return i < 0 ? p : p[(i + 1)..];
    }

    /// <summary>所在目录。约定：返回值的形态与 <see cref="ToDirectoryKey"/> 一致（盘根带尾分隔符）。</summary>
    public static string GetParentDirectory(string path)
    {
        var p = NormalizePath(path);
        var i = p.LastIndexOf(Sep);
        if (i < 0) return string.Empty;
        var dir = p[..i];
        if (dir.Length == 2 && dir[1] == ':') return dir + Sep;        // "C:\x" → "C:\"
        if (dir.Length == 0) return DoubleSep;                          // "\x" 根
        return dir;
    }

    /// <summary>给 UI 显示的目录串：盘根与 UNC 根都保留结尾分隔符，其余不带。</summary>
    public static string GetDirectoryDisplayName(string path)
    {
        var dir = GetParentDirectory(path);
        return dir.Length == 0 ? path : dir;
    }

    /// <summary><paramref name="candidate"/> 是否位于目录 <paramref name="directory"/> 内（含目录自身）。</summary>
    public static bool IsWithinDirectory(string candidate, string directory)
    {
        var c = NormalizePath(candidate);
        var d = ToDirectoryKey(directory);
        if (c.Length < d.Length) return false;
        if (!string.Equals(c[..d.Length], d, StringComparison.OrdinalIgnoreCase)) return false;
        if (c.Length == d.Length) return true;
        return c[d.Length] == Sep || IsDriveRoot(d);
    }

    static bool IsDriveRoot(string p) => p.Length == 3 && p[1] == ':' && p[2] == Sep;

    /// <summary>
    /// 实体融合键。第一版规则 = “同路径即同实体”：
    /// 1) 有本地路径 → <c>p:&lt;normalized&gt;</c>，跨 Provider 可合并；
    /// 2) 否则有 URI → <c>u:&lt;uri&gt;</c>；
    /// 3) 否则退化为 <c>x:provider:id</c>，永不与其他 Provider 合并。
    /// 未来加入 DOI / ISBN / Zotero key 时在此追加命名空间并建立二级索引（docs/06-RESULT-FUSION.md）。
    /// </summary>
    public static string ToFusionKey(string providerId, string itemId, string? path, string? uri)
    {
        if (!string.IsNullOrWhiteSpace(path)) return "p:" + NormalizePath(path).ToUpperInvariant();  // Windows 路径大小写不敏感：键必须归一，否则同一文件的两条命中不会合并
        if (!string.IsNullOrWhiteSpace(uri)) return "u:" + uri.Trim();
        return "x:" + providerId + ":" + itemId;
    }
}
