using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniSearch.Core.Archiving;
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

    // ─────────────── 多选批量菜单（第 12 轮）───────────────

    /// <summary>右键落在哪一行时的处理决策。抽成纯函数是为了能自检断言 ——
    /// 真实右键手势（合成鼠标）会被 UIPI 拦掉，没法自动化。</summary>
    internal enum RightClickDecision { KeepSelection, ResetToRow }

    /// <summary>
    /// 已选中项上右键 → <b>保住整份选择</b>（否则一右键只剩一行，批量动作无从谈起）；
    /// 未选中项上右键 / 单选状态 → 重置为光标下那一行（资源管理器的习惯）。
    /// </summary>
    internal static RightClickDecision DecideRightClick(bool rowAlreadySelected, int selectedCount)
        => rowAlreadySelected && selectedCount > 1 ? RightClickDecision.KeepSelection
                                                   : RightClickDecision.ResetToRow;

    const uint BatchArchive = 0x8100;
    const uint BatchCopyPaths = 0x8101;
    const uint BatchCopyNames = 0x8102;
    const uint BatchConsole = 0x8103;
    const uint BatchCopyQuoted = 0x8104;

    /// <summary>批量菜单的项（id, 标签），顺序即显示顺序。
    /// 「压缩为 ZIP」排第一 —— 它是这条需求的正主（"多选然后压缩文件"）。</summary>
    internal static IReadOnlyList<(uint Id, string Label)> BuildBatchMenuItems(int count)
        => new List<(uint, string)>
        {
            (BatchArchive, "压缩为 ZIP(&Z)…"),
            (BatchCopyPaths, $"复制 {count} 个路径(&C)"),
            (BatchCopyQuoted, $"复制为引号列表(&Q)"),
            (BatchCopyNames, $"复制 {count} 个名称(&N)"),
            (BatchConsole, "在终端中打开(&T)"),
        };

    /// <summary>批量菜单的动作分发。返回 false 表示 id 不认识。</summary>
    internal bool RunBatchAction(uint id)
    {
        switch (id)
        {
            case BatchArchive: ArchiveSelectedAsZip(); return true;
            case BatchCopyPaths: CopySelectedPaths(); return true;
            case BatchCopyQuoted: CopySelectedPathsQuoted(); return true;
            case BatchCopyNames: CopySelectedNames(); return true;
            case BatchConsole: OpenInConsoleSelected(); return true;   // 取主选中项所在目录
        }
        return false;
    }

    /// <summary>
    /// 把多选压成一个 zip（第 12 轮的核心需求："多选然后压缩文件"）。
    /// <para>
    /// 包放**第一个选中项所在目录**，命名走 <c>ArchivePlanner.DefaultNameTemplate</c>
    /// （<c>{parent}-{count}项-{yyyyMMdd-HHmm}.zip</c>），重名自动加 <c> (2)</c>；
    /// 压缩在后台线程跑（大目录可能几十秒），完成后状态条报条目数与落点并在资源管理器里定位。
    /// </para>
    /// </summary>
    public void ArchiveSelectedAsZip()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) { Report(false, "选中的项没有本地路径，无法压缩"); return; }

        if (Archive.MaxItems > 0 && paths.Count > Archive.MaxItems)
        {
            Report(false, $"选中 {paths.Count} 项，超过上限 {Archive.MaxItems}（改 archive.maxItems 或分批压）");
            return;
        }

        var plan = ArchivePlanner.PlanEntries(paths);
        if (plan.Count == 0) { Report(false, "没有可压缩的条目"); return; }

        var firstDir = Path.GetDirectoryName(paths[0]);
        if (string.IsNullOrEmpty(firstDir)) firstDir = Environment.CurrentDirectory;
        var parentName = Path.GetFileName(firstDir.TrimEnd('\\'));
        var fileName = ArchivePlanner.BuildArchiveName(Archive.NameTemplate, parentName, plan.Count, DateTime.Now) + ".zip";

        // 落点是设置项：ask（每次弹保存对话框，满足"我想自己挑地方"）/ same-as-first（默认，最顺手）
        string zipPath;
        if (string.Equals(Archive.Destination, "ask", StringComparison.OrdinalIgnoreCase))
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "压缩到…",
                FileName = fileName,
                DefaultExt = ".zip",
                Filter = "ZIP 压缩包 (*.zip)|*.zip",
                InitialDirectory = firstDir,
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog() != true) { Report(true, "已取消压缩"); return; }
            zipPath = dlg.FileName;
        }
        else
        {
            zipPath = ArchivePlanner.EnsureUniqueFile(firstDir, fileName, File.Exists);
        }

        Report(true, $"正在压缩 {plan.Count} 项…");
        _log?.Info("archive", $"开始压缩 {plan.Count} 项 -> {zipPath}");
        _ = Task.Run(() =>
        {
            try
            {
                var (added, failures) = ArchiveService.CreateZip(plan, zipPath);
                _log?.Info("archive", $"压缩完成 added={added} failed={failures.Count} -> {zipPath}");
                Post(() =>
                {
                    Report(failures.Count == 0,
                           $"已压缩 {added} 个条目 → {zipPath}",
                           $"已压缩 {added} 个条目，{failures.Count} 项失败（详见 host.log）");
                    if (failures.Count > 0) _log?.Warn("archive", string.Join("; ", failures.Take(5)));
                    if (Archive.RevealAfter) RevealPathInExplorer(zipPath);
                });
            }
            catch (Exception ex)
            {
                _log?.Error("archive", "压缩失败", ex);
                Post(() => Report(false, $"压缩失败：{ex.Message}"));
            }
        });
    }

    /// <summary>回到 UI 线程（后台任务写状态条必须经过它，否则绑定会在非 UI 线程上被触发）。</summary>
    static void Post(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null) { action(); return; }
        app.Dispatcher.BeginInvoke(action);
    }

    /// <summary>在资源管理器里定位一个文件（压缩完顺手让用户看到包在哪）。</summary>
    static void RevealPathInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch { /* 定位失败不影响压缩结果本身 */ }
    }

    /// <summary>复制多选里的全部路径：<b>每行一条</b>（资源管理器"复制"粘贴到别处的常见口径）。</summary>
    public void CopySelectedPaths()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) { Report(false, "选中的项没有本地路径"); return; }
        CopyToClipboard(string.Join(Environment.NewLine, paths));
        Report(true, $"已复制 {paths.Count} 个路径");
    }

    /// <summary>复制多选里的全部名称（每行一条）。</summary>
    public void CopySelectedNames()
    {
        var names = _selection.Select(r => r.Title).Where(t => !string.IsNullOrEmpty(t)).ToList();
        if (names.Count == 0) { Report(false, "没有可复制的名称"); return; }
        CopyToClipboard(string.Join(Environment.NewLine, names));
        Report(true, $"已复制 {names.Count} 个名称");
    }

    /// <summary>
    /// 复制成**引号列表**（<c>"a" "b"</c>）：粘到命令行/脚本里当参数用，
    /// 路径带空格也不会被拆开。与"每行一条"是两个不同的使用场景，所以两个口径都给。
    /// </summary>
    public void CopySelectedPathsQuoted()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) { Report(false, "选中的项没有本地路径"); return; }
        CopyToClipboard(string.Join(" ", paths.Select(p => $"\"{p}\"")));
        Report(true, $"已复制 {paths.Count} 个路径（引号列表）");
    }

    // ─────────────── 结果表（平铺单表 + 可调列 + 列排序）───────────────

    /// <summary>当前显示的行：已排序、已按 <see cref="MaxRows"/> 截断。</summary>
    public ObservableCollection<ResultItemViewModel> Rows { get; } = [];

    /// <summary>
    /// 当前快照的全部融合结果，<b>保持相关度次序</b>（未截断）。
    /// 排序时复制一份再排 —— 这样"点回相关度"永远能回到 Broker 给出的原始次序，
    /// 而不是在上一次排序的结果上再排（那样会越点越乱）。
    /// </summary>
    readonly List<FusedResult> _fused = [];

    /// <summary>
    /// 最近一次快照里的分类分组。留着它是因为<b>模板切换要能重建标签栏而不重跑搜索</b> ——
    /// 标签栏 = 内置分类（来自快照的分组）+ 自定义筛选器（来自模板），只有后者会变。
    /// </summary>
    IReadOnlyList<CategoryGroup> _groups = [];

    /// <summary>
    /// <b>标签栏的基准</b>：最近一次"没加筛选器"（即「全部」）时的分组与行。
    /// <para>
    /// 标签栏必须按它算，<b>不能按当前结果集算</b>：点「期刊论文」之后结果全是文献条目，
    /// 「文档」那一组就不存在了 —— 标签跟着消失，等于<b>筛选把回去的路也一起删了</b>
    /// （实测踩到：Zotero 下点「期刊论文」后「文档」就没了）。
    /// 基准固定成「全部」，点任何筛选器就只改高亮、不改标签栏。
    /// </para>
    /// </summary>
    IReadOnlyList<CategoryGroup> _tabGroups = [];

    /// <summary>与 <see cref="_tabGroups"/> 配套的行集合（自定义筛选器的计数按它算，理由同上）。</summary>
    readonly List<FusedResult> _tabFused = [];

    /// <summary>本次查询是不是"没加任何筛选器"（是的话刷新标签栏基准）。</summary>
    bool _tabBaselineEligible = true;

    /// <summary>列表最多显示多少行。来自设置（<c>search.maxRows</c>）。</summary>
    public int MaxRows { get; set; } = SearchSettings.DefaultMaxRows;

    /// <summary>多选压缩的设置（落点 / 命名模板 / 完成后是否定位）。由 App 从 settings.json 注入。</summary>
    public ArchiveSettings Archive { get; set; } = new();

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
        // 换了来源 → 标签栏要按新后端重解析模板（没被钉住时就跟着走），
        // 并且把标签选择归零 —— 上一个后端选中的筛选器在新后端多半没有意义。
        RefreshTemplate(reSearch: false, providerChanged: true);
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
        // 默认来源集合变了 → "有没有某一个后端"也可能变了，模板要重解析
        RefreshTemplate(reSearch: false, providerChanged: true);
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

    // ── 筛选器模板（filters.json v2）──────────────────────────────────────────
    // 模板只决定"标签栏里显示哪几个、按什么顺序"，不碰匹配语义；没有任何模板时
    // ForTemplate 走平铺，与加模板之前逐项一致。

    Dictionary<string, string> _pinnedTemplates = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _deploymentTemplates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>没有"某一个后端"可归属时的手动选择（没地方钉，所以只作用于本次会话）。</summary>
    string? _sessionTemplateId;

    /// <summary>宿主注入：钉住 / 恢复默认时落盘（providerId → 模板 id；null = 删掉这个键）。</summary>
    public Action<string, string?>? PersistPinnedTemplate { get; set; }

    /// <summary>
    /// 解析模板时要认的后端，<b>只有限定到某一个时才认</b>。
    /// 来源栏是单选，所以常态就是一个；"没限定、但默认集合恰好只有一个"也算。
    /// 默认集合填了多个、或清空（不限制全部来源）时返回 null —— 那时没有"某个后端"可言，
    /// 解析链落到 <c>"*"</c> 兜底，再没有就平铺。<b>刻意不为多后端设计模板语义。</b>
    /// </summary>
    public string? TemplateProviderId =>
        ActiveSourceId is { Length: > 0 } id ? id
        : AutoSearchProviders.Count == 1 ? AutoSearchProviders[0]
        : null;

    /// <summary>当前生效的模板（null = 平铺，与文件里没有 templates 节时一致）。</summary>
    public FilterTemplate? ActiveTemplate { get; private set; }

    /// <summary>有没有模板可切 —— 没有就连锚点都不显示（升级第一天不该多出一个没用的按钮）。</summary>
    public bool HasTemplates => Catalog.Templates.Count > 0;

    /// <summary>锚点上显示的模板名。</summary>
    public string TemplateLabel => ActiveTemplate?.Name ?? "默认标签";

    /// <summary>当前模板是不是用户钉住的（锚点提示用）。</summary>
    public bool IsTemplatePinned =>
        TemplateProviderId is { Length: > 0 } pid && _pinnedTemplates.ContainsKey(pid);

    /// <summary>没钉住 = 跟着解析链走（下拉里"跟随后端默认"那条就是当前状态）。</summary>
    public bool TemplateFollowsDefault => !IsTemplatePinned;

    /// <summary>
    /// 锚点按钮的提示。必须说清"这个模板是怎么来的" —— 否则用户切了来源发现标签栏自己变了，
    /// 会以为是 bug 而不是"跟随默认"。
    /// </summary>
    public string TemplateAnchorTip => ActiveTemplate is null
        ? "当前没有模板生效，标签栏按定义文件平铺"
        : IsTemplatePinned ? $"模板「{ActiveTemplate.Name}」（已钉住：切来源不会自动跟随）"
        : TemplateProviderId is { Length: > 0 } pid
            ? $"模板「{ActiveTemplate.Name}」（跟随 {SnapshotMapper.Pretty(pid)} 自动切换）"
            : $"模板「{ActiveTemplate.Name}」（全局默认）";

    /// <summary>"跟随后端默认"那条的提示。</summary>
    public string ResetTemplateTip => "取消钉住，回到「后端认领 → \"*\" 兜底 → 平铺」这条解析链";

    /// <summary>锚点下拉里的条目（当前后端可选的模板；"跟随后端默认"是固定追加的一条）。</summary>
    public ObservableCollection<TemplateOption> TemplateOptions { get; } = [];

    /// <summary>当前钉住表（自检要能原样还原，所以暴露成只读）。</summary>
    public IReadOnlyDictionary<string, string> PinnedTemplates => _pinnedTemplates;

    /// <summary>当前部署级默认表（同上）。</summary>
    public IReadOnlyDictionary<string, string> DeploymentTemplates => _deploymentTemplates;

    /// <summary>宿主注入钉住表与部署级默认（<c>settings.filterTemplates</c> / <c>providers.*.options.filterTemplate</c>）。</summary>
    public void SetFilterTemplates(IReadOnlyDictionary<string, string>? pinned,
                                   IReadOnlyDictionary<string, string>? deployment)
    {
        _pinnedTemplates = pinned is null ? new(StringComparer.OrdinalIgnoreCase)
                                          : new(pinned, StringComparer.OrdinalIgnoreCase);
        _deploymentTemplates = deployment is null ? new(StringComparer.OrdinalIgnoreCase)
                                                  : new(deployment, StringComparer.OrdinalIgnoreCase);
        RefreshTemplate(reSearch: false);
    }

    /// <summary>用户在下拉里选了模板：单一后端时钉住并落盘，否则只作用于本次会话。</summary>
    [RelayCommand]
    void SelectTemplate(string? id)
    {
        if (Catalog.FindTemplate(id) is null) return;

        if (TemplateProviderId is { Length: > 0 } pid)
        {
            _pinnedTemplates[pid] = id!;
            PersistPinnedTemplate?.Invoke(pid, id);
        }
        else
        {
            _sessionTemplateId = id;
        }
        RefreshTemplate(reSearch: false);
    }

    /// <summary>恢复默认：删掉钉住，回到解析链（后端认领 → <c>"*"</c> → 平铺）。</summary>
    [RelayCommand]
    void ResetTemplate()
    {
        _sessionTemplateId = null;
        if (TemplateProviderId is { Length: > 0 } pid && _pinnedTemplates.Remove(pid))
            PersistPinnedTemplate?.Invoke(pid, null);
        RefreshTemplate(reSearch: false);
    }

    /// <summary>
    /// 重算当前模板并重建标签栏。<b>不重跑搜索</b> —— 切模板只是"标签栏显示哪几个"，
    /// 结果集与查询串都不变。唯一的例外见下面那段：当前选中的自定义筛选器被新模板藏掉了。
    /// </summary>
    /// <param name="reSearch">重建后是否重跑一次查询。</param>
    /// <param name="providerChanged">
    /// 这次重算是不是由"换来源"引起的。是的话<b>把标签选择归零</b>：筛选器本来就是按后端分叉的
    /// （模板那套正是干这个的），一个后端下选中的分类到了另一个后端多半毫无意义 ——
    /// Zotero 的「期刊论文」在 Everything 下不存在，AnyTXT 的「正文命中」在 Zotero 下也不存在。
    /// 留着它只会让人看到一个 0 结果、又说不清为什么的界面。
    /// </param>
    void RefreshTemplate(bool reSearch, bool providerChanged = false)
    {
        var pid = TemplateProviderId;

        ActiveTemplate = pid is null && _sessionTemplateId is { Length: > 0 } sid
            ? Catalog.FindTemplate(sid)
            : Catalog.ResolveTemplate(pid, Lookup(_pinnedTemplates, pid), Lookup(_deploymentTemplates, pid));

        // 换来源 → 值域形状与候选值都变了（Zotero 的标签在 Everything 下不存在）。
        // 必须做在 BuildTabs/重查之前：否则会带着上一个后端的标签去问新后端。
        if (providerChanged) ResetFacetSelection();

        RebuildTemplateOptions();
        OnPropertyChanged(nameof(ActiveTemplate));
        OnPropertyChanged(nameof(HasTemplates));
        OnPropertyChanged(nameof(TemplateLabel));
        OnPropertyChanged(nameof(IsTemplatePinned));
        OnPropertyChanged(nameof(TemplateFollowsDefault));
        OnPropertyChanged(nameof(TemplateAnchorTip));
        NotifyFacetShape();

        BuildTabs();

        // 换来源 → 无条件回「全部」；同一后端下重查 → 只处理"选中的筛选器被藏掉了"这种情况。
        // 两种都靠 SelectedTabId 的 setter 重跑一次查询（会取消调用方那次，不会出两份结果）。
        if (providerChanged && SelectedTabId != CategoryIds.All)
            SelectedTabId = CategoryIds.All;
        else if (CurrentFilter is not null && Tabs.All(t => t.Id != SelectedTabId))
            SelectedTabId = CategoryIds.All;
        else if (reSearch)
            _ = RunAsync();
    }

    void RebuildTemplateOptions()
    {
        TemplateOptions.Clear();
        foreach (var t in Catalog.TemplatesFor(TemplateProviderId))
            TemplateOptions.Add(new TemplateOption(t.Id, t.Name, t.Id == ActiveTemplate?.Id, IsTemplatePinned));
        // "跟随后端默认"不在这里 —— 它是固定追加的一条，绑 ResetTemplateCommand，
        // 而这里的每条都绑 SelectTemplateCommand（参数是模板 id）。混在一起就得靠 null 分派，更绕。
    }

    static string? Lookup(Dictionary<string, string> map, string? key) =>
        key is { Length: > 0 } && map.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    // ── 值域筛选器（候选值来自后端，见 IFacetProvider）─────────────────────────
    // 和前两种筛选都不一样：内置分类的值域是宿主的分类体系，filters.json 筛选器的值域是
    // 定义文件写的扩展名/类型，而这里的值域**只有后端自己知道**（Zotero 有哪些标签）。
    // 所以它不摊在标签栏上（标签可能上百个），而是一枚"点开才展开"的锚点：
    // 顶部只回显已选的那几个，展开面板里才是全部候选值。

    /// <summary>当前后端支持的值域。<b>只取第一个</b> —— 真有第二个（集合之类）时，
    /// 这一块要改成"锚点列表"，而不是在这里加分支。</summary>
    public FacetDescriptor? ActiveFacet
    {
        get
        {
            var p = ActiveFacetProvider();
            return p is { Facets.Count: > 0 } ? p.Facets[0] : null;
        }
    }

    /// <summary>当前"那个后端"的值域能力。与模板用同一个后端口径（<see cref="TemplateProviderId"/>）——
    /// 两个机制都在回答"现在是谁在给我供数据"。</summary>
    IFacetProvider? ActiveFacetProvider()
    {
        if (TemplateProviderId is not { Length: > 0 } pid) return null;
        foreach (var e in _broker.Providers)
            if (string.Equals(e.Descriptor.Id, pid, StringComparison.OrdinalIgnoreCase))
                return e.Provider as IFacetProvider;
        return null;
    }

    /// <summary>当前后端有没有值域（没有就连锚点都不显示）。</summary>
    public bool HasFacet => ActiveFacet is not null;

    public string FacetGlyph => ActiveFacet?.Glyph ?? "\uE8EC";

    /// <summary>锚点上显示的名字（"标签"）。</summary>
    public string FacetLabel => ActiveFacet?.DisplayName ?? string.Empty;

    /// <summary>展开面板的开关。</summary>
    [ObservableProperty]
    bool _isFacetOpen;

    /// <summary>候选值正在取（后端要读一次标签库）。</summary>
    [ObservableProperty]
    bool _isFacetLoading;

    /// <summary>多选口径：false = 任一命中（默认），true = 全部命中。</summary>
    [ObservableProperty]
    bool _facetMatchAll;

    public string FacetMatchAllLabel => FacetMatchAll ? "全部命中" : "任一命中";

    public string FacetMatchAllTip => FacetMatchAll
        ? "多个值「全部命中」—— 后端按 AND 下推（Zotero：重复 tag=）"
        : "多个值「任一命中」—— 后端按 OR 下推（Zotero：tag=A || B）";

    partial void OnFacetMatchAllChanged(bool value)
    {
        OnPropertyChanged(nameof(FacetMatchAllLabel));
        OnPropertyChanged(nameof(FacetMatchAllTip));
        OnPropertyChanged(nameof(FacetAnchorTip));
        // 只有一个值时两种口径结果相同 —— 不为它白跑一次后端
        if (_selectedFacetValues.Count > 1) _ = RunAsync();
    }

    /// <summary>已选的值（顶部只回显这些）。</summary>
    public ObservableCollection<FacetChip> SelectedFacets { get; } = [];

    /// <summary>展开面板里的全部候选值。</summary>
    public ObservableCollection<FacetCandidate> FacetCandidates { get; } = [];

    /// <summary>
    /// 已选值的真相：存的是<b>下推值</b>（<c>FacetValue.Pushdown</c>），不是显示名。
    /// 思源的笔记本显示"R语言"而下推要 id —— 存显示名会让 SQL 查出 0 条（实测踩到）。
    /// </summary>
    readonly List<string> _selectedFacetValues = [];

    /// <summary>读候选值失败的原因（要如实显示，不能装作"这个库里没有标签"）。</summary>
    [ObservableProperty]
    string? _facetError;

    public bool HasFacetSelection => _selectedFacetValues.Count > 0;

    /// <summary>已选值的下推值列表（自检用）。</summary>
    public IReadOnlyList<string> SelectedFacetValues => _selectedFacetValues;

    /// <summary>已选值的显示名（状态条与提示用；找不到候选就退回下推值本身）。</summary>
    IReadOnlyList<string> SelectedFacetDisplays =>
        _selectedFacetValues.Select(k => FacetCandidates.FirstOrDefault(c => c.Key == k)?.Value ?? k).ToList();

    /// <summary>锚点提示：必须说清"候选值是从后端拿的"，否则用户会以为是程序内置的固定列表。</summary>
    public string FacetAnchorTip
    {
        get
        {
            if (ActiveFacet is not { } f) return string.Empty;
            var source = TemplateProviderId is { Length: > 0 } pid ? SnapshotMapper.Pretty(pid) : "当前来源";
            if (_selectedFacetValues.Count == 0)
                return $"{f.DisplayName}：候选值来自{source}自己" + (f.Tip is null ? "" : "\n" + f.Tip);
            return $"已选 {_selectedFacetValues.Count} 个{f.DisplayName}（{FacetMatchAllLabel}）\n来源：{source}\n" +
                   string.Join("、", SelectedFacetDisplays);
        }
    }

    /// <summary>状态条上的一句话口径（null = 没在按值域筛）。</summary>
    public string? FacetSummary => _selectedFacetValues.Count == 0 || ActiveFacet is not { } f
        ? null
        : $"{f.DisplayName}：{string.Join("、", SelectedFacetDisplays)}（{FacetMatchAllLabel}）";

    /// <summary>最近一次真正带上的值域选择（诊断用：一眼看出它有没有进查询）。</summary>
    public string? LastFacetLabel { get; private set; }

    [RelayCommand]
    void ToggleFacetValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (!_selectedFacetValues.Remove(value)) _selectedFacetValues.Add(value);
        SyncFacetSelection();
        _ = RunAsync();
    }

    [RelayCommand]
    void RemoveFacetValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || !_selectedFacetValues.Remove(value)) return;
        SyncFacetSelection();
        _ = RunAsync();
    }

    [RelayCommand]
    void ClearFacetValues()
    {
        if (_selectedFacetValues.Count == 0) return;
        _selectedFacetValues.Clear();
        SyncFacetSelection();
        _ = RunAsync();
    }

    [RelayCommand]
    void ToggleFacetMatchAll() => FacetMatchAll = !FacetMatchAll;

    /// <summary>已选值的真相在 <see cref="_selectedFacetValues"/>，这里把它同步给三处视图：
    /// 顶部回显、展开面板的勾选状态、以及几个依赖它的提示文本。</summary>
    void SyncFacetSelection()
    {
        SelectedFacets.Clear();
        foreach (var key in _selectedFacetValues)
        {
            // chip 显示名字、按 key 移除 —— 两者可能不同（思源的笔记本）
            var display = FacetCandidates.FirstOrDefault(c => c.Key == key)?.Value ?? key;
            SelectedFacets.Add(new FacetChip(key, display));
        }

        foreach (var c in FacetCandidates)
            c.IsChecked = _selectedFacetValues.Contains(c.Key);

        OnPropertyChanged(nameof(HasFacetSelection));
        OnPropertyChanged(nameof(FacetAnchorTip));
        OnPropertyChanged(nameof(FacetSummary));
    }

    /// <summary>后端变了（或值域形状变了）：清掉选择与候选 —— 上一个后端的标签在另一个后端毫无意义。
    /// 这与"换来源时标签选择归零"是同一条理由。</summary>
    void ResetFacetSelection()
    {
        _selectedFacetValues.Clear();
        FacetCandidates.Clear();
        FacetError = null;
        IsFacetOpen = false;
        SyncFacetSelection();
        NotifyFacetShape();
    }

    void NotifyFacetShape()
    {
        // ⚠ 每一个都要通知到。漏一个的症状是"图标对、文字空" ——
        // 绑定只求值一次，换来源时没收到通知的属性会一直留着上一个后端的值
        // （实测踩到：FacetLabel 漏了通知，锚点上只剩一个图标，看不出是什么筛选器）。
        OnPropertyChanged(nameof(ActiveFacet));
        OnPropertyChanged(nameof(HasFacet));
        OnPropertyChanged(nameof(FacetGlyph));
        OnPropertyChanged(nameof(FacetLabel));
        OnPropertyChanged(nameof(FacetAnchorTip));
        OnPropertyChanged(nameof(FacetSummary));
    }

    partial void OnIsFacetOpenChanged(bool value)
    {
        // 展开时才去读候选值：它要真跑一趟后端，而多数查询根本不碰值域筛选器
        if (value) _ = LoadFacetCandidatesAsync();
    }

    /// <summary>读候选值。失败要显示原因 —— 空面板 + 无解释是最糟的结果（分不清"没标签"和"读不到"）。</summary>
    public async Task LoadFacetCandidatesAsync()
    {
        if (ActiveFacet is not { } facet || ActiveFacetProvider() is not { } provider) return;

        IsFacetLoading = true;
        FacetError = null;
        try
        {
            var values = await provider.GetFacetValuesAsync(facet.Id, Context ?? SearchContext.Global(),
                                                            CancellationToken.None);
            // 读的过程中用户又换了后端 —— 这批候选已经不属于当前上下文了
            if (!ReferenceEquals(provider, ActiveFacetProvider())) return;

            if (!values.Ok)
            {
                FacetCandidates.Clear();
                FacetError = values.Error;
                return;
            }

            FacetCandidates.Clear();
            foreach (var v in values.Values)
                FacetCandidates.Add(new FacetCandidate(v.Value, v.Count, v.Pushdown)
                {
                    IsChecked = _selectedFacetValues.Contains(v.Pushdown),
                });
        }
        catch (Exception ex)
        {
            FacetError = ex.Message;
            _log?.Warn("ui", $"读取值域 {facet.Id} 失败：{ex.Message}", ex);
        }
        finally
        {
            IsFacetLoading = false;
        }
    }

    /// <summary>宿主加载完 filters.json 后注入（改文件后重启程序生效）。</summary>
    public void SetFilterCatalog(FilterCatalog catalog)
    {
        Catalog = catalog;
        OnPropertyChanged(nameof(Catalog));
        RefreshTemplate(reSearch: true);   // 标签栏与过滤条件都变了，重跑一次
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
                // B4：解析到真图标前先用后端声明的兜底字形（各不相同）；没声明 Icon 的退回通用放大镜
                Glyph = d.Icon?.FallbackGlyph ?? "\uE721",
                Description = d.Description,
                IsAvailable = entry.Enabled,
                IsDefaultAuto = AutoSearchProviders.Contains(d.Id, StringComparer.OrdinalIgnoreCase),
                StatusText = entry.Enabled ? "就绪" : "已在设置中禁用",
                IsActive = string.Equals(ActiveSourceId, d.Id, StringComparison.OrdinalIgnoreCase),
            });
        }
        OnPropertyChanged(nameof(Sources));
        OnPropertyChanged(nameof(SourceLabel));
        LoadSourceIcons();
    }

    /// <summary>
    /// B4：后台解析各后端的软件图标。开始菜单扫描可能几十毫秒，不能在 UI 线程干等；
    /// 到位后回 UI 线程填充 <see cref="SourceViewModel.Icon"/>，模板自动从字形切到真图标。
    /// 结果按 providerId 缓存在 <see cref="ProviderIconResolver"/> 里，重建 Sources 不重扫。
    /// </summary>
    void LoadSourceIcons()
    {
        foreach (var s in Sources)
        {
            var decl = _broker.Providers.FirstOrDefault(x => x.Descriptor.Id == s.Id)?.Descriptor.Icon;
            if (decl is null) continue;

            var source = s;   // 闭包捕获，别让 foreach 变量坑
            _ = Task.Run(() => ProviderIconResolver.Resolve(s.Id, decl))
                .ContinueWith(t =>
                {
                    if (t.Result is { } hit) source.Icon = hit.Icon;
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion,
                   TaskScheduler.FromCurrentSynchronizationContext());
        }
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
        var broker = _broker;
        _ = Task.Run(async () =>
        {
            var result = await LoadPreviewAsync(broker, svc, src, mine.Token).ConfigureAwait(false);
            if (mine.Token.IsCancellationRequested) return;

            // 回到 UI 线程写（ImageSource 已在服务里 Freeze，可跨线程）
            _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!mine.Token.IsCancellationRequested) PreviewContent = result;
            });
        }, mine.Token);
    }

    /// <summary>
    /// 预览内容取数：本地文件走 <see cref="PreviewService"/> 主管线；
    /// <b>没有路径的结果（思源的块、在线条目）先问 Provider 自己</b> ——
    /// <see cref="IPreviewProvider"/> 是 Provider 供预览的口子，思源的跨机 Markdown 预览从这走。
    /// </summary>
    static async Task<UniSearch.Host.Services.PreviewResult> LoadPreviewAsync(
        SearchBroker broker, PreviewService svc, SearchResult src, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(src.Path))
            return await svc.LoadAsync(src.Path, src.Extension, src.IsFolder, ct).ConfigureAwait(false);

        var entry = broker.Providers.FirstOrDefault(x =>
            string.Equals(x.Descriptor.Id, src.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (entry?.Provider is IPreviewProvider provider)
        {
            try
            {
                var content = await provider.GetPreviewAsync(
                    src, new PreviewRequest(0, 0, 0, WantsThumbnails: false), ct).ConfigureAwait(false);
                PreviewService.Trace?.Invoke(
                    $"无路径结果 {src.ProviderId} 走 Provider 预览 -> " +
                    (content is null ? "null（拒答）" : $"{content.Kind} {(content.Text?.Length ?? 0)} 字"));
                if (content is not null) return MapProviderPreview(content);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* Provider 已把失败写进 host.log；这里照旧降级，不崩 */ }
        }

        // 对"天生没有路径"的结果，"文件不存在"是在撒谎 —— 如实说没有可预览的
        return new UniSearch.Host.Services.PreviewResult(
            UniSearch.Host.Services.PreviewKind.Unavailable, null, null, "没有可用的预览", src.Uri);
    }

    /// <summary>Sdk 的 <c>PreviewContent</c> → 宿主预览面板。文本类（含 Markdown 源码）走同一个文本模板。</summary>
    static UniSearch.Host.Services.PreviewResult MapProviderPreview(PreviewContent content) => content.Kind switch
    {
        UniSearch.Sdk.Contracts.PreviewKind.Text or UniSearch.Sdk.Contracts.PreviewKind.Markdown
            or UniSearch.Sdk.Contracts.PreviewKind.Html
            => new UniSearch.Host.Services.PreviewResult(
                UniSearch.Host.Services.PreviewKind.Text, null, content.Text, null, content.Title),
        UniSearch.Sdk.Contracts.PreviewKind.Fields when content.Fields.Count > 0
            => new UniSearch.Host.Services.PreviewResult(
                UniSearch.Host.Services.PreviewKind.Text, null,
                string.Join(Environment.NewLine, content.Fields.Select(f =>
                    string.IsNullOrEmpty(f.Value) ? f.Label : $"{f.Label}: {f.Value}")),
                null, content.Title),
        _ => new UniSearch.Host.Services.PreviewResult(
                UniSearch.Host.Services.PreviewKind.Unavailable, null, null,
                $"该后端不支持「{content.Kind}」形态的预览"),
    };

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

    /// <summary>
    /// 已发起的查询次数。给自检用 —— 像"切模板不该重查"这种承诺，只有能数出来才验得了。
    /// </summary>
    public long SearchRequestCount => Interlocked.Read(ref _requestCounter);

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
        var merged = q.Filters;
        var filtersChanged = false;

        if (CurrentFilter is { } filter)
        {
            merged = merged with
            {
                Extensions = filter.Extensions,
                Kinds = filter.Kinds.Select(k => Enum.Parse<ResultKind>(k, ignoreCase: true)).ToList(),
                // ⚠ 三个维度都要带上。少带一个的后果不是报错，是**筛选静默失效** ——
                // 点「期刊论文」得到和「全部」一样的结果，而没有任何地方能看出是这里漏了
                // （实测踩到：Zotero 的条目类型筛选器就是这么"点了没用"的）。
                Subtypes = filter.Subtypes,
            };
            filtersChanged = true;
        }

        // 值域筛选（标签）：候选值来自后端，由后端自己下推。
        // 只有"当前后端声明了值域、且真的选了值"时才带上 —— 带上它，调度器就会把
        // 不懂这个域的后端跳过（见 ProviderSelector 的 NotApplicableToFacetFilter）。
        LastFacetLabel = null;
        if (ActiveFacet is { } facet && _selectedFacetValues.Count > 0)
        {
            merged = merged with
            {
                Facets = [new FacetSelection(facet.Id, [.. _selectedFacetValues], FacetMatchAll)],
            };
            filtersChanged = true;
            LastFacetLabel = $"{facet.Id}={(FacetMatchAll ? "全部" : "任一")}:{string.Join("|", _selectedFacetValues)}";
        }

        if (filtersChanged) q = UniSearch.Core.Parsing.QueryParser.WithFilters(q, merged);

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

        // 只有"没加任何筛选器"的这次查询才够格刷新标签栏基准（见 _tabGroups 的注释）。
        // 值域选择也算"加了筛选器"：否则一勾标签，分类标签栏就会按筛过的结果重建 —— 与
        // "点分类不该把其他标签删掉"是同一个坑。
        _tabBaselineEligible = builtinCategory is null && CurrentFilter is null && _selectedFacetValues.Count == 0;

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

        _groups = snap.Groups;

        // 标签栏基准只在"没加筛选器"时刷新。加了筛选器时保留上一次的基准，
        // 这样点「期刊论文」不会把「文档」那个标签弄没（筛选不该删掉回去的路）。
        if (_tabBaselineEligible)
        {
            _tabGroups = snap.Groups;
            _tabFused.Clear();
            _tabFused.AddRange(_fused);
        }

        BuildTabs();

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

    /// <summary>
    /// 重建标签栏：内置分类（来自最近一次快照的分组）+ 自定义筛选器（来自当前模板与来源）。
    /// <para>
    /// <b>为什么从 <see cref="Apply"/> 里拆出来</b>：模板切换只影响后半截，逼着重新搜一次
    /// 既慢又没必要（切模板不改查询串）。拆出来之后两条路径共用同一段逻辑，不会各自跑偏。
    /// </para>
    /// </summary>
    void BuildTabs()
    {
        Tabs.Clear();
        // 用 _tabGroups（「全部」时的基准），不是 _groups（当前可能已被筛过）；
        // 再按当前后端掐掉"在这个后端下没有意义"的分类（例如 AnyTXT 下的「正文命中」恒等于全部）。
        var scopeProvider = TemplateProviderId;
        // ⚠ 「全部」的计数必须把所有分组都算进来，**不能只算可见的那几个** ——
        // 被按后端隐藏的分类（AnyTXT/思源下的「正文命中」）照样有结果，
        // 漏掉它们会让「全部(0)」和结果表里的几十行同时出现在屏幕上。
        var allGroups = _tabGroups.Where(g => g.CategoryId != CategoryIds.All).ToList();
        var shown = allGroups
            .Where(g => CategoryEngine.AppliesToProvider(g.CategoryId, scopeProvider))
            .ToList();
        var totalAll = allGroups.Sum(g => g.TotalAvailable);
        var tabs = new List<CategoryTab>
        {
            new(CategoryIds.All, "全部", totalAll, SnapshotMapper.GlyphFor(CategoryIds.All), 0),
        };
        foreach (var g in shown)
            tabs.Add(new CategoryTab(g.CategoryId, g.DisplayName, g.TotalAvailable,
                                     SnapshotMapper.GlyphFor(g.CategoryId), CategoryEngine.OrderOf(g.CategoryId)));

        // 自定义筛选器（filters.json）：只列<b>当前模板与来源下可见</b>的那些 ——
        // 这就是"不同后端用不同筛选器"的落点。没有模板时 ForTemplate 走平铺，与从前一致。
        // 计数按当前结果集现算（筛选器不属于 Core 的分类体系）。
        foreach (var f in Catalog.ForTemplate(ActiveTemplate, EffectiveProviderScope))
        {
            // 计数也按基准行集合算 —— 否则筛一下之后其他筛选器的计数全变 0，
            // 看起来像"这些筛选器坏了"
            var count = _tabFused.Count(x => f.Matches(x.Display));
            tabs.Add(new CategoryTab(f.Id, f.Name, count,
                                     f.Glyph ?? SnapshotMapper.GlyphFor(CategoryIds.More), f.Order, f));
        }

        // ⚠ 当前选中的内置分类，哪怕这次一条都没归到它，**也必须留着**。
        // 分类标签是按"快照里有哪些分组"建的，而分组是按**结果**建的 —— 于是点一个分类之后
        // 重查如果一条都没落进去，那个分组就没了、标签也跟着消失，但 SelectedTabId 还停在它上面：
        // 筛选仍在生效、用户却看不见也点不掉（实测踩到：AnyTXT 的「正文命中」就是这么消失的）。
        // 保留一个 0 计数的标签，至少让"当前在筛什么"是可见、可撤销的。
        if (SelectedTabId is { Length: > 0 } sel && sel != CategoryIds.All &&
            Catalog.Find(sel) is null &&                     // 自定义筛选器走另一条路（见下）
            tabs.All(t => t.Id != sel))
        {
            tabs.Add(new CategoryTab(sel, CategoryEngine.DisplayNameOf(sel), 0,
                                     SnapshotMapper.GlyphFor(sel), CategoryEngine.OrderOf(sel)));
        }

        // 模板内顺序已由 ForTemplate 按"引用先后"给出，但内置分类仍按 order 排 ——
        // 所以这里只给自定义筛选器保持相对次序：OrderBy 是稳定的。
        tabs.Sort((a, b) => a.Order.CompareTo(b.Order));

        // 恢复选中：keep 旧选择（若快照仍有该分类），否则回"全部"。
        // 注意只能在这里统一 Add —— 先前若已 Tabs.Add 过一次，标签就会重复出现（实测出现过两个「全部」）。
        var selId = SelectedTabId;
        foreach (var t in tabs)
        {
            t.IsSelectedTab = t.Id == selId;
            Tabs.Add(t);
        }
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

/// <summary>
/// 模板锚点下拉里的一条。<c>Id</c> 是模板 id，直接当 <c>SelectTemplateCommand</c> 的参数。
/// </summary>
public sealed record TemplateOption(string Id, string Name, bool IsActive, bool IsPinned)
{
    public string TooltipText => IsActive
        ? IsPinned ? "当前模板（已钉住：切来源不会自动跟随）" : "当前模板"
        : $"切换到「{Name}」";
}
