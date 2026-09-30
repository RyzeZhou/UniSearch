using System.Globalization;
using System.Text.Json;
using UniSearch.Sdk.Model;

namespace UniSearch.Providers.Siyuan;

/// <summary>
/// 思源 SQL 的行 → 统一结果模型。<b>纯函数</b>，喂真实响应样例就能断言字段映射对不对。
/// <para>
/// <b>块没有标题</b>：思源的一行是"一个内容块"，可能是段落、标题、代码块、列表项。
/// 所以 <see cref="SearchResult.Title"/> 只能拿 <c>content</c> 当标题用（文档块例外，
/// 它的 content 真的是标题）—— 截断到一行，完整内容进 <see cref="SearchResult.Snippet"/>。
/// </para>
/// <para>
/// <b>不填 <see cref="SearchResult.Path"/></b>：块对应的是内核库里的 <c>.sy</c> JSON 文件，
/// 只有在<b>与内核同机</b>且配了 workspace 时才拼得出真实路径。跨机（本项目的实际部署）
/// 填了只会让"打开/预览"指向一个不存在的文件。走 <see cref="SearchResult.Uri"/> 的
/// <c>siyuan://blocks/&lt;id&gt;</c> 分支。
/// </para>
/// </summary>
public static class SiYuanItemMapper
{
    /// <summary>标题与摘要各自的最大长度（字符）。块内容可以很长，界面上放不下。</summary>
    internal const int TitleMax = 90;
    internal const int SnippetMax = 240;

    /// <summary>把 <c>data</c> 数组映射成结果行。<b>不抛异常</b>：坏行跳过，不拖垮整批。</summary>
    public static List<SearchResult> MapArray(JsonElement data, int? totalAvailable,
                                              IReadOnlyDictionary<string, string>? notebookNames = null)
    {
        var list = new List<SearchResult>();
        if (data.ValueKind != JsonValueKind.Array) return list;

        foreach (var row in data.EnumerateArray())
        {
            var mapped = Map(row, totalAvailable, notebookNames);
            if (mapped is not null) list.Add(mapped);
        }
        return list;
    }

    /// <summary>映射单行。返回 null = 没有可用的块 id，跳过。</summary>
    public static SearchResult? Map(JsonElement row, int? totalAvailable,
                                    IReadOnlyDictionary<string, string>? notebookNames = null)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        if (Str(row, "id") is not { Length: > 0 } id) return null;

        var type = Str(row, "type") ?? "";
        var subtype = Str(row, "subtype");
        var content = Str(row, "content") ?? "";
        var fcontent = Str(row, "fcontent") ?? "";
        var hpath = Str(row, "hpath") ?? "";
        var box = Str(row, "box") ?? "";
        var title = Str(row, "name") is { Length: > 0 } name ? name : "";

        var isDoc = type == "d";
        var isCode = type == "c";

        // 文档块的 content 就是它的标题；别的块只能把内容首行当标题。
        // fcontent 是纯文本版（富文本块可能有它），空的时候退回 content。
        var body = content.Length > 0 ? content : fcontent;
        if (title.Length == 0) title = FirstLine(body, TitleMax);
        if (title.Length == 0) title = hpath.Length > 0 ? LastSegment(hpath) : id;

        var snippet = body.Length > 0 && body != title ? Truncate(body, SnippetMax) : null;

        var notebook = box.Length > 0 && notebookNames is not null && notebookNames.TryGetValue(box, out var nb)
            ? nb : null;

        return new SearchResult
        {
            ProviderId = SiYuanProvider.ProviderId,
            ProviderItemId = id,
            Kind = KindOf(type),
            Subtype = subtype is { Length: > 0 } ? subtype.ToLowerInvariant() : null,
            // 文档块命中的是"标题"，其他块命中的是正文 —— 这个区别会一路影响排序（Ranker 按 Match 加分）
            Match = isDoc ? MatchKind.NameWord : MatchKind.Content,
            Title = title,
            Subtitle = BuildSubtitle(notebook, hpath, type, subtype),
            Path = null,                      // 见类注释：跨机时真实路径不存在
            Uri = BlockUri(id),
            Snippet = snippet,
            SizeBytes = null,
            ModifiedAt = ParseStamp(Str(row, "updated")),
            CreatedAt = ParseStamp(Str(row, "created")),
            Metadata = BuildMetadata(row, type, subtype, notebook),
            Icon = new IconHint
            {
                Glyph = isDoc ? "\uE8A5" : isCode ? "\uE943" : "\uE70B",   // 文档 / 代码 / 笔记
                IconByExtensionOnly = true,
            },
            Tags = BuildTags(row, notebook, isCode),
            ReadOnly = true,
            // 没有本地文件，但预览不缺席：宿主对无路径结果会问 IPreviewProvider，
            // 本 Provider 用 exportMdContent 把块导成 Markdown 给预览面板（跨机可用）
            TotalAvailable = totalAvailable,
            CopyText = body.Length > 0 ? body : BlockUri(id),
        };
    }

    /// <summary>在思源里定位到这个块。<c>siyuan://blocks/&lt;id&gt;</c> 是思源自己注册的协议。</summary>
    public static string BlockUri(string id) => $"siyuan://blocks/{id}";

    /// <summary>
    /// 思源的块类型 → <see cref="ResultKind"/>。二十来种 type 只归三档
    /// （与 <c>SiYuanQueryTranslator.MapKinds</c> 的下推口径<b>必须一致</b>，
    /// 否则会出现"点分类得到空结果"—— 筛选器说能筛、结果却归不到那一类）。
    /// </summary>
    public static ResultKind KindOf(string type) => type switch
    {
        "d" => ResultKind.Document,
        "c" => ResultKind.CodeSymbol,
        _ => ResultKind.Note,
    };

    /// <summary>
    /// 人类可读的中文类型名（Subtitle 用）。<b>只收录实测出现过的</b>
    /// （5275 块里 type 的完整分布见 SIYUAN-API-VERIFIED.md §2）。
    /// </summary>
    internal static string TypeName(string type, string? subtype) => type switch
    {
        "d" => "文档",
        "h" => "标题",
        "p" => "段落",
        "l" => "列表",
        "i" => "列表项",
        "c" => "代码块",
        "t" => "表格",
        "b" => "引述",
        "s" => "超级块",
        "m" => "公式",
        "tb" => "分隔线",
        "av" => "属性视图",
        "query_embed" => "嵌入查询",
        "html" => "HTML 块",
        "audio" => "音频",
        "video" => "视频",
        "iframe" => "嵌入网页",
        _ => subtype is { Length: > 0 } ? subtype : "块",
    };

    static string? BuildSubtitle(string? notebook, string hpath, string type, string? subtype)
    {
        var kind = TypeName(type, subtype);
        var where = notebook is { Length: > 0 } ? notebook : null;
        if (hpath.Length > 0)
        {
            var tail = LastSegment(hpath);
            if (tail.Length > 0 && tail != where) where = where is null ? tail : $"{where} · {tail}";
        }
        return where is null ? kind : $"{kind} · {where}";
    }

    static IReadOnlyDictionary<string, string> BuildMetadata(JsonElement row, string type, string? subtype,
                                                             string? notebook)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "思源笔记",
            ["blockType"] = type,
        };
        if (subtype is { Length: > 0 }) meta["blockSubtype"] = subtype;
        if (notebook is { Length: > 0 }) meta["notebook"] = notebook;
        // 笔记本的原始 id：界面上用名字，但要拿它去限定作用域 / 下推值域时得用 id
        if (Str(row, "box") is { Length: > 0 } box) meta["boxId"] = box;
        if (Str(row, "hpath") is { Length: > 0 } hp) meta["hpath"] = hp;
        if (Str(row, "root_id") is { Length: > 0 } rid) meta["rootId"] = rid;
        if (Str(row, "alias") is { Length: > 0 } alias) meta["alias"] = alias;
        if (Str(row, "memo") is { Length: > 0 } memo) meta["memo"] = memo;
        return meta;
    }

    /// <summary>
    /// 行上的小标签。标签字段是 <c>#标签#</c> 或 <c>#甲# #乙#</c>（实测），要把井号剥掉 ——
    /// 界面上再显示一层 <c>#</c> 是噪声。代码块另给一枚徽标（它是唯一能一眼认出的块类型）。
    /// </summary>
    static IReadOnlyList<ResultTag> BuildTags(JsonElement row, string? notebook, bool isCode)
    {
        var tags = new List<ResultTag> { new("思源", ResultTagTone.Accent) };
        if (isCode) tags.Add(new("代码", ResultTagTone.Neutral));

        var raw = Str(row, "tag");
        if (raw is { Length: > 0 })
        {
            foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim('#', ' ').Trim();
                if (t.Length > 0) tags.Add(new ResultTag(t, ResultTagTone.Neutral));
                if (tags.Count >= 5) break;
            }
        }
        return tags;
    }

    /// <summary>
    /// <c>YYYYMMDDHHMMSS</c> → <see cref="DateTimeOffset"/>。<b>空串是常态</b>
    /// （实测 5275 块里 413 块的 <c>updated</c> 是空的）—— 那不是错误，是"这个块没有该字段"。
    /// </summary>
    internal static DateTimeOffset? ParseStamp(string? value)
    {
        if (value is not { Length: 14 }) return null;
        if (!DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var dt)) return null;
        return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
    }

    internal static string FirstLine(string text, int max)
    {
        if (text.Length == 0) return "";
        var idx = text.IndexOfAny(['\n', '\r']);
        var line = idx >= 0 ? text[..idx] : text;
        return Truncate(line.Trim(), max);
    }

    internal static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd() + "…";

    internal static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
