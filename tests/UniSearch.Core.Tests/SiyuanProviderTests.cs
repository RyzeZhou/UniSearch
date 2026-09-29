using System.Text.Json;
using UniSearch.Providers.Siyuan;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Model;
using Xunit;

namespace UniSearch.Tests;

/// <summary>
/// 思源 Provider 的纯函数部分：SQL 翻译与行映射。
/// <para>
/// 断言用的 JSON <b>照真实响应抄</b>（本机 192.168.200.1:6806 / 思源 3.3.5 实测，5275 块），
/// 不是编的样例 —— 编的样例只会证明"我以为的形状是对的"。
/// </para>
/// </summary>
public class SiyuanProviderTests
{
    static SearchQuery Query(string text, QueryFilters? filters = null, bool structured = false, int budget = 60)
        => new()
        {
            RequestId = 1,
            RawText = text,
            Text = text,
            ProviderText = text,
            IsStructured = structured,
            Terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).ToList(),
            Filters = filters ?? QueryFilters.None,
            ResultBudget = budget,
        };

    static string Sql(SearchQuery q, string? scope = null) =>
        SiYuanQueryTranslator.Translate(q, scope).Request.Statement;

    /// <summary>SQL 里的单引号总数。偶数 = 每个引号都成对 = 没有落单的引号可供逃逸。</summary>
    static int QuoteCount(string sql) => sql.Count(c => c == '\'');

    // ───────────────────────── 头号纪律：SQL 注入 ─────────────────────────

    [Fact]
    public void Every_value_goes_through_the_single_quote_doubling()
    {
        // /api/query/sql 不接受参数绑定（请求体只有 {stmt}），值只能拼进 SQL ——
        // 一个没转义的引号就是注入。这条测试是整个 Provider 的安全底线。
        var sql = Sql(Query("it's"));

        Assert.Contains("'%it''s%'".Replace("%", ""), sql.Replace("%", ""));
        Assert.Contains("instr(lower(content), lower('it''s'))", sql);
    }

    [Fact]
    public void A_quote_in_a_phrase_cannot_break_out_of_the_string_literal()
    {
        var q = Query("x", QueryFilters.None with { Phrase = "'; DROP TABLE blocks;--" });
        var sql = Sql(q);

        // 整个恶意串必须留在<b>一个字面量</b>里
        Assert.Contains("'''; DROP TABLE blocks;--'", sql);
        // 判据是"引号成对"：落单的引号才是能逃逸的那种。
        // （不能断言 DoesNotContain("'; DROP") —— 那串是 '''; DROP 的子串，转义正确时反而会命中）
        Assert.Equal(0, QuoteCount(sql) % 2);
        Assert.EndsWith("LIMIT 60", sql);
    }

    [Fact]
    public void Values_are_matched_literally_not_as_like_wildcards()
    {
        // 用 instr 而不是 LIKE：思源的 SQL 端点不支持 ESCAPE 子句（实测任何写法都返回 data:null），
        // 于是 LIKE 里的 % 没法转义 —— 用户搜 "100%" 会命中所有含 "100" 的块（实测 96 条 vs 字面 6 条）。
        var sql = Sql(Query("100%"));

        Assert.Contains("instr(lower(content), lower('100%'))", sql);
        Assert.DoesNotContain("LIKE", sql);
    }

    [Fact]
    public void Underscore_is_also_literal()
    {
        var sql = Sql(Query("a_b"));
        Assert.Contains("lower('a_b')", sql);
        Assert.DoesNotContain("LIKE", sql);
    }

    // ───────────────────────── 条件拼装 ─────────────────────────

    [Fact]
    public void Every_term_must_appear()
    {
        var sql = Sql(Query("GEO 数据"));
        Assert.Contains("lower('geo')", sql);
        Assert.Contains("lower('数据')", sql);
        Assert.Contains(" AND ", sql);
    }

    [Fact]
    public void No_terms_means_no_where_clause_but_still_orders_and_limits()
    {
        var t = SiYuanQueryTranslator.Translate(Query(""), null);
        Assert.DoesNotContain("WHERE", t.Request.Statement);
        Assert.Contains("ORDER BY updated DESC", t.Request.Statement);
        Assert.True(t.Request.EndsWithLimit);
        Assert.Contains(t.Notes, n => n.Contains("没有可下推的条件"));
    }

    [Theory]
    [InlineData(ResultKind.Document, "type IN ('d')")]
    [InlineData(ResultKind.CodeSymbol, "type IN ('c')")]
    [InlineData(ResultKind.Note, "type NOT IN ('d', 'c')")]
    public void Kinds_map_onto_block_types(ResultKind kind, string expected)
    {
        Assert.Contains(expected, Sql(Query("x", QueryFilters.None with { Kinds = [kind] })));
    }

    [Fact]
    public void Note_kind_excludes_documents_and_code_blocks()
    {
        // 不排除的话 kind:note 会把文档块也带出来，而它们在结果里归到「文档」分类 ——
        // 用户点「笔记」却看到文档，是"筛选器没生效"那类老坑的翻版
        var sql = Sql(Query("x", QueryFilters.None with { Kinds = [ResultKind.Note] }));
        Assert.Contains("NOT IN", sql);
    }

    [Fact]
    public void File_like_kinds_are_reported_as_frontend_filtered()
    {
        var t = SiYuanQueryTranslator.Translate(Query("x", QueryFilters.None with { Kinds = [ResultKind.Image] }), null);
        Assert.DoesNotContain("type IN", t.Request.Statement);
        Assert.Contains(t.Notes, n => n.Contains("只产出笔记块"));
    }

    [Fact]
    public void Only_siyuan_subtypes_reach_the_sql()
    {
        // SDK 的 Subtypes 本来是文献条目语义（journal-article）。直接拼进 SQL 会得到
        // 一个永远匹配不到的 subtype='journal-article' —— 白跑一趟还让人以为在筛。
        var mixed = Sql(Query("x", QueryFilters.None with { Subtypes = ["h1", "journal-article", "h2"] }));
        Assert.Contains("subtype IN ('h1', 'h2')", mixed);
        Assert.DoesNotContain("journal-article", mixed);

        var foreign = SiYuanQueryTranslator.Translate(
            Query("x", QueryFilters.None with { Subtypes = ["journal-article"] }), null);
        // 注意判据是 "subtype IN" 而不是 "subtype" —— 后者是 SELECT 的列名，永远在语句里
        Assert.DoesNotContain("subtype IN", foreign.Request.Statement);
        Assert.Contains(foreign.Notes, n => n.Contains("不属于思源的块类型"));
    }

    [Fact]
    public void Notebook_facet_becomes_a_box_filter()
    {
        var q = Query("x", QueryFilters.None with
        {
            Facets = [new FacetSelection(SiYuanProvider.NotebookFacetId, ["20260616204442-157esq1", "20210808180117-czj9bvb"])],
        });
        Assert.Contains("box IN ('20260616204442-157esq1', '20210808180117-czj9bvb')", Sql(q));
    }

    [Fact]
    public void Notebook_scope_and_facet_combine_instead_of_conflicting()
    {
        var q = Query("x", QueryFilters.None with
        {
            Facets = [new FacetSelection(SiYuanProvider.NotebookFacetId, ["NNN"])],
        });
        Assert.Contains("box IN ('SCOPED', 'NNN')", Sql(q, "SCOPED"));
    }

    [Fact]
    public void An_empty_facet_shell_produces_no_condition()
    {
        var q = Query("x", QueryFilters.None with
        {
            Facets = [new FacetSelection(SiYuanProvider.NotebookFacetId, [])],
        });
        Assert.DoesNotContain("box IN", Sql(q));
    }

    // ───────────────────────── 日期 / 正则 / 降级 ─────────────────────────

    [Fact]
    public void Modified_after_is_formatted_as_siyuan_stamps()
    {
        var after = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        var sql = Sql(Query("x", QueryFilters.None with { ModifiedAfter = after }));
        Assert.Contains("updated >= '20260102030405'", sql);
    }

    [Fact]
    public void Regex_pushes_down_because_the_kernel_supports_it()
    {
        // 实测 content REGEXP 'G[0-9]+' 返回 109 条 —— SQLite 本身没有 REGEXP，是思源注入的
        var q = Query("x", QueryFilters.None with { RegexRequested = true, RegexPattern = "G[0-9]+" });
        Assert.Contains("content REGEXP 'G[0-9]+'", Sql(q));
    }

    [Fact]
    public void Unsupported_filters_are_reported_not_silently_dropped()
    {
        var t = SiYuanQueryTranslator.Translate(
            Query("x", QueryFilters.None with
            {
                Extensions = ["pdf"],
                MinSizeBytes = 1024,
                FoldersOnly = true,
                ExplicitDirectory = @"D:\x",
            }), null);

        Assert.Contains(t.Notes, n => n.Contains("扩展名"));
        Assert.Contains(t.Notes, n => n.Contains("大小"));
        Assert.Contains(t.Notes, n => n.Contains("文件夹"));
        Assert.Contains(t.Notes, n => n.Contains("目录限定"));
        Assert.DoesNotContain("ext", t.Request.Statement);
    }

    [Fact]
    public void Foreign_query_syntax_is_searched_as_plain_text()
    {
        var t = SiYuanQueryTranslator.Translate(Query("ext:pdf foo", structured: true), null);
        Assert.Contains(t.Notes, n => n.Contains("不支持高级查询语法"));
        Assert.Contains("lower('ext:pdf foo')", t.Request.Statement);
    }

    [Fact]
    public void Budget_is_clamped()
    {
        Assert.Equal(200, SiYuanQueryTranslator.Translate(Query("x", budget: 9999), null).Request.Limit);
        Assert.Equal(1, SiYuanQueryTranslator.Translate(Query("x", budget: 0), null).Request.Limit);
    }

    // ───────────────────────── 行映射 ─────────────────────────
    //
    // 下面这几条 JSON 照 192.168.200.1:6806 的 SELECT 结果抄（字段与取值都是真的）

    const string ParagraphRow = """
    {"alias":"","box":"20251016171651-ht0ydon","content":"Precision to plate: AI-driven innovations in fermentation",
     "created":"20251017165116","fcontent":"","hpath":"/AI发酵参考文章","id":"20251017165116-4g4wjxt",
     "name":"","parent_id":"20251017165116-s38efzb","path":"/20251016171654-s0twauh.sy",
     "root_id":"20251016171654-s0twauh","subtype":"","tag":"","type":"p","updated":"20251017165116"}
    """;

    const string DocRow = """
    {"box":"20251016171651-ht0ydon","content":"AI发酵参考文章","created":"20251016171654",
     "fcontent":"","hpath":"/AI发酵参考文章","id":"20251016171654-s0twauh","name":"","path":"/20251016171654-s0twauh.sy",
     "root_id":"20251016171654-s0twauh","subtype":"","tag":"","type":"d","updated":"20251020101516"}
    """;

    const string CodeRow = """
    {"box":"20251209154600-9kzscxv","content":"library(ggplot2)\nggplot(df, aes(x=GEO))","created":"20251209180319",
     "fcontent":"","hpath":"/R语言/绘图","id":"20251209180319-1dwks6u","name":"","path":"/20251209180319-abc.sy",
     "root_id":"20251209180319-root","subtype":"","tag":"#R语言# #绘图#","type":"c","updated":"20251020101609"}
    """;

    static SearchResult Map(string json, IReadOnlyDictionary<string, string>? names = null)
    {
        using var doc = JsonDocument.Parse(json);
        var r = SiYuanItemMapper.Map(doc.RootElement, 1, names);
        Assert.NotNull(r);
        return r!;
    }

    [Fact]
    public void Kinds_come_from_the_block_type_and_match_the_pushdown()
    {
        // 映射口径必须与 MapKinds 的下推口径一致，否则会出现"点分类得到空结果"
        Assert.Equal(ResultKind.Note, Map(ParagraphRow).Kind);
        Assert.Equal(ResultKind.Document, Map(DocRow).Kind);
        Assert.Equal(ResultKind.CodeSymbol, Map(CodeRow).Kind);
    }

    [Fact]
    public void A_block_opens_through_its_siyuan_uri()
    {
        var r = Map(ParagraphRow);
        Assert.Equal("siyuan://blocks/20251017165116-4g4wjxt", r.Uri);
        // 跨机部署时磁盘上没有这个 .sy 文件 —— 填了 Path 只会让打开/预览指向不存在的文件
        Assert.Null(r.Path);
        Assert.True(r.DisablePreview);
    }

    [Fact]
    public void A_document_block_is_a_title_hit_the_rest_are_content_hits()
    {
        Assert.Equal(MatchKind.NameWord, Map(DocRow).Match);
        Assert.Equal(MatchKind.Content, Map(ParagraphRow).Match);
    }

    [Fact]
    public void Subtitle_carries_the_type_and_the_notebook()
    {
        var names = new Dictionary<string, string> { ["20251209154600-9kzscxv"] = "R语言" };
        var r = Map(CodeRow, names);
        Assert.Equal("代码块 · R语言 · 绘图", r.Subtitle);
        Assert.Equal("R语言", r.Metadata["notebook"]);
    }

    [Fact]
    public void Tags_lose_their_hash_marks()
    {
        // 思源的 tag 字段是 "#R语言# #绘图#" 这种形状（实测），界面上再显示一层 # 是噪声
        var r = Map(CodeRow);
        Assert.Contains(r.Tags, t => t.Label == "思源");
        Assert.Contains(r.Tags, t => t.Label == "代码");
        Assert.Contains(r.Tags, t => t.Label == "R语言");
        Assert.Contains(r.Tags, t => t.Label == "绘图");
        Assert.DoesNotContain(r.Tags, t => t.Label.Contains('#'));
    }

    [Fact]
    public void Timestamps_are_parsed_and_the_empty_one_is_not_an_error()
    {
        // 实测 5275 块里 413 块的 updated 是空串 —— 那不是错误，是"这个块没有该字段"
        var r = Map(ParagraphRow);
        Assert.Equal(new DateTime(2025, 10, 17, 16, 51, 16), r.ModifiedAt!.Value.DateTime);

        Assert.Null(SiYuanItemMapper.ParseStamp(""));
        Assert.Null(SiYuanItemMapper.ParseStamp(null));
        Assert.Null(SiYuanItemMapper.ParseStamp("2025-10-17"));      // 不是 YYYYMMDDHHMMSS
    }

    [Fact]
    public void A_long_block_becomes_a_one_line_title_plus_a_snippet()
    {
        var longText = new string('长', 200);
        var json = $$"""
        {"box":"b","content":"{{longText}}","hpath":"/x","id":"20260101000000-aaaaaaa","type":"p","updated":""}
        """;
        var r = Map(json);

        Assert.True(r.Title.Length <= SiYuanItemMapper.TitleMax + 1);
        Assert.EndsWith("…", r.Title);
        Assert.NotNull(r.Snippet);
        Assert.True(r.Snippet!.Length > r.Title.Length);
    }

    [Fact]
    public void A_multiline_block_titles_on_its_first_line()
    {
        var r = Map(CodeRow);
        Assert.Equal("library(ggplot2)", r.Title);
        Assert.Contains("\n", r.Snippet!);
    }

    [Fact]
    public void Rows_without_an_id_are_skipped_instead_of_crashing()
    {
        using var doc = JsonDocument.Parse("""[{"content":"没有 id"},{"id":"ok-1","content":"有 id","type":"p"}]""");
        var list = SiYuanItemMapper.MapArray(doc.RootElement, 2);
        Assert.Single(list);
        Assert.Equal("ok-1", list[0].ProviderItemId);
    }

    [Fact]
    public void Array_mapping_walks_the_data_envelope()
    {
        using var doc = JsonDocument.Parse($"[{ParagraphRow},{DocRow}]");
        var list = SiYuanItemMapper.MapArray(doc.RootElement, 2);
        Assert.Equal(2, list.Count);
        Assert.All(list, r => Assert.Equal(2, r.TotalAvailable));
    }

    // ───────────────────────── 能力与选项 ─────────────────────────

    [Fact]
    public void Descriptor_must_not_claim_kind_filter_support()
    {
        // 声明了它，调度器就以为思源能吃 ext: 过滤 —— 而思源的块根本没有扩展名。
        // 这是从 Zotero 那边学来的同一条教训。
        var p = new SiYuanProvider();
        Assert.False(p.Descriptor.Capabilities.Has(ProviderCapability.SupportsKindFilter));
        Assert.True(p.Descriptor.Capabilities.Has(ProviderCapability.ReturnsNotes));
        Assert.True(p.Descriptor.Capabilities.Has(ProviderCapability.SearchesFileContent));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("siyuan:nb/abc", "abc")]
    [InlineData("zotero:collection/abc", null)]
    public void Notebook_scope_only_accepts_its_own_prefix(string? scope, string? expected)
        => Assert.Equal(expected, SiYuanProvider.ParseNotebookScope(scope));

    [Fact]
    public void Options_default_to_the_local_kernel_and_take_overrides()
    {
        var defaults = SiYuanProvider.ReadOptions(new Dictionary<string, string>());
        Assert.Equal("http://127.0.0.1", defaults.Host);
        Assert.Equal(6806, defaults.Port);
        Assert.Null(defaults.Token);

        var over = SiYuanProvider.ReadOptions(new Dictionary<string, string>
        {
            ["host"] = "192.168.200.1",
            ["port"] = "6807",
            ["token"] = "secret",
        });
        Assert.Equal("192.168.200.1", over.Host);
        Assert.Equal(6807, over.Port);
        Assert.Equal("secret", over.Token);
    }

    [Fact]
    public void A_host_that_already_carries_a_port_is_not_doubled()
    {
        // 用户很容易把 "192.168.200.1:6806" 整个填进 host —— 拼成 ":6806:6806" 的报错
        // 从表面上看不出哪里错了
        using var client = new SiYuanApiClient("192.168.200.1:8080");
        Assert.Equal("http://192.168.200.1:8080", client.BaseUrl);
    }

    [Theory]
    [InlineData("192.168.200.1:6806", "http://192.168.200.1:6806")]
    [InlineData("http://127.0.0.1", "http://127.0.0.1:6806")]
    [InlineData("192.168.200.1", "http://192.168.200.1:6806")]
    public void Base_url_is_built_predictably(string host, string expected)
    {
        using var client = new SiYuanApiClient(host);
        Assert.Equal(expected, client.BaseUrl);
    }

    [Fact]
    public void Auth_hints_point_at_the_right_cause()
    {
        // "凭证没设" 与 "凭证不对" 是两件事，提示不能一样
        Assert.Contains("访问授权码", SiYuanApiClient.AuthHintFor(
            "Auth failed: for security reasons, please set [Access authorization code]")!);
        Assert.Contains("凭证不对", SiYuanApiClient.AuthHintFor("Auth failed [header: Authorization]")!);
        Assert.Null(SiYuanApiClient.AuthHintFor("something else"));
    }

    [Fact]
    public void Block_ids_are_sanitized_before_becoming_file_names()
    {
        Assert.Equal("20251016171654-s0twauh", SiYuanProvider.Sanitize("20251016171654-s0twauh"));
        Assert.Equal("ab", SiYuanProvider.Sanitize("..\\..\\a/b"));
        Assert.Equal("block", SiYuanProvider.Sanitize("///"));
    }

    [Fact]
    public void The_sql_log_line_drops_the_column_list()
    {
        var s = SiYuanProvider.Summarize("SELECT id, box FROM blocks WHERE type='d' LIMIT 60");
        Assert.Equal(" WHERE type='d' LIMIT 60", s);
    }
}
