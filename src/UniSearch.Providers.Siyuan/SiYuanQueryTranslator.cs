using System.Text;
using UniSearch.Sdk.Model;

namespace UniSearch.Providers.Siyuan;

/// <summary>一次思源 SQL 检索的入参（纯数据，便于单测逐字段比对）。</summary>
public sealed record SiYuanRequest
{
    /// <summary>发给 <c>POST /api/query/sql</c> 的 <c>stmt</c>。</summary>
    public required string Statement { get; init; }

    public int Limit { get; init; }

    /// <summary>
    /// 同条件的 <c>COUNT(*)</c>。<b>为什么需要第二条语句</b>：主查询被 <c>LIMIT</c> 截断，
    /// 回来的行数不等于命中总数 —— 状态条上的"共 N 条"必须是真的 N，
    /// 否则用户看到"共 60 条"而库里其实有 204 条。
    /// </summary>
    public required string CountStatement { get; init; }

    /// <summary>取个便于断言的尾巴（<c>LIMIT n</c> 之后没有别的子句）。</summary>
    public bool EndsWithLimit => Statement.EndsWith($"LIMIT {Limit}", StringComparison.Ordinal);
}

/// <summary>
/// 把统一查询翻译成思源的 SQL。<b>纯函数</b>，不碰网络。
/// <para>
/// <b>头号纪律：单引号成对转义。</b> <c>/api/query/sql</c> <b>不接受参数绑定</b>
/// （请求体只有 <c>{stmt}</c>），所以值只能拼进 SQL —— 一个没转义的引号就是注入。
/// 所有进入 SQL 的值<b>必须</b>经过 <see cref="Literal"/>，没有例外。
/// </para>
/// <para>
/// <b>为什么用 <c>instr</c> 而不是 <c>LIKE</c></b>（实测，见 SIYUAN-API-VERIFIED.md §7）：
/// <list type="bullet">
/// <item><description><c>LIKE</c> 里 <c>%</c> 与 <c>_</c> 是通配符，而思源的 SQL 端点
/// <b>不支持 <c>ESCAPE</c> 子句</b>（任何写法都返回 <c>data:null</c>）—— 于是用户搜
/// <c>100%</c> 会命中所有含 <c>100</c> 的块（实测 96 条 vs 字面 6 条），没法修；</description></item>
/// <item><description><c>instr()</c> 是<b>字面</b>子串匹配，没有通配符概念，天然免疫这个坑。</description></item>
/// </list>
/// 大小写不敏感用 <c>lower()</c> 两侧包一层还原（<c>geo</c> → 46 条，与 <c>LIKE</c> 一致）；
/// 中文没有大小写，<c>lower()</c> 对它是恒等变换。
/// </para>
/// </summary>
public static class SiYuanQueryTranslator
{
    /// <summary>结果条数上限。<see cref="SearchQuery.ResultBudget"/> 会被夹到这个区间里。</summary>
    public const int MaxLimit = 200;

    /// <summary>
    /// 会出现在结果里的列。写死而不是 <c>SELECT *</c>：<c>blocks</c> 有 21 列，
    /// 其中 <c>markdown</c>、<c>ial</c>、<c>hash</c> 这些既用不上又占带宽。
    /// </summary>
    internal const string Columns =
        "id, parent_id, root_id, box, path, hpath, name, content, fcontent, type, subtype, tag, created, updated";

    /// <summary>
    /// 思源认识的块子类型白名单。只用来过滤 <see cref="QueryFilters.Subtypes"/> ——
    /// SDK 的 Subtypes 本来是文献条目语义（<c>journal-article</c>），直接塞进 SQL 会拼出
    /// 一个永远匹配不到的 <c>subtype='journal-article'</c>。白名单之外的值一律忽略并记说明。
    /// </summary>
    internal static readonly HashSet<string> KnownSubtypes =
        new(StringComparer.OrdinalIgnoreCase) { "h1", "h2", "h3", "h4", "h5", "h6", "u", "o", "t", "p" };

    /// <summary>
    /// SQL 字符串字面量。SQLite 的转义规则是<b>单引号写两遍</b>（不是反斜杠）。
    /// </summary>
    internal static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>字面子串匹配，大小写不敏感（<c>instr(lower(列), lower('值'))</c>）。</summary>
    internal static string Contains(string column, string value) =>
        $"instr(lower({column}), lower({Literal(value)})) > 0";

    public static SiYuanTranslation Translate(SearchQuery query, string? notebookScope = null)
    {
        var notes = new List<string>();
        var where = new List<string>();

        var limit = Math.Clamp(query.ResultBudget, 1, MaxLimit);

        // ── 关键词：每个词都要出现（AND），与 Everything/AnyTXT 的口径一致 ──
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Filters.Phrase))
        {
            terms.Add(query.Filters.Phrase!.Trim());
        }
        else if (query.IsStructured)
        {
            // Everything 的函数名（ext:、dm: 之类）对思源毫无意义，原样拼进 instr 只会 0 条
            notes.Add("该后端不支持高级查询语法，已按纯文本搜索");
            var t = query.Text.Trim();
            if (t.Length > 0) terms.Add(t);
        }
        else
        {
            foreach (var term in query.Terms)
                if (term.Length > 0) terms.Add(term);
            if (terms.Count == 0 && query.Text.Trim().Length > 0) terms.Add(query.Text.Trim());
        }

        foreach (var term in terms)
            where.Add(Contains("content", term));

        // ── 块类型（Kind）：只按"能不能产出这种 Kind"下推 ──
        var typeFilter = MapKinds(query.Filters.Kinds, notes);
        if (typeFilter is not null) where.Add(typeFilter);

        // ── 块子类型：只认白名单里的（见 KnownSubtypes 的注释）──
        var subtypes = query.Filters.Subtypes
            .Where(s => KnownSubtypes.Contains(s))
            .Select(s => Literal(s.ToLowerInvariant()))
            .Distinct()
            .ToList();
        if (subtypes.Count > 0)
            where.Add($"subtype IN ({string.Join(", ", subtypes)})");
        else if (query.Filters.Subtypes.Count > 0)
            notes.Add("子类型条件不属于思源的块类型，已在前端过滤");

        // ── 值域（笔记本）：候选值来自 lsNotebooks ──
        var boxes = CollectBoxes(query.Filters.Facets, notes);
        if (notebookScope is { Length: > 0 } scoped) boxes.Insert(0, scoped);
        if (boxes.Count > 0)
            where.Add($"box IN ({string.Join(", ", boxes.Select(Literal))})");

        // ── 时间：updated 是 YYYYMMDDHHMMSS 字符串；空串字典序最小，天然被范围条件排除 ──
        if (query.Filters.ModifiedAfter is { } after)
            where.Add($"updated >= {Literal(FormatStamp(after))}");

        // ── 正则：实测内核支持 REGEXP（SQLite 本身没有，是思源注入的）──
        if (query.Filters.RegexRequested && query.Filters.RegexPattern is { Length: > 0 } pattern)
            where.Add($"content REGEXP {Literal(pattern)}");

        NoteUnsupported(query, notes);

        var sb = new StringBuilder("SELECT ").Append(Columns).Append(" FROM blocks");
        if (where.Count > 0) sb.Append(" WHERE ").Append(string.Join(" AND ", where));
        // 最近改过的排前面。updated 为空串的块排最后（字典序最小）—— 那是没同步过 updated 的老块，
        // 排在后面正合适。加 id 兜底让顺序稳定（同样的 updated 之间不该每次换序）。
        sb.Append(" ORDER BY updated DESC, id DESC");
        sb.Append(" LIMIT ").Append(limit);

        if (where.Count == 0)
            notes.Add("没有可下推的条件，将列出最近更新的块");

        // 总数的另一条语句：SELECT 被 LIMIT 截断了，行数不等于命中总数。
        // 状态条要显示"还有 N 条"，那个 N 只能问数据库要（把同一套 WHERE 换个投影）。
        var countSb = new StringBuilder("SELECT COUNT(*) AS n FROM blocks");
        if (where.Count > 0) countSb.Append(" WHERE ").Append(string.Join(" AND ", where));

        return new SiYuanTranslation(
            new SiYuanRequest { Statement = sb.ToString(), Limit = limit, CountStatement = countSb.ToString() },
            notes);
    }

    /// <summary>
    /// Kind → <c>blocks.type</c>。思源的块类型有二十来种，而 SDK 的 <see cref="ResultKind"/>
    /// 只认三档：
    /// <list type="bullet">
    /// <item><description><c>d</c>（文档块）→ <see cref="ResultKind.Document"/></description></item>
    /// <item><description><c>c</c>（代码块）→ <see cref="ResultKind.CodeSymbol"/></description></item>
    /// <item><description>其余（段落/标题/列表/表格/公式…）→ <see cref="ResultKind.Note"/></description></item>
    /// </list>
    /// 三档里只要出现"笔记"，就得用它排除前两档 —— 否则 <c>kind:note</c> 会把文档块也带出来。
    /// </summary>
    internal static string? MapKinds(IReadOnlyList<ResultKind> kinds, List<string> notes)
    {
        if (kinds.Count == 0) return null;

        var wantsDoc = kinds.Contains(ResultKind.Document);
        var wantsCode = kinds.Contains(ResultKind.CodeSymbol);
        var wantsNote = kinds.Contains(ResultKind.Note);

        if (wantsNote && !wantsDoc && !wantsCode) return "type NOT IN ('d', 'c')";

        var types = new List<string>();
        if (wantsDoc) types.Add("'d'");
        if (wantsCode) types.Add("'c'");
        if (types.Count == 0)
        {
            // 剩下的 Kind（文件/图片/视频…）对思源没有意义 —— 调度阶段本该已经跳过它
            notes.Add("该后端只产出笔记块，类型条件已在前端过滤");
            return null;
        }
        return $"type IN ({string.Join(", ", types)})";
    }

    /// <summary>值域里的笔记本 id。域 id 与 <c>SiYuanProvider</c> 声明的一致。</summary>
    internal static List<string> CollectBoxes(IReadOnlyList<FacetSelection> facets, List<string> notes)
    {
        var sel = facets.FirstOrDefault(f =>
            string.Equals(f.FacetId, SiYuanProvider.NotebookFacetId, StringComparison.OrdinalIgnoreCase));
        if (sel is null || sel.Values.Count == 0) return [];

        var boxes = new List<string>(sel.Values.Count);
        foreach (var raw in sel.Values)
        {
            var v = raw.Trim();
            if (v.Length == 0) continue;
            boxes.Add(v);
        }
        // 多选恒为 OR（box IN (...)）—— 一个块只属于一个笔记本，"全部命中"在这里没有意义，
        // 所以这一域不提供 MatchAll 开关（声明 MatchAllDefault: false 且 UI 不显示切换）。
        if (sel.MatchAll && boxes.Count > 1)
            notes.Add("笔记本多选恒为「任一」—— 一个块只属于一个笔记本");
        return boxes;
    }

    /// <summary><c>DateTimeOffset</c> → 思源的 <c>YYYYMMDDHHMMSS</c>（本地时间）。</summary>
    internal static string FormatStamp(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyyMMddHHmmss");

    /// <summary>把"本后端做不到"的条件说清楚，让状态条能如实降级而不是假装筛过了。</summary>
    static void NoteUnsupported(SearchQuery query, List<string> notes)
    {
        if (query.Filters.Extensions.Count > 0)
            notes.Add("思源的块没有扩展名，扩展名条件已在前端过滤");
        if (query.Filters.MinSizeBytes is not null || query.Filters.MaxSizeBytes is not null)
            notes.Add("思源没有文件大小的概念，大小条件已在前端过滤");
        if (query.Filters.FoldersOnly)
            notes.Add("思源没有文件夹概念，该条件已忽略");
        if (query.Filters.ExplicitDirectory is { Length: > 0 })
            notes.Add("思源不接受目录限定，该条件已忽略");
    }
}

/// <param name="Statement">最终 SQL。</param>
/// <param name="Notes">降级说明（每条都是"这个条件在本后端发生了什么"）。</param>
public sealed record SiYuanTranslation(SiYuanRequest Request, IReadOnlyList<string> Notes);
