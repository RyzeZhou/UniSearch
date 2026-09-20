using UniSearch.Core.Fusion;
using UniSearch.Core.Selection;
using UniSearch.Sdk.Contracts;
using UniSearch.Sdk.Model;

namespace UniSearch.Core.Broker;

/// <summary>一个分类的展示分组。</summary>
public sealed record CategoryGroup(
    string CategoryId,
    string DisplayName,
    IReadOnlyList<FusedResult> Items,
    int TotalAvailable,
    /// <summary>本组被截断（还有更多，Ctrl+F 之类可下钻）。</summary>
    bool Trimmed,
    /// <summary>该分组由哪些后端贡献，用于组标题右侧的 “来自 Everything, AnyTXT”。</summary>
    IReadOnlyList<string> Providers);

/// <summary>每次 Provider 产出都会产生一个增量快照；UI 只需替换整个列表，不做 diff。</summary>
public sealed record SearchSnapshot(
    long RequestId,
    IReadOnlyList<CategoryGroup> Groups,
    IReadOnlyList<ProviderOutcome> Outcomes,
    int TotalFused,
    bool IsComplete,
    TimeSpan Elapsed)
{
    public static SearchSnapshot Empty(long requestId) =>
        new(requestId, [], [], 0, false, TimeSpan.Zero);
}

public enum ProviderOutcomeState { Running, Done, Failed, Skipped, Timeout, Cancelled }

/// <summary>状态条与诊断面板的数据：每个后端一行，说明它是否被调度、为什么。</summary>
public sealed record ProviderOutcome(
    string ProviderId,
    string DisplayName,
    ProviderOutcomeState State,
    int Returned,
    int Kept,
    int? TotalAvailable,
    TimeSpan Elapsed,
    /// <summary>被跳过/失败的人话原因，例如 “不支持限定目录（Zotero 无法在 D:\x 内搜索）”。</summary>
    string? Detail);

/// <summary>调度器对某个 Provider 的最终处置。</summary>
public sealed record Disposition(ProviderEntry Entry, ProviderOutcomeState State, string? Reason);

/// <summary>Broker 的固定阈值。全部走配置，不允许散落在实现里。</summary>
public sealed record BrokerOptions
{
    /// <summary>每个分类在“全部”视图里最多展示多少行。</summary>
    public int PerCategoryLimit { get; init; } = 24;
    /// <summary>单分类视图里的上限（翻页再放大）。</summary>
    public int PerCategoryLimitInFocus { get; init; } = 200;
    /// <summary>两次快照之间的最小间隔，避免 Everything 大结果集把 UI 打爆。</summary>
    public TimeSpan SnapshotThrottle { get; init; } = TimeSpan.FromMilliseconds(45);
    public TimeSpan DefaultDeadline { get; init; } = TimeSpan.FromMilliseconds(1800);
    /// <summary>慢后端（Zotero/在线）先占位再补齐。</summary>
    public bool ShowPlaceholderForSlowProviders { get; init; } = true;
    /// <summary>没有路径的实体是否单独成组（而不是塞进“其他”）。</summary>
    public bool IsolateUnknownEntities { get; init; } = false;
    /// <summary>隐藏/系统文件（除非用户显式要）。</summary>
    public bool FilterHiddenAndSystemByDefault { get; init; } = true;
}
