# 参考仓库 3：ramensoftware/windhawk-mods

- 本地路径 `D:/tools/refs/windhawk-mods`，603 个 mod（`mods/<id>.wh.cpp`，每 mod 一个文件）
- Windhawk 本体：**GPL-3.0** 的 Windows 注入/hook 框架（`wh.exe`/注入器 + 每个目标进程里加载 mod 源码编译出的 dll）
- 与本项目的关系：**只在阶段三（接管/隐藏 Explorer 原生搜索入口）需要它**；阶段一/二完全不碰注入

## 0. 许可证规则（先纠正一次保守误判）

`README.md` 原文：
> Mods which don't specify a license are submitted under **the MIT license**. It's the author's responsibility to specify the appropriate license for third-party code.

→ 没有 `// @license` 头的 mod（470/603）**默认就是 MIT**，可以复制；不必当成"无授权"。
→ 但要逐个看是否内嵌他人代码，例如 `explorer-details-better-file-sizes.wh.cpp` 明确写了
"based on code from SizeES: A Plugin for Fast, Persistent FolderSizes in x2 via Everything Search" —— 这类要看上游。
→ 另外 GPL 只约束 Windhawk 本体；mod 是**独立作品**，用 MIT mod 不会把我们的 C# 项目变成 GPL。
但如果将来发布一个 mod 作为分发物，需要注意 Windhawk 的加载/编译流程与它的条款。

## 1. 与"接管 Explorer 搜索框"直接相关的 mod 索引

| mod 文件 | 行 | 机制（可直接复用的知识点） |
|---|---|---|
| `legacy-search-bar.wh.cpp` | 120 | **符号 hook**：`LoadLibraryW("ExplorerFrame.dll")` + `WindhawkUtils::SYMBOL_HOOK`，用 **undecorated 签名字符串**定位私有成员函数并替换：<br>`private: bool __thiscall CUniversalSearchBand::IsModernSearchBoxEnabled(void)` → 返回 false 即退回 DirectUI 老搜索框；<br>`public: long __thiscall CSearchEditBox::HideSuggestions(void)`。这就是"原生搜索框区域可改"的直接证据 |
| `hide-search-bar.wh.cpp` | 204 | **窗口层隐藏**（不需要符号）：`FindChildWindow(hwnd, "TravelBand")` → `GetParent` 拿到 rebar → `SendMessage(rebar, RB_IDTOINDEX, 4/5, 0)` + `RB_SHOWBAND` + `RedrawWindow(RDW_UPDATENOW|RDW_ALLCHILDREN)`；触发时机是 subclass 里的 `WM_SIZE`(22H2+) 与 `WM_PARENTNOTIFY && LOWORD(wParam)==1`(子窗口创建)；用 `SetWindowSubclassFromAnyThread` 从 hook 线程挂到 UI 线程窗口 |
| `remove-command-bar.wh.cpp` | 110 | 直接移除 Win11 命令栏（同上思路的更粗粒度版本） |
| `explorer-command-bar.wh.cpp` | **5161**（`@license MIT`） | 在 `CabinetWClass` / `ShellTabWindowClass` 里**自己造一个命令栏/工具栏并塞进 Explorer**的完整做法。我们要"把 UniSearch 的搜索框长在 Explorer 上"时，这是唯一一份可复制的长参考实现 |
| `explorer-ctrln-newfile.wh.cpp` | 1415 | **在 Explorer 里截获加速键**（把 Ctrl+N 变成新建文件）：消息循环/hook 里判 `GetAsyncKeyState(VK_CONTROL/VK_MENU/VK_SHIFT/'N')`；**`WaitForModifierKeysReleased()` 轮询 40×5ms 等修饰键抬起**再用 `SendInput` 重放 —— 我们吞掉 Ctrl+F 后自己发键时**必须**遵守这个模式，否则系统认为组合键仍按住 |
| `explorer-ctrlq-new-folder.wh.cpp` | 461 | 同类加速键替换，规模更小，适合当"最小可运行 mod"样板 |
| `classic-theme-explorer-search-fix.wh.cpp` | — | 经典主题下搜索框不可用的修补，说明搜索框实现随主题/版本而变 |
| `aerexplorer.wh.cpp`、`win32-ui-modernizer.wh.cpp` | 大 | 也触及 `CSearchBox`/DirectUI 控件层，作为交叉参考 |
| `explorer-details-better-file-sizes.wh.cpp` | 2999 | **`explorer.exe` 进程内查询 Everything** 的生产级样本：动态加载 SDK（1.5.0.1384a+ 走 **Everything3/SDK3**，否则 1.4），要求用户在 Everything 里开 "folder sizes index"；注释里给出配置路径。证明 `explorer → Everything` 链路稳定 |
| `add-virtual-folders-to-nav-top.wh.cpp` | — | 导航窗格注入，若日后做"UniSearch 保存的搜索 → 侧边栏"可参考 |

## 2. 取"当前目录"的两条路（都已在本地看到实现）

| 路线 | 出处 | 特点 |
|---|---|---|
| **A：不注入**，从外部用 Shell COM | Flow 的 `WindowsExplorer.cs`（见 REF-2 §2.1）；`explorer-ctrln-newfile.wh.cpp` 的 `ResolveActiveShellView()` 是同一套（`CoCreateInstance(CLSID_ShellWindows)` → `get_Count` → `IWebBrowser2` → `FindWindowEx(...,"ShellTabWindowClass",...)` 选活动 tab → `QueryService(IShellBrowser)` → `IShellView`，STA 线程里取句柄） | ✅ **UniSearch 采用这条**。跨进程、无需 Windhawk、Win11 多标签可用；坑是 STA + COM 释放 |
| B：进程内 hook，拿 `IShellView`/`CShellBrowser` 私有符号 | Windhawk mods | 只在需要"原生搜索框点击接管"或"直接改 Explorer 输入行为"时才用；Windows 更新后符号漂移即失效，维护成本高 |

## 3. 用 Windhawk 的风险清单（为什么它是阶段三而不是阶段一）

1. **符号 hook 依赖签名匹配**：undecorated 字符串随 Windows 版本/补丁变化（mod 普遍用 `#if defined(_M_X64)` + 多版本回退表）。
2. 搜索框实现至少三套：DirectUI（旧）、EdgeHTML/XAML 版（Win11 现代）、以及 24H2 之后的命令栏内嵌 —— 同一份代码要分版本走不同分支（`IsWindows11Version22H2OrHigher()` 这类判断满天飞）。
3. 崩溃半径在 `explorer.exe` 内：任何一次 P/Invoke 出错都是资源管理器重启。
4. 分发依赖用户装 Windhawk（GPL 客户端），且 mod 需要联网/本地编译。

→ 因此本项目对 Shell 层的纪律保持不变：**注入层只做三件事**（判断是否 Explorer/桌面、取当前路径、把 Ctrl+F/搜索框点击转出去），
通过**命名管道**与 `UniSearch.Host` 通信；UI、Provider、排序、融合全在独立进程。
Windhawk 路线如果最终采用，我们的 mod 也只承载这三件事 + 一个"隐藏原生搜索框"开关（复用 §1 的 `hide-search-bar` 做法）。

## 4. 我们自己的协议边界（对照 mod 侧要实现什么）

```
[explorer.exe 内的 mod / 或外部 ShellContext 监听]
   ├─ 前台窗口是 CabinetWClass / ExploreWClass / PassportApp_* / shell_TrayWnd(桌面)？
   ├─ 是 → 取当前 tab 路径（路线 A）
   ├─ Ctrl+F / 搜索框获得焦点 → 向管道写一条 ContextEvent
   └─ (可选) 收到 HideNativeSearchBar 指令 → RB_SHOWBAND
          │  \\.\pipe\UniSearch.ShellBridge  （JSON 行协议，见 docs/spec/SHELLBRIDGE.md）
          ▼
[UniSearch.Host] → SearchContext{Mode=FileSystem, Scope=CurrentDirectoryRecursive, RootPath=…}
```

阶段二先用**纯外部实现**（`RegisterHotKey` 全局热键 + 路线 A），跑通后才决定要不要 Windhawk；
这意味着本项目**可以在完全不写 native 代码的情况下发布 v0.1**。

## 5. 待办（读代码时按需回来看）

- [ ] 抄 `hide-search-bar.wh.cpp` 的 band 索引枚举方式，做一个"隐藏原生搜索框"实验 mod
- [ ] 精读 `explorer-command-bar.wh.cpp` 的窗口插入与布局（阶段三真正动手时）
- [ ] 检查 `explorer-details-better-file-sizes.wh.cpp` 里 SizeES 的上游许可证（若要复制其 Everything 调用片段）
