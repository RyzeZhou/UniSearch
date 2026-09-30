using System.Runtime.CompilerServices;
using System.Text.Json;
using UniSearch.Core.Broker;
using UniSearch.Core.Filters;
using UniSearch.Core.Selection;
using UniSearch.Providers.Anytxt;
using UniSearch.Providers.Siyuan;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Contracts;
using UniSearch.Sdk.Model;
using Xunit;

namespace UniSearch.Tests;

/// <summary>
/// 这一轮三个改动的脱机测试：
/// <list type="bullet">
/// <item>筛选器<b>热重载判定</b>（FilterCatalogReloader）—— 坏文件要被拒，不能静默变残；</item>
/// <item>Broker 的<b>多批记账</b> —— 第二批重发已见过的行，"N 条"不能翻倍；</item>
/// <item>AnyTXT <b>片段标记解析</b> 与思源<b>预览截断</b>（两个纯函数）。
/// </list>
/// 真联调另有 --selftest-filters / --selftest-anytxt / --selftest-siyuan。
/// </summary>
public class HotReloadAndTwoStageTests
{
    // ───────────────────────── 热重载判定 ─────────────────────────

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "unisearch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    const string OneFilterJson = """{"version":1,"filters":[{"id":"t1","name":"T1","extensions":["xyz"]}]}""";

    [Fact]
    public void Reload_accepts_when_every_configured_file_still_parses()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "filters.json");
        File.WriteAllText(path, OneFilterJson);

        var current = FilterCatalog.Load(path);
        var result = new FilterCatalogReloader(path).Reload(current);

        Assert.Equal(FilterReloadOutcome.Accepted, result.Outcome);
        Assert.Equal(1, result.Catalog.All.Count);
    }

    [Fact]
    public void Reload_rejects_broken_file_and_keeps_previous()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "filters.json");
        File.WriteAllText(path, OneFilterJson);
        var current = FilterCatalog.Load(path);

        // 用户保存到一半 / 写坏了 JSON：Load 不抛异常，只是悄悄跳过 —— 判定器必须把它揪出来
        File.WriteAllText(path, "{\"version\":1,\"filters\":[ }");
        var result = new FilterCatalogReloader(path).Reload(current);

        Assert.Equal(FilterReloadOutcome.RejectedFileBroken, result.Outcome);
        Assert.Equal(1, result.Catalog.All.Count);   // 还是上一版
    }

    [Fact]
    public void Reload_rejects_when_definitions_vanish()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "filters.json");
        File.WriteAllText(path, OneFilterJson);
        var current = FilterCatalog.Load(path);

        // 合法 JSON、但筛选器被清空 —— 大概率也是误操作，不给它静默生效
        File.WriteAllText(path, """{"version":1,"filters":[]}""");
        var result = new FilterCatalogReloader(path).Reload(current);

        Assert.Equal(FilterReloadOutcome.RejectedEmpty, result.Outcome);
        Assert.Equal(1, result.Catalog.All.Count);
    }

    [Fact]
    public void Missing_user_file_is_not_a_failure()
    {
        var dir = TempDir();
        var builtin = Path.Combine(dir, "filters.json");
        File.WriteAllText(builtin, OneFilterJson);
        var user = Path.Combine(dir, "user", "filters.json");   // 目录都不存在 = 首次运行形态

        var current = FilterCatalog.Load(builtin, user);
        var result = new FilterCatalogReloader(builtin, user).Reload(current);

        Assert.Equal(FilterReloadOutcome.Accepted, result.Outcome);
    }

    // ───────────────────────── Broker 多批记账 ─────────────────────────

    sealed class TwoBatchProvider : ISearchProvider, IGlobalScopeProvider
    {
        public ProviderDescriptor Descriptor { get; } = new()
        {
            Id = "twobatch",
            DisplayName = "TwoBatch",
            Priority = 10,
            Capabilities = ProviderCapability.ReturnsFiles | ProviderCapability.SupportsGlobalScope,
        };

        public async IAsyncEnumerable<SearchBatch> SearchAsync(
            SearchQuery query, SearchContext context, [EnumeratorCancellation] CancellationToken ct)
        {
            var a = Row("a");
            yield return SearchBatch.Of(Descriptor.Id, query.RequestId, [a, Row("b")], 2, isLast: false);
            await Task.Yield();
            // 第二批：a 重发（补上了 Snippet）+ 新行 c —— FusionStore 会原地升级，记账必须去重
            yield return SearchBatch.Of(Descriptor.Id, query.RequestId,
                [a with { Snippet = "正文 [[a]]" }, Row("c")], 3, isLast: true);
        }

        static SearchResult Row(string id) => new()
        {
            ProviderId = "twobatch",
            ProviderItemId = id,
            Kind = ResultKind.File,
            Title = id,
            Path = @"C:\" + id,
            Match = MatchKind.Content,
        };
    }

    [Fact]
    public async Task Broker_counts_rows_by_fusion_key_across_batches()
    {
        var provider = new TwoBatchProvider();
        var broker = new SearchBroker([new ProviderEntry(provider, provider.Descriptor)]);
        var query = new SearchQuery
        {
            RequestId = 1,
            RawText = "a",
            Text = "a",
            ProviderText = "a",
            Terms = ["a"],
            Filters = QueryFilters.None,
            ResultBudget = 10,
        };

        SearchSnapshot? final = null;
        await foreach (var snap in broker.RunAsync(query, SearchContext.Global(), CancellationToken.None))
            final = snap;

        Assert.NotNull(final);
        var outcome = Assert.Single(final!.Outcomes, o => o.ProviderId == "twobatch");
        // 三行就是三行：a 重发不能把 Returned/Kept 顶成 5
        Assert.Equal(3, outcome.Returned);
        Assert.Equal(3, outcome.Kept);
        Assert.Equal(3, final.TotalFused);
        // 第二批的 Snippet 要能升级到已有行上（FusionStore 原地合并的证据）
        Assert.Contains(final.Groups.SelectMany(g => g.Items), f => f.Snippet is not null);
    }

    // ───────────────────────── AnyTXT 片段解析 ─────────────────────────

    [Fact]
    public void ParseFragment_converts_markers_and_joins_segments()
    {
        var output = JsonSerializer.SerializeToElement(new
        {
            text = new[] { "前文 *<<*半导体*>>* 后文", "第二段 *<<*工艺*>>*" },
            count = 2,
        });

        var snippet = AnytxtProvider.ParseFragment(output);

        Assert.NotNull(snippet);
        Assert.Contains("[[半导体]]", snippet);
        Assert.Contains("[[工艺]]", snippet);
        Assert.DoesNotContain("*<<*", snippet);
        Assert.Contains(" … ", snippet);   // 多段用统一分隔
    }

    [Fact]
    public void ParseFragment_accepts_single_string_form()
    {
        var output = JsonSerializer.SerializeToElement(new { text = "plain *<<*hit*>>* text" });
        Assert.Equal("plain [[hit]] text", AnytxtProvider.ParseFragment(output));
    }

    [Fact]
    public void ParseFragment_returns_null_without_text()
    {
        Assert.Null(AnytxtProvider.ParseFragment(JsonSerializer.SerializeToElement(new { errno = 0 })));
        Assert.Null(AnytxtProvider.ParseFragment(default));
    }

    [Fact]
    public void ParseFragment_caps_each_segment()
    {
        var longText = new string('x', 500);
        var output = JsonSerializer.SerializeToElement(new { text = longText });

        var snippet = AnytxtProvider.ParseFragment(output);

        Assert.NotNull(snippet);
        Assert.True(snippet!.Length <= 261);   // 260 + 省略号
        Assert.EndsWith("…", snippet);
    }

    // ───────────────────────── 思源预览截断 ─────────────────────────

    [Fact]
    public void Siyuan_preview_passes_short_content_through()
    {
        var content = "# 标题\n\n正文";
        Assert.Equal(content, SiYuanProvider.TruncateForPreview(content));
    }

    [Fact]
    public void Siyuan_preview_truncates_long_content_with_a_pointer_to_export()
    {
        var content = new string('x', 9000);

        var preview = SiYuanProvider.TruncateForPreview(content);

        Assert.EndsWith("）", preview);
        Assert.Contains("导出 Markdown", preview);
        Assert.True(preview.Length < content.Length);
    }
}
