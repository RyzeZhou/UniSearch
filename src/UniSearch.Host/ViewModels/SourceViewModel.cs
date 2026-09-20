using CommunityToolkit.Mvvm.ComponentModel;

namespace UniSearch.Host.ViewModels;

/// <summary>
/// 左侧来源栏的一项：一个搜索后端。
/// <para>
/// 点击的语义是<b>"这次只搜它"</b>（查询带上 <c>ProviderScope</c>），再点一次取消限定、
/// 回到设置里的默认集合。所以这一栏不只是过滤器 —— 它决定后端<b>跑不跑</b>，
/// 状态条与日志据此如实报告，而不是"全都跑了、只把别人的结果藏起来"。
/// </para>
/// </summary>
public sealed partial class SourceViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Glyph { get; init; }

    /// <summary>副标题：能力自述或"为什么现在用不了"。</summary>
    public string? Description { get; init; }

    /// <summary>当前是否被限定为唯一来源。</summary>
    [ObservableProperty]
    bool _isActive;

    /// <summary>它是否属于设置里的"随输入自动搜索"集合（左栏上打一个小标）。</summary>
    [ObservableProperty]
    bool _isDefaultAuto;

    /// <summary>右侧的实时状态：就绪 / 已禁用 / 本次 N 条。</summary>
    [ObservableProperty]
    string _statusText = string.Empty;

    /// <summary>本次查询该后端返回的条数（左栏紧凑态显示的就是它）。</summary>
    [ObservableProperty]
    int _count;

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CompactCount));

    /// <summary>
    /// 紧凑态（窄栏）显示的数量：<b>超过 9999 一律写 "&gt;9999"</b> ——
    /// 56px 宽的栏里放不下五位数，写全了会被截成 "1234…" 反而看不出量级。
    /// </summary>
    public string CompactCount => Count > 9999 ? ">9999" : Count.ToString();

    /// <summary>后端当前是否可用（不可用置灰，但仍可点 —— 点了会如实报告为什么没结果）。</summary>
    [ObservableProperty]
    bool _isAvailable = true;

    public string TooltipText => Description is { Length: > 0 } d
        ? $"{DisplayName}：{d}"
        : $"只搜 {DisplayName}（再点一次取消限定）";
}
