using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using UniSearch.Core.Categories;
using UniSearch.Host.Services;
using UniSearch.Host.Settings;
using UniSearch.Host.ViewModels;

namespace UniSearch.Host.Views;

/// <summary>
/// 主窗口：左（来源）· 中（详细列表）· 右（预览）三栏。
/// <para>
/// 结果表用 <see cref="GridView"/> 而不是手写 Grid：列宽拖拽、列头点击这些交互是它自带的，
/// 自己实现一遍只会更脆。列本身<b>不写死在 XAML 里</b> —— 可见列、顺序、宽度都来自设置，
/// 所以在这里按 <see cref="SearchSessionViewModel.Columns"/> 动态装配。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    SearchSessionViewModel Vm => (SearchSessionViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        // 列头点击排序：Click 是冒泡路由事件，挂在 ListView 上就能收到所有列头的点击
        ResultList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnColumnHeaderClick));
        ResultGrid.ColumnHeaderContextMenu = BuildColumnMenuTemplate();
        ResultGrid.Columns.CollectionChanged += OnGridColumnsReordered;
        Loaded += OnLoaded;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        ApplyPaneVisibility();
        AutoFitFillerColumn();
    }

    /// <summary>
    /// 接"再启动一次"的广播：第二个实例只负责广播消息就退出，唤出由已有窗口完成。
    /// 不做这件事，用户再双击一次图标看到的是"什么都没发生"（或者更糟：多出一个托盘图标）。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        (PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource)?.AddHook(WndProc);
    }

    nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (Services.SingleInstance.SummonMessage != 0
            && (uint)msg == Services.SingleInstance.SummonMessage)
        {
            Summon();
            handled = true;
        }
        return 0;
    }

    // ─────────────── 列装配（设置 → GridView）───────────────

    SearchSessionViewModel? _hooked;

    void HookViewModel()
    {
        if (DataContext is not SearchSessionViewModel vm || ReferenceEquals(_hooked, vm)) return;
        if (_hooked is not null)
        {
            _hooked.LayoutChanged -= RebuildColumns;
            _hooked.PropertyChanged -= OnVmPropertyChanged;
        }
        _hooked = vm;
        vm.LayoutChanged += RebuildColumns;
        vm.PropertyChanged += OnVmPropertyChanged;
        RebuildColumns();
        ApplyPaneVisibility();
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchSessionViewModel.IsPreviewOpen)
            or nameof(SearchSessionViewModel.IsSourceBarCompact)) ApplyPaneVisibility();
    }

    /// <summary>列宽监听：拖动列头改的是 GridViewColumn.Width，要回写到 VM 才能落盘。</summary>
    static readonly DependencyPropertyDescriptor WidthDescriptor =
        DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn));

    readonly List<(GridViewColumn Column, EventHandler Handler)> _widthHooks = [];
    readonly List<(ResultColumnViewModel Column, PropertyChangedEventHandler Handler)> _vmHooks = [];

    void RebuildColumns()
    {
        if (DataContext is not SearchSessionViewModel vm) return;

        foreach (var (col, handler) in _widthHooks) WidthDescriptor.RemoveValueChanged(col, handler);
        _widthHooks.Clear();
        foreach (var (col, handler) in _vmHooks) col.PropertyChanged -= handler;
        _vmHooks.Clear();
        ResultGrid.Columns.Clear();

        foreach (var c in vm.Columns)
        {
            if (!c.IsVisible) continue;

            var gvc = new GridViewColumn
            {
                // Header 直接放视图模型：列头的 ContentTemplate 绑定它显示标题与排序箭头
                Header = c,
                Width = c.Width,
                CellTemplate = TryFindResource("Cell." + c.Key) as DataTemplate,
            };

            // 方向一：拖列头改的是 GridViewColumn.Width，要回写到 VM 才能落盘
            EventHandler widthHandler = (_, _) =>
            {
                if (Math.Abs(c.Width - gvc.Width) > 0.5) c.Width = gvc.Width;
            };
            WidthDescriptor.AddValueChanged(gvc, widthHandler);
            _widthHooks.Add((gvc, widthHandler));

            // 方向二：VM 改宽度（名称列自动填充、设置里恢复默认）→ 推回 GridViewColumn。
            // 少了这一条，"自动填充"只会改到视图模型，屏幕上什么都不会动。
            PropertyChangedEventHandler vmHandler = (_, e) =>
            {
                if (e.PropertyName != nameof(ResultColumnViewModel.Width)) return;
                if (Math.Abs(gvc.Width - c.Width) > 0.5) gvc.Width = c.Width;
            };
            c.PropertyChanged += vmHandler;
            _vmHooks.Add((c, vmHandler));

            ResultGrid.Columns.Add(gvc);
        }

        AutoFitFillerColumn();
    }

    /// <summary>
    /// "名称"列吃掉右侧剩余宽度 —— 资源管理器就是这么做的：窗口变宽，名称列跟着变宽，
    /// 而不是在右边留一片空白（或者反过来，把大小/时间列挤出视口）。
    /// <para>
    /// <b>只扩不缩</b>（下限是该列的默认宽度）：剩余空间不足时把名称列压小，
    /// 只会让文件名截得更厉害，而右侧那点空白本来也没别的东西要放。
    /// </para>
    /// </summary>
    void AutoFitFillerColumn()
    {
        if (DataContext is not SearchSessionViewModel vm) return;

        var filler = vm.Columns.FirstOrDefault(c => c.Key == ColumnCatalog.Name && c.IsVisible);
        if (filler is null) return;

        var others = vm.Columns.Where(c => c.IsVisible && !ReferenceEquals(c, filler)).Sum(c => c.Width);
        var avail = ResultList.ActualWidth - others - 30;   // 竖向滚动条 + 列头描边
        var target = Math.Max(filler.DefaultWidth, avail);
        if (Math.Abs(filler.Width - target) < 1) return;
        filler.Width = target;
    }

    void OnResultListSizeChanged(object sender, SizeChangedEventArgs e) => AutoFitFillerColumn();

    /// <summary>
    /// 列拖拽换位（GridView AllowsColumnReorder）的落盘：视图列序变了就回写 <c>Vm.Columns</c>，
    /// 窗口关闭时的 PersistLayout 会把它存进 settings.json。
    /// 重建列时的中间态（数量对不上）直接忽略；顺序一致时是 no-op。
    /// </summary>
    void OnGridColumnsReordered(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not SearchSessionViewModel vm) return;
        var visible = vm.Columns.Where(c => c.IsVisible).ToList();
        if (ResultGrid.Columns.Count != visible.Count) return;
        var viewOrder = ResultGrid.Columns
            .Select(c => (c.Header as ResultColumnViewModel)?.Key)
            .ToList();
        if (viewOrder.Any(string.IsNullOrEmpty)) return;
        if (visible.Select(c => c.Key).SequenceEqual(viewOrder!, StringComparer.OrdinalIgnoreCase)) return;
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < viewOrder.Count; i++) rank[viewOrder[i]!] = i;
        var hidden = vm.Columns.Where(c => !c.IsVisible).ToList();
        var ordered = visible.OrderBy(c => rank.TryGetValue(c.Key, out var r) ? r : int.MaxValue).ToList();
        vm.Columns.Clear();
        foreach (var c in ordered) vm.Columns.Add(c);
        foreach (var c in hidden) vm.Columns.Add(c);
    }

    // ── 拖出到 Explorer（MVP D6）────────────────────
    // 按住左键拖出一定距离后，把选中项的真实路径以 FileDrop 交给系统：
    // Explorer 会复制/移动，编辑器会打开。不存在路径（Zotero 条目等）直接忽略。
    System.Windows.Point _dragStart;
    bool _dragArmed;

    void OnResultPreviewMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = ShouldArmDragOut(e.OriginalSource as DependencyObject);
        if (_dragArmed) _dragStart = e.GetPosition(ResultList);
    }

    /// <summary>
    /// 这次左键按下该不该启动"拖出到 Explorer"。
    /// <para>
    /// <b>只认"按在结果行上"。</b>本处理器挂在 ListView 的
    /// <c>PreviewMouseLeftButtonDown/Move</c>（隧道事件）上，列头拖拽热区（Thumb）、列头本身、
    /// 以及列表内部的滚动条同样会触发它；而 <see cref="DragDrop.DoDragDrop"/> 是<b>模态</b>的 ——
    /// 一旦越过阈值启动，鼠标就被它抢走，Thumb / ScrollBar 自己的拖动全部失效。
    /// 表现就是用户说的"滑块和列宽都拖不动"（2026-09-20 实机实测；老的实现无条件 arm）。
    /// 这里按落点过滤，把左键拖动让回给它们。
    /// </para>
    /// </summary>
    internal static bool ShouldArmDragOut(DependencyObject? src)
    {
        if (src is null) return false;
        if (FindAncestor<Thumb>(src) is not null) return false;                // 列宽拖拽热区
        if (FindAncestor<GridViewColumnHeader>(src) is not null) return false; // 列头（排序 / 换位）
        if (FindAncestor<ScrollBar>(src) is not null) return false;            // 滚动条滑块
        return FindAncestor<ListViewItem>(src) is not null;                    // 只认结果行
    }

    /// <summary>
    /// 从元素往上找第一个 <typeparamref name="T"/>。
    /// 必须同时处理 <see cref="ContentElement"/>：命中文字时 <c>OriginalSource</c> 可能是
    /// <see cref="System.Windows.Documents.Run"/>（不是 Visual，<c>VisualTreeHelper.GetParent</c> 会抛）。
    /// </summary>
    static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T hit) return hit;
            node = node is Visual
                ? VisualTreeHelper.GetParent(node)
                : node is ContentElement ce
                    ? ContentOperations.GetParent(ce) ?? (ce as FrameworkContentElement)?.Parent
                    : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    void OnResultPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var cur = e.GetPosition(ResultList);
        if (Math.Abs(cur.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(cur.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        var path = Vm.Selected?.Source.Path;
        if (string.IsNullOrEmpty(path)) return;
        if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path)) return;
        try
        {
            DragDrop.DoDragDrop(ResultList,
                new DataObject(DataFormats.FileDrop, new[] { path }),
                DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch { /* 拖出失败不应崩 */ }
    }

    /// <summary>
    /// <b>Shift + 滚轮 = 横向滚动</b>（Windows 通用习惯：资源管理器 / 浏览器 / Office 都是它）。
    /// <para>
    /// WPF 的 <see cref="ScrollViewer"/> 默认只把滚轮当纵向滚动，Shift 会被直接忽略，
    /// 所以这里自己接。不带 Shift 一律放行，纵向滚动仍走默认逻辑。
    /// 一格的位移量与纵向保持一致（<see cref="SystemParameters.WheelScrollLines"/> × 16 DIP）。
    /// </para>
    /// </summary>
    void OnResultPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsHorizontalScrollGesture(Keyboard.Modifiers)) return;   // 不带 Shift → 默认纵向
        e.Handled = ScrollResultHorizontally(e.Delta);
    }

    /// <summary>Shift 是"横向滚动"的修饰键（单独抽出来，自检可以直接断言）。</summary>
    internal static bool IsHorizontalScrollGesture(ModifierKeys modifiers)
        => (modifiers & ModifierKeys.Shift) != 0;

    /// <summary>
    /// 把结果表横向滚一格（<paramref name="delta"/> 用滚轮的符号：正 = 向左）。
    /// 返回是否真的处理了（没有可滚的宽度就返回 false，让事件继续冒泡）。
    /// </summary>
    internal bool ScrollResultHorizontally(int delta)
    {
        var sv = FindDescendant<ScrollViewer>(ResultList);
        if (sv is null || sv.ScrollableWidth <= 0.5) return false;
        var step = Math.Max(1, SystemParameters.WheelScrollLines) * 16.0;
        var target = sv.HorizontalOffset - Math.Sign(delta) * step;
        sv.ScrollToHorizontalOffset(Math.Clamp(target, 0, sv.ScrollableWidth));
        return true;
    }

    /// <summary>横向滚动探针：确认"能横向滚 + Shift 是那个修饰键"（自检用）。</summary>
    internal string ProbeHorizontalScroll()
    {
        ResultList.UpdateLayout();
        var sv = FindDescendant<ScrollViewer>(ResultList);
        if (sv is null) return "找不到 ScrollViewer ✗";
        var info = $"CanContentScroll={sv.CanContentScroll} Extent={sv.ExtentWidth:0} Viewport={sv.ViewportWidth:0} 可滚={sv.ScrollableWidth:0}";
        if (sv.ScrollableWidth <= 0.5) return $"当前没有横向可滚内容（{info}）——未验证";
        var start = sv.HorizontalOffset;
        var handled = ScrollResultHorizontally(-120);          // 负 = 向右
        sv.UpdateLayout();                                     // 偏移要等一次布局才读得到
        var after = sv.HorizontalOffset;
        sv.ScrollToHorizontalOffset(start);
        sv.UpdateLayout();
        var gate = IsHorizontalScrollGesture(ModifierKeys.Shift) && !IsHorizontalScrollGesture(ModifierKeys.None)
                   && !IsHorizontalScrollGesture(ModifierKeys.Control);
        return $"{info}；一格 {Math.Max(1, SystemParameters.WheelScrollLines) * 16:0}DIP " +
               $"Shift 判定={(gate ? "✓" : "✗")} 向右滚 {start:0} -> {after:0}" +
               $"{(handled && after > start + 0.5 ? " ✓" : " ✗")}";
    }

    void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column.Header: ResultColumnViewModel col }) return;
        Vm.ToggleSort(col.Key);
        e.Handled = true;
    }

    // ── 自检钩子（--selftest-layout）───────────────────────
    // 列装配的正确性（"视图模型说 5 列，GridView 里是不是真有 5 列"）没法靠看截图确认，
    // 所以把这几件事开成 internal，让 UiSelfTest 直接问。

    internal void RebuildColumnsForTest() => RebuildColumns();

    internal int GridColumnCount => ResultGrid.Columns.Count;

    internal double? GridColumnWidth(string key) =>
        ResultGrid.Columns.FirstOrDefault(c => c.Header is ResultColumnViewModel vm && vm.Key == key)?.Width;

    internal bool IsColumnReorderEnabled => ResultGrid.AllowsColumnReorder;

    internal string ViewColumnOrder => string.Join(",",
        ResultGrid.Columns.Select(c => (c.Header as ResultColumnViewModel)?.Key ?? "?"));

    /// <summary>
    /// 列换位探针：程序化搬一次列（等价于用户拖列头换位落点），验证视图列序 →
    /// <c>Vm.Columns</c> 回写 → <c>CaptureLayout().Visible</c> 落盘口径三者一致。
    /// 合成鼠标会被 UIPI 拦掉，但换位一旦发生就是一次 <c>Columns.Move</c>，与鼠标等价。
    /// </summary>
    internal string ProbeColumnMove(string key, int newIndex)
    {
        if (DataContext is not SearchSessionViewModel vm) return "没有视图模型";
        var cols = ResultGrid.Columns;
        var found = cols.FirstOrDefault(c => (c.Header as ResultColumnViewModel)?.Key == key);
        if (found is null) return $"找不到列 {key}";
        var oldIndex = cols.IndexOf(found);
        newIndex = Math.Clamp(newIndex, 0, cols.Count - 1);
        cols.Move(oldIndex, newIndex);
        var view = ViewColumnOrder;
        var ordered = string.Join(",", vm.Columns.Where(c => c.IsVisible).Select(c => c.Key));
        var disk = string.Join(",", vm.CaptureLayout().Visible);
        var ok = string.Equals(view, ordered, StringComparison.OrdinalIgnoreCase)
              && string.Equals(view, disk, StringComparison.OrdinalIgnoreCase);
        return $"换位 {key} {oldIndex}->{newIndex}：视图=[{view}] 回写=[{ordered}] 落盘=[{disk}] {(ok ? "PASS" : "FAIL 不一致！")}";
    }

    /// <summary>构建一份列头菜单并转储内容（勾选态 + 移动项可用性）。右键弹出本身要人工点，菜单内容可以先验。</summary>
    internal List<string> BuildColumnMenuForTest()
    {
        var menu = new ContextMenu();
        FillColumnMenu(menu);
        var items = menu.Items.OfType<MenuItem>()
            .Select(i => $"{(i.IsCheckable ? (i.IsChecked ? "☑" : "☐") : "·")}{i.Header}{(i.IsEnabled ? "" : "(禁用)")}")
            .ToList();
        _menuTargetColumn = null;
        return items;
    }

    /// <summary>
    /// 列宽拖拽链路的探针。
    /// <para>
    /// GridView 的"拖列头调宽"完全依赖列头模板里那个叫 <c>PART_HeaderGripper</c> 的 Thumb ——
    /// 自定义模板时漏掉它，拖拽会<b>静默失效</b>（不报错、光标也不变，用户只会说"拖不动"）。
    /// 这里检查它存在，并程序化发一次 DragDelta 看列宽到底动不动：
    /// 合成鼠标会被 UIPI 拦掉，但 <c>RaiseEvent</c> 不受限。
    /// </para>
    /// </summary>
    internal string ProbeColumnResize(string key, double delta)
    {
        var col = ResultGrid.Columns.FirstOrDefault(c => c.Header is ResultColumnViewModel vm && vm.Key == key);
        if (col is null) return $"找不到列 {key}";

        // 必须先强制布局：ActualWidth 只在布局跑过之后才有意义，
        // 否则读到的是一排 0，会得出"列头没有热区"的错误结论（第一次探针就这么误判了）。
        ResultList.UpdateLayout();

        var presenter = FindDescendant<GridViewHeaderRowPresenter>(ResultList);
        var all = presenter is null ? [] : Descendants<GridViewColumnHeader>(presenter).ToList();
        var dump = string.Join(" | ", all.Take(8).Select(h =>
        {
            var k = h.Column?.Header is ResultColumnViewModel hvm ? hvm.Key : "(filler)";
            return $"{k}:T={(h.Template is not null ? "有" : "无")},W={h.ActualWidth:0},kids={VisualTreeHelper.GetChildrenCount(h)}";
        }));

        var header = all.FirstOrDefault(h => ReferenceEquals(h.Column, col));
        if (header is null) return $"列 {key} 的列头不在可视树里；presenter={(presenter is null ? "无" : "有")} 列头总数={all.Count}｜{dump}";

        if (header.Template?.FindName("PART_HeaderGripper", header) is not Thumb thumb)
        {
            var thumbs = Descendants<Thumb>(header).ToList();
            return $"列头[{key}] 里没有 PART_HeaderGripper（Thumb={thumbs.Count} 个）" +
                   $"｜T={(header.Template is not null ? "有" : "无")} W={header.ActualWidth:0} kids={VisualTreeHelper.GetChildrenCount(header)}" +
                   $"｜全部列头=[{dump}]";
        }

        var before = col.Width;
        thumb.RaiseEvent(new DragDeltaEventArgs(delta, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        var after = col.Width;
        var verdict = Math.Abs(after - before) < 0.5 ? "  ← 没变，链路不通！" : "  ✓";
        return $"热区={thumb.ActualWidth:0}x{thumb.ActualHeight:0} 拖 {delta:+0;-0}px: 列宽 {before:0} -> {after:0}{verdict}";
    }

    /// <summary>
    /// 热区"命中性"探针：Thumb 存在、尺寸正常，并不代表鼠标点得到它 ——
    /// 用户实测"列宽拖不动"时，必须问一句"这个坐标上真正命中的是谁"。
    /// 只检查落在 ListView 视口内的热区（横向滚动后有些列的列头不在视口里）。
    /// </summary>
    internal string ProbeGripperHitTest()
    {
        ResultList.UpdateLayout();
        var presenter = FindDescendant<GridViewHeaderRowPresenter>(ResultList);
        if (presenter is null) return "没有列头";

        var bounds = new Rect(0, 0, ResultList.ActualWidth, ResultList.ActualHeight);
        var parts = new List<string>();
        foreach (var h in Descendants<GridViewColumnHeader>(presenter).Take(6))
        {
            var key = h.Column?.Header is ResultColumnViewModel vm ? vm.Key : "(filler)";
            if (h.Template?.FindName("PART_HeaderGripper", h) is not Thumb t)
            {
                parts.Add($"{key}:没有热区 ✗");
                continue;
            }
            var c = t.TranslatePoint(new System.Windows.Point(t.ActualWidth / 2, t.ActualHeight / 2), ResultList);
            if (!bounds.Contains(c)) { parts.Add($"{key}:热区在视口外(x={c.X:0})，未验证"); continue; }
            var hit = VisualTreeHelper.HitTest(ResultList, c)?.VisualHit;
            var ok = hit is not null && FindAncestor<Thumb>(hit) is not null;
            var what = hit is null ? "null" : hit.GetType().Name;
            // 屏幕坐标：脚本要据此用真实鼠标去拖（ListView 坐标换算屏幕要过 DPI 与窗口位置）
            var scr = ResultList.PointToScreen(c);
            parts.Add($"{key}:命中={what}{(ok ? " ✓" : " ✗ ← 点不到热区")}@{scr.X:0},{scr.Y:0}px");
        }
        return string.Join(" / ", parts);
    }

    /// <summary>
    /// "拖出"落点过滤探针：确认 <c>DoDragDrop</c> 不会从列头热区 / 列头 / 滚动条上被启动。
    /// <para>
    /// 这是 2026-09-20 用户实测"滑块和列宽都拖不动"的根因 —— 老实现无条件 arm，
    /// <c>PreviewMouseMove</c> 一越过拖拽阈值就进模态拖拽循环，把 Thumb/ScrollBar 的鼠标抢走。
    /// <b><see cref="ProbeColumnResize"/> 查不出这个 bug</b>（它是直接 RaiseEvent 到 Thumb 上的，
    /// 绕过了真实手势路径）—— 所以必须单独有这一条落点过滤的断言。
    /// </para>
    /// </summary>
    internal string ProbeDragArm()
    {
        ResultList.UpdateLayout();
        var parts = new List<string>();

        var presenter = FindDescendant<GridViewHeaderRowPresenter>(ResultList);
        var headers = presenter is null ? [] : Descendants<GridViewColumnHeader>(presenter).ToList();
        var header = headers.FirstOrDefault();

        var thumb = header?.Template?.FindName("PART_HeaderGripper", header) as Thumb;
        parts.Add(thumb is null
            ? "列头热区=找不到 ✗"
            : (ShouldArmDragOut(thumb) ? "列头热区=会 arm ✗" : "列头热区=不 arm ✓"));

        parts.Add(header is null
            ? "列头=找不到 ✗"
            : (ShouldArmDragOut(header) ? "列头=会 arm ✗" : "列头=不 arm ✓"));

        var vsb = Descendants<ScrollBar>(ResultList).FirstOrDefault(s => s.Orientation == Orientation.Vertical);
        parts.Add(vsb is null
            ? "垂直滑块=找不到 ✗"
            : (ShouldArmDragOut(vsb) ? "垂直滑块=会 arm ✗" : "垂直滑块=不 arm ✓"));

        if (ResultList.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem row)
        {
            var cell = Descendants<TextBlock>(row).FirstOrDefault();
            parts.Add(cell is null
                ? "结果行单元格=没有 TextBlock ✗"
                : (ShouldArmDragOut(cell) ? "结果行单元格=会 arm ✓" : "结果行单元格=不 arm ✗"));
            parts.Add(ShouldArmDragOut(row) ? "结果行容器=会 arm ✓" : "结果行容器=不 arm ✗");
        }
        else
        {
            parts.Add("结果行=没有行（没搜出结果时测不了这一条）");
        }

        return string.Join(" / ", parts);
    }

    GridViewColumnHeader? FindColumnHeader(GridViewColumn col)
    {
        var presenter = FindDescendant<GridViewHeaderRowPresenter>(ResultList);
        if (presenter is null) return null;
        return Descendants<GridViewColumnHeader>(presenter).FirstOrDefault(h => ReferenceEquals(h.Column, col));
    }

    static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        => Descendants<T>(root).FirstOrDefault();

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deep in Descendants<T>(child)) yield return deep;
        }
    }

    // ─────────────── 列头右键菜单：切换列 / 移动列 / 恢复默认 ───────────────

    ResultColumnViewModel? _menuTargetColumn;

    ContextMenu BuildColumnMenuTemplate()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => FillColumnMenu(menu);
        return menu;
    }

    void FillColumnMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        _menuTargetColumn = (menu.PlacementTarget as GridViewColumnHeader)?.Column?.Header as ResultColumnViewModel;

        foreach (var c in Vm.Columns)
        {
            var item = new MenuItem
            {
                Header = c.Header,
                IsCheckable = true,
                IsChecked = c.IsVisible,
                Tag = c.Key,
            };
            item.Click += OnToggleColumnVisibility;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var left = new MenuItem { Header = "左移一列", IsEnabled = CanMoveColumn(-1) };
        left.Click += (_, _) => MoveColumn(-1);
        menu.Items.Add(left);

        var right = new MenuItem { Header = "右移一列", IsEnabled = CanMoveColumn(1) };
        right.Click += (_, _) => MoveColumn(1);
        menu.Items.Add(right);

        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "恢复默认列布局" };
        reset.Click += (_, _) => Vm.ResetColumns();
        menu.Items.Add(reset);
    }

    void OnToggleColumnVisibility(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key }) return;
        var col = Vm.Columns.FirstOrDefault(c => c.Key == key);
        if (col is null) return;

        // 至少留一列：全隐藏的表是一片空白，用户会以为程序坏了
        if (col.IsVisible && Vm.Columns.Count(c => c.IsVisible) <= 1)
        {
            MessageBox.Show(this, "至少要保留一列。", "UniSearch", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        col.IsVisible = !col.IsVisible;
        RebuildColumns();
    }

    bool CanMoveColumn(int delta)
    {
        if (_menuTargetColumn is null) return false;
        var idx = Vm.Columns.IndexOf(_menuTargetColumn);
        var target = idx + delta;
        return idx >= 0 && target >= 0 && target < Vm.Columns.Count;
    }

    void MoveColumn(int delta)
    {
        if (_menuTargetColumn is null) return;
        var idx = Vm.Columns.IndexOf(_menuTargetColumn);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= Vm.Columns.Count) return;

        Vm.Columns.Move(idx, target);
        RebuildColumns();
    }

    // ─────────────── 三栏开合 ───────────────

    double _previewWidth = 340;
    double _sourceWidth = 188;

    /// <summary>
    /// 三栏的宽度/可见性都从这里出。
    /// <para>
    /// 左栏是<b>两态</b>（大 188 / 小 56），不是"有或无"：小形态本身就是"缩回去"的样子，
    /// 只留后端图标和一行数量。右栏是"展开 / 收起成一条把手"—— 收起后必须留个把手，
    /// 否则用户只剩快捷键能把预览叫回来。
    /// </para>
    /// <para>宽度用列宽表达（而不是把列删掉），这样用户拖过的宽度能记住。</para>
    /// </summary>
    void ApplyPaneVisibility()
    {
        if (DataContext is not SearchSessionViewModel vm) return;

        // 记下用户拖出来的宽度，下次打开还原（而不是每次都回到默认 340）
        if (PreviewColumn.Width.IsAbsolute && PreviewColumn.Width.Value > 80) _previewWidth = PreviewColumn.Width.Value;
        if (SourceColumn.Width.IsAbsolute && SourceColumn.Width.Value > 80) _sourceWidth = SourceColumn.Width.Value;

        var preview = vm.IsPreviewOpen;
        PreviewPane.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewRail.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewColumn.MinWidth = preview ? 220 : 0;
        PreviewColumn.Width = preview ? new GridLength(_previewWidth) : new GridLength(22);

        // 紧凑态不允许再拖宽（拖了也没有意义），所以把分隔条一起收起来
        var compact = vm.IsSourceBarCompact;
        SourceSplitter.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SourceColumn.MinWidth = compact ? 0 : 132;
        SourceColumn.Width = new GridLength(compact ? 56 : _sourceWidth);
    }

    void OnToggleSourceBar(object sender, RoutedEventArgs e) => Vm.ToggleSourceBar();

    void OnTogglePreview(object sender, RoutedEventArgs e) => Vm.TogglePreview();

    /// <summary>收起后整条把手都可点：点哪里都是展开（角落小按钮不好找，这是兜底）。</summary>
    void OnPreviewRailMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!Vm.IsPreviewOpen) Vm.TogglePreview();
    }

    internal bool IsPreviewRailVisible => PreviewRail.Visibility == Visibility.Visible;

    // ── 鼠标：右键先落选中再弹 shell 菜单；双击打开 ──────────────
    // 结果行的选中由 ListView 自己管（SelectedItem 双向绑定到 VM.Selected），
    // 这里只处理"右键弹谁的菜单"和"双击打开"。

    void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource as DependencyObject) is null) return;   // 双击列头/空白不算
        Vm.OpenSelected();
        e.Handled = true;
    }

    /// <summary>
    /// 列表 → 视图模型的选择同步。
    /// <para>
    /// <b>为什么不用 <c>SelectedItem</c> 双向绑定</b>：快照刷新时 <c>Rows</c> 是整表重建的
    /// （Clear + Add），ListView 会在 Clear 那一刻把 SelectedItem 置空并<b>回写</b>，
    /// 于是视图模型里的选中被抹成 null、恢复锚点（路径）也一起丢了 ——
    /// 症状是"预览永远停在第一行"（实测踩到：自检里 PDF/文件夹各项报的都是第一行的内容）。
    /// </para>
    /// <para>改成单向绑定 + 这个事件：重建期间由 <see cref="SearchSessionViewModel.IsRebuildingRows"/> 挡住回写。</para>
    /// </summary>
    void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not SearchSessionViewModel vm || vm.IsRebuildingRows) return;
        vm.Selected = ResultList.SelectedItem as ResultItemViewModel;
    }

    /// <summary>本窗口是否由我们把右键转成了 shell 菜单（用于同时吞掉 Up，避免 WPF 菜单也弹出来）。</summary>
    bool _shellMenuShown;

    void OnRootPreviewMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        // 右键必须先把选中落到光标下那一行，否则菜单动作会作用在别处
        if (FindRow(e.OriginalSource as DependencyObject) is not { } row) return;
        Vm.Selected = row;

        // 有真实文件路径 → 直接弹系统 shell 菜单：它包含"打开方式/发送到/属性"和第三方动词
        // （7-Zip、Git…），是我们自己那份菜单给不了的。
        // 路径为空的结果（将来的 Zotero 条目、书签等）没有 shell 语义，交给 WPF 菜单。
        if (row.Source.Path is { Length: > 0 } path)
        {
            _shellMenuShown = true;
            e.Handled = true;
            var pt = PointToScreen(e.GetPosition(this));   // 物理像素，TrackPopupMenuEx 只认这个
            Vm.ShowShellMenu(path, (int)pt.X, (int)pt.Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
        else
        {
            _shellMenuShown = false;
        }
    }

    void OnRootPreviewMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_shellMenuShown) return;
        _shellMenuShown = false;
        e.Handled = true;   // 已经弹过 shell 菜单，别让 WPF 的 ContextMenu 再弹一次
    }

    static ResultItemViewModel? FindRow(DependencyObject? d)
    {
        var guard = 0;
        while (d is not null && guard++ < 256)
        {
            if (d is FrameworkElement { DataContext: ResultItemViewModel vm }) return vm;
            d = d switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(d),
                FrameworkContentElement fce => fce.Parent,
                _ => LogicalTreeHelper.GetParent(d),
            };
        }
        return null;
    }

    // ── 键盘（UI-SPEC §4.2）──────────────────────────────
    // 挂在**窗口**上而不是搜索框上：结果行、分类标签、预览窗格都是不可聚焦的，
    // 鼠标点一下它们焦点就离开输入框了。挂在输入框上时，那些快捷键会集体失效
    // （实测症状：点过分类标签之后，Ctrl+J 和 Ctrl+1..9 全都没反应）。
    void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt 组合键在 WPF 里 e.Key 报的是 Key.System，真正的键在 SystemKey。
        // 不转换这一次，Alt+Enter（属性）永远匹配不到 Key.Enter。
        if (HandleShortcut(NormalizeKey(e.Key, e.SystemKey), Keyboard.Modifiers)) e.Handled = true;
    }

    internal static Key NormalizeKey(Key key, Key systemKey) => key == Key.System ? systemKey : key;

    /// <summary>
    /// 快捷键分发。签名收 <c>(key, modifiers)</c> 而不是直接读 <see cref="KeyEventArgs"/>：
    /// 一是为了能自检 —— 合成键盘输入在 DSH 环境里会被 UIPI 拦掉，但直接调这个方法不受限；
    /// 二是 Alt 的 <see cref="Key.System"/> 归一化只需要在一个地方做对。
    /// 返回 true 表示"已消费"，调用方要置 <c>e.Handled</c>，否则按键继续流向输入框。
    /// </summary>
    internal bool HandleShortcut(Key key, ModifierKeys mods)
    {
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);

        switch (key)
        {
            // 导航
            case Key.Down: Vm.MoveSelection(1); return true;
            case Key.Up: Vm.MoveSelection(-1); return true;
            case Key.PageDown: Vm.MoveSelection(10); return true;
            case Key.PageUp: Vm.MoveSelection(-10); return true;
            // Home/End 在输入框里是"光标到行首/行尾"，不能抢
            case Key.Home when !InSearchBox(): Vm.MoveSelection(-100000); return true;
            case Key.End when !InSearchBox(): Vm.MoveSelection(100000); return true;

            case Key.Enter:
                // Ctrl+Shift 必须先判，否则会被 Ctrl 分支吃掉
                if (ctrl && shift) Vm.OpenInConsoleSelected();
                else if (ctrl) Vm.RunAsAdminSelected();
                else if (shift) Vm.RevealSelected();
                else if (alt) Vm.ShowPropertiesSelected();
                else Vm.OpenSelected();
                return true;

            case Key.Tab when !shift:
                if (InSearchBox() && TryAcceptCompletion()) return true;
                Vm.SelectNextCategory(); return true;   // Tab = 下一个分类（框内有补全候选时先补全）

            case Key.Escape:
                Hide(); return true;

            case Key.C:
                // 搜索框里选中了文字时，Ctrl+C 是"复制我选的这段"，不是"复制路径"
                if (ctrl && shift) { Vm.CopyName(); return true; }
                if (ctrl && SearchBox.SelectionLength == 0) { Vm.CopyPath(); return true; }
                return false;

            case Key.J when ctrl:
                Vm.TogglePreview(); return true;   // 预览窗格开合（UI-SPEC §4.2）

            case Key.B when ctrl:
                Vm.ToggleSourceBar(); return true;  // 左栏（来源）开合

            case Key.Q when ctrl:
                App.Quit(); return true;   // 真退出；X/Esc 都只是收进后台

            case Key.F12:
                ShowSettingsDialog(); return true;
        }

        // Ctrl+1..9 切分类，Ctrl+0 回"全部"（主键盘与小键盘都要认）
        if (ctrl)
        {
            var digit = key switch
            {
                >= Key.D0 and <= Key.D9 => key - Key.D0,
                >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
                _ => -1,
            };
            if (digit >= 0)
            {
                if (digit == 0) Vm.SelectedTabId = CategoryIds.All;
                else Vm.SelectNthCategory(digit);
                return true;   // 即使分类不存在也吃掉：不然会往输入框里插一个数字
            }
        }

        return false;
    }

    /// <summary>
    /// Tab 补全（MVP D9 / UI-SPEC §4.3）：搜索框内按 Tab，用当前高亮项的
    /// <c>AutoCompleteText</c>（Everything 回来的文件名）补全输入；
    /// 没有候选时返回 false，调用方回落到"切下一个分类"。
    /// </summary>
    bool TryAcceptCompletion()
    {
        var ac = Vm.Selected?.AutoCompleteText;
        var cur = Vm.Input ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cur) || string.IsNullOrWhiteSpace(ac)) return false;
        if (string.Equals(cur, ac, StringComparison.OrdinalIgnoreCase)) return false;
        // 只有"补全能延伸输入"时才吃掉 Tab：候选与输入无关时仍走切换分类
        if (!ac.StartsWith(cur, StringComparison.OrdinalIgnoreCase) &&
            !ac.Contains(cur, StringComparison.OrdinalIgnoreCase)) return false;
        Vm.Input = ac;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        return true;
    }

    /// <summary>焦点是否还在搜索框里（决定 Home/End 是"跳结果"还是"改光标"）。</summary>
    bool InSearchBox() => SearchBox.IsKeyboardFocusWithin;

    /// <summary>窗口一被激活就把光标送回搜索框：唤出来就能直接打字，不用先点一下。</summary>
    void OnWindowActivated(object? sender, EventArgs e) => FocusSearchBox(selectAll: false);

    /// <summary>
    /// 关闭窗口 = 收进后台，<b>不销毁窗口</b>。
    /// <para>
    /// 必须这样：<c>ShutdownMode=OnExplicitShutdown</c> 让进程在窗口关掉后继续活着，
    /// 而热键唤出要对窗口调 <c>Show()</c>。窗口一旦被销毁，每次唤出都抛
    /// <c>InvalidOperationException：关闭窗口后，无法设置可见性…</c>，
    /// 用户看到的现象就是"关掉一次之后热键再也呼不出来"（实测踩到过）。
    /// </para>
    /// <para>真退出走 <see cref="App.Quit"/>：右键托盘图标 → 退出 UniSearch。</para>
    /// </summary>
    void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 列宽/排序这类纯 UI 状态随窗口一起落盘：拖完列宽就写文件太频繁，攒到这里一次写
        if (DataContext is SearchSessionViewModel vm) PersistLayout?.Invoke(vm.CaptureLayout());

        if (App.IsQuitting) return;   // 真退出，放行

        // 设置里选了"关闭按钮直接退出程序"：放行关闭，但必须显式 Quit ——
        // ShutdownMode 是 OnExplicitShutdown，光关窗口进程会留着，热键还占着却唤不出窗口。
        if (!CloseToTray)
        {
            App.Quit();
            return;
        }

        e.Cancel = true;
        Hide();

        // 第一次隐藏时提示一下：托盘图标可能被折叠进溢出区，不说用户会以为程序已经退了
        if (NotifyOnFirstHide && !_hideNoticed)
        {
            _hideNoticed = true;
            try { FirstHideNotice?.Invoke(); } catch { /* 提示失败不该影响隐藏 */ }
        }
    }

    bool _hideNoticed;

    /// <summary>首次"关闭即隐藏"时的提示回调（宿主接托盘气泡）。</summary>
    public Action? FirstHideNotice { get; set; }

    /// <summary>宿主注入：把列布局写进设置文件（<b>不触发重跑查询</b>）。</summary>
    public Action<ColumnsSettings>? PersistLayout { get; set; }

    /// <summary>
    /// 热键唤出：推到前台 + 全选原查询（跟 Everything 一致 —— 唤出即替换，不用先清空）。
    /// </summary>
    public void Summon()
    {
        WindowSummoner.BringToFront(this);
        FocusSearchBox(selectAll: true);
    }

    void FocusSearchBox(bool selectAll)
    {
        SearchBox.Focus();
        if (selectAll) SearchBox.SelectAll();
        else SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettingsDialog();

    /// <summary>打开设置窗口（宿主注入）。为 null 时退化成内置帮助文本。</summary>
    public Action? OpenSettings { get; set; }

    /// <summary>点 X 时收进托盘（true）还是直接退出程序（false）。来自设置。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>第一次收进托盘时是否弹气泡提示。来自设置。</summary>
    public bool NotifyOnFirstHide { get; set; } = true;

    /// <summary>设置入口。F12、工具条齿轮、托盘菜单都走这里（托盘要能直接调，所以是 public）。</summary>
    public void ShowSettingsDialog()
    {
        if (OpenSettings is not null) { OpenSettings(); return; }
        ShowBuiltInHelp();
    }

    void ShowBuiltInHelp()
    {
        _ = MessageBox.Show(
            "设置页将在后续里程碑提供。\n\n" +
            "现在可用：\n" +
            "  输入即搜（Everything 文件名索引）\n" +
            "  ↑↓ / PgUp PgDn / Home End 移动选中\n" +
            "  Enter 打开 · Shift+Enter 所在目录 · Ctrl+Enter 管理员\n" +
            "  Alt+Enter 属性 · Ctrl+Shift+Enter 终端\n" +
            "  Ctrl+C 复制路径 · Ctrl+Shift+C 复制文件名\n" +
            "  Tab 下一个分类 · Ctrl+1..9 切分类 · Ctrl+0 全部\n" +
            "  Ctrl+J 预览开合 · Ctrl+B 来源栏开合 · Shift+滚轮 结果表横向滚动\n" +
            "  右键 更多动作 · Esc 关闭\n\n" +
            "全局热键：Alt+Win+Space 唤出 · Ctrl+F 唤出并限定到资源管理器当前目录\n\n" +
            "关闭窗口只是收进后台（热键仍可唤回）。\n" +
            "要彻底退出：右键任务栏托盘图标 → 退出 UniSearch。",
            "UniSearch");
    }
}
