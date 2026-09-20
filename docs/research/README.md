# 研究与复用索引（本目录）

> 规矩：**每个参考仓库一份中文文档**，内容是「它做了什么 / 与我们目标的关联 / 哪些可学 / 哪些必须避坑」，
> 并且**给出文件路径指针**而不是把代码抄进文档 —— 真到开发要用时按指针回仓库细读。

| 文档 | 对象 | 许可证 | 一句话结论 |
|---|---|---|---|
| `EVERYTHING-IPC-VERIFIED.md` | voidtools 官方 IPC/搜索语法 | MIT(头文件) | **协议唯一事实来源**；纠正了社区与模型记忆里一批**根本不存在**的 `EN_*` 常量与 `folderparent:`/`pic:` 语法 |
| `UPSTREAM-AUDIT.md` | 五仓库 + 两包总审 | 见文内表 | 许可证尽调 + 本机环境实测（NuGet 可用、无 MSVC）+ "能不能当基座"的判断 |
| `REF-1-everythingtoolbar.md` | srwi/EverythingToolbar | MIT（`ThirdParty/ShellContextMenu.cs` **无声明**，勿抄） | 不 fork：结果模型是文件专用；但**图标探测、高亮、动作表、Mica、接管输入的状态机**都该移植 |
| `REF-2-flow-launcher.md` | Flow-Launcher | MIT | 两件宝贝：**DialogJump**（不注入取当前 Explorer 目录/标签页）与 **Explorer 插件的 IProvider 三层 + Windows 索引检索**；另附 MRU 公式 `5×同查询 + 1×全局` |
| `REF-3-windhawk-mods.md` | windhawk-mods | **未声明者按仓库规则视为 MIT** | 阶段三专用；记录了 `ExplorerFrame.dll` 符号 hook、`TravelBand`+`RB_SHOWBAND` 隐藏搜索栏、`WaitForModifierKeysReleased` 重放键的坑 |
| `REF-4-everythingsearchbox.md` | HamzaETTH/EverythingSearchBox | ❌ **无许可证** | 行为最有参考价值（当前目录优先级、`{path}/{query}` 模板、三级降级 UX），**代码一行不抄** |
| `REF-5-powertoys-cmdpal.md` | PowerToys `src/modules/cmdpal` | MIT | 白送 SystemIndex 的托管 COM 互操作 + **AQS 直通规则**（已发现本项目的真实缺陷）+ 动作词汇表；`Section` 次序语义我们有意不采用 |
| `REF-6-everythingsearchclient.md` | NuGet `EverythingSearchClient` 0.9.0.148 | Apache-2.0 | **v1 的 Everything 传输层**：纯托管、已实现 QUERY2（含 Size/Date/Attributes）；列出了它的 6 个边界与隔离策略 |
| `REUSE-CANDIDATES.md` | 上列全部 REF 的**轮子清单** | 逐个实地核实 | **该直接用的别手写**：全局热键(NHotkey.Wpf)、托盘(NativeTray)、拖出(Droplex)、真 shell 菜单(Vanara `IContextMenu`)、Windows 索引(FL `WindowsIndex/`)、缩略图管线、Zotero(Microsoft.Data.Sqlite)。含许可证陷阱（windhawk 34 个 GPL mod、ET 无证 ShellContextMenu、SharpVectors BSD-3）与"哪些我手写其实在重复造轮子"的诚实评估 |

## 由审计直接导致的改动（已落地/待落地）

| # | 改动 | 状态 |
|---|---|---|
| 1 | 放弃自写 Everything `EN_*` IPC（常量为虚构） → 改用 `EverythingSearchClient`，`EverythingQueryTranslator` 镜像自有枚举 | ✅ 已编译通过 |
| 2 | 查询语法：**看起来结构化就原样透传给后端**，不再一律拆解重排（`kind:folder`、`content:`、`(a|b)`、`C:\Users`…） | ⬜ 待改 `QueryParser` |
| 3 | `SearchResult` 补 `CopyText` / `AutoCompleteText` / `RecordKey` / `AddSelectedCount` / `PreviewVisibility` | ⬜ 待改 Sdk |
| 4 | `IUsageStore` 双键（查询维度 5× + 全局 1×），文件实体优先用 Everything 的 `RUN_COUNT` | ⬜ 待改 Core |
| 5 | ~~新增内置 `WindowsIndexProvider`（OLE DB SystemIndex）~~ **已决定不做**：Everything 已覆盖文件名/路径检索，且它的索引质量与速度都更好；引入 SystemIndex 只会多一个"为什么这条没出来"的解释负担。见 `REUSE-CANDIDATES.md` 与 MVP §G | ❌ 撤销 |
| 6 | 分类**次序固定**在 `CategoryIds`，不接受 provider 决定分组（有意与 CmdPal 的 `Section` 分歧） | ✅ 设计如此 |
| 7 | UI 依赖 `WPF-UI`（MIT，ET 同款）而不是自造 Win11 主题 | ⬜ 待建 App |
| 8 | 动作集对齐 CmdPal 词汇表 + ET 的 `SearchResultActions`（含"动作后回写 run count"） | ⬜ 待改 |
| 9 | 预览不自研：接 QuickLook / Seer（ET 的 `FilePreviewerAdapter` 路线），或 `IPreviewHandler` | ⬜ 阶段二 |
| 10 | 阶段二用**非注入**路线（`RegisterHotKey` + ShellWindows COM），Windhawk 只留给阶段三 | ✅ 设计如此 |
