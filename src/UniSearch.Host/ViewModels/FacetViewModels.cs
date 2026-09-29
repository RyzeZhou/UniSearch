using CommunityToolkit.Mvvm.ComponentModel;

namespace UniSearch.Host.ViewModels;

/// <summary>
/// 标签栏上回显的一枚<b>已选值</b>。值域筛选器不把候选值摊在标签栏上（可能上百个），
/// 只把已经选中的那几个摆出来 —— 这样"当前筛了什么"始终可见、且点一下就能去掉。
/// </summary>
public sealed record FacetChip(string Value);

/// <summary>展开面板里的一行候选值。</summary>
public sealed partial class FacetCandidate : ObservableObject
{
    public FacetCandidate(string value, long count)
    {
        Value = value;
        Count = count;
    }

    public string Value { get; }

    /// <summary>该值下的条目数，<b>后端给的</b>（不是已返回子集里数出来的）。</summary>
    public long Count { get; }

    public string Display => Count > 0 ? $"{Value}  ({Count})" : Value;

    /// <summary>勾选状态由视图模型统一同步（真相在 <c>_selectedFacetValues</c>），
    /// 所以这里只做显示，不反向驱动查询。</summary>
    [ObservableProperty]
    bool _isChecked;
}
