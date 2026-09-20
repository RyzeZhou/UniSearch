# 参考仓库 4：HamzaETTH/EverythingSearchBox

- 本地路径 `D:/tools/refs/everythingsearchbox`（单 commit，浅克隆）
- ⚠️ **许可证：无**。仓库没有 LICENSE 文件，README 无许可声明，源码文件头也没有授权语句
  （`AssemblyInfo.cs` 只有 `[assembly: AssemblyCopyright("Copyright (C)  2024/" )]`）。
  **公开可读 ≠ 已授权**：本项目只记录其行为与设计，**不复制其任何代码/资源**（含 `Resources/*.png` 的 Everything 图标，那是 voidtools 的资产）。
- 规模很小：`SearchBoxPlugin.cs`、`SearchBoxToolbarControl.cs`、`CustomFilterBox.cs`、`Localizer.cs`；.NET Framework 4.8；依赖 **QTTabBar**（`lib/QTPluginLib.dll`）

## 1. 它做了什么（行为规格）

给 QTTabBar 的工具栏加一个搜索框；回车后**把查询限定在当前标签页目录**交给 Everything：

1. 控件：`ToolStripControlHost` 包住一个自定义 `SearchBoxToolbarControl`（含内嵌图标、可配置 placeholder、深色模式配色）；
   `QuerySubmitted` 事件 → `RunEverythingSearch(query)`。
2. **当前目录解析优先级**（`GetCurrentDirectory()`）：
   `pluginServer.SelectedTab.Path` → `pluginServer.Path` → （再往下是空值分支）；
   取不到就 `MessageBox` 报错中止 —— 它没有回退到 Shell COM，因为它寄生在 QTTabBar 里。
3. **参数模板**：`BuildEverythingArguments(currentDirectory, query)`，用户可配置含 `{path}`、`{query}` 占位符的模板
   （README 给的默认形态就是 `-path "<path>" -s "<query>"`）。
4. **优先复用已运行实例**：`TrySendCommandLineToRunningEverything(arguments)` —— 成功即结束；
   失败才 `ResolveEverythingPath()`（自动探测 `Everything.exe` / `Everything64.exe`）→ `TryStartEverything(path, args)`；
   还不行 → `PromptForEverythingExecutable()` 让用户手选，然后**只再试一次**且要求路径不同（避免死循环）。

> 第 4 步与 `EVERYTHING_IPC_COPYDATA_COMMAND_LINE_UTF8`（dwData **0**，负载 `EVERYTHING_IPC_COMMAND_LINE{DWORD show_command; BYTE utf8 text[]}`）是同一件事 —— 见
> `docs/research/EVERYTHING-IPC-VERIFIED.md` 表 E。**这印证了"把命令行喂给已运行的 Everything"是官方支持的通道。**

## 2. 与本项目的关系

| 我们的设计 | 它的对照 | 结论 |
|---|---|---|
| `SearchContext.RootPath` 来源优先级（活动 tab → 窗口 → 桌面） | `SelectedTab.Path → pluginServer.Path` | **同样的优先级语义**，我们要再加一层"没有标签页框架时用 Shell COM"（见 REF-2 §2.1），因为它完全依赖 QTTabBar |
| 阶段三"原生搜索框点击接管" | 它是在工具栏**新加**一个框，不接管原生框 | 它回避了最难的部分；我们要做的正是它没做的那块（这也是本项目原创价值所在） |
| 不注入 explorer.exe | 靠 QTTabBar 注入 | 我们**不采用**它的寄生路线：QTTabBar 是重量级第三方依赖（且自身许可证/维护状况需要另查），不能要求用户为搜索装标签页框架 |
| Provider 无关 | 直接 `Process.Start("Everything.exe", args)` | 我们不用"拉 GUI 传命令行"作为主路径（那会把 UI 交给 Everything），但**保留为降级方案**：Everything 在跑、但 IPC 不可用时（例如权限级别不同）可以用它兜底 |
| 找不到后端的 UX | 自动探测 → 失败弹框让用户选 exe → 记住 | ✅ **借鉴这套三级降级**（探测 / 让用户选 / 记住），落到 `ProviderHealth.Hint` + 设置页 |

## 3. 可迁移到规格里的具体点

1. **`{path}` / `{query}` 模板**是好的可配置边界：UniSearch 的 Everything Provider 应允许"自定义附加参数"（例如默认 `regex:`、排除规则 `!ext:tmp`），并暴露给用户，而不是硬编码。
2. 复用已运行实例的意义不只是"不弹窗"，还包含**沿用用户当前实例（含 1.5 命名实例、多实例）** —— 对应 `EVERYTHING_IPC_WNDCLASS_(instance)`（见已核实文档表 A）。
3. 它把"取不到当前目录"当成硬错误弹框。我们应当**降级而不是弹框**：取不到就按 Global 搜并明确显示范围，避免打断输入流。
4. 深色模式/图标内嵌这类细节说明：**工具栏内嵌搜索框宽度很窄**（它的控件默认约 150–200px）。
   → UniSearch 的紧凑 UI 要有"极窄时只显图标 + 点击展开"的状态，不能假设宽度足够。

## 4. 不做的事

- 不复制它的代码与图标资源。
- 不引入 QTTabBar 依赖。
- 不把"启动 Everything GUI"当主路径（只当降级），否则分类聚合、正文命中、实体融合这些我们的核心价值全部落空。
