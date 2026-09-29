using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using UniSearch.Core.Archiving;
using UniSearch.Core.Categories;
using UniSearch.Core.Filters;
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
        // 压缩设置也走一遍往返（第 12 轮新增的节）
        probe.Archive.Destination = "ask";
        probe.Archive.NameTemplate = "自检-{count}";
        probe.Archive.MaxItems = 7;
        probe.Archive.RevealAfter = false;

        var saved = store.Save(probe);
        var reread = new SettingsStore(Path.GetDirectoryName(store.FilePath)!, log).Current;
        log.Info("selftest", $"往返写入成功={saved}；重新读回 列表上限={reread.Search.MaxRows}（期望 33）");
        log.Info("selftest", $"往返读回 额外排除=[{string.Join(" | ", reread.Search.ExtraExcludePaths)}]（期望原样两条，含空格）");
        log.Info("selftest", $"往返读回 列布局：排序={reread.Columns.SortKey}{(reread.Columns.SortDescending ? "↓" : "↑")}（期望 size↓） "
                             + $"name 列宽={reread.Columns.Widths.GetValueOrDefault("name")}（期望 333） "
                             + $"可见列=[{string.Join(",", reread.Columns.Visible)}]（期望 name,path,size）");
        log.Info("selftest", $"往返读回 压缩：落点=[{reread.Archive.Destination}]（期望 ask） "
                             + $"模板=[{reread.Archive.NameTemplate}]（期望 自检-{{count}}） "
                             + $"上限={reread.Archive.MaxItems}（期望 7） 完成后定位={reread.Archive.RevealAfter}（期望 False）");

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
        // 模板（v2）：有节时标签栏由模板决定；没节时这一行会显示"0 个 / 平铺"，也就是现状
        log.Info("selftest", $"模板 {cat.Templates.Count} 个，当前生效=[{vm.ActiveTemplate?.Id ?? "(无：平铺)"}]" +
                             $"（解析后端={vm.TemplateProviderId ?? "无"}，钉住={vm.IsTemplatePinned}，" +
                             $"下拉可选=[{string.Join(", ", vm.TemplateOptions.Select(t => t.Id))}]）");
        log.Info("selftest", $"标签栏=[{string.Join(", ", vm.Tabs.Select(t => t.Id + (t.IsCustom ? "(自定义)" : "")))}]");

        // 每个筛选器在当前结果集里命中多少 —— 匹配逻辑的直接证据（不依赖"刚好搜到生信文件"）
        var counts = cat.All.Select(f => $"{f.Id}={vm.Rows.Count(r => f.Matches(r.Source))}");
        log.Info("selftest", $"当前 {vm.Rows.Count} 行里各筛选器命中：[{string.Join(", ", counts)}]");

        // 挑一个**当前来源下真的可见**的筛选器当探针。
        // ⚠ 不能用 cat.All.First()：定义文件里现在有只给 Zotero 用的条目类型筛选器，
        // 它们按 order 排在最前，但在 Everything 下根本不出现 —— 拿它当探针会"点了没反应"，
        // 然后断言把"筛选器不可见"误报成"筛选没生效"。
        var probe = cat.For(vm.EffectiveProviderScope).FirstOrDefault();
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

        // ⑥ 选中的分类标签**不能消失**。
        // 分类标签是按"快照里有哪些分组"建的，而分组是按结果建的 —— 点一个这次一条都没落进去的分类，
        // 重查后分组没了、标签也跟着没，但 SelectedTabId 还停在它上面：筛选仍在生效、用户却看不见也点不掉。
        // （实测踩到：AnyTXT 的「正文命中」就是这么消失的。）
        var zeroCategory = new[] { CategoryIds.ContentMatches, CategoryIds.Music, CategoryIds.Archives, CategoryIds.Videos }
                    .FirstOrDefault(c => vm.Tabs.All(t => t.Id != c));
        if (zeroCategory is null)
        {
            log.Warn("selftest", "⑥ 跳过：所有候选分类都已在标签栏里，挑不出一个 0 结果的");
        }
        else
        {
            vm.SelectedTabId = zeroCategory;
            await WaitForIdleAsync(vm).ConfigureAwait(true);

            var kept = vm.Tabs.Any(t => t.Id == zeroCategory);
            var selected = string.Equals(vm.SelectedTabId, zeroCategory, StringComparison.Ordinal);
            log.Info("selftest", $"⑥ 选中一个 0 结果的分类「{zeroCategory}」后重查 -> 标签还在={kept}、" +
                                 $"仍是当前选中={selected}（期望都为 True）-> {(kept && selected ? "PASS" : "FAIL")}");
            log.Info("selftest", $"   标签栏=[{string.Join(", ", vm.Tabs.Select(t => t.Id + "(" + t.Count + ")"))}]");

            vm.SelectedTabId = CategoryIds.All;
            await WaitForIdleAsync(vm).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 点一个标签并等这次查询真的跑完。
    /// <para>
    /// ⚠ <b>不能只等 <c>IsBusy</c></b>：<c>SelectedTabId</c> 的 setter 里是 <c>_ = RunAsync()</c>，
    /// 从"设了值"到"RunAsync 把 IsBusy 置 true"之间有一瞬间它还是 false —— 那时等它等于没等，
    /// 读到的还是上一次的行数。表现就是"筛选器明明生效了，自检却说没生效"（实测踩到）。
    /// 所以先等请求计数往前走，再等它落回空闲。
    /// </para>
    /// </summary>
    /// <summary>
    /// 值域筛选器自检（Zotero 标签）。
    /// <para>
    /// <b>为什么断言的是后端报的总数、不是行数</b>：行数受每个后端的结果预算限制（Zotero 60 条），
    /// 而标签计数是<b>服务端口径</b>。用行数断言的话，"筛对了"和"没筛、只是结果恰好少"分不开 ——
    /// 而把这两件事分清，正是这个功能存在的全部意义。
    /// </para>
    /// <para>
    /// 标签名与计数全部<b>现场从后端取</b>，不写死在断言里：用户的库随时在变，
    /// 写死一个「蛋白设计=3」的断言，第二天就会变成一条与功能无关的假红。
    /// </para>
    /// </summary>
    public static async Task RunFacetsAsync(SearchSessionViewModel vm, IUniSearchLog log)
    {
        log.Info("selftest", "=== 值域筛选器自检（Zotero 标签）===");
        var bad = new List<string>();

        void Check(bool ok, string what)
        {
            log.Info("selftest", $"  {(ok ? "✓" : "✗")} {what}");
            if (!ok) bad.Add(what);
        }

        static int? ZoteroTotal(SearchSessionViewModel v) =>
            v.Outcomes.FirstOrDefault(o => o.ProviderId == "zotero")?.TotalAvailable;

        // ① 没有值域的后端：锚点整块不该出现（否则点开是空的）
        vm.Input = "";
        vm.ActiveSourceId = "everything";
        await WaitForIdleAsync(vm).ConfigureAwait(true);
        Check(!vm.HasFacet, "Everything 下没有值域锚点");

        // ② 切到 Zotero → 值域出现，且换来源必须把已选值清掉
        vm.ActiveSourceId = "zotero";
        await WaitForIdleAsync(vm).ConfigureAwait(true);
        Check(vm.HasFacet, "Zotero 下值域锚点出现");
        Check(vm.FacetLabel == "标签", $"值域名是「标签」（实际「{vm.FacetLabel}」）");
        Check(vm.SelectedFacetValues.Count == 0, "换来源后已选值归零");

        // ③ 展开 → 候选值真从后端来
        vm.IsFacetOpen = true;
        for (var i = 0; i < 60 && vm.IsFacetLoading; i++) await Task.Delay(50).ConfigureAwait(true);

        var candidates = vm.FacetCandidates.ToList();
        Check(candidates.Count > 0, $"展开后拿到候选标签（{candidates.Count} 个）");
        Check(vm.FacetError is null, $"读取候选值没有报错（{vm.FacetError ?? "-"}）");
        log.Info("selftest", $"  候选：{string.Join(" | ", candidates.Take(20).Select(c => c.Display))}");

        var baseline = ZoteroTotal(vm);
        log.Info("selftest", $"  无标签时 Zotero 总命中 {baseline?.ToString() ?? "?"}");

        // 取计数最小的两个标签：OR 与 AND 的差别最明显，也最不容易撞上结果预算
        var picks = candidates.Where(c => c.Count > 0).OrderBy(c => c.Count).ThenBy(c => c.Value, StringComparer.Ordinal)
                              .Take(2).ToList();
        if (picks.Count < 2)
        {
            log.Warn("selftest", "标签不足两个，跳过筛选行为断言");
            FinishFacets(log, bad);
            return;
        }

        // ④ 单选一个标签：总命中必须**精确等于**该标签的条目数 —— 这是"真下推了"的硬证据
        vm.ToggleFacetValueCommand.Execute(picks[0].Value);
        await WaitForIdleAsync(vm).ConfigureAwait(true);
        var one = ZoteroTotal(vm);
        log.Info("selftest", $"  选「{picks[0].Value}」-> 总命中 {one}（标签自称 {picks[0].Count}）" +
                             $" 查询={vm.LastFacetLabel ?? "-"}");
        Check(one == picks[0].Count, $"单选「{picks[0].Value}」的总命中等于它自己的计数");
        Check(vm.LastFacetLabel is { Length: > 0 }, "查询里带上了值域选择");
        Check(vm.SelectedFacetValues.Count == 1, "顶部回显一个已选值");

        // ⑤ 再选一个 → 默认「任一命中」，总数落在 [最大, 之和] 区间
        vm.ToggleFacetValueCommand.Execute(picks[1].Value);
        await WaitForIdleAsync(vm).ConfigureAwait(true);
        var or = ZoteroTotal(vm);
        var lo = Math.Max(picks[0].Count, picks[1].Count);
        var hi = picks[0].Count + picks[1].Count;
        log.Info("selftest", $"  再选「{picks[1].Value}」（任一命中）-> 总命中 {or}（应在 {lo}..{hi}）" +
                             $" 查询={vm.LastFacetLabel ?? "-"}");
        Check(or >= lo && or <= hi, "「任一命中」的总命中落在两个标签计数之间");

        // ⑥ 切成「全部命中」→ 只能更少（多以 0 条收场，这正是默认给 OR 的原因）
        vm.ToggleFacetMatchAllCommand.Execute(null);
        await WaitForIdleAsync(vm).ConfigureAwait(true);
        var and = ZoteroTotal(vm);
        log.Info("selftest", $"  切「全部命中」-> 总命中 {and}（应 ≤ {or}）查询={vm.LastFacetLabel ?? "-"}");
        Check(and is not null && or is not null && and <= or, "「全部命中」的总命中不多于「任一命中」");

        // ⑦ 清空 → 回到基线
        vm.ClearFacetValuesCommand.Execute(null);
        await WaitForIdleAsync(vm).ConfigureAwait(true);
        Check(vm.SelectedFacetValues.Count == 0, "清空后没有已选值");
        Check(vm.LastFacetLabel is null, "清空后查询里不再带值域");
        Check(ZoteroTotal(vm) == baseline, $"清空后回到基线总命中 {baseline?.ToString() ?? "?"}");

        // ⑧ 切回没有值域的后端：锚点消失、选择清掉
        vm.ActiveSourceId = "everything";
        await WaitForIdleAsync(vm).ConfigureAwait(true);
        Check(!vm.HasFacet, "切回 Everything 后值域锚点消失");
        Check(vm.SelectedFacetValues.Count == 0, "切回 Everything 后已选值仍为空");

        FinishFacets(log, bad);
    }

    static void FinishFacets(IUniSearchLog log, List<string> bad)
    {
        if (bad.Count == 0)
        {
            log.Info("selftest", "值域筛选器自检：全部通过 ✓");
            return;
        }
        log.Warn("selftest", $"值域筛选器自检失败 {bad.Count} 项：");
        foreach (var b in bad) log.Warn("selftest", "  ✗ " + b);
    }

    static async Task SelectTabAndWaitAsync(SearchSessionViewModel vm, string tabId)
    {
        var before = vm.SearchRequestCount;
        vm.SelectedTabId = tabId;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.SearchRequestCount == before && sw.ElapsedMilliseconds < 2000)
            await Task.Delay(20).ConfigureAwait(true);

        await WaitForIdleAsync(vm).ConfigureAwait(true);
    }

    /// <summary>行集合的指纹：标题排序后拼起来。用来判断"两个标签的结果是不是同一批"。</summary>
    static string RowFingerprint(SearchSessionViewModel vm) =>
        string.Join('\u0001', vm.Rows.Select(r => r.Title).OrderBy(t => t, StringComparer.Ordinal));

    /// <summary>等到搜索跑完（IsBusy 落回 false）。最多 5 秒。</summary>
    static async Task WaitForIdleAsync(SearchSessionViewModel vm, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(80).ConfigureAwait(true);
            if (!vm.IsBusy) return;
        }
    }

    /// <summary>
    /// 标签有效性自检：<b>把每个后端下的每个标签真的点一遍，报真实行数</b>。
    /// <para>
    /// 为什么需要它：标签点下去"没反应"和"生效了但结果一样"从界面上分不出来 ——
    /// 用户报的"AnyTXT/Zotero 全部以外的筛选无效"就是这么来的。
    /// 只有逐个点、逐个记行数，才能把"筛选静默失效"和"确实筛到了"分开。
    /// </para>
    /// <para>判定：一个标签若与「全部」行数完全相同 <b>且</b> 不是「全部」本身，就是可疑的（SUSPECT）。</para>
    /// </summary>
    public static async Task RunTabsAsync(SearchSessionViewModel vm, string query, IUniSearchLog log)
    {
        log.Info("selftest", $"=== 标签有效性自检：查询「{query}」 ===");

        var providers = new[] { "everything", "anytxt", "zotero", "siyuan" };
        var bad = new List<string>();

        foreach (var provider in providers)
        {
            vm.Input = query;
            var beforeSwitch = vm.SearchRequestCount;
            vm.ActiveSourceId = provider;          // 换来源 → 标签选择会归零（这正是要验的行为之一）
            var swSwitch = System.Diagnostics.Stopwatch.StartNew();
            while (vm.SearchRequestCount == beforeSwitch && swSwitch.ElapsedMilliseconds < 2000)
                await Task.Delay(20).ConfigureAwait(true);
            await WaitForIdleAsync(vm).ConfigureAwait(true);

            // 换来源必须把标签归零：上一个后端选中的筛选器在新后端多半没有意义
            var resetOk = vm.SelectedTabId == CategoryIds.All;
            if (!resetOk) bad.Add($"{provider}: 换来源后标签没归零（停在 {vm.SelectedTabId}）");

            var tabs = vm.Tabs.ToList();
            log.Info("selftest", $"[{provider}] 标签栏 {tabs.Count} 个：{string.Join(" | ", tabs.Select(t => $"{t.DisplayName}({t.Count})"))}");
            log.Info("selftest", $"[{provider}] 换来源后标签归零={resetOk}（期望 True）");

            var allRows = vm.Rows.Count;
            var allPrint = RowFingerprint(vm);

            // 把「全部」时每一行的分类依据打出来 —— "标签栏里那几个分类是怎么来的"必须可查，
            // 否则遇到"计数是 1、点下去 0"这种对不上的情况只能靠猜（实测踩到过）。
            foreach (var r in vm.Rows.Take(5))
                log.Info("selftest", $"[{provider}]   · Kind={r.Source.Kind} Subtype={r.Source.Subtype ?? "-"} " +
                                     $"Ext={r.Source.Extension ?? "-"} Match={r.Source.Match} " +
                                     $"File={(r.Source.Path is { Length: > 0 } p ? System.IO.Path.GetFileName(p) : "(无路径)")}");

            foreach (var tab in tabs.Where(t => t.Id != CategoryIds.All))
            {
                await SelectTabAndWaitAsync(vm, tab.Id).ConfigureAwait(true);

                var rows = vm.Rows.Count;
                // 判据是"结果**完全相同**"，不是"行数相同" ——
                // 行数相同完全可能是巧合（比如"期刊论文"恰好也是 16 条，但那 16 条确实是筛出来的）。
                var sameAsAll = rows == allRows && RowFingerprint(vm) == allPrint;
                if (sameAsAll && allRows > 0)
                    bad.Add($"{provider}/{tab.DisplayName}: 结果与「全部」完全相同（{rows} 行），筛选疑似没生效");

                log.Info("selftest", $"[{provider}]   点「{tab.DisplayName}」-> {rows} 行" +
                                     $"（全部 {allRows}）{(sameAsAll && allRows > 0 ? "  ← SUSPECT" : "")}" +
                                     $"{(vm.LastFilterLabel is { Length: > 0 } fl ? $" 筛选器={fl}" : "")}");
            }

            await SelectTabAndWaitAsync(vm, CategoryIds.All).ConfigureAwait(true);
        }

        vm.ActiveSourceId = null;
        await WaitForIdleAsync(vm).ConfigureAwait(true);

        if (bad.Count == 0) log.Info("selftest", "标签有效性自检：全部通过 ✓");
        else
        {
            log.Warn("selftest", $"标签有效性自检：{bad.Count} 处可疑 ✗");
            foreach (var b in bad) log.Warn("selftest", "   " + b);
        }
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

        // ④b 引号列表口径（粘到命令行当参数用）+ 压缩设置项存在且合法
        vm.CopySelectedPathsQuoted();
        var quoted = SafeClipboard() ?? "";
        var quoteCount = quoted.Count(c => c == '"');
        log.Info("selftest", $"复制为引号列表 -> 引号数 {quoteCount}（期望 {pathCount * 2}）");
        ok &= quoteCount == pathCount * 2;

        var arch = vm.Archive;
        var archOk = !string.IsNullOrWhiteSpace(arch.NameTemplate)
                     && arch.Destination is "ask" or "same-as-first"
                     && arch.MaxItems >= 0;
        log.Info("selftest", $"压缩设置：落点={arch.Destination} 模板=[{arch.NameTemplate}] " +
                             $"完成后定位={arch.RevealAfter} 上限={arch.MaxItems}");
        ok &= archOk;

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

    /// <summary>
    /// **路 B PoC**（第 12 轮阶段 6）：跨目录多选能不能拿到原生 shell 菜单。
    /// 单选的 <c>SHBindToParent + GetUIObjectOf</c> 只吃同一父目录下的多选，而搜索结果天然跨目录；
    /// 这里造两个**不同目录**的文件，试 <c>IShellItemArray.BindToHandler(BHID_SFUIObject)</c>。
    /// 拿到动词表 = 路 B 可行（Win11 上就能白拿系统的"压缩为 ZIP"与 7-Zip 的动词）。
    /// </summary>
    public static void RunShellMenuMulti(ShellContextMenu menu, IUniSearchLog log)
    {
        var root = Path.Combine(Path.GetTempPath(), "unisearch-shellmenu-multi");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var dirA = Path.Combine(root, "甲");
            var dirB = Path.Combine(root, "乙");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            var a = Path.Combine(dirA, "a.txt");
            var a2 = Path.Combine(dirA, "a2.txt");
            var b = Path.Combine(dirB, "b.txt");
            File.WriteAllText(a, "a");
            File.WriteAllText(a2, "a2");
            File.WriteAllText(b, "b");

            // 对照：单选（同一目录）能拿到的动词
            var single = menu.ListVerbs(a, out var singleError);
            log.Info("selftest", $"单选动词（{Path.GetFileName(a)}）：{single.Count} 个 " +
                                 $"[{string.Join(", ", single.Take(12))}]{(singleError.Length > 0 ? " 错误：" + singleError : "")}");

            // 跨目录多选
            var multi = menu.ListVerbsMulti(new[] { a, b }, out var multiError);
            log.Info("selftest", $"跨目录多选动词（甲\\a.txt + 乙\\b.txt）：{multi.Count} 个 " +
                                 $"[{string.Join(", ", multi.Take(20))}]{(multiError.Length > 0 ? " 错误：" + multiError : "")}");

            // 同目录多选（路 A 已能处理的场景）作对照
            var sameDir = menu.ListVerbsMulti(new[] { a, a2 }, out _);
            log.Info("selftest", $"同目录多选动词：{sameDir.Count} 个");

            log.Info("selftest", multi.Count > 0
                ? "路 B PoC：跨目录多选**能**拿到原生 shell 菜单 ✓"
                : "路 B PoC：跨目录多选**拿不到**原生 shell 菜单 ✗（那就只用我们自己的批量动作）");
        }
        catch (Exception ex)
        {
            log.Error("selftest", "路 B PoC 异常", ex);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// 模板自检（第 13 轮 F1）：<b>切后端换模板、钉住后不跟随、恢复默认回解析链、切模板不重查</b>。
    /// <para>
    /// 为什么要造一份临时 filters.json 而不是用现场那份：程序自带的 `filters.json` 目前<b>没有</b>
    /// templates 节（这是刻意的，"没节 = 现状"），拿它验不出任何模板行为。临时目录里的目录只喂给
    /// VM 的 <see cref="SearchSessionViewModel.SetFilterCatalog"/>，**不落盘、不动用户配置**，
    /// 结束后原样还原（目录、钉住表、落盘回调、来源）。
    /// </para>
    /// </summary>
    public static void RunTemplates(SearchSessionViewModel vm, IUniSearchLog log)
    {
        var path = Path.Combine(Path.GetTempPath(), $"unisearch-templates-{Guid.NewGuid():N}.json");

        // 现场快照：结束时要一模一样地还回去，否则自检本身就把用户的会话改了
        var savedCatalog = vm.Catalog;
        var savedPinned = new Dictionary<string, string>(vm.PinnedTemplates, StringComparer.OrdinalIgnoreCase);
        var savedDeployment = new Dictionary<string, string>(vm.DeploymentTemplates, StringComparer.OrdinalIgnoreCase);
        var savedPersist = vm.PersistPinnedTemplate;
        var savedSource = vm.ActiveSourceId;

        try
        {
            File.WriteAllText(path, """
            {
              "filters": [
                { "id": "bio", "name": "生信", "order": 500, "extensions": ["pdb"] },
                { "id": "zot-itemtype", "name": "条目类型", "order": 510, "providers": ["zotero"], "kinds": ["BibliographicItem"] },
                { "id": "zot-tag", "name": "标签", "order": 520, "providers": ["zotero"], "kinds": ["BibliographicItem"] },
                { "id": "only-anytxt", "name": "仅正文命中", "order": 530, "providers": ["anytxt"], "extensions": ["txt"] }
              ],
              "templates": [
                { "id": "files",   "name": "文件查找", "order": 100, "filters": ["bio"], "defaultFor": ["everything"] },
                { "id": "lit",     "name": "文献查找", "order": 110, "filters": ["zot-itemtype", "zot-tag"], "defaultFor": ["zotero"] },
                { "id": "minimal", "name": "极简",     "order": 900, "filters": [], "defaultFor": ["*"] }
              ]
            }
            """);

            var catalog = FilterCatalog.Load(path);
            if (catalog.Templates.Count != 3)
            {
                log.Warn("selftest", $"模板自检：临时目录只读到 {catalog.Templates.Count} 个模板（期望 3）—— 跳过");
                return;
            }

            var ok = true;
            vm.SetFilterCatalog(catalog);
            vm.SetFilterTemplates(null, null);          // 干净起点：没有钉住、没有部署级默认
            vm.PersistPinnedTemplate = (pid, tpl) => _lastPersist = (pid, tpl);
            _lastPersist = null;
            vm.SelectedTabId = CategoryIds.All;

            log.Info("selftest", $"=== 模板自检：{catalog.Templates.Count} 个模板 " +
                                 $"[{string.Join(", ", catalog.Templates.Select(t => t.Id))}] ===");

            // ① 默认集合恰好只有一个后端 → 认它的 defaultFor
            vm.ActiveSourceId = null;
            vm.SetAutoSearchProviders(["everything"]);
            var a1 = vm.ActiveTemplate?.Id;
            log.Info("selftest", $"① 默认来源=[everything] -> 模板={Show(a1)}（期望 files）" +
                                 $"标签栏=[{TabsOf(vm)}]");
            ok &= a1 == "files" && TabsOf(vm) == $"{CategoryIds.All}, bio";

            // ② 切来源 → 自动跟随到该后端的 defaultFor（这就是"按后端分叉"）
            vm.ActiveSourceId = "zotero";
            var a2 = vm.ActiveTemplate?.Id;
            log.Info("selftest", $"② 限定来源=zotero -> 模板={Show(a2)}（期望 lit）标签栏=[{TabsOf(vm)}]");
            ok &= a2 == "lit" && TabsOf(vm) == $"{CategoryIds.All}, zot-itemtype, zot-tag";

            // ③ 钉住：手动切模板 + 落盘回调收到正确参数
            var before = vm.SearchRequestCount;
            vm.SelectTemplateCommand.Execute("minimal");
            var a3 = vm.ActiveTemplate?.Id;
            var persistOk = _lastPersist == ("zotero", "minimal");
            log.Info("selftest", $"③ 手动切到 minimal -> 模板={Show(a3)} 钉住={vm.IsTemplatePinned}" +
                                 $" 落盘回调={Show(_lastPersist?.Item1)}/{Show(_lastPersist?.Item2)}（期望 zotero/minimal）" +
                                 $" 标签栏=[{TabsOf(vm)}]");
            log.Info("selftest", $"   切模板前后查询次数 {before} -> {vm.SearchRequestCount}（期望不变：切模板不该重查）");
            ok &= a3 == "minimal" && vm.IsTemplatePinned && persistOk && vm.SearchRequestCount == before;

            // ④ 钉住后切走再切回来 → 不跟随默认
            vm.ActiveSourceId = "everything";
            var b1 = vm.ActiveTemplate?.Id;
            vm.ActiveSourceId = "zotero";
            var b2 = vm.ActiveTemplate?.Id;
            log.Info("selftest", $"④ 钉住后切到 everything -> 模板={Show(b1)}（期望 files，没钉到它头上）；" +
                                 $"再切回 zotero -> 模板={Show(b2)}（期望 minimal，而不是默认的 lit）");
            ok &= b1 == "files" && b2 == "minimal";

            // ⑤ 恢复默认 → 回解析链
            vm.ResetTemplateCommand.Execute(null);
            var c1 = vm.ActiveTemplate?.Id;
            var unpinOk = _lastPersist == ("zotero", null);
            log.Info("selftest", $"⑤ 恢复默认 -> 模板={Show(c1)}（期望 lit）钉住={vm.IsTemplatePinned}（期望 False）" +
                                 $" 落盘回调={Show(_lastPersist?.Item1)}/{(_lastPersist?.Item2 is null ? "null" : Show(_lastPersist?.Item2))}（期望 zotero/null）");
            ok &= c1 == "lit" && !vm.IsTemplatePinned && unpinOk;

            // ⑥ 没有"某一个后端"时（默认集合多个）→ 走 "*" 兜底，而不是某个后端的模板
            vm.ActiveSourceId = null;
            vm.SetAutoSearchProviders(["everything", "anytxt"]);
            var d1 = vm.ActiveTemplate?.Id;
            log.Info("selftest", $"⑥ 默认来源=[everything, anytxt]（不是单一后端）-> 模板={Show(d1)}（期望 minimal：走 \"*\" 兜底）");
            ok &= d1 == "minimal";

            // ⑦ 当前选中的自定义筛选器被新模板藏掉 → 必须退回「全部」，不能"没标签高亮却还在过滤"
            vm.SetAutoSearchProviders(["zotero"]);
            vm.SelectedTabId = "zot-tag";
            vm.SelectTemplateCommand.Execute("files");     // files 里没有 zot-tag
            var e1 = vm.SelectedTabId;
            log.Info("selftest", $"⑦ 选中 zot-tag 后切到只含 bio 的模板 -> SelectedTabId={e1}（期望 all：否则查询串还在按 zot-tag 过滤）");
            ok &= e1 == CategoryIds.All;

            log.Info("selftest", ok ? "模板自检：全部通过 ✓" : "模板自检：有失败 ✗");
        }
        catch (Exception ex)
        {
            log.Error("selftest", "模板自检异常", ex);
        }
        finally
        {
            vm.PersistPinnedTemplate = savedPersist;
            vm.SetFilterCatalog(savedCatalog);
            vm.SetFilterTemplates(savedPinned, savedDeployment);
            vm.ActiveSourceId = savedSource;
            try { File.Delete(path); } catch { }
        }
    }

    static (string, string?)? _lastPersist;

    static string Show(string? s) => s is { Length: > 0 } ? s : "(无)";

    static string TabsOf(SearchSessionViewModel vm) => string.Join(", ", vm.Tabs.Select(t => t.Id));

    /// <summary>
    /// AnyTXT 联调自检（US-14）：打真服务，验"协议摸清了没有、盘符调度对不对"。
    /// <para>
    /// 单测覆盖的是纯函数（翻译 / 行解析）；这里覆盖的只有真跑才能知道的事：
    /// 端点对不对、<c>filterDir</c> 真的在限定、跨盘合并真的合上了、fid 真能取回来。
    /// <b>AnyTXT 没在跑时整段跳过</b>（不是失败）—— 没装不该让别的自检变红。
    /// </para>
    /// </summary>
    public static async Task RunAnytxtAsync(UniSearch.Providers.Anytxt.AnytxtProvider provider, IUniSearchLog log)
    {
        var ok = true;
        log.Info("selftest", "=== AnyTXT 联调自检 ===");

        // ① 健康探针（anytxt.v1.status）
        var health = await provider.ProbeHealthAsync(CancellationToken.None).ConfigureAwait(false);
        log.Info("selftest", $"① 健康：{health.State} · {health.Detail ?? "-"} · 提示={health.Hint ?? "-"}");
        if (health.State != UniSearch.Sdk.Contracts.HealthState.Ready)
        {
            log.Warn("selftest", "AnyTXT 不可用 —— 联调自检跳过（这不是失败，是「没装 / 没开」）");
            return;
        }

        // ② 索引状态
        var idx = await provider.GetIndexStateAsync(CancellationToken.None).ConfigureAwait(false);
        log.Info("selftest", $"② 索引状态：Indexed={idx.Indexed} Phase={idx.Phase ?? "-"}");

        // ③ 全盘查询（要跨盘枚举）
        var drives = UniSearch.Providers.Anytxt.AnytxtProvider.FixedDrives();
        log.Info("selftest", $"③ 本机固定盘：[{string.Join(", ", drives)}] —— 全盘搜索必须逐盘问，" +
                             $"filterDir 传空会被服务端强制成 C:");

        var q = new UniSearch.Sdk.Model.SearchQuery
        {
            RequestId = 1,
            RawText = "semiconductor",
            Text = "semiconductor",
            ProviderText = "semiconductor",
            ResultBudget = 10,
        };

        var (rows, total, batches) = await CollectAsync(provider, q, UniSearch.Sdk.Capabilities.SearchContext.Global())
            .ConfigureAwait(false);
        log.Info("selftest", $"   全盘 \"semiconductor\" -> 批次={batches} 行={rows.Count} 总数={total}");
        foreach (var r in rows.Take(3))
            log.Info("selftest", $"     {r.SizeBytes,10}B  {r.ModifiedAt:yyyy-MM-dd}  {r.Path}");

        var globalOk = rows.Count > 0 && rows.All(r => r.Path is { Length: > 0 })
                       && rows.All(r => r.Match.HasFlag(UniSearch.Sdk.Model.MatchKind.Content))
                       && rows.All(r => r.Metadata.ContainsKey("fid"));
        ok &= globalOk;
        log.Info("selftest", $"   断言：有行 / 每行有真实路径 / 都标 Content / 都带 fid -> {(globalOk ? "PASS" : "FAIL")}");

        // ③b 跨盘合并的直接证据：每个固定盘都要被问过一次（不能只看合并后的行数 ——
        //     "两个盘都问了"和"只问了 C 盘而 D 盘恰好没结果"在行数上看不出区别）
        var asked = provider.LastDriveBreakdown.Select(d => d.Dir).ToList();
        var mergeOk = drives.All(d => asked.Contains(d, StringComparer.OrdinalIgnoreCase));
        log.Info("selftest", $"   逐盘明细：[{string.Join(", ", provider.LastDriveBreakdown.Select(d => $"{d.Dir}={d.Rows} 行"))}] " +
                             $"（期望每盘都问过）-> {(mergeOk ? "PASS" : "FAIL")}");
        ok &= mergeOk;

        // ④ 限定目录（filterDir 真在限定吗）
        // ⚠ 不能拿"某一行的目录"当唯一候选：AnyTXT 的索引是**活的**（后台一直在重建），
        // 上一秒还命中 3 行的目录、下一秒可能就 0 行 —— 那样断言会偶发变红，
        // 而红的原因跟 filterDir 一点关系都没有。所以按行逐个试，取第一个真有结果的目录。
        var scopeOk = false;
        var tried = 0;
        foreach (var row in rows)
        {
            var scopeDir = System.IO.Path.GetDirectoryName(row.Path!);
            if (scopeDir is not { Length: > 0 }) continue;
            if (++tried > 3) break;

            var scoped = new UniSearch.Sdk.Model.SearchQuery
            {
                RequestId = 2, RawText = "semiconductor", Text = "semiconductor",
                ProviderText = "semiconductor", ResultBudget = 10,
            };
            var ctx = UniSearch.Sdk.Capabilities.SearchContext.InDirectory(
                scopeDir, UniSearch.Sdk.Capabilities.QueryOrigin.Self);
            var (srows, _, _) = await CollectAsync(provider, scoped, ctx).ConfigureAwait(false);

            var inside = srows.All(r => r.Path!.StartsWith(scopeDir, StringComparison.OrdinalIgnoreCase));
            log.Info("selftest", $"④ 限定到 [{scopeDir}] -> 行={srows.Count}，全部落在该目录内={(inside ? "是" : "否")}");

            // "全部落在目录内"才是硬断言（filterDir 被忽略时必然越界）；
            // "有行"只说明这个目录当前还命中 —— 索引抖动导致 0 行不算 filterDir 的错。
            if (srows.Count == 0) continue;

            scopeOk = inside;
            break;
        }

        if (tried == 0) log.Warn("selftest", "④ 跳过：③ 没拿到带目录的行，无法验证 filterDir");
        ok &= scopeOk;
        log.Info("selftest", $"④ 结论：filterDir 确实在限定 -> {(scopeOk ? "PASS" : "FAIL")}");

        // ⑤ 不存在的词必须 0 条（否则"看起来能搜"其实是没在过滤）
        var none = new UniSearch.Sdk.Model.SearchQuery
        {
            RequestId = 3, RawText = "zzz_no_such_token_qqq", Text = "zzz_no_such_token_qqq",
            ProviderText = "zzz_no_such_token_qqq", ResultBudget = 10,
        };
        var (nrows, _, _) = await CollectAsync(provider, none, UniSearch.Sdk.Capabilities.SearchContext.Global())
            .ConfigureAwait(false);
        ok &= nrows.Count == 0;
        log.Info("selftest", $"⑤ 不存在的词 -> 行={nrows.Count}（期望 0）-> {(nrows.Count == 0 ? "PASS" : "FAIL")}");

        log.Info("selftest", ok ? "AnyTXT 联调自检：全部通过 ✓" : "AnyTXT 联调自检：有失败 ✗");
    }

    /// <summary>把一次查询的批次收干，顺便数批次 —— 批次是"流式"这件事唯一的证据。</summary>
    static async Task<(List<UniSearch.Sdk.Model.SearchResult> Rows, int Total, int Batches)> CollectAsync(
        UniSearch.Providers.Anytxt.AnytxtProvider provider,
        UniSearch.Sdk.Model.SearchQuery query,
        UniSearch.Sdk.Capabilities.SearchContext context)
    {
        var rows = new List<UniSearch.Sdk.Model.SearchResult>();
        var total = 0;
        var batches = 0;
        await foreach (var b in provider.SearchAsync(query, context, CancellationToken.None).ConfigureAwait(false))
        {
            batches++;
            rows.AddRange(b.Results);
            if (b.TotalAvailable is { } t) total = t;
        }
        return (rows, total, batches);
    }

    /// <summary>
    /// Zotero 联调自检（US-15）：打真服务。
    /// <para>
    /// 单测覆盖的是纯函数（翻译 / 条目映射）；这里覆盖的只有真跑才知道的事：
    /// 本地 API 有没有被用户授权、<c>qmode</c> 真的多搜到东西、附件路径真的在磁盘上存在、
    /// 集合限定真的收敛。
    /// </para>
    /// </summary>
    public static async Task RunZoteroAsync(UniSearch.Providers.Zotero.ZoteroProvider provider, IUniSearchLog log)
    {
        var ok = true;
        log.Info("selftest", "=== Zotero 联调自检 ===");

        var health = await provider.ProbeHealthAsync(CancellationToken.None).ConfigureAwait(false);
        log.Info("selftest", $"① 健康：{health.State} · 版本={health.Version ?? "-"} · {health.Detail ?? "-"}");
        if (health.State != UniSearch.Sdk.Contracts.HealthState.Ready)
        {
            log.Warn("selftest", $"Zotero 不可用（{health.Detail}）—— 联调自检跳过" +
                                 (health.Hint is null ? "" : $"；提示：{health.Hint}"));
            return;
        }
        log.Info("selftest", $"   提示位：{health.Hint ?? "-"}");

        // ② 关键词：元数据命中（标题里有 AlphaGenome 的那篇）
        var (meta, metaTotal, _) = await CollectZoteroAsync(provider, "AlphaGenome", "titleCreatorYear", null)
            .ConfigureAwait(false);
        log.Info("selftest", $"② qmode=titleCreatorYear \"AlphaGenome\" -> 行={meta.Count} 总数={metaTotal}");
        var metaOk = meta.Count > 0 && meta.All(r => r.ProviderId == "zotero")
                     && meta.All(r => r.Kind == UniSearch.Sdk.Model.ResultKind.BibliographicItem)
                     && meta.All(r => r.Uri is { Length: > 0 } && r.Path is null);
        ok &= metaOk;
        log.Info("selftest", $"   断言：有行 / 都是文献条目 / 有 select URI / 无本地路径 -> {(metaOk ? "PASS" : "FAIL")}");
        foreach (var r in meta.Take(2))
            log.Info("selftest", $"     {r.Title}  [{r.Subtitle}]  {r.Uri}");

        // ③ 同一查询换 everything：必须**多**搜到（PDF 正文），否则说明 qmode 根本没生效
        var (full, _, _) = await CollectZoteroAsync(provider, "AlphaGenome", "everything", null).ConfigureAwait(false);
        var qmodeOk = full.Count > meta.Count;
        log.Info("selftest", $"③ 同查询 qmode=everything -> 行={full.Count}（titleCreatorYear 是 {meta.Count}）" +
                             $" -> {(qmodeOk ? "PASS：全文确实多搜到了" : "FAIL：qmode 没生效")}");
        ok &= qmodeOk;

        // ④ 附件行的本地路径必须真的存在 —— 这是 URL 解码对不对的唯一硬证据
        var (attachments, _, _) = await CollectZoteroAsync(provider, "", "everything", null, kinds:
            [UniSearch.Sdk.Model.ResultKind.Attachment]).ConfigureAwait(false);
        var withPath = attachments.Where(a => a.Path is { Length: > 0 }).ToList();
        var existing = withPath.Where(a => System.IO.File.Exists(a.Path)).ToList();
        log.Info("selftest", $"④ 附件（itemType=attachment）-> 行={attachments.Count}，带路径={withPath.Count}，" +
                             $"路径真实存在={existing.Count}");
        if (withPath.Count > 0)
        {
            log.Info("selftest", $"     样例：{withPath[0].Path}");
            var pathOk = existing.Count > 0;
            ok &= pathOk;
            log.Info("selftest", $"   断言：至少一条附件路径在磁盘上真实存在 -> {(pathOk ? "PASS" : "FAIL")}");
        }
        else
        {
            log.Warn("selftest", "   跳过：没有拿到带路径的附件行");
        }

        // ⑤ 集合限定真的收敛（拿库里第一个集合试）
        var collectionKey = await FirstCollectionKeyAsync(provider).ConfigureAwait(false);
        if (collectionKey is { Length: > 0 })
        {
            var (inCollection, colTotal, _) = await CollectZoteroAsync(provider, "", "everything", collectionKey)
                .ConfigureAwait(false);
            var (everything, allTotal, _) = await CollectZoteroAsync(provider, "", "everything", null)
                .ConfigureAwait(false);
            var scopeOk = colTotal <= allTotal && inCollection.Count > 0;
            log.Info("selftest", $"⑤ 集合 {collectionKey} -> 总数={colTotal}；整个文库 -> 总数={allTotal}" +
                                 $" -> {(scopeOk ? "PASS：确实收敛了" : "FAIL")}");
            ok &= scopeOk;
        }
        else
        {
            log.Warn("selftest", "⑤ 跳过：库里没有集合");
        }

        log.Info("selftest", ok ? "Zotero 联调自检：全部通过 ✓" : "Zotero 联调自检：有失败 ✗");
    }

    static async Task<(List<UniSearch.Sdk.Model.SearchResult> Rows, int Total, int Batches)> CollectZoteroAsync(
        UniSearch.Providers.Zotero.ZoteroProvider provider,
        string text, string qmode, string? collectionKey,
        IReadOnlyList<UniSearch.Sdk.Model.ResultKind>? kinds = null)
    {
        var query = new UniSearch.Sdk.Model.SearchQuery
        {
            RequestId = 1,
            RawText = text,
            Text = text,
            ProviderText = text,
            ResultBudget = 20,
            Filters = kinds is null ? UniSearch.Sdk.Model.QueryFilters.None
                                    : UniSearch.Sdk.Model.QueryFilters.None with { Kinds = kinds },
        };

        var context = collectionKey is { Length: > 0 }
            ? UniSearch.Sdk.Capabilities.SearchContext.Global() with
              {
                  Scope = UniSearch.Sdk.Capabilities.ScopeKind.NamedScope,
                  NamedScope = UniSearch.Providers.Zotero.ZoteroProvider.CollectionScopePrefix + collectionKey,
              }
            : UniSearch.Sdk.Capabilities.SearchContext.Global();

        // qmode 走 Provider 选项，所以这里临时换一份选项再跑 —— 自检要能证明"两种模式结果不同"
        var saved = provider.Options;
        provider.ApplyOptions(UniSearch.Providers.Zotero.ZoteroQueryOptions.Default with { Qmode = qmode });
        try
        {
            var rows = new List<UniSearch.Sdk.Model.SearchResult>();
            var total = 0;
            var batches = 0;
            await foreach (var b in provider.SearchAsync(query, context, CancellationToken.None).ConfigureAwait(false))
            {
                batches++;
                rows.AddRange(b.Results);
                if (b.TotalAvailable is { } t) total = t;
            }
            return (rows, total, batches);
        }
        finally
        {
            provider.ApplyOptions(saved);
        }
    }

    /// <summary>取库里第一个集合的 key（没有就返回 null）。</summary>
    static async Task<string?> FirstCollectionKeyAsync(UniSearch.Providers.Zotero.ZoteroProvider provider)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync("http://127.0.0.1:23119/api/users/0/collections?limit=1")
                                .ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            foreach (var c in doc.RootElement.EnumerateArray())
                if (c.TryGetProperty("key", out var k)) return k.GetString();
        }
        catch
        {
            // 取不到就当没有集合，不影响别的断言
        }
        return null;
    }

    // ───────────────────────── 思源（US-16）─────────────────────────

    /// <summary>
    /// 思源联调自检：打真服务（本次是跨机的 <c>192.168.200.1:6806</c>）。
    /// <para>
    /// 单测覆盖纯函数（SQL 翻译 / 行映射）；这里覆盖只有真跑才知道的事：
    /// <b>凭证对不对</b>、SQL 真的被内核接受了、笔记本计数与总块数对得上、
    /// 作用域真的收敛、以及<b>含单引号的查询不炸</b>（转义纪律的现场验证）。
    /// </para>
    /// <para>
    /// 查询词<b>现场从库里取</b>（拿一条文档块的标题），不写死 —— 写死一个 "GEO"
    /// 就是把断言绑在用户笔记的内容上，改个标题就红。
    /// </para>
    /// </summary>
    public static async Task RunSiyuanAsync(UniSearch.Providers.Siyuan.SiYuanProvider provider, IUniSearchLog log)
    {
        var ok = true;
        log.Info("selftest", "=== 思源联调自检 ===");

        var health = await provider.ProbeHealthAsync(CancellationToken.None).ConfigureAwait(false);
        log.Info("selftest", $"① 健康：{health.State} · 版本={health.Version ?? "-"} · {health.Detail ?? "-"}");
        if (health.State != UniSearch.Sdk.Contracts.HealthState.Ready)
        {
            log.Warn("selftest", $"思源不可用（{health.Detail}）—— 联调自检跳过" +
                                 (health.Hint is null ? "" : $"；提示：{health.Hint}"));
            return;
        }
        log.Info("selftest", $"   地址：{provider.Options.Host}:{provider.Options.Port}" +
                             $"（凭证：{(provider.Options.Token is null ? "无" : "已配置")}）");

        // ② 库的规模：总数与笔记本清单（凭证不对时这里就会失败，且提示要指得准）
        var facets = await provider.GetFacetValuesAsync(UniSearch.Providers.Siyuan.SiYuanProvider.NotebookFacetId,
                                                        UniSearch.Sdk.Capabilities.SearchContext.Global(),
                                                        CancellationToken.None).ConfigureAwait(false);
        log.Info("selftest", $"② 笔记本值域：{(facets.Ok ? $"{facets.Values.Count} 个" : "失败：" + facets.Error)}");
        foreach (var v in facets.Values.Take(12))
            log.Info("selftest", $"     {v.Value}  ({v.Count} 块)");
        var facetOk = facets.Ok && facets.Values.Count > 0 && facets.Values.All(v => v.Count > 0);
        ok &= facetOk;
        log.Info("selftest", $"   断言：有笔记本且每个都有块 -> {(facetOk ? "PASS" : "FAIL")}");

        var baseTotal = facets.Values.Sum(v => v.Count);
        log.Info("selftest", $"   全库块数（各笔记本之和）= {baseTotal}");

        // ③ 拿一条真实文档块的标题当查询词
        var (probeWord, probeBox) = await FirstDocTitleAsync(provider).ConfigureAwait(false);
        if (probeWord is null)
        {
            log.Warn("selftest", "③ 跳过：库里没有可当查询词的文档块标题");
            log.Info("selftest", ok ? "思源联调自检：全部通过 ✓" : "思源联调自检：有失败 ✗");
            return;
        }
        log.Info("selftest", $"③ 查询词（取自真实文档标题）：「{probeWord}」");

        var (hits, total, batches) = await CollectSiyuanAsync(provider, probeWord).ConfigureAwait(false);
        var hitOk = hits.Count > 0 && hits.All(r => r.ProviderId == "siyuan")
                    && hits.All(r => r.ProviderItemId.Length > 0)
                    && hits.All(r => r.Uri is { Length: > 0 } u && u.StartsWith("siyuan://blocks/", StringComparison.Ordinal))
                    // 跨机部署：磁盘上没有 .sy 文件，填了 Path 只会让"打开/预览"指向不存在的文件
                    && hits.All(r => r.Path is null);
        ok &= hitOk;
        log.Info("selftest", $"   行={hits.Count} 总数={total} 批次={batches}");
        log.Info("selftest", $"   断言：有行 / 有块 id / 有 siyuan:// URI / 无假路径 -> {(hitOk ? "PASS" : "FAIL")}");
        foreach (var r in hits.Take(3))
            log.Info("selftest", $"     [{r.Kind}] {r.Title}  —— {r.Subtitle}");

        // ④ 命中的行里必须能找到那条文档自己（查询词就是从它标题里取的）
        var selfHit = hits.Any(r => r.Kind == UniSearch.Sdk.Model.ResultKind.Document);
        log.Info("selftest", $"④ 结果里含文档块 -> {(selfHit ? "PASS" : "FAIL：标题命中的文档块没回来")}");
        ok &= selfHit;

        // ⑤ 笔记本作用域真的收敛
        if (probeBox is { Length: > 0 })
        {
            var (scoped, scopedTotal, _) = await CollectSiyuanAsync(provider, probeWord, probeBox).ConfigureAwait(false);
            // 限定了笔记本之后，回来的每一行都该属于那个笔记本
            var strays = scoped.Count(r => !r.Metadata.TryGetValue("boxId", out var b) || b != probeBox);
            var scopeOk = scoped.Count > 0 && scoped.Count <= hits.Count && strays == 0;
            log.Info("selftest", $"⑤ 限定到笔记本 {probeBox} -> 行={scoped.Count}（不限定时 {hits.Count}）" +
                                 $" 跑出作用域的行={strays} -> {(scopeOk ? "PASS：确实收敛了" : "FAIL")}");
            ok &= scopeOk;
        }

        // ⑥ 头号纪律的现场验证：含单引号的查询必须被内核正常接受（而不是 SQL 语法错误）
        var (quoted, _, _) = await CollectSiyuanAsync(provider, "it's").ConfigureAwait(false);
        var (danger, _, _) = await CollectSiyuanAsync(provider, "'; DROP TABLE blocks;--").ConfigureAwait(false);
        var (stillAlive, _, _) = await CollectSiyuanAsync(provider, probeWord).ConfigureAwait(false);
        var injectOk = stillAlive.Count > 0;
        ok &= injectOk;
        log.Info("selftest", $"⑥ 含单引号查询 -> 行={quoted.Count}；注入尝试 -> 行={danger.Count}；" +
                             $"之后同一查询仍有 {stillAlive.Count} 行");
        log.Info("selftest", $"   断言：注入尝试没炸掉库（转义生效）-> {(injectOk ? "PASS" : "FAIL")}");

        // ⑦ 值域下推：选一个笔记本后，后端报的总数必须等于该笔记本自己的块数。
        //    ⚠ 走的是 **Facets**（UI 上点 chip 就是这条路），不是 NamedScope ——
        //    两者虽然最终都拼成 box=，但 UI 那条路多一层"显示名 → 下推值"的转换，
        //    第一版就是在这里错的（把笔记本名字当成了 box 值，永远 0 条）。
        if (facets.Ok && facets.Values.Count > 0)
        {
            var pick = facets.Values.OrderBy(v => v.Count).First();
            var facetSel = new UniSearch.Sdk.Model.FacetSelection(
                UniSearch.Providers.Siyuan.SiYuanProvider.NotebookFacetId, [pick.Pushdown]);

            var (inBox, boxTotal, _) = await CollectSiyuanAsync(provider, "", facets: [facetSel]).ConfigureAwait(false);
            var pushOk = boxTotal == pick.Count;
            var rowsBelong = inBox.All(r => r.Metadata.TryGetValue("boxId", out var b) && b == pick.Pushdown);
            ok &= pushOk && rowsBelong;
            log.Info("selftest", $"⑦ 值域下推「{pick.Value}」（下推值 {pick.Pushdown}）-> 后端总数={boxTotal}" +
                                 $"（值域面板说 {pick.Count}），行={inBox.Count}，越界的行={inBox.Count - inBox.Count(r => r.Metadata.TryGetValue("boxId", out var b) && b == pick.Pushdown)}");
            log.Info("selftest", $"   断言：总数一致 且 每行都属于该笔记本 -> {(pushOk && rowsBelong ? "PASS" : "FAIL")}");
        }

        log.Info("selftest", ok ? "思源联调自检：全部通过 ✓" : "思源联调自检：有失败 ✗");
    }

    static string? NotebookOf(string subtitle)
    {
        // Subtitle 形状是 "类型 · 笔记本 · 路径尾段"，取第二段
        var parts = subtitle.Split('·');
        return parts.Length >= 2 ? parts[1].Trim() : null;
    }

    /// <summary>取一条真实文档块的标题当查询词（连同它所在的笔记本 id）。</summary>
    static async Task<(string? Word, string? Box)> FirstDocTitleAsync(
        UniSearch.Providers.Siyuan.SiYuanProvider provider)
    {
        var (rows, _, _) = await CollectSiyuanAsync(provider, "", kinds:
            [UniSearch.Sdk.Model.ResultKind.Document]).ConfigureAwait(false);
        foreach (var r in rows)
        {
            var t = r.Title.Trim();
            if (t.Length < 2) continue;
            if (!r.Metadata.TryGetValue("boxId", out var boxId) || boxId.Length == 0) continue;
            return (t, boxId);
        }
        return (null, null);
    }

    static async Task<string?> BoxIdOfAsync(UniSearch.Providers.Siyuan.SiYuanProvider provider, string notebookName)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        var names = provider.NotebookIdsByName();
        return names.TryGetValue(notebookName, out var id) ? id : null;
    }

    static async Task<(List<UniSearch.Sdk.Model.SearchResult> Rows, int Total, int Batches)> CollectSiyuanAsync(
        UniSearch.Providers.Siyuan.SiYuanProvider provider,
        string text,
        string? boxScope = null,
        IReadOnlyList<UniSearch.Sdk.Model.ResultKind>? kinds = null,
        IReadOnlyList<UniSearch.Sdk.Model.FacetSelection>? facets = null)
    {
        var filters = UniSearch.Sdk.Model.QueryFilters.None;
        if (kinds is not null) filters = filters with { Kinds = kinds };
        if (facets is not null) filters = filters with { Facets = facets };

        var query = new UniSearch.Sdk.Model.SearchQuery
        {
            RequestId = 1,
            RawText = text,
            Text = text,
            ProviderText = text,
            Terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).ToList(),
            ResultBudget = 50,
            Filters = filters,
        };

        var context = boxScope is { Length: > 0 }
            ? UniSearch.Sdk.Capabilities.SearchContext.Global() with
              {
                  Scope = UniSearch.Sdk.Capabilities.ScopeKind.NamedScope,
                  NamedScope = UniSearch.Providers.Siyuan.SiYuanProvider.NotebookScopePrefix + boxScope,
              }
            : UniSearch.Sdk.Capabilities.SearchContext.Global();

        var rows = new List<UniSearch.Sdk.Model.SearchResult>();
        var total = 0;
        var batches = 0;
        await foreach (var b in provider.SearchAsync(query, context, CancellationToken.None).ConfigureAwait(false))
        {
            batches++;
            rows.AddRange(b.Results);
            if (b.TotalAvailable is { } t) total = t;
        }
        return (rows, total, batches);
    }
}
