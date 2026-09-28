using UniSearch.Providers.Zotero;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Model;
using Xunit;

namespace UniSearch.Tests;

/// <summary>
/// Zotero Provider 的纯函数部分：查询翻译、条目映射、路径解码、URI。
/// <para>
/// 断言用的 JSON 全部<b>照真实响应抄</b>（本机 Zotero 10.0.3 实测），不是编的样例 ——
/// 编的样例只会证明"我以为的形状是对的"。
/// </para>
/// </summary>
public class ZoteroProviderTests
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

    // ───────────────────────── 翻译 ─────────────────────────

    [Fact]
    public void Translate_sends_keyword_limit_and_format()
    {
        var t = ZoteroQueryTranslator.Translate(Query("AlphaGenome"), 50);

        Assert.Equal("/users/0/items", t.Request.Path);
        Assert.Equal("AlphaGenome", t.Request.Q);
        Assert.Equal("everything", t.Request.Qmode);     // 默认含 PDF 正文
        Assert.Equal(50, t.Request.Limit);
        Assert.Contains(t.Request.Query, kv => kv.Key == "format" && kv.Value == "json");
        Assert.Null(t.Request.ItemType);
    }

    [Fact]
    public void Translate_must_always_send_limit()
    {
        // 本地 API 省略 limit 会一次返回整个文库（实测）—— 库里几千条时会拖死首屏。
        var t = ZoteroQueryTranslator.Translate(Query("x"), 25);
        Assert.NotNull(t.Request.Limit);
        Assert.Contains(t.Request.Query, kv => kv.Key == "limit");
    }

    [Fact]
    public void Translate_quotes_a_locked_phrase()
    {
        // Zotero 的引号是词序敏感的真短语：`"chip semiconductor"` 与 `"semiconductor chip"` 结果不同。
        var t = ZoteroQueryTranslator.Translate(Query("semiconductor chip", phrase: "semiconductor chip"), 10);
        Assert.Equal("\"semiconductor chip\"", t.Request.Q);
    }

    [Fact]
    public void Translate_does_not_passthrough_foreign_syntax()
    {
        var q = Query("ancestor:\"D:\\x\\\" report", structured: true);
        var t = ZoteroQueryTranslator.Translate(q, 10);

        Assert.Equal(q.Text, t.Request.Q);
        Assert.Contains(t.Notes, n => n.Contains("不支持高级查询语法"));
    }

    [Fact]
    public void Translate_routes_a_collection_scope_to_the_collection_endpoint()
    {
        var t = ZoteroQueryTranslator.Translate(Query("x"), 10, collectionKey: "7GSUHR6G");
        Assert.Equal("/users/0/collections/7GSUHR6G/items", t.Request.Path);
    }

    [Theory]
    [InlineData("zotero:collection/7GSUHR6G", "7GSUHR6G")]
    [InlineData("ZOTERO:COLLECTION/abc", "abc")]
    [InlineData("erf:/home/zhou", null)]        // 别的后端的 scope 不该被误解析
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseCollectionKey_only_accepts_its_own_prefix(string? scope, string? expected)
        => Assert.Equal(expected, ZoteroProvider.ParseCollectionKey(scope));

    // ───────────────────────── itemType 近似映射 ─────────────────────────

    [Fact]
    public void MapItemType_approximates_bibliographic_items_as_not_attachment()
    {
        // Zotero 有 40 种条目类型，我们的 Kind 只有"文献条目"一档 —— 只能取最接近的表达。
        var notes = new List<string>();
        Assert.Equal("-attachment", ZoteroQueryTranslator.MapItemType([ResultKind.BibliographicItem], [], notes));
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData(ResultKind.Note, "note")]
    [InlineData(ResultKind.Attachment, "attachment")]
    public void MapItemType_maps_the_exact_ones(ResultKind kind, string expected)
        => Assert.Equal(expected, ZoteroQueryTranslator.MapItemType([kind], [], []));

    [Fact]
    public void MapItemType_reports_when_it_has_to_approximate()
    {
        var notes = new List<string>();
        var mapped = ZoteroQueryTranslator.MapItemType([ResultKind.BibliographicItem, ResultKind.Note], [], notes);

        Assert.NotNull(mapped);
        Assert.Contains(notes, n => n.Contains("前端过滤"));
    }

    [Fact]
    public void MapItemType_says_nothing_for_file_like_kinds()
    {
        var notes = new List<string>();
        Assert.Null(ZoteroQueryTranslator.MapItemType([ResultKind.Image], [], notes));
        Assert.Contains(notes, n => n.Contains("只产出文献条目"));
    }

    [Fact]
    public void MapItemType_prefers_the_exact_subtype_over_the_approximate_kind()
    {
        // 子类型是精确的（journal-article → journalArticle），Kind 只能近似（-attachment）。
        // 两者同时给出时必须用子类型 —— 否则"只要期刊论文"会退化成"只要不是附件"。
        var notes = new List<string>();
        var mapped = ZoteroQueryTranslator.MapItemType([ResultKind.BibliographicItem], ["journal-article"], notes);

        Assert.Equal("journalArticle", mapped);
        Assert.Empty(notes);
    }

    [Fact]
    public void MapItemType_joins_several_subtypes_with_or()
    {
        // 多个子类型 = "期刊论文或预印本"，用 Zotero 的 ||（实测有效）
        var mapped = ZoteroQueryTranslator.MapItemType([], ["journal-article", "preprint"], []);
        Assert.Equal("journalArticle || preprint", mapped);
    }

    [Theory]
    [InlineData("journal-article", "journalArticle")]
    [InlineData("preprint", "preprint")]
    [InlineData("book-section", "bookSection")]
    [InlineData("computer-program", "computerProgram")]
    [InlineData("attachment", "attachment")]
    public void ToZoteroItemType_is_the_inverse_of_ToSubtype(string subtype, string expected)
        => Assert.Equal(expected, ZoteroQueryTranslator.ToZoteroItemType(subtype));

    [Fact]
    public void Subtype_round_trips_through_both_directions()
    {
        // 两处转换必须互逆：映射器把后端值转成统一形式，翻译器再转回后端值。
        foreach (var zoteroType in new[] { "journalArticle", "bookSection", "computerProgram", "preprint" })
            Assert.Equal(zoteroType,
                ZoteroQueryTranslator.ToZoteroItemType(ZoteroItemMapper.ToSubtype(zoteroType)));
    }

    [Fact]
    public void Translate_pushes_subtypes_down_as_item_type()
    {
        var t = ZoteroQueryTranslator.Translate(
            Query("x", new QueryFilters { Subtypes = ["journal-article"] }), 10);

        Assert.Equal("journalArticle", t.Request.ItemType);
    }

    // ───────────────────────── 如实降级 ─────────────────────────

    [Fact]
    public void Translate_reports_everything_it_cannot_push_down()
    {
        var t = ZoteroQueryTranslator.Translate(Query("x", new QueryFilters
        {
            Extensions = ["pdf"],
            MinSizeBytes = 1024,
            ModifiedAfter = DateTimeOffset.FromUnixTimeSeconds(1),
            RegexRequested = true,
            FoldersOnly = true,
        }), 10);

        Assert.Contains(t.Notes, n => n.Contains("扩展名"));
        Assert.Contains(t.Notes, n => n.Contains("大小"));
        Assert.Contains(t.Notes, n => n.Contains("日期范围"));   // API 根本没有日期过滤，而且未知参数会被静默忽略
        Assert.Contains(t.Notes, n => n.Contains("正则"));
        Assert.Contains(t.Notes, n => n.Contains("文件夹"));
    }

    [Fact]
    public void Translate_never_smuggles_a_date_parameter()
    {
        // 这是本后端最阴的坑：未知参数名会被**静默忽略** ——
        // 顺手塞个 date= 进去不会报错，只会让人以为在筛日期。
        var t = ZoteroQueryTranslator.Translate(Query("x", new QueryFilters
        {
            ModifiedAfter = DateTimeOffset.FromUnixTimeSeconds(1),
        }), 10);

        Assert.DoesNotContain(t.Request.Query, kv => kv.Key.Contains("date", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(t.Request.Query, kv => kv.Key.Contains("since", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Translate_flags_a_query_with_nothing_to_push_down()
    {
        var t = ZoteroQueryTranslator.Translate(Query("   "), 10);
        Assert.Contains(t.Notes, n => n.Contains("列出整个文库"));
    }

    // ───────────────────────── URL 拼接 ─────────────────────────

    [Fact]
    public void BuildUrl_encodes_values_but_keeps_the_boolean_operators()
    {
        // Zotero 的 `||` 与 `-` 是语法的一部分，编码错一个字符语义就变了。
        var url = ZoteroApiClient.BuildUrl("http://127.0.0.1:23119", "/users/0/items",
        [
            new("tag", "DNA合成"),
            new("itemType", "journalArticle || preprint"),
            new("limit", "10"),
        ]);

        Assert.StartsWith("http://127.0.0.1:23119/users/0/items?", url);
        Assert.Contains("tag=DNA%E5%90%88%E6%88%90", url);
        Assert.Contains("%7C%7C", url);          // || 编码后是 %7C%7C，解回来仍是 ||
        Assert.Contains("limit=10", url);
    }

    [Fact]
    public void BuildUrl_without_query_has_no_question_mark()
        => Assert.Equal("http://127.0.0.1:23119/api/", ZoteroApiClient.BuildUrl("http://127.0.0.1:23119/", "/api/", []));

    // ───────────────────────── 条目映射（真实响应样例） ─────────────────────────

    /// <summary>照本机 Zotero 10.0.3 的真实响应抄的顶层条目（字段有删减，结构未改）。</summary>
    const string JournalArticleJson = """
    {
      "key": "N35RT33I",
      "version": 0,
      "links": {
        "self": { "href": "http://localhost:23119/api/users/21644069/items/N35RT33I", "type": "application/json" },
        "attachment": { "href": "http://localhost:23119/api/users/21644069/items/FGRH7LG3",
                        "type": "application/json", "attachmentType": "application/pdf", "attachmentSize": 9295744 }
      },
      "meta": { "creatorSummary": "Jung 等", "parsedDate": "2026-06-17", "numChildren": 1 },
      "data": {
        "key": "N35RT33I",
        "itemType": "journalArticle",
        "title": "Parallel enzymatic DNA synthesis using a semiconductor chip",
        "date": "2026-06-17",
        "DOI": "10.1038/s41928-026-01662-9",
        "url": "https://www.nature.com/articles/s41928-026-01662-9",
        "publicationTitle": "Nature Electronics",
        "journalAbbreviation": "Nat Electron",
        "volume": "9", "issue": "8", "pages": "932-940",
        "creators": [
          { "firstName": "Woo-Bin", "lastName": "Jung", "creatorType": "author" },
          { "firstName": "Han Sae", "lastName": "Jung", "creatorType": "author" }
        ],
        "tags": [],
        "collections": [],
        "dateAdded": "2026-09-15T03:27:32Z",
        "dateModified": "2026-09-15T03:27:32Z"
      }
    }
    """;

    /// <summary>照真实响应抄的附件行（enclosure 的 href 里带中文与空格，都是百分号编码的）。</summary>
    const string AttachmentJson = """
    {
      "key": "FGRH7LG3",
      "version": 0,
      "links": {
        "up": { "href": "http://localhost:23119/api/users/21644069/items/N35RT33I", "type": "application/json" },
        "enclosure": {
          "href": "file:///C:/Users/zhou/Zotero/storage/FGRH7LG3/Jung%20%E7%AD%89%20-%202026%20-%20Parallel%20enzymatic%20DNA%20synthesis%20using%20a%20semiconductor%20chip.pdf",
          "type": "application/pdf",
          "title": "Jung 等 - 2026 - Parallel enzymatic DNA synthesis using a semiconductor chip.pdf",
          "length": 9295744
        }
      },
      "data": {
        "key": "FGRH7LG3",
        "itemType": "attachment",
        "title": "PDF",
        "contentType": "application/pdf",
        "dateAdded": "2026-09-15T03:27:32Z",
        "dateModified": "2026-09-15T03:27:32Z",
        "tags": []
      }
    }
    """;

    static SearchResult MapOne(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return ZoteroItemMapper.Map(doc.RootElement, 44)!;
    }

    [Fact]
    public void Map_top_level_item_has_no_path_and_a_select_uri()
    {
        var r = MapOne(JournalArticleJson);

        Assert.Equal("N35RT33I", r.ProviderItemId);
        Assert.Equal(ResultKind.BibliographicItem, r.Kind);
        // SDK 约定 Subtype 是小写连字符形式（筛选器定义按它匹配），不是 Zotero 原生的 camelCase
        Assert.Equal("journal-article", r.Subtype);
        Assert.Equal("Parallel enzymatic DNA synthesis using a semiconductor chip", r.Title);
        Assert.Null(r.Path);                                        // 顶层条目没有本地路径
        Assert.Equal("zotero://select/library/items/N35RT33I", r.Uri);
        Assert.True(r.ReadOnly);                                    // UniSearch 不改 Zotero
        Assert.True(r.DisablePreview);                              // 没有本地文件，预览给不出东西
        Assert.Equal(44, r.TotalAvailable);
    }

    [Fact]
    public void Map_builds_a_readable_subtitle_from_metadata()
    {
        var r = MapOne(JournalArticleJson);
        Assert.Equal("Jung 等 · 2026 · Nature Electronics", r.Subtitle);
    }

    [Fact]
    public void Map_puts_the_useful_metadata_on_the_row()
    {
        var r = MapOne(JournalArticleJson);

        Assert.Equal("10.1038/s41928-026-01662-9", r.Metadata["doi"]);
        Assert.Equal("2026", r.Metadata["year"]);
        Assert.Equal("Nature Electronics", r.Metadata["venue"]);
        Assert.Equal("Jung 等", r.Metadata["author"]);
        Assert.Equal("journalArticle", r.Metadata["itemType"]);
        Assert.Equal(9295744, r.SizeBytes);                          // 来自 links.attachment.attachmentSize
        Assert.Equal(DateTimeOffset.Parse("2026-09-15T03:27:32Z"), r.ModifiedAt);
    }

    [Fact]
    public void Map_attachment_gets_a_real_decoded_local_path()
    {
        var r = MapOne(AttachmentJson);

        Assert.Equal(ResultKind.Attachment, r.Kind);
        // 必须解码：不解码的话右键/预览都会拿着 %20 去找一个不存在的文件
        Assert.Equal(@"C:\Users\zhou\Zotero\storage\FGRH7LG3\Jung 等 - 2026 - Parallel enzymatic DNA synthesis using a semiconductor chip.pdf",
                     r.Path);
        Assert.Null(r.Uri);                                          // 有真实路径就不走 URI 分支
        Assert.Equal(9295744, r.SizeBytes);
        Assert.False(r.DisablePreview);
        Assert.Contains(r.Tags, t => t.Label == "Zotero");
    }

    [Fact]
    public void Map_attachment_keeps_the_title_as_given_even_if_it_is_just_PDF()
    {
        // Zotero 把附件标题存成 "PDF" —— 别自作主张改成文件名，
        // 否则用户在 UniSearch 里看到的标题和 Zotero 里对不上。
        Assert.Equal("PDF", MapOne(AttachmentJson).Title);
    }

    [Fact]
    public void Map_never_throws_on_a_broken_row()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"key":"A","data":{"itemType":"book"}}""");
        var r = ZoteroItemMapper.Map(doc.RootElement, null);

        Assert.NotNull(r);
        Assert.Equal("A", r!.Title);        // 没标题就退回 key，而不是崩
        Assert.Null(r.Path);
    }

    [Fact]
    public void Map_skips_rows_without_a_key()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"data":{"itemType":"book","title":"x"}}""");
        Assert.Null(ZoteroItemMapper.Map(doc.RootElement, null));
    }

    [Fact]
    public void MapArray_ignores_a_non_array_root()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"oops":true}""");
        Assert.Empty(ZoteroItemMapper.MapArray(doc.RootElement, null));
    }

    // ───────────────────────── 年份与作者摘要 ─────────────────────────

    [Theory]
    [InlineData("2026-06-17", "2026")]
    [InlineData("11/2025", "2025")]
    [InlineData("2026", "2026")]
    [InlineData("Spring 2019", "2019")]
    [InlineData("n.d.", null)]
    [InlineData(null, null)]
    public void Year_handles_zoteros_free_text_dates(string? date, string? expected)
        => Assert.Equal(expected, ZoteroItemMapper.Year(date));

    // ───────────────────────── 定位可执行文件 ─────────────────────────

    [Theory]
    [InlineData("journalArticle", "journal-article")]
    [InlineData("preprint", "preprint")]
    [InlineData("bookSection", "book-section")]
    [InlineData("computerProgram", "computer-program")]
    [InlineData("attachment", "attachment")]
    [InlineData("", "document")]
    public void ToSubtype_converts_zoteros_camel_case_to_the_sdk_form(string itemType, string expected)
        => Assert.Equal(expected, ZoteroItemMapper.ToSubtype(itemType));

    [Theory]
    [InlineData("\"D:\\Program\\Zotero\\zotero.exe\" -url \"%1\"", "D:\\Program\\Zotero\\zotero.exe")]
    [InlineData("C:\\Zotero\\zotero.exe -url %1", "C:\\Zotero\\zotero.exe")]
    [InlineData("\"C:\\a b\\zotero.exe\"", "C:\\a b\\zotero.exe")]
    public void ExtractExe_parses_the_registered_command_line(string line, string expected)
        => Assert.Equal(expected, ZoteroLocator.ExtractExe(line));

    // ───────────────────────── 能力声明 ─────────────────────────

    [Fact]
    public void Descriptor_must_not_claim_kind_filter_support()
    {
        // 声明了它，调度器就以为 Zotero 能吃 ext: 过滤 —— 于是 ext:pdf 会白跑一趟 Zotero
        // 再把结果全丢掉。按条目类型筛走的是 Returns* 那些能力位，不靠这一位。
        var p = new ZoteroProvider();
        Assert.False(p.Descriptor.Capabilities.Has(ProviderCapability.SupportsKindFilter));
        Assert.True(p.Descriptor.Capabilities.Has(ProviderCapability.ReturnsBibliographicItems));
        Assert.True(p.Descriptor.Capabilities.Has(ProviderCapability.SearchesMetadata));
    }

    [Fact]
    public void Provider_must_not_implement_directory_scoping()
    {
        // 调度器的规则是"目录内搜索只调度实现了 IFileSystemScopedProvider 的 Provider"，
        // 所以不实现它 = 在 Explorer 目录里搜文件时不会去问 Zotero —— 那正是我们要的。
        var p = new ZoteroProvider();
        Assert.False(p is UniSearch.Sdk.Contracts.IFileSystemScopedProvider);
        Assert.True(p is UniSearch.Sdk.Contracts.IGlobalScopeProvider);
    }
}
