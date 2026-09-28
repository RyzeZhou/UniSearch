using UniSearch.Providers.Anytxt;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Model;
using Xunit;

namespace UniSearch.Tests;

/// <summary>
/// AnyTXT Provider 的纯函数部分：查询翻译、行解析、盘符调度。
/// <para>
/// 这些断言全部**脱机**跑（不碰 9924）—— 下推规则和字段映射是最容易悄悄写错的地方，
/// 而"起服务才能测"会让它们长期没人测。真联调另有 <c>--selftest-anytxt</c>。
/// </para>
/// </summary>
public class AnytxtProviderTests
{
    static SearchQuery Query(string text, QueryFilters? filters = null, bool structured = false,
                             int budget = 60, string? phrase = null) => new()
    {
        RequestId = 1,
        RawText = text,
        Text = text,
        ProviderText = text,
        IsStructured = structured,
        Terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).ToList(),
        Filters = (filters ?? QueryFilters.None) with { Phrase = phrase ?? filters?.Phrase },
        ResultBudget = budget,
    };

    // ───────────────────────── 翻译：基本形状 ─────────────────────────

    [Fact]
    public void Translate_keeps_free_text_and_pins_the_directory()
    {
        var t = AnytxtQueryTranslator.Translate(Query("半导体 工艺"), @"D:\文档");

        Assert.NotNull(t.Request);
        Assert.Equal("半导体 工艺", t.Request!.Pattern);
        Assert.Equal(@"D:\文档", t.Request.FilterDir);
        Assert.Equal("*", t.Request.FilterExt);        // 不限扩展名
        Assert.Equal(0, t.Request.LastModifyBegin);    // 不限时间
        Assert.Equal(0, t.Request.LastModifyEnd);      // 0 = 不设上界（文档明说）
        Assert.Empty(t.Notes);
    }

    [Fact]
    public void Translate_uses_quotes_for_a_locked_phrase()
    {
        var t = AnytxtQueryTranslator.Translate(Query("semiconductor chip", phrase: "semiconductor chip"), "C:");
        Assert.Equal("\"semiconductor chip\"", t.Request!.Pattern);
    }

    [Fact]
    public void Translate_does_not_passthrough_foreign_syntax()
    {
        // Everything 的函数名对 AnyTXT 毫无意义；原样送过去只会 0 条，所以必须退化成纯文本并说明。
        var q = Query("ancestor:\"D:\\x\\\" report", structured: true);
        var t = AnytxtQueryTranslator.Translate(q, "C:");

        Assert.Equal(q.Text, t.Request!.Pattern);
        Assert.Contains(t.Notes, n => n.Contains("不支持高级查询语法"));
    }

    [Fact]
    public void Translate_maps_extensions_to_filter_ext()
    {
        var t = AnytxtQueryTranslator.Translate(Query("x", new QueryFilters
        {
            Extensions = ["pdf", ".DOCX", "pdf", "  ", "*md"],
        }), "C:");

        // 去点、去星号、小写、去重、保序；分号分隔
        Assert.Equal("*.pdf;*.docx;*.md", t.Request!.FilterExt);
    }

    [Fact]
    public void Translate_maps_modified_after_to_unix_seconds()
    {
        var when = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var t = AnytxtQueryTranslator.Translate(Query("x", new QueryFilters { ModifiedAfter = when }), "C:");

        Assert.Equal(1_700_000_000, t.Request!.LastModifyBegin);
        Assert.Equal(0, t.Request.LastModifyEnd);
    }

    [Theory]
    [InlineData(0, 1)]         // 0 会被服务端拒（"limit is outside its valid integer range"）
    [InlineData(1, 1)]
    [InlineData(60, 60)]
    [InlineData(300, 300)]     // 官方上限
    [InlineData(999, 300)]
    public void Translate_clamps_limit_into_the_documented_range(int budget, int expected)
    {
        var t = AnytxtQueryTranslator.Translate(Query("x", budget: budget), "C:", budget);
        Assert.Equal(expected, t.Request!.Limit);
    }

    [Fact]
    public void Translate_returns_nothing_for_a_blank_query()
    {
        Assert.True(AnytxtQueryTranslator.Translate(Query("   "), "C:").IsEmpty);
    }

    // ───────────────────────── 翻译：如实降级 ─────────────────────────

    [Fact]
    public void Translate_reports_every_filter_it_cannot_push_down()
    {
        var t = AnytxtQueryTranslator.Translate(Query("x", new QueryFilters
        {
            RegexRequested = true,
            RegexPattern = "a.*b",
            MinSizeBytes = 1024,
            FoldersOnly = true,
            Kinds = [ResultKind.BibliographicItem],
        }), "C:");

        Assert.Contains(t.Notes, n => n.Contains("正则"));
        Assert.Contains(t.Notes, n => n.Contains("大小"));
        Assert.Contains(t.Notes, n => n.Contains("文件夹"));
        Assert.Contains(t.Notes, n => n.Contains("类型条件"));
    }

    [Fact]
    public void Translate_says_nothing_when_every_filter_is_supported()
    {
        var t = AnytxtQueryTranslator.Translate(Query("x", new QueryFilters
        {
            Extensions = ["pdf"],
            Kinds = [ResultKind.File],
            ModifiedAfter = DateTimeOffset.FromUnixTimeSeconds(1),
        }), "C:");

        Assert.Empty(t.Notes);   // 别为了"有说明"而编说明
    }

    [Fact]
    public void Count_parameters_must_not_carry_paging_or_order()
    {
        // anytxt.v1.search 只吃搜索参数；带上 limit/offset/order 会被判 -32602。
        var t = AnytxtQueryTranslator.Translate(Query("x"), "C:");
        var count = t.Request!.ToCountParameters();

        Assert.False(count.ContainsKey("limit"));
        Assert.False(count.ContainsKey("offset"));
        Assert.False(count.ContainsKey("order"));
        Assert.True(count.ContainsKey("pattern"));
        Assert.Equal(5, count.Count);
    }

    // ───────────────────────── 行解析：按 field 表查名 ─────────────────────────

    const string StandardOutput = """
    {
      "count": 2,
      "field": ["fid", "lastModify", "size", "file"],
      "files": [
        ["15774878399612516299", "1757005192", "276213", "C:\\a\\english_wikipedia.txt"],
        ["10806752599277329527", "1789178973", "34334", "C:\\b\\deep_research.md"]
      ]
    }
    """;

    static List<SearchResult> Parse(string json, out int pageCount)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return AnytxtProvider.ParseRows(doc.RootElement, "C:", out pageCount);
    }

    [Fact]
    public void ParseRows_maps_the_documented_columns()
    {
        var rows = Parse(StandardOutput, out var pageCount);

        Assert.Equal(2, pageCount);            // count 是**本页行数**，不是总数
        Assert.Equal(2, rows.Count);

        var first = rows[0];
        Assert.Equal(@"C:\a\english_wikipedia.txt", first.Path);
        Assert.Equal("english_wikipedia.txt", first.Title);
        Assert.Equal(276213, first.SizeBytes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1757005192), first.ModifiedAt);
        Assert.Equal("txt", first.Subtype);
        Assert.Equal("15774878399612516299", first.Metadata["fid"]);   // 64 位数字串，必须当字符串
        Assert.Equal(ResultKind.File, first.Kind);
    }

    [Fact]
    public void ParseRows_follows_the_field_table_not_the_position()
    {
        // 服务端哪天把列换个顺序，按下标硬编码就会静默读错列 —— 这条断言就是防它的。
        const string shuffled = """
        {
          "count": 1,
          "field": ["file", "size", "fid", "lastModify"],
          "files": [["C:\\x\\y.pdf", "999", "42", "1600000000"]]
        }
        """;

        var row = Assert.Single(Parse(shuffled, out _));
        Assert.Equal(@"C:\x\y.pdf", row.Path);
        Assert.Equal(999, row.SizeBytes);
        Assert.Equal("42", row.Metadata["fid"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1600000000), row.ModifiedAt);
    }

    [Fact]
    public void ParseRows_marks_every_row_as_a_content_match()
    {
        var rows = Parse(StandardOutput, out _);
        Assert.All(rows, r => Assert.True(r.Match.HasFlag(MatchKind.Content)));
        Assert.All(rows, r => Assert.Equal(AnytxtProvider.ProviderId, r.ProviderId));
    }

    [Fact]
    public void ParseRows_survives_a_missing_or_broken_payload()
    {
        using var empty = System.Text.Json.JsonDocument.Parse("{}");
        Assert.Empty(AnytxtProvider.ParseRows(empty.RootElement, "C:", out var n));
        Assert.Equal(0, n);
    }

    [Fact]
    public void ParseRows_skips_rows_without_a_path()
    {
        const string noPath = """
        { "count": 1, "field": ["fid", "lastModify", "size", "file"], "files": [["1", "0", "0", ""]] }
        """;
        Assert.Empty(Parse(noPath, out _));
    }

    // ───────────────────────── 类型判定 ─────────────────────────

    [Theory]
    [InlineData("jpg", ResultKind.Image)]
    [InlineData("MP4", ResultKind.Video)]
    [InlineData("flac", ResultKind.Audio)]
    [InlineData("exe", ResultKind.Application)]
    [InlineData("pdf", ResultKind.File)]
    [InlineData(null, ResultKind.File)]
    public void KindOf_only_declares_what_it_can_prove(string? ext, ResultKind expected)
        => Assert.Equal(expected, AnytxtProvider.KindOf(ext));

    // ───────────────────────── 盘符调度 ─────────────────────────

    [Fact]
    public void ResolveTargets_uses_the_directory_when_scoped()
    {
        var ctx = SearchContext.InDirectory(@"D:\文档\子目录", QueryOrigin.ExplorerHotkey);
        Assert.Equal([@"D:\文档\子目录"], AnytxtProvider.ResolveTargets(ctx));
    }

    [Fact]
    public void ResolveTargets_enumerates_drives_when_global()
    {
        // 全盘搜索必须逐盘枚举：filterDir 传空会被服务端强制成 C:，D 盘的东西永远搜不到。
        var targets = AnytxtProvider.ResolveTargets(SearchContext.Global());

        Assert.NotEmpty(targets);
        Assert.All(targets, t => Assert.EndsWith(":", t));      // 形如 "C:"，不是 "C:\"
        Assert.DoesNotContain("", targets);
    }

    [Fact]
    public void FixedDrives_are_reported_without_a_trailing_separator()
    {
        Assert.All(AnytxtProvider.FixedDrives(), d => Assert.DoesNotContain('\\', d));
    }

    // ───────────────────────── 端点 ─────────────────────────

    [Fact]
    public void Default_endpoint_is_the_documented_one()
    {
        // 9920 是内部 QJsonRpc 接口（方法名 ATRpcServer.Searcher.V1.*、params 要单元素数组），
        // 用错端口会得到 "service '' not found"，而且很难看出是端口的问题。
        Assert.Equal("http://127.0.0.1:9924/rpc", AnytxtRpcClient.DefaultEndpoint);
    }

    [Fact]
    public void Rpc_parse_reads_the_business_envelope()
    {
        var call = AnytxtRpcClient.Parse(
            """{"jsonrpc":"2.0","id":1,"result":{"data":{"input":{},"output":{"return":true}},"errno":0}}""");

        Assert.True(call.Ok);
        Assert.Equal(0, call.Errno);
        Assert.True(call.Output.GetProperty("return").GetBoolean());
    }

    [Fact]
    public void Rpc_parse_surfaces_protocol_errors()
    {
        var call = AnytxtRpcClient.Parse(
            """{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"invalid parameters"}}""");

        Assert.False(call.Ok);
        Assert.Equal(-32602, call.ErrorCode);
        Assert.Contains("invalid parameters", call.Failure);
    }

    [Fact]
    public void Rpc_parse_does_not_treat_a_nonzero_errno_as_failure()
    {
        // 实测：filterDir 给 D: 时明明有结果，errno 却是 1。拿 errno 判成败会把好结果丢掉。
        var call = AnytxtRpcClient.Parse(
            """{"jsonrpc":"2.0","id":1,"result":{"data":{"input":{},"output":{"count":1}},"errno":1}}""");

        Assert.True(call.Ok);
        Assert.Equal(1, call.Errno);
        Assert.Equal(1, call.Output.GetProperty("count").GetInt32());
    }
}
