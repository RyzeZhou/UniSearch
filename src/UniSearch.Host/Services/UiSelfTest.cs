using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using UniSearch.Core.Archiving;
using UniSearch.Core.Categories;
using UniSearch.Host.Settings;
using UniSearch.Host.ViewModels;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Host.Services;

/// <summary>
/// UI 自检：把"右键菜单到底绑上了没有、文件和文件夹是不是真的不同"变成可脚本验证的事实。
/// <para>
/// 理由：右键菜单绑错一个环节就是"菜单能弹但点了没反应"或者"两类条目菜单一样"，
/// 这种失败人工点几下不一定碰得到。这里程序化打开菜单，逐项报告可见性与命令状态，
/// 并把弹出层单独渲染成 PNG 供人眼复核。
/// </para>
/// </summary>
public static class UiSelfTest
{
    /// <summary>分别以"文件"和"文件夹"为选中项打开菜单，对比两者差异。</summary>
    public static void RunMenu(Window win, SearchSessionViewModel vm, IUniSearchLog log, string? dumpDir)
    {
        var (element, _) = FindRowWithMenu(win);
        if (element is null)
        {
            log.Warn("selftest", "没有找到带右键菜单的结果行（结果为空？）");
            return;
        }

        var menu = element.ContextMenu!;
        // 程序化打开必须显式给 PlacementTarget：菜单的 DataContext 就是经它取回来的会话 VM
        menu.PlacementTarget = element;

        var file = First(vm, r => !r.Source.IsFolder);
        var folder = First(vm, r => r.Source.IsFolder);

        log.Info("selftest", $"菜单 DataContext={(menu.DataContext?.GetType().Name ?? "null")}");
        Dump(log, menu, vm, "文件", file, dumpDir, "menu-file.png");
        Dump(log, menu, vm, "文件夹", folder, dumpDir, "menu-folder.png");

        // 剪贴板动作顺带验一下：它不碰外部程序，最安全
        if (file is not null)
        {
            vm.Selected = file;
            vm.CopyPath();
            log.Info("selftest", $"CopyPath 后剪贴板=[{SafeClipboard()}]");
        }

        menu.IsOpen = false;
    }

    static void Dump(IUniSearchLog log, ContextMenu menu, SearchSessionViewModel vm,
                     string kind, ResultItemViewModel? row, string? dumpDir, string fileName)
    {
        if (row is null)
        {
            log.Warn("selftest", $"结果里没有{kind}，跳过");
            return;
        }

        vm.Selected = row;                       // 右键的真实语义：先落选中，菜单再跟着变
        log.Info("selftest", $"===== {kind}：{row.Title} =====");

        menu.IsOpen = false;                     // 先关再开，强制重新测量
        menu.IsOpen = true;
        menu.UpdateLayout();

        foreach (var item in menu.Items)
        {
            switch (item)
            {
                case MenuItem mi:
                    var can = mi.Command?.CanExecute(mi.CommandParameter) ?? false;
                    log.Info("selftest", $"  [{mi.Header}] visible={mi.Visibility} " +
                                         $"cmd={(mi.Command is null ? "NULL" : "ok")} canExecute={can}");
                    break;
                case Separator sp:
                    log.Info("selftest", $"  (分隔线) visible={sp.Visibility}");
                    break;
                default:
                    log.Info("selftest", $"  ({item.GetType().Name})");
                    break;
            }
        }

        if (!string.IsNullOrEmpty(dumpDir) && menu.Parent is Popup { Child: FrameworkElement child })
        {
            var path = Path.Combine(dumpDir, fileName);
            var ok = RenderDump.RunVisual(child, path);
            log.Info("selftest", $"  弹出层渲染{(ok ? "成功" : "失败(尺寸0)")} -> {path}");
        }
    }

    /// <summary>
    /// 真正执行一次"打开目录"：走完整的 选中 → 动作 → Shell 调用链。
    /// 打开资源管理器是这里最直观又完全无害的验证；文件的 ShellExecute 走同一段代码，故不真的弹应用窗口。
    /// </summary>
    public static void RunOpen(SearchSessionViewModel vm, IUniSearchLog log)
    {
        var folder = First(vm, r => r.Source.IsFolder);
        var file = First(vm, r => !r.Source.IsFolder);

        if (folder?.Source.Path is not { Length: > 0 } dir)
        {
            log.Warn("selftest", "结果里没有文件夹，跳过打开目录");
        }
        else
        {
            vm.Selected = folder;
            log.Info("selftest", $"打开文件夹: {dir}");
            vm.OpenSelected();
            log.Info("selftest", $"  状态条: {vm.StatusText}");
        }

        if (file?.Source.Path is { Length: > 0 } f)
            log.Info("selftest", $"（不实际执行）打开文件将走 ShellExecute: {f}");
    }

    /// <summary>
    /// 验证偏移调用（MAKEINTRESOURCE）真的能执行 shell 命令：
    /// 选一个文件按 "properties" 动词调用，外部脚本随后核对属性窗口是否弹出。
    /// 这是右键菜单"点了会不会有反应"的核心链路。
    /// </summary>
    public static void RunShellInvoke(ShellContextMenu menu, SearchSessionViewModel vm, IUniSearchLog log)
    {
        var row = First(vm, r => !r.Source.IsFolder && r.Source.Path is { Length: > 0 });
        if (row?.Source.Path is not { Length: > 0 } path)
        {
            log.Warn("selftest", "没有可测的文件行");
            return;
        }

        log.Info("selftest", $"按动词 properties 调用: {path}");
        var ok = menu.InvokeByVerb(path, "properties", out var error);
        log.Info("selftest", $"InvokeByVerb -> {(ok ? "成功（应已弹出属性对话框）" : $"失败: {error}")}");
    }

    /// <summary>
    /// 对文件夹与文件各开一次"属性"对话框。
    /// 这条曾经是真 bug：ProcessStartInfo.Verb="properties" 不带 SEE_MASK_INVOKEIDLIST，
    /// 必然失败且静默 —— 所以必须在这里留下可核对的成功/失败记录。
    /// </summary>
    public static void RunProperties(SearchSessionViewModel vm, IUniSearchLog log)
    {
        foreach (var (kind, row) in new[] { ("文件夹", First(vm, r => r.Source.IsFolder)),
                                            ("文件", First(vm, r => !r.Source.IsFolder)) })
        {
            if (row?.Source.Path is not { Length: > 0 } p)
            {
                log.Warn("selftest", $"结果里没有{kind}，跳过属性测试");
                continue;
            }

            vm.Selected = row;
            log.Info("selftest", $"{kind}属性: {p}");
            vm.ShowPropertiesSelected();
            log.Info("selftest", $"  状态条: {vm.StatusText}");
        }
    }

    /// <summary>
    /// 触发一次「直达后端程序」。外部脚本可以借此核对后端窗口标题是否真的变成了该查询，
    /// 从而验证"按钮直达"不是只绑上了命令而没生效。
    /// </summary>
    public static void RunBackendJump(SearchSessionViewModel vm, IUniSearchLog log)
    {
        var names = new List<string>();
        foreach (var t in vm.BackendTargets) names.Add(t.Name);
        log.Info("selftest", $"可跳转后端 {names.Count} 个: [{string.Join(", ", names)}]");
        if (names.Count == 0) { log.Warn("selftest", "没有可跳转的后端"); return; }

        log.Info("selftest", $"当前查询 = \"{vm.Input}\"");
        vm.OpenInBackend(null);
        log.Info("selftest", $"状态条: {vm.StatusText}");
    }

    /// <summary>
    /// 核对图标是否真的经后台线程刷回来了（而不是只绑了命令没生效）。
    /// 关键在于统计"仍在用字体图标"的行数 —— 那正是后台解析尚未回落时的兜底显示。
    /// </summary>
    public static void RunIcons(SearchSessionViewModel vm, IconLoader loader, IUniSearchLog log, int round = 1)
    {
        var total = 0;
        var withIcon = 0;
        var withGlyph = 0;
        foreach (var it in vm.Rows)
        {
            total++;
            if (it.Icon is not null) withIcon++;
            else if (it.UsesGlyph) withGlyph++;
        }

        log.Info("selftest", $"[第 {round} 次] 行数={total} 有真图标={withIcon} 仍用字体图标兜底={withGlyph}");
        log.Info("selftest", $"[第 {round} 次] 图标调度: 缓存命中={loader.CacheHits} 排队={loader.Queued} " +
                             $"合并={loader.Coalesced} 放弃={loader.Dropped} 解析完成={loader.Resolved}" +
                             $" 失败={loader.Failed}{(loader.LastError is { Length: > 0 } e ? $" ({e})" : "")}");
        var noIcon = new List<string>(loader.NoIconKeys);
        log.Info("selftest", $"[第 {round} 次] 无专用图标、退回通用图标的键: [{string.Join(", ", noIcon)}]");
        foreach (var f in loader.IconFailures)
            log.Warn("selftest", $"[第 {round} 次] 图标解析失败原因: {f}");
    }

    /// <summary>
    /// 按类型各挑一行，走完整的"选中 → 取预览 → 落到 PreviewContent"链路，
    /// 报告每种类型最终拿到的是图片、文本还是"没有可用预览"。
    /// </summary>
    public static async Task RunPreviewAsync(SearchSessionViewModel vm, IUniSearchLog log)
    {
        string[] imageExt = ["png", "jpg", "jpeg", "gif", "bmp", "webp", "svg", "ico"];
        string[] textExt = ["txt", "md", "json", "jsonl", "log", "js", "mjs", "ts", "cs", "py", "xml"];

        var picks = new (string Kind, ResultItemViewModel? Row)[]
        {
            ("图片", First(vm, r => Has(r, imageExt))),
            ("文本", First(vm, r => Has(r, textExt))),
            ("PDF文档", First(vm, r => Has(r, ["pdf"]))),
            ("其它", First(vm, r => !Has(r, imageExt) && !Has(r, textExt) && !Has(r, ["pdf"]) && !r.Source.IsFolder)),
            ("文件夹", First(vm, r => r.Source.IsFolder)),
        };

        foreach (var (kind, row) in picks)
        {
            if (row is null) { log.Warn("selftest", $"{kind}：结果里没有可测的行"); continue; }

            var before = vm.PreviewContent;
            vm.Selected = row;
            // 等到预览真的换了一次，而不是死等固定时长。
            // 固定 700ms 会让 PDF 假失败：Windows.Data.Pdf 渲染首页实测要 ~2 秒，
            // 自检读到的还是上一行的内容，看上去像"预览没跟着选中走"（踩过）。
            var waited = await WaitForPreviewChangeAsync(vm, before).ConfigureAwait(true);
            var p = vm.PreviewContent;
            log.Info("selftest", $"{kind}: {row.Title} -> Kind={p.Kind} 图片={(p.HasImage ? "有" : "无")} " +
                                 $"文本={(p.HasText ? $"有({p.Text!.Length}字)" : "无")} " +
                                 $"说明={p.Message ?? "-"} 明细={p.Detail ?? "-"} 等待={waited}");
            if (PreviewService.LastError is { Length: > 0 } err)
                log.Warn("selftest", $"  预览失败原因: {err}");
        }
    }

    /// <summary>
    /// 等 <see cref="SearchSessionViewModel.PreviewContent"/> 换一次（最多 6 秒）。
    /// 返回等待毫秒数；返回 -1 表示超时没换 —— 那就是真的有问题，不是"慢"。
    /// </summary>
    static async Task<string> WaitForPreviewChangeAsync(SearchSessionViewModel vm, PreviewResult before)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6000)
        {
            await Task.Delay(80).ConfigureAwait(true);
            if (!ReferenceEquals(vm.PreviewContent, before)) return $"{sw.ElapsedMilliseconds}ms";
        }
        return "超时未更新";
    }

    static bool Has(ResultItemViewModel row, string[] exts)
    {
        var e = (row.Source.Extension ?? "").TrimStart('.').ToLowerInvariant();
        return e.Length > 0 && Array.IndexOf(exts, e) >= 0;
    }

    /// <summary>
    /// 验证真 shell 菜单的 COM 管道：对文件、文件夹、可执行文件各取一次系统动词表。
    /// 交互部分（TrackPopupMenuEx）无法自动化，但拿到动词就说明管道是通的。
    /// </summary>
    public static void RunShellMenu(SearchSessionViewModel vm, ShellContextMenu menu, IUniSearchLog log)
    {
        var picks = new (string Kind, ResultItemViewModel? Row)[]
        {
            ("文件", First(vm, r => !r.Source.IsFolder && r.Source.Path is { Length: > 0 })),
            ("文件夹", First(vm, r => r.Source.IsFolder && r.Source.Path is { Length: > 0 })),
            ("可执行", First(vm, r => (r.Source.Extension ?? "").Equals("exe", StringComparison.OrdinalIgnoreCase))),
        };

        foreach (var (kind, row) in picks)
        {
            if (row?.Source.Path is not { Length: > 0 } path)
            {
                log.Warn("selftest", $"{kind}：没有可测的行");
                continue;
            }

            var verbs = menu.ListVerbs(path, out var error);
            log.Info("selftest", $"{kind} {System.IO.Path.GetFileName(path)} -> 系统动词 {verbs.Count} 个: [{string.Join(", ", verbs)}]");
            if (error.Length > 0) log.Warn("selftest", $"  {kind} 管道错误: {error}");
        }
    }

    /// <summary>
    /// 核对热键注册结果，并直接调一次 Explorer 目录探测。
    /// <para>
    /// 探测的<b>真实链路</b>：脚本先把某个 Explorer 窗口拉到前台，然后本方法读
    /// <c>GetForegroundWindow</c> —— 前台不是 UniSearch，正是热键按下时的真实场景
    /// （Ctrl+F 是全局热键，按下时焦点在 Explorer 上）。
    /// </para>
    /// </summary>
    public static void RunHotkeys(GlobalHotkeys hotkeys, Func<string?> probe, IUniSearchLog log)
    {
        foreach (var (name, gesture, ok) in hotkeys.Registered)
            log.Info("selftest", $"热键 {gesture} ({name}) -> {(ok ? "已就绪" : "未就绪")}");
        log.Info("selftest", $"目录限定键钩子已安装={hotkeys.DirectoryScopeHookInstalled}（它不再用 RegisterHotKey 独占注册）");

        // 拦截判定：这是"记事本按 Ctrl+F 不该被抢"的关键，直接断言纯函数
        var g = Settings.Gestures.Parse("Ctrl+F");
        var vkF = System.Windows.Input.KeyInterop.VirtualKeyFromKey(System.Windows.Input.Key.F);
        var vkD = System.Windows.Input.KeyInterop.VirtualKeyFromKey(System.Windows.Input.Key.D);
        log.Info("selftest", "拦截判定（Ctrl+F）：" +
            $"前台=资源管理器 -> {BlockingKeyHotkey.ShouldIntercept(g, System.Windows.Input.ModifierKeys.Control, vkF, true)}（期望 True）；" +
            $"前台=记事本 -> {BlockingKeyHotkey.ShouldIntercept(g, System.Windows.Input.ModifierKeys.Control, vkF, false)}（期望 False）；" +
            $"多按 Shift -> {BlockingKeyHotkey.ShouldIntercept(g, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift, vkF, true)}（期望 False）；" +
            $"裸按 F -> {BlockingKeyHotkey.ShouldIntercept(g, System.Windows.Input.ModifierKeys.None, vkF, true)}（期望 False）；" +
            $"别的键 D -> {BlockingKeyHotkey.ShouldIntercept(g, System.Windows.Input.ModifierKeys.Control, vkD, true)}（期望 False）");

        log.Info("selftest", $"ExplorerLocator.GetCurrentDirectory -> [{probe() ?? "(null)"}]");
    }

    /// <summary>
    /// 快捷键自检：直接调窗口的分发函数，绕开合成键盘。
    /// <para>
    /// <b>为什么不能靠合成按键</b>：DSH 环境里合成输入会被 UIPI 拦掉（试过 SendKeys 与 keybd_event），
    /// 按键要么收不到要么发给别的窗口，测出来的结论是假的。把分发抽成
    /// <c>HandleShortcut(key, modifiers)</c> 之后，这条路完全确定。
    /// </para>
    /// <para>覆盖的正是用户报过"没反应"的那几个：Ctrl+J、Ctrl+1..9、Ctrl+0，外加 Alt+Enter 的键位归一化。</para>
    /// </summary>
    public static void RunKeys(Views.MainWindow win, SearchSessionViewModel vm, IUniSearchLog log)
    {
        var tabs = vm.Tabs.Select(t => t.Id).ToArray();
        log.Info("selftest", $"分类标签 = [{string.Join(", ", tabs)}]（共 {tabs.Length} 个）");

        // Ctrl+J：预览开合
        var before = vm.IsPreviewOpen;
        var handled = win.HandleShortcut(System.Windows.Input.Key.J, System.Windows.Input.ModifierKeys.Control);
        log.Info("selftest", $"Ctrl+J -> handled={handled} IsPreviewOpen {before} -> {vm.IsPreviewOpen} 把手可见={win.IsPreviewRailVisible}（期望 True）");
        win.HandleShortcut(System.Windows.Input.Key.J, System.Windows.Input.ModifierKeys.Control);   // 还原
        log.Info("selftest", $"Ctrl+J 再按一次 -> IsPreviewOpen={vm.IsPreviewOpen} 把手可见={win.IsPreviewRailVisible}（期望 False）");

        // Ctrl+3：切到第 3 个分类（Tabs[0] 是"全部"，所以 Tabs[3]）
        handled = win.HandleShortcut(System.Windows.Input.Key.D3, System.Windows.Input.ModifierKeys.Control);
        log.Info("selftest", $"Ctrl+3 -> handled={handled} SelectedTabId={vm.SelectedTabId}（期望 {tabs.ElementAtOrDefault(3) ?? "(无)"}）");

        // Ctrl+NumPad2：小键盘也要认
        handled = win.HandleShortcut(System.Windows.Input.Key.NumPad2, System.Windows.Input.ModifierKeys.Control);
        log.Info("selftest", $"Ctrl+NumPad2 -> handled={handled} SelectedTabId={vm.SelectedTabId}（期望 {tabs.ElementAtOrDefault(2) ?? "(无)"}）");

        // Ctrl+0：回"全部"
        handled = win.HandleShortcut(System.Windows.Input.Key.D0, System.Windows.Input.ModifierKeys.Control);
        log.Info("selftest", $"Ctrl+0 -> handled={handled} SelectedTabId={vm.SelectedTabId}");

        // 越界不该崩，也不该放行（放行会让数字被插进搜索框）
        handled = win.HandleShortcut(System.Windows.Input.Key.D9, System.Windows.Input.ModifierKeys.Control);
        log.Info("selftest", $"Ctrl+9（越界）-> handled={handled} SelectedTabId={vm.SelectedTabId}");

        // Alt+Enter：WPF 把 Alt 组合报成 Key.System，必须归一化，否则永远匹配不到 Enter
        var normalized = Views.MainWindow.NormalizeKey(
            System.Windows.Input.Key.System, System.Windows.Input.Key.Enter);
        log.Info("selftest", $"NormalizeKey(System, Enter) = {normalized}（期望 Enter）");

        // 不带修饰键的普通字符必须放行给输入框，否则就没法打字了
        var plain = win.HandleShortcut(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.None);
        log.Info("selftest", $"裸按 A -> handled={plain}（期望 False，放行给输入框）");

        // shell 菜单的宿主附加项：文件 / 文件夹 / 可执行文件三种必须给出不同的项
        DumpShellItems(log, "文件夹", SearchSessionViewModel.BuildShellMenuItems(isFolder: true, runnable: false, backendName: "Everything"));
        DumpShellItems(log, "普通文件", SearchSessionViewModel.BuildShellMenuItems(isFolder: false, runnable: false, backendName: "Everything"));
        DumpShellItems(log, "可执行文件", SearchSessionViewModel.BuildShellMenuItems(isFolder: false, runnable: true, backendName: "Everything"));
    }

    /// <summary>
    /// 设置自检：转储当前值 + <b>做一次真实的落盘往返</b>。
    /// <para>
    /// 为什么必须有往返：设置最容易出的问题是"界面显示保存成功，重启后回到默认"。
    /// 这里写一份改动过的、重新建一个 Store 从磁盘读回来比对，最后还原 —— 才算真的验证了持久化。
    /// 顺带覆盖<b>带空格的路径</b>（这正是 Config.Net 挂掉的那个用例）。
    /// </para>
    /// </summary>
    public static void RunSettings(SettingsStore store, IUniSearchLog log)
    {
        var s = store.Current;
        log.Info("selftest", $"设置文件 = {store.FilePath}（当前存在={File.Exists(store.FilePath)}）");
        log.Info("selftest", $"热键：启用={s.Hotkeys.Enabled} 唤出=[{s.Hotkeys.Summon}] 目录=[{s.Hotkeys.DirectoryScope}]");
        log.Info("selftest", $"搜索：列表上限={s.Search.MaxRows} 行 "
                             + $"自动搜索后端=[{string.Join(",", s.Search.AutoSearchProviders)}] "
                             + $"过滤隐藏={s.Search.FilterHiddenAndSystem} 排除噪声={s.Search.ExcludeNoisePaths} "
                             + $"额外排除={s.Search.ExtraExcludePaths.Count} 条");
        log.Info("selftest", $"列布局：可见列=[{string.Join(",", s.Columns.Visible)}] "
                             + $"排序={s.Columns.SortKey}{(s.Columns.SortDescending ? "↓" : "↑")} "
                             + $"宽度覆盖={s.Columns.Widths.Count} 条");
        log.Info("selftest", $"预览：默认展开={s.Preview.OpenByDefault}；窗口：关闭进托盘={s.Window.CloseToTray} "
                             + $"首次提示={s.Window.NotifyOnFirstHide}");

        // 手势串的规范化（设置文件里可能被手改成 win+alt+space 这种写法）
        var normalized = Settings.Gestures.Normalize("win+alt+space");
        log.Info("selftest", $"手势规范化 win+alt+space -> [{normalized}]（期望 Alt+Windows+Space）");

        var probe = s.Clone();
        probe.Search.MaxRows = 33;
        probe.Search.ExtraExcludePaths = [@"D:\带 空格 的目录", "node_modules"];
        probe.Columns.SortKey = "size";
        probe.Columns.SortDescending = true;
        probe.Columns.Widths["name"] = 333;
        probe.Columns.Visible = ["name", "path", "size"];

        var saved = store.Save(probe);
        var reread = new SettingsStore(Path.GetDirectoryName(store.FilePath)!, log).Current;
        log.Info("selftest", $"往返写入成功={saved}；重新读回 列表上限={reread.Search.MaxRows}（期望 33）");
        log.Info("selftest", $"往返读回 额外排除=[{string.Join(" | ", reread.Search.ExtraExcludePaths)}]（期望原样两条，含空格）");
        log.Info("selftest", $"往返读回 列布局：排序={reread.Columns.SortKey}{(reread.Columns.SortDescending ? "↓" : "↑")}（期望 size↓） "
                             + $"name 列宽={reread.Columns.Widths.GetValueOrDefault("name")}（期望 333） "
                             + $"可见列=[{string.Join(",", reread.Columns.Visible)}]（期望 name,path,size）");

        // 还原成自检前的值：自检不该改用户的设置
        store.Save(s);
        var restored = new SettingsStore(Path.GetDirectoryName(store.FilePath)!, log).Current;
        log.Info("selftest", $"已还原：列表上限={restored.Search.MaxRows}（期望 {s.Search.MaxRows}） "
                             + $"可见列=[{string.Join(",", restored.Columns.Visible)}]（期望 {string.Join(",", s.Columns.Visible)}）");
    }

    /// <summary>
    /// 布局自检：三栏改造新增的几条链路 —— <b>来源限定 / 列排序 / 列装配 / 列布局落盘</b>。
    /// <para>
    /// 这几件事截图都证明不了：截图能看出"列长什么样"，看不出"点了列头次序是不是真的变了"、
    /// "选了来源之后后端到底跑没跑"。所以这里直接调视图模型并核对结果，把结论写进日志。
    /// </para>
    /// </summary>
    public static async Task RunLayoutAsync(Views.MainWindow win, SearchSessionViewModel vm,
                                           SettingsStore store, IUniSearchLog log)
    {
        log.Info("selftest", "=== 布局自检：来源 / 列排序 / 列装配 / 列布局落盘 ===");

        // ① 来源栏
        log.Info("selftest", $"来源列表 = [{string.Join(", ", vm.Sources.Select(s => $"{s.Id}{(s.IsActive ? "←选中" : "")}({s.StatusText})"))}]");
        log.Info("selftest", $"来源口径 = {vm.SourceLabel}；生效 scope = [{string.Join(",", vm.EffectiveProviderScope ?? [])}]");

        vm.SelectSource("everything");
        await Task.Delay(900).ConfigureAwait(true);
        log.Info("selftest", $"钉住 everything：scope=[{string.Join(",", vm.EffectiveProviderScope ?? [])}] 行数={vm.Rows.Count} "
                             + $"后端处置=[{string.Join(", ", vm.Outcomes.Select(o => $"{o.ProviderId}:{o.State}"))}]");

        // 钉一个不存在的后端：必须真的一条都没有，而不是"偷偷还是搜了 Everything"
        vm.ActiveSourceId = "no-such-provider";
        await Task.Delay(900).ConfigureAwait(true);
        log.Info("selftest", $"钉住不存在的后端：行数={vm.Rows.Count}（期望 0） "
                             + $"后端处置=[{string.Join(", ", vm.Outcomes.Select(o => $"{o.ProviderId}:{o.State}/{(o.Detail ?? "-")}"))}]");

        vm.ActiveSourceId = null;
        await Task.Delay(900).ConfigureAwait(true);
        log.Info("selftest", $"取消限定：scope=[{string.Join(",", vm.EffectiveProviderScope ?? [])}] 行数={vm.Rows.Count}");

        // ② 列排序：点列头 → 次序必须真的按那一列单调
        vm.ToggleSort("size");
        log.Info("selftest", $"点「大小」列头 → 排序={vm.SortKey} 方向={(vm.SortDescending ? "降序" : "升序")}");
        CheckOrder(vm, log, "大小降序", SizeKey, descending: true);

        vm.ToggleSort("size");
        CheckOrder(vm, log, "大小升序（同列再点一次换方向）", SizeKey, descending: false);

        vm.ToggleSort("modified");
        log.Info("selftest", $"点「修改时间」列头 → 排序={vm.SortKey} 方向={(vm.SortDescending ? "降序" : "升序")} "
                             + $"前 3 行=[{string.Join(", ", vm.Rows.Take(3).Select(r => r.ModifiedText))}]");

        vm.ToggleSort("name");
        log.Info("selftest", $"点「名称」列头 → 排序={vm.SortKey} 方向={(vm.SortDescending ? "降序" : "升序")} "
                             + $"前 3 行=[{string.Join(", ", vm.Rows.Take(3).Select(r => r.Title))}]");

        // ③ 列装配：视图模型说几列，GridView 里就得真有几列
        var visible = vm.Columns.Where(c => c.IsVisible).Select(c => c.Key).ToList();
        log.Info("selftest", $"列装配：可见列=[{string.Join(",", visible)}] GridView 实际列数={win.GridColumnCount}（期望 {visible.Count}）");

        var ext = vm.Columns.First(c => c.Key == "extension");
        ext.IsVisible = true;
        win.RebuildColumnsForTest();
        log.Info("selftest", $"勾选「扩展名」后：GridView 列数={win.GridColumnCount}（期望 {visible.Count + 1}）");
        ext.IsVisible = false;
        win.RebuildColumnsForTest();
        log.Info("selftest", $"取消勾选后：GridView 列数={win.GridColumnCount}（期望 {visible.Count}）");

        // 视图模型改宽度 → GridViewColumn 必须跟着变。
        // 少了这条，"名称列自动填充"只会改到内存里的数字，屏幕上一动不动。
        var nameCol = vm.Columns.First(c => c.Key == "name");
        nameCol.Width = 275;
        log.Info("selftest", $"把「名称」列宽设为 275 → GridView 列宽={win.GridColumnWidth("name")}（期望 275）");

        // 列头右键菜单的内容（增减列 / 左右移动 / 恢复默认）
        log.Info("selftest", $"列头右键菜单 = [{string.Join(" | ", win.BuildColumnMenuForTest())}]");

        // 列宽拖拽链路：模板里的 PART_HeaderGripper 在不在 + 拖一次列宽真的动吗
        log.Info("selftest", $"列宽拖拽探针(路径 +40px)：{win.ProbeColumnResize("path", 40)}");
        log.Info("selftest", $"列宽热区可点性：{win.ProbeGripperHitTest()}");
        log.Info("selftest", $"横向滚动(Shift+滚轮)：{win.ProbeHorizontalScroll()}");
        // 拖出的落点过滤：列头热区 / 列头 / 滚动条都不能被 DoDragDrop 抢走鼠标（用户实测过这个 bug）
        log.Info("selftest", $"拖出落点过滤：{win.ProbeDragArm()}");

        // 列换位（拖列头本身）：开关状态 + 程序化搬一次验证回写/落盘三者一致，再搬回来复原
        log.Info("selftest", $"列换位开关 AllowsColumnReorder={win.IsColumnReorderEnabled}（期望 True）");
        log.Info("selftest", $"列换位探针：{win.ProbeColumnMove("kind", 0)}");
        log.Info("selftest", $"列换位复原：{win.ProbeColumnMove("kind", 4)}");

        // Ctrl+B：来源栏开合（左栏是这轮新加的，快捷键也得接上）
        var barBefore = vm.IsSourceBarCompact;
        var handledBar = win.HandleShortcut(System.Windows.Input.Key.B, System.Windows.Input.ModifierKeys.Control);
        log.Info("selftest", $"Ctrl+B -> handled={handledBar} 来源栏紧凑 {barBefore} -> {vm.IsSourceBarCompact}");
        win.HandleShortcut(System.Windows.Input.Key.B, System.Windows.Input.ModifierKeys.Control);
        log.Info("selftest", $"Ctrl+B 再按一次 -> 来源栏紧凑={vm.IsSourceBarCompact}（期望 {barBefore}）");

        // ④ 列布局落盘往返
        var original = store.Current.Columns;
        var probe = new Settings.ColumnsSettings
        {
            Visible = ["name", "path", "size"],
            SortKey = "size",
            SortDescending = true,
            Widths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["name"] = 333 },
        };
        var settings = store.Current;
        settings.Columns = probe;
        var saved = store.Save(settings, notify: false);
        var reread = new SettingsStore(Path.GetDirectoryName(store.FilePath)!, log).Current;
        log.Info("selftest", $"列布局往返：写入成功={saved}；读回 name 列宽={reread.Columns.Widths.GetValueOrDefault("name")}（期望 333） "
                             + $"排序={reread.Columns.SortKey}{(reread.Columns.SortDescending ? "↓" : "↑")}（期望 size↓） "
                             + $"可见列=[{string.Join(",", reread.Columns.Visible)}]（期望 name,path,size）");

        settings.Columns = original;
        store.Save(settings, notify: false);
        var restored = new SettingsStore(Path.GetDirectoryName(store.FilePath)!, log).Current;
        log.Info("selftest", $"已还原列布局：可见列=[{string.Join(",", restored.Columns.Visible)}] 排序={restored.Columns.SortKey}");
    }

    /// <summary>排序用的键：文件夹没有大小，给 -1 让它稳定地待在一端（与 Core 的 ResultSort 同口径）。</summary>
    static long SizeKey(ResultItemViewModel r) => r.Source.IsFolder ? -1 : r.Source.SizeBytes ?? -1;

    /// <summary>核对当前行序是否按某个键单调 —— 这是"列排序真的生效了"唯一算数的证据。</summary>
    static void CheckOrder(SearchSessionViewModel vm, IUniSearchLog log, string label,
                           Func<ResultItemViewModel, long> key, bool descending)
    {
        long prev = descending ? long.MaxValue : long.MinValue;
        var ok = true;
        foreach (var r in vm.Rows)
        {
            var v = key(r);
            if (descending ? v > prev : v < prev) { ok = false; break; }
            prev = v;
        }
        var head = string.Join(", ", vm.Rows.Take(5).Select(r => $"{r.Title}={key(r)}"));
        log.Info("selftest", $"{label}：{(ok ? "PASS" : "FAIL")}（行数={vm.Rows.Count}，前 5 行 {head}）");
    }

    static void DumpShellItems(IUniSearchLog log, string kind,
        IReadOnlyList<(uint Id, string Label)> items)
        => log.Info("selftest", $"shell 菜单附加项[{kind}]（{items.Count} 个）：{string.Join(" / ", items.Select(i => $"{i.Label} [0x{i.Id:X}]"))}");

    /// <summary>
    /// 托盘自检：核对图标是否创建成功、菜单项是否齐全；给了 <paramref name="invokeHeader"/> 就
    /// <b>模拟点那一下</b>。"右键托盘 → 退出"必须能被脚本验证 —— 合成鼠标会被 UIPI 拦掉，
    /// 只靠肉眼点一次不算验证。
    /// </summary>
    public static void RunTray(TrayPresence tray, IUniSearchLog log, string? invokeHeader)
    {
        log.Info("selftest", $"托盘 Ok={tray.Ok} 有图标={tray.HasIcon} 错误=[{tray.Error ?? "(无)"}]");
        log.Info("selftest", $"托盘菜单 = [{string.Join(" | ", tray.MenuHeaders)}]");

        if (string.IsNullOrEmpty(invokeHeader)) return;

        log.Info("selftest", $"模拟点击托盘菜单项「{invokeHeader}」…");
        var ok = tray.InvokeMenuItem(invokeHeader!, out var error);
        log.Info("selftest", ok ? $"菜单项「{invokeHeader}」已执行" : $"菜单项执行失败：{error}");
    }

    static ResultItemViewModel? First(SearchSessionViewModel vm, Func<ResultItemViewModel, bool> pred)
    {
        foreach (var it in vm.Rows)
            if (pred(it)) return it;
        return null;
    }

    static string SafeClipboard()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : "(空)"; }
        catch { return "(读不到)"; }
    }

    static (FrameworkElement?, ResultItemViewModel?) FindRowWithMenu(DependencyObject root)
    {
        if (root is FrameworkElement { ContextMenu: not null, DataContext: ResultItemViewModel vm })
            return ((FrameworkElement)root, vm);

        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var hit = FindRowWithMenu(VisualTreeHelper.GetChild(root, i));
            if (hit.Item1 is not null) return hit;
        }
        return (null, null);
    }

    /// <summary>
    /// 筛选器自检（filters.json）：定义文件读到了什么、问题在哪、点一个自定义筛选器之后
    /// <b>查询串有没有真的带上过滤条件</b>、结果有没有越界。
    /// <para>
    /// 光看"标签栏里多了一个「生信相关」"不算数 —— 标签能显示但点了不过滤，是最容易糊过去的那种坏法。
    /// </para>
    /// </summary>
    public static async Task RunFiltersAsync(SearchSessionViewModel vm, IUniSearchLog log)
    {
        var cat = vm.Catalog;
        log.Info("selftest", $"=== 筛选器自检：{cat.All.Count} 个定义，来源=[{string.Join(", ", cat.Sources)}] ===");
        foreach (var f in cat.All)
            log.Info("selftest", $"定义 {f.Id}「{f.Name}」扩展名={f.Extensions.Count} 类型=[{string.Join(",", f.Kinds)}] " +
                                 $"正则={(f.NameRegex is null ? "-" : f.NamePattern)} " +
                                 $"适用后端=[{(f.Providers.Count == 0 ? "所有" : string.Join(",", f.Providers))}] order={f.Order}");
        foreach (var p in cat.Problems) log.Warn("selftest", $"定义问题：{p}");

        log.Info("selftest", $"当前来源（{vm.SourceLabel}）下可见的筛选器=[{string.Join(", ", cat.For(vm.EffectiveProviderScope).Select(f => f.Id))}]");
        log.Info("selftest", $"标签栏=[{string.Join(", ", vm.Tabs.Select(t => t.Id + (t.IsCustom ? "(自定义)" : "")))}]");

        // 每个筛选器在当前结果集里命中多少 —— 匹配逻辑的直接证据（不依赖"刚好搜到生信文件"）
        var counts = cat.All.Select(f => $"{f.Id}={vm.Rows.Count(r => f.Matches(r.Source))}");
        log.Info("selftest", $"当前 {vm.Rows.Count} 行里各筛选器命中：[{string.Join(", ", counts)}]");

        var probe = cat.All.FirstOrDefault();
        if (probe is null)
        {
            log.Warn("selftest", "没有加载到任何筛选器定义（dist\\filters.json 或 %LOCALAPPDATA%\\UniSearch\\filters.json 都不在？）");
            return;
        }

        // 等"行数真的变了"而不是死等固定时长：固定延迟下快照可能还没回来，
        // 于是"回到全部"读到的还是上一次的行数，看起来像"取消筛选没生效"（踩过）。
        var before = vm.Rows.Count;
        vm.SelectedTabId = probe.Id;
        var changed = await WaitForRowsChangeAsync(vm, before).ConfigureAwait(true);
        var bad = vm.Rows.Where(r => !probe.Matches(r.Source)).Take(5).Select(r => $"{r.Title}({r.ExtensionLabel})").ToList();
        log.Info("selftest", $"选中「{probe.Name}」：发给后端的查询串=[{vm.LastProviderText}]（期望含 ext:）");
        log.Info("selftest", $"  行数 {before} -> {vm.Rows.Count}（等到新快照={changed}）" +
                             $"越界行=[{(bad.Count == 0 ? "无" : string.Join(", ", bad))}]（期望无）");
        log.Info("selftest", $"  前 5 行=[{string.Join(", ", vm.Rows.Take(5).Select(r => $"{r.Title}({r.ExtensionLabel})"))}]");

        var filtered = vm.Rows.Count;
        vm.SelectedTabId = CategoryIds.All;
        changed = await WaitForRowsChangeAsync(vm, filtered).ConfigureAwait(true);
        log.Info("selftest", $"回到「全部」：行数 {filtered} -> {vm.Rows.Count}（等到新快照={changed}）" +
                             $"查询串=[{vm.LastProviderText}]（期望不含 ext:）");
    }

    /// <summary>等到行数变化（最多 5 秒）。返回是否等到 —— 没等到就是真有问题，不是"慢"。</summary>
    static async Task<bool> WaitForRowsChangeAsync(SearchSessionViewModel vm, int before, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(80).ConfigureAwait(true);
            if (vm.Rows.Count != before) return true;
        }
        return false;
    }

    /// <summary>
    /// 多选自检（2026-09-25 第 12 轮）：直接喂选中集合给视图模型，断言
    /// 单选 / 多选 / 清空三种状态下 —— 计数、主选中项、汇总文案、状态条、路径收集。
    /// 真实 Ctrl/Shift 点击需要人手（合成鼠标会被 UIPI 拦），这里验的是"选择模型"本身。
    /// 配合 <c>--query</c> 用：没有结果就没有可选的行。
    /// </summary>
    public static void RunMultiSelect(SearchSessionViewModel vm, IUniSearchLog log)
    {
        if (vm.Rows.Count < 3)
        {
            log.Warn("selftest", $"多选自检需要至少 3 行结果，当前 {vm.Rows.Count} 行 —— 跳过");
            return;
        }

        var three = vm.Rows.Take(3).ToList();
        bool ok = true;

        // ① 单选：与改造前完全一致（无汇总、非多选）
        vm.SyncSelection(new[] { three[0] });
        var singleOk = vm.SelectionCount == 1 && !vm.HasMultiSelection && vm.SelectionSummary is null
                       && ReferenceEquals(vm.Selected, three[0]);
        log.Info("selftest", $"单选：count={vm.SelectionCount} multi={vm.HasMultiSelection} " +
                             $"summary={vm.SelectionSummary ?? "null"} 主选中={vm.Selected?.Title}");
        ok &= singleOk;

        // ② 多选：主选中项必须是第一个（既有单选语义不能漂）
        vm.SyncSelection(three);
        var paths = vm.SelectedPaths();
        var multiOk = vm.SelectionCount == 3 && vm.HasMultiSelection
                      && ReferenceEquals(vm.Selected, three[0])
                      && vm.StatusText.Contains("已选 3 项", StringComparison.Ordinal);
        log.Info("selftest", $"多选：count={vm.SelectionCount} multi={vm.HasMultiSelection} " +
                             $"主选中={vm.Selected?.Title}（期望第一行）汇总={vm.SelectionSummary}");
        log.Info("selftest", $"  状态条: {vm.StatusText}");
        log.Info("selftest", $"  路径收集: {paths.Count} 条（有真实路径的才收）" +
                             $"{(paths.Count > 0 ? " 例：" + paths[0] : "")}");
        ok &= multiOk;

        // ③ 右键落点决策（纯函数）：真实右键手势自动化不了，决策逻辑必须能断言
        var keep = SearchSessionViewModel.DecideRightClick(true, 3);
        var resetSingle = SearchSessionViewModel.DecideRightClick(true, 1);
        var resetOther = SearchSessionViewModel.DecideRightClick(false, 3);
        var decideOk = keep == SearchSessionViewModel.RightClickDecision.KeepSelection
                       && resetSingle == SearchSessionViewModel.RightClickDecision.ResetToRow
                       && resetOther == SearchSessionViewModel.RightClickDecision.ResetToRow;
        log.Info("selftest", $"右键落点：已选中&多选={keep}（期望 KeepSelection）；" +
                             $"已选中&单选={resetSingle}（期望 ResetToRow）；未选中&多选={resetOther}（期望 ResetToRow）");
        ok &= decideOk;

        // ④ 批量菜单项 + 复制路径真跑一次（读剪贴板核对行数）
        var items = SearchSessionViewModel.BuildBatchMenuItems(vm.SelectionCount);
        log.Info("selftest", $"批量菜单项：{string.Join(" | ", items.Select(i => i.Label))}");
        vm.CopySelectedPaths();
        var clip = SafeClipboard();
        var clipLines = string.IsNullOrEmpty(clip) ? 0 : clip.Split('\n').Length;
        var pathCount = vm.SelectedPaths().Count;
        log.Info("selftest", $"复制 {vm.SelectionCount} 个路径 -> 剪贴板 {clipLines} 行（期望 {pathCount}）");
        ok &= clipLines == pathCount;

        // ⑤ 清空：Esc 的语义（先取消选择，窗口还在）
        vm.SyncSelection(Array.Empty<ResultItemViewModel>());
        var clearOk = vm.SelectionCount == 0 && !vm.HasMultiSelection && vm.SelectionSummary is null;
        log.Info("selftest", $"清空：count={vm.SelectionCount} multi={vm.HasMultiSelection} " +
                             $"summary={vm.SelectionSummary ?? "null"}");
        ok &= clearOk;

        // 收尾：回到单选第一行，别把窗口留在"无选中"状态
        vm.SyncSelection(new[] { three[0] });
        log.Info("selftest", ok ? "多选自检：全部通过 ✓" : "多选自检：有失败 ✗");
    }

    /// <summary>
    /// 压缩自检（第 12 轮阶段 3）：造一个含"同名文件 + 中文名 + 子目录"的小树，走**真实**的
    /// 规划与压缩，再把 zip 读回来核对条目名与内容。
    /// <para>
    /// 为什么不走 <c>vm.ArchiveSelectedAsZip()</c>：那需要选中"搜索结果里的行"，会往用户的搜索目录里
    /// 写 zip（副作用）。这里验的是压缩引擎与规划这一层，VM 那层只是把两者接起来。
    /// </para>
    /// </summary>
    public static void RunArchive(IUniSearchLog log)
    {
        var root = Path.Combine(Path.GetTempPath(), "unisearch-archive-selftest");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(Path.Combine(root, "甲"));
            Directory.CreateDirectory(Path.Combine(root, "乙"));
            File.WriteAllText(Path.Combine(root, "甲", "同名.txt"), "AAA");
            File.WriteAllText(Path.Combine(root, "乙", "同名.txt"), "BBB");
            File.WriteAllText(Path.Combine(root, "根文件.txt"), "CCC");

            var paths = new[]
            {
                Path.Combine(root, "甲", "同名.txt"),
                Path.Combine(root, "乙", "同名.txt"),
                Path.Combine(root, "根文件.txt"),
            };

            var plan = ArchivePlanner.PlanEntries(paths);
            log.Info("selftest", "压缩规划：" +
                string.Join(" | ", plan.Select(p => $"{Path.GetFileName(p.Source)} -> {p.Entry}")));

            var zipPath = Path.Combine(root, "测试包.zip");
            var (added, failures) = ArchiveService.CreateZip(plan, zipPath);
            var size = File.Exists(zipPath) ? new FileInfo(zipPath).Length : -1;
            log.Info("selftest", $"真压：added={added} failures={failures.Count} exists={File.Exists(zipPath)} size={size}B");

            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            log.Info("selftest", $"读回条目：{string.Join(" | ", entries)}");

            string ReadEntry(string name)
            {
                var e = zip.Entries.FirstOrDefault(x => x.FullName == name);
                if (e is null) return "<缺>";
                using var s = e.Open();
                using var r = new StreamReader(s);
                return r.ReadToEnd();
            }

            var first = ReadEntry("同名.txt");
            var second = ReadEntry("乙/同名.txt");
            var third = ReadEntry("根文件.txt");
            log.Info("selftest", $"内容核对：同名.txt=[{first}]（期望 AAA）· 乙/同名.txt=[{second}]（期望 BBB）· 根文件.txt=[{third}]（期望 CCC）");

            var ok = added == 3 && failures.Count == 0
                     && first == "AAA" && second == "BBB" && third == "CCC";
            log.Info("selftest", ok ? "压缩自检：全部通过 ✓" : "压缩自检：有失败 ✗");
        }
        catch (Exception ex)
        {
            log.Error("selftest", "压缩自检异常", ex);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
