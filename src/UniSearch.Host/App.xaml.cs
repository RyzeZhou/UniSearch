using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using UniSearch.Core.Broker;
using UniSearch.Core.Filters;
using UniSearch.Core.Selection;
using UniSearch.Host.Services;
using UniSearch.Host.Settings;
using UniSearch.Host.ViewModels;
using UniSearch.Host.Views;
using UniSearch.Providers.Everything;
using UniSearch.Sdk.Capabilities;
using UniSearch.Sdk.Runtime;

namespace UniSearch.Host;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    ProviderRuntime? _runtime;
    IconLoader? _icons;
    ShellContextMenu? _shellMenu;
    GlobalHotkeys? _hotkeys;
    TrayPresence? _tray;
    SettingsStore? _settingsStore;
    SearchBroker? _broker;
    SearchSessionViewModel? _vm;
    FilterCatalog? _filterCatalog;
    MainWindow? _win;
    EverythingProvider? _everything;
    List<ProviderEntry> _entries = [];

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniSearch");
        var log = new HostLog(Path.Combine(dataDir, "host.log"));

        // ── 单实例守门（必须在注册热键/建托盘之前）──────────────
        // 第二个实例的全局热键注册必然失败、托盘会多一个图标、两份搜索状态各自为政，
        // 用户看到的是"热键时灵时不灵"。这里让第二个实例只做一件事：通知已有窗口唤出，然后退出。
        if (!SingleInstance.Initialize())
        {
            log.Info("host", "已有 UniSearch 在运行 —— 已通知它唤出窗口，本进程退出（单实例）");
            SingleInstance.BroadcastSummon();
            Shutdown();
            return;
        }

        // 设置必须在一切之前载入：热键、每组上限、预览默认值、Provider 启停都由它决定
        _settingsStore = new SettingsStore(dataDir, log);
        var settings = _settingsStore.Current;

        _runtime = new ProviderRuntime(log, new WindowsProcessLauncher(log), _settingsStore);

        // ── 装配 Provider ---------------
        // M0 只有 Everything；M3 起从 plugins 目录扫描 [UniSearchProvider] 的 dll。
        _everything = new EverythingProvider();
        _entries =
        [
            new(_everything, _everything.Descriptor)
            {
                Enabled = _settingsStore.IsProviderEnabled(EverythingProvider.ProviderId),
            },
        ];

        foreach (var entry in _entries)
            // 每个 Provider 拿到绑定了自己 id 的运行时视图，Settings 才是它那一节
            await entry.Provider.InitializeAsync(_runtime.ForProvider(entry.Descriptor.Id), CancellationToken.None);

        ApplyProviderOptions(settings);

        _broker = new SearchBroker(_entries, options: BuildBrokerOptions(settings));

        // Shell 图标缓存必须在 UI 线程建（BitmapSource 亲和 Dispatcher），OnStartup 正好满足。
        // 解析默认单线程：Shell 调用已在 ShellIconCache 内串行化（SHGetFileInfo 并发会随机失败），
        // 多开线程只会互相阻塞。环境变量保留给排查用。
        var iconWorkers = int.TryParse(Environment.GetEnvironmentVariable("UNISEARCH_ICON_WORKERS"), out var iw)
            ? Math.Clamp(iw, 1, 8) : 1;
        _icons = new IconLoader(new ShellIconCache(), Dispatcher, iconWorkers);
        ViewModels.SnapshotMapper.Icons = _icons;

        var vm = _vm = new SearchSessionViewModel(_broker, launcher: _runtime.Process, log: log)
        {
            // --query "xxx" 或环境变量 UNISEARCH_QUERY：预填搜索框。
            // 自动化验证靠它（合成键盘输入被 UIPI 拦掉），也方便从命令行直接拉起一次搜索。
            Input = ReadInitialQuery(e.Args),
            Preview = new PreviewService(),
            ShellMenu = _shellMenu = new ShellContextMenu(),
            IsPreviewOpen = settings.Preview.OpenByDefault,
            MaxRows = settings.Search.MaxRows,
        };
        // 列布局（列宽/顺序/可见列/排序）来自设置；默认来源集合决定"输入时自动搜哪些后端"
        vm.ApplyLayout(settings.Columns);
        vm.SetAutoSearchProviders(settings.Search.AutoSearchProviders);
        log.Info("ui", $"列布局已应用：可见列=[{string.Join(",", vm.Columns.Where(c => c.IsVisible).Select(c => c.Key))}] " +
                       $"排序={vm.SortKey}{(vm.SortDescending ? "↓" : "↑")} 行上限={vm.MaxRows} " +
                       $"默认来源=[{string.Join(",", settings.Search.AutoSearchProviders)}]");

        // 筛选器定义：程序自带的模板（dist\filters.json）+ 用户自己的（%LOCALAPPDATA%\UniSearch\filters.json）。
        // 后者覆盖同 id 的前者 —— 用户改模板文件会被下次更新覆盖，这一点必须在文档里说清楚。
        _filterCatalog = FilterCatalog.Load(
            Path.Combine(AppContext.BaseDirectory, "filters.json"),
            Path.Combine(dataDir, "filters.json"));
        vm.SetFilterCatalog(_filterCatalog);
        log.Info("filters", $"筛选器 {_filterCatalog.All.Count} 个（" +
                            $"来源=[{string.Join(", ", _filterCatalog.Sources.Select(Path.GetFileName))}]）");
        foreach (var problem in _filterCatalog.Problems)
            log.Warn("filters", problem);

        // 预览链路的诊断接到 host.log：它为了"失败降级不崩"会吞异常，没有这条通道就查不出原因
        PreviewService.Trace = m => log.Info("preview", m);
        ShellContextMenu.Trace = m => log.Info("shellmenu", m);

        // 全局热键（要等 vm 与窗口就绪，SetContext 才有落点）：
        // 唤出键 + 目录限定键都来自设置。窗口在下面才建，这里先用闭包持有它 ——
        // 热键回调只在真正按键时才跑，那时 win 必定已就绪。
        // showWith 必须**同时把窗口唤到前台**：只 SetContext 不 Show 的话，按热键只是后台
        // 重跑了一次搜索，窗口还藏在别的程序后面，用户看到的现象就是"热键没反应"。
        MainWindow? win = null;
        _hotkeys = new GlobalHotkeys(
            globalContextFactory: () => SearchContext.Global(),
            showWith: ctx => { win?.Summon(); vm.SetContext(ctx); },
            trace: m => log.Info("hotkey", m));
        _hotkeys.Apply(settings.Hotkeys);
        LogHotkeys(log);

        // 设置一保存就即时生效（不用重启）：这是"有设置界面"和"设置文件只是摆设"的分界线
        _settingsStore.Changed += ApplySettings;

        // ── 装配 DI（轻量：只有一个 VM 与 broker 需要跨窗口共享）----
        var sc = new ServiceCollection();
        sc.AddSingleton(vm);
        sc.AddSingleton(_broker);
        Services = sc.BuildServiceProvider();

        // --compact-source / --no-preview：把三栏切到"缩回去"的形态再渲染。
        // 两种形态都要有截图证据 —— 折叠按钮点一下对不对，靠看而不是靠猜。
        if (e.Args.Contains("--compact-source", StringComparer.OrdinalIgnoreCase)) vm.IsSourceBarCompact = true;
        if (e.Args.Contains("--no-preview", StringComparer.OrdinalIgnoreCase)) vm.IsPreviewOpen = false;

        win = _win = new MainWindow
        {
            DataContext = vm,
            CloseToTray = settings.Window.CloseToTray,
            NotifyOnFirstHide = settings.Window.NotifyOnFirstHide,
            OpenSettings = ShowSettingsWindow,
            // 列宽/排序这类纯 UI 状态：落盘但**不触发 Changed** ——
            // 拖一下列宽就重跑一次查询显然不对（见 SettingsStore.Save 的 notify 参数）
            PersistLayout = layout =>
            {
                if (_settingsStore is null) return;
                var s = _settingsStore.Current;
                s.Columns = layout;
                _settingsStore.Save(s, notify: false);
            },
        };
        MainWindow = win;
        win.Show();

        // ── 托盘常驻 ────────────────────────────────
        // 窗口按 X 只是收进后台（热键还要能唤回），进程会一直活着 ——
        // 所以必须有一个看得见、点得到的退出入口，否则用户只能去任务管理器杀进程。
        // 顺序要在 win.Show() 之后：托盘菜单里的动作都会操作这个窗口。
        _tray = TrayPresence.Create(
            summon: win.Summon,
            hide: win.Hide,
            settings: win.ShowSettingsDialog,
            quit: Quit,
            log: log);
        log.Info("tray", _tray.Ok ? "托盘图标已就绪（右键 = 显示/隐藏/设置/退出）" : $"托盘创建失败：{_tray.Error}");
        UniSearch.Host.Services.AutoStart.Apply(settings.Window.AutoStartAtLogin, log);

        // 第一次"关闭即隐藏"时提示一句：托盘图标可能被折进溢出区，不提示用户会以为程序没退
        win.FirstHideNotice = () => _tray?.NotifyHiddenToTray();
        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("host", "未处理异常", args.Exception);
            args.Handled = true;
        };

        // --selftest-hotkey：核对热键注册 + Explorer 目录探测。
        if (e.Args.Contains("--selftest-hotkey", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    if (_hotkeys is not null)
                        UiSelfTest.RunHotkeys(_hotkeys,
                            () => ExplorerLocator.GetCurrentDirectory(), log);
                }
                catch (Exception ex) { log.Error("selftest", "热键自检失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --selftest-settings：转储设置并做一次真实的落盘往返（含带空格路径）。
        if (e.Args.Contains("--selftest-settings", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    if (_settingsStore is not null) UiSelfTest.RunSettings(_settingsStore, log);
                    LogHotkeys(log);
                }
                catch (Exception ex) { log.Error("selftest", "设置自检失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --selftest-layout：验证来源限定、列排序、列装配、列布局落盘（三栏改造新增的链路）。
        // 配合 --query 用：没有结果就没有可排序的行。
        if (e.Args.Contains("--selftest-layout", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try
                {
                    if (_settingsStore is not null) await UiSelfTest.RunLayoutAsync(win, vm, _settingsStore, log);
                }
                catch (Exception ex) { log.Error("selftest", "布局自检失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --selftest-multiselect：多选模型（计数 / 主选中项 / 汇总 / 状态条 / 路径收集）。
        // 真实 Ctrl+点击 要人手（合成鼠标被 UIPI 拦），这里验的是选择模型本身。配合 --query 用。
        if (e.Args.Contains("--selftest-multiselect", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { UiSelfTest.RunMultiSelect(vm, log); }
                catch (Exception ex) { log.Error("selftest", "多选自检失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --selftest-archive：压缩自检（造小树 → 真压 → 读回核对条目名与内容）。不需要 --query。
        if (e.Args.Contains("--selftest-archive", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { UiSelfTest.RunArchive(log); }
                catch (Exception ex) { log.Error("selftest", "压缩自检失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --selftest-filters：核对 filters.json 的加载与问题，以及"筛选器 → 查询串"的翻译。
        if (e.Args.Contains("--selftest-filters", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try { await UiSelfTest.RunFiltersAsync(vm, log); }
                catch (Exception ex) { log.Error("selftest", "筛选器自检失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --selftest-keys：直接调快捷键分发函数，核对 Ctrl+J / Ctrl+1..9 / Ctrl+0 真的接上了。
        // 配合 --query 用（没有结果就没有分类标签，测不出东西）。
        if (e.Args.Contains("--selftest-keys", StringComparer.OrdinalIgnoreCase))        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { UiSelfTest.RunKeys(win, vm, log); }
                catch (Exception ex) { log.Error("selftest", "快捷键自检失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --selftest-tray [菜单标题]：核对托盘图标与菜单；给了标题就模拟点那一下
        // （用「退出 UniSearch」可以把"右键托盘 → 退出"整条链路验证到底）。
        if (e.Args.Contains("--selftest-tray", StringComparer.OrdinalIgnoreCase))
        {
            var header = ReadOptionValue(e.Args, "--selftest-tray");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    if (_tray is not null) UiSelfTest.RunTray(_tray, log, string.IsNullOrEmpty(header) ? null : header);
                }
                catch (Exception ex) { log.Error("selftest", "托盘自检失败", ex); }
                Quit();   // 若上面点的是「退出」项，进程此时已经退出了，这里不会跑到
            };
            timer.Start();
            return;
        }

        // --selftest-shellinvoke：按动词名直接调用，验证偏移调用真的能执行 shell 命令。
        // 退出不能太早：属性对话框由本进程托管，进程一走窗口就没了（踩过一次）。
        if (e.Args.Contains("--selftest-shellinvoke", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { if (_shellMenu is not null) UiSelfTest.RunShellInvoke(_shellMenu, vm, log); }
                catch (Exception ex) { log.Error("selftest", "shell 调用自检失败", ex); }
            };
            timer.Start();

            var bye = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12000) };
            bye.Tick += (_, _) => { bye.Stop(); Quit(); };
            bye.Start();
            return;
        }

        // --selftest-shellmenu：验证真 shell 菜单的 COM 管道能拿到系统动词表。
        if (e.Args.Contains("--selftest-shellmenu", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { if (_shellMenu is not null) UiSelfTest.RunShellMenu(vm, _shellMenu, log); }
                catch (Exception ex) { log.Error("selftest", "shell 菜单自检失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --selftest-preview：按类型各挑一行验证预览窗格真的取到内容。
        if (e.Args.Contains("--selftest-preview", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try { await UiSelfTest.RunPreviewAsync(vm, log); }
                catch (Exception ex) { log.Error("selftest", "预览自检失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --selftest-icons：核对后台图标加载确实把图标刷回了行上（连读两次，区分"还没解析完"和"卡住"）。
        if (e.Args.Contains("--selftest-icons", StringComparer.OrdinalIgnoreCase))
        {
            var round = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (_icons is not null) UiSelfTest.RunIcons(vm, _icons, log, ++round);
                }
                catch (Exception ex) { log.Error("selftest", "图标自检失败", ex); }

                if (round >= 3) { timer.Stop(); Quit(); }
            };
            timer.Start();
            return;
        }

        // --selftest-backend：触发一次「直达后端程序」，验证命令确实把查询送进了后端。
        if (e.Args.Contains("--selftest-backend", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { UiSelfTest.RunBackendJump(vm, log); }
                catch (Exception ex) { log.Error("selftest", "后端跳转自检失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --selftest-props：对文件夹与文件各开一次"属性"，验证 ShellExecuteEx 那条路径。
        // 注意退出不能太早：属性对话框由本进程托管，进程一走窗口就没了，外部脚本也就观察不到。
        if (e.Args.Contains("--selftest-props", StringComparer.OrdinalIgnoreCase))
        {
            var act = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            act.Tick += (_, _) =>
            {
                act.Stop();
                try { UiSelfTest.RunProperties(vm, log); }
                catch (Exception ex) { log.Error("selftest", "属性自检失败", ex); }
            };
            act.Start();

            var bye = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12000) };
            bye.Tick += (_, _) => { bye.Stop(); Quit(); };
            bye.Start();
            return;
        }

        // --selftest-open：真正执行一次"打开目录"，验证 选中 → 动作 → Shell 调用链。
        if (e.Args.Contains("--selftest-open", StringComparer.OrdinalIgnoreCase))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { UiSelfTest.RunOpen(vm, log); }
                catch (Exception ex) { log.Error("selftest", "执行动作失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --selftest-menu [目录]：程序化打开结果行的右键菜单，逐项报告绑定情况后退出。
        if (e.Args.Contains("--selftest-menu", StringComparer.OrdinalIgnoreCase))
        {
            var dir = ReadOptionValue(e.Args, "--selftest-menu");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { UiSelfTest.RunMenu(win, vm, log, string.IsNullOrEmpty(dir) ? null : dir); }
                catch (Exception ex) { log.Error("selftest", "自检失败", ex); }
                Quit();
            };
            timer.Start();
            return;
        }

        // --dump-settings <png>：把设置窗口离屏渲染出来（含滚动区域里"看不见的下半截"）。
        // 存在的理由：这台机器 175% 缩放，长窗口用 CopyFromScreen 会被窗口管理器截到屏幕高度，
        // 上一轮就因为拍不到而缺了后三节的截图证据；离屏渲染走视觉树，不受屏幕尺寸限制。
        var dumpSettingsTo = ReadOptionValue(e.Args, "--dump-settings");
        if (!string.IsNullOrEmpty(dumpSettingsTo) && _settingsStore is not null)
        {
            var dlg = new Views.SettingsWindow(
                _settingsStore.Current,
                _ => "（自检：未保存）",
                System.IO.Path.GetDirectoryName(_settingsStore.FilePath)!,
                filtersSummary: BuildFiltersSummary(),
                userFiltersPath: Path.Combine(Path.GetDirectoryName(_settingsStore.FilePath)!, "filters.json"),
                templateFiltersPath: Path.Combine(AppContext.BaseDirectory, "filters.json"));
            // 拉高到比屏幕还高：离屏渲染不经过窗口管理器，这样"下半截"能一次性拍全，
            // 不必再靠合成滚轮去滚 ScrollViewer（上一轮试过，WPF 不理会合成滚轮）。
            dlg.Height = 1180;
            dlg.Show();
            var settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            settingsTimer.Tick += (_, _) =>
            {
                settingsTimer.Stop();
                try
                {
                    // 渲染滚动区内容（长图）：设置页比屏幕高，拍窗口只会得到被截断的上半截
                    if (!RenderDump.RunVisual(dlg.DumpContent, dumpSettingsTo!))
                        RenderDump.Run(dlg, dumpSettingsTo!);
                    log.Info("host", $"设置窗口离屏渲染完成 -> {dumpSettingsTo}");
                }
                catch (Exception ex) { log.Error("host", "设置窗口渲染失败", ex); }
                dlg.Close();
                Quit();
            };
            settingsTimer.Start();
            return;
        }

        // --dump-render <png>：等搜索跑完 + 图标刷回行上再离屏渲染窗口，供脚本断言画面。
        // 以前固定等 2.5s —— Everything 冷启动握手就要 ~2.5-3s，经常拍成"搜索中…"（2026-09-20 实测）。
        // 现在轮询 IsBusy：空闲后再多等一拍让后台 shell 图标落到行上，12s 兜底强拍。
        var dumpTo = ReadOptionValue(e.Args, "--dump-render");
        if (!string.IsNullOrEmpty(dumpTo))
        {
            var started = DateTime.UtcNow;
            var settle = TimeSpan.Zero;
            var tick = TimeSpan.FromMilliseconds(200);
            var timer = new DispatcherTimer { Interval = tick };
            timer.Tick += (_, _) =>
            {
                var elapsed = DateTime.UtcNow - started;
                var giveUp = elapsed >= TimeSpan.FromSeconds(12);   // 后端卡死也要出图，别把自动化拖住
                if (!giveUp)
                {
                    if (elapsed < TimeSpan.FromMilliseconds(600)) return;  // 先让布局与首帧就位
                    if (_vm.IsBusy) { settle = TimeSpan.Zero; return; }    // 还在搜，继续等
                    settle += tick;                                        // 搜完再多等一拍（图标）
                    if (settle < TimeSpan.FromMilliseconds(800)) return;
                }
                timer.Stop();
                try { RenderDump.Run(win, dumpTo!); log.Info("host", $"离屏渲染完成 -> {dumpTo}"); }
                catch (Exception ex) { log.Error("host", "离屏渲染失败", ex); }
                Quit();
            };
            timer.Start();
        }
    }

    /// <summary>
    /// 进程是否正在真正退出。窗口的 <c>Closing</c> 靠它区分两件事：
    /// <b>用户按 X / Esc = 收进后台（热键还能唤回）</b> vs <b>程序要退出</b>。
    /// </summary>
    public static bool IsQuitting { get; private set; }

    // ─────────────── 设置 ───────────────

    /// <summary>设置 → Broker 阈值。设置里一改就换一个新实例，正在跑的请求下一批就用上新值。</summary>
    static BrokerOptions BuildBrokerOptions(UniSearchSettings s) => new()
    {
        // 平铺单表之后"每组上限"没有组可言了：两个值都放到列表总上限。
        // 否则每一类各自被砍到 24 条，用户看到的是"明明有 102 个 pdf 却只显示 24 个"。
        PerCategoryLimit = s.Search.MaxRows,
        PerCategoryLimitInFocus = s.Search.MaxRows,
        FilterHiddenAndSystemByDefault = s.Search.FilterHiddenAndSystem,
    };

    /// <summary>
    /// 用户看到的是"排除系统噪声"这种人话开关，Provider 要的是路径列表 —— 这层映射只放在这里一处。
    /// </summary>
    void ApplyProviderOptions(UniSearchSettings s)
        => _everything?.ApplyOptions(new EverythingQueryOptions
        {
            ExcludeNoisePaths = s.Search.ExcludeNoisePaths,
            ExtraExcludePaths = s.Search.ExtraExcludePaths,
        });

    /// <summary>
    /// 把设置推到各个部件上。<b>设置一保存就跑这里</b>，所以改了键/上限不用重启程序。
    /// </summary>
    void ApplySettings(UniSearchSettings s)
    {
        if (_broker is not null) _broker.Options = BuildBrokerOptions(s);

        ApplyProviderOptions(s);

        foreach (var entry in _entries)
            entry.Enabled = _settingsStore?.IsProviderEnabled(entry.Descriptor.Id) ?? entry.Enabled;

        _hotkeys?.Apply(s.Hotkeys);
        LogHotkeys(_runtime?.Log);

        if (_vm is not null)
        {
            _vm.IsPreviewOpen = s.Preview.OpenByDefault;
            _vm.MaxRows = s.Search.MaxRows;
            _vm.SetAutoSearchProviders(s.Search.AutoSearchProviders);
            _vm.ApplyLayout(s.Columns);   // 设置里改了列 → 窗口立刻重建列
            // 上限/排除项/来源都作用于查询，得重跑一次才看得见效果
            _ = _vm.RunAsync();
        }

        if (_win is not null)
        {
            _win.CloseToTray = s.Window.CloseToTray;
            _win.NotifyOnFirstHide = s.Window.NotifyOnFirstHide;
        }

        UniSearch.Host.Services.AutoStart.Apply(s.Window.AutoStartAtLogin, _runtime?.Log);
    }

    void LogHotkeys(IUniSearchLog? log)
    {
        if (log is null || _hotkeys is null) return;
        if (_hotkeys.Registered.Count == 0)
        {
            log.Info("hotkey", "没有注册任何全局热键（已禁用或在设置里留空）");
            return;
        }
        foreach (var (name, gesture, ok) in _hotkeys.Registered)
            log.Info("hotkey", $"{name} = {gesture} -> {(ok ? "已注册" : "被其它程序占用")}");
    }

    /// <summary>保存后回给设置窗口看的一句话（哪些热键真的注册上了，是这里最该说的事）。</summary>
    string BuildApplySummary()
    {
        var s = _settingsStore?.Current;
        if (s is null || _hotkeys is null) return "已保存。";

        if (!s.Hotkeys.Enabled) return "已保存。全局热键当前是关闭状态。";
        if (_hotkeys.Registered.Count == 0) return "已保存，但没有注册任何热键（组合键留空了？）。";

        var parts = _hotkeys.Registered
            .Select(r => $"{r.Gesture} {(r.Ok ? "✓" : "✗ 被其它程序占用")}");
        return "已保存并生效。热键：" + string.Join("，", parts);
    }

    /// <summary>设置窗口「筛选器」那一节的一句话摘要（几个、来自哪、有没有坏定义）。</summary>
    string BuildFiltersSummary()
    {
        if (_filterCatalog is null) return "（筛选器还没加载）";
        if (_filterCatalog.All.Count == 0)
            return "没有读到任何筛选器定义 —— 标签栏只有内置分类。";

        var from = string.Join("、", _filterCatalog.Sources.Select(Path.GetFileName));
        var problems = _filterCatalog.Problems.Count > 0
            ? $"，其中 {_filterCatalog.Problems.Count} 条定义有问题（详见 host.log）"
            : string.Empty;
        return $"{_filterCatalog.All.Count} 个筛选器，来自 {from}{problems}。";
    }

    /// <summary>F12 / 托盘「设置…」都走这里。</summary>
    void ShowSettingsWindow()
    {
        if (_settingsStore is null) return;
        if (_win is null) return;

        Views.SettingsWindow? dlg = null;
        dlg = new Views.SettingsWindow(
            _settingsStore.Current,
            apply: s =>
            {
                // 列布局由主窗口实时维护（拖列宽就落盘），设置窗口这份草稿是打开时的快照 ——
                // 用户没在本窗口里动过列布局时，就用实时值，免得把他刚拖出来的列宽回退掉。
                if (dlg is { ColumnsEdited: false }) s.Columns = _settingsStore.Current.Columns;
                return _settingsStore.Save(s) ? BuildApplySummary() : "保存失败，详见 host.log。";
            },
            settingsDirectory: System.IO.Path.GetDirectoryName(_settingsStore.FilePath)!,
            filtersSummary: BuildFiltersSummary(),
            userFiltersPath: Path.Combine(Path.GetDirectoryName(_settingsStore.FilePath)!, "filters.json"),
            templateFiltersPath: Path.Combine(AppContext.BaseDirectory, "filters.json"))
        {
            Owner = _win,
        };
        dlg.ShowDialog();
    }

    /// <summary>
    /// 真正退出进程。<b>不要在别处直接调 <c>Shutdown()</c></b>：
    /// 窗口的 <c>Closing</c> 会取消关闭请求（关闭即隐藏），不先置这个位就退不掉。
    /// </summary>
    public static void Quit()
    {
        IsQuitting = true;
        Current.Shutdown();
    }

    /// <summary>取 <c>--key value</c> 形式的选项值。</summary>
    static string? ReadOptionValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    /// <summary>--query "xxx" 优先，其次环境变量 UNISEARCH_QUERY。</summary>
    static string ReadInitialQuery(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--query", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return Environment.GetEnvironmentVariable("UNISEARCH_QUERY") ?? string.Empty;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出时同步等待落盘（阻塞在这里没问题：进程正要结束）
        try { _runtime?.FlushAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { /* 忽略 */ }
        _icons?.Dispose();
        _shellMenu?.Dispose();   // 移除消息钩子并销毁隐藏窗口
        _hotkeys?.Dispose();     // 注销全局热键
        _tray?.Dispose();        // 摘掉托盘图标（不摘的话图标会残留在任务栏直到鼠标划过）
        SingleInstance.Release();
        Services?.Dispose();
        base.OnExit(e);
    }
}