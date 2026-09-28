using UniSearch.Sdk.Model;

namespace UniSearch.Providers.Zotero;

/// <summary>一次 Zotero 读取的入参（纯数据，便于单测逐字段比对）。</summary>
public sealed record ZoteroRequest
{
    /// <summary>对象路径，如 <c>/users/0/items</c> 或 <c>/users/0/collections/ABCD1234/items</c>。</summary>
    public required string Path { get; init; }

    /// <summary>查询参数。<b>顺序即拼接顺序</b>（同名参数多次出现是 AND 语义）。</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Query { get; init; }

    public string? Q => Find("q");
    public string? Qmode => Find("qmode");
    public string? ItemType => Find("itemType");
    public int? Limit => int.TryParse(Find("limit"), out var n) ? n : null;

    string? Find(string key)
    {
        foreach (var kv in Query)
            if (string.Equals(kv.Key, key, StringComparison.Ordinal)) return kv.Value;
        return null;
    }
}

/// <summary>
/// 把统一查询翻译成 Zotero 本地 API 的读取请求。<b>纯函数</b>，不碰网络。
/// <para>
/// <b>能下推的只有三样</b>（实测，见 ZOTERO-LOCAL-API-VERIFIED.md §3）：
/// <c>q=</c> 关键词、<c>itemType=</c> 类型、<c>collection/</c> 集合。
/// 扩展名 / 大小 / 修改时间范围 <b>API 根本不支持</b> —— 而且<b>未知参数会被静默忽略</b>，
/// 所以这里绝不能"顺手"塞一个 <c>date=</c> 进去假装在筛。
/// </para>
/// </summary>
public static class ZoteroQueryTranslator
{
    public const string UserPrefix = "/users/0";

    /// <summary>
    /// 翻译。<paramref name="limit"/> 必须显式给 —— 本地 API 省略 limit 会一次返回<b>全部</b>对象
    /// （库里几千条时会拖死首屏）。
    /// </summary>
    public static ZoteroTranslation Translate(SearchQuery query, int limit, string? collectionKey = null,
                                              ZoteroQueryOptions? options = null)
    {
        var opt = options ?? ZoteroQueryOptions.Default;
        var notes = new List<string>();
        var q = new List<KeyValuePair<string, string>>();

        var path = collectionKey is { Length: > 0 }
            ? $"{UserPrefix}/collections/{Uri.EscapeDataString(collectionKey)}/items"
            : $"{UserPrefix}/items";

        var text = BuildSearchText(query, notes);
        if (text.Length > 0)
        {
            q.Add(new("q", text));
            q.Add(new("qmode", opt.Qmode));
        }

        var itemType = MapItemType(query.Filters.Kinds, query.Filters.Subtypes, notes);
        if (itemType is not null) q.Add(new("itemType", itemType));

        q.Add(new("limit", limit.ToString()));
        q.Add(new("format", "json"));

        // 没有 q 也没有 itemType 也没有集合 = 什么都没筛，那就是"列出整个库"。
        // 这不算错（用户可能就想看看库里有什么），但不能假装这是一次搜索。
        if (text.Length == 0 && itemType is null && collectionKey is null)
            notes.Add("没有可下推的条件，将列出整个文库");

        NoteUnsupported(query, notes);
        return new ZoteroTranslation(new ZoteroRequest { Path = path, Query = q }, notes);
    }

    /// <summary>
    /// 搜索串。Zotero 的 <c>q=</c> 覆盖 标题 / 作者 / 期刊名与缩写 / 年份 / key，
    /// <b>不含 DOI、摘要、标签、集合名</b> —— 后面那些要么走专用参数，要么根本搜不到。
    /// </summary>
    internal static string BuildSearchText(SearchQuery query, List<string> notes)
    {
        if (!string.IsNullOrWhiteSpace(query.Filters.Phrase))
            return "\"" + query.Filters.Phrase!.Replace("\"", string.Empty).Trim() + "\"";

        if (query.IsStructured)
        {
            // Everything 的函数名对 Zotero 毫无意义；原样送过去只会 0 条。
            notes.Add("该后端不支持高级查询语法，已按纯文本搜索");
            return query.Text.Trim();
        }

        return query.Text.Trim();
    }

    /// <summary>
    /// 条目类型下推。<b>子类型优先</b>：它是精确的（<c>journal-article</c> → <c>journalArticle</c>），
    /// 而 <see cref="ResultKind"/> 只有"文献条目"这一档粗粒度，只能做近似（翻译成 <c>-attachment</c>）。
    /// <para>
    /// 布尔语法是实测过的：多个 <c>itemType=</c> 是 AND、<c>||</c> 是 OR、<c>-</c> 是 NOT。
    /// 多个子类型用 <c>||</c> 连（"期刊论文或预印本"）。
    /// </para>
    /// </summary>
    internal static string? MapItemType(IReadOnlyList<ResultKind> kinds, IReadOnlyList<string> subtypes,
                                        List<string> notes)
    {
        // ① 子类型是精确的，优先
        if (subtypes.Count > 0)
        {
            var mapped = subtypes.Select(ToZoteroItemType).Where(s => s.Length > 0).ToList();
            if (mapped.Count == 1) return mapped[0];
            if (mapped.Count > 1) return string.Join(" || ", mapped);
        }

        if (kinds.Count == 0) return null;

        var wantsNote = kinds.Contains(ResultKind.Note);
        var wantsAttachment = kinds.Contains(ResultKind.Attachment);
        var wantsBib = kinds.Contains(ResultKind.BibliographicItem);

        if (wantsBib && !wantsNote && !wantsAttachment) return "-attachment";
        if (wantsNote && !wantsAttachment && !wantsBib) return "note";
        if (wantsAttachment && !wantsNote && !wantsBib) return "attachment";

        if (wantsBib || wantsNote || wantsAttachment)
        {
            notes.Add("类型条件含多种条目类型，已取最宽的一种下推，其余在前端过滤");
            return wantsAttachment ? "attachment" : wantsNote ? "note" : "-attachment";
        }

        // 其它 Kind（文件/图片/视频…）对 Zotero 没有意义 —— 调度阶段本该已经跳过它。
        notes.Add("该后端只产出文献条目，类型条件已在前端过滤");
        return null;
    }

    /// <summary>
    /// SDK 的子类型（<c>journal-article</c>）→ Zotero 的 <c>itemType</c>（<c>journalArticle</c>）。
    /// 与 <c>ZoteroItemMapper.ToSubtype</c> 互为逆运算 —— 那边把后端值转成统一形式，
    /// 这边把统一形式转回后端值。
    /// </summary>
    internal static string ToZoteroItemType(string subtype)
    {
        if (string.IsNullOrWhiteSpace(subtype)) return string.Empty;
        var parts = subtype.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;

        var sb = new System.Text.StringBuilder(subtype.Length);
        sb.Append(parts[0]);
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            sb.Append(char.ToUpperInvariant(parts[i][0])).Append(parts[i][1..]);
        }
        return sb.ToString();
    }

    /// <summary>把"下推不了"的条件逐条说清楚 —— 静默丢弃会让用户以为筛选生效了。</summary>
    internal static void NoteUnsupported(SearchQuery query, List<string> notes)
    {
        var f = query.Filters;

        if (f.Extensions.Count > 0)
            notes.Add("Zotero 不按文件扩展名筛选，该条件已在前端过滤");

        if (f.MinSizeBytes is not null || f.MaxSizeBytes is not null)
            notes.Add("Zotero 不支持按大小筛选，已在前端过滤");

        if (f.ModifiedAfter is not null)
            // ⚠ 这条特别重要：API 没有任何日期过滤参数，而**未知参数会被静默忽略** ——
            // 顺手塞一个 date= 进去不会报错，只会让人以为在筛。
            notes.Add("Zotero 本地 API 没有日期范围过滤，该条件已在前端过滤");

        if (f.RegexRequested || !string.IsNullOrWhiteSpace(f.RegexPattern))
            notes.Add("Zotero 不支持正则，已按纯文本搜索");

        if (f.FoldersOnly)
            notes.Add("Zotero 没有文件夹概念（集合不等于目录）");
    }
}

/// <summary>翻译结果 + 如实降级说明。</summary>
public sealed record ZoteroTranslation(ZoteroRequest Request, IReadOnlyList<string> Notes);

/// <summary>
/// 后端级选项。默认 <c>everything</c>：用户搜文献时，词在标题里还是在 PDF 里都该找到，
/// 而 <c>titleCreatorYear</c> 连摘要都不搜（实测）。
/// </summary>
public sealed record ZoteroQueryOptions
{
    /// <summary><c>everything</c>（含 PDF 正文）或 <c>titleCreatorYear</c>（只搜元数据）。</summary>
    public string Qmode { get; init; } = "everything";

    public static ZoteroQueryOptions Default { get; } = new();
}
