using System.Diagnostics;
using UniSearch.Core.Broker;
using UniSearch.Core.Parsing;
using UniSearch.Core.Selection;
using UniSearch.Providers.Everything;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Model;
using UniSearch.Sdk.Runtime;
using ESC = EverythingSearchClient;

namespace UniSearch.Tools.Probe;

/// <summary>
/// 分层探针：绕开 WPF，把 Parser → Translator → Everything IPC → Broker/Fusion 每一层单独打出来。
/// 用法： dotnet run --project tools/Probe -- "pdf"
/// </summary>
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--meta")   // 反射出客户端支持的排序/字段，供排名策略参考
        {
            foreach (var e in new[] { typeof(ESC.SearchClient.SortBy), typeof(ESC.SearchClient.SearchFlags),
                                       typeof(ESC.SearchClient.BehaviorWhenBusy) })
                Console.WriteLine($"-- {e.Name}: {string.Join(", ", Enum.GetNames(e))}");
            Console.WriteLine("-- Result.Item: " + string.Join(", ",
                typeof(ESC.Result.Item).GetProperties().Select(p => p.Name + ":" + p.PropertyType.Name)));
            return 0;
        }

        if (args.Length > 1 && args[0] == "--raw")   // 直接把查询串发给 Everything，用来核实语法/排除子句是否真生效
        {
            var raw = string.Join(' ', args.Skip(1));
            {
                var c = new ESC.SearchClient { ReceiveTimeout = TimeSpan.FromSeconds(5) };
                var r = c.Search(raw, ESC.SearchClient.SearchFlags.None, 12, 0,
                                 ESC.SearchClient.BehaviorWhenBusy.Continue, 5000,
                                 ESC.SearchClient.SortBy.Name, ESC.SearchClient.SortDirection.Ascending);
                Console.WriteLine($"RAW \"{raw}\" -> total={r.TotalItems} got={r.Items.Length}");
                foreach (var it in r.Items.Take(8))
                    Console.WriteLine($"   {it.Flags,-14} {it.Name,-34} {it.Path}");
            }
            return 0;
        }

        var text = args.Length > 0 ? string.Join(' ', args) : "pdf";
        Console.WriteLine($"== 输入: \"{text}\"");

        // ── 0. Everything 可达性 ────────────────────────────
        var probe = EverythingLocator.Probe();
        Console.WriteLine($"\n[0] Locator.Probe -> Present={probe.Present} version={probe.Version} instance={probe.InstanceName} hwnd={probe.Handle}");
        Console.WriteLine($"[0] IsEverythingAvailable -> {ESC.SearchClient.IsEverythingAvailable()}");
        Console.WriteLine($"[0] FindExecutable -> {EverythingLocator.FindExecutable() ?? "(未找到)"}");

        // ── 1. 解析层 ───────────────────────────────────────
        var q = QueryParser.Parse(1, text);
        Console.WriteLine($"\n[1] Parse: ProviderText=\"{q.ProviderText}\" IsStructured={q.IsStructured} " +
                          $"MatchTarget=\"{q.MatchTarget}\" Terms=[{string.Join("|", q.Terms)}]");
        Console.WriteLine($"[1] Filters: kinds=[{string.Join(",", q.Filters.Kinds)}] exts=[{string.Join(",", q.Filters.Extensions)}] " +
                          $"regex={q.Filters.RegexRequested}");
        Console.WriteLine($"[1] Notice: {q.SyntaxNotice ?? "(none)"}");

        // ── 2. 翻译层（实际发给 Everything 的查询串）─────────
        var ctx = SearchContext.Global();
        Console.WriteLine($"\n[2] Translate -> \"{EverythingQueryTranslator.Translate(q, ctx)}\"  flags={EverythingQueryTranslator.BuildFlags(q)}");

        var dirPath = Path.Combine("D:", "tools");
        if (Directory.Exists(dirPath))
        {
            var dctx = SearchContext.InDirectory(dirPath, QueryOrigin.ExplorerHotkey);
            Console.WriteLine($"[2] 目录限定 {dirPath} -> \"{EverythingQueryTranslator.Translate(q, dctx)}\"");
        }

        // ── 3. Provider 直连（不经 Broker）──────────────────
        Console.WriteLine("\n[3] Provider 直连:");
        var provider = new EverythingProvider();
        await provider.InitializeAsync(new NoopRuntime(), CancellationToken.None);
        var health = await provider.ProbeHealthAsync(CancellationToken.None);
        Console.WriteLine($"    health: {health.State} {health.Detail ?? ""}");

        var sw = Stopwatch.StartNew();
        var direct = 0;
        await foreach (var batch in provider.SearchAsync(q, ctx, CancellationToken.None))
        {
            Console.WriteLine($"    批次: {batch.Results.Count} 条 (total={batch.TotalAvailable}) 累计用时 {sw.ElapsedMilliseconds}ms isLast={batch.IsLast}");
            foreach (var r in batch.Results.Take(5))
                Console.WriteLine($"      - {r.Kind,-10} {r.Title,-32} {r.Path}");
            direct += batch.Results.Count;
            if (direct > 5) break;
        }
        Console.WriteLine($"    直连合计取到 {direct} 条，用时 {sw.ElapsedMilliseconds}ms");

        // ── 4. Broker 全链路 ────────────────────────────────
        Console.WriteLine("\n[4] Broker 全链路:");
        using var broker = new SearchBroker([new ProviderEntry(provider, provider.Descriptor)]);
        var snaps = 0;
        SearchSnapshot? last = null;
        sw.Restart();
        await foreach (var snap in broker.RunAsync(QueryParser.Parse(2, text), ctx, CancellationToken.None))
        {
            snaps++;
            last = snap;
        }
        Console.WriteLine($"    快照数={snaps} 用时={sw.ElapsedMilliseconds}ms");
        if (last is null) { Console.WriteLine("    !! Broker 一个快照都没产出"); return 1; }
        Console.WriteLine($"    TotalFused={last.TotalFused} complete={last.IsComplete}");
        foreach (var g in last.Groups)
        {
            Console.WriteLine($"      组 {g.CategoryId,-12} {g.DisplayName,-6} 展示={g.Items.Count,-3} 总数={g.TotalAvailable} 来自=[{string.Join(",", g.Providers)}]");
            // 打印排名后的实际顺序：这才是用户看到的东西，噪声全在这里暴露
            foreach (var it in g.Items.Take(4))
                Console.WriteLine($"          {it.Display.Title,-46} | {it.Display.Path}");
        }
        foreach (var o in last.Outcomes)
            Console.WriteLine($"      结局 {o.ProviderId,-12} {o.State,-9} returned={o.Returned,-4} kept={o.Kept,-4} total={o.TotalAvailable,-6} {o.Detail ?? ""}");

        return last.TotalFused > 0 ? 0 : 2;
    }
}

/// <summary>探活用最小运行时：Provider 只要求不抛。</summary>
sealed class NoopRuntime : IProviderRuntime
{
    public string DataDirectory => Path.GetTempPath();
    public IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>();
    public IUsageStore Usage { get; } = new NoopUsage();
    public IUniSearchLog Log { get; } = new NoopLog();
    public IProcessLauncher Process { get; } = new NoopProcess();
    public void RecordActivation(string k, string? q, string p, string a) { }
}

sealed class NoopUsage : IUsageStore
{
    public double GetWeight(string k) => 0;
    public double GetWeight(string k, string? q) => 0;
    public int GetActivationCount(string k) => 0;
    public DateTimeOffset? LastActivatedAt(string k) => null;
    public IReadOnlyList<string> GetTopKeys(string? kindPrefix, int take) => [];
    public void Record(string k, string? q, string p, string a) { }
    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
}

sealed class NoopLog : IUniSearchLog
{
    public void Debug(string p, string m) { }
    public void Info(string p, string m) { }
    public void Warn(string p, string m, Exception? e = null) { }
    public void Error(string p, string m, Exception? e = null) => Console.WriteLine($"[LOG-ERR] {p}: {m} {e?.Message}");
}

sealed class NoopProcess : IProcessLauncher
{
    public bool OpenFile(string p, string? a = null, string? w = null) => true;
    public bool OpenFolder(string p, bool select = false) => true;
    public bool StartUri(string u) => true;
    public bool RunVerb(string p, string v) => true;
    public bool RunAsAdmin(string p, string? a = null) => true;
    public bool OpenInConsole(string p) => true;
}