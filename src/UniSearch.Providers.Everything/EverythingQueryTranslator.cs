using System.Text;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Model;

namespace UniSearch.Providers.Everything;

/// <summary>
/// 把 UniSearch 的语义翻译成 Everything 的<b>搜索函数串</b>。
/// 每条语法都对照 docs/research/EVERYTHING-IPC-VERIFIED.md 表 F 核实过；
/// 分类用的 <c>ext:</c> 列表取自 EverythingToolbar 内置筛选器（与 Everything 自身语义一致，不自造）。
/// 纯函数、可脱机单测 —— 本机没装 Everything 也能验证正确性。
/// </summary>
public static class EverythingQueryTranslator
{
    const char Sep = '\u005C';

    // 来源：refs/everythingtoolbar/EverythingToolbar.App/Services/DefaultFilterProvider.cs
    public static readonly string[] Picture = ["ani", "bmp", "gif", "ico", "jpe", "jpeg", "jpg", "pcx", "png", "psd", "tga", "tif", "tiff", "webp", "wmf", "heic", "svg"];
    public static readonly string[] Document = ["doc", "docx", "odt", "pdf", "rtf", "txt", "md", "xls", "xlsx", "ppt", "pptx", "epub", "mobi", "caj", "ofd", "tex"];
    public static readonly string[] Audio = ["aac", "ape", "flac", "m4a", "mid", "midi", "mp3", "ogg", "opus", "wav", "wma"];
    // 不放 "ts"：它既是 MPEG-TS 视频又是 TypeScript，而 Kind 判定时 Video 先于 Code，
    // 实测导致 pdf-body.client.spec.ts 这类文件整批掉进「视频」组。要视频可显式 ext:ts / kind:video。
    public static readonly string[] Video = ["avi", "flv", "m4v", "mkv", "mov", "mp4", "mpeg", "mpg", "rmvb", "webm", "wmv"];
    public static readonly string[] Archive = ["7z", "bz2", "cab", "gz", "iso", "rar", "tar", "wim", "xz", "zip"];
    public static readonly string[] Executable = ["appx", "bat", "cmd", "exe", "lnk", "msi", "msp", "msix", "scr"];
    public static readonly string[] Code = ["c", "cc", "cs", "css", "go", "h", "hpp", "html", "java", "js", "json", "jsx", "kt", "py", "rb", "rs", "sh", "sql", "swift", "toml", "ts", "tsx", "xml", "yaml", "yml", "ps1"];

    /// <summary>
    /// 系统噪声位置：这些地方的命中对用户几乎从不是想要的。
    /// 回收站是原文件的重复副本；WinSxS / servicing 是组件仓库；Windows.old 是上次安装的遗留。
    /// <para>
    /// 已在这台 Everything 1.4.1 上实测（tools/Probe --raw）：
    /// <c>pdf</c> 331 条 → 排除回收站 <c>!path:"$RECYCLE.BIN"</c> 312 → 再排除 <c>!path:"C:\Windows\WinSxS"</c> 275。
    /// </para>
    /// <para>
    /// <b>取反必须用 <c>!</c>，不能用 <c>-</c></b>：实测 <c>pdf -fixture</c> 返回 2 条名字里含
    /// <c>-fixture</c> 的文件（如 <c>pdf-fixture.ts</c>），说明 <c>-xxx</c> 被当成含连字符的字面文本，
    /// 而不是取反。写错会静默把结果打到 0，非常难查。
    /// </para>
    /// </summary>
    static readonly string[] NoisePaths =
    [
        "$RECYCLE.BIN",
        "System Volume Information",
        @"C:\Windows\WinSxS",
        @"C:\Windows\servicing",
        @"C:\Windows.old",
    ];

    /// <summary>
    /// 噪声排除子句，形如 <c>!path:"…" </c>（自带尾空格，可直接拼在查询尾部）。
    /// 用户明确在某个噪声目录里搜索时跳过对应项 —— 否则 Ctrl+F 打开回收站会一片空白。
    /// </summary>
    /// <param name="options">来自用户设置；为 null 时按历史默认行为（排除内置噪声、无额外排除）。</param>
    public static string BuildNoiseExclusions(SearchContext ctx, EverythingQueryOptions? options = null)
    {
        var root = ctx.IsDirectoryBounded ? ctx.RootPath : null;
        var clause = string.Empty;

        var excludes = new List<string>();
        if (options?.ExcludeNoisePaths ?? true) excludes.AddRange(NoisePaths);
        if (options?.ExtraExcludePaths is { Count: > 0 } extra) excludes.AddRange(extra);

        foreach (var n in excludes)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            // 用 Contains 而不是 StartsWith：噪声项既可能是子串（$RECYCLE.BIN 在每个卷上都有），
            // 也可能是绝对前缀（C:\Windows\WinSxS）。用户从任意层级进入这些位置时都不能再排除它。
            if (root is not null && root.Contains(n, StringComparison.OrdinalIgnoreCase)) continue;
            clause += "!path:\"" + n.TrimEnd(Sep) + "\" ";
        }
        return clause;
    }

    /// <summary>
    /// 生成交给 Everything 的查询串（不含 search_flags / sort_type，那些走请求结构体的字段）。
    /// </summary>
    public static string Translate(SearchQuery q, SearchContext ctx, EverythingQueryOptions? options = null)
    {
        var scope = BuildScopePrefix(ctx);
        var noise = BuildNoiseExclusions(ctx, options);
        var body = q.ProviderText;

        if (string.IsNullOrWhiteSpace(body))
        {
            // 空查询：在目录里就列举本层；否则不给 Everything 发查询（那会匹配整个索引）
            if (q.ListScopeContents && ctx.IsDirectoryBounded) return scope + "file: " + noise.TrimEnd();
            return string.Empty;
        }
        return (scope.Length > 0 ? scope + body : body) + " " + noise.TrimEnd();
    }

    /// <summary>
    /// Explorer Ctrl+F 的目录限定：仅本层用 <c>parent:</c>、含子目录用 <c>ancestor:</c>（均为已核实的真函数）。
    /// 返回串自带结尾空格，可直接前缀拼接。
    /// </summary>
    public static string BuildScopePrefix(SearchContext ctx)
    {
        if (!ctx.IsDirectoryBounded || ctx.RootPath is not { } root) return string.Empty;
        var func = ctx.Scope == ScopeKind.CurrentDirectoryOnly ? "parent:" : "ancestor:";
        return func + QuotePath(root) + " ";
    }

    /// <summary>正则请求由 UI 侧显式开启；<c>size:</c> 之类函数一旦存在就不能再走正则通道。</summary>
    public static EverythingSearchFlags BuildFlags(SearchQuery q)
    {
        var f = EverythingSearchFlags.None;
        if (q.Filters.RegexRequested) f |= EverythingSearchFlags.RegEx;
        if (q.MatchTarget.Length <= 2) f |= EverythingSearchFlags.MatchWholeWord;
        return f;
    }

    /// <summary>把结果类型映射到扩展名表（不用 1.5 专有的 <c>kind:</c>，保证 1.4 行为一致）。</summary>
    public static IReadOnlyList<string> MapKindToExtensions(ResultKind k) => k switch
    {
        ResultKind.Image => Picture,
        ResultKind.Document => Document,
        ResultKind.Audio => Audio,
        ResultKind.Video => Video,
        ResultKind.ArchiveEntry => Archive,
        ResultKind.Application => Executable,
        ResultKind.CodeSymbol => Code,
        ResultKind.Folder => [],
        _ => [],
    };

    /// <summary>
    /// 路径必须整体加引号（不加会被拆成 AND 词项），并保留结尾 <c>\</c> 表示“在该目录内”。
    /// 例：<c>D:\Research\AI</c> → <c>"D:\Research\AI\"</c>
    /// </summary>
    public static string QuotePath(string path)
    {
        var p = path.TrimEnd(Sep);
        return "\"" + p.Replace("\"", "\\\"") + Sep + "\"";
    }

    public static string QuoteLiteral(string s) =>
        s.Contains(' ') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;

    public static string ToHumanSize(long bytes)
    {
        string[] units = ["b", "kb", "mb", "gb", "tb"];
        var v = (double)bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return (i == 0 ? ((long)v).ToString() : v.ToString("0.#")) + units[i];
    }
}

/// <summary>
/// Everything 侧的可调项（来自用户设置里的 <c>search.*</c>）。
/// 默认值 = 这一路走来的历史行为，所以"没配过"和"配成默认"结果一致。
/// </summary>
public sealed record EverythingQueryOptions
{
    /// <summary>排除回收站 / WinSxS / servicing / Windows.old 这些系统噪声位置。</summary>
    public bool ExcludeNoisePaths { get; init; } = true;

    /// <summary>用户自己加的排除目录。</summary>
    public IReadOnlyList<string> ExtraExcludePaths { get; init; } = [];

    public static readonly EverythingQueryOptions Default = new();
}

/// <summary>镜像第三方包的能力位，避免上层直接依赖它（换客户端实现时不动调用方）。</summary>
[Flags]
public enum EverythingSearchFlags
{
    None = 0,
    MatchCase = 1 << 0,
    MatchWholeWord = 1 << 1,
    MatchPath = 1 << 2,
    RegEx = 1 << 3,
}
