using UniSearch.Core.Fusion;
using UniSearch.Core.Matching;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Model;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Core.Ranking;

/// <summary>
/// 跨 Provider 的排序。<b>不要求各后端给出可比的 ProviderScore</b>（量纲完全不同），
/// 而是把“命中质量 + 用户习惯 + 上下文邻近度 + 新鲜度”折算成 UniSearch 自己的分数。
/// 因为最终是按分类分组展示，慢后端无法把别人的榜单搅乱。
/// </summary>
public sealed class Ranker
{
    readonly IUsageStore? _usage;

    public Ranker(IUsageStore? usage = null) => _usage = usage;

    /// <summary>返回未归一化的原始分（只用于同一分类内比较）。</summary>
    public double Score(FusedResult f, SearchQuery q, SearchContext ctx, int providerPriority)
    {
        var s = 0.0;

        // 1) 名称命中质量（主导项）
        var name = f.NameScore > 0 ? f.NameScore : ComputeNameScore(f, q);
        s += name * 100;

        // 2) 正文命中：没有名称命中时它是主信号，有则只是加分
        if (!f.NameMatched && f.ContentMatched) s += 35 + Math.Min(15, f.ContentMatchCount ?? 1);
        else if (f.ContentMatched) s += 6;

        // 3) 用户习惯（对数抑制，避免一次误点长期霸榜）
        if (_usage is not null && f.Display.CountUsage)
            s += Math.Min(30, Math.Log2(1 + _usage.GetWeight(f.Display.UsageKey, q.MatchTarget)) * 8);

        // 4) 上下文邻近度：在 Explorer 目录内，越靠上越优先
        if (ctx.IsDirectoryBounded && f.Display.Path is { } path && ctx.RootPath is { } root)
        {
            var depth = DepthOf(path, root);
            s += depth >= 99 ? -20 : Math.Max(-18, 14 - depth * 3.5);
        }

        // 5) 目录在前：对“打了一半的词”这一经验规则极有效
        if (q.Text.Length > 0 && name >= NameMatcher.Prefix - 1e-9 && f.Display.IsFolder) s += 9;

        // 6) 新鲜度（只在名称接近打平时起作用）
        if (f.ModifiedAt is { } mt)
        {
            var days = (DateTimeOffset.Now - mt).TotalDays;
            s += days < 7 ? 5 : days < 90 ? 2.5 : days < 730 ? 1 : 0;
        }

        s += providerPriority * 0.08;                       // 7) 后端可信度
        return s;
    }

    double ComputeNameScore(FusedResult f, SearchQuery q)
    {
        var d = f.Display;
        var target = q.MatchTarget;
        var best = NameMatcher.Score(d.Title, target);
        if (d.Subtitle is { Length: > 0 } sub) best = Math.Max(best, NameMatcher.Score(sub, target) * 0.75);
        if (d.Path is { } p && !string.Equals(Canonicalization.GetFileName(p), d.Title, StringComparison.OrdinalIgnoreCase))
            best = Math.Max(best, NameMatcher.Score(p, target) * 0.6);
        if (f.Snippet is { Length: > 0 } sn) best = Math.Max(best, NameMatcher.Score(sn, target) * 0.25);

        // 查询词正好等于扩展名时（搜 "pdf" / "exe" / "zip"），扩展名命中是最强的意图信号。
        // 朴素的名称匹配会让 pdf-worker.fixture.mjs（文件名前缀命中）压过 report.pdf，
        // 用户读作"搜不准"。实测：修前 documents 组只剩 5 条真文档，排在 files 组一堆 .mjs 后面。
        if (d.Extension is { Length: >= 2 } ext)
        {
            var bare = ext.TrimStart('.');
            foreach (var t in q.Terms)
                if (t.Length >= 2 && string.Equals(t, bare, StringComparison.OrdinalIgnoreCase))
                    best = Math.Max(best, NameMatcher.Prefix + 0.01);
        }

        return best < 0 ? 0.05 : best;                     // 纯正文命中也要有位置
    }

    static int DepthOf(string path, string root)
    {
        if (!Canonicalization.IsWithinDirectory(path, root)) return 99;
        var dir = Canonicalization.ToDirectoryKey(root);
        var rel = Canonicalization.NormalizePath(path);
        if (rel.Length <= dir.Length) return 0;
        return rel.Count(c => c == Canonicalization.Sep) - dir.Count(c => c == Canonicalization.Sep);
    }

    /// <summary>把任意范围的原始分压到 0..1，供 UI 画相关度条。</summary>
    public static double Normalize(double score, double max) => max <= 0 ? 0 : Math.Clamp(score / max, 0, 1);
}
