using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Model;

namespace UniSearch.Providers.Anytxt;

/// <summary>
/// 一次 <c>anytxt.v1.getResult</c> / <c>anytxt.v1.search</c> 的入参。
/// 字段名与官方 API 一一对应，故意用 <c>init</c> 记录以便单测直接比对。
/// </summary>
public sealed record AnytxtRequest
{
    /// <summary>查询表达式。AnyTXT 自己那套语法（<c>!</c> 排除、<c>"短语"</c>）。</summary>
    public required string Pattern { get; init; }

    /// <summary>
    /// 目录限定。<b>必须显式给盘符</b> —— 传空串会被服务端强制成 <c>C:</c>（实测回显证实），
    /// 所以"全盘搜索"只能靠调用方逐个盘符枚举，绝不能传空。
    /// </summary>
    public required string FilterDir { get; init; }

    /// <summary>扩展名过滤，形如 <c>*.pdf;*.docx</c>；不限为 <c>*</c>。</summary>
    public string FilterExt { get; init; } = "*";

    /// <summary>修改时间下界（Unix 秒）。0 = 不限。</summary>
    public long LastModifyBegin { get; init; }

    /// <summary>修改时间上界（Unix 秒）。<b>0 = 不设上界</b>（文档明说）。</summary>
    public long LastModifyEnd { get; init; }

    public int Limit { get; init; } = AnytxtQueryTranslator.MaxLimit;

    public int Offset { get; init; }

    /// <summary>0 默认 / 1 改升 / 2 改降 / 3 路径升 / 4 路径降（实测）。</summary>
    public int Order { get; init; }

    /// <summary>转成 API 的 params 对象（字段名照官方文档，别改大小写）。</summary>
    public Dictionary<string, object?> ToParameters() => new()
    {
        ["pattern"] = Pattern,
        ["filterDir"] = FilterDir,
        ["filterExt"] = FilterExt,
        ["lastModifyBegin"] = LastModifyBegin,
        ["lastModifyEnd"] = LastModifyEnd,
        ["limit"] = Limit,
        ["offset"] = Offset,
        ["order"] = Order,
    };

    /// <summary>只要总数时用的 params（<c>anytxt.v1.search</c> 不接受 limit/offset/order）。</summary>
    public Dictionary<string, object?> ToCountParameters() => new()
    {
        ["pattern"] = Pattern,
        ["filterDir"] = FilterDir,
        ["filterExt"] = FilterExt,
        ["lastModifyBegin"] = LastModifyBegin,
        ["lastModifyEnd"] = LastModifyEnd,
    };
}

/// <summary>
/// 翻译结果。<see cref="Notes"/> 是"如实降级"的说明 —— 宿主会把它放进
/// <c>ProviderOutcome.Detail</c>，让用户看得见"哪些条件没下推到后端"。
/// </summary>
public sealed record AnytxtTranslation(AnytxtRequest? Request, IReadOnlyList<string> Notes)
{
    public static AnytxtTranslation Empty(params string[] notes) => new(null, notes);

    public bool IsEmpty => Request is null;
}

/// <summary>
/// 把统一查询翻译成 AnyTXT 的入参。<b>纯函数</b>，不碰网络 —— 这样下推规则可以逐条单测，
/// 不用起服务（对齐 <c>EverythingQueryTranslator</c> 的做法）。
/// <para>
/// 与 Everything 最大的不同：<b>AnyTXT 没有可比拟的高级语法</b>，所以结构化查询在这里
/// 不做透传（透传 Everything 的函数名给 AnyTXT 只会搜不到东西），而是退化成纯文本 + 一条说明。
/// </para>
/// </summary>
public static class AnytxtQueryTranslator
{
    /// <summary>官方文档：<c>limit</c> 必须在 1–300 之间，省略为 300。</summary>
    public const int MaxLimit = 300;

    /// <summary>一次请求最多拉多少行（超过要翻页；分页由 Provider 负责）。</summary>
    public const int DefaultLimit = 200;

    /// <summary>
    /// 翻译。<paramref name="filterDir"/> 由调用方给（限定搜索给目录，全盘搜索给某个盘符）——
    /// 翻译器<b>不</b>自己决定"搜哪些盘"，那是 Provider 的调度职责。
    /// </summary>
    public static AnytxtTranslation Translate(SearchQuery query, string filterDir, int limit = DefaultLimit)
    {
        var notes = new List<string>();

        var pattern = BuildPattern(query, notes);
        if (pattern.Length == 0) return AnytxtTranslation.Empty();

        var request = new AnytxtRequest
        {
            Pattern = pattern,
            FilterDir = filterDir,
            FilterExt = BuildFilterExt(query.Filters.Extensions),
            LastModifyBegin = ToUnixSeconds(query.Filters.ModifiedAfter),
            LastModifyEnd = 0,                      // 0 = 不设上界；查询语法目前没有上界
            Limit = Math.Clamp(limit, 1, MaxLimit),
            Offset = 0,
            Order = 0,                              // 排序由 Core 统一做，后端不参与
        };

        NoteUnsupportedFilters(query, notes);
        return new AnytxtTranslation(request, notes);
    }

    /// <summary>
    /// 查询表达式。
    /// <list type="bullet">
    /// <item>用户用引号锁了短语 → 直接用（AnyTXT 的引号是<b>词序敏感的真短语</b>）；</item>
    /// <item>结构化查询 → <b>不透传</b>，退化成纯文本并记一条说明；</item>
    /// <item>其余 → 自由文本原样。</item>
    /// </list>
    /// </summary>
    internal static string BuildPattern(SearchQuery query, List<string> notes)
    {
        if (!string.IsNullOrWhiteSpace(query.Filters.Phrase))
            return Quote(query.Filters.Phrase!);

        if (query.IsStructured)
        {
            // Everything 的函数名（ancestor:、attrib:…）对 AnyTXT 毫无意义，原样送过去只会 0 条。
            notes.Add("该后端不支持高级查询语法，已按纯文本搜索");
            return query.Text.Trim();
        }

        return query.Text.Trim();
    }

    /// <summary>
    /// 扩展名过滤。空 = 不限（<c>*</c>）。
    /// 用 <c>*.ext</c> 这种带星号的写法：官方 JSON-RPC 示例是 <c>doc;pdf</c>、MCP schema 是
    /// <c>*.cpp;*.h</c>，<b>实测两种都生效</b>，选后者是因为不会被误读成文件名。
    /// </summary>
    internal static string BuildFilterExt(IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0) return "*";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>(extensions.Count);
        foreach (var raw in extensions)
        {
            var e = (raw ?? string.Empty).Trim().TrimStart('.').TrimStart('*').TrimStart('.');
            if (e.Length == 0 || !seen.Add(e)) continue;
            parts.Add("*." + e.ToLowerInvariant());
        }
        return parts.Count == 0 ? "*" : string.Join(';', parts);
    }

    /// <summary>把 <see cref="DateTimeOffset"/> 换成 Unix 秒；null 或越界 → 0（= 不限）。</summary>
    internal static long ToUnixSeconds(DateTimeOffset? when)
    {
        if (when is not { } t) return 0;
        var s = t.ToUnixTimeSeconds();
        return s < 0 ? 0 : s;
    }

    /// <summary>把"下推不了"的条件逐条说清楚 —— 静默丢弃会让用户以为筛选生效了。</summary>
    internal static void NoteUnsupportedFilters(SearchQuery query, List<string> notes)
    {
        var f = query.Filters;

        if (f.RegexRequested || !string.IsNullOrWhiteSpace(f.RegexPattern))
            notes.Add("AnyTXT 不支持正则（实测 [] 与 . 都不生效），已按纯文本搜索");

        if (f.MinSizeBytes is not null || f.MaxSizeBytes is not null)
            notes.Add("AnyTXT 不支持按大小过滤，已在前端过滤");

        if (f.FoldersOnly)
            notes.Add("AnyTXT 只索引文件，没有文件夹结果");

        if (f.Kinds.Count > 0 && f.Kinds.All(k => k is not (ResultKind.File or ResultKind.Document
                                                          or ResultKind.Image or ResultKind.Video
                                                          or ResultKind.Audio or ResultKind.Application)))
            notes.Add("该后端只产出文件类结果，类型条件已在前端过滤");
    }

    /// <summary>短语定界：值里已有的引号去掉，避免 <c>""x""</c> 这种解析歧义。</summary>
    internal static string Quote(string phrase) => "\"" + phrase.Replace("\"", string.Empty).Trim() + "\"";
}
