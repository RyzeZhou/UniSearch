# 开发日志

> 目的：把「做到哪了 / 怎么验证的 / 踩过什么坑 / 明天从哪继续」写清楚，
> 让下次接手（人或 agent）不必重新推导。**只记已验证的事实**，推断会明确标注。

---

## 2026-09-16 第 1 轮（目标 `goal-951e1b98`）

### 本轮目标

让原型达到可用，六件事：① 输入即搜 ② 按类型分化的菜单与预览 ③ 真 shell 右键菜单
④ 落地已核实许可证的复用项 ⑤ 直达后端程序的按钮 ⑥ 不实现 Windows 索引。

### 状态总览

| # | 事项 | 状态 | 验证方式 |
|---|---|---|---|
| ① | 输入即搜 | ✅ 之前已完成 | 集成测试 + 实机 |
| ② | 类型分化菜单 | ✅ 文件/文件夹/可执行 已分叉 | 自检逐项报告可见性 |
| ② | 预览窗格 | ✅ 图片/文本/PDF/文件夹/兜底 | `--selftest-preview` + 截图 |
| ③ | 真 shell 菜单 | 🟡 **管道已验证；交互路径待实测** | `--selftest-shellmenu` |
| ④ | 复用项落地 | 🟡 图标加载✅ / 热键、托盘、拖出、acronym 待做 | `--selftest-icons` |
| ⑤ | 直达后端按钮 | ✅ | `--selftest-backend` + 窗口标题核对 |
| ⑥ | 移除 Windows 索引 | ✅ | 文档已改，见下 |

**构建状态**：全解决方案 `0 错误 0 警告`；单元测试 **79/79 通过**。

---

### 1. 已完成并验证

#### ⑥ 移除 Windows 索引（Everything 已替代）

改了三处文档，避免后人再把 REF 里的旧结论当结论用：
- `docs/research/README.md` 第 5 条 → 标为「撤销」
- `docs/research/REF-2-flow-launcher.md` §2.2 结论、`REF-5-powertoys-cmdpal.md` §2 → 加作废说明
- `docs/spec/MVP.md` §G → 写明理由

#### ⑤ 直达后端程序

新增 SDK 能力接口 **`IExternalUiProvider`**（`ExternalUiName` / `CanOpenExternalUi` / `OpenExternalUi`）——
不是硬编码 Everything，而是通用能力，AnyTXT/Zotero 接上会自动多出按钮。

- `EverythingLocator.FindExecutable()`：**优先从 IPC 宿主窗口反查进程路径**
  （本机是便携安装 `D:\Everything\everything.exe`，注册表里什么都没有），注册表与常见路径仅作兜底。
- 命令行开关**实测**（不是照文档猜）：`everything.exe -search "<q>"` 会让运行中的实例把窗口标题
  变成 `<q> - Everything`，且复用同一进程。
- 验证：`--selftest-backend` 后窗口标题 = `transformer attention - Everything` ✅

#### ④ 图标后台加载（本轮最大的坑，见 §3.1 / §3.2）

照 EverythingToolbar 的 `IconLoader` 架构（MIT）重写，但**有一处不能照搬**：
ET 的列表是增量 diff，我们是整表重建，所以必须**按缓存键（扩展名）合并请求**。

最终实测：85 行全部拿到真图标，0 丢弃，0 失败。

#### ② 预览窗格

按类型分流（`PreviewService`）：

| 类型 | 做法 |
|---|---|
| 图片 | WPF 原生解码（`DecodePixelWidth=720`，避免全解大图） |
| 文本/代码 | 直读前 48KB，**NUL 字节做二进制检测**（比按扩展名猜可靠） |
| **PDF** | 系统自带 `Windows.Data.Pdf` 渲染首页 |
| 其余 | shell 缩略图 `IShellItemImageFactory`（`THUMBNAILONLY`） |
| 拿不到 | 如实显示「没有可用的预览」，不留空白 |

**为什么 PDF 要特殊处理**：实测本机**没有注册 PDF 缩略图处理器**，只走 shell 缩略图时
PDF 永远显示「没有可用的预览」——而 PDF 恰恰最需要预览。

验证（`--selftest-preview`，查询 `pdf`）：
```
图片: pdf-translate.png  -> Kind=Image
文本: pdf-worker.fixture.mjs -> Kind=Text (749 字)
PDF文档: preview.pdf -> Kind=Image · 第 1 页      ← 截图确认渲染出真实首页
其它: PdfBody.d.ts.map -> Kind=Unavailable「没有可用的预览」（正确）
文件夹: pdf -> Kind=Folder
```

布局：结果列表 `*` + `GridSplitter` + 预览窗格 320px，`Ctrl+J` 开合。

#### ③ 真 shell 菜单（管道已验证）

`Services/ShellContextMenu.cs`，用 **Vanara.PInvoke.Shell32 5.0.7**（MIT，支持 net8）——
不手写 COM 互操作。关键设计：

- **隐藏窗口当菜单属主**：`WM_DRAWITEM`/`WM_MEASUREITEM`/`WM_INITMENUPOPUP` 是发给
  **菜单属主窗口**的，所以建一个 `HwndSource` 收这些消息再转发给 `IContextMenu2/3`。
  不转发的话「发送到」子菜单会是空的。
- **用 `CMINVOKECOMMANDINFOEX.lpVerbW`（普通宽字符串）调用**，而不是
  `MAKEINTRESOURCE(offset)` —— 绕开 Vanara 的 `ResourceId` union，少一层易错的 marshalling。
- `TPM_RETURNCMD`：直接拿命令 id，不用等 `WM_COMMAND`。

验证（`--selftest-shellmenu`）：
```
文件 .mjs   -> 4 个动词: link, delete, rename, properties
文件夹 pdf  -> 7 个动词: link, delete, rename, properties, cut, copy, paste
可执行 .exe -> 4 个动词: link, delete, rename, properties
```

### 2. 未完成 / 明天从这里继续

| 优先级 | 事项 | 说明 |
|---|---|---|
| **高** | **实测右键** | 右键已改成：有真实路径 → 弹 shell 菜单；无路径 → 退回 WPF 菜单。**交互路径（TrackPopupMenuEx + InvokeCommand）尚未人工实测**，需要真的右键点几下 |
| **高** | **shell 菜单动词调用兜底** | 自检显示 `.mjs` 只报出 link/delete/rename/properties，**没有 "open"**。说明部分菜单项不实现 `GetCommandString(GCS_VERBW)`，按动词名调用会失败（状态条会报「该项没有可用的动词名」）。兜底方案：改回 `MAKEINTRESOURCE(offset)`，需要构造 EX 版的 `lpVerb`（`ResourceId`） |
| 中 | 全局热键 | 用 **NHotkey.Wpf**（Apache-2.0，ET 与 FlowLauncher 都在用），别手写 `RegisterHotKey` |
| 中 | 移植 `WindowsExplorer.cs` | FlowLauncher 用 `IShellWindows` COM 取 Explorer 当前目录，MIT，约 100 行，CsWin32 已有 |
| 中 | 托盘 + 拖出 | NativeTray（MIT）、Droplex（MIT） |
| 低 | acronym 匹配 | `NameMatcher` 缺首字母缩写（"vs code" → "Visual Studio Code"），抄 FlowLauncher `StringMatcher.cs` 思路 |
| 低 | 清理自检开关 | 现有 7 个 `--selftest-*`，可考虑合并成一个 |

### 3. 踩过的坑（按价值排序，都值得记住）

#### 3.1 `SHGetFileInfo` 不是线程安全的 ⭐

同一份查询，只改工作线程数：

| 工作线程 | 拿到真图标 |
|---|---|
| 1 | **85/85** |
| 2 | 82/85 |
| 4 | 72/85 |

每次失败的扩展名都不同（`.map`/`.jsonl`/`.dll` 轮着来），看起来像「这些类型本来就没图标」，
极易误判。**修法**：在 `ShellIconCache` 内部用锁串行化 —— 把不变量放在**需要它的那一层**，
而不是靠调用方自觉。现任意线程数都是 85/85。

#### 3.2 后台工作线程绝不能因单个任务失败而退出 ⭐

`WorkerAsync` 最初只捕获 `OperationCanceledException`。一个未捕获异常带走线程后，
**队列永久停滞**，计数冻在「排队 25 / 完成 17」不动 —— 看起来像卡死，实际是线程已死。
现在每个任务单独 try/catch 并记账。

#### 3.3 `SHBindToParent` 返回的 childPidl 不能释放 ⭐⭐

它是**指向父 PIDL 内部的指针**，不是独立分配的内存。对它调 `FreeCoTaskMem` 会破坏堆，
进程在**原生层 fail-fast** —— 托管 `catch` 抓不到，表现为**无声退出、日志里什么都没有**。
只释放父 PIDL 即可。

#### 3.4 目标框架一变，输出目录就变（验证陷阱）

为 PDF 预览把宿主抬到 `net8.0-windows10.0.19041.0` 后，输出目录从
`bin/Debug/net8.0-windows/` 变成 `bin/Debug/net8.0-windows10.0.19041.0/`，
而启动命令还指向旧目录 —— **一直在跑陈旧 exe**，导致「预览改动全部没生效」，查了很久。
**修法**：`<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>`。

#### 3.5 吞异常的层必须留诊断通道

`ShellIconCache.ResolveNow` / `PreviewService` 为了「失败要降级而不是崩」会吞掉异常。
结果是排查时只能看到「没有图标 / 没有预览」，看不到原因。
现在两者都记录失败原因（`Failures` / `LastFailure`），`PreviewService.Trace` 直连 host.log。

#### 3.6 Everything 的排除运算符是 `!`，不是 `-`

实测：`pdf -fixture` 返回 2 条名字里含 `-fixture` 的文件（**`-xxx` 是字面文本**），
`pdf !fixture` 才排除。写成 `-` 会静默返回 0 条。

#### 3.7 `HostLog` 持有文件句柄，`rm` 会静默失败

日志用 `File.AppendText` 且进程全程持有，上一实例没完全退出时 `rm -f` 失败，
于是读到**上一次运行的残留内容**，看起来像「改动没生效」。删日志前先确认进程已退出。

#### 3.8 查类型存不存在必须用反射

`strings` 和 grep XML 都给出过假阴性（嵌套类型名带 `+`、XML 文档的 TFM 目录与 lib 不一致）。
`IContextMenu` 就这样被误判为「不存在」。Vanara 的多数类型是**嵌套在 `Shell32` 类里**的
（`Shell32.IContextMenu`、`Shell32.SIIGBF`、`User32.TrackPopupMenuFlags`）。

### 4. 验证手段（可复现）

```powershell
# 构建与测试
dotnet build -c Debug                     # 应为 0 错误 0 警告
dotnet test  -c Debug                     # 应为 79/79

# 分层探针（绕开 WPF，直接打数据链路）
dotnet run --project tools/Probe -- "pdf"          # Parser→Translator→IPC→Broker 全链路
dotnet run --project tools/Probe -- --raw 'pdf !ext:mjs'   # 直连 Everything 试语法
dotnet run --project tools/Probe -- --meta         # 反射出客户端支持的排序/字段

# UI 自检（每个都会写 host.log 后退出）
$exe = "src\UniSearch.Host\bin\Debug\UniSearch.exe"      # 注意：路径不含 TFM
& $exe --query pdf --selftest-icons        # 图标是否刷回行上（含缓存/合并/失败计数）
& $exe --query pdf --selftest-preview      # 图片/文本/PDF/文件夹各取一次预览
& $exe --query pdf --selftest-shellmenu    # shell 菜单 COM 管道 + 系统动词表
& $exe --query pdf --selftest-menu out     # 右键菜单逐项可见性（文件 vs 文件夹）
& $exe --query pdf --selftest-props        # 属性对话框（文件 + 文件夹）
& $exe --query pdf --selftest-open         # 真执行一次"打开目录"
& $exe --query pdf --selftest-backend      # 触发"直达 Everything"

# 环境变量
$env:UNISEARCH_QUERY = "pdf"               # 等价于 --query
$env:UNISEARCH_ICON_WORKERS = 1            # 图标工作线程数（排查 Shell 并发问题用）

# 抓图（PrintWindow，无视遮挡与前台锁）
powershell -File tools/capture-ui.ps1 -Out ui.png

# 日志
Get-Content "$env:LOCALAPPDATA\UniSearch\host.log" -Tail 40
```

### 5. 许可证纪律（本轮新增核实）

| 包 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| Vanara.PInvoke.Shell32 | 5.0.7 | **MIT**（已核实，支持 net8） | shell COM：缩略图 + 右键菜单 |
| tlbimp-Microsoft.Search.Interop | — | MIT | ~~Windows 索引~~ 已撤销 |

**不能碰**：`refs/everythingsearchbox`（全无许可证）、
`everythingtoolbar/ThirdParty/ShellContextMenu.cs`（1555 行、无许可证头、2008 年 CodeProject 代码）、
`SharpVectors.Wpf`（BSD-3，超出 MIT/Apache 范围）、
windhawk-mods 里 **34 个 GPL + 1 个 CC-BY-NC-SA** 的 mod。
详见 `docs/research/REUSE-CANDIDATES.md`。

### 6. 目标框架约束（重要）

宿主是 **`net8.0-windows10.0.19041.0`**，不是 `net8.0-windows` —— `Windows.Data.Pdf` 需要
Windows SDK 投影。同时 `<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>`
保证输出路径稳定。改动 TFM 前先想清楚这两点。

---

## 2026-09-16 第 2 轮（用户第一次亲手试用 → 实测揪出 5 个 bug，全部修复并验证）

### 本轮目标

把程序交给用户实际操作。用户反馈四件事：`Ctrl+1..9`/`Ctrl+0` 无效、`Ctrl+J` 无效、
右键菜单"又犯了不区分文件类型的问题"、两个热键都无效。

这四条**全部是真实 bug**，而且前两个里藏着两个致命缺陷。

### 状态总览

| 事项 | 状态 | 验证方式 |
|---|---|---|
| 热键一按就崩进程 | ✅ 已修 | 合成 Ctrl+F，进程存活 |
| 热键不唤出窗口 | ✅ 已修 | 最小化后按 Ctrl+F → 回前台且还原 |
| Explorer 目录感知 | ✅ **首次跑通** | 日志 `Ctrl+F：Explorer 目录 C:\Users\zhou\.dsh` |
| Ctrl+J / Ctrl+数字 | ✅ 已修 | `--selftest-keys` |
| Alt+Enter 属性 | ✅ 已修（此前从未生效过） | `--selftest-keys` 的 NormalizeKey |
| 右键附加项不区分类型 | ✅ 已修 | `--selftest-keys` 转储三种类型 |
| 空查询显示"没有结果" | ✅ 文案已改 | 代码 |
| 关闭窗口后再也呼不出来 | ✅ 已修（用户实测第 6 个 bug） | 合成 WM_CLOSE + Ctrl+F |
| 齿轮/电源图标只画出一半 | ✅ 已修（改之前就存在） | PrintWindow 真实窗口抓图 |
| 多选文件后右键 | ❌ 未做 | 设计待定，见下 |

### 五个 bug 的根因（都值得记住）

1. **`GlobalHotkeys` 里 `_trace = m => _trace(m)` 是自我递归。**
   字段赋值的 lambda 又去调用这个字段自己。任何热键一按下 → 无限递归 →
   `StackOverflowException`。这个异常**不可捕获**，进程瞬间消失、`host.log` 一个字都不留 ——
   症状和"热键没反应"**一模一样**，所以差点被当成注册失败。
   修法：lambda 捕获构造参数 `trace`，不再引用字段；热键回调整体包 try/catch。

2. **热键只 `SetContext` 不 `Show`** —— `GlobalHotkeys` 收了 `getWindow` 却从没用过。
   按热键的实际效果是"后台重跑了一次搜索"，窗口还藏在别的程序后面。
   修法：新增 `MainWindow.Summon()`（`WindowSummoner.BringToFront` + 聚焦并全选搜索框），
   App 的 `showWith` 改成"先 Summon 再 SetContext"；`getWindow` 参数直接删（本来就没用）。

3. **快捷键只挂在 `SearchBox` 上** —— 结果行、分类标签、预览窗格都不可聚焦，
   鼠标点一下就离开输入框，`PreviewKeyDown` 再也不触发。
   实测症状正是"点过分类标签之后 Ctrl+J 和 Ctrl+1..9 全没反应"。
   修法：挂到 `Window` 上；并把分发抽成 `MainWindow.HandleShortcut(key, mods) -> bool`，
   返回 false 的按键继续流向输入框，所以正常打字不受影响。

4. **`Alt+Enter`（属性）从来没有生效过** —— WPF 把 Alt 组合报成 `e.Key == Key.System`，
   真正的键在 `e.SystemKey`，`case Key.Enter when alt` 永远匹配不上。
   修法：`NormalizeKey(e.Key, e.SystemKey)`。**这类 bug 人工点不出来**，只有自检能抓。

5. **shell 菜单附加项写死 4 项、与类型无关** —— 文件夹也显示"打开所在目录""复制文件名"。
   更根本的问题：因为所有结果都带真实 path，`MainWindow` **永远**走 shell 菜单分支，
   那份带类型分叉的 WPF `ContextMenu` 直接成了死代码。
   修法：抽出纯函数 `SearchSessionViewModel.BuildShellMenuItems(isFolder, runnable, backendName)`。

顺带修的：`Dispose()` 注销名写成 `unisearch.global`（实际注册的是 `unisearch.summon`，等于从没注销过）；
`Ctrl+C` 在搜索框有选中文本时会把"复制选中文字"抢成"复制路径"（现在放行）；
数字键盘 `Ctrl+NumPad0..9`；窗口 `Activated` 时自动把焦点送回搜索框。

### 补充：用户实测又揪出第 6 个 bug ——"关掉之后热键再也呼不出来"

**根因**：`ShutdownMode=OnExplicitShutdown` 让窗口关掉后进程继续活着，
但**窗口对象本身被销毁了**。热键唤出对已关闭的窗口调 `Show()`，每次都抛：

```
INF [hotkey] unisearch.summon 热键处理失败：InvalidOperationException:
            关闭窗口后，无法设置可见性，也无法调用 Show、ShowDialogor 或 WindowInteropHelper.EnsureHandle。
```

进程活着、热键也确实在触发、日志有记录，但屏幕上什么都不发生 —— 用户看到的正是"呼不出来"。
（第 1 轮我把这条标成"没擅自改"，现在证明**必须改**。）

**修法**：

1. `MainWindow.OnWindowClosing`：不是真退出就 `e.Cancel = true` + `Hide()` ——
   关闭 = 收进后台，窗口对象保活，热键永远唤得回来。
2. 新增 `App.IsQuitting` / `App.Quit()`；App 里 11 处 `Shutdown()` **全部改走 `Quit()`**，
   否则 `Closing` 的 cancel 会把真退出也一起拦掉。
3. **必须同时给一个真退出入口**，否则就变成"关不掉、也退不了"：
   `Ctrl+Q` + 工具条最右边一个电源按钮（tooltip 写明 Ctrl+Q），F12 帮助文本也写上。
   托盘图标做好之前，这个按钮就是唯一可发现的退出路径。

**验证**（合成 WM_CLOSE 模拟点 X，再合成 Ctrl+F 唤出）：

```
step1-visible-at-start=True              窗口本来可见
step2-process-alive-after-close=True     关掉后进程还在
step2-window-visible=False               窗口已隐藏（没被销毁）
step3-window-visible=True                按 Ctrl+F 后回来了
step3-isForeground=True                  而且在最前面
失败计数 = 0
```

`Ctrl+Q` 也实测过：合成按键后 `processStillAlive=False`。

### 补充：顺手修掉的图标裁切 bug（改之前就存在）

齿轮图标一直是坏的 —— 只画出左半边，看着像缺字形。

**根因不是字体**：`FontFamily` 是 `"Segoe Fluent Icons, Segoe MDL2 Assets"`，
但这台机器**没装 Segoe Fluent Icons**（只有 MDL2），所以回退本身是对的；
`\uE713` / `\uE7E8` 在 MDL2 里都是正常字形（GDI+ 与 WPF 各自离屏渲染都验证过，
`\uE7E8` 就是电源符号）。

真正的原因是**按钮没设 `Padding`**，于是继承了 WPF-UI 隐式 `Button` 样式的**大内边距**；
而这两个按钮又写死了 `Width="30"` → 内容区被挤到只剩几像素 →
**字形被横向裁掉一半**。Everything 按钮因为显式写了 `Padding="6,0"` 且宽度自适应，所以一直正常。

修法：给两个图标按钮都加 `Padding="0"`。**任何固定宽度的图标按钮都必须显式写 `Padding="0"`。**

**踩坑提醒**：`--dump-render`（离屏 `RenderTargetBitmap`）输出的是 96 DPI 的 760x520，
而真实窗口在 175% 缩放下是 1330x910 —— **离屏渲染会丢缩放**。
看小图标这类细节必须用 `tools/capture-ui.ps1`（PrintWindow）抓真实窗口，否则会被误导。

> **2026-09-20 已修**（见第 9 轮）：`RenderDump` 现在按显示器实际缩放（`VisualTreeHelper.GetDpi`）
> 渲染，`--dump-render` 的输出与用户屏幕 1:1（175% 下 2240x1155）。上面这条提醒只在
> 需要"抓真实窗口"（遮挡、弹层、非 WPF 合成）时才还需要 `capture-ui.ps1`。

### 未完成 / 下次从这里继续

- **多选文件后右键**（用户明确提问）：当前是单选模型（只有一个 `Selected`），
  `ShellContextMenu.Show` 也只吃单个 path。要做多选需要：
  ① UI 支持 Ctrl/Shift 点选；② `Show` 改传 PIDL 数组
  （`GetUIObjectOf(hwnd, N, pidls[], ...)`，`IContextMenu.InvokeCommand` 原生支持多选）；
  ③ 附加项改成"复制 3 个路径"这类口径。
- 图片/文档/代码的分类专属右键项（现在只分了 文件夹/文件/可执行 三档）。
- 托盘图标、拖出文件、拼音首字母。
- `MainWindow` 里那份 WPF `ContextMenu` 别删 —— 它是"没有真实 path 的结果"
  （将来的 Zotero 条目、书签）的退路。
- **退出方式**：`ShutdownMode=OnExplicitShutdown` 但窗口能被 X 关掉 → 关掉窗口 = 热键一起失效。
  加托盘图标之前**不要**改成"关闭即隐藏"，否则用户没有退出入口（只能任务管理器）。

### 新踩的坑

- **解决方案级 `dotnet build -c Debug` 会漏报 Host 项目的编译错误。**
  它报"0 个错误 / 已成功生成"，但 `UniSearch.Host` 其实压根没编译
  （`UniSearch.dll` 时间戳不变）。必须单独 build `UniSearch.Host.csproj` 并**核对 dll 时间戳**。
  这次就是靠这个才没把 `WindowSummoner` 找不到 using 的错误放过去。
- **运行中的实例会锁住 `bin\Debug\*.dll`** → 编译报 MSB3027/MSB3021。
  先 `taskkill //IM UniSearch.exe //F`。
- **Windows PowerShell 5.1 用 GBK 读 `.ps1`**，脚本里的中文会把字符串引号搞断
  （报 "The string is missing the terminator"）。探测脚本一律写**纯 ASCII**。
- **`SetForegroundWindow` / `SwitchToThisWindow` 从 PowerShell 抢不到前台**（前台锁），
  但**我们自己的 `Ctrl+F` 热键能可靠唤出** —— 自动化就用它当"聚焦手段"。
- **Ctrl+F 唤出会顺带把范围限定到 Explorer 当前目录**；若那目录没有匹配结果，
  后续所有依赖"有结果"的测试（切分类、预览、属性）会全部空转、还看不出来。
  要测这些用 `Alt+Win+Space`，或先让窗口自己是前台再按 Ctrl+F（走全局分支）。

### 验证手段变化

新增 `--selftest-keys`（配合 `--query` 用）。
**合成键盘在 DSH 环境里不可靠**（UIPI + 前台锁，试过 SendKeys 与 keybd_event，
按键要么收不到要么发给别的窗口，结论是假的），所以把快捷键分发抽成
`MainWindow.HandleShortcut(Key, ModifierKeys) -> bool` 直接调，结论确定。
这个开关同时充当"右键附加项类型分叉"的回归测试。

```bash
# 一键回归：快捷键 + 菜单附加项 + 分类标签
src/UniSearch.Host/bin/Debug/UniSearch.exe --query pdf --selftest-keys
grep -a selftest "$LOCALAPPDATA/UniSearch/host.log"

# 热键真实验证（合成 Ctrl+F；不依赖 UIPI 放行 SendKeys）
# 脚本样例见第 2 轮记录：Ctrl↓ F↓ F↑ Ctrl↑，然后核对 GetForegroundWindow 与 host.log
```

实测日志（2026-09-16 11:16 与 11:18）：

```
INF [selftest] 分类标签 = [all, files, folders, documents, images, apps, code]（共 7 个）
INF [selftest] Ctrl+J -> handled=True IsPreviewOpen True -> False
INF [selftest] Ctrl+3 -> handled=True SelectedTabId=documents（期望 documents）
INF [selftest] Ctrl+NumPad2 -> handled=True SelectedTabId=folders（期望 folders）
INF [selftest] Ctrl+0 -> handled=True SelectedTabId=all
INF [selftest] Ctrl+9（越界）-> handled=True SelectedTabId=all
INF [selftest] NormalizeKey(System, Enter) = Return（期望 Enter）
INF [selftest] 裸按 A -> handled=False（期望 False，放行给输入框）
INF [selftest] shell 菜单附加项[文件夹]（5 个）：在终端中打开 / 在父目录中显示 / 复制路径 / 复制文件夹名 / 在 Everything 中搜索
INF [selftest] shell 菜单附加项[普通文件]（5 个）：打开所在目录 / 在终端中打开 / 复制路径 / 复制文件名 / 在 Everything 中搜索
INF [selftest] shell 菜单附加项[可执行文件]（6 个）：…上同… / 以管理员身份运行 [0x8006] / 在 Everything 中搜索
INF [hotkey] Ctrl+F：Explorer 目录 C:\Users\zhou\.dsh      ← 这条链路第一次跑通
INF [hotkey] Ctrl+F：前台不是 Explorer，退回全局              ← 全局分支也验证过
```

### 仍然只能靠人工的验证

- 真 shell 菜单弹出后**点某一项**（脚本可核对"按动词调用"能弹属性框，但点不了菜单项本身）
- 右键菜单里系统动词与宿主附加项**混合显示**的观感
- 多选、拖放

---

## 2026-09-16 第 3 轮（托盘常驻 + 右键退出）

### 本轮目标

用户要求："先做成托盘图标，然后右键托盘退出，而不是按 Ctrl+Q。"
背景：第 2 轮把"关闭窗口"改成收进后台之后，退出入口只剩 `Ctrl+Q` 和工具条上一个临时电源按钮 ——
启动器没有托盘本来就是残缺的。

用户同时给了后续方向（**尚未开始**）："如果轻量、占用资源少，最佳的方式是常驻服务，
真正替代 Explorer 自带的搜索功能。"

### 状态总览

| 事项 | 状态 | 验证方式 |
|---|---|---|
| 应用图标（.ico，9 尺寸） | ✅ 新增 | `tools/make-icon.ps1` + GDI+/肉眼复核 |
| 托盘图标常驻 | ✅ 完成 | 抓任务栏溢出面板，图标在 |
| 右键托盘菜单 | ✅ 完成 | 合成右键 + 截屏，四项齐全 |
| **右键 → 退出** | ✅ 完成 | `--selftest-tray "退出 UniSearch"` → 进程真的退出 |
| 工具条临时电源按钮 | ✅ 移除（托盘取代） | 代码 |
| 首次"关闭即隐藏"气泡提示 | ✅ 完成 | 代码 |
| 常驻服务（替代 Explorer 搜索） | ❌ 未开始 | 用户提出的后续方向 |

### 已完成并验证

- **依赖**：`NativeTray 2.2.3`（**MIT**，本地缓存已有，ET 同款）。
  刻意避开 `Hardcodet.NotifyIcon.Wpf` —— 它是 CPOL，不在项目允许的 MIT/Apache-2.0 范围内。
- **应用图标**：仓库里原先**一个 .ico 都没有**，程序顶着 .NET 默认图标 —— 而托盘图标就是它的脸。
  `tools/make-icon.ps1` 用 WPF 渲染"蓝色圆角方块 + 白色放大镜"，输出 9 个尺寸的多帧 .ico，
  自己画的所以许可证干净。同时设为 `ApplicationIcon`（exe/任务栏/Alt-Tab）。
- **`Services/TrayPresence.cs`**：托盘宿主 + 菜单（显示 / 隐藏 / 设置… / 退出），
  左键单击唤出，首次隐藏弹一次气泡。菜单动作统一经 `Dispatcher` 回 UI 线程。
- **退出入口**：菜单里的"退出 UniSearch"绑的是 `App.Quit`（不是 `Application.Shutdown`）——
  窗口的 `Closing` 会取消关闭请求，不先置位就退不掉。工具条上那个临时电源按钮已移除。

### 验证证据（都是可复现的脚本，不是肉眼点一次）

```
# 1) 托盘建起来了 + 菜单齐全
> UniSearch.exe --query pdf --selftest-tray
INF [tray] 托盘图标已就绪（右键 = 显示/隐藏/设置/退出）
INF [selftest] 托盘 Ok=True 有图标=True 错误=[(无)]
INF [selftest] 托盘菜单 = [显示 UniSearch | 隐藏窗口 | 设置… | 退出 UniSearch]

# 2) 右键 → 退出 真的会退出（模拟点那一下）
> UniSearch.exe --query pdf --selftest-tray "退出 UniSearch"
INF [selftest] 模拟点击托盘菜单项「退出 UniSearch」…
INF [selftest] 菜单项「退出 UniSearch」已执行
（tasklist 里 UniSearch 消失）

# 3) 图标真的出现在托盘里：抓 Shell_TrayWnd → 点溢出箭头 → 抓屏
#    溢出面板里能看到蓝色放大镜图标
# 4) 右键那个图标 → 截屏确认菜单：显示 UniSearch / 隐藏窗口 / 设置… / 退出 UniSearch
```

`--selftest-tray [菜单标题]` 这个钩子存在的意义就是第 2 条：**合成鼠标点不了托盘菜单**
（UIPI），"右键 → 退出"这条链路必须能被脚本证伪，不能只靠人点一次。

### 新踩的坑（这次集中踩在 PowerShell 与图标格式上）

1. **PowerShell 5.1 按 ANSI/GBK 读没有 BOM 的 .ps1** → 脚本里的中文注释会把解析器搞乱，
   报出莫名其妙的 `Unexpected token ')'`，而且**行号是错的**（报第 6 行，但第 6 行只是一个 `#`）。
   排查顺序应该是：先 `head -c 3 | xxd` 看有没有 `efbbbf`。
   **`.ps1` 里的中文必须配 UTF-8 BOM**；用 `sed -i` / 普通写文件都可能把 BOM 弄丢，改完要复查。
2. **`return $bytes` 会被 PowerShell 展开成一个个元素**，调用方拿到的不是 `byte[]`。
   要写 `return ,$bytes`。症状：9 帧的 ico 只有 159 字节。
3. **`$x = if (...) { ... } else { ... }` 会把管道输出再收集一层**，
   `byte[]` 被包成 `object[]`。要改成两条 `if/else` 直接赋值。
4. **ICO 里只放 PNG 帧，GDI+ 读不了**：`new System.Drawing.Icon(path, 256, 256).ToBitmap()`
   直接抛 `Requested range extends past the end of the array`。
   Windows 外壳能显示，但别的库/工具拿到我们的图标会失败。
   所以 **≤64 的尺寸写成 BMP/DIB 帧**（含 32bpp XOR + 1bpp AND 掩码，注意反预乘 alpha 与自下而上），
   只有 128/256 用 PNG。行业惯例也是这么做的。
5. **新注册的托盘图标默认被折叠进溢出区**，任务栏可见区里看不到 —— 这不是失败。
   而且 **`HKCU\Control Panel\NotifyIconSettings` 不能用来判断**
   （这台机器上这个键压根不存在）。要确认就得点开溢出面板抓图。
6. 托盘菜单项一旦 `Header` 改动，`--selftest-tray "标题"` 里的字符串也要同步改。

### 未完成 / 下次从这里继续

- **托盘图标默认在溢出区**：需要用户自己去「任务栏设置 → 选择哪些图标显示在任务栏上」
  把 UniSearch 打开。这是 Windows 的行为，程序改不了（除非去写注册表，属于侵入性操作，暂不做）。
- **常驻服务（用户提出的方向）**：真正替代 Explorer 自带搜索，形态上应该是
  独立进程/服务 + IPC，宿主界面只做前端。开始之前要想清楚：
  ① 索引与提供者的生命周期怎么脱离 UI 进程；② 与 Explorer 的集成点（搜索框接管？快捷键？）；
  ③ 单实例、开机自启、升级时如何不打断常驻进程。**目前完全没有开始，也没有设计。**
- 托盘菜单还可以加：最近搜索、开机自启开关、暂停索引。
- `Ctrl+Q` 仍然保留（托盘之外的快捷方式），需要在设置页里能改。

### 验证手段变化

新增 `--selftest-tray [菜单标题]`。托盘相关的验证以后都走它 + 抓图，不要靠"我点了一下没问题"。

---

## 2026-09-16 第 4 轮（设置界面 + 配置持久化）

### 本轮目标

在"目前能做到什么"盘点后，用户选了**设置界面 + 配置持久化**。
理由（当时给的）：它是后续所有功能的落脚点 —— 热键改键、每组上限、拼音开关、内容 Provider 的
启用/禁用，全都要有地方配；在此之前这些东西全硬编码在代码里，每加一个功能都要改代码。

### 状态总览

| 事项 | 状态 | 验证方式 |
|---|---|---|
| `settings.json` 读写（原子写、坏文件备份） | ✅ 完成 | `--selftest-settings` 落盘往返 |
| 设置窗口（F12 / 托盘「设置…」） | ✅ 完成 | 抓图（PrintWindow） |
| 热键改键 + 留空 + 总开关 | ✅ 完成 | 改 `Ctrl+Alt+U` 实测注册；留空实测跳过 |
| 每组上限 / 单分类上限 | ✅ 完成 | 设置即时生效 |
| 隐藏系统文件过滤 / 噪声排除开关 / 额外排除目录 | ✅ 完成 | **手改 settings.json → 结果 1→0**、**噪声开关 192→260** |
| 预览默认展开 | ✅ 完成 | 代码 + 自检转储 |
| 关闭进托盘 / 首次提示 | ✅ 完成 | 代码 + 自检转储 |
| Provider 启用/禁用 | ✅ 完成 | 禁用后 `fused=0` |
| **顺手修**：单分类上限从未生效 | ✅ 完成 | 代码 + 自检 |
| 设置页里的拼音/内容搜索开关 | ❌ 未做（功能本身还没有） | — |

### 关键决策：为什么没用 Config.Net

原计划（复用清单里写的）是用 **Config.Net**（Apache-2.0，ET 同款）。**实测后否掉了**：

```csharp
// 写入 int=24、string[]={"D:\\a","C:\\b"}
{ "Limit": "24", "Name": "hi", "Excludes": "D:\\a C:\\b" }
```

它把 `int` 存成字符串、把 `string[]` 存成**空格拼接的单个字符串**。排除目录里带空格
（Windows 路径的常态，例如 `D:\Program Files`）就会散架 —— 而且读回来是"看起来对"的那两条，
只有真带空格才炸。另外落盘格式也不适合手改。

改用内置 `System.Text.Json`：零新增依赖、原生数组、`unsafeRelaxedJsonEscaping` 之后中文路径
和 `Alt+Windows+Space` 都是原样可读的。

### 设置项清单（`%LOCALAPPDATA%\UniSearch\settings.json`）

| 节 | 项 | 默认 | 作用于 |
|---|---|---|---|
| `hotkeys` | `enabled` | true | 全局热键总开关 |
| | `summon` | `Alt+Windows+Space` | 唤出（留空 = 不注册） |
| | `directoryScope` | `Ctrl+F` | 唤出并限定到前台资源管理器目录 |
| `search` | `perCategoryLimit` | 24 | "全部"视图每类行数 |
| | `perCategoryLimitInFocus` | 200 | 点进单个分类后的行数 |
| | `filterHiddenAndSystem` | true | 隐藏/系统文件过滤 |
| | `excludeNoisePaths` | true | 回收站 / WinSxS / servicing / Windows.old |
| | `extraExcludePaths` | `[]` | 用户自定义排除路径片段 |
| `preview` | `openByDefault` | true | 启动时预览窗格是否展开 |
| `window` | `closeToTray` | true | 关闭按钮 = 收进托盘 or 退出程序 |
| | `notifyOnFirstHide` | true | 首次收进托盘弹一次气泡 |
| `providers.<id>` | `enabled` / `options` | true / `{}` | Provider 启停；`options` 是 Provider 私有键值（`IProviderRuntime.Settings` 的数据源） |

**保存后全部即时生效**（改键重注册、改上限重跑查询），不用重启 —— 这是"有设置界面"和
"设置文件只是摆设"的分界线。

### 顺手修掉的：单分类上限从来没生效过

`BrokerOptions.PerCategoryLimitInFocus = 200`（注释写着"单分类视图里的上限（翻页再放大）"）
**定义了却从没被任何代码读过** —— 分组时永远只用 `PerCategoryLimit`。
日志里就能看出来：`组=[files:24/102]`，点了"文件"标签也还是 24 条。
做设置项要引用它时才暴露。现在按 `q.ForcedCategory` 分叉，点进单分类真的给 200 条。

### 验证证据（可复现）

```bash
# 1) 落盘往返（含带空格的路径 —— 正是 Config.Net 挂掉的用例）
> UniSearch.exe --query pdf --selftest-settings
INF [selftest] 手势规范化 win+alt+space -> [Alt+Windows+Space]（期望 Alt+Windows+Space）
INF [selftest] 往返写入成功=True；重新读回 每组上限=33（期望 33）
INF [selftest] 往返读回 额外排除=[D:\带 空格 的目录 | node_modules]（期望原样两条，含空格）
INF [selftest] 已还原：每组上限=24（期望 24）

# 2) 手改 settings.json 真的影响查询（整条链路：设置 → Provider → 翻译器 → Everything）
extraExcludePaths=["content-probe"] → 搜 plain-note-file：fused  1 → 0
excludeNoisePaths true → false      → 搜 pdf：fused 192 → 260（文件夹 27→48，文件 102→146）

# 3) 改键 / 留空 / 禁用 Provider
summon="Ctrl+Alt+U"      → INF [hotkey] unisearch.summon = Ctrl+Alt+U -> 已注册
directoryScope=""        → INF [hotkey] 目录限定热键未设置，跳过（留空是允许的）
providers.everything.enabled=false → 搜 pdf：fused=0
```

设置窗口用 PrintWindow 抓图核对过：全局热键 / 搜索 / 预览 / 窗口与托盘 / 提供者五节都在，
热键框里显示的是 `Alt+Windows+Space`。

### 新踩的坑

1. **`System.Text.Json` 默认把 `+` 转义成 `\u002B`**，非 ASCII 也全变 `\u5E26` 这种。
   热键串因此会写成 `"Alt\u002BWindows\u002BSpace"` —— 对"给人手改的文件"是灾难。
   必须 `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`。
   （`Unsafe` 是针对"嵌进 HTML"的场景；本地配置文件没有那个风险。）
2. **把 `int` 直接绑到 `TextBox.Text` 时，用户敲了非法内容，WPF 会静默保留旧值** ——
   用户以为改成功了。所以数值项不绑定，改成读字符串 + 显式校验并报错。
3. **截屏验证窗口时要小心屏幕高度**：这台机器 175% 缩放，设置窗 620 DIP 高 = 1085 物理像素，
   想拉高到看全内容会被窗口管理器截断到屏幕高（1372），下半截永远拍不到。
   鼠标滚轮合成也没能让 WPF 的 ScrollViewer 滚动。**结论：长窗口的"下半截"别指望截图，
   改用行为测试**（改配置 → 看效果）。
4. 运行时实例会锁 `bin\Debug\*.dll`，编译报 MSB3027/MSB3021，先 `taskkill`。
   （第 2 轮就记过，这轮又踩了一次 —— 因为设置改动要反复重编。）

### 未完成 / 下次从这里继续

- **设置还没覆盖到的**：拼音开关、内容搜索 Provider 的启用（功能本身还没有）、
  窗口尺寸/位置记忆、结果行密度、开机自启。
- **设置窗口的"下半截"没有截图证据**：只有行为测试。等窗口能被完整显示时补一次抓图。
- `IProviderRuntime.Settings`（`providers.<id>.options`）目前**没有 Provider 在读** ——
  是给将来的 AnyTXT/Zotero 之类插件留的。第一个读它的 Provider 出现时要顺手验证。
- 设置项目测：`tests/UniSearch.Core.Tests` 只覆盖 Core，设置服务在 Host（WPF）里，
  目前只有 `--selftest-settings`。要真正单测得把 Settings 抽到一个非 WPF 项目。

---

## 2026-09-18 第 5 轮（界面重做：三栏 + Everything 式详细列表）

### 本轮目标（用户原话）

> "现在这个界面要稍微调整下，增加信息密度，做成像 everything 这样，或者 Win 资源管理器的详细信息视图那样，
> 可以列调整、列排序；增加左侧栏和右侧栏，左侧栏是切换不同的搜索后端来源（当前仅 Everything），
> 右侧栏是预览窗格。"

三个决策点当场问过用户，答复是：
1. **列表形态 → 单表平铺**（取消分组，Everything 口径），不是"保留分组 + 组内详细列"；
2. **左栏语义 → Everything 是默认后端**；其它后端要**点击左栏才触发搜索**（设置里可改），点了就看这个后端的结果；
3. **列宽/排序/可见列 → 全部持久化**到 `settings.json`。

### 状态总览

| 事项 | 状态 | 验证方式 |
|---|---|---|
| 结果表改平铺详细列表（可调列宽、点列头排序） | ✅ | `--selftest-layout`（排序单调性 PASS）+ 截图 |
| 列增减 / 左右移动 / 恢复默认（右键列头） | ✅ | `--selftest-layout` 转储菜单 + 列数断言 |
| 列布局落盘（宽度/顺序/可见列/排序键） | ✅ | `--selftest-layout` 落盘往返 + `--selftest-settings` |
| 左栏来源：点击 = 只搜它（进 `ProviderScope`） | ✅ | `--selftest-layout`：钉不存在的后端 → 行数 0 且报"不在当前来源范围内" |
| 右栏预览 + 选中项摘要 | ✅ | `--selftest-preview` 五种类型全对 |
| 设置窗口新增「列表与列」节 | ✅ | `--dump-settings` 离屏截图（新开关，见下） |
| 旧的"每组上限 24"退场，改 `search.maxRows`（默认 500） | ✅ | `--selftest-settings` 往返 + 日志 `行=186/186`（原来只显示 24×N） |
| **顺手修**：`UniSearch.Host` 根本不在 `.sln` 里 | ✅ | `dotnet build UniSearch.sln` 现在会编主程序 |
| 拖列头**重排**列（鼠标拖拽换位） | ❌ 未做（只有菜单左右移动） | — |
| 行高设置项（24/30/44 可选） | ❌ 未做 | — |

**构建状态**：全解决方案（现在含 Host）`0 错误 0 警告`；单元测试 **88/88**（新增 9 个）。

### 1. 三栏布局

```
来源(188, Ctrl+B)  │  结果表（列头可点/可拖/右键）  │  预览 + 摘要(340, Ctrl+J)
```

- 左栏点击 = **本次只搜它**（`SearchQuery.ProviderScope`），再点一次取消限定回到设置里的默认集合。
  这条刻意做成"决定后端跑不跑"而不是"过滤显示"：将来接上 AnyTXT 这类要起服务/读索引的后端时，
  "没搜"和"搜了没结果"必须能区分，否则状态条会骗人。
- 右栏 = 预览 + 选中项摘要（标题/路径/类型·大小·时间·来源），摘要字段直接来自列映射，不额外查盘。
- 两栏收起 = 列宽归零（不是移除列），所以拖过的宽度记得住。

### 2. 平铺单表：为什么"每组 24 行"必须退场

日志里原来的样子：`组=[files:24/102]` —— 搜 `pdf` 时 102 个文件只显示 24 个，
而"点进单分类才给 200"这个补偿动作用户根本不会做。平铺之后：

```
INF [ui] 快照 req=1 fused=186 complete=True 来源=[Everything] 行=186/186 排序=name↑ 可见列=[name,path,size,modified,kind]
```

分类改由**顶部标签**（硬过滤）+ **类型列**表达。`search.perCategoryLimit*` 两个键在载入时
迁移成 `search.maxRows`（取旧的单分类上限），迁移完就从文件里消失 —— 留着会让人以为它还在起作用。

### 3. 验证证据（可复现）

```bash
UniSearch.exe --query pdf --selftest-layout      # 来源 / 排序 / 列装配 / 列布局落盘
UniSearch.exe --query pdf --selftest-preview     # 五种类型的预览
UniSearch.exe --dump-render out/x.png            # 主窗口离屏截图
UniSearch.exe --dump-settings out/s.png          # 设置窗口离屏截图（新增）
```

```
INF [selftest] 钉住 everything：scope=[everything] 行数=186 后端处置=[everything:Done]
INF [selftest] 钉住不存在的后端：行数=0（期望 0） 后端处置=[everything:Skipped/不在当前来源范围内（左侧来源栏未选中它）]
INF [selftest] 大小降序：PASS（前 5 行 mojiezhanji5.pdf=28710707, mspdf.dll=18874184, …）
INF [selftest] 大小升序（同列再点一次换方向）：PASS（前 5 行 linkfox-zhihuiya-pdf-data=-1, …）
INF [selftest] 列装配：可见列=[name,path,size,modified,kind] GridView 实际列数=5（期望 5）
INF [selftest] 勾选「扩展名」后：GridView 列数=6；取消勾选后：GridView 列数=5
INF [selftest] 把「名称」列宽设为 275 → GridView 列宽=275（期望 275）
INF [selftest] 列头右键菜单 = [☑名称 | ☑路径 | ☑大小 | ☑修改时间 | ☑类型 | ☐扩展名 | ☐来源 | ·左移一列 | ·右移一列 | ·恢复默认列布局]
INF [selftest] Ctrl+B -> handled=True 来源栏 True -> False
INF [selftest] 列布局往返：写入成功=True；读回 name 列宽=333（期望 333） 排序=size↓ 可见列=[name,path,size]
INF [selftest] 图片/文本/PDF文档/其它/文件夹 -> Kind=Image / Text / Image·第 1 页 / Unavailable / Folder
```

截图：`out/final-layout3.png`（主窗口）、`out/settings2.png`（设置窗口，含新的「列表与列」节）。

### 4. 新踩的坑（按价值排序）

1. **`SelectedItem` 双向绑定 + 整表重建 = 选中永远跳回第一行** ⭐
   快照刷新时 `Rows` 是 `Clear() + Add()`，ListView 在 Clear 那一刻把 `SelectedItem` 置空并**回写**，
   于是视图模型的选中被抹成 null、"按路径找回原来那一行"的锚点也一起丢了。
   症状不是报错，而是**预览永远停在第一行**（自检里 PDF/文件夹各项报的都是第一行的内容）。
   修法：`SelectedItem` 改**单向**绑定 + `SelectionChanged` 里受控回写，
   重建期间用 `IsRebuildingRows` 挡住；锚点也改成"只在有值时更新"。
2. **`ColumnDefinition.Width` 要的是 `GridLength`，不是 `Double`** ——
   把 `sys:Double x:Key="SideBar.Width"` 直接 `{StaticResource}` 上去，启动即崩：
   `对属性 Width 指定的值无效`。两个键必须分开声明（`MinWidth` 用 Double，`Width` 用 GridLength）。
3. **`UniSearch.Host` 不在 `.sln` 里** —— `dotnet build UniSearch.sln` 报"已成功生成"，
   但主程序根本没重编（exe 时间戳是上一次单项目构建的）。我因此拿着旧 exe 跑了一轮自检，
   还纳闷"新加的日志怎么没打出来"。已 `dotnet sln add`。
4. **固定 sleep 的自检会给出假失败** —— PDF 首页渲染实测要 ~2 秒（`Windows.Data.Pdf`），
   而自检原来死等 700ms，读到的是上一行的内容，看起来像"预览不跟着选中走"。
   改成"轮询等 `PreviewContent` 换一次，最多 6 秒"，并打印真实等待毫秒数（现在 PDF 报 327ms，缓存热）。
5. **"名称列自动填充"不能顺手把列压小** —— 第一版按"剩余空间"设宽，空间不够时反而把
   名称列从 180 压到 151，文件名截得更厉害。改成**只扩不缩**（下限 = 该列默认宽度）。
6. **设置窗口的草稿会覆盖实时列布局** —— 列宽是主窗口实时落盘的，设置窗口那份草稿是打开时的快照，
   保存时无脑覆盖会把用户刚拖出来的列宽回退。加了 `ColumnsEdited` 标记：用户没在设置窗口里动过列，就用实时值。
7. 175% 缩放 + 屏幕高度限制依旧：设置窗口 `Height=1180` 会被窗口管理器压到 784（=屏幕高），
   但**离屏渲染不受影响**，所以 `--dump-settings` 能拍到 ScrollViewer 里"看不见的下半截"。
   （上一轮缺的后三节截图证据，这轮补上了。）

### 5. 未完成 / 下次从这里继续

- **拖列头重排**（鼠标直接拖列头换位）没做，现在只有右键菜单的"左移/右移一列"。GridView 不支持原生列重排，要自己写 DragDrop。
- **列头右键菜单的交互**只验证了内容构建，真实右键弹出仍需人工点一次（合成鼠标会被 UIPI 拦掉）。
- 行高、字体大小没有设置项（现在固定 24px；Tokens 里留了 26/30/44）。
- 左栏目前只有一个后端，所以"点击切换"的实际手感要等第二个 Provider（AnyTXT）接上才算真验证过。
- `ProviderDescriptor` 没有图标字段，左栏所有后端暂时共用一个放大镜字形。
- 平铺之后 `CategoryGroup.Trimmed`/`ProvidersLabel` 在 UI 层已无人读（Broker 仍产出），要么删要么等诊断面板用。

### 6. 验证手段变化

- 新增 `--selftest-layout`：来源限定 / 列排序单调性 / 列装配列数 / 列布局落盘往返。
- 新增 `--dump-settings <png>`：设置窗口离屏渲染，专治"长窗口下半截拍不到"。
- `RunPreviewAsync` 从"死等 700ms"改成"等预览真的换一次"（并报告等待耗时）。

### 7. 交付：桌面快捷方式 + 发布目录（2026-09-18 追加）

用户："我还不知道可执行文件在哪，你弄个桌面快捷方式。"

- 发布目录 **`dist\`**（`dotnet publish -c Release -o dist`，框架依赖，约 30 个文件）。
  桌面快捷方式 `C:\Users\zhou\Desktop\UniSearch.lnk` 指向 `dist\UniSearch.exe`。
- **为什么不指向 `src\UniSearch.Host\bin\Debug\UniSearch.exe`**：那是构建产物目录，
  每次构建都变；而且程序运行时锁住里面的 dll，想重编就得先 taskkill（本项目踩过 MSB3021）。
  `dist\` 是"给人用的那一份"，改完代码重跑脚本即可，快捷方式路径不变。
- 新增 **`tools/install-desktop-shortcut.ps1`**（发布 + 建/刷新快捷方式，幂等）：
  ```powershell
  powershell -ExecutionPolicy Bypass -File tools\install-desktop-shortcut.ps1
  powershell -ExecutionPolicy Bypass -File tools\install-desktop-shortcut.ps1 -NoShortcut   # 只发布
  ```
  脚本会先 kill 运行中的实例（它锁着 dist 里的 dll），发布完把快捷方式**读回来核对**
  （`CreateShortcut.Save()` 失败是静默的，不读回等于没验证）。
- 验证：`Start-Process` 快捷方式 → `PID=2312 窗口标题=[UniSearch] 有窗口=True`。
- `.gitignore` 加 `dist/`（发布产物不进版本控制）。

**注意**：改完代码要重新跑一次这个脚本，桌面快捷方式才用的是新版本（路径没变，不用重建快捷方式）。

---

## 2026-09-18 第 6 轮（用户实测反馈 7 条：字体/字号/列拖拽/布局/两态左栏/可扩展筛选器）

### 本轮目标（用户原话，编号照抄）

1. 没有使用默认字体，应该是回退了日文字体显示中文
2. 中间界面的文字字号略大，包括筛选行和列表，需下修一下默认字号大小
3. 列实测无法拖动
4. 界面分割不对，筛选行应该放在和中间的列表一起的上方，而不是从左侧边栏开始；不同的后端应用不同的筛选器；
   左侧栏和右侧栏要有个缩回去的按钮；左侧栏要设计两种形态（占用小 = 只显示后端图标 + 下面一行数量，最多显示到 >9999）
5. 确定原则：将整个软件设计更加松耦合，例如不同搜索后端用不同的筛选器，现有的筛选器可扩展
   （例如定义 pdb/cif/pse/fasta/fa 为"生信相关"），要做成 JSON/YAML 的定义器

### 状态总览

| # | 事项 | 状态 | 验证方式 |
|---|---|---|---|
| 1 | 字体回退到日文字形 | ✅ 字体栈显式加 `Microsoft YaHei UI` | 截图 + `Window` 级 FontFamily 继承 |
| 2 | 字号下修 | ✅ 12→11 / 11→10.5 / 13.5→12.5 | 截图对比 |
| 3 | 列拖不动 | ✅ **根因是热区只有 7px 且完全隐形**（链路本来是通的） | `--selftest-layout` 拖拽探针：`热区=10x26 拖 +40px: 230 -> 270 ✓` |
| 4a | 筛选行移进中间列 | ✅ | 截图（`out/ui-v4-full.png`） |
| 4b | 左栏两态（大/小） | ✅ 小形态 = 图标 + 数量（>9999 缩写成 `>9999`） | `out/ui-v4-compact.png` + Ctrl+B 自检 |
| 4c | 左右栏折叠按钮 | ✅ 左栏两态切换；右栏收起后留一条"把手" | 截图 |
| 5 | 可扩展筛选器定义器（JSON） | ✅ `filters.json`：扩展名/类型/正则 + 按后端分叉 | `--selftest-filters` + 94/94 单测 |
| — | **顺手修**：设置页长图截图 | ✅ `--dump-settings` 改渲染滚动区内容（1117px 全图） | `out/ui-v4-settings.png` |

**构建状态**：全解决方案 `0 错误 0 警告`；单元测试 **94/94**（新增 6 个筛选器测试）。

### 1. 字体：为什么中文会变成日文字形

字体栈原来只写 `Segoe UI Variable Text, Segoe UI`。Segoe UI 没有汉字，WPF 走系统字体回退 ——
在中文 Windows 上这个回退**可能挑到 Yu Gothic UI**（日文字体），于是"直""骨""込"这些字是日文字形。
修法：字体栈里显式写中文字体，并在 `Window` 上设 `FontFamily`（继承给所有子元素，避免漏网）：

```xml
<FontFamily x:Key="Font.Text">Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei, SimSun</FontFamily>
```

字号同时下修（用户第 2 条）：`Primary 12→11`、`Secondary 11→10.5`、`Chip 10.5→10`、`Search 13.5→12.5`，
等宽字体也统一到 `Font.Mono`（含中文回退）。

### 2. 列拖不动的真相：链路是通的，热区是隐形的

用户说"列实测无法拖动"。我先写了个探针（`ProbeColumnResize`），结论分两步：

1. 第一版探针读 `header.ActualWidth` 得到 **一排 0**、`Thumb 共 0 个` —— 差点误判成"模板没应用"。
   真因是**没调 `UpdateLayout()`**：`ActualWidth` 在布局跑过之前恒为 0。
2. 加上 `UpdateLayout()` 后真相出来：`热区=7x26 拖 +40px: 列宽 230 -> 270 ✓` ——
   **GridView 的拖拽链路完全正常**，问题是热区只有 7px 宽、而且整块 `Opacity=0`：
   用户根本看不见边界在哪，拖十次有九次没命中。

修法：热区 7px → **10px**，hover 时露出 2px 强调色竖线当把手（不 hover 时仍然隐形）。
探针保留在 `--selftest-layout` 里，以后谁再动列头模板，一眼能看出有没有把 `PART_HeaderGripper` 弄丢。

> 这个坑值得记：**"功能没生效"和"用户没找到入口"看起来一模一样**。
> 探针（程序化 `RaiseEvent` 发 DragDelta）能一次分清这两件事，合成鼠标做不到（UIPI 拦）。

### 3. 布局：筛选行属于列表，不属于窗口

筛选行（标签栏）原来横跨整个窗口、从左栏上方开始 —— 视觉上像是"管着来源栏"。
改成放在**中间列内部**（列表上方），与列表左边缘对齐；左栏/右栏变成真正的通栏。

同时按用户要求给两侧加折叠：
- **左栏两态**：大（188px，图标+名称+状态+"默认"标）/ 小（56px，只有后端图标 + 一行数量）。
  数量超过 9999 时写 `>9999` —— 56px 放不下五位数，写全了会被截成 `1234…`，反而看不出量级。
- **右栏**：展开 340px / 收起成 22px 的"把手"（把手上有展开按钮）。
  **收起后必须留把手**，否则用户只剩快捷键能把预览叫回来。

筛选行也从横向滚动改成 `WrapPanel` 换行：滚动条会吃掉一行高度，而且"有标签藏在右边"用户看不出来；
换行最多多占一行，但一个标签都不会丢（加了 4 个自定义筛选器之后确实会换到第二行）。

### 4. 筛选器定义器（第 5 条，本轮最大的一块）

**落点**：`filters.json`，两份合并 —— 程序自带模板（`dist\filters.json`）+ 用户自己的
（`%LOCALAPPDATA%\UniSearch\filters.json`），**同 id 后者覆盖前者**。

```json
{
  "id": "bioinformatics", "name": "生信相关", "order": 500,
  "extensions": ["pdb","cif","mmcif","pse","fasta","fa","fastq","sam","bam","vcf","gff","gtf"]
}
```

- **为什么 JSON 不是 YAML**：项目已经在用 `System.Text.Json` 读设置（零新增依赖），
  为省几个引号再引一个 YAML 库不划算；这里还开了"允许注释 + 允许尾随逗号"，手改体验接近 YAML。
- **按后端分叉**（用户第 4 条的"不同的后端应用不同的筛选器"）：`providers` 字段限定只在某些后端下显示。
  左栏切到某个后端时，标签栏按 `Catalog.For(EffectiveProviderScope)` 重算。
- **查询翻译**：点自定义筛选器 → 新增的 `QueryParser.WithFilters` 把扩展名/类型并进 `Filters`
  并**重建 `ProviderText`**。只改 `Filters` 不重建查询串的话，后端会把整库结果搬过来再由 Core 后过滤。
  自检里能看到翻译结果：`probe ext:pdb;ent;cif;mmcif;…`。
- **内置分类不动**：文件/图片/文件夹… 仍由 `CategoryEngine` 判定，自定义筛选器与它们并列显示、按 `order` 排序。
  这样新机制缺席时（没有 filters.json）旧行为完全成立。

写错的代价是可控的：整份 JSON 坏了只跳过这一份；单条定义有问题（没有 id、类型名不认识、正则不合法、
一个条件都没写）只剔除这一条，原因写进 `host.log` 的 `[filters]` 行，设置窗口也会提示"N 条定义有问题"。

### 5. 验证证据（可复现）

```bash
UniSearch.exe --query probe --selftest-filters    # 筛选器加载 + 翻译 + 过滤
UniSearch.exe --query probe --selftest-layout     # 列拖拽探针 + 排序 + 列装配
UniSearch.exe --dump-render out/x.png             # 主窗口
UniSearch.exe --dump-settings out/s.png           # 设置窗口（现在是整页长图）
```

```
INF [filters] 筛选器 4 个（来源=[filters.json]）
INF [selftest] 标签栏=[all, folders, code, bioinformatics(自定义), cad3d(自定义), notebooks(自定义), configs(自定义)]
INF [selftest] 当前 236 行里各筛选器命中：[bioinformatics=13, cad3d=3, notebooks=0, configs=23]
INF [selftest] 选中「生信相关」：查询串=[probe ext:pdb;ent;cif;mmcif;…]（期望含 ext:）
INF [selftest]   行数 236 -> 13（等到新快照=True）越界行=[无]（期望无）
INF [selftest] 回到「全部」：行数 13 -> 236（等到新快照=True）查询串=[probe]（期望不含 ext:）
INF [selftest] 列宽拖拽探针(路径 +40px)：热区=10x26 拖 +40px: 列宽 230 -> 270  ✓
```

截图：`out/ui-v4-full.png`（大形态 + 自定义筛选器）、`out/ui-v4-compact.png`（紧凑形态）、
`out/ui-v4-settings.png`（设置页整页长图，含新的「筛选器」节）。

单测 94/94，新增 6 个覆盖：扩展名/类型规范化、多文件覆盖同 id、坏定义只报问题不抛异常、
并集匹配语义、`providers` 可见性、文件不存在不算错误。

### 6. 新踩的坑

1. **`ActualWidth` 在布局跑过之前恒为 0** —— 探针第一版据此得出"列头没有热区"的错误结论。
   读任何 `ActualWidth/ActualHeight` 之前先 `UpdateLayout()`。
2. **"功能没生效"和"入口看不见"要分开验证** —— 列拖拽的真实原因不是链路断了，是热区 7px 且透明。
   程序化 `RaiseEvent` 能证明链路，合成鼠标不能（UIPI 拦）。
3. **XML 注释里不能出现 `--`** —— 我在 XAML 注释里写了 `--dump-settings`，编译直接报
   `An XML comment cannot contain '--'`。
4. **WPF-UI 的隐式 `RadioButton` 样式会把标签撑宽** —— 七个分类标签因此放不下、出现横向滚动条。
   给标签加 `Style="{x:Null}"` 之后宽度只由自己的模板决定。
5. **自定义筛选器计数不能取自 Broker** —— Broker 只认内置分类，自定义筛选器的计数由 UI 按当前结果集现算
   （`_fused.Count(f.Matches)`），所以它反映的是"当前这批结果里有多少条符合"，而不是全库。
6. 设置窗口"比屏幕高"的老问题这次真的解决了：**渲染滚动区内容而不是窗口**，
   `--dump-settings` 现在输出 1117px 高的整页长图。

### 7. 未完成 / 下次从这里继续

- `namePattern` 目前只在前端（结果集）过滤，没下推到后端查询；筛选器之间不能组合（"生信相关 且 最近一周"）。
- 左栏紧凑态的图标还是所有后端共用一个放大镜（`ProviderDescriptor` 没有图标字段）。
- 拖列头**重排**列仍未做（只有右键菜单的左移/右移）。
- 筛选器文件改动需要重启程序生效（没有做文件监视 + 热重载）。
- `filters.json` 的模板只有 4 组示例；等用户自己长出来之后，可以考虑把常用的收进模板。

---

## 2026-09-19 第 7 轮（字号再下修 + 功能缺口 + 列拖拽换位）

### 本轮目标（用户原话）

> "中间结果列表和筛选器的字号仍过大，需要降低字号，把功能缺口补齐，再看列能不能设置成可拖动的"

### 状态总览

| # | 事项 | 状态 | 验证方式 |
|---|---|---|---|
| 1 | 中间列表与筛选器字号下修 | ✅ Primary 11→10 / Secondary 10.5→9.5 / Chip 10→9（搜索框 12.5 不动） | `out/ui-v5-font.png` |
| 2 | 列拖拽换位 | ✅ `AllowsColumnReorder=True` + 换位回写 `Vm.Columns`（随窗口关闭落盘） | 构建通过，自检列装配照旧 |
| 3 | Tab 补全（MVP D9） | ✅ 框内有候选先补全，无候选回落切分类 | 构建通过（`AutoCompleteText=文件名`） |
| 4 | 拖出到 Explorer（MVP D6） | ✅ FileDrop 拖出 | 构建通过 |
| 5 | 开机自启（MVP A3） | ✅ HKCU Run + 设置项（默认关）即时生效 | 自检日志 `开机自启已关` |
| 6 | 自包含发布（MVP A1） | ✅ 脚本加 `-SelfContained` 开关（默认仍框架依赖） | 代码 |
| 7 | 单实例（MVP A2） | ✅ 本轮核对：早已实现，无需再做 | 代码（`SingleInstance.cs`） |

**构建状态**：`0 错误`；单元测试 **94/94**；`--selftest-layout/filters/settings` 全绿；
已重新发布到 `dist\`，桌面快捷方式指向新版。

### 说明

- **字号到底了**：再小中文笔画在 100%~175% 缩放下会糊。`UI-SPEC.md` §5 同步了新数值。
- **Tab 的两条规格是兼容的**：§4.2（切分类）与 §4.3（框内补全）—— 框内有候选先补全，否则切分类。
- **列宽拖拽 vs 列换位**：前者（热区 10px）上一轮已修好；本轮开的是换位（拖列头本身）。
  换位的真实拖动需人工拖一次确认（合成鼠标被 UIPI 拦，自检覆盖不到）。
- **没做的**：Mica/深浅色（装饰，功能优先原则下暂缓）；多后端故障降级实测（等第二个后端）。

---
### 第 7 轮补记（2026-09-20，用户实测打回）

用户指出三点：① 左栏变得特别小、中间看着没变；② 拖拽没做好；③ 怀疑快捷方式不是最新。
核实：快捷方式与 `dist\` 都是 22:27 最新构建，运行中实例即新版 —— 版本没问题。
真因：上一轮是全局下修，左栏四个文本同时缩小所以显小，中间只小 1pt 看着没变。

- 字号改按区域拆分：共享恢复 Primary 11 / Secondary 10.5 / Chip 10（左右栏回到上一轮），
  中间独立 `ResultRow 9 / FilterTab 8.5 / ColumnHeader 8.5`（`Controls.xaml` 三处改绑）。
- 列换位加 `ProbeColumnMove` 探针并接入 `--selftest-layout`：开关 True、
  kind 列搬 4→0 再 0→4，双 PASS（视图/回写/落盘三者一致）。
- 拖出坐标 `GetPosition(null)` 改挂 `ResultList`（按下与移动同一参照）。
- 截图 `out/ui-v5b-font.png`，重发 `dist\`（00:18）。

遗留：鼠标拖列头换位的"手势本身"自检覆盖不到（UIPI 拦合成鼠标），需人工拖一次；
若仍不动，请明确是哪一种拖（调宽 / 换位 / 拖出到 Explorer），探针已证明链路是通的。

---
## 2026-09-20 第 8 轮（前端栈评估 + 中间栏传统桌面密度）

### 本轮目标（用户原话）

> 前端技术栈是不是有限制，有没有更适合的；以后要接 explore-remote-files 当远端搜索后端（后话）；
> 关键是中间栏文字和间距太大，不是传统桌面软件的样子。

### 结论（先说）

- **栈不换**： density 是度量问题，已按 Everything 默认度量收紧；换栈的代价（见下）远大于收益。
- **远端搜索**：对接点定在 ERF 常驻服务协议（`ERF_PROTOCOL_PLAN` / 常驻服务架构），
  待它暴露列举/搜索 RPC 后，UniSearch 侧新增一个 Provider 包（只引用 Sdk），与 Everything 同构。
  许可证先审：ERF 自研部分可复用，第三方只用 MIT/Apache-2.0（本项目硬约束）。
- **密度**：行高 24→20、列头 26→22、单元格边距 6→4、筛选标签 `7,2→6,1`、
  筛选行间距 4→2、名称图标格 20→18。一屏约 24 行（原来约 17 行）。

### 栈评估（为什么不换）

| 备选 | 否决理由（一句话） |
|---|---|
| WinUI 3 | 要带 WinAppSDK 运行时（部署变重）；托盘/全局热键/shell 互操作（真右键菜单、拖放、IShellWindows）全是坑区，ET/Flow 的现成经验都是 WPF 的 |
| Avalonia | 跨平台用不上（只做 Windows）；Windows shell 集成故事比 WPF 弱，社区轮子少 |
| Qt | 商业/GPL 与本项目 MIT/Apache-only 硬约束冲突；且整仓 C# 重写不划算 |
| Electron/Tauri | 常驻启动器的内存/启动开销；全局热键+托盘+shell 菜单要走原生桥，复杂度不降反升 |
| WinForms | 传统密度确实顺手，但富预览/样式/数据绑定要手写回来，等于倒退 |

WPF 的真实限制（诚实列出，都不在当前路线上）：Mica 背板难做（已按功能优先暂缓）、
应用商店/MSIX 打包、Web 渲染的富预览。触发换栈的条件：要上架商店、要跨 macOS/Linux、
或预览要内嵌浏览器级排版 —— 在此之前换栈是亏的。

### 验证

构建 0 错误；单测 94/94；`--selftest-layout` 全绿（含换位探针双 PASS）；
截图 `out/ui-v5c-density.png`；`MVP.md` D1（20px/≥18行）与 `UI-SPEC.md` 度量同步；
已重发 `dist\`（00:26），快捷方式指向新版。**看效果前请重启托盘里的旧进程。**

---
### 第 8 轮补记（用户：字还能小 + 右侧收缩按钮没做）

- 字比资源管理器显大：`app.manifest` 已是 PerMonitorV2（排除位图拉伸），真因是 WPF 默认
  Ideal 文本度量偏宽偏糊 —— 主窗口改 `TextFormattingMode="Display"`；中间字号行 9→8.5、
  标签/列头 8.5→8（间距不动）。中文再往下（<8pt）笔画会糊，到底。
- 右侧收缩按钮：逻辑一直通的（Ctrl+J 自检来回全绿），收起后的 22px 把手几乎隐形才是真相 ——
  把手按钮纵向拉满、箭头 11→14 居中、整条可点；自检加把手可见性断言（双过）；
  收起态裁图确认箭头清晰。截图 `out/ui-v6b-rail.png`，重发 `dist\`（00:50）。

---
### 暂停点（2026-09-20，用户实拍对比后叫停，次日继续）

用户发实拍三窗并排（UniSearch 搜 pdf / 设置窗 / 资源管理器 D 盘）：UniSearch 结果文件名
明显大于资源管理器（目测约 1.3 倍），筛选标签更大一圈，列头相近略大。
用户要求今天停手、只记录状态。

次日首查（按序）：① 运行中实例是否为 00:50 新版（dist 时间戳 vs 进程启动时间）；
② 若已是新版仍大，并排度量同一字符串像素高度，再定缩小点数还是换字体栈。
悬而未决：列拖拽手势的用户实测（自检链路已全绿）。

---
## 2026-09-20 第 9 轮（"字号和你说的对不上"的真因：WPF-UI 的隐式 TextBlock 样式把字号顶掉了）

### 本轮目标（用户原话）

> 那个是凌晨的对话，现在就是要继续做的。但奇怪的是，为什么你说的字号和我看的对不上，
> 为什么 UI 界面的字体大小差别特别大，是不是有些地方吃 DPI，有些不吃。

### 结论（先说）

**用户是对的，前几轮"缩小字号"全部无效。**

**根因：WPF-UI 4.0.3 的 `ControlsDictionary` 里带了一个隐式 `TextBlock` 样式，且设了 `FontSize=14`。**
WPF 的取值优先级里 **"样式 setter" 高于 "属性继承"**，而本项目的字号体系恰恰是
"容器设字号、子元素继承"（`ResultRowStyle` 把 8.5 设在 `ListViewItem` 上，单元格 TextBlock 靠继承）。
于是**所有靠继承取字号的文字都被强行画成 14 DIP（= 10.5pt）**，包括：

- 结果行的 7 个单元格（名称/路径/扩展名/大小/修改时间/类型/来源）；
- 列头的标题文字；
- 筛选标签（`RadioButton` 的 `Content` 是字符串，由 `ContentPresenter` 生成 TextBlock → 也被顶掉）。

14 DIP = 10.5pt，而资源管理器列表是 **9pt = 12 DIP** —— **大 17%**，
再加上行高只有 20 DIP，视觉上就更挤更大。所以用户看到的"中间区域比资源管理器大"完全正确；
而我此前只在算令牌、没验证令牌有没有真的生效，连续三轮（行 9→8.5、标签 8.5→8）全是空转。

**另外两条也确实存在，但不是"看着大"的原因：**

1. **DPI 没有不一致**：`app.manifest` 的 PerMonitorV2 生效（实测进程 `GetProcessDpiAwareness = 2`）；
   本机显示器 **168 dpi / 175%**（物理 2390x1344，逻辑 1366x768）。WPF 全按 DIP 布局，
   整窗文字吃同一个缩放系数 —— **不存在"有些地方吃 DPI、有些不吃"**。
2. **单位混淆**：XAML 的 `FontSize` 是 **DIP（1/96 英寸）**，不是磅；`1 pt = 1.333 DIP`
   （WPF 默认 12 DIP = 9pt）。所以令牌里的 `8.5` 是 **6.4pt**，此前一直按"8.5pt"向用户汇报。
3. **验证工具丢缩放**：`RenderDump` 原来固定 96 DPI 离屏渲染，`out/ui-v6*.png` 全是 1280x660
   （= 100% 缩放下的样子），而用户屏幕上同一窗口是 2240x1155。拿那种图判断"字大不大"必然对不上。

基准实测：`SPI_GETICONTITLELOGFONT` → `Microsoft YaHei UI, lfHeight=-12`（12 px = 9pt）；
`SystemFonts.IconTitleFont = Microsoft YaHei UI 9 pt`。

### 改了什么

- **`src/UniSearch.Host/App.xaml`（真正的修复）**：在 `Application.Resources` 里**直接**声明一份
  "不设 `FontSize`" 的隐式 `TextBlock` 样式 —— 直接声明（而不是塞进 `MergedDictionaries`）才能
  压过 WPF-UI 的同键条目。作用是把"继承"这条语义还给 TextBlock：
  结果行回到 8.5、筛选标签回到 8、左栏回到 11 …（都来自各自的容器）。
- `src/UniSearch.Host/Theme/Controls.xaml`：7 个单元格 + 列头的 `TextBlock` **显式**写出
  `FontSize`（ResultRow / ColumnHeader），把意图固定在本地，不再依赖继承；并加长注释记录这个坑。
- `src/UniSearch.Host/Services/RenderDump.cs`：`Run` / `RunVisual` 改为按
  `VisualTreeHelper.GetDpi()` 的**真实缩放**渲染，像素尺寸 = DIP × 缩放（175% → 2240x1155）；
  新增 `PixelsPerDip(Visual)`。新增可选 `scale` 参数，默认自动。
- `src/UniSearch.Host/App.xaml.cs` 的 `--dump-render`：不再固定等 2.5s（Everything 冷启动
  握手常超 2.5s，会拍成"搜索中…"），改成轮询 `_vm.IsBusy`，空闲后再等 800ms 让后台 shell
  图标落到行上；**12s 之后无论如何出图**，别把自动化拖住。

### 验证

- **像素级实测**（扫 `out/ui-v8-final-175.png` 的文字墨迹高度，175% 下 1 DIP = 1.75 px）：
  结果行 **14~15 px = 8.0~8.6 DIP**（令牌 8.5 ✓）、列头 **14 px = 8.0 DIP**（令牌 8 ✓）、
  筛选标签 **12 px = 6.9 DIP**（令牌 8 ✓）。修复前这些是 14 DIP（≈25 px）。
- **独立复现证据**：`tmp\UniSearch\FontProbe`（引用同一个 WPF-UI 4.0.3，复刻 App.xaml 的合并顺序）
  未打补丁时打印 `RadioButton(Content=字符串, FontSize=8) → 实际 14`、
  `ListViewItem(8.5) 里的 TextBlock → 实际 14`；加上这份隐式样式后变成 8 / 8.5。
- 构建 0 错误；单测 **94/94**；`--selftest-layout` 全绿（列宽拖拽探针 ✓、换位探针双 PASS、
  Ctrl+B 往返、列布局落盘往返）；`--selftest-keys` 全绿（Ctrl+J 收起→把手可见=True→展开=False，双过）。
- `--dump-render` 现在输出 **2240x1155**（1280x660 DIP × 1.75），与用户屏幕 1:1。
- 产物：`out/ui-v8-final-175.png`（修复后，含结果行）、`out/ui-v8-rowfont-fixed.png`
  （只修了单元格、标签还没修的中间态）、`out/ui-v7-font-compare.png`（字号对照图表）。

### 未完成 / 下次从这里继续

- **字号令牌这一轮没改**，改的是"令牌到底有没有生效"。现在的实际观感 = 设计值：
  结果行 8.5 DIP（6.4pt）、筛选标签/列头 8 DIP（6pt）、搜索框 12.5 DIP（9.4pt）。
  请用户看 `ui-v8-final-175.png` 再决定要不要调令牌本身。
- **同类陷阱的排查提醒**：凡"容器设字号、子元素继承"的写法都要留个心眼 ——
  WPF-UI 的隐式样式会顶掉继承。现已排查：全仓 XAML 里没有其它依赖继承的 TextBlock
  （`tmp\UniSearch\audit-fontsize2.py` 可重跑）。
- **命令行冷启动查 Everything 偶发不返回**（本次复现两次、第三次正常）：一切正常时
  `--query pdf` 约 1s 内拿到 260 条；异常时 12s 内一条快照都没有。怀疑与 Everything
  被反复 kill / 其 GUI 实例刚重启有关，暂按"环境抖动"记，未再追。
- 列拖拽手势的用户实测仍然欠着（自检链路全绿，只差人手拖一下）。

---
## 2026-09-20 第 10 轮（字号规矩落地 + 修"滑块/列宽拖不动"）

### 本轮目标（用户原话）

> 按一般的设计，标题属性的字号要大一些，而内部元素的字号要统一。例如左侧栏的"来源"要大些，
> everything 和条目数文本按中间区域一样的字号，也就是 9pt。
> 你现在需要修正的是中间区域的拖拽异常，无论是滑块、列宽都无法拉出来。

### 字号：定规矩并落地

- 新令牌 `Font.Size.SectionTitle = 14`（10.5pt）= **区域/分区标题**：左栏"来源"、预览的选中项标题、
  设置窗的分区标题（`SectionHeader`）。
- 其余所有"内部元素"统一 `12`（= **9pt = 12 DIP**，与资源管理器列表同档）：
  左栏条目（名称/状态/计数/"默认"标签/底部提示）、预览元信息与正文、
  中间列表/筛选标签/列头、共享令牌 `Primary`/`Secondary`/`Chip` 一律 12。
- 令牌注释里写清了这条规矩（`Tokens.xaml` 字号段）。

### 拖拽：找到两个真问题

**问题 1（本轮修掉）：拖出到 Explorer 的处理器抢走了鼠标。**
`OnResultPreviewMouseLeftDown/Move` 挂在 **ListView 的 `PreviewMouseLeftButtonDown/Move`**（隧道事件）上，
而且**无条件 arm**。列头拖拽热区（`Thumb`）、列头本身、以及列表内部的**滚动条**同样会触发它；
`DragDrop.DoDragDrop` 是**模态**的 —— 一旦越过拖拽阈值启动，鼠标就被它抢走，
Thumb / ScrollBar 自己的拖动全部失效。用户看到的就是"滑块和列宽都拖不出来"。

修法：`ShouldArmDragOut(落点)` 只认"按在结果行上" —— 命中 `Thumb` / `GridViewColumnHeader` /
`ScrollBar` 一律不 arm（落点从 `ContentElement` 也要能往上爬，命中文字时 `OriginalSource` 是 `Run`）。

**问题 2（新加的探针查得出来）：`ProbeColumnResize` 这种"直接 RaiseEvent 到 Thumb"的探针
查不出问题 1** —— 它绕过了真实手势路径，所以前几轮一直"自检全绿、用户拖不动"。本轮补两条断言：

- `ProbeGripperHitTest`：热区中心用 `VisualTreeHelper.HitTest` 打一下，命中的必须是 Thumb 或其后代
  （当前实测 `name:命中=Grid ✓`）。
- `ProbeDragArm`：列头热区 / 列头 / 垂直滑块**都不 arm**，结果行单元格 / 行容器**要 arm**。
  实测：`列头热区=不 arm ✓ / 列头=不 arm ✓ / 垂直滑块=不 arm ✓ / 结果行单元格=会 arm ✓ / 结果行容器=会 arm ✓`。

### 验证

- 构建 0 错误（36 警告，与上轮持平）；单测 **94/94**；`--selftest-layout --query pdf` 全绿
  （真实 260 行：排序 / 列装配 / 列宽拖拽探针 `510 -> 550 ✓` / 热区可点性 ✓ / 落点过滤 ✓ /
  换位探针双 PASS / Ctrl+B / 落盘往返）。
- 截图 `out/ui-v11-fonts-9pt.png`（"来源" 14 DIP 明显大于内部 12 DIP；中间列统一 9pt）。
- 已重发 `dist\`（11:06）。

### 未完成 / 下次从这里继续

- **真实鼠标手势没能在我这边自动验证成功**（诚实记一笔）：我写了 `tmp\UniSearch\drag-*.ps1`
  用 `SetCursorPos + mouse_event` 真拖，但要定位列头热区必须知道布局，而**名称列是"填充列"**
  （`AutoFitFillerColumn` 每次 SizeChanged 都按 `max(DefaultWidth, avail)` 重算），
  导致两次运行之间的热区坐标并不相同 —— 我的拖拽点落到了列头中部而不是热区上，
  测出来的"没变"是假阴性；另外"宽度是否落盘"要等窗口关闭才写（`OnWindowClosing`），
  也不能作为即时判据。**结论：这一条的最终确认需要用户亲手拖一下。**
- 用户试的时候请分别试两处：① 结果表列头的**右边缘**（10 DIP 热区，hover 会露出强调色竖线）；
  ② 列表右侧的**滚动条滑块**。若仍拖不动，请告知"光标形状有没有变成左右箭头"——
  这一条能直接区分"热区没命中"和"鼠标被别的东西抢走"。

---
### 第 10 轮补记（用户确认修复 + 要求补一个横向滚动手势）

用户："修复了，操作符合预期，不过最好再捕捉一个左右滑动的方式，按照一般习惯，
哪个按键+滚轮可以左右滑动？"

- 采用 Windows 通用习惯 **`Shift` + 滚轮 = 横向滚动**（资源管理器 / 浏览器 / Office 都是它）。
  WPF 的 `ScrollViewer` 默认只把滚轮当纵向、忽略 Shift，所以自己接：
  `OnResultPreviewMouseWheel` → 不带 Shift 直接放行（纵向走默认），带 Shift 则横向滚一格，
  一格的量与纵向一致（`SystemParameters.WheelScrollLines × 16` DIP）。
- 抽了两个可断言的小口子：`IsHorizontalScrollGesture(modifiers)`（Shift 判定）与
  `ScrollResultHorizontally(delta)`（真的滚）；新增探针 `ProbeHorizontalScroll`。
- **踩坑**：`ScrollToHorizontalOffset` 之后立刻读 `HorizontalOffset` 拿到的是旧值（偏移要等一次
  布局才生效）—— 探针一开始因此误报 ✗；补 `sv.UpdateLayout()` 后为
  `向右滚 0 -> 96 ✓`（`CanContentScroll=True Extent=1263 Viewport=702 可滚=561`）。
- `UI-SPEC.md` 快捷键表加了 `Shift`+滚轮 一行；内置帮助文本同步。

---
## 2026-09-20 第 11 轮（Ctrl+F 不该全局独占：改成"只在前台是资源管理器时拦截"）

### 本轮目标（用户原话）

> 有问题，Ctrl+F 似乎是全局监听，我在记事本按这个快捷键也呼出了 Unisearch。

### 根因

`Ctrl+F` 走的是 NHotkey 的 `RegisterHotKey` —— 那是**系统级独占注册**：注册成功之后，
**所有**程序里的 Ctrl+F 都被我们抢走（记事本/浏览器/编辑器的"查找"全失效），
按下时 `OnDirectoryScope` 发现前台不是 Explorer，就按 C6 的约定"静默降级为全局"——
于是用户在记事本里按 Ctrl+F，看到的是 UniSearch 弹出来。用户日志里留有现场：
`16:30:55 [hotkey] 目录限定热键：前台不是 Explorer，退回全局`。

### 改法：低级键盘钩子 + 前台判定

- 目录限定键不再用 `RegisterHotKey`，改为 `BlockingKeyHotkey`（`WH_KEYBOARD_LL`）：
  回调里先做最便宜的短路（是不是那个键码、修饰键是否**正好**相符），
  最后才判断"前台窗口的进程是不是 explorer.exe"，不是就 `CallNextHookEx` **原样放行**。
  只有前台是 Explorer 时才吞掉按键并唤出（吞掉意味着接管 Explorer 自己原本的 Ctrl+F，这是既定语义）。
- `Alt+Win+Space` 继续走 `RegisterHotKey` —— 这个组合键本来就没人用，独占没有副作用。
- 钩子实现的四个坑都写进注释了：① 回调委托**必须存字段**（被 GC 回收 → 钩子静默失效）；
  ② 回调在装钩子的线程（UI 线程）上跑；③ 必须便宜短路，否则撞 `LowLevelHooksTimeout` 被摘掉；
  ④ 长按会连发 KEYDOWN，用 latch 去抖，别唤出几十次。
- 判定逻辑抽成纯函数 `BlockingKeyHotkey.ShouldIntercept(gesture, pressed, vkCode, foregroundMatches)`，
  自检可以直接断言（钩子回调本身没法测）。

### 验证

- **真机按键实测**（`tmp\UniSearch\hotkey-scope-test.ps1` / `hotkey-explorer-test2.ps1`，
  用 `keybd_event` 发真实 Ctrl+F，读 `host.log`）：
  - 前台=记事本（非 Explorer）：新增的"目录限定"日志 **0 条** → Ctrl+F 原样放行 ✓（这正是用户报的场景）；
  - 前台=Explorer 窗口：命中拦截，日志出现"目录限定热键" ✓。
  - （附注：自动化里那次 Explorer 的目录读到的是 null，因此退回了全局 —— 因为脚本是硬把前台
    切到一个句柄很怪的 CabinetWClass 上，且当时有 3 个 Explorer 窗口；手工单窗口场景此前已验证过 C1/C4。）
- `--selftest-hotkey`：`目录限定键钩子已安装=True`；
  `拦截判定（Ctrl+F）：前台=资源管理器 -> True；前台=记事本 -> False；多按 Shift -> False；
  裸按 F -> False；别的键 D -> False` —— 全部符合预期。
- 构建 0 错误、单测 94/94；已重发 `dist\`。
- `UI-SPEC.md` 快捷键表、`MVP.md`（新增 C6b）、设置窗那条说明文字都已改写。



---
### 第 9 轮补记（用户确认修复生效，要求抬到 9pt）

用户看过 `out/ui-v8-final-175.png` 后："这个字号才像 6.4pt / 6pt，这样才调到 9pt 估计差不多。"
—— 即确认"令牌终于落到像素上"，并要求把中间列抬到与资源管理器同档。只动令牌、不动结构：

| 令牌 | 旧 | 新 | 说明 |
|---|---|---|---|
| `Font.Size.ResultRow` | 8.5 | **12** | = 9.0 pt |
| `Font.Size.FilterTab` | 8 | **12** | = 9.0 pt |
| `Font.Size.ColumnHeader` | 8 | **12** | = 9.0 pt |

中间列三档刻意取同一个值：资源管理器与 Everything 的列表文字、列头本来就同档（9pt），
这就是"传统桌面"的观感。行高仍是 20 DIP（9pt 的行盒约 16 DIP，上下各余约 2 DIP，与资源管理器相当）。

验证：构建 0 错误、单测 94/94；`out/ui-v9-9pt-175.png` 像素实测 —— 结果行与筛选标签墨迹
**20~21 px = 11.4~12.0 DIP**（即 9pt）✓。已重发 `dist\`。

遗留（小）：字体变大后，持久化的列宽（尤其"大小"列）显得偏窄，值会被裁成 "2 k"。
列宽是用户自己的布局，拖列头或"恢复默认列布局"即可；是否按 9pt 重算一版默认列宽，等用户定。

---
## 2026-09-26 第 12 轮（结果多选 + 批量动作：压缩为 ZIP）

### 本轮目标（用户原话）

> 多选很需要，搜索出结果有可能多选然后压缩文件的。

### 状态总览

| # | 事项 | 状态 | 验证方式 |
|---|---|---|---|
| 1 | 结果表多选（Ctrl/Shift 点击、Ctrl+A、Esc 先清选择） | ✅ | `--selftest-multiselect` 三态断言 |
| 2 | 多选右键分发（保住整份选择 / 未选中则重置单选） | ✅ | `DecideRightClick` 纯函数断言 |
| 3 | 批量动作：**压缩为 ZIP** | ✅ | 单测 12 条 + `--selftest-archive` 真压回读 |
| 4 | 批量动作：复制 N 个路径 / 引号列表 / 名称 / 终端 | ✅ | 剪贴板行数与引号数断言 |
| 5 | 压缩设置项 + 设置窗「多选与压缩」节 | ✅ | `--selftest-settings` 往返 + 控件值日志 |
| 6 | 路 B PoC：跨目录多选走原生 shell 菜单 | ❌ **不可行** | `--selftest-shellmenu-multi`（系统返回 `E_FAIL`） |

### 已完成并验证

细节见 `tmp\UniSearch\worklog-第12轮.md`；提交：`243779a`（多选 UI）、`42ccca6`（右键分发）、
`218276a`（压缩）、`38009e0`（设置项）、`8cf3b76`（设置窗）、`2978338`（PoC）。

- **多选模型**：`Selected` 语义**不变**（= 多选里的第一个），新增
  `SelectedItems / SelectionCount / HasMultiSelection / SelectionSummary`；因此预览、快捷键、
  单选右键那套零改动。`SelectionMode=Extended`；**Esc 先取消选择、再按一次才收窗口**。
- **压缩**：`Core.Archiving.ArchivePlanner`（包名模板 `{parent}-{count}项-{yyyyMMdd-HHmm}`、
  重名自动加 ` (2)`、zip 条目名规划：同名冲突 → `父目录名/文件名` → 计数兜底，分隔符统一 `/`，
  **绝不静默覆盖**）+ `Host.Services.ArchiveService`（目录递归、空目录写 `/` 条目、失败清单）。
  批量菜单首位「压缩为 ZIP(&Z)…」：落点 = 第一个选中项所在目录，后台线程压缩，完成后状态条报条目数
  并在资源管理器里定位。
- **设置**：`archive.destination`（`same-as-first` / `ask`）、`archive.nameTemplate`、
  `archive.revealAfter`、`archive.maxItems`（默认 200，防手滑），设置窗新增「多选与压缩」节。
- **单测**：**106/106**（原 94 + `ArchivePlannerTests` 12 条）。

### 未完成 / 下次从这里继续

- **同目录多选改走原生 shell 菜单**（PoC 已证明**可行**：同目录多选实测拿到 4 个动词）——
  能白拿 Win11 的「压缩为 ZIP 文件」与 7-Zip 等第三方动词。跨目录不行（系统 `E_FAIL`），
  所以只能是"同一目录时走原生、否则走我们自己的批量动作"。
- **真实鼠标手势仍需人手确认**：Ctrl/Shift 点选、右键批量菜单、压缩落点与结果。
- **~~`--dump-settings` 拍出空白图~~ 已解释（见下方补记）**：会话**没有活动桌面**时
  `RenderTargetBitmap` 拍出来就是空白；会话 Active 时一切正常。
- 分类专属右键项（原 B3）与 `actions.json` 外置**还没做**：本轮只做了"多选 + 压缩"这条主线。

### 新踩的坑

1. **残留的旧 UniSearch 进程会让 `--selftest-*` 静默不跑**：单实例机制把新进程的启动"唤醒"给已在运行的
   实例，新进程 `exit=0`、日志不新增 —— 现象是"自检跑了但什么都没输出"。
   **跑自检前先 `Get-Process UniSearch | Stop-Process -Force`。**
2. **`edit` 吞掉注释行的行尾换行，会把下一行代码并进注释**：C# 不报错（整行都是注释），
   只有断言失败才暴露（现象："清空：count=3"）。改注释行别把换行一起删掉。
3. **屏幕坐标两套 API 要求相反**：`TrackPopupMenuEx`（shell 菜单）只认**物理像素**；
   WPF `ContextMenu.Placement=AbsolutePoint` 用 **DIP** —— 175% 缩放下不换算就偏出去一大截。
4. **`IShellItemArray.BindToHandler`（非泛型）抛 `InvalidCastException`**：它内部走
   `Marshal.GetObjectForIUnknown`，对不支持 IDispatch 的接口不适用；要用 Vanara 的泛型扩展
   `Shell32.BindToHandler<T>(array, null, BHID…)`。另外 Vanara 的 `Shell32.BHID` 常量**不是 `Guid`**，
   不能直接当 `in Guid` 传。
5. 日志实际写在 `%LOCALAPPDATA%\UniSearch\host.log`（`spec\MVP.md` 的 F5 原写 `logs\` 子目录，本轮订正）。

### 验证手段变化

- 新增 `--selftest-multiselect`：选择模型三态 + 右键落点决策 + 批量菜单项 + 剪贴板行数/引号数 + 压缩设置合法性；
- 新增 `--selftest-archive`：造小树 → 真压 → 读回核对条目名与内容；
- 新增 `--selftest-shellmenu-multi`：跨目录多选的原生菜单 PoC（只列动词不弹菜单）；
- `--selftest-settings` 增加压缩节的往返断言；`--dump-settings` 增加控件值日志（截图之外的可断言证据）。

---

### 第 12 轮补记（2026-09-26 深夜：锁屏后复拍 GUI + D1 现场）

用户报"虚拟机刚锁屏"，要求重新调 GUI。重跑了一遍 GUI 渲染与布局自检：

| 项 | 结果 |
|---|---|
| `--dump-settings`（设置窗长图） | **正常出图**（1003×2750，302 KB）——「多选与压缩」节完整：说明文字、落点下拉（第一个选中项所在目录）、包名模板、完成后定位、项数上限 200 |
| `--dump-render --query txt`（主窗） | **正常出图**（2240×1155，193 KB）—— 左栏"Everything / 260 条"、筛选标签"全部(260) 文件夹(260) …"、260 行结果、右栏文件夹预览、状态条"找到 260 条" |
| `--selftest-layout --query txt` | 全绿（列装配 5/6/5、列宽 275、列宽拖拽 710→750、横向滚动 0→96、落点过滤、换位双 PASS、Ctrl+B、落盘往返） |

**结论 1：上一轮那张空白图不是 bug** —— 会话没有活动桌面（锁屏/断开）时
`RenderTargetBitmap` 渲染出来就是空白（尺寸对、内容空）。会话 Active 后同一命令立刻正常。
以后判断"图是不是空白"，先确认会话状态（`query session`）。

**结论 2：顺手撞上了 D1 的现场（Everything 偶发超时）** —— 第一次 `--dump-render` 拍到的正是它：

- 左栏来源显示 `Everything` 状态 **"超时"**（`ProviderOutcomeState.Timeout`）；
- 状态条停在 **"搜索中…"**，结果区是"没有匹配的结果"（0 行）；
- 同一命令**重跑一次即正常**（260 行，1s 内到齐）。

这与第 8/9 轮记的"命令行冷启动查 Everything 偶发不返回（正常 ~1s / 异常 12s 内一条快照都没有）"
是同一个现象，而且这次抓到了**界面上的表现**：来源栏标"超时" + 状态条"搜索中…"。
D1 排查可以从这里入手（超时判定发生在哪一层、为什么不重试）。

---

## 2026-09-28 第 13 轮（筛选器模板 F0 + F1：Core 层 + 模板锚点）

### 本轮目标

接 [US-13]（任务 `TASK-2026-09-27-07`）：**F0** = `filters.json` v2 的 `templates` 节（schema +
两文件独立合并 + 激活解析链 + 兼容回退，纯 Core）；**F1** = UI（模板锚点、切换、钉住/恢复默认、
按来源自动跟随、settings 持久化）。
依据 `docs/spec/FILTER-TEMPLATES.md`（本轮随文档一起入库 git）。

### 状态总览

| 事项 | 状态 | 验证方式 |
|---|---|---|
| 3 份未入库文档提交 | ✅ | `4b182ae` / `2606d21` / `704ea83` |
| `FilterTemplate` 记录 + v2 schema | ✅ | `src/UniSearch.Core/Filters/FilterTemplate.cs` |
| 两文件**独立**合并（filters 与 templates 各自合并） | ✅ | 单测 ×1 |
| 引用校验 / `defaultFor` 冲突 | ✅ | 单测 ×2 |
| 激活解析链（钉住 → 部署级 → `defaultFor` → `"*"` → 平铺） | ✅ | 单测 ×1 + 自检 ①②⑥ |
| 双重显隐 + 模板内引用顺序 | ✅ | 单测 ×1 |
| **没写 `templates` 节 = 现状** | ✅ | 单测 ×1 + **主窗渲染与改造前逐像素相同**（SHA256 一致） |
| 模板锚点（标签栏最左）+ 下拉 | ✅ | `out/tpl-anchor.png`（有节）/ `out/tpl-none.png`（无节，锚点消失） |
| 钉住 / 恢复默认 / 落盘 | ✅ | 自检 ③④⑤（含落盘回调参数断言） |
| 按来源自动跟随 | ✅ | 自检 ②④ |
| 切模板不重查 | ✅ | 自检 ③（查询次数前后不变） |
| 单测 | **112/112**（原 106 + 6） | `dotnet test -c Debug` |
| F2（热重载） | ⬜ 下一轮 | — |
| F3（下推映射 + Zotero 模板） | ⬜ 与 Zotero Provider 同期 | — |

### 已完成并验证

**F0（Core）**

- **`FilterTemplate`**（新文件）：`id` / `name` / `order` / `filters` / `defaultFor` / `providers`，
  只引用不复制；`AvailableFor(providerId)` 管"下拉里出不出来"，`DefaultFor` 管"认领谁是默认"。
- **`FilterFile.Templates` 声明成 `List<FilterTemplate>?`**：`null` = 这个文件根本没写这个节 ——
  与"写了空数组"必须区分开，否则"没写 = 回退现状"这条保证根本没法表达。
- **合并**：`filters` 与 `templates` **各自独立合并**（同 id 后者整条覆盖，不做字段级合并）。
- **校验放在全部文件合并完之后**：模板引用的定义可能只在另一份文件里，边读边校验会误判成"不存在"。
- **解析链** `ResolveTemplate(providerId, pinned, deployment)`：钉住 → 部署级 → `defaultFor` 认领
  → `defaultFor: ["*"]` → **null（回退平铺）**。认不出的 id 不生效、直接往下一步走。
- **`ForTemplate(template, providerIds)`**：模板引用 ∩ 显隐规则，顺序以模板里的**引用先后**为准。

**F1（UI / 设置 / 自检）**

- **模板锚点**：标签栏最左一枚（漏斗图标 + 当前模板名），`ContextMenu` 下拉（`PlacementTarget`
  取回 VM，与结果行右键同一套办法）；**没有 `templates` 节时整块不显示**。
- **`settings.filterTemplates.<pid>`**：手动切模板即钉住并落盘（`notify: false`，不触发全量重放）；
  「跟随后端默认」删掉该键、回解析链。`Normalize` 里键值规范成小写、空值直接删（"钉了空串" = 没钉）。
- **部署级默认 `providers.<id>.options.filterTemplate` 真被读起来了** —— 遗留清单 C4 点名的那个
  "没人读的口子"，现在排在"用户钉住"之后、"`defaultFor`"之前。
- **多后端口径（本轮拍板）**：`TemplateProviderId` 只在**限定到某一个后端**时才给出 id；
  默认集合填了多个 / 清空 → `null` → 走 `"*"` 兜底。**不为多后端设计模板语义**（用户明确要求）。
- **切模板不重查**：`Apply()` 里的标签栏构建拆成 `BuildTabs()`（内置分类来自缓存的 `_groups`，
  自定义筛选器来自模板），模板切换只重跑这一段；为此新增 `SearchRequestCount` 供自检断言。
- **藏掉当前筛选器时的兜底**：新模板里没有当前选中的自定义筛选器 → 退回「全部」。不处理的话
  标签栏一个高亮的都没有、查询串却还在按它过滤（"结果莫名其妙少了一大截"那种最难查的坏法）。

### 未完成 / 下次从这里继续

- **F2**：`FileSystemWatcher` 热重载（吸收遗留 C3）。
- **F3**：下推映射 + Zotero 模板示例（与 [US-15] Zotero Provider 同期）。
- 设置窗里的**模板编辑器**（勾选定义入模板、拖动排序、指定 `defaultFor`，写回用户那份 `filters.json`）——
  设计文档 §5 提到，但不在 F1 验收里，仍未做。
- 程序自带的 `filters.json` 还没加 `templates` 节：等 F3 有真实模板内容（文件查找 / 文献查找）再一起加。

### 新踩的坑

1. **`"*"` 不能过筛选器那套 id 规范化**：`NormalizeId` 把非字母数字一律换成 `-`，
   `defaultFor: ["*"]` 会静默变成 `"-"`，全局兜底失效。后端 token 得单独走
   `NormalizeProviderToken`（保留 `*`）。而模板**引用**上正好相反 —— 模板里写 `"Bio Info"`
   必须规范化成 `bio-info` 才引用得到，否则是"看着写对了却引用不到"。
2. **`if (file?.Filters is null) continue;` 会顺手吃掉 templates**：那个早退是按"filters 是必填节"
   写的，加了可选节之后它就成了 bug 源 —— 文件里只写 `templates` 时整份被跳过。
3. **自检里断言"标签栏 = 全部, bio"是错的**：`CategoryTab.Id` 用的是 `CategoryIds.All`（`all`），
   "全部"只是显示名。第一遍 7 项里就这一条挂 —— 断言要对着 id 写，不是对着界面上看到的字写。
4. **`--dump-render` 的值就是输出路径**（`--dump-render <png>`）：写成 `--dump-render --query txt`
   会把 `--query` 当成文件名，日志里是"离屏渲染完成 -> --query"，图根本没出。
5. （工具坑）pwsh 里 `git commit -m "...\"*\"..."` 的反斜杠转义不成立，消息被拆成 pathspec ——
   代码与文档挤进同一个提交、消息还只写了文档。**多行 / 带引号的提交消息一律用 `-F <文件>`。**

### 验证手段变化

- `dotnet test`：106 → **112**（新增 6 条：无节回退逐项比对 / 合并覆盖 / 引用校验 /
  `defaultFor` 冲突 / 解析链优先级 / 双重显隐与引用顺序）。
- 新增 **`--selftest-templates`**：造临时 `filters.json`（3 个模板），驱动 VM 断言 7 项 ——
  默认集合单后端认领、切来源自动跟随、钉住 + 落盘回调参数、钉住后不跟随、恢复默认回解析链、
  多后端走 `"*"` 兜底、藏掉当前筛选器时退回「全部」；外加"切模板不重查"（查询次数不变）。
  **不落盘、不动用户配置**，结束时目录/钉住表/回调/来源原样还原。
- `--selftest-filters` 增补一行模板状态（`模板 N 个，当前生效=[…]（解析后端=… 钉住=… 下拉可选=[…]）`）。
- 渲染证据：`out/tpl-anchor.png`（有 templates 节 → 锚点「文件查找」在标签栏最左，
  且模板没引用的「笔记本/配置」不出现）、`out/tpl-none.png`（删掉后锚点消失，
  **SHA256 与第 12 轮的 `r12-main2.png` 完全相同** = 逐像素没变）。

---

## 2026-09-28 第 14 轮（US-14 / US-15：AnyTXT 与 Zotero 两个后端落地）

### 本轮目标

把两个真后端接上：**AnyTXT**（全文内容搜索）与 **Zotero 10**（文献元数据 + PDF 全文）。
用户原话："把两个后端做出来"，外加一条 —— **搜索框右侧那个按钮要能把当前查询传给对应的软件**。

### 状态总览

| 事项 | 状态 | 验证方式 |
|---|---|---|
| AnyTXT Provider（传输 / 翻译 / 映射 / 定位） | ✅ | `src/UniSearch.Providers.Anytxt/` 四个文件 |
| Zotero Provider（同上） | ✅ | `src/UniSearch.Providers.Zotero/` 四个文件 |
| 两个 Provider 注册进 Host | ✅ | `App.xaml.cs` 的 `_entries` |
| 「直达后端程序」带查询 | ✅ AnyTXT / ❌ Zotero | AnyTXT：`ATGUI.exe /s "<关键词>"`（实测）；Zotero 没有这种口子，如实只唤起 |
| 单测 | **183/183**（原 112 + 71） | `dotnet test -c Debug` |
| `--selftest-anytxt` | ✅ 全部通过 | 打真服务 9924/rpc |
| `--selftest-zotero` | ✅ 全部通过 | 打真服务 23119/api |
| `QUERY-SYNTAX.md` §4 更新 | ✅ | 原先写"Zotero 只用 q.Text"，已按实测改写 |

### 已完成并验证

**AnyTXT（`anytxt`）**

- 端点 `http://127.0.0.1:9924/rpc`，方法命名空间 `anytxt.v1`，**params 直接给对象**。
  ⚠ 9920 是内部 QJsonRpc 接口（`ATRpcServer.Searcher.V1.*`、params 要单元素数组），**不是给我们用的**。
- **全盘搜索逐盘枚举**：`filterDir` 传空会被服务端强制成 `C:`（回显证实），不逐盘问的话
  D 盘的东西永远搜不到、而且用户看不出哪里不对。逐盘明细有日志与
  `LastDriveBreakdown` 可查 —— 只看合并后的行数区分不出"两个盘都问了"和"只问了 C 盘"。
- 行解析**按 `output.field` 查名**，不按下标硬编码（服务端加字段时不会静默读错列）。
- `fid` 当字符串存 Metadata（无符号 64 位，过数值类型会掉精度）。
- **`errno` 不当失败信号**：实测有结果时也可能是 1。
- 下推不了的条件（正则/大小/文件夹/类型）逐条记 Notes 如实降级。

**Zotero（`zotero`）**

- 本地 API `http://127.0.0.1:23119/api/`，读请求无需鉴权；Zotero 10 的版本头 `X-Zotero-Version`。
- **能下推的只有三样**：`q=`、`itemType=`、`collection/`。
  ⚠ 扩展名/大小/日期范围 **API 根本不支持**，而且**未知参数会被静默忽略** ——
  顺手塞个 `date=` 进去不会报错，只会让人以为在筛。有单测专门钉住"绝不塞日期参数"。
- **刻意不声明 `SupportsKindFilter`**：调度器把它当"能吃 `ext:` 过滤"，声明了 `ext:pdf`
  就会白跑一趟 Zotero 再把结果全丢掉。
- **刻意不实现 `IFileSystemScopedProvider`**：调度器的规则正是"目录内搜索跳过不实现它的 Provider"，
  那正是要的语义。集合限定走 `NamedScope`。
- 附件行的 `file:///` enclosure **必须解码**，否则右键/预览拿着 `%20` 去找一个不存在的文件。
- 作者摘要优先用 Zotero 自己的 `meta.creatorSummary` —— 两边算法不一致的话，
  同一个条目在 Zotero 里和在 UniSearch 里会显示成两个样子。

**「直达后端程序」**

- AnyTXT：`ATGUI.exe /s "<关键词>"`。命令行是从程序自己的**「命令行帮助」对话框**里挖出来的
  （传未知参数它会弹这个），实测窗口标题从 `pdf - …` 变成 `uia-probe-关键字 - …`，复用同一进程。
  顺带发现 `/st 4` = 正则匹配 —— RPC 的 `pattern` 不暴露正则，但程序本身支持。
- Zotero：`zotero://` 协议只支持定位到某个对象（`select/library/items/<KEY>`），**没有搜索形式**。
  所以按钮如实只做"带到前台"，真正的"跳过去看这一条"是每条结果上的「在 Zotero 中打开」动作。
  **不假装传了查询。**

### 未完成 / 下次从这里继续

- **标签筛选器**（用户已定口径：小三角展开、顶部只显示已选）需要一个**"值域型筛选器"**模型
  （候选值来自后端）。Zotero 的 `tag=` 支持布尔（`||` 或、`-` 非、多个 `tag=` 是 AND），
  接口已经就绪，缺的是筛选器模型那一层。
- AnyTXT 的 **Snippet 二段式**（首屏出行 → 异步补 `getFragment`）**没做**：协议侧已摸清
  （`*<<*命中*>>*` 标记），但需要先确认 Broker 的融合能正确处理"同一行二次 emit"。
- 程序自带的 `filters.json` 还没加 `templates` 节（等真有 2～3 套模板再一起加）。
- F2 热重载（遗留 C3）、F3 下推映射。

### 新踩的坑

1. **请求路径少了 `/api` 前缀 → 全 404**：客户端现在容忍用户把 `/api` 一起填进来，否则
   拼成 `/api/api/...` 也是 404，而两种 404 从表面上看不出区别。
2. **健康探测按 JSON 解析 `GET /api/`**：它返回的是纯文本 `Nothing to see here.`（Zotero 的玩笑），
   解析当场抛异常 → "API 明明好好的"被误报成不可用。探测只该看状态码与响应头。
3. **自检断言不能依赖"某一行的内容"**：AnyTXT 的索引是**活的**（后台一直在重建），
   上一秒命中 3 行的目录、下一秒可能 0 行 —— ④ 原本只拿第一行的目录验 `filterDir`，偶发变红。
   改成按行逐个试，只把"全部落在目录内"当硬断言。**断言要依赖结构，不要依赖索引内容。**
4. **没标签时连来源徽标都没了**：`BuildTags` 原本在没有 tag 时返回空列表，
   把"这条来自 Zotero"这个每行都该有的信息也一起丢了。
5. 作者摘要自己拼成 `Jung Woo-Bin 等`，与 Zotero 的 `Jung 等` 不一致 —— 该用后端算好的那个。

### 验证手段变化

- `dotnet test`：112 → **183**（AnyTXT 32 条 + Zotero 39 条，全部脱机）。
  Zotero 的样例 JSON **照真实响应抄**，不是编的 —— 编的样例只会证明"我以为的形状是对的"。
- 新增 **`--selftest-anytxt`**：健康 / 索引状态 / 逐盘明细 / `filterDir` 限定 / 不存在的词 0 条。
- 新增 **`--selftest-zotero`**：健康+版本 / `qmode` 差异（1 → 5 行，证明全文确实多搜到了）/
  **附件路径全部在磁盘上真实存在**（URL 解码对不对的唯一硬证据）/ 集合确实收敛（19 vs 106）。
- 两个 Provider 的翻译器与映射器都开 `InternalsVisibleTo` 给测试，而不是把它们全改成 public。

---

## 2026-09-29 第 15 轮（筛选器体系收尾：静默失效 / 按后端隐藏 / 值域筛选器）

### 本轮目标

接第 14 轮的"未完成"：用户报"筛选器点了不起作用"，修完之后把**最后一项由用户亲自设计的
UI** —— Zotero 标签的"小三角展开"做出来。

### 状态总览

| 事项 | 状态 | 验证方式 |
|---|---|---|
| 筛选器静默失效（点「期刊论文」= 点「全部」） | ✅ 已修 | `--selftest-tabs`，206 单测 |
| 内置分类按后端隐藏（AnyTXT 下的「正文命中」恒等于全部） | ✅ 已做 | `--selftest-tabs` 打印三后端标签栏 |
| **值域筛选器**（Zotero 标签：小三角展开 / 顶部只回显已选 / 下推 `tag=`） | ✅ 本轮 | `--selftest-facets` 14 项 + **UIA 真实鼠标点击** |
| F2 热重载（遗留 C3） | ⬜ 未做 | — |
| AnyTXT Snippet 二段式 | ⬜ 未做 | — |

### 已完成并验证

**1. 筛选器静默失效的真凶：`QueryParser.WithFilters` 只换了 `ProviderText`，没写回 `Filters`**

一个赋值漏了，症状是"点筛选器得到和「全部」一模一样的结果，而没有任何地方看得出哪里错了"。
Everything 读 `ProviderText` 所以**看起来是好的**，AnyTXT / Zotero / Core 的后过滤全部静默失效。

**2. 内置分类按后端隐藏**

AnyTXT 的索引只收了 txt/md，于是「图片/视频/音乐/应用/压缩包/正文命中」在它下面**恒等于全部** ——
点下去数字不变，像坏了。现在按后端逐个写死（`CategoryEngine.AppliesToProvider`），
但**「全部」永远保留**（那是回去的路）。

**3. 值域筛选器（本轮主体）**

见 `spec/FILTERS.md`「值域筛选器」与 `research/ZOTERO-LOCAL-API-VERIFIED.md` §4.10。
要点：

- 新 SDK 能力 `IFacetProvider`（候选值来自后端）+ `QueryFilters.Facets`；
- `ProviderSelector` 对带值域选择的查询**跳过不懂值域的后端**，且**不做前端近似过滤**；
- UI：锚点「标签 ⌄」在最左，展开面板列全部候选（带计数），顶部只回显已选值（带 ×）；
- 默认「任一命中」，可切「全部命中」（两种口径在 URL 上写法不同，见下）；
- 换来源清空选择；判据用 `HasFacetSelection`，空壳不算。

### 未完成 / 下次从这里继续

- **F2 热重载**（改 `filters.json` 免重启，遗留 C3）—— 值域筛选器与它无关，仍然缺；
- **AnyTXT Snippet 二段式**：协议已摸清（`*<<*命中*>>*`），要先确认 Broker 融合能处理"同一行二次 emit"；
- **候选值超过一两百个怎么办**：现在展开面板是一个纯列表（Zotero 17 个标签刚好）。
  真到上百个时，面板里需要加一个过滤框 —— 现在**刻意不做**（YAGNI），但这是已知的规模上限；
- 「附件」与内置分类的关系：`zot-pdf` 筛选器（`subtypes=["attachment"]`，51 条）与内置「文档」
  是两回事，界面上暂时看不出区别，将来要么合并要么改名。

### 新踩的坑

1. **`AutomationProperties` 挂在没有 AutomationPeer 的元素上等于没挂**：我给已选 chip 的外层
   `Border` 设了 `AutomationId="FacetChip"`，UIA 里死活找不到 —— `Border` 不生成 peer。
   挪到里面的 `TextBlock` 上就有了。**"看不见"要先怀疑自己挂错了地方，而不是功能坏了。**
2. **属性漏一个 `OnPropertyChanged`，症状是"图标在、文字没了"**：`FacetLabel` 忘了通知，
   绑定只在首次求值一次（那时还是 Everything，值是空串），切到 Zotero 后不会重读。
   而 `FacetGlyph` 有 fallback 恰好同值，所以**只有文字消失**，看着像"设计成这样"。
3. **UIA 数 ListView 的行数是假的**：选了 AAV 时数出 1（真实），清空后数出 25 —— 那是
   **虚拟化后已实现的容器数**，不是行数。用 UIA 验证结果集大小要靠状态条文本，不能数 DataItem。
4. **`tag="蛋白设计"` 返回 0 条**：官方文档写 `tag="exact phrase"`，本地端点不认引号
   （详见 §4.10.2）。**照文档写的代码不一定对，照实测写的才对。**
5. **构建被正在运行的实例锁住**：`dotnet build` 报 MSB3027/MSB3021 而自检照跑 ——
   于是**验证的是上一版 exe**（filters.json 的改名一直没生效，差点被当成"改名失败"）。
   **先杀进程再构建**，且看到 MSB3027 就不能把结果当真。

### 验证手段变化

- `dotnet test`：183 → **217**（值域下推 9 条 + 调度 2 条）。
- 新增 **`--selftest-facets`**：14 项断言。**断言的是后端报的总数，不是行数** ——
  行数受结果预算限制（Zotero 60 条），拿行数断言分不出"筛对了"和"没筛但恰好少"。
  标签名与计数现场从后端取，不写死（写死一条 `蛋白设计=3`，第二天就是与功能无关的假红）。
- **UIA + 真实鼠标点击**（`tmp/facet-ui-verify.ps1`）：自检驱动 VM 会绕过 XAML 绑定与命令路由，
  上一轮就是这么"自检全绿、用户说不行"的。本轮用真点击验证了整条链：
  切来源 → 点小三角（`ToggleState=On`）→ 17 个候选渲染 → 点「AAV」→ 顶部出现 chip + 结果 1 行 →
  点 chip 上的 × → chip 归零。

---

```markdown
## YYYY-MM-DD 第 N 轮（目标 goal-xxxx）

### 本轮目标

### 状态总览（表：事项 / 状态 / 验证方式）

### 已完成并验证

### 未完成 / 下次从这里继续

### 新踩的坑

### 验证手段变化
```
