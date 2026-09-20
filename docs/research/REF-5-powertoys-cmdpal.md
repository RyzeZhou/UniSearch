# 参考仓库 5：microsoft/PowerToys → `src/modules/cmdpal`（Command Palette）

- 本地路径 `D:/tools/refs/powertoys`（**sparse-checkout 只取了 `src/modules/cmdpal`**，80MB）
- 许可证 **MIT** (Microsoft Corporation)
- 技术栈：WinUI 3 + `net9.0-windows`、`Microsoft.Windows.CsWin32`、WinRT 元数据编组(MBM) 做进程外扩展
- 对本项目的价值排序：**① 设计文档 `doc/initial-sdk-spec/`（最值钱）② Ext.Indexer 的 Windows 搜索索引互操作 ③ extensionsdk 的动作词汇表 ④ UI 观感参考**。UI 代码本身不可移植（WinUI3 + 宿主约束）。

## 1. 结构索引

| 路径 | 内容 |
|---|---|
| `doc/initial-sdk-spec/initial-sdk-spec.md` (2843 行) | 扩展 SDK 规范：扩展发现/生命周期、Commands、Pages、Other types、Helper 类、Status messages、`ICommandProvider2`、带参数命令、**Addenda III: Rich Search（A/B/C 三份草稿）** |
| `doc/command-pal-anatomy/command-palette-anatomy.md` | 宿主 UI 语义分解（含 gif 示意） |
| `extensionsdk/Microsoft.CommandPalette.Extensions.Toolkit/` | C# 便捷层：`Command`、`CommandItem`、`CommandContextItem`、`DynamicValueSettings`、`BaseObservable`… 以及 **`Commands/` 现成动作**：`OpenFileCommand`、`ShowFileInFolderCommand`、`OpenWithCommand`、`OpenPropertiesCommand`、`OpenInConsoleCommand`、`CopyPathCommand`、`CopyTextCommand`、`ConfirmableCommand`、`NoOpCommand` |
| `Microsoft.CmdPal.UI/` + `.UI.ViewModels/` | WinUI3 宿主：`ExtViews/ListPage.xaml`、`ListItemsView.xaml`（分组列表渲染）、`Dock`、消息与导航栈 |
| `ext/Microsoft.CmdPal.Ext.Indexer/` | **文件/应用搜索内置扩展**（下一节） |
| `ext/SamplePagesExtension/Pages/SectionsPages/*` | 分组列表的官方样例（`SampleListPageWithSections.cs`） |
| `ExtensionTemplate/` | 新建扩展模板（含 winmd 注册流程） |

## 2. `Ext.Indexer` —— ~~UniSearch `WindowsIndexProvider` 的直接来源~~ **【已作废：明确不做 Windows 索引】**

> **决定**：不实现 Windows 索引 Provider（Everything 已覆盖，索引质量/速度/语法均更好）。
> 本节保留为技术记录，供将来评估正文检索后端时参考。

这一层替我们把 **Windows Search 索引 (SystemIndex) 的托管互操作**全写好了（MIT）：

```
Indexer/SystemSearch/  ISearchManager  ISearchCatalogManager  ISearchQueryHelper
                       ICommand  ICommandText  IDBCreateSession  IDBInitialize
                       IRowset  IRowsetInfo  IPropertyStore  PropVariant  PropertyKey
Indexer/OleDB/         DBPROP  DBPROPIDSET  DBPROPSET  IRowset  IRowsetInfo
Indexer/Utils/         QueryStringBuilder.cs            ← 组装 SELECT
                       ImplicitWildcardQueryBuilder.cs  ← ★ 自由文本 vs 结构化查询的分流
                       UrlToFilePathConverter.cs
Commands/PeekFileCommand.cs                            ← 文件“快速查看”动作
```

关键 API：`ISearchQueryHelper.GenerateSQLFromUserQuery(string)` —— **微软自己实现 AQS→SQL**，
我们不需要手写 `CONTAINS(...)`/`LIKE` 串（Flow 那份 `QueryConstructor.cs` 是手写 SQL，见 REF-2 §2.2）。

### 2.1 必须采纳的设计规则：结构化查询直通（AQS passthrough）

`ext/Microsoft.CmdPal.Ext.Indexer/README.md` 的 "Query Handling Contract" 写得很清楚：

> 简单自由文本 → 放宽文件名匹配（加隐式通配），让搜索更符合直觉；
> 已经像 AQS/结构化语法 → **不改写**，直接 `GenerateSQLFromUserQuery` 原样传下去。
> 不改写的例子：`name:report`、`kind:folder`、`kind:folder AND report`、`*report*`、`C:\Users`、`size>10MB`

判定与实现在 `Indexer/Utils/ImplicitWildcardQueryBuilder.cs`（`ParsedTokenKind.StructuredToken`、`IsStructuredToken(token)`、`expectsStructuredValue`）。

> **对本项目的直接影响（这是一个真实缺陷）**：我现在写的 `QueryParser` + `EverythingQueryTranslator` 会
> 把 `kind:folder` 翻成 `ext:...`、把 token 逐个重排 —— 用户如果输入的是 **Everything 自己的高级语法**
> （`content:`、`filelist:`、`boolean:`、`regex:extension:^mp`、`(a|b)` 分组、`!"x"`）就会被拆坏。
> 必须加一条 **"看起来结构化 → 原样透传给该后端"** 的规则，每个 Provider 自己决定它信任哪种语法：
> - Everything：识别到 Everything 专有函数/括号/`|` → 整串直通（仅在其前拼 `ancestor:"…"` 范围前缀）。
> - WindowsIndex：走 `GenerateSQLFromUserQuery`。
> - AnyTXT/Zotero：没有结构化语法 → 只用自由文本部分。
> 规则细节与判定表放到 `docs/spec/QUERY-SYNTAX.md`。

### 2.2 兜底能力

`Ext.Indexer` 证明：**没有 Everything 也能做毫秒级文件名+正文搜索**（Windows 索引开着的话），
且有 `SearchCatalogStatus.cs` / `SearchCatalogStatusReader.cs` 读索引器状态（Paused/Scanning/…）——
这正是我们 `IContentSearchProvider.GetIndexStateAsync` 需要的实现样本。

## 3. Pages / 分组语义：与我们的**有意分歧**

规范里（§735 起）：

- `IPage`：`Title`、`IsLoading`（宿主画不确定进度条）、`AccentColor`；页面类型 List / Content / Grid。
- List 分 **static**（宿主负责过滤，用 fuzzy 匹配 `Name` + `Subtitle` + `Tag.Text`）与 **dynamic**（宿主只把 `SearchText` 设回页面，由扩展自己出结果）。
- `ListItem.Section`（string）：宿主按 **section 首次出现的顺序**分组 —— 分组次序由**扩展的返回顺序**决定。
- `ITag`、`TextToSuggest`（建议写回搜索框，见注册表路径补全的例子）、`GetMoreListItems()`（"加载更多"）、`Favicon`/`Glyph`。
- Addenda III Rich Search：A=富搜索框、B=前缀搜索、C=ZWSP 分隔 token（`TokenSearch` + `IExtendedAttributesProvider`），2025-11 起宿主实现 C 的简化版。

**分歧点（重要）**：`Section` 把"分组与次序"交给 Provider，正是我们在规格里明令禁止的
（"不要让 Provider 决定最终分类"）。UniSearch 的分类次序由 `CategoryIds` 固定表 + 用户配置决定，
Provider 只给语义字段。→ 我们采纳 static/dynamic 之分、`TextToSuggest`、`GetMoreListItems`、`IsLoading`、`ITag`；
**不采纳** Section 次序语义。

## 4. 进程外扩展模型（看，但 v1 不用）

规范 §144–§452：扩展是 **out-of-proc COM 服务器**，用 WinRT metadata-based marshalling（`Microsoft.CommandPalette.Extensions.winmd`）
+ 注册表发现 + 生命周期（idle 卸载、`Reload`），并直言 WinRT 集合对象 "Considered Harmful"（无法表达增量变化）、
cppwinrt/CsWinRT 每接口单独 `GetRuntimeClassName` 的坑。

→ 结论：这条路能走通、且是官方做法，但要背上 Windows App SDK + winmd 注册 + COM 生命周期。
UniSearch 的 v1 用**进程内 Provider DLL**；若将来开外部插件，**优先 Flow 那种 stdio JSON-RPC**（REF-2 §2.4），
把 CmdPal 这套当"如果要上 Microsoft Store 生态"时的备选。

## 5. 采纳清单（本仓库带来的改动）

1. `QueryParser` 增加 **`LooksStructured`** 判定 + `SearchQuery.RawForProvider`（透传串）；各 Provider 声明自己信任的语法族。
2. 新增 `UniSearch.Providers.WindowsIndex`：移植 `Indexer/SystemSearch/*` + `OleDB/*` 的 COM 接口定义与 `QueryStringBuilder`，
   用 `ISearchQueryHelper.GenerateSQLFromUserQuery` + `scope='file:<dir>'` 实现"免费的本机正文+名称检索"（AnyTXT 之外的可选后端）。
3. 移植 `SearchCatalogStatus*` 的实现思路给 `IndexState`（索引是否在扫描/暂停/被排除路径）。
4. 采纳动作词汇表（`OpenFile/ShowFileInFolder/OpenWith/OpenProperties/CopyPath/RunInConsole/Confirmable`）作为 UniSearch 的**宿主默认动作集**，命名对齐。
5. 采纳 `IsLoading` + `GetMoreListItems`（"加载更多"）到 `IPagedSearchProvider`（新接口）。
6. 参考 `PeekFileCommand` 作为"应用内预览"的备选（WinUI 用 `Windows.Storage`/`IExplorerCommand`；我们是 WPF → 用 `IPreviewHandler`，或直接接 QuickLook，见 REF-1）。
