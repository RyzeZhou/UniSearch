# 上游项目审计：许可证 + 复用决策

> 全部已 clone 到 `D:/tools/refs/`，本文件的每条结论都来自**本地源码**（含文件路径/行号），不是记忆或二手博客。
> 核实时间：本轮开发会话。协议细节另见同目录 `EVERYTHING-IPC-VERIFIED.md`。

## 1. 许可证明细（能不能复制代码）

| 项目 | 许可证 | 本地路径 | 可否复制代码 | 备注 |
|---|---|---|---|---|
| srwi/EverythingToolbar (14.7k★) | **MIT** (Copyright (c) 2023 Stephan Rumswinkel) | `refs/everythingtoolbar` | ✅ 可以（保留版权） | 例外见下行 |
| ↳ `EverythingToolbar/ThirdParty/ShellContextMenu.cs` | **文件内无许可证声明**，`namespace Peter`（CodeProject 时代的"stand-alone shell context menu"） | 同上 | ⚠️ **不要复制** | 上游仓库整体 MIT 不覆盖带 ThirdParty 标记的外来文件；需要右键菜单请自行用 `IContextMenu`+`TrackPopupMenu` 实现 |
| ↳ `EverythingToolbar.Deskband/ThirdParty/CSDeskBand.cs` | **MIT** (Copyright (c) 2017 Brandon Chong，文件头指向 dsafa/CSDeskBand) | 同上 | ✅ 可以 | 我们暂时不做 deskband，仅备用 |
| ↳ `EverythingSDK/`、`EverythingSDK3/` | **MIT** (Copyright (c) 2025 voidtools / David Carpenter) | 同上 | ✅ 可以 | 官方 IPC 头 + 1.5 命名管道客户端源码（`Everything3.c` 9633 行）——协议层最权威依据 |
| Flow-Launcher/Flow.Launcher (15.5k★) | **MIT** (Copyright (c) 2019 Flow-Launcher; Copyright (c) 2015 Wox) | `refs/flowlauncher` | ✅ 可以 | `attribution.md` 说明其源自 Wox 分支 |
| microsoft/PowerToys → `src/modules/cmdpal` | **MIT** (Microsoft Corporation) | `refs/powertoys`（sparse） | ✅ 可以 | 只 sparse-checkout 了 Command Palette |
| ramensoftware/windhawk-mods | **逐文件声明**：603 个 mod 中 **470 个没有 `@license`** | `refs/windhawk-mods` | ⚠️ **默认不可复制** | 只有带 `// @license MIT` 的文件可用（如 `explorer-command-bar.wh.cpp`、`search-active-display.wh.cpp`）。无声明的只读思路 |
| HamzaETTH/EverythingSearchBox (3★) | **无 LICENSE 文件** | `refs/everythingsearchbox` | ❌ 不可复制 | 公共可见 ≠ 授权。行为可参考，代码不要搬 |
| sgrottel/EverythingSearchClient | **Apache-2.0**（NuGet 元数据确认；0.9.0.148，2025-06-26 发布） | NuGet 包 | ✅ 可以 | 纯托管、AnyCPU、不依赖原生 `everything.dll` |
| lepoco/wpfui | **MIT** (2021-2025 Leszek Pomianowski) | NuGet（4.3.0） | ✅ 可以 | EverythingToolbar 自己就用它做 Win11 观感 |
| voidtools Everything 本体 | 免费但**闭源** | 未安装 | — | 外部依赖，只走 IPC |
| AnyTXT Searcher | **闭源** | 未安装 | — | 只能作为外部服务写 Provider |
| Zotero 桌面端 | AGPL-3.0（其源码），但我们只用它的**本地 HTTP API** | — | — | 不链接其代码，无传染性 |

## 2. 本机环境事实（会影响技术选型，都已实测）

| 事实 | 证据 | 后果 |
|---|---|---|
| **NuGet 可以联网还原** | `dotnet restore` EverythingToolbar.Core 成功（3.08s）；`dotnet add package CommunityToolkit.Mvvm 8.3.2` 成功 | 之前"零依赖"的约束作废 → 允许用 WPF-UI / CsWin32 / EverythingSearchClient / xunit |
| **没有 MSVC** | `where cl.exe` 未找到 | ET 的 `EverythingSDK.vcxproj`/`EverythingSDK3.vcxproj` 无法构建 → **不能直接引用 `EverythingToolbar.Platform`**，只能读+移植；也印证"不要 fork ET 当基座" |
| Everything 未安装 | `C:\Program Files\Everything` 不存在 | Everything Provider 在本机只能做协议层单测，实机验证需要用户装 |
| WPF 可用 | `Microsoft.WindowsDesktop.App 8.0.30` 已装 | UI 走 WPF + .NET 8 成立 |
| 传输会吃掉双反斜杠 | 本会话实测：`'\\'` 被写成 `'\'` | C# 里反斜杠一律用 `'\u005C'`；见 `docs/16-ENV-PITFALLS.md` |

## 3. 读码后对"能不能当基座"的判断

### 3.1 为什么**不** fork EverythingToolbar

不是因为它不好，而是它的**结果模型是文件专用的**，正好和"要接 Zotero/Obsidian 这类非文件实体"冲突：

```csharp
// refs/everythingtoolbar/EverythingToolbar.Core/Data/SearchResult.cs
public sealed record SearchResult(
    string HighlightedPath, string HighlightedFileName,
    string FullPathAndFileName, bool IsFile, long FileSize, FILETIME DateModified)
```

- `Path` / `FileName` 是从 `FullPathAndFileName` 现推的；没有 URI、没有正文 snippet、没有元数据字典、没有动作列表 → 文献条目（标题/作者/年份/`zotero://`）无处安放。
- 它的抽象接缝 `IItemsProvider<T>`（`Core/Search/IItemsProvider.cs`）虽然泛型化，但**只有一个实现** `EverythingItemsProvider`，且 `SearchQuery` 记录里全是 Everything 的开关（MatchCase/MatchPath/UseRegex），分类靠 Everything 的 `Filter.Search` 字符串（`file:` / `ext:bat;cmd;exe;…`）。
- 它的 `Platform` 层要 CsWin32 + NLog + 两个 C++ 工程 → 本机不可构建。
- 结论：**保留"新建 Search Host"的路线，把 ET 当协议与 UI 的高价值移植来源。**

### 3.2 但有一件事 ET 已经替我们做完了：分类规则

ET 的内置筛选器就是 Everything 官方语义的分类，直接对齐比我自造扩展名表更正确
（`EverythingToolbar.App/Services/DefaultFilterProvider.cs` + `Services/FilterNames.cs`）：

| ET 分类 | 交给 Everything 的串 |
|---|---|
| 文件 | `file:` |
| 文件夹 | `folder:` |
| 可执行 | `ext:bat;cmd;exe;msi;msp;scr`（宏 `exe`） |
| 图片 | `ext:ani;bmp;gif;ico;jpe;jpeg;jpg;pcx;png;psd;tga;tif;tiff;webp;wmf`（宏 `pic`） |
| 音乐 / 视频 / 压缩包 / 文档 | 宏 `audio` / `video` / `zip` / `doc` |

→ `UniSearch.Core` 的分类引擎应当**输出这些串给 Everything**，而不是自己按扩展名后过滤。
同时 `CategoryIds.ExtensionMap` 保留，用于 AnyTXT/Zotero 等不懂 Everything 语法的后端。

### 3.3 Flow Launcher 里最该拿的东西：`DialogJump`

`refs/flowlauncher/Flow.Launcher.Infrastructure/DialogJump/`（MIT，`DialogJump.cs` 1115 行 + `Models/WindowsExplorer.cs` 260 行）
已经解决了本项目"阶段二"最难的一块 —— **不注入 explorer.exe 就拿当前活动标签页的目录**：

1. `GetProcessNameFromHwnd(前台窗口)` == `explorer.exe` 判定来源；
2. `CoCreateInstance(CLSID_ShellWindows = 9BA05972-F6A8-11CF-A442-00A0C90A8F39)` → `IShellWindows` 枚举 → `IWebBrowser2`；
3. **活动标签页** = `FindWindowEx(顶层, NULL, "ShellTabWindowClass", NULL)`（z-order 最上面那个就是当前 tab，注释里致谢了 w4po/ExplorerTabUtility）；
4. 用 `IServiceProvider.QueryService(IShellBrowser)` → `GetWindow()`（**必须在 STA 线程里**，见 `StartSTAThread`）→ 与 tab 句柄比对定位窗口；
5. `IWebBrowser2.LocationURL` → `NormalizeLocation()`；取不到时退回 `IShellFolderViewDual`（处理回收站、此电脑、命名空间 GUID）。

→ 移植进 `UniSearch.ShellContext`，就是"在哪个目录里按 Ctrl+F 就搜哪个目录"的实现。
配合 `RegisterHotKey` 全局钩子 + 前台窗口探测，**MVP 完全不需要 Windhawk**。

### 3.4 Windhawk 留到阶段三，但已经把路探明了

无许可证所以不抄代码，不过这些 mod 证实了原生搜索框区域**确实可改**，并给出了具体 hook 目标：

| 文件 | 给出的可操作事实 |
|---|---|
| `legacy-search-bar.wh.cpp` | hook `ExplorerFrame.dll` 中的符号 `CUniversalSearchBand::IsModernSearchBoxEnabled()`、`CSearchEditBox::HideSuggestions()` 可切回 DirectUI 搜索框 |
| `hide-search-bar.wh.cpp` | `FindChildWindow(hWnd, "TravelBand")` + `SetWindowSubclassFromAnyThread` 在 `WM_SIZE`/`WM_PARENTNOTIFY` 时隐藏搜索带 |
| `explorer-command-bar.wh.cpp`（MIT，5161 行） | 在 `CabinetWClass`/`ShellTabWindowClass` 里挂自定义命令栏的完整做法 |
| `explorer-details-better-file-sizes.wh.cpp` | 在 explorer.exe 进程内动态加载 Everything SDK3 查询目录大小（1.5.0.1384a+ 用 SDK3）——证明 `explorer.exe → Everything` 链路稳定可行 |

### 3.5 PowerToys Command Palette：只借 UI 与扩展契约思路

`refs/powertoys/src/modules/cmdpal/Microsoft.CmdPal.UI/ExtViews/{ListPage,ListItemsView}.xml` 是 WinUI 的分组/虚拟化列表写法，
可以作为"分区列表 + 键盘优先"的 UI 参考；但它是 WinUI3 + 宿主框架约束，不符合"UI/分类完全自定义"，
**且我们要的 Explorer 上下文接管它没有**。→ 思路参考，代码不引入。

## 4. 因此：依赖表（推翻上一轮的"零依赖"）

| 用途 | 决策 | 来源 |
|---|---|---|
| Win11 观感（Mica/FluentWindow/TitleBar/Tray/Snackbar/控件样式） | **用包** `WPF-UI` 4.3.0 | MIT，ET 同款 |
| MVVM | **用包** `CommunityToolkit.Mvvm` 8.3.2 | MIT，ET 同款 |
| Everything 查询（阶段一） | **先用包** `EverythingSearchClient` 0.9.0.148 | Apache-2.0，纯托管无原生 DLL；API：`Search(query, SearchFlags, maxResults, offset, whenBusy, timeoutMs)` → `Result{TotalItems,NumItems,Offset,Item[]{Name,Path,Flags}}` |
| Everything 查询（阶段二升级） | **自己写 QUERY2 客户端**（Lite/2 版布局已核实） | 包只回 Name/Path/Flags，拿不到 Size/DateModified/Highlighted\*/RunCount，而列表列与排序需要它们 |
| 1.5 命名管道传输 | 自己写（`Everything3.c` 已给出线格式） | MIT 源码在本地 |
| 当前 Explorer 目录 | **移植** Flow 的 DialogJump（COM：IShellWindows/IWebBrowser2/IShellBrowser/IFolderView2） | MIT；用 `Microsoft.Windows.CsWin32` 生成接口，或手写这 4 个 |
| 全局快捷键 | 自己写 `RegisterHotKey` + 消息循环（约 120 行）或 `NHotkey.Wpf` | ET 用 NHotkey.Wpf + `LowLevelKeyboardHook.cs` 处理 StartMenu 拦截 |
| Shell 图标 | 自己写（`SHGetFileInfo`/`IImageList`），参照 ET `Icons/IconProvider.cs` 的缓存与 DPI 处理 | 思路参考 |
| 右键 shell 扩展菜单 | 自己写 `IContextMenu`；**不要**复制 ET 的 `ThirdParty/ShellContextMenu.cs` | 该文件无许可证声明 |
| 单测 | **用包** `xunit` + `Microsoft.NET.Test.Sdk` | NuGet 可用 |
| 日志 | `Microsoft.Extensions.Logging` + 文件 sink，或 ET 的 NLog | 择一 |

## 5. 对已写代码的处置

| 已存在 | 处置 |
|---|---|
| `UniSearch.Sdk`（能力接口 / SearchResult / SearchContext） | **保留**。审计确认：ET/Flow/CmdPal 都没有"非文件实体 + 目录限定调度"这层，这正是本项目的原创部分 |
| `UniSearch.Core` 的 Fusion / Category / Ranker / Broker | **保留并改**：分类改为优先输出 Everything 串（3.2）；`IUsageStore` 对文件实体让位给 Everything 的 `RUN_COUNT`（本地 clone 的 `everything_ipc.h` 证实 19/20 号请求可读写，表 E） |
| 我原先打算手写的 Everything `EN_*` IPC 层 | **作废**。那些常量是虚构的（见 `EVERYTHING-IPC-VERIFIED.md` 第 0 节），改成"先用包，再按核实布局自写 QUERY2" |
| 零依赖 `Directory.Build.props` 注释 | 撤掉，改成集中版本管理（`Directory.Packages.props`） |
