using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniSearch.Core.Broker;
using UniSearch.Core.Categories;
using UniSearch.Core.Filters;
using UniSearch.Core.Fusion;
using UniSearch.Core.Sorting;
using UniSearch.Host.Services;
using UniSearch.Host.Settings;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Contracts;
using UniSearch.Sdk.Model;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Host.ViewModels;

/// <summary>
/// 搜索会话：输入 → Broker → 快照 → <b>平铺单表</b>（Everything / 资源管理器"详细信息"口径）。
/// 这里只做"节流后的整表替换"，不做增量 diff —— Broker 已经保证快照是不可变的完整视图。
/// <para>
/// 三栏各管一段：左栏（<see cref="Sources"/>）决定<b>搜哪些后端</b>，
/// 中间表（<see cref="Rows"/> + <see cref="Columns"/>）决定<b>怎么呈现与排序</b>，
/// 右栏（预览）跟着选中项走。
/// </para>
/// </summary>
public sealed partial class SearchSessionViewModel : ObservableObject
{
    readonly SearchBroker _broker;
    readonly IProcessLauncher _launcher;
    readonly IUniSearchLog? _log;
    CancellationTokenSource? _run;

    public SearchSessionViewModel(SearchBroker broker, IProcessLauncher? launcher = null, IUniSearchLog? log = null)
    {
        _broker = broker;
        _log = log;
        _launcher = launcher ?? new UniSearch.Host.Services.WindowsProcessLauncher(log ?? new NullLog());
        ApplyLayout(new ColumnsSettings());   // 先建默认列，App 随后用落盘的布局覆盖
        RefreshBackendTargets();
        RefreshSources();
    }

    /// <summary>无日志时的空实现，避免各处判空。</summary>
    sealed class NullLog : IUniSearchLog
    {
        public void Debug(string p, string m) { }
        public void Info(string p, string m) { }
        public void Warn(string p, string m, Exception? e = null) { }
        public void Error(string p, string m, Exception? e = null) { }
    }

    [ObservableProperty]
    string _input = string.Empty;

    [ObservableProperty]
    ResultItemViewModel? _selected;

    partial void OnSelectedChanged(ResultItemViewModel? value)
    {
        if (_previouslySelected is not null) _previouslySelected.IsSelected = false;
        if (value is not null) value.IsSelected = true;
        _previouslySelected = value;
        // 只在有值时更新恢复锚点：整表重建时 ListView 会把选中置空并回写，
        // 若这里跟着清掉，"按路径找回原来那一行"就失去了依据（症状：选中永远跳回第一行）。
        if (value is not null) _selectedPath = value.Source.Path ?? value.Source.Uri;
        RefreshProviderActions();   // 右键菜单里的领域动作跟着选中项换

        // 右键菜单按 文件/文件夹/可执行 分叉，这三项变了菜单就得重算
        OnPropertyChanged(nameof(IsFolderSelection));
        OnPropertyChanged(nameof(IsFileSelection));
        OnPropertyChanged(nameof(IsRunnableSelection));

        QueuePreview(value);
    }

    // ─────────────── 多选（Ctrl/Shift 点选，2026-09-25 第 12 轮）───────────────

    /// <summary>
    /// 当前多选集合，由视图的 <c>SelectionChanged</c> 经 <see cref="SyncSelection"/> 灌进来。
    /// <see cref="Selected"/> 仍是"主选中项 = 第一个"，因此预览、快捷键、单选右键那套
    /// （真 shell 菜单 + 追加项）的行为一字不变 —— 多选是**加**上来的，不是替换。
    /// </summary>
    readonly List<ResultItemViewModel> _selection = [];
    public IReadOnlyList<ResultItemViewModel> SelectedItems => _selection;

    public int SelectionCount => _selection.Count;

    /// <summary>≥2 项才算多选：1 项时右键必须走原来那条单选路径。</summary>
    public bool HasMultiSelection => _selection.Count > 1;

    /// <summary>多选汇总文案（状态条与预览区共用）；单选/空选为 null。</summary>
    public string? SelectionSummary { get; private set; }

    /// <summary>视图选择变化 → 同步进视图模型。传 <c>ListView.SelectedItems</c> 即可。</summary>
    public void SyncSelection(System.Collections.IEnumerable items)
    {
        _selection.Clear();
        foreach (var o in items)
            if (o is ResultItemViewModel r) _selection.Add(r);

        // 主选中项 = 第一个：既有代码全部读 Selected，这样它们零改动
        var first = _selection.Count > 0 ? _selection[0] : null;
        if (!ReferenceEquals(Selected, first)) Selected = first;

        SelectionSummary = BuildSelectionSummary();
        OnPropertyChanged(nameof(SelectedItems));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasMultiSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(StatusText));
        QueuePreview(Selected);
    }

    /// <summary>已选 N 项 · 合计 X · 含 M 个文件夹。文件夹不计入大小（不知道就是不知道）。</summary>
    string? BuildSelectionSummary()
    {
        if (_selection.Count <= 1) return null;

        long bytes = 0;
        int sized = 0, folders = 0;
        foreach (var r in _selection)
        {
            if (r.Source.IsFolder) { folders++; continue; }
            if (r.Source.SizeBytes is { } s) { bytes += s; sized++; }
        }

        var parts = new List<string> { $"已选 {_selection.Count} 项" };
        if (sized > 0) parts.Add($"合计 {Formatting.HumanSize(bytes)}");
        if (folders > 0) parts.Add($"含 {folders} 个文件夹");
        return string.Join(" · ", parts);
    }

    /// <summary>多选里的全部本地路径（批量动作用；跳过没有真实路径的实体，如将来的 Zotero 条目）。</summary>
    public IReadOnlyList<string> SelectedPaths()
        => _selection.Select(r => r.Source.Path)
                     .Where(p => !string.IsNullOrEmpty(p))
                     .Select(p => p!)
                     .ToList();

    // ─────────────── 结果表（平铺单表 + 可调列 + 列排序）───────────────

    /// <summary>当前显示的行：已排序、已按 <see cref="MaxRows"/> 截断。</summary>
    public ObservableCollection<ResultItemViewModel> Rows { get; } = [];

    /// <summary>
    /// 当前快照的全部融合结果，<b>保持相关度次序</b>（未截断）。
    /// 排序时复制一份再排 —— 这样"点回相关度"永远能回到 Broker 给出的原始次序，
    /// 而不是在上一次排序的结果上再排（那样会越点越乱）。
    /// </summary>
    readonly List<FusedResult> _fused = [];

    /// <summary>列表最多显示多少行。来自设置（<c>search.maxRows</c>）。</summary>
    public int MaxRows { get; set; } = SearchSettings.DefaultMaxRows;

    /// <summary>列定义（含隐藏列）。顺序 = 显示顺序，可拖宽、可点排序、可勾选显隐。</summary>
    public ObservableCollection<ResultColumnViewModel> Columns { get; } = [];

    [ObservableProperty]
    ResultSortKey _sortKey = ResultSortKey.Name;

    [ObservableProperty]
    bool _sortDescending;

    partial void OnSortKeyChanged(ResultSortKey value) => UpdateSortGlyphs();
    partial void OnSortDescendingChanged(bool value) => UpdateSortGlyphs();

    /// <summary>列布局变了（数量/顺序/可见性/宽度）。宿主据此重建 GridView 的列。</summary>
    public event Action? LayoutChanged;

    /// <summary>用户点了"恢复默认列"。只重置布局，不动数据。</summary>
    [RelayCommand]
    public void ResetColumns()
    {
        var s = new ColumnsSettings();
        s.Normalize();
        ApplyLayout(s);
        Report(true, "已恢复默认列布局");
    }

    /// <summary>把落盘的列布局应用到当前会话（启动时一次；设置里改了也走这里）。</summary>
    public void ApplyLayout(ColumnsSettings s)
    {
        s.Normalize();

        // 可见列 = 配置里列出的（顺序即显示顺序）；其余列追加在末尾但隐藏 ——
        // 用户哪天勾回来时，它会出现在表尾，而不是插进中间把现有列序打乱。
        var order = new List<string>();
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in s.Visible)
        {
            if (ColumnCatalog.Find(k) is null) continue;   // 手改配置写错的键：忽略，不当成新列
            if (visible.Add(k)) order.Add(k);
        }
        foreach (var spec in ColumnCatalog.All)
            if (!visible.Contains(spec.Key)) order.Add(spec.Key);

        Columns.Clear();
        foreach (var key in order)
        {
            var spec = ColumnCatalog.Find(key)!;
            var width = s.Widths.TryGetValue(spec.Key, out var w) && w > 0 ? w : spec.DefaultWidth;
            Columns.Add(new ResultColumnViewModel(spec, width, visible.Contains(spec.Key)));
        }

        // 一列都不显示的表等于空白，用户会以为程序坏了：退回默认可见列
        if (!Columns.Any(c => c.IsVisible))
            foreach (var c in Columns)
                c.IsVisible = ColumnCatalog.DefaultVisible.Contains(c.Key, StringComparer.OrdinalIgnoreCase);

        ResultSort.TryParse(s.SortKey, out var sortKey);
        SortKey = sortKey;
        SortDescending = s.SortDescending;

        UpdateSortGlyphs();
        LayoutChanged?.Invoke();
    }

    /// <summary>把当前列布局导出成可落盘的形式（窗口隐藏/设置保存时调用）。</summary>
    public ColumnsSettings CaptureLayout()
    {
        var s = new ColumnsSettings
        {
            Visible = Columns.Where(c => c.IsVisible).Select(c => c.Key).ToList(),
            SortKey = ResultSort.ToConfigName(SortKey),
            SortDescending = SortDescending,
            Widths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        };
        foreach (var c in Columns) s.Widths[c.Key] = (int)Math.Round(c.Width);
        s.Normalize();
        return s;
    }

    void UpdateSortGlyphs()
    {
        foreach (var c in Columns)
        {
            var isSorted = ResultSort.TryParse(c.Key, out var k) && k == SortKey;
            c.IsSorted = isSorted;
            c.SortGlyph = isSorted ? (SortDescending ? "▼" : "▲") : string.Empty;
        }
    }

    /// <summary>
    /// 点列头：同一列再点一次换方向，换列时用该列的"首次方向"
    /// （大小/时间默认降序 —— 点"大小"想看的是最大的那几个）。
    /// </summary>
    public void ToggleSort(string columnKey)
    {
        if (!ResultSort.TryParse(columnKey, out var key)) return;

        if (key == SortKey) SortDescending = !SortDescending;
        else { SortKey = key; SortDescending = ResultSort.DefaultDescending(key); }

        RebuildRows();
        _log?.Info("ui", $"排序 = {ResultSort.ToConfigName(SortKey)}{(SortDescending ? " 降序" : " 升序")}，行数={Rows.Count}");
    }

    /// <summary>
    /// 正在整表重建 <see cref="Rows"/>。
    /// 宿主据此忽略重建期间 ListView 发出的"选中变 null"（那是 Clear 的副作用，不是用户意图）。
    /// </summary>
    public bool IsRebuildingRows { get; private set; }

    /// <summary>按当前排序重建 <see cref="Rows"/>（排序、截断、映射、恢复选中）。</summary>
    void RebuildRows()
    {
        var list = new List<FusedResult>(_fused);
        // 相关度不排序：Broker 给的次序就是分数降序（见 Apply）
        if (SortKey != ResultSortKey.Relevance) ResultSort.Sort(list, SortKey, SortDescending);

        var take = Math.Clamp(MaxRows, 20, 20000);
        var keep = _selectedPath;

        var rows = new List<ResultItemViewModel>(Math.Min(list.Count, take));
        for (var i = 0; i < list.Count && i < take; i++) rows.Add(SnapshotMapper.Map(list[i]));

        IsRebuildingRows = true;
        try
        {
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
        }
        finally { IsRebuildingRows = false; }

        // 整表替换后旧的 Selected 已不在列表里：按路径重新落位，
        // 否则 Enter 会打开一条界面上看不见的结果，选中高亮也永远画不出来。
        ResultItemViewModel? match = null;
        if (keep is { Length: > 0 })
            match = rows.FirstOrDefault(r =>
                string.Equals(r.Source.Path ?? r.Source.Uri, keep, StringComparison.OrdinalIgnoreCase));
        Selected = match ?? rows.FirstOrDefault();

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyHint));
    }

    // ─────────────── 左侧来源栏 ───────────────

    /// <summary>可选的搜索后端。点击 = 本次只搜它；再点一次 = 回到默认集合。</summary>
    public ObservableCollection<SourceViewModel> Sources { get; } = [];

    /// <summary>
    /// 用户钉住的唯一来源；null = 跟随设置里的"随输入自动搜索"集合。
    /// <b>它决定后端跑不跑</b>（进 <c>SearchQuery.ProviderScope</c>），不只是过滤显示。
    /// </summary>
    [ObservableProperty]
    string? _activeSourceId;

    partial void OnActiveSourceIdChanged(string? value)
    {
        foreach (var s in Sources)
            s.IsActive = string.Equals(s.Id, value, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(StatusText));
        _ = RunAsync();
    }

    /// <summary>设置里"随输入自动参与搜索"的后端集合（默认只有 Everything）。</summary>
    public IReadOnlyList<string> AutoSearchProviders { get; private set; } = ["everything"];

    /// <summary>设置变了：重算默认集合，并把钉住的选择放回默认（用户的意图已经变了）。</summary>
    public void SetAutoSearchProviders(IReadOnlyList<string> ids)
    {
        AutoSearchProviders = ids.Count > 0 ? ids : [];
        foreach (var s in Sources)
            s.IsDefaultAuto = AutoSearchProviders.Contains(s.Id, StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(SourceLabel));
    }

    /// <summary>本次查询实际要问的后端（null = 不限制，全部合格后端都问）。</summary>
    public IReadOnlyList<string>? EffectiveProviderScope =>
        ActiveSourceId is { Length: > 0 } id ? [id]
        : AutoSearchProviders.Count > 0 ? AutoSearchProviders
        : null;

    /// <summary>状态条上的来源口径。</summary>
    public string SourceLabel =>
        ActiveSourceId is { Length: > 0 } id ? $"{SnapshotMapper.Pretty(id)}（已限定）"
        : AutoSearchProviders.Count > 0 ? string.Join(" + ", AutoSearchProviders.Select(SnapshotMapper.Pretty))
        : "全部来源";

    // ─────────────── 筛选器（filters.json 定义的可扩展筛选器）───────────────

    /// <summary>
    /// 筛选器目录。默认空 —— 没有定义文件时标签栏就只有内置分类，
    /// 一切照旧（这也是"松耦合"的意思：新机制缺席时旧行为必须还成立）。
    /// </summary>
    public FilterCatalog Catalog { get; private set; } = FilterCatalog.Empty;

    /// <summary>当前选中的自定义筛选器（选中的是内置分类时为 null）。</summary>
    public FilterDefinition? CurrentFilter => Catalog.Find(SelectedTabId);

    /// <summary>最近一次查询真正发给后端的查询串（诊断用：一眼看出筛选器有没有翻译成 ext:/kind:）。</summary>
    public string? LastProviderText { get; private set; }

    /// <summary>最近一次查询用的自定义筛选器名（null = 用的内置分类）。</summary>
    public string? LastFilterLabel { get; private set; }

    /// <summary>宿主加载完 filters.json 后注入（改文件后重启程序生效）。</summary>
    public void SetFilterCatalog(FilterCatalog catalog)
    {
        Catalog = catalog;
        OnPropertyChanged(nameof(Catalog));
        _ = RunAsync();   // 标签栏与过滤条件都变了，重跑一次
    }

    [RelayCommand]
    public void SelectSource(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        // 再点一次已选中的项 = 取消限定。比"去设置里改默认集合"快得多，
        // 而且不会把一次临时查看变成永久配置。
        ActiveSourceId = string.Equals(ActiveSourceId, id, StringComparison.OrdinalIgnoreCase) ? null : id;
    }

    void RefreshSources()
    {
        Sources.Clear();
        foreach (var entry in _broker.Providers)
        {
            var d = entry.Descriptor;
            Sources.Add(new SourceViewModel
            {
                Id = d.Id,
                DisplayName = d.DisplayName,
                Glyph = "\uE721",   // 搜索语义；后端自己的图标等 ProviderDescriptor 有图标字段再说
                Description = d.Description,
                IsAvailable = entry.Enabled,
                IsDefaultAuto = AutoSearchProviders.Contains(d.Id, StringComparer.OrdinalIgnoreCase),
                StatusText = entry.Enabled ? "就绪" : "已在设置中禁用",
                IsActive = string.Equals(ActiveSourceId, d.Id, StringComparison.OrdinalIgnoreCase),
            });
        }
        OnPropertyChanged(nameof(Sources));
        OnPropertyChanged(nameof(SourceLabel));
    }

    /// <summary>把本次查询每个后端的处置写回来源栏（"12 条" / "未参与" / "失败"）。</summary>
    void UpdateSourceStatus(IReadOnlyList<ProviderOutcome> outcomes)
    {
        foreach (var s in Sources)
        {
            var o = outcomes.FirstOrDefault(x => string.Equals(x.ProviderId, s.Id, StringComparison.OrdinalIgnoreCase));
            if (o is null) continue;
            // 数量也写回左栏：紧凑形态下它就是这一项唯一的数字（>9999 会被缩写成 ">9999"）
            s.Count = o.Kept;
            s.StatusText = o.State switch
            {
                ProviderOutcomeState.Running => "搜索中…",
                ProviderOutcomeState.Done => $"{o.Kept} 条",
                ProviderOutcomeState.Failed => "失败",
                ProviderOutcomeState.Timeout => "超时",
                ProviderOutcomeState.Skipped => "未参与",
                ProviderOutcomeState.Cancelled => "已取消",
                _ => o.State.ToString(),
            };
            if (o.State is ProviderOutcomeState.Failed or ProviderOutcomeState.Timeout)
                s.IsAvailable = false;
        }
    }

    // ─────────────── 预览窗格 ───────────────

    /// <summary>预览服务由宿主注入；为 null 时预览区显示"预览不可用"。</summary>
    public UniSearch.Host.Services.PreviewService? Preview { get; set; }

    /// <summary>预览面板是否展开（Ctrl+J 切换；UI-SPEC 里允许用户关掉它换更多列表空间）。</summary>
    [ObservableProperty]
    bool _isPreviewOpen = true;

    /// <summary>
    /// 左栏形态：<c>false</c> = 大（图标 + 名称 + 状态），<c>true</c> = 小（只有图标 + 一行数量）。
    /// 默认大 —— 第一次用的人得看清"点这里能只搜某个后端"。
    /// </summary>
    [ObservableProperty]
    bool _isSourceBarCompact;

    /// <summary>左栏切换按钮的字形：大形态给"向左收"，小形态给"向右展"。</summary>
    public string SourceBarToggleGlyph => IsSourceBarCompact ? "\uE76C" : "\uE76B";

    partial void OnIsSourceBarCompactChanged(bool value) => OnPropertyChanged(nameof(SourceBarToggleGlyph));

    [RelayCommand]
    public void ToggleSourceBar() => IsSourceBarCompact = !IsSourceBarCompact;

    /// <summary>结果表是否为空（显示空态提示，不留一片空白让人以为卡住了）。</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>空态文案：区分"还没输入"和"搜了没结果" —— 这两件事对用户的意思完全不同。</summary>
    public string EmptyHint => string.IsNullOrWhiteSpace(Input)
        ? $"输入关键词开始搜索\n当前来源：{SourceLabel}"
        : $"没有匹配的结果\n当前来源：{SourceLabel}";

    [ObservableProperty]
    UniSearch.Host.Services.PreviewResult _previewContent = UniSearch.Host.Services.PreviewResult.None;

    CancellationTokenSource? _previewRun;

    /// <summary>
    /// 选中变化就重新取预览。<b>先取消上一次</b>：按住 ↓ 快速划过时，
    /// 每行都启动一次解码/读盘会堆积，最终显示的可能还是旧行 —— 这是预览窗格最常见的 bug。
    /// </summary>
    void QueuePreview(ResultItemViewModel? row)
    {
        var mine = new CancellationTokenSource();
        Interlocked.Exchange(ref _previewRun, mine)?.Cancel();

        if (Preview is null || !IsPreviewOpen)
        {
            PreviewContent = UniSearch.Host.Services.PreviewResult.None;
            return;
        }

        // 多选：不预览"最后一个被点中的那一项"（会让人以为只选了它），改给汇总
        if (HasMultiSelection)
        {
            PreviewContent = new UniSearch.Host.Services.PreviewResult(
                UniSearch.Host.Services.PreviewKind.None, null, null, SelectionSummary);
            return;
        }

        if (row is null)
        {
            PreviewContent = UniSearch.Host.Services.PreviewResult.None;
            return;
        }

        var svc = Preview;
        var src = row.Source;
        _ = Task.Run(async () =>
        {
            var result = await svc.LoadAsync(src.Path, src.Extension, src.IsFolder, mine.Token)
                                    .ConfigureAwait(false);
            if (mine.Token.IsCancellationRequested) return;

            // 回到 UI 线程写（ImageSource 已在服务里 Freeze，可跨线程）
            _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!mine.Token.IsCancellationRequested) PreviewContent = result;
            });
        }, mine.Token);
    }

    [RelayCommand]
    public void TogglePreview()
    {
        IsPreviewOpen = !IsPreviewOpen;
        QueuePreview(Selected);
    }

    public bool HasPreviewImage => PreviewContent.HasImage;
    public bool HasPreviewText => PreviewContent.HasText;
    public bool HasPreviewMessage => PreviewContent.HasMessage;

    partial void OnPreviewContentChanged(UniSearch.Host.Services.PreviewResult value)
    {
        OnPropertyChanged(nameof(HasPreviewImage));
        OnPropertyChanged(nameof(HasPreviewText));
        OnPropertyChanged(nameof(HasPreviewMessage));
    }
    ResultItemViewModel? _previouslySelected;
    string? _selectedPath;

    /// <summary>
    /// 选中项是不是文件夹。菜单要据此分叉：文件夹本身就能被"打开"，
    /// 所以它只给「打开此文件夹」，不给「打开」和「打开所在目录」（那两项对文件夹是重复的）。
    /// </summary>
    public bool IsFolderSelection => Selected?.Source.IsFolder ?? false;

    /// <summary>选中项是文件（非文件夹）。「打开所在目录」只对文件有意义。</summary>
    public bool IsFileSelection => Selected is not null && !Selected.Source.IsFolder;

    /// <summary>可执行文件才给"以管理员身份运行"—— 对 .txt 弹 UAC 没有意义。</summary>
    public bool IsRunnableSelection
    {
        get
        {
            var ext = Selected?.Source.Extension;
            if (string.IsNullOrEmpty(ext)) return false;
            return ext.TrimStart('.').ToLowerInvariant()
                   is "exe" or "bat" or "cmd" or "com" or "msi" or "msp" or "ps1" or "vbs" or "scr";
        }
    }

    /// <summary>状态条：忙时“搜索中…”，否则报告结果规模 / 来源口径 / 语法提示 / 最近一次动作反馈。</summary>
    public string StatusText
    {
        get
        {
            if (IsBusy) return "搜索中…";
            if (!string.IsNullOrEmpty(ActionFeedback)) return ActionFeedback!;
            if (!string.IsNullOrEmpty(SyntaxNotice)) return SyntaxNotice!;
            // 多选：此刻用户关心的是"我选了几个"，压过结果规模那行
            if (HasMultiSelection && !string.IsNullOrEmpty(SelectionSummary))
                return $"{SelectionSummary}。右键批量操作 · Esc 取消选择";

            var shown = Rows.Count;
            var total = _fused.Count;
            // 空查询说"没有结果"是误导 —— 用户还没搜呢，得告诉他可以开始打字
            if (shown == 0 && string.IsNullOrWhiteSpace(Input))
                return $"输入关键词开始搜索（来源：{SourceLabel}）";
            if (shown == 0) return $"没有结果（来源：{SourceLabel}；换个关键词，或按 Esc 关闭）";

            // 两个数都给：列表有上限，只说"找到 N 条"会让人以为后端就只有这么多
            var head = total > shown ? $"显示 {shown} / 共 {total} 条" : $"找到 {shown} 条";
            return $"{head} · 来源 {SourceLabel}。Enter 打开 · Shift+Enter 所在目录 · 右键更多";
        }
    }

    [ObservableProperty]
    string _scopeLabel = "全局";

    [ObservableProperty]
    bool _isBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    [ObservableProperty]
    string? _syntaxNotice;
    partial void OnSyntaxNoticeChanged(string? value) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(HasSyntaxNotice)); }

    /// <summary>搜索行下方弹一句挑错式的语法提示（非阻塞）。</summary>
    public bool HasSyntaxNotice => !string.IsNullOrEmpty(SyntaxNotice);

    /// <summary>当前上下文。由 ShellContext / 热键入口设置后触发重跑。</summary>
    public SearchContext Context { get; private set; } = SearchContext.Global();

    /// <summary>顶部分类标签（含计数）。"全部"永远在第一位。</summary>
    public ObservableCollection<CategoryTab> Tabs { get; } = [];

    [ObservableProperty]
    string _selectedTabId = CategoryIds.All;

    public IReadOnlyList<ProviderOutcome> Outcomes { get; private set; } = [];

    long _requestCounter;

    public void SetContext(SearchContext ctx)
    {
        Context = ctx;
        ScopeLabel = ctx.Describe();
        _ = RunAsync();
    }

    [RelayCommand]
    void SelectTab(string id)
    {
        if (Tabs.Any(t => t.Id == id)) SelectedTabId = id;
    }

    partial void OnInputChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyHint));
        _ = RunAsync();
    }

    partial void OnSelectedTabIdChanged(string value)
    {
        foreach (var t in Tabs) t.IsSelectedTab = t.Id == value;
        _ = RunAsync();
    }

    /// <summary>新输入立即让旧请求作废：Broker 内部也只维持一个活动请求。</summary>
    [RelayCommand]
    public async Task RunAsync()
    {
        var mine = new CancellationTokenSource();
        Interlocked.Exchange(ref _run, mine)?.Cancel();
        // 注意：不 Dispose 旧的（可能正被枚举），交给 GC；只 Cancel 即可安全终止。

        var requestId = Interlocked.Increment(ref _requestCounter);
        var q = UniSearch.Core.Parsing.QueryParser.Parse(requestId, Input);

        // 自定义筛选器（filters.json）→ 查询过滤器，并重建 ProviderText。
        // 只改 Filters 不重建 ProviderText 的话，后端拿到的还是没过滤的查询串：
        // 它会把整库结果搬过来再由 Core 后过滤（几十万条 vs 几十条）。
        if (CurrentFilter is { } filter)
        {
            var merged = q.Filters with
            {
                Extensions = filter.Extensions,
                Kinds = filter.Kinds.Select(k => Enum.Parse<ResultKind>(k, ignoreCase: true)).ToList(),
            };
            q = UniSearch.Core.Parsing.QueryParser.WithFilters(q, merged);
        }

        // ForcedCategory 只对<b>内置分类</b>有意义：自定义筛选器 id 不是分类 id，
        // 交给 Core 硬过滤会一条都剩不下（筛选器已经用 ext:/kind: 表达了同一件事）。
        var builtinCategory = Catalog.Find(SelectedTabId) is null && SelectedTabId != CategoryIds.All
            ? SelectedTabId : null;

        q = q with
        {
            ForcedCategory = builtinCategory,
            ListScopeContents = string.IsNullOrWhiteSpace(Input),
            // 来源限定：只让当前选中的后端参与。这决定后端跑不跑，不只是过滤显示
            ProviderScope = EffectiveProviderScope,
        };

        IsBusy = true;
        SyntaxNotice = q.SyntaxNotice;
        ActionFeedback = null;   // 上一次动作的"已复制…"提示到此为止
        // 诊断用：筛选器到底翻译成了什么查询串（自检与 host.log 都看它）
        LastProviderText = q.ProviderText;
        LastFilterLabel = CurrentFilter?.Name;
        var token = mine.Token;
        try
        {
            await foreach (var snap in _broker.RunAsync(q, Context, token))
            {
                if (!ReferenceEquals(_run, mine)) break;      // 已被更新的输入取代
                Apply(snap);
            }
        }
        catch (OperationCanceledException) { /* 正常路径 */ }
        finally
        {
            if (ReferenceEquals(_run, mine)) IsBusy = false;
        }
    }

    void Apply(SearchSnapshot snap)
    {
        // 平铺单表：把各分类的行按相关度摊平成一列（分类不再靠分组表达，
        // 而是"类型"列 + 顶部标签的硬过滤）。保留相关度次序，供"点回相关度"用。
        _fused.Clear();
        foreach (var g in snap.Groups) _fused.AddRange(g.Items);
        _fused.Sort((a, b) => b.Score.CompareTo(a.Score));

        Tabs.Clear();
        var shown = snap.Groups.Where(g => g.CategoryId != CategoryIds.All).ToList();
        var totalAll = shown.Sum(g => g.TotalAvailable);
        var tabs = new List<CategoryTab>
        {
            new(CategoryIds.All, "全部", totalAll, SnapshotMapper.GlyphFor(CategoryIds.All), 0),
        };
        foreach (var g in shown)
            tabs.Add(new CategoryTab(g.CategoryId, g.DisplayName, g.TotalAvailable,
                                     SnapshotMapper.GlyphFor(g.CategoryId), CategoryEngine.OrderOf(g.CategoryId)));

        // 自定义筛选器（filters.json）：只列<b>当前来源适用</b>的那些 ——
        // 这就是"不同后端用不同筛选器"的落点。计数按当前结果集现算（筛选器不属于 Core 的分类体系）。
        foreach (var f in Catalog.For(EffectiveProviderScope))
        {
            var count = _fused.Count(x => f.Matches(x.Display));
            tabs.Add(new CategoryTab(f.Id, f.Name, count,
                                     f.Glyph ?? SnapshotMapper.GlyphFor(CategoryIds.More), f.Order, f));
        }

        tabs.Sort((a, b) => a.Order.CompareTo(b.Order));

        // 恢复选中：keep 旧选择（若快照仍有该分类），否则回"全部"。
        // 注意只能在这里统一 Add —— 先前若已 Tabs.Add 过一次，标签就会重复出现（实测出现过两个「全部」）。
        var selId = SelectedTabId;
        foreach (var t in tabs)
        {
            t.IsSelectedTab = t.Id == selId;
            Tabs.Add(t);
        }

        Outcomes = snap.Outcomes;
        OnPropertyChanged(nameof(Outcomes));
        UpdateSourceStatus(snap.Outcomes);

        RebuildRows();

        _log?.Info("ui", $"快照 req={snap.RequestId} fused={snap.TotalFused} complete={snap.IsComplete} " +
                         $"来源=[{SourceLabel}] 行={Rows.Count}/{_fused.Count} " +
                         $"排序={ResultSort.ToConfigName(SortKey)}{(SortDescending ? "↓" : "↑")} " +
                         $"可见列=[{string.Join(",", Columns.Where(c => c.IsVisible).Select(c => c.Key))}] " +
                         $"选中={(Selected?.Title ?? "null")}");
    }



    /// <summary>↑↓ 移动选中项（在平铺表里就是相邻行）。返回 false 表示没有可选项。</summary>
    public bool MoveSelection(int delta)
    {
        if (Rows.Count == 0) return false;
        var idx = Selected is null ? -1 : Rows.IndexOf(Selected);
        if (idx < 0) idx = delta > 0 ? -1 : Rows.Count;   // 没有选中时：向下从第一行开始，向上从最后一行
        idx = Math.Clamp(idx + delta, 0, Rows.Count - 1);
        Selected = Rows[idx];
        return true;
    }

    /// <summary>
    /// Tab = 切到下一个分类标签。
    /// 平铺单表后没有"分组"可跳了（原来跳的是组首项），跳分类才是等价的快捷动作。
    /// </summary>
    public bool SelectNextCategory()
    {
        if (Tabs.Count <= 1) return false;
        var idx = 0;
        for (var i = 0; i < Tabs.Count; i++)
            if (string.Equals(Tabs[i].Id, SelectedTabId, StringComparison.Ordinal)) { idx = i; break; }
        SelectedTabId = Tabs[(idx + 1) % Tabs.Count].Id;
        return true;
    }

    public bool SelectNthCategory(int oneBased)
    {
        if (oneBased <= 0 || oneBased >= Tabs.Count) return false;
        SelectedTabId = Tabs[oneBased].Id;
        return true;
    }

    /// <summary>Enter / 双击 / 右键"打开"：文件夹=Explorer；条目=其 Uri；其余=ShellExecute 文件。</summary>
    [RelayCommand]
    public void OpenSelected()
    {
        if (Selected is null) return;
        var r = Selected.Source;
        if (!string.IsNullOrEmpty(r.Path) && r.IsFolder)
            Report(_launcher.OpenFolder(r.Path), $"已打开文件夹 {r.Path}");
        else if (!string.IsNullOrEmpty(r.Uri))
            Report(_launcher.StartUri(r.Uri!), "已打开链接");
        else if (!string.IsNullOrEmpty(r.Path))
            Report(_launcher.OpenFile(r.Path), $"已打开 {r.Title}");
    }

    /// <summary>Shift+Enter / 右键"打开所在目录"：在 Explorer 中打开父目录并选中该项。</summary>
    [RelayCommand]
    public void RevealSelected()
    {
        if (Selected?.Source.Path is not { Length: > 0 } p) return;
        // 对文件夹用 /select, 会打开它的父目录并选中它 —— 这正是"打开所在目录"的语义
        Report(_launcher.OpenFolder(p, selectFile: true), "已在资源管理器中定位");
    }

    /// <summary>Ctrl+Enter：以管理员身份运行（用户取消 UAC 不算错误）。</summary>
    [RelayCommand]
    public void RunAsAdminSelected()
    {
        if (Selected?.Source.Path is not { Length: > 0 } p) return;
        Report(_launcher.RunAsAdmin(p), "已请求以管理员身份运行", "提权被取消或失败");
    }

    /// <summary>Ctrl+Shift+Enter：在终端中打开所在目录。</summary>
    [RelayCommand]
    public void OpenInConsoleSelected()
    {
        if (Selected is null) return;
        var dir = Selected.Source.IsFolder
            ? Selected.Source.Path
            : Selected.Source.Path is { Length: > 0 } p ? System.IO.Path.GetDirectoryName(p) : null;
        if (string.IsNullOrEmpty(dir)) return;
        Report(_launcher.OpenInConsole(dir), "已在终端中打开");
    }

    /// <summary>Alt+Enter：壳的"属性"对话框（必须走 ShellExecuteEx + SEE_MASK_INVOKEIDLIST 才打得开）。</summary>
    [RelayCommand]
    public void ShowPropertiesSelected()
    {
        if (Selected?.Source.Path is not { Length: > 0 } p) return;
        Report(_launcher.RunVerb(p, "properties"), "已打开属性", "打不开属性对话框");
    }

    /// <summary>Ctrl+C：复制路径。</summary>
    [RelayCommand]
    public void CopyPath()
    {
        if (Selected is null) return;
        CopyToClipboard(Selected.Source.Path ?? Selected.Source.Uri ?? Selected.Title);
        Report(true, "已复制路径");
    }

    /// <summary>Ctrl+Shift+C：只复制文件名。</summary>
    [RelayCommand]
    public void CopyName()
    {
        if (Selected is null) return;
        CopyToClipboard(Selected.Title);
        Report(true, "已复制文件名");
    }

    /// <summary>右键里由 Provider 声明的领域动作（Everything 默认没有；AnyTXT/Zotero 会用到）。</summary>
    [ObservableProperty]
    IReadOnlyList<ResultAction> _providerActions = [];

    public bool HasProviderActions => ProviderActions.Count > 0;

    void RefreshProviderActions()
    {
        var list = Array.Empty<ResultAction>();
        if (Selected?.Source is { ProviderId: { Length: > 0 } pid } src)
        {
            var entry = _broker.Providers.FirstOrDefault(x => x.Descriptor.Id == pid);
            if (entry?.Provider is IActionProvider ap)
            {
                try { list = ap.GetActions(src).ToArray(); }
                catch { list = []; }   // Provider 抛错不该连带把菜单打不开
            }
        }
        ProviderActions = list;
        OnPropertyChanged(nameof(HasProviderActions));
    }

    [RelayCommand]
    public async Task InvokeProviderActionAsync(ResultAction? action)
    {
        if (action is null || Selected is null) return;
        var pid = Selected.Source.ProviderId;
        var entry = _broker.Providers.FirstOrDefault(x => x.Descriptor.Id == pid);
        if (entry?.Provider is not IActionProvider ap) return;

        try
        {
            var res = await ap.ExecuteAsync(action, Selected.Source, CancellationToken.None);
            if (!res.Success && res.Message is { Length: > 0 } msg) Report(false, msg);
            else if (res.Success) Report(true, action.Label);
        }
        catch (Exception ex) { Report(false, $"{action.Label} 失败：{ex.Message}"); }
    }

    /// <summary>
    /// 把动作结果写进状态条 —— 键鼠操作必须看得见反馈。
    /// 失败也要写：原先失败就静默返回 false，用户看到的是"点了没反应"（属性那个 bug 就是这样被掩盖的）。
    /// </summary>
    void Report(bool ok, string okMessage, string failMessage = "操作失败，详见 host.log")
    {
        ActionFeedback = ok ? okMessage : failMessage;
    }

    /// <summary>最近一次动作的结果提示（状态条右侧显示一次，下次搜索清空）。</summary>
    [ObservableProperty]
    string? _actionFeedback;
    partial void OnActionFeedbackChanged(string? value) => OnPropertyChanged(nameof(StatusText));

    /// <summary>旧接口保留：Ctrl+C 走 CopyPath，这里给"复制可粘贴文本"用。</summary>
    public void CopySelected(bool pathOnly = false)
    {
        if (pathOnly) { CopyPath(); return; }
        if (Selected is null) return;
        CopyToClipboard(Selected.CopyText);
        Report(true, "已复制");
    }

    static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        System.Windows.Clipboard.SetText(text);
    }

    /// <summary>shell 菜单里宿主附加项的分发。返回 false 表示 id 不认识。</summary>
    bool HandleShellMenuItem(uint id)
    {
        switch (id)
        {
            case CustomReveal: RevealSelected(); return true;
            case CustomConsole: OpenInConsoleSelected(); return true;
            case CustomCopyPath: CopyPath(); return true;
            case CustomCopyName: CopyName(); return true;
            case CustomOpenBackend: OpenInBackend(null); return true;
            case CustomRunAsAdmin: RunAsAdminSelected(); return true;
        }
        return false;
    }

    /// <summary>Shell 菜单由宿主注入；为 null 时右键退回 WPF 菜单。</summary>
    public UniSearch.Host.Services.ShellContextMenu? ShellMenu { get; set; }

    /// <summary>宿主附加项的 id（与 <see cref="ShellContextMenu.CustomIdBase"/> 对应，顺序即菜单顺序）。</summary>
    const uint CustomReveal = 0x8001;
    const uint CustomConsole = 0x8002;
    const uint CustomCopyPath = 0x8003;
    const uint CustomCopyName = 0x8004;
    const uint CustomOpenBackend = 0x8005;
    const uint CustomRunAsAdmin = 0x8006;

    /// <summary>
    /// 弹出真正的 Windows shell 右键菜单（带"打开方式/发送到"与第三方动词），
    /// 并把宿主动作追加到尾部 —— 系统动词和我们的快捷项在同一个菜单里。
    /// 屏幕坐标必须是物理像素 —— <c>TrackPopupMenuEx</c> 只认物理像素。
    /// </summary>
    public void ShowShellMenu(string path, int screenX, int screenY, bool extendedVerbs)
    {
        if (ShellMenu is null) return;

        // 类型优先按选中项判定；选中项和 path 不一致时（理论上不该发生）退回问文件系统。
        var sel = Selected;
        var isFolder = sel is not null && string.Equals(sel.Source.Path, path, StringComparison.OrdinalIgnoreCase)
            ? sel.Source.IsFolder
            : Directory.Exists(path);

        var items = BuildShellMenuItems(
            isFolder,
            runnable: !isFolder && sel is not null && IsRunnableSelection,
            backendName: HasBackendTargets ? BackendTargets[0].Name : null);

        try
        {
            var ok = ShellMenu.Show(path, screenX, screenY, extendedVerbs, items, HandleShellMenuItem, out var error);
            if (ok) return;   // 动作自己会往状态条写反馈
            if (error.Length > 0 && error != "用户取消") Report(false, $"shell 菜单：{error}");
        }
        catch (Exception ex)
        {
            Report(false, $"shell 菜单失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 追加到 shell 菜单尾部的宿主动作。<b>必须按类型分叉</b> ——
    /// 否则文件夹和文件的菜单长得一模一样，用户一眼看不出选中的到底是哪一种
    /// （曾经的 bug：两类都固定给同样 4 项，"打开所在目录""复制文件名"对文件夹是错的口径）。
    /// </summary>
    /// <param name="backendName">可跳转的后端名（Everything…）；为 null 表示没有后端按钮。</param>
    internal static IReadOnlyList<(uint Id, string Label)> BuildShellMenuItems(
        bool isFolder, bool runnable, string? backendName)
    {
        var items = new List<(uint Id, string Label)>();

        if (isFolder)
        {
            // 文件夹：终端开在它<b>自己</b>里面；"打开所在目录"对文件夹应是"在父目录里定位它"
            items.Add((CustomConsole, "在终端中打开(&T)"));
            items.Add((CustomReveal, "在父目录中显示(&F)"));
            items.Add((CustomCopyPath, "复制路径(&C)"));
            items.Add((CustomCopyName, "复制文件夹名(&N)"));
        }
        else
        {
            items.Add((CustomReveal, "打开所在目录(&F)"));
            items.Add((CustomConsole, "在终端中打开(&T)"));
            items.Add((CustomCopyPath, "复制路径(&C)"));
            items.Add((CustomCopyName, "复制文件名(&N)"));
            // 只有可执行文件才给提权项：对 .txt 弹 UAC 是纯噪音
            if (runnable) items.Add((CustomRunAsAdmin, "以管理员身份运行(&A)"));
        }

        if (backendName is { Length: > 0 }) items.Add((CustomOpenBackend, $"在 {backendName} 中搜索(&E)"));

        return items;
    }

    /// <summary>选中项变化时，Provider 领域动作要跟着换。</summary>
    // （分组折叠随平铺单表一起取消了：表里没有组头可点）

    // ─────────────── 直达后端自己的程序（例如 Everything）───────────────

    /// <summary>可跳转的后端。只有声明了 <c>IExternalUiProvider</c> 且当前确实可用的才会出现在这里。</summary>
    public IReadOnlyList<BackendTarget> BackendTargets { get; private set; } = [];

    public bool HasBackendTargets => BackendTargets.Count > 0;

    void RefreshBackendTargets()
    {
        var list = new List<BackendTarget>();
        foreach (var entry in _broker.Providers)
        {
            if (entry.Provider is not IExternalUiProvider ext) continue;
            bool usable;
            try { usable = ext.CanOpenExternalUi; }
            catch { usable = false; }        // 探测失败不该让窗口起不来
            if (usable) list.Add(new BackendTarget(entry.Descriptor.Id, ext.ExternalUiName));
        }
        BackendTargets = list;
        OnPropertyChanged(nameof(BackendTargets));
        OnPropertyChanged(nameof(HasBackendTargets));
    }

    /// <summary>
    /// 把当前查询带到后端自己的界面（用户搜完想用后端的能力继续，例如 Everything 的筛选/排序/导出）。
    /// 查询串按"用户看得懂"的形式给：就是他输入的内容；若当前限定了目录，则补上 <c>ancestor:</c> 前缀，
    /// 否则跳过去之后范围会莫名其妙地变大。
    /// </summary>
    [RelayCommand]
    public void OpenInBackend(BackendTarget? target)
    {
        target ??= BackendTargets.FirstOrDefault();
        if (target is null) return;

        var entry = _broker.Providers.FirstOrDefault(p => p.Descriptor.Id == target.ProviderId);
        if (entry?.Provider is not IExternalUiProvider ext) return;

        var text = Input.Trim();
        var query = text;
        if (Context.IsDirectoryBounded && Context.RootPath is { Length: > 0 } root)
        {
            var dir = root.TrimEnd('\\', '/');
            query = $"ancestor:\"{dir}\\\" {text}".Trim();
        }

        Report(ext.OpenExternalUi(query),
               $"已在 {ext.ExternalUiName} 中打开",
               $"唤不起 {ext.ExternalUiName}（未安装或路径未知）");
    }
}

public sealed partial class CategoryTab : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public CategoryTab(string id, string displayName, int count, string glyph, int order = 500,
                       FilterDefinition? filter = null)
    {
        Id = id; DisplayName = displayName; Count = count; Glyph = glyph; Order = order; Filter = filter;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public int Count { get; }
    public string Glyph { get; }

    /// <summary>标签栏里的次序（内置分类 0–900，自定义筛选器默认 500）。</summary>
    public int Order { get; }

    /// <summary>非空 = 这是 filters.json 里的自定义筛选器（查询时要把它翻译成过滤器）。</summary>
    public FilterDefinition? Filter { get; }

    public string Label => Count > 0 ? $"{DisplayName}({Count})" : DisplayName;

    /// <summary>自定义筛选器加个视觉标记，让人一眼看出"这是我自己定义的"。</summary>
    public bool IsCustom => Filter is not null;

    public string TooltipText => Filter is { } f
        ? $"{f.Name}（自定义筛选器）{(f.Description is { Length: > 0 } d ? "：" + d : "")}"
        : DisplayName;

    public bool IsSelectedTab { get => _isSelectedTab; set => SetProperty(ref _isSelectedTab, value); }
    bool _isSelectedTab;
}

/// <summary>
/// 「直达后端程序」按钮的数据。一个后端一个按钮 —— 用 ItemsControl 渲染，
/// 将来 AnyTXT/Zotero 接上就自动多出按钮，不用改 XAML 布局。
/// </summary>
public sealed record BackendTarget(string ProviderId, string Name)
{
    public string TooltipText => $"在 {Name} 中打开当前搜索";
    public string Glyph => "\uE8A7";   // Segoe：OpenInNewWindow 语义
}
