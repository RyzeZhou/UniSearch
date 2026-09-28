using UniSearch.Core;
using UniSearch.Core.Categories;
using UniSearch.Core.Filters;
using UniSearch.Core.Fusion;
using UniSearch.Core.Matching;
using UniSearch.Core.Parsing;
using UniSearch.Core.Ranking;
using UniSearch.Core.Selection;
using UniSearch.Core.Sorting;
using UniSearch.Providers.Everything;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Contracts;
using UniSearch.Sdk.Model;
using Xunit;
using S = UniSearch.Sdk.Model.Canonicalization;

namespace UniSearch.Tests;

/// <summary>
/// 全部是纯逻辑断言 —— 不需要本机安装 Everything / AnyTXT / Zotero。
/// 这一层正确，聚合行为就正确；后端只是把数据搬进来。
/// </summary>
public class CoreLogicTests
{
    const char B = '\u005C';
    static string P(string s) => s.Replace('/', B);

    // ───────────────── 路径规范化 / 实体融合锚点 ─────────────────

    [Theory]
    [InlineData("d:/a/b.TXT", "D:\\a\\b.TXT")]
    [InlineData(@"\\?\D:\a\b.txt", "D:\\a\\b.txt")]
    [InlineData("D:/a/b/", "D:\\a\\b")]
    [InlineData("C:", @"C:\")]
    [InlineData("C:/", "C:\u005C")]
    [InlineData("a/b/", "a\u005Cb")]        // 相对路径：只去尾分隔符
    public void NormalizePath_is_stable(string input, string expected) =>
        Assert.Equal(expected, S.NormalizePath(input));

    [Fact]
    public void Case_and_slash_differences_produce_same_fusion_key()
    {
        var k1 = S.ToFusionKey("everything", "1", P("D:/Papers/paper.pdf"), null);
        var k2 = S.ToFusionKey("anytxt", "x", P("d:/papers/PAPER.PDF"), null);
        var k3 = S.ToFusionKey("zotero", "KEY1", P("D:/papers/paper.pdf"), "zotero://select/items/KEY1");
        Assert.Equal(k1, k2);
        Assert.Equal(k1, k3);                       // 有路径就一律以路径为锚
    }

    [Fact]
    public void No_path_falls_back_to_uri_then_provider_private()
    {
        Assert.StartsWith("u:", S.ToFusionKey("zotero", "K", null, "zotero://x"));
        Assert.StartsWith("x:", S.ToFusionKey("zotero", "K", null, null));
    }

    [Theory]
    [InlineData("D:/Research/AI/x.md", "D:/Research/AI", true)]
    [InlineData("D:/Research/AI", "D:/Research/AI", true)]
    [InlineData("D:/Research/AI/deep/deeper/x.md", "D:/Research/AI", true)]
    [InlineData("D:/Research/Other/x.md", "D:/Research/AI", false)]
    [InlineData("D:/Research/AII/x.md", "D:/Research/AI", false)]   // 前缀相似但不是子目录
    [InlineData("E:/x", "C:/", false)]
    public void IsWithinDirectory_is_prefix_safe(string candidate, string dir, bool expected) =>
        Assert.Equal(expected, S.IsWithinDirectory(candidate, dir));

    [Theory]
    [InlineData("a/b.PDF", "pdf")]
    [InlineData("noext", null)]
    [InlineData(".gitignore", null)]        // 点开头不算扩展名
    [InlineData("dir.tar.gz", "gz")]        // 取最后一个点之后
    public void GetExtension_rules(string path, string? expected) =>
        Assert.Equal(expected, S.GetExtension(path));

    // ───────────────── 查询解析：结构化透传（REF-5 §2.1 的规则） ─────────────────

    [Theory]
    [InlineData("content:transformer")]
    [InlineData("C:\\Users")]
    [InlineData("D:/Research/AI/")]
    [InlineData("*report*")]
    [InlineData("name:foo OR name:bar")]
    [InlineData("!temp")]
    [InlineData("parent:D:\\Research")]
    public void Structured_inputs_are_passed_through(string raw)
    {
        var q = QueryParser.Parse(1, raw);
        Assert.True(q.IsStructured, $"应判定为结构化：{raw}");
        Assert.Equal(raw.Trim(), q.ProviderText);
        Assert.Empty(q.Terms);                       // 不做词项后过滤
    }

    [Theory]
    [InlineData("report")]
    [InlineData("transformer attention")]
    [InlineData("会议纪要 2024")]
    [InlineData("note to self")]
    [InlineData("kind:folder report")]                // 我们自己的过滤器：解析，不透传
    [InlineData("size:>10mb")]
    [InlineData("ext:pdf|docx")]                      // | 用在自家过滤器里仍可解析
    public void Free_text_inputs_are_not_structured(string raw)
    {
        var q = QueryParser.Parse(1, raw);
        Assert.False(q.IsStructured, $"不应判为结构化：{raw}");
    }

    [Fact]
    public void Own_filters_are_normalized_so_every_backend_gets_same_semantics()
    {
        var q = QueryParser.Parse(1, "report ext:pdf kind:doc");
        Assert.False(q.IsStructured);
        Assert.Contains("ext:pdf", q.ProviderText);     // 仍然会带给 Everything（它同样懂 ext:）
        Assert.Contains("report", q.ProviderText);
        Assert.Equal(["report"], q.Terms);              // 词项保留 → Core 能做统一打分与后过滤
        Assert.Equal(["pdf"], q.Filters.Extensions);
        Assert.Equal([ResultKind.Document], q.Filters.Kinds);
    }

    [Fact]
    public void Pipe_inside_own_filter_is_a_list_not_an_or_operator()
    {
        var q = QueryParser.Parse(1, "ext:pdf|docx");
        Assert.False(q.IsStructured);
        Assert.Equal(["pdf", "docx"], q.Filters.Extensions);
    }

    [Fact]
    public void Kind_folder_is_parsed_as_folders_only()
    {
        var q = QueryParser.Parse(1, "kind:folder report");
        Assert.True(q.Filters.FoldersOnly);
        Assert.Equal(["report"], q.Terms);
    }

    [Fact]
    public void Size_filter_is_parsed_into_bytes()
    {
        var q = QueryParser.Parse(1, "size:>10mb");
        Assert.Equal(10L * 1024 * 1024, q.Filters.MinSizeBytes);
    }

    [Fact]
    public void Quoted_phrase_becomes_match_target()
    {
        var q = QueryParser.Parse(1, "Attention \"Is All You Need\"");
        Assert.Equal("Is All You Need", q.Filters.Phrase);
        Assert.Equal("Is All You Need", q.MatchTarget);
    }

    // ───────────────── Everything 翻译：范围限定 ─────────────────

    static readonly SearchContext Recursive = SearchContext.InDirectory(P("D:/Research/AI"), QueryOrigin.ExplorerHotkey);
    static readonly SearchContext LayerOnly = SearchContext.InDirectory(P("D:/Research/AI"), QueryOrigin.ExplorerHotkey, recursive: false);

    [Fact]
    public void CtrlF_recursive_uses_ancestor_function()
    {
        var q = QueryParser.Parse(1, "transformer");
        // 尾部还会追加系统噪声排除子句，所以断言"以 范围+查询 开头"而不是全等
        Assert.StartsWith("ancestor:\"D:\\Research\\AI\\\" transformer", EverythingQueryTranslator.Translate(q, Recursive));
    }

    [Fact]
    public void Layer_only_uses_parent_function()
    {
        var q = QueryParser.Parse(1, "transformer");
        var t = EverythingQueryTranslator.Translate(q, LayerOnly);
        Assert.StartsWith("parent:\"D:\\Research\\AI\\\" transformer", t);
        Assert.DoesNotContain("ancestor:", t);   // 只本层时绝不能退化成递归
    }

    [Fact]
    public void Path_with_space_is_quoted_as_one_term()
    {
        var ctx = SearchContext.InDirectory(P("C:/Program Files/App"), QueryOrigin.ExplorerHotkey);
        var prefix = EverythingQueryTranslator.BuildScopePrefix(ctx);
        Assert.Equal("ancestor:\"C:\\Program Files\\App\\\" ", prefix);
        // 关键：引号内部，路径不能被拆成 AND
        Assert.DoesNotContain("\" C:\\Program\"", prefix);
    }

    [Fact]
    public void Global_context_adds_no_scope()
    {
        var q = QueryParser.Parse(1, "report");
        var t = EverythingQueryTranslator.Translate(q, SearchContext.Global());
        Assert.StartsWith("report ", t);
        Assert.DoesNotContain("ancestor:", t);
        Assert.DoesNotContain("parent:", t);
    }

    [Fact]
    public void Empty_query_in_directory_lists_layer()
    {
        var q = QueryParser.Parse(1, "") with { ListScopeContents = true };
        Assert.StartsWith("ancestor:\"D:\\Research\\AI\\\" file:", EverythingQueryTranslator.Translate(q, Recursive));
    }

    [Fact]
    public void Empty_query_globally_is_not_sent()
    {
        var q = QueryParser.Parse(1, "");
        Assert.Equal(string.Empty, EverythingQueryTranslator.Translate(q, SearchContext.Global()));
    }

    [Fact]
    public void Advanced_query_survives_translation_untouched()
    {
        // 用户写 Everything 高级语法时，我们只加范围前缀、只追加排除子句，绝不重排其内部
        var q = QueryParser.Parse(1, "(ext:pdf | ext:epub) size:>5mb");
        var t = EverythingQueryTranslator.Translate(q, Recursive);
        Assert.StartsWith("ancestor:\"", t);
        Assert.Contains("(ext:pdf | ext:epub) size:>5mb", t);
        Assert.True(t.IndexOf("(ext:pdf", StringComparison.Ordinal) < t.IndexOf("!path:", StringComparison.Ordinal),
                      "排除子句只能追加在用户查询之后");
    }

    // ───────────── 系统噪声排除（回收站 / WinSxS / Windows.old）─────────────

    [Fact]
    public void Noise_exclusion_uses_bang_operator_not_minus()
    {
        var t = EverythingQueryTranslator.Translate(QueryParser.Parse(1, "report"), SearchContext.Global());
        Assert.Contains("!path:\"$RECYCLE.BIN\"", t);
        Assert.Contains("!path:\"C:\\Windows\\WinSxS\"", t);
        // 实测：这台 Everything 把 "-xxx" 当成含连字符的字面文本，不是取反。
        // 写成 -path: 会让查询静默返回 0 条，是最难查的一类 bug。
        Assert.DoesNotContain("-path:", t);
    }

    [Fact]
    public void Searching_inside_a_noise_folder_does_not_exclude_it()
    {
        // 用户 Ctrl+F 打开回收站时，再排除回收站就等于永远空白
        var ctx = SearchContext.InDirectory(P("D:/$RECYCLE.BIN"), QueryOrigin.ExplorerHotkey);
        var t = EverythingQueryTranslator.Translate(QueryParser.Parse(1, "report"), ctx);
        Assert.DoesNotContain("!path:\"$RECYCLE.BIN\"", t);
        Assert.Contains("!path:\"C:\\Windows\\WinSxS\"", t);   // 其余噪声照排
    }

    [Fact]
    public void TypeScript_extension_is_not_classified_as_video()
    {
        // ".ts" 既是 TypeScript 又是 MPEG-TS，而 Kind 判定 Video 在前；
        // 留在 Video 表里会让 pdf-body.client.spec.ts 整批掉进「视频」组。
        Assert.DoesNotContain("ts", EverythingQueryTranslator.Video);
        Assert.Contains("ts", EverythingQueryTranslator.Code);
    }

    // ───────────────── 分类引擎：Provider 不决定分类 ─────────────────

    [Theory]
    [InlineData(ResultKind.Folder, null, CategoryIds.Folders)]
    [InlineData(ResultKind.BibliographicItem, "journal-article", CategoryIds.Literature)]
    [InlineData(ResultKind.Note, "markdown-note", CategoryIds.Notes)]
    [InlineData(ResultKind.Application, null, CategoryIds.Applications)]
    [InlineData(ResultKind.Image, null, CategoryIds.Images)]
    [InlineData(ResultKind.WebPage, null, CategoryIds.Web)]
    public void Kind_decides_category_before_extension(ResultKind kind, string? subtype, string expected)
    {
        var r = new SearchResult { ProviderId = "p", ProviderItemId = "1", Kind = kind, Title = "x", Subtype = subtype, Path = P("D:/a/x.dat") };
        Assert.Equal(expected, new CategoryEngine().Classify(r));
    }

    [Theory]
    [InlineData("x.pdf", CategoryIds.Documents)]
    [InlineData("x.jpg", CategoryIds.Images)]
    [InlineData("x.mp4", CategoryIds.Videos)]
    [InlineData("x.flac", CategoryIds.Music)]
    [InlineData("x.zip", CategoryIds.Archives)]
    [InlineData("x.cs", CategoryIds.Code)]
    [InlineData("x.exe", CategoryIds.Applications)]
    [InlineData("x.unknownext", CategoryIds.Files)]
    public void File_extension_decides_category(string name, string expected)
    {
        var r = new SearchResult { ProviderId = "p", ProviderItemId = name, Kind = ResultKind.File, Title = name, Path = P("D:/a/") + name };
        Assert.Equal(expected, new CategoryEngine().Classify(r));
    }

    [Fact]
    public void Extension_override_wins()
    {
        var engine = new CategoryEngine(new Dictionary<string, string> { ["md"] = CategoryIds.Notes });
        var r = new SearchResult { ProviderId = "p", ProviderItemId = "x", Kind = ResultKind.File, Title = "x.md", Path = P("D:/a/x.md") };
        Assert.Equal(CategoryIds.Notes, engine.Classify(r));
    }

    // ───────────────── 匹配打分 ─────────────────

    [Theory]
    [InlineData("transformer.py", "transformer.py", NameMatcher.Exact)]
    [InlineData("transformer_v2.py", "transformer", NameMatcher.Prefix)]
    [InlineData("my_transformer_notes.md", "transformer", NameMatcher.WordBoundary)]
    [InlineData("attention-is-all-you-need.pdf", "need", NameMatcher.WordBoundary)]  // "-" 是词边界
    [InlineData("transformer.py", "zzz", NameMatcher.NoMatch)]
    public void Name_match_quality_is_ordered(string title, string needle, double expected) =>
        Assert.Equal(expected, NameMatcher.Score(title, needle), 3);

    [Fact]
    public void Multi_term_is_AND_semantics()
    {
        Assert.True(NameMatcher.MatchesAll("deep learning notes", ["learning", "notes"]));
        Assert.False(NameMatcher.MatchesAll("deep learning notes", ["learning", "quantum"]));
    }

    [Fact]
    public void Highlight_wraps_matches() =>
        Assert.Equal("[[trans]]former", NameMatcher.Highlight("transformer", ["trans"]));

    // ───────────────── 融合 ─────────────────

    static SearchResult R(string provider, string kind, string title, string? path, MatchKind match = MatchKind.NameWord, string? snippet = null) => new()
    {
        ProviderId = provider,
        ProviderItemId = path ?? title,
        Kind = kind == "folder" ? ResultKind.Folder : kind == "bib" ? ResultKind.BibliographicItem : ResultKind.File,
        Title = title,
        Path = path is null ? null : P(path),
        Match = match,
        Snippet = snippet,
        ContentMatchCount = match.HasFlag(MatchKind.Content) ? 12 : null,
    };

    [Fact]
    public void Same_path_across_three_providers_becomes_one_row()
    {
        var store = new FusionStore();
        store.Add(R("everything", "file", "paper.pdf", "D:/Papers/paper.pdf"));
        store.Add(R("anytxt", "file", "paper.pdf", "D:/papers/PAPER.pdf", MatchKind.Content, "…[[transformer]] attention…"));
        store.Add(R("zotero", "bib", "Attention Is All You Need", "D:/Papers/paper.pdf"));

        Assert.Equal(1, store.Count);
        var fused = store.Items.Single();
        // 文献条目有资格决定标题（AuthorityOf）
        Assert.Equal("Attention Is All You Need", fused.Display.Title);
        // 但路径信息仍在（被文件系统贡献补齐）
        Assert.Equal(P("D:/Papers/paper.pdf"), fused.Display.Path);
        Assert.Equal(3, fused.Contributions.Count);
        Assert.True(fused.NameMatched);
        Assert.True(fused.ContentMatched);
        Assert.Equal(12, fused.ContentMatchCount);
        var tags = fused.BuildTags().Select(t => t.Label).ToList();
        Assert.Contains("anytxt", tags);
        Assert.Contains("zotero", tags);
        Assert.Contains(tags, t => t.Contains("正文"));
    }

    [Fact]
    public void Content_only_hit_lands_in_content_category()
    {
        var store = new FusionStore();
        store.Add(R("anytxt", "file", "notes.docx", "D:/x/notes.docx", MatchKind.Content, "…transformer…"));
        var fused = store.Items.Single();
        Assert.Equal(CategoryIds.ContentMatches, fused.CategoryId);
    }

    [Fact]
    public void Name_hit_corrects_category_back_to_entity()
    {
        var store = new FusionStore();
        store.Add(R("anytxt", "file", "notes.docx", "D:/x/notes.docx", MatchKind.Content, "…transformer…"));
        store.Add(R("everything", "file", "notes.docx", "D:/x/notes.docx"));
        Assert.Equal(CategoryIds.Documents, store.Items.Single().CategoryId);
    }

    [Fact]
    public void Distinct_paths_are_not_merged()
    {
        var store = new FusionStore();
        store.Add(R("everything", "file", "a.pdf", "D:/x/a.pdf"));
        store.Add(R("everything", "file", "b.pdf", "D:/x/b.pdf"));
        Assert.Equal(2, store.Count);
    }

    // ───────────────── 排序 ─────────────────

    static FusedResult One(string title, string path, MatchKind m = MatchKind.NameWord, DateTimeOffset? mod = null)
    {
        var store = new FusionStore();
        store.Add(new SearchResult { ProviderId = "everything", ProviderItemId = path, Kind = ResultKind.File, Title = title, Path = P(path), Match = m, ModifiedAt = mod });
        return store.Items.Single();
    }

    [Fact]
    public void Exact_beats_prefix_beats_substring()
    {
        var ranker = new Ranker();
        var ctx = SearchContext.Global();
        var q = QueryParser.Parse(1, "transformer");
        var exact = ranker.Score(One("transformer", "D:/a/transformer"), q, ctx, 100);
        var prefix = ranker.Score(One("transformer_v2", "D:/a/transformer_v2"), q, ctx, 100);
        var sub = ranker.Score(One("my transformer notes", "D:/a/my transformer notes"), q, ctx, 100);
        Assert.True(exact > prefix, $"exact {exact} 应 > prefix {prefix}");
        Assert.True(prefix > sub, $"prefix {prefix} 应 > substring {sub}");
    }

    [Fact]
    public void Nearer_directory_wins_within_scope()
    {
        var ranker = new Ranker();
        var ctx = SearchContext.InDirectory(P("D:/R"), QueryOrigin.ExplorerHotkey);
        var q = QueryParser.Parse(1, "x");
        var near = ranker.Score(One("x1", "D:/R/x1"), q, ctx, 100);
        var far = ranker.Score(One("x1", "D:/R/a/b/c/x1"), q, ctx, 100);
        Assert.True(near > far);
    }

    [Fact]
    public void Usage_is_query_scoped_not_just_global()
    {
        var dir = Path.Combine(Path.GetTempPath(), "unisearch-usage-" + Guid.NewGuid().ToString("N"));
        var store = new UniSearch.Core.Usage.FileUsageStore(dir);
        store.Record(P("D:/a/chrome.pdf"), "chrome", "everything", "open");
        store.Record(P("D:/a/chrome.pdf"), "chrome", "everything", "open");

        var withQuery = store.GetWeight(P("D:/a/chrome.pdf"), "chrome");
        var otherQuery = store.GetWeight(P("D:/a/chrome.pdf"), "unrelated");
        Assert.True(withQuery > otherQuery, "同查询命中应显著更高");
        Assert.True(withQuery / otherQuery >= 5, $"倍数应体现查询维度权重，实际 {withQuery / otherQuery:F2}");
        Directory.Delete(dir, true);
    }

    // ───────────────── 调度：能力匹配而不是广播 ─────────────────

    class FakeProvider : ISearchProvider, IGlobalScopeProvider
    {
        public FakeProvider(string id, ProviderCapability caps, int priority = 50) =>
            Descriptor = new ProviderDescriptor { Id = id, DisplayName = id, Capabilities = caps, Priority = priority };
        public ProviderDescriptor Descriptor { get; }
        public IAsyncEnumerable<SearchBatch> SearchAsync(SearchQuery q, SearchContext c, CancellationToken t)
            => EmptyBatches();
        static async IAsyncEnumerable<SearchBatch> EmptyBatches() { await Task.CompletedTask; yield break; }
    }

    sealed class ScopedProvider : FakeProvider, IFileSystemScopedProvider
    {
        public ScopedProvider(string id, ProviderCapability caps, int priority = 50) : base(id, caps, priority) { }
        public string? TranslateScope(SearchContext c) => c.RootPath;
    }

    static readonly ProviderCapability FileCaps = ProviderCapability.ReturnsFiles | ProviderCapability.SearchesFileName |
                                                  ProviderCapability.SupportsGlobalScope | ProviderCapability.SupportsDirectoryScope;

    [Fact]
    public void Explorer_context_skips_providers_that_cannot_scope()
    {
        var everything = new ScopedProvider("everything", FileCaps, 100);
        var zotero = new FakeProvider("zotero", FileCaps | ProviderCapability.ReturnsBibliographicItems, 60);
        var sel = ProviderSelector.Select(
            [new ProviderEntry(everything, everything.Descriptor), new ProviderEntry(zotero, zotero.Descriptor)],
            Recursive, QueryParser.Parse(1, "transformer"));

        Assert.Single(sel.Selected);
        Assert.Equal("everything", sel.Selected[0].Descriptor.Id);
        Assert.Equal(SkipReason.CannotScopeToDirectory, sel.Skipped[0].Reason);
    }

    [Fact]
    public void Global_context_schedules_both()
    {
        var everything = new ScopedProvider("everything", FileCaps, 100);
        var zotero = new FakeProvider("zotero", FileCaps, 60);
        var sel = ProviderSelector.Select(
            [new ProviderEntry(zotero, zotero.Descriptor), new ProviderEntry(everything, everything.Descriptor)],
            SearchContext.Global(), QueryParser.Parse(1, "transformer"));
        Assert.Equal(2, sel.Selected.Count);
        Assert.Equal("everything", sel.Selected[0].Descriptor.Id);   // 按优先级降序
    }

    [Fact]
    public void Kind_filter_excludes_providers_that_cannot_produce_it()
    {
        var everything = new ScopedProvider("everything", FileCaps, 100);
        var zotero = new FakeProvider("zotero", ProviderCapability.ReturnsBibliographicItems | ProviderCapability.SupportsGlobalScope, 60);
        var q = QueryParser.Parse(1, "kind:pdf x");
        Assert.Equal(["pdf"], q.Filters.Extensions);   // kind:pdf → 扩展名过滤
        var sel = ProviderSelector.Select(
            [new ProviderEntry(everything, everything.Descriptor), new ProviderEntry(zotero, zotero.Descriptor)],
            SearchContext.Global(), q);
        Assert.Equal(["everything"], sel.Selected.Select(s => s.Descriptor.Id));
    }

    // ───────────────── 来源限定（左侧来源栏）─────────────────
    // 语义是"只搜它"，不是"只显示它的结果"：没被选中的后端必须真的不进调度，
    // 否则将来接上 AnyTXT 这类有真实开销的后端时，"没搜"会被误读成"没结果"。

    [Fact]
    public void Provider_scope_keeps_only_the_selected_provider()
    {
        var everything = new ScopedProvider("everything", FileCaps, 100);
        var zotero = new FakeProvider("zotero", FileCaps | ProviderCapability.ReturnsBibliographicItems, 60);
        var q = QueryParser.Parse(1, "transformer") with { ProviderScope = ["everything"] };

        var sel = ProviderSelector.Select(
            [new ProviderEntry(everything, everything.Descriptor), new ProviderEntry(zotero, zotero.Descriptor)],
            SearchContext.Global(), q);

        Assert.Equal(["everything"], sel.Selected.Select(s => s.Descriptor.Id));
        Assert.Equal(SkipReason.NotInScope, Assert.Single(sel.Skipped).Reason);
    }

    [Fact]
    public void Provider_scope_matches_id_case_insensitively_and_empty_scope_means_all()
    {
        var everything = new ScopedProvider("everything", FileCaps, 100);
        var zotero = new FakeProvider("zotero", FileCaps, 60);
        var entries = new[] { new ProviderEntry(everything, everything.Descriptor), new ProviderEntry(zotero, zotero.Descriptor) };

        // 配置里写成 Everything（手改 settings.json 很容易这样），不该因此搜不到
        var upper = QueryParser.Parse(1, "x") with { ProviderScope = ["Everything"] };
        Assert.Equal(["everything"], ProviderSelector.Select(entries, SearchContext.Global(), upper).Selected.Select(s => s.Descriptor.Id));

        // 空 scope = 不限制
        var empty = QueryParser.Parse(1, "x") with { ProviderScope = [] };
        Assert.False(empty.HasProviderScope);
        Assert.Equal(2, ProviderSelector.Select(entries, SearchContext.Global(), empty).Selected.Count);
    }

    // ───────────────── 列排序（详细列表）─────────────────

    static FusedResult Fused(string title, long? size = null, string? path = null,
                             DateTimeOffset? modified = null, bool folder = false, double score = 0)
    {
        var r = new SearchResult
        {
            ProviderId = "everything",
            ProviderItemId = path ?? title,
            Kind = folder ? ResultKind.Folder : ResultKind.File,
            Title = title,
            Path = path ?? P($"D:/t/{title}"),
            SizeBytes = size,
            ModifiedAt = modified,
        };
        var f = new FusedResult { FusionKey = r.FusionKey, Display = r, Score = score };
        f.Contributions["everything"] = r;
        if (modified is not null) f.ModifiedAt = modified;
        return f;
    }

    [Fact]
    public void Sort_by_size_descending_puts_the_largest_first()
    {
        List<FusedResult> items =
        [
            Fused("small.bin", 10),
            Fused("big.bin", 5_000_000),
            Fused("mid.bin", 2048),
        ];
        ResultSort.Sort(items, ResultSortKey.Size, descending: true);
        Assert.Equal(["big.bin", "mid.bin", "small.bin"], items.Select(i => i.Display.Title));
    }

    [Fact]
    public void Folders_have_no_size_and_cluster_at_the_small_end()
    {
        List<FusedResult> items =
        [
            Fused("a.bin", 100),
            Fused("folder", folder: true),
            Fused("b.bin", 1),
        ];
        ResultSort.Sort(items, ResultSortKey.Size, descending: false);
        Assert.Equal("folder", items[0].Display.Title);        // -1 排最前（升序）
        ResultSort.Sort(items, ResultSortKey.Size, descending: true);
        Assert.Equal("folder", items[^1].Display.Title);       // 降序时排最后
    }

    [Fact]
    public void Ties_fall_back_to_name_so_the_order_never_jitters()
    {
        List<FusedResult> items =
        [
            Fused("c.bin", 100),
            Fused("a.bin", 100),
            Fused("b.bin", 100),
        ];
        ResultSort.Sort(items, ResultSortKey.Size, descending: true);
        // 主键全相等 → 用名称兜底。没有这一级，同样的结果集每次刷新次序都可能不同（列表看起来在乱跳）
        Assert.Equal(["a.bin", "b.bin", "c.bin"], items.Select(i => i.Display.Title));
    }

    [Fact]
    public void Name_sort_is_number_aware()
    {
        List<FusedResult> items = [Fused("file10.txt"), Fused("file2.txt"), Fused("file1.txt")];
        ResultSort.Sort(items, ResultSortKey.Name, descending: false);
        // 纯 Ordinal 比较会给出 1,10,2 —— 在文件名里这几乎总是错的
        Assert.Equal(["file1.txt", "file2.txt", "file10.txt"], items.Select(i => i.Display.Title));
    }

    [Fact]
    public void Modified_sort_uses_the_fused_time()
    {
        var older = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        // Display 上的时间旧、融合时间新（多后端证实时取最新）——排序必须用后者
        var f = Fused("mixed.txt", modified: older);
        f.ModifiedAt = newer;

        List<FusedResult> items = [f, Fused("plain.txt", modified: older)];
        ResultSort.Sort(items, ResultSortKey.Modified, descending: true);
        Assert.Equal("mixed.txt", items[0].Display.Title);
    }

    [Fact]
    public void Sort_key_names_round_trip_through_config()
    {
        foreach (var key in Enum.GetValues<ResultSortKey>())
        {
            var name = ResultSort.ToConfigName(key);
            Assert.True(ResultSort.TryParse(name, out var back), $"配置名 {name} 应当能解析回来");
            Assert.Equal(key, back);
        }
        // 手改配置写错时的兜底：不抛异常，退回名称排序
        Assert.False(ResultSort.TryParse("no-such-column", out var fallback));
        Assert.Equal(ResultSortKey.Name, fallback);
    }

    [Fact]
    public void Text_columns_start_ascending_while_size_and_time_start_descending()
    {
        Assert.False(ResultSort.DefaultDescending(ResultSortKey.Name));
        Assert.False(ResultSort.DefaultDescending(ResultSortKey.Path));
        Assert.True(ResultSort.DefaultDescending(ResultSortKey.Size));
        Assert.True(ResultSort.DefaultDescending(ResultSortKey.Modified));
    }

    // ───────────────── 筛选器定义（filters.json）─────────────────
    // 这套东西是"给用户自己扩展"的入口，所以它必须：读得进手写的 JSON、
    // 写错了要报出来（而不是静默丢掉整份文件）、规范化得够宽容（.PDB / pdb / PDB 都该能用）。

    static string WriteTempJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"unisearch-filters-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    static SearchResult FileRow(string title, string path) => new()
    {
        ProviderId = "everything",
        ProviderItemId = path,
        Kind = ResultKind.File,
        Title = title,
        Path = P($"D:/t/{path}"),
    };

    [Fact]
    public void Filter_catalog_normalizes_extensions_ids_and_kinds()
    {
        var path = WriteTempJson("""
        {
          // 手改文件里留注释和尾随逗号都不该炸
          "filters": [
            {
              "id": "  Bio-Info  ",
              "name": "生信相关",
              "extensions": [".PDB", "cif", "pdb", "  ", "Fasta"],
              "kinds": ["file"],
            }
          ]
        }
        """);

        var cat = FilterCatalog.Load(path);
        var f = Assert.Single(cat.All);
        Assert.Equal("bio-info", f.Id);                              // 去空格 + 小写
        Assert.Equal(["pdb", "cif", "fasta"], f.Extensions);         // 去点、去空、去重、保序
        Assert.Equal(["File"], f.Kinds);                             // 类型名按枚举规范化
        Assert.Empty(cat.Problems);
        File.Delete(path);
    }

    [Fact]
    public void Filter_catalog_later_file_overrides_the_same_id()
    {
        var template = WriteTempJson("""{ "filters": [ { "id": "bio", "name": "模板版", "extensions": ["pdb"] } ] }""");
        var mine = WriteTempJson("""{ "filters": [ { "id": "bio", "name": "我的版", "extensions": ["fasta"] } ] }""");

        var cat = FilterCatalog.Load(template, mine);   // 后面的覆盖前面的
        var f = Assert.Single(cat.All);
        Assert.Equal("我的版", f.Name);
        Assert.Equal(["fasta"], f.Extensions);
        Assert.Equal(2, cat.Sources.Count);
        File.Delete(template);
        File.Delete(mine);
    }

    [Fact]
    public void Filter_catalog_reports_problems_instead_of_throwing()
    {
        var broken = WriteTempJson("{ this is not json");
        var partial = WriteTempJson("""
        {
          "filters": [
            { "id": "empty-one", "name": "没有条件" },
            { "id": "bad-kind", "name": "类型写错", "kinds": ["NoSuchKind"] },
            { "id": "bad-regex", "name": "正则写错", "namePattern": "([unclosed" },
            { "id": "good", "name": "好的", "extensions": ["pdb"] }
          ]
        }
        """);

        var cat = FilterCatalog.Load(broken, partial);
        Assert.Equal(["good"], cat.All.Select(f => f.Id));       // 坏的被剔除，好的留下
        // 4 条：坏 JSON 一条 + 三条各自的问题各一条。写错类型名/正则之后不会再额外来一条"没有匹配条件"
        // —— 后者是前者的后果，重复报会让人以为有两处错误。
        Assert.True(cat.Problems.Count == 4, $"期望 4 条问题，实际 {cat.Problems.Count} 条：{string.Join(" | ", cat.Problems)}");
        Assert.Single(cat.Problems, p => p.Contains("bad-kind"));
        Assert.Single(cat.Problems, p => p.Contains("正则写错"));
        Assert.Contains(cat.Problems, p => p.Contains("解析失败"));
        Assert.Contains(cat.Problems, p => p.Contains("没有任何匹配条件"));
        Assert.Contains(cat.Problems, p => p.Contains("NoSuchKind"));
        File.Delete(broken);
        File.Delete(partial);
    }

    [Fact]
    public void Filter_matches_by_extension_kind_or_name_pattern()
    {
        var path = WriteTempJson("""
        {
          "filters": [
            { "id": "bio", "name": "生信", "extensions": ["pdb", "fasta"] },
            { "id": "dirs", "name": "目录", "kinds": ["Folder"] },
            { "id": "logs", "name": "日志", "namePattern": "^log-\\d+\\.txt$" }
          ]
        }
        """);

        var cat = FilterCatalog.Load(path);
        var bio = cat.Find("bio")!;
        var dirs = cat.Find("dirs")!;
        var logs = cat.Find("logs")!;

        Assert.True(bio.Matches(FileRow("x.pdb", "x.pdb")));
        Assert.True(bio.Matches(FileRow("x.PDB", "x.PDB")));        // 扩展名大小写不敏感
        Assert.False(bio.Matches(FileRow("x.txt", "x.txt")));

        var folder = new SearchResult
        {
            ProviderId = "everything", ProviderItemId = "d", Kind = ResultKind.Folder,
            Title = "src", Path = P("D:/t/src"),
        };
        Assert.True(dirs.Matches(folder));
        Assert.False(dirs.Matches(FileRow("x.pdb", "x.pdb")));

        Assert.True(logs.Matches(FileRow("log-42.txt", "log-42.txt")));
        Assert.False(logs.Matches(FileRow("mylog-42.txt", "mylog-42.txt")));
        File.Delete(path);
    }

    [Fact]
    public void Filter_provider_scope_limits_visibility()
    {
        var path = WriteTempJson("""
        {
          "filters": [
            { "id": "anywhere", "name": "到处都有", "extensions": ["pdb"] },
            { "id": "only-anytxt", "name": "只给全文后端", "extensions": ["txt"], "providers": ["anytxt"] },
            { "id": "hidden-alias", "name": "别名", "extensions": ["md"], "hidden": true }
          ]
        }
        """);

        var cat = FilterCatalog.Load(path);
        Assert.Equal(["anywhere"], cat.For(["everything"]).Select(f => f.Id));       // hidden 不出现
        Assert.Equal(["anywhere", "only-anytxt"], cat.For(["anytxt"]).Select(f => f.Id));
        Assert.Equal(["anywhere", "only-anytxt"], cat.For((IReadOnlyList<string>?)null).Select(f => f.Id)); // 没选来源时给全量
        File.Delete(path);
    }

    [Fact]
    public void Filter_catalog_ignores_missing_files()
    {
        var cat = FilterCatalog.Load(Path.Combine(Path.GetTempPath(), "definitely-not-here.json"), null);
        Assert.Empty(cat.All);
        Assert.Empty(cat.Sources);
        Assert.Empty(cat.Problems);   // 文件不存在是常态（首次运行），不是错误
    }

    // ── 筛选器模板（v2）────────────────────────────────────────────────────────
    // 模板只改"标签栏怎么组织"，不改匹配语义；最要紧的一条是：**没写 templates 节时行为与现状一致**。

    [Fact]
    public void Filter_catalog_without_templates_keeps_flat_behaviour()
    {
        var path = WriteTempJson("""
        {
          "filters": [
            { "id": "bio", "name": "生信", "extensions": ["pdb"] },
            { "id": "only-anytxt", "name": "只给全文后端", "extensions": ["txt"], "providers": ["anytxt"] }
          ]
        }
        """);

        var cat = FilterCatalog.Load(path);
        Assert.Empty(cat.Templates);                                     // 没写 = 一个模板都没有
        Assert.Null(cat.ResolveTemplate("everything"));
        Assert.Null(cat.ResolveTemplate("everything", "files", "literature"));   // 节都没有，钉住也无从谈起
        Assert.Null(cat.ResolveTemplate(null));
        Assert.Equal(cat.For(["everything"]).Select(f => f.Id), cat.ForTemplate(null, ["everything"]).Select(f => f.Id));
        Assert.Equal(cat.For(null).Select(f => f.Id), cat.ForTemplate(null, null).Select(f => f.Id));
        Assert.Equal(["bio"], cat.For(["everything"]).Select(f => f.Id));
        File.Delete(path);
    }

    [Fact]
    public void Filter_templates_merge_independently_and_later_file_wins()
    {
        var program = WriteTempJson("""
        {
          "filters": [ { "id": "bio", "name": "生信", "extensions": ["pdb"] } ],
          "templates": [ { "id": "files", "name": "文件查找", "filters": ["bio"], "defaultFor": ["everything"] } ]
        }
        """);
        var mine = WriteTempJson("""
        { "templates": [ { "id": "files", "name": "我的文件查找", "order": 10, "filters": [] } ] }
        """);

        var cat = FilterCatalog.Load(program, mine);
        var t = Assert.Single(cat.Templates);
        Assert.Equal("我的文件查找", t.Name);           // 同 id 后者为准
        Assert.Empty(t.Filters);
        Assert.Empty(t.DefaultFor);                     // 覆盖是整条替换，不做字段级合并
        Assert.Equal(10, t.Order);
        // filters 与 templates 各自独立合并：用户文件里没写 filters，程序模板那条定义照样在
        Assert.Equal(["bio"], cat.All.Select(f => f.Id));
        File.Delete(program);
        File.Delete(mine);
    }

    [Fact]
    public void Filter_templates_drop_unknown_references_with_a_problem()
    {
        var path = WriteTempJson("""
        {
          "filters": [ { "id": "Bio Info", "name": "生信", "extensions": ["pdb"] } ],
          "templates": [
            { "id": "files", "name": "文件查找", "filters": ["Bio Info", "ghost", "ghost"] }
          ]
        }
        """);

        var cat = FilterCatalog.Load(path);
        // 引用按筛选器那套规范化（"Bio Info" → "bio-info"）才对得上；不存在的剔除、重复的去掉
        Assert.Equal(["bio-info"], Assert.Single(cat.Templates).Filters);
        Assert.Single(cat.Problems, p => p.Contains("ghost"));
        File.Delete(path);
    }

    [Fact]
    public void Filter_templates_default_conflict_resolves_by_order_and_is_reported()
    {
        var path = WriteTempJson("""
        {
          "filters": [ { "id": "bio", "name": "生信", "extensions": ["pdb"] } ],
          "templates": [
            { "id": "b", "name": "乙", "order": 200, "filters": ["bio"], "defaultFor": ["Everything"] },
            { "id": "a", "name": "甲", "order": 100, "filters": ["bio"], "defaultFor": ["everything"] }
          ]
        }
        """);

        var cat = FilterCatalog.Load(path);
        Assert.Equal(["a", "b"], cat.Templates.Select(t => t.Id));        // 按 order 排
        Assert.Equal("a", cat.DefaultTemplate("everything")!.Id);          // order 小者胜
        Assert.Equal("a", cat.DefaultTemplate("EVERYTHING")!.Id);          // 后端 id 大小写不敏感
        Assert.Single(cat.Problems, p => p.Contains("默认模板"));
        File.Delete(path);
    }

    [Fact]
    public void Filter_templates_resolution_chain_is_pinned_then_deployment_then_default_then_star()
    {
        var path = WriteTempJson("""
        {
          "filters": [ { "id": "bio", "name": "生信", "extensions": ["pdb"] } ],
          "templates": [
            { "id": "files",   "name": "文件查找", "order": 100, "filters": ["bio"], "defaultFor": ["everything"] },
            { "id": "lit",     "name": "文献查找", "order": 110, "filters": ["bio"], "defaultFor": ["zotero"] },
            { "id": "minimal", "name": "极简",     "order": 900, "filters": [],    "defaultFor": ["*"] }
          ]
        }
        """);

        var cat = FilterCatalog.Load(path);
        Assert.Equal("files", cat.ResolveTemplate("everything")!.Id);        // 3. defaultFor 认领
        Assert.Equal("lit", cat.ResolveTemplate("zotero")!.Id);
        Assert.Equal("minimal", cat.ResolveTemplate("anytxt")!.Id);          // 4. "*" 兜底
        Assert.Equal("minimal", cat.ResolveTemplate(null)!.Id);              // 全局视图也走 "*"
        Assert.Equal("lit", cat.ResolveTemplate("everything", pinnedTemplateId: "lit")!.Id);        // 1. 钉住
        Assert.Equal("lit", cat.ResolveTemplate("everything", deploymentTemplateId: "lit")!.Id);    // 2. 部署级
        Assert.Equal("lit", cat.ResolveTemplate("everything", "lit", "files")!.Id);                 // 钉住压过部署级
        Assert.Equal("files", cat.ResolveTemplate("everything", "nope")!.Id);  // 认不出的 id 不生效，往下一步走
        File.Delete(path);
    }

    [Fact]
    public void Filter_templates_intersect_with_provider_scope_and_keep_reference_order()
    {
        var path = WriteTempJson("""
        {
          "filters": [
            { "id": "bio", "name": "生信", "extensions": ["pdb"], "order": 900 },
            { "id": "only-anytxt", "name": "仅正文命中", "extensions": ["txt"], "providers": ["anytxt"], "order": 100 },
            { "id": "alias", "name": "别名", "extensions": ["md"], "hidden": true, "order": 200 }
          ],
          "templates": [
            { "id": "mix", "name": "混合", "order": 100, "filters": ["only-anytxt", "alias", "bio"] },
            { "id": "lit", "name": "文献", "order": 200, "filters": ["bio"], "providers": ["zotero"] }
          ]
        }
        """);

        var cat = FilterCatalog.Load(path);
        var mix = cat.FindTemplate("mix")!;

        // 顺序以模板里的引用先后为准（only-anytxt 的 order=100 但 bio 的 order=900，这里按引用排）
        Assert.Equal(["only-anytxt", "bio"], cat.ForTemplate(mix, ["anytxt"]).Select(f => f.Id));
        // 双重显隐：Everything 下 only-anytxt 不适用、alias 是别名 → 只剩 bio
        Assert.Equal(["bio"], cat.ForTemplate(mix, ["everything"]).Select(f => f.Id));
        // 还不知道来源时给模板内全量（hidden 仍然不显示）
        Assert.Equal(["only-anytxt", "bio"], cat.ForTemplate(mix, null).Select(f => f.Id));
        // 模板可选性：lit 只认 zotero，Everything 下不该出现在下拉里
        Assert.Equal(["mix"], cat.TemplatesFor("everything").Select(t => t.Id));
        Assert.Equal(["mix", "lit"], cat.TemplatesFor("zotero").Select(t => t.Id));
        Assert.Equal(["mix", "lit"], cat.TemplatesFor(null).Select(t => t.Id));
        Assert.Null(cat.FindTemplate("nope"));
        File.Delete(path);
    }
}
