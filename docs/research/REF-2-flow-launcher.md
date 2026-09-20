# 参考仓库 2：Flow-Launcher/Flow.Launcher

- 本地路径 `D:/tools/refs/flowlauncher`（浅克隆）
- 许可证 **MIT**（Copyright (c) 2019 Flow-Launcher；Copyright (c) 2015 Wox —— `attribution.md` 说明它是 Wox 分支的延续，所以里面不少写法继承自 Wox）
- 15.5k★，`.NET 9` / WPF，插件生态（`Flow.Launcher.PluginsManifest` 独立仓库）
- 定位：**启动器**（一维榜单 + action keyword），不是资源管理器式分类检索 —— 与我们目标不同的地方见 §5

## 1. 它到底做了什么（工程结构索引）

| 目录 | 作用 |
|---|---|
| `Flow.Launcher.Plugin/` (2597 行) | **插件契约层**：`Result`、`Query`、`IPlugin`/`IAsyncPlugin`、各种能力接口。这是我们最该读的一层 |
| `Flow.Launcher.Core/` | 宿主内核：插件加载（`Plugin/`）、进程外插件（`ExternalPlugins/`）、设置、存储、更新 |
| `Flow.Launcher/` | WPF 前端：`MainWindow.xaml`、`ViewModel/MainViewModel.cs`、`Storage/`（含 MRU）、`Helper/`（热键、单实例、置顶） |
| `Plugins/Flow.Launcher.Plugin.*` | 内置插件：Explorer、Program(应用)、WindowsSettings、BrowserBookmark、Shell、Sys、Url、WebSearch、Calculator、ProcessKiller、PluginsManager、PluginIndicator |

## 2. 与本项目直接相关的四块（带指针）

### 2.1 `Flow.Launcher.Infrastructure/DialogJump/` —— **不注入 explorer.exe 就拿到当前目录/标签页**

这是本项目"Ctrl+F 在当前 Explorer 目录内搜"的核心机制，且已解决 Win11 多标签页问题。

| 文件 | 关键点 |
|---|---|
| `DialogJump.cs`(1115) | 前台窗口判定、去抖/缓存、把启动器结果"跳"到目标窗口（写入文件对话框的控件） |
| `Models/WindowsExplorer.cs`(260) | 取 Explorer 路径的全链路：`GetProcessNameFromHwnd == "explorer.exe"` → `CoCreateInstance(CLSID_ShellWindows 9BA05972-F6A8-11CF-A442-00A0C90A8F39)` → `IShellWindows.Item(i)` as `IWebBrowser2` → 活动标签页 = `FindWindowEx(hwnd, 0, "ShellTabWindowClass", 0)`（注释：活动 tab 总在 z-order 最前，致谢 w4po/ExplorerTabUtility）→ `IServiceProvider.QueryService(ISHellBrowser)`（**必须在 STA 线程里 `GetWindow()`**，见 `StartSTAThread`）→ `IWebBrowser2.LocationURL` → `NormalizeLocation()`；拿不到路径（回收站/此电脑/命名空间 GUID）时退回 `IShellFolderViewDual` |
| `Models/WindowsDialog.cs`(345) | 同类机制对付"另存为/打开"文件对话框（`#32770` 类）—— 顺手解决"在保存对话框里搜文件并填路径" |
| `Flow.Launcher.Plugin/{DialogJumpResult.cs,IDialogJump.cs,IDialogJumpExplorer.cs,IDialogJumpDialog.cs}` | 对外契约：插件可产出 `DialogJumpResult{ DialogJumpPath }` 参与这个"跳转" |

→ **移植到 `UniSearch.ShellContext`**（阶段二）。这等于免写了整套 Shell COM 探测。
注意它依赖 `Microsoft.Windows.CsWin32` 生成的 `Windows.Win32.*` 接口，移植时要么带 CsWin32，要么手写这 5 个接口。

### 2.2 `Plugins/Flow.Launcher.Plugin.Explorer/Search/` —— **Windows 索引就是免费的正文检索器**

这一层回答了一个我们原方案里没考虑的问题：**正文搜索未必要依赖闭源的 AnyTXT。**

| 文件 | 关键点 |
|---|---|
| `IProvider/IIndexProvider.cs` | `IAsyncEnumerable<SearchResult> SearchAsync(search, ct)` |
| `IProvider/IContentIndexProvider.cs` | `ContentSearchAsync(plainSearch, contentSearch, ct)` —— **文件名检索与正文检索被拆成两个接口**，正好对应我们 `SearchesFileName` / `SearchesFileContent` 两个能力位 |
| `IProvider/IPathIndexProvider.cs` | `EnumerateAsync(path, search, recursive, ct)` —— 目录列举/递归枚举，对应我们 `SupportsListing` + `ScopeKind.CurrentDirectoryOnly/Recursive` |
| `WindowsIndex/QueryConstructor.cs` | 用 **SystemIndex catalog + OLE DB SQL**：`scope='file:<path>'`(递归目录限定)、`AND (System.FileName LIKE 'x%' OR CONTAINS(System.FileName,'"x*"'))`、正文 `FREETEXT('<q>')`、排序 `System.Search.Rank DESC`、`SELECT TOP n` |
| `WindowsIndex/{WindowsIndex.cs,WindowsIndexSearchManager.cs}` | ADODB 连接、列集合、结果转 `SearchResult` |
| `Everything/*` | Everything 后端：`EverythingAPI.cs`+`EverythingApiDllImport.cs`(1.4 SDK) 与 `EverythingApiV3.cs`(1.5 SDK) 并存，`EverythingSearchManager.cs` 负责消息窗口/线程，`EverythingSearchOption.cs`/`EverythingSortOption.cs`，`EverythingDownloadHelper.cs`（**没有 Everything 时引导下载安装**的 UX），`Exceptions/*` |
| `Search/ResultManager.cs` | 路径规范化、`GetPathWithActionKeyword`（结果标题带前缀关键字，点击后复用插件）、常用位置集合（`GetFolderPath` + `Downloads`）、`IsHomeFolderPath` |
| `Search/SearchResult.cs` | `readonly record struct SearchResult(FullPath, ResultType Type, int Score, bool WindowsIndexed, List<int> HighlightData)` |

→ ~~结论：`UniSearch.Providers.WindowsIndex` 应作为**内置第二后端**~~ **【已作废 — 明确不做 Windows 索引】**
   决定（见 MVP §G 与 `REUSE-CANDIDATES.md`）：Everything 已覆盖文件名/路径检索，其索引质量、速度、
   语法能力均优于 SystemIndex；再接一个索引源只会增加"为什么这条没出来"的解释负担。
   本节保留仅作技术记录 —— `scope='file:'`、`System.Search.Rank`、OleDb 保留字转义这些细节
   若将来真要接正文检索后端仍有参考价值。
→ `WindowsIndexed` 这个字段的启发仍然成立：结果要能声明"来自哪个索引"，UI 才能解释"为什么这条没有"。我们对应的是 `SearchResult.ProviderId` + `ResultTag` 溯源 chips。

### 2.3 `Flow.Launcher/Storage/UserSelectedRecord.cs` + `ViewModel/MainViewModel.cs` —— **MRU 的具体公式**

```
GetSelectedCount(result) = 5 * count(hash(query + result)) + count(hash(result))
result.Score += GetSelectedCount(result) + priorityScore   // 并对 int.MaxValue 做饱和
持久化：FlowLauncherJsonStorage<UserSelectedRecord>，Dictionary<int,int>（键=hash）
```

含义：**同一查询下点过同一条结果**的权重是**全局点过该结果**的 5 倍。
→ 我们的 `IUsageStore` 之前只有全局键（canonical path），要补上"查询维度"，否则"我在搜 chrome 时点过一次某个 pdf，会污染所有查询的排序"。见 §4。

### 2.4 `Flow.Launcher.Core/Plugin/JsonRPCPlugin*.cs` + `ExternalPlugins/Environments/*` —— **进程外插件**

子进程 + stdio 换行分隔 JSON-RPC（`RedirectStandardOutput`，方法名如 `query`），环境抽象 `AbstractPluginEnvironment` 派生 Python/JavaScript/TypeScript（+V2 协议）。
→ 阶段四再考虑；但它证明"`ISearchProvider` 可以被序列化成 JSON 契约"这条路可行，所以 Sdk 里**不要让 `Payload`/`Action` 携带不可序列化对象**（见 §4 的 SDK 缺口）。

## 3. `Flow.Launcher.Plugin/Result.cs`(485) 逐字段对照

它有而我们的 `SearchResult` **缺**的（按优先级）：

| 字段 | 语义 | 是否补 |
|---|---|---|
| `CopyText` | Ctrl+C 实际复制的文本（可以是 URL/引用串，而非标题） | ✅ 补 |
| `AutoCompleteText` | 按 Tab 补全成什么 | ✅ 补（Everything 的 typeahead 体验靠它） |
| `TitleHighlightData : List<int>` | 标题中命中字符的**索引集合**（而非字符串标记） | ⚠️ 我们的 `[[..]]` 记法够用；但索引法对模糊匹配更准，保留演进空间 |
| `RecordKey` | 参与 MRU 记账的键（可与标题不同） | ✅ 与 §2.3 一起补 |
| `AddSelectedCount` | 该结果是否参与 MRU | ✅ 补 |
| `ProgressBar` / `ProgressBarColor` | 结果行内进度条（索引进度、下载进度） | ⚠️ 阶段二再补，现在用 provider 级 `IndexState` |
| `PreviewVisibility`(`Optional/Disabled/...`) | 单条结果禁用预览 | ✅ 补，成本极低 |
| `RoundedIcon` / `ShowBadge` / `BadgeIcoPath` / `Glyph` | 图标呈现细节（圆形头像、角标） | ⚠️ 部分补：`IconHint` 已有 Glyph/Badge，缺 `RoundedIcon` |
| `Action` / `AsyncAction` 委托、`PreviewPanel : Lazy<UserControl>` | 把**委托和 WPF 控件**塞进结果对象 | ❌ **不学**：这使结果无法跨进程、无法缓存、无法测试。我们用 `Payload` + `IActionProvider`/`IPreviewProvider`（能力接口）替代 |
| `Score : int`（`MaxScore = int.MaxValue`） | 插件自报分数，宿主只做加法 | ❌ 不学：跨后端 int 不可比，这正是我们要 `ProviderScore + 统一 Ranker` 的原因 |

它有而我们认为**不该要**的：`PluginID` 由宿主回填（我们是 `ProviderId` 显式字段，等价）；`QuerySuggestionText`；`ActionKeywordAssigned`（启动器专属）。

## 4. 因此对 UniSearch 的具体改动（本仓库带来的）

1. `SearchResult` 增加：`CopyText`、`AutoCompleteText`、`RecordKey`、`AddSelectedCount`、`PreviewVisibility`。
2. `IUsageStore` 改成**双键**（查询维度 5× + 全局 1×），并允许结果自定义 `RecordKey`。
3. 新增内置 **`WindowsIndexProvider`**（OLE DB SystemIndex：`scope='file:'` + `FREETEXT` + `System.Search.Rank`），AnyTXT 降为可选。
4. `Sdk` 保持"无委托、无 UI 类型、可 JSON 序列化"的约束，为将来的进程外 Provider 留门。
5. `ShellContext` 直接照 DialogJump 的链路实现（含 STA 线程坑与 `LocationURL` 规整）。

## 5. 与本项目**不同**、不要照搬的部分

- **UI 是一维榜单**：所有插件抢同一个纵向列表，靠 `Score`+ 优先级排序；我们要的是**分类分组**（全部/文件/文件夹/文档/图片/应用/文献/笔记/正文命中）。所以 `MainViewModel` 的结果合并/去重逻辑可参考，展示模型不可参考。
- **action keyword 心智模型**（`>` `#` `!` 前缀切插件）与我们的"分类标签 + 目录范围"心智不同；可作为高级功能保留语法糖，不作主干。
- 它没有"上下文作用域"概念（没有 `SearchContext`/`ScopeKind`），所以"在 Explorer 目录里搜当前目录"要我们自己设计 —— DialogJump 只给了**取值**手段，没给**调度**语义。
- 设置持久化用 `Config` 包 + JSON 文件，插件各自一份 settings.json；我们集中一份 `settings.json` + `providers.<id>` 节。
