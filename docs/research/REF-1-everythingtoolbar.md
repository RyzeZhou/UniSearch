# 参考仓库 1：srwi/EverythingToolbar

- 本地路径 `D:/tools/refs/everythingtoolbar`（浅克隆 develop/master）
- 许可证 **MIT** (Copyright (c) 2023 Stephan Rumswinkel)；14.7k★
- 规模：`EverythingToolbar` UI 143 文件/16805 行、`.App` 17/2323、`.Core` 19/434、`.Platform` 8/1790、`.Deskband` 4/2906、`.Launcher` 10/1947
- 技术栈：**WPF** + `WPF-UI` 4.0.3 + `CommunityToolkit.Mvvm` 8.3.2 + `Config.Net` + `NLog` + `NativeTray` + `NHotkey.Wpf` + `Microsoft.Windows.CsWin32` + `Microsoft.Extensions.DependencyInjection`；TFM `net8.0-windows10.0.17763.0`
- 形态：任务栏原生搜索框(deskband) + 独立搜索窗口 + 全局快捷键 + 设置页 + 更新器 + 托盘

## 0. 本机不可直接引用（重要）

`EverythingToolbar.Platform` 依赖两个 **C++ 工程** `EverythingSDK.vcxproj`、`EverythingSDK3.vcxproj`（生成/链接原生 `everything.dll`、`Everything3.dll`），
本机 `where cl.exe` 无结果 → 无 MSVC，**这两个工程与 Platform 无法构建**。
所以本仓库对 UniSearch 的价值是"读 + 移植"，不是"引用/ fork"。（`EverythingToolbar.Core` 可以单独还原并构建，实测 `dotnet restore` 成功。）

## 1. 分层（值得照抄的工程划分）

```
EverythingToolbar.Core      纯契约：SearchResult / Filter / SortBy / IEverythingClient / IItemsProvider<T>
                            + Platform 抽象接口（IClipboard/IFileLauncher/IFilePreviewer/INotifier/IShellDialogs）
                            —— 无 UI、无 Win32，434 行，是整套代码里最干净的一层
EverythingToolbar.Platform  Everything 访问实现：Ipc(1.4) / Pipe(1.5) / Router
EverythingToolbar.App       编排：SearchSession / SearchState / VirtualizingCollection / Filters / SearchHistory / CustomActionService
EverythingToolbar           WPF 视图 + 服务（窗口、图标、主题、快捷键、预览）
EverythingToolbar.Deskband  任务栏 COM band（CSDeskBand）
EverythingToolbar.Launcher  升权/重启宿主（UAC 场景）
```

## 2. 逐点结论（可复用清单）

| 上游文件 | 内容 | 对 UniSearch 的处置 |
|---|---|---|
| `EverythingSDK/include/Everything_IPC.h`（903 行） | **官方 IPC 头**，全部常量与结构体的权威来源 | ✅ 已提取到 `docs/research/EVERYTHING-IPC-VERIFIED.md`（推翻社区流传的 `EN_*` 命名） |
| `EverythingSDK3/src/Everything3.c`（9633 行，MIT by voidtools） | 1.5 **命名管道**协议实现：`\\.\PIPE\Everything IPC`，报头 `{DWORD code;DWORD size}`，`SEARCH`=7、`GET_RESULTS`=22，长度用 **VLQ**、`SIZE_T` 为指针宽度、文本 UTF-8 | ✅ 已提取到同一文档；阶段二自写 1.5 传输时照此实现 |
| `.Platform/Search/EverythingClientRouter.cs` | 1.4 IPC / 1.5 pipe 选择与回退 | 借鉴思路（先试 1.5 管道，失败回落 1.4） |
| `.Platform/Search/EverythingIpcClient.cs`(645) | 1.4 WM_COPYDATA：message-only 窗口 + QUERY2 + LIST2 解析 | ⚠️ 暂不移植：已被 Apache-2.0 的 `EverythingSearchClient` 包覆盖（见 REF-6） |
| `.Core/Data/SearchResult.cs` | `record(HighlightedPath, HighlightedFileName, FullPathAndFileName, IsFile, FileSize, FILETIME DateModified)` | ❌ **不能当聚合基座**：纯文件模型，无 URI/snippet/元数据/动作，塞不进文献与笔记 → 这正是 UniSearch 自建 `Sdk.Model.SearchResult` 的理由 |
| `.Core/Search/IItemsProvider<T>` | `IsBusy / FetchCount(pageSize,isAsync,ct) / FetchRange(startIndex,pageSize,isAsync,ct) / TryFetchCachedFirstPage` | ✅ **移植成分页契约**：Broker 的"更多结果"按这个形状设计（比 `IAsyncEnumerable` 更省内存，但要另做流式首屏） |
| `.Core/Search/SearchQuery.cs` | `record(SearchText, SortBy, SortDescending, MatchCase, MatchPath, MatchWholeWord, UseRegex)` | 借鉴；UniSearch 的 `SearchQuery` 需要再加 Terms/Filters/Budget/Deadline |
| `.App/Search/VirtualizingCollection.cs` | 分页虚拟化 `IList<T>`：占位符页、按页 `FetchRange`、查询变更时 `CancellationTokenSource` 打断、页驱逐时清占位引用、`Replace` 通知与 `IsBusy` 透传 | ✅ **移植思路**（阶段二做"结果滚动加载"时用；第一版先流式+截断） |
| `.App/Search/SearchResultActions.cs` | Open / RunAsAdmin / OpenParentFolderAndSelect / OpenWith / CopyFileDropList / CopyPath / CopyFileName，每步 `everything.IncrementRunCount(path)` | ✅ **直接作为宿主默认动作表**；注意"每次动作都回写 Everything 的 run count"这个细节（排序反哺） |
| `.Core/Platform/{IFileLauncher,IShellDialogs,IClipboard,IFilePreviewer,INotifier}.cs` | 5 个小接口把 Win32 挡在 UI 外 | ✅ 照抄这套抽象（UniSearch.Sdk 已有 `IProcessLauncher`，补齐其余） |
| `Services/FileLauncherAdapter.cs` | `Process.Start(new ProcessStartInfo(path){UseShellExecute=true})`；升权 `Verb="runas"`，捕获 `OperationCanceledException` 当"用户取消 UAC" | ✅ 移植（含"UAC 取消不算错误"的处理） |
| `Services/ShellDialogsAdapter.cs` | OpenWith / 属性页 / `ShellContextMenu` | 借鉴；**注意 `ThirdParty/ShellContextMenu.cs` 无许可证声明**（`namespace Peter`，CodeProject 系） → 不复制，自写 `IContextMenu` |
| `Services/FilePreviewerAdapter.cs` | 预览交给外部工具：`FindWindowEx(null,null,"SeerWindowClass",null)` + `WM_COPYDATA`；或 QuickLook | ✅ **不要自写预览引擎**：接 QuickLook(GPL-3.0，独立进程不传染) / Seer。UniSearch 只传路径 |
| `Helpers/HighlightedText.cs`(99) | WPF 附加属性 `HighlightedText.Source`：把 Everything 的 `*命中*` 标记切成 `Run` 并加粗；无 `*` 时走 `TextBlock.Text` 快路径（注释称布局开销约省一半）；`TryUpdateRuns` 复用已有 Run 避免重建 | ✅ **移植**（把 `*` 换成本项目统一的 `[[..]]` 记法；快路径与 Run 复用两个优化照搬） |
| `Icons/IconProvider.cs`(283) + `IconLoader.cs` | 两级缓存 + **探测式取索引**：用假文件名 `asdf1234.<ext>` / `asdf1234` 走 `SHGetFileInfo(SHGFI_SYSICONINDEX|SHGFI_SMALLICON[|SHGFI_USEFILEATTRIBUTES])` 得到扩展名→图标索引，再 `SHGetImageList(SHIL_*)` + `IImageList` 按 DPI 取图；缓存键 `index_size`；注释明确警告 `GetExactImage` 会**在网络路径上阻塞，不可在 UI 线程调用** | ✅ **移植**（这套"按扩展名探测而非真实文件"是关键性能点，能避免列表滚动时打网络/磁盘） |
| `Services/ThemeService.cs`(420) | 系统主题/强调色/应用主题、Mica 相关、`WPF-UI` 集成 | 借鉴；UniSearch 直接用 `WPF-UI`（MIT）的 `ApplicationThemeManager` + `ControlsDictionary` |
| `Controls/AcrylicWindow.cs`(276) | `DwmSetWindowAttribute` 实现 Mica/Acrylic 背景 + 圆角 | ✅ 移植（约 60 行 P/Invoke） |
| `Services/SearchWindowController.cs`(249) + `Behaviors/SearchWindowPlacement.cs` + `TaskbarLayoutProbe.cs` / `TaskbarInfoProvider.cs` / `SearchWindowAnimator.cs` | 窗口跟随任务栏位置（顶/底/左/右、单/多屏、DPI）、显隐动画、失焦隐藏 | 借鉴；UniSearch 阶段一要支持"从 Explorer 底部唤出并跟随该窗口"，这套 probe 逻辑可复用思路 |
| `Services/GlobalShortcutListener.cs`(79) | 基于 `NHotkey.Wpf` 注册全局热键 | 借鉴；UniSearch 自写 `RegisterHotKey`+`WM_HOTKEY` 消息循环即可（少一个依赖） |
| `Services/LowLevelKeyboardHook.cs`(141) + `StartMenuSearchInterceptor.cs`(365) | **WH_KEYBOARD_LL 钩子 + `SetWinEventHook` 焦点事件**：检测开始菜单/搜索面板出现（`_searchAppHwnd`），录制按键队列 `_recordedInputs`，判断原生搜索是否激活 `_isNativeSearchActive`，`_isInterceptingKeys` 时把按键吞掉（`return 1`）并改用自己的 UI，另有 1 秒 `DispatcherTimer` 清理与"恢复动画设置" | ✅✅ **阶段三（接管原生搜索框）的最强参考**：证明了"不注入、用低级键盘钩子+焦点事件劫持输入再转发"这条路可行，且给出了防抖/状态机/还原细节 |
| `.App/Services/SearchHistory.cs` | `%LOCALAPPDATA%\EverythingToolbar\history.xml`，`MaxHistorySize=50`，可被设置开关，异步串行保存（`_saveGate` + 单写 Task） | ✅ 移植（UniSearch 的查询历史） |
| `.App/Services/{DefaultFilterProvider,EverythingFilterProvider,FilterProvider}.cs` + `Core/Data/Filter.cs` | 分类=**一个 Everything 查询串/宏**；内置：`file:`、`folder:`、`exe`→`ext:bat;cmd;exe;msi;msp;scr`、`pic`→`ext:ani;bmp;gif;ico;jpe;jpeg;jpg;pcx;png;psd;tga;tif;tiff;webp;wmf`、宏 `doc/audio/video/zip`。`Filter.GetSearchPrefix(...)` 用 `case:/nocase:`、`ww:/noww:`、`path:/nopath:`、`regex:/noregex:`、`diacritics:`、`prefix:`、`suffix:`、`punctuation:`、`whitespace:` **前缀修饰符**来对齐用户当前开关 | ✅ **采用其分类语义**：UniSearch 的分类对 Everything 应翻成 `ext:`/`kind:` 串（见 `EverythingQueryTranslator`），而不是自己按扩展名后过滤 |
| `Controls/SearchResultsView.xaml.cs`(503) | 结果列表交互：鼠标双击/中键、Ctrl/Shift 多选、拖出(DragDrop 到 Explorer)、右键菜单、方向键、Enter/Ctrl+Enter、预览面板联动 | ✅ 阶段一 UI 的行为清单来源（键鼠友好） |
| `Controls/SearchBox.xaml.cs`(249) + `SearchButton.xaml.cs` + `FilterSelector.xaml.cs` | 搜索框：文本+图标+清除+筛选器下拉，`WPF-UI` 样式 | 借鉴 |
| `.App/Search/SearchState.cs` / `EverythingSearchLauncher.cs` / `EverythingItemsProvider.cs` | 一次会话的状态机（当前查询、筛选器、排序、页数、焦点项） | 借鉴；UniSearch 对应 `SearchSession` + Broker |
| `.App/Data/Rule.cs` + `SettingsProxy.cs` + `ToolbarSettings.cs` + `Helpers/ConfigPaths.cs` | `Config.Net`（ini）设置层 | ❌ 不移植：UniSearch 用 JSON 配置 |
| `.Deskband/*`(2906，含 CSDeskBand MIT) | COM deskband 塞进任务栏 | ❌ 阶段一不做（风险高、Win11 24H2 后微软在收编任务栏）；日后若做"真任务栏搜索框"再回看 |
| `EverythingToolbar.Launcher` | 提升权限重启、跨会话 IPC | 借鉴（UniSearch 需要"以管理员运行搜索结果"，但 UAC 升权进程边界要单独设计） |

## 3. 值得单独记下的工程细节

1. **run count 反哺排序**：Everything 自己维护打开次数（`INC_RUN_COUNT` dwData 23/24，或 1.5 管道 cmd 17）。
   → UniSearch 对**文件实体**的 MRU 信号应直接读写 Everything，而不是自建一份；自建 usage 只服务非文件实体。
2. **`HIGHLIGHTED_NAME`/`HIGHLIGHTED_PATH` 请求位**（QUERY2 0x2000/0x4000）：高亮由 Everything 计算，客户端不要重复实现匹配。
3. **每个 reply 窗口只允许一个在飞查询**，新查询自动取消旧查询 —— 与"输入即搜"天然契合，也是 Broker 里 `RequestId` 兜底校验的原因。
4. 注释里大量"为什么这么写"的性能说明（快路径、避免枚举失效、网络路径不可在 UI 线程调用）——移植时**连注释一起搬**，这些是最贵的部分。
5. 窗口置顶/跟随任务栏/失焦隐藏/动画开关是分开的小服务，不是塞在 code-behind 里 —— UniSearch 沿用这种切分。

## 4. 不采用 fork 作为基座的原因（一句话）

它的 `SearchResult` 是文件专用的，`SearchQuery` 是 Everything 开关的直译，`Platform` 绑死原生 SDK ——
把它改成 provider 无关，等于重写这三层，而这三层正是"fork 之后最难维护的差异"。
UI/图标/预览/动作/接管输入这些**外围资产**才是该拿的东西。
