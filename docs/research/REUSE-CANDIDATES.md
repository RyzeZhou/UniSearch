# 复用候选清单（REF 里现成的轮子）

> 结论先行：**能直接用的轮子比我原先估的多**。全局热键、托盘、拖出、Windows 索引、
> 缩略图管线、真·shell 右键菜单都有成熟且许可证干净的选择。
>
> 本文所有许可证都**实地核实过**（还原到临时工程再读 nuspec / 内嵌 LICENSE），
> 不是凭印象写的。核实方法见 §6，可复现。

---

## 1. 结论速览

| 领域 | 建议 | 许可证（已核实） | 里程碑 |
|---|---|---|---|
| 全局热键 `Ctrl+F` | **NHotkey.Wpf** — ET 和 FlowLauncher 都在用，别手写 `RegisterHotKey` + 消息循环 | Apache-2.0 | M2 |
| Explorer 当前目录 | **移植 FlowLauncher `WindowsExplorer.cs`**（IShellWindows COM） | MIT | M2 |
| 托盘图标 | **NativeTray** 2.2.3 — ✅ **已采用**（第 3 轮）。`TrayIconHost` + `TrayMenu`，右键菜单/左键单击/气泡都有；**不要用 Hardcodet.NotifyIcon.Wpf**（CPOL，不在 MIT/Apache 范围内） | MIT | ✅ 完成 |
| 拖出文件 | **Droplex**（`SetFileDropList`，UI-SPEC §4.2 那条） | MIT | M2 |
| 真·shell 右键菜单 | **Vanara.PInvoke.Shell32** 提供 `IContextMenu/2/3`，托管逻辑自写 ~150 行 | MIT | M2+ |
| ~~Windows 索引~~ | ❌ **不采用**：Everything 已覆盖文件名/路径检索，索引质量、速度、语法均更好；再接一个索引源只会增加解释负担。原文见 §3.2（保留作技术记录） | — | 撤销 |
| 缩略图管线 | **移植 FlowLauncher `ThumbnailReader.cs`**（`IShellItemImageFactory`） | MIT | M3 |
| 图标异步加载 | **移植 ET `IconLoader.cs`**（2 工作线程 + 128 积压 + 批量刷 UI） | MIT | M3 |
| LRU 缓存 | **BitFaster.Caching** | MIT | M3 |
| Zotero 库读取 | **Microsoft.Data.Sqlite** | MIT | M4 |
| 拼音搜索 | **ToolGood.Words.Pinyin** | MIT | 后续 |
| 设置持久化 | ❌ **不用 Config.Net**（Apache-2.0，原计划），改用**内置 `System.Text.Json`**。第 4 轮实测：Config.Net 把 `int` 写成字符串、把 `string[]` 存成**空格拼接的单个字符串**（`{"Excludes": "D:\\a C:\\b"}`）—— 排除目录带空格就散架，而 Windows 路径带空格是常态。另外它的 JSON 也不适合手改。 | — | ✅ 完成 |
| 快捷键显示串 | ❌ **不用 ChefKeys**：WPF 自带的 `KeyGestureConverter` 实测可无损往返（`win+alt+space` ↔ `Alt+Windows+Space`），零依赖 | — | ✅ 完成 |
| XAML 行为 | **Microsoft.Xaml.Behaviors.Wpf** | MIT | M2 |
| 图标网格虚拟化 | **VirtualizingWrapPanel** | MIT | M3+ |
| Markdown 预览 | **MdXaml** | MIT | M3+ |
| 压缩包内预览 | **SharpZipLib** | MIT | 后续 |
| 计划任务式自启 | **TaskScheduler**（比注册表 Run 键规范） | MIT | M2 |

---

## 2. 不能碰的（许可证陷阱）

| 东西 | 问题 | 结论 |
|---|---|---|
| `refs/windhawk-mods`（共 603 个 mod） | 仓库无 LICENSE 文件，但 README 声明"未指定许可证的 mod 按 MIT 提交"。实测：**470 个未声明 → 归 MIT**；133 个显式声明，其中 **34 个 GPL-3.0 系 + 1 个 CC-BY-NC-SA-4.0** | **必须逐 mod 查文件头 `@license` 字段**。GPL 有传染性、CC-BY-NC-SA 禁商用 —— 这 35 个**绝对不能抄**（详见 §2.1） |
| `refs/everythingsearchbox` | LICENSE / README / package.json **全无任何许可证声明** | 只能读行为思路（当前目录优先级、模板、降级 UX），**代码一行不抄**（与 REF-4 结论一致） |
| `everythingtoolbar/EverythingToolbar/ThirdParty/ShellContextMenu.cs` | 1555 行，**没有许可证头**；2008 年 CodeProject 代码（原作者 Andreas Johansson），且依赖 WinForms | **不能抄**。这是"MIT 仓库里的第三方文件 ≠ MIT"的典型陷阱 |
| `SharpVectors.Wpf` | **BSD-3-Clause**，超出你定的 MIT/Apache-2.0 范围（虽属宽松许可） | 要用得你点头 |
| `InputSimulator` | 只有 .NET Framework 版本（NU1701），许可证是 codeplex 链接（疑似 MS-PL） | 不建议 |
| `Meziantou.Framework.Win32.Jobs` | 4.0.3 只支持 net10/net11，**与 net8 不兼容**（还原直接报 NU1202） | 不能用当前版本 |

### 2.1 windhawk-mods 的许可证分布（实测）

```
470 个 mod  未声明 @license   → README 规定归 MIT      ✅ 可用
 96 个       MIT                                       ✅ 可用
 26 个       GPL-3.0        ┐
  4 个       GPL-3.0-only   │ 34 个 copyleft          ❌ 绝不能抄
  3 个       GPL-3.0-or-later┘
  2 个       BSD-3-Clause                              ⚠️ 超出你定的范围
  1 个       LGPL-2.1-or-later                         ❌ 弱 copyleft，避免
  1 个       CC-BY-NC-SA-4.0                           ❌ 禁商用
```

M5（Windhawk 路线）本身是可选的阶段三工作；真要参考时**先看那个 mod 文件的
`// @license` 行**，未声明才按 MIT 对待。

---

## 3. 值得移植的源码（均在 MIT 仓库，且是仓库自研代码）

### 3.1 M2：Explorer 目录感知
`refs/flowlauncher/Flow.Launcher.Infrastructure/DialogJump/Models/WindowsExplorer.cs`

做法（约 100 行）：
1. `GetProcessNameFromHwnd(GetForegroundWindow())` 判断是否 `explorer.exe`
2. 枚举 **`IShellWindows` COM**，把 HWND 对到 `IWebBrowser2`
3. 读 `LocationURL` 得到当前目录

用的是 **CsWin32**（`Windows.Win32.*`），与我已有依赖一致。这是 `ShellContext`（Ctrl+F 限定目录）最省事的路径。

### 3.2 ~~M3：Windows 搜索索引~~ 【已作废：明确不做 Windows 索引】
> 决定见 MVP §G。以下保留为技术记录 —— 将来若要接**正文检索**后端，这套
> `scope='file:'` + `FREETEXT` + OleDb 保留字转义的做法仍有参考价值。
`refs/flowlauncher/Plugins/Flow.Launcher.Plugin.Explorer/Search/WindowsIndex/`
- `WindowsIndex.cs`（106）+ `QueryConstructor.cs`（146）+ `WindowsIndexSearchManager.cs`（122）= **374 行**
- 走 `Microsoft.Search.Interop` + `System.Data.OleDb` 查 `SystemIndex`
- 含保留字转义：`^[`\@\＠\#\＃\＊\^,\&\＆\/\\\$\%_;\[\]]+$`（OleDb 的坑）
- 依赖包 `tlbimp-Microsoft.Search.Interop` — **已核实 MIT**（作者 mamift）

**更稳的替代**：`refs/powertoys/src/modules/cmdpal/ext/Microsoft.CmdPal.Ext.Indexer/Indexer/`
用 `ISearchManager.GetCatalog("SystemIndex")` + `ISearchQueryHelper` 拿连接串和查询，
而不是硬编码 Provider 字符串。PowerToys 是微软自己的 MIT。

### 3.3 排序：补"首字母缩写"匹配
`refs/flowlauncher/Flow.Launcher.Infrastructure/StringMatcher.cs`（431 行）

我的 `NameMatcher` 已有 exact/prefix/word-boundary/substring/subsequence 五档，
但**缺 acronym 匹配**（"vs code" → "Visual Studio Code"）。
它还有可配置的 `SearchPrecisionScore` 和基于空格索引的 `CalculateSearchScore`。
→ 抄思路，实现自己写（我的分档结构更贴合当前 `Ranker`）。

### 3.4 图标与缩略图
- `refs/everythingtoolbar/EverythingToolbar/Icons/IconLoader.cs`（110 行）
  **架构值得照抄**：2 个工作线程、最多 128 积压、`WeakReference` 持有行、批量 flush 回 Dispatcher。
  我现在的 `ShellIconCache` 是**在 UI 线程同步调 `SHGetFileInfo`** 的，滚动时会卡。
- `refs/flowlauncher/Flow.Launcher.Infrastructure/Image/`（879 行）
  `ThumbnailReader.cs`（184）走 `IShellItemImageFactory` 拿真缩略图；`ImageLoader.cs`（544）是完整管线；
  `ImageCache.cs`（80）是 LRU（他们也用 BitFaster.Caching）。

---

## 4. 我已手写的东西：哪些其实在重复造轮子

诚实评估，不护短：

| 我写的 | 评价 |
|---|---|
| Everything IPC 访问 | ✅ 已用 `EverythingSearchClient`，**没重复造** |
| 主题/控件 | ✅ 已用 `WPF-UI` |
| MVVM / DI | ✅ 已用 `CommunityToolkit.Mvvm` / `Microsoft.Extensions.DependencyInjection` |
| `Ranker` / `QueryParser` / `FileUsageStore` | ✅ 这三个**没有现成轮子**（各家都是自研），自写合理 |
| `WindowsProcessLauncher` | ✅ 自写合理。`ShellExecuteEx` + `SEE_MASK_INVOKEIDLIST` 那段是必须自己写的（`Process.Start` 做不到） |
| `ShellIconCache` | ⚠️ **半重复**。没有干净轮子（WindowsAPICodePack 已停更且许可证混乱），自写是对的；但**同步加载架构该换**成 ET 的后台池，并补 `IShellItemImageFactory` 缩略图 |
| `NameMatcher` | ⚠️ **半重复**。分档逻辑要自写，但 acronym 该补 |
| 右键菜单（7 项自研） | ⚠️ **可升级**。现在是宿主动作，缺系统动词（7-Zip / Git / "打开方式" / "发送到"）。用 Vanara + 自写托管可以拿到**真 shell 菜单** |

### 关于真·shell 右键菜单

`Vanara.PInvoke.Shell32` 5.0.7（MIT，**支持 net8.0-windows**）经反射确认提供：
`IContextMenu` / `IContextMenu2` / `IContextMenu3` / `IContextMenuCB`，
含 `QueryContextMenu` / `InvokeCommand` / `GetCommandString`；以及
`SHCreateItemFromParsingName`、`SHBindToObject`。

仍需自写的部分（约 150 行）：`QueryContextMenu` → `TrackPopupMenuEx` →
转发 `WM_DRAWITEM`/`WM_MEASUREITEM` 给 `IContextMenu2/3`（否则"发送到"子菜单和图标不显示）。
**不要**去抄 ET 那个无许可证的 `ThirdParty/ShellContextMenu.cs`。

---

## 5. 建议执行顺序

1. **M2 立即用 NHotkey.Wpf** —— 手写 `RegisterHotKey` + 隐藏消息窗口是纯浪费，且容易漏热键冲突处理
2. **移植 `WindowsExplorer.cs`** 把 `ShellContext` 打通（Ctrl+F 限定目录）
3. **图标加载改后台池**（照 ET `IconLoader` 架构）+ BitFaster LRU —— 直接决定滚动流畅度
4. **右键菜单升级为真 shell 菜单**（Vanara）—— 价值高，但要处理 owner-draw 消息转发
5. ~~M3 Windows 索引~~ → **已撤销**（Everything 已覆盖）
6. **M4 Zotero**：`Microsoft.Data.Sqlite` 直读 `zotero.sqlite`

---

## 6. 核实方法（可复现）

许可证不能靠印象。做法是还原到临时工程再读包内文件：

```powershell
mkdir /tmp/licprobe; cd /tmp/licprobe
# PackageReference Include="X" Version="*"，注意 ManagePackageVersionsCentrally=false
dotnet restore
# 读 nuspec 的 license 字段
grep -o '<license[^>]*>[^<]*' ~/.nuget/packages/<id>/<ver>/<id>.nuspec
# 若 nuspec 只写 "LICENSE"（内嵌文件），读包内 license 文件
head -4 ~/.nuget/packages/<id>/<ver>/license
```

查**类型是否存在**必须用反射，不能用 `strings` 或 grep XML 猜 —— 嵌套类型名带 `+`，
且 XML 文档的 TFM 目录与实际 lib 不一致，两种方式都会给出假阴性
（`IContextMenu` 就是这样被误判为"不存在"的）：

```csharp
var asm = Assembly.Load("Vanara.PInvoke.Shell32");
foreach (var t in asm.GetExportedTypes().Where(t => t.Name.Contains("ContextMenu")))
    Console.WriteLine(t.FullName);
```

---

## 7. 与既有调研文档的关系

本文只回答"**有什么现成轮子可以直接用**"。架构层面的经验教训
（IPC 协议、目录语义、能力协商、Windhawk 可行性等）见：
`REF-1-everythingtoolbar.md`、`REF-2-flow-launcher.md`、`REF-3-windhawk-mods.md`、
`REF-4-everythingsearchbox.md`、`REF-5-powertoys-cmdpal.md`、`REF-6-everythingsearchclient.md`、
`EVERYTHING-IPC-VERIFIED.md`、`UPSTREAM-AUDIT.md`。
