namespace UniSearch.Core.Filters;

public enum FilterReloadOutcome
{
    /// <summary>新目录已通过校验，调用方应换用它。</summary>
    Accepted,

    /// <summary>配置里的某个文件解析失败 —— 整体拒绝，继续用旧目录。</summary>
    RejectedFileBroken,

    /// <summary>重载后一条筛选器都不剩（而旧目录还有）—— 大概率也是文件写坏了，整体拒绝。</summary>
    RejectedEmpty,
}

/// <summary>一次热重载的结果：判定 + 原因（给人看的，直接进状态条/日志）。</summary>
public sealed record FilterReloadResult(FilterCatalog Catalog, FilterReloadOutcome Outcome, string? Reason = null)
{
    public static FilterReloadResult Accepted(FilterCatalog catalog) => new(catalog, FilterReloadOutcome.Accepted);
}

/// <summary>
/// filters.json 的<b>热重载</b>判定器（遗留清单 C3 / F2）。
/// <para>
/// 监视文件、防抖是宿主的事（涉及 FileSystemWatcher 与线程封送）；这里只回答
/// "新读出来的目录该不该换上去"。判定是纯逻辑，所以放在 Core 让单测够得着 ——
/// 这条判定恰恰是热重载最容易写错的地方：<see cref="FilterCatalog.Load"/> 对坏文件
/// <b>不抛异常</b>，只是把问题记进 <see cref="FilterCatalog.Problems"/> 并跳过该文件，
/// 直接换用会让"文件写坏了"静默变成"筛选器变少/变空"。
/// </para>
/// <para>
/// 口径：<b>配置里存在、却没被成功读进 Sources 的文件 = 解析失败</b> → 整体拒绝，
/// 保留旧目录并明确提示（宁可"改了暂时不生效"，也不"改坏了悄悄吞掉一半筛选器"）。
/// 用户改的明明是用户文件，程序自带那份又没动 —— 拒绝整体不会丢内容。
/// </para>
/// </summary>
public sealed class FilterCatalogReloader
{
    readonly string?[] _paths;

    /// <param name="paths">与初次 <see cref="FilterCatalog.Load"/> 完全相同的路径序列（顺序有意义：后者覆盖前者）。</param>
    public FilterCatalogReloader(params string?[] paths) => _paths = paths;

    /// <summary>重新加载并判定。返回的 <see cref="FilterReloadResult.Catalog"/> 在 Accepted 时才有换用价值。</summary>
    public FilterReloadResult Reload(FilterCatalog current)
    {
        var fresh = FilterCatalog.Load(_paths);

        // "配置了却没读到"的就是解析失败的那个文件。首次运行时用户文件不存在是常态
        // （Load 对不存在的文件静默跳过、也不进 Sources），所以这里只对"现在存在"的文件较真。
        foreach (var path in _paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var normalized = Path.GetFullPath(path);
            var loaded = fresh.Sources.Any(s =>
                string.Equals(Path.GetFullPath(s), normalized, StringComparison.OrdinalIgnoreCase));
            if (loaded) continue;

            var problem = fresh.Problems.FirstOrDefault(p => p.StartsWith(Path.GetFileName(path), StringComparison.Ordinal))
                          ?? $"{Path.GetFileName(path)} 未能加载（解析失败？）";
            return new FilterReloadResult(current, FilterReloadOutcome.RejectedFileBroken, problem);
        }

        if (fresh.All.Count == 0 && current.All.Count > 0)
            return new FilterReloadResult(current, FilterReloadOutcome.RejectedEmpty,
                "重载后没有任何筛选器（文件内容被清空？）");

        return FilterReloadResult.Accepted(fresh);
    }
}
