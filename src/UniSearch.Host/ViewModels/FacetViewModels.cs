using CommunityToolkit.Mvvm.ComponentModel;

namespace UniSearch.Host.ViewModels;

/// <summary>
/// 标签栏上回显的一枚<b>已选值</b>。值域筛选器不把候选值摊在标签栏上（可能上百个），
/// 只把已经选中的那几个摆出来 —— 这样"当前筛了什么"始终可见、且点一下就能去掉。
/// </summary>
/// <param name="Key">下推给后端的值（移除时按它匹配）。</param>
/// <param name="Display">给人看的名字。</param>
public sealed record FacetChip(string Key, string Display);

/// <summary>展开面板里的一行候选值。</summary>
public sealed partial class FacetCandidate : ObservableObject
{
    public FacetCandidate(string value, long count, string? key = null)
    {
        Value = value;
        Count = count;
        // 显示名与下推值可以不同（思源的笔记本：显示"R语言"、下推 id）
        Key = key is { Length: > 0 } k ? k : value;
    }

    /// <summary>给人看的显示名。</summary>
    public string Value { get; }

    /// <summary>下推给后端用的值（勾选/取消都按它走）。</summary>
    public string Key { get; }

    /// <summary>该值下的条目数，<b>后端给的</b>（不是已返回子集里数出来的）。</summary>
    public long Count { get; }

    public string Display => Count > 0 ? $"{Value}  ({Count})" : Value;

    /// <summary>勾选状态由视图模型统一同步（真相在 <c>_selectedFacetValues</c>），
    /// 所以这里只做显示，不反向驱动查询。</summary>
    [ObservableProperty]
    bool _isChecked;
}
