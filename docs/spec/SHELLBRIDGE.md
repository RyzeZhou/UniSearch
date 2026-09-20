# ShellBridge / ShellContext 规格

> 目标：**在哪个 Explorer 目录里按 Ctrl+F，就以那个目录为搜索范围**；其他位置用全局快捷键走全盘。
> 决策：阶段一/二 **完全不注入 explorer.exe**（非注入路线已被 Flow Launcher 的 DialogJump 验证，见 `docs/research/REF-2-flow-launcher.md` §2.1）。
> Windhawk 只留给阶段三（接管/隐藏原生搜索框），见 `docs/research/REF-3-windhawk-mods.md`。

## 1. 进程与职责边界

```
┌─ UniSearch.Host.exe (WPF, 单实例, 托盘) ────────────────────────┐
│  HotkeyGate        RegisterHotKey + WM_HOTKEY 消息循环           │
│  ShellContext      前台窗口判定 + 当前目录解析（COM，不注入）      │
│  Broker            Provider 调度/融合/分类/排序                   │
│  UI                紧凑结果窗口（docs/spec/UI-SPEC.md）           │
│  PipeServer        \\.\pipe\UniSearch.ShellBridge（供外部/未来 mod）│
└──────────────────────────────────────────────────────────────────┘
          ▲                        ▲
          │ WM_HOTKEY              │ Named pipe (JSON lines)
   explorer.exe 前台             第三方 / 未来的 Windhawk mod
```

**注入层只做三件事**（若阶段三真做 mod）：判断是否 Explorer/桌面、取当前路径、把事件转发出来。UI/Provider/排序一律不在 explorer.exe 进程内。

## 2. 上下文判定算法（阶段一实现）

```
OnGlobalHotkey(id):
  hwnd = GetForegroundWindow()
  cls = GetClassName(hwnd)
  proc = GetProcessNameFromHwnd(hwnd)

  if proc != "explorer.exe":
      → SearchContext.Global(origin=GlobalHotkey, owner=hwnd); return

  switch cls:
    "CabinetWClass", "ExploreWClass":       # Explorer 窗口
        tab = FindWindowEx(hwnd, 0, "ShellTabWindowClass", 0)   # 活动 tab 总在 z-order 最前
        path = QueryShellWindows(tab)                            # COM：见下
        origin = ExplorerHotkey
    "WorkerW", "Progman":                   # 桌面
        path = %USERPROFILE%\Desktop ; origin = DesktopHotkey
    "#32770":                               # 文件对话框（可选增强）
        path = 从地址组合框读取 ; origin = ExplorerHotkey
    default:
        → Global

  if path == null: 降级为 Global（并在状态条说明"未能确定当前文件夹"）
  else: ScopeKind.CurrentDirectoryRecursive（Shift+热键 = OnlyCurrentLayer）
```

### COM 取路径链路（照抄 Flow 的做法，含其踩过的坑）

1. `CoCreateInstance(CLSID_ShellWindows {9BA05972-F6A8-11CF-A442-00A0C90A8F39})` → `IShellWindows`
2. 遍历 `Item(i)` → `IWebBrowser2`；用 `IWebBrowser2.HWND` 或 `IShellBrowser.GetWindow()` 与目标 tab 句柄比对
3. **坑**：`GetWindow()` 必须在 **STA 线程**里调用 —— Flow 用 `StartSTAThread(...)` 起一个 STA 线程执行再 join，我们同样处理
4. `IWebBrowser2.LocationURL`（file: URI）→ `NormalizeLocation()`（percent-decode、去 query、`\` 归一）
5. 拿不到路径时（回收站 / 此电脑 / 命名空间 GUID）退回 `IShellFolderViewDual`；仍失败 → 该次降级为 Global
6. 每个 COM 对象都要 `Marshal.ReleaseComObject`，否则 explorer 侧引用泄漏会导致窗口关不掉

规范化统一走 `Sdk.Model.Canonicalization.ToDirectoryKey()`（大小写、尾分隔符、`\\?\` 前缀都在那里处理）。

## 3. 命名管道协议（对外契约，v1）

- 管道名：`\\.\pipe\UniSearch.ShellBridge`；字节流、UTF-8、**每行一个 JSON 对象**（NDJSON）。
- 单实例 Host 持有管道服务端；客户端断开不影响 Host。
- 所有请求都带 `v`（协议版本，当前 `1`）与 `type`。

### 3.1 客户端 → Host

```jsonc
// 1) 上报上下文并请求唤出 UI（未来的 Explorer mod 用这个）
{"v":1,"type":"context","origin":"ExplorerHotkey","path":"D:\\Research\\AI",
 "recursive":true,"ownerHwnd":131422,"selection":["D:\\a.md"],"ts":"2026-09-15T10:00:00Z"}

// 2) 纯查询（命令行工具 / 其它应用唤起搜索）
{"v":1,"type":"query","text":"transformer","scope":"global"}

// 3) 健康探测
{"v":1,"type":"ping"}

// 4) 阶段三：让 Host 指示 mod 隐藏原生搜索框（Host 主动下发时也走同一通道，方向相反）
{"v":1,"type":"command","id":"hide-native-searchbar","enabled":true}
```

### 3.2 Host → 客户端

```jsonc
{"v":1,"type":"ack","requestId":42}
{"v":1,"type":"pong","hostVersion":"0.1.0","providers":["everything","windows-index"]}
{"v":1,"type":"error","code":"unsupported_origin","message":"..."}
```

### 3.3 语义规则

| 情况 | Host 行为 |
|---|---|
| `context` 且窗口已显示 | 只更新范围（RootPath），保留输入文本，立即重跑一次查询 |
| `context` 且窗口隐藏 | 显示窗口、聚焦输入框、`SelectAll`（便于直接改词） |
| `path` 不存在 / 无权限 | 降级 Global，状态条提示；**绝不弹错误框**（REF-4 §3.3 的教训） |
| 连续多个 `context`（用户切标签页） | 去抖 150ms，取最后一个 |
| 客户端是提升权限进程、Host 非提升 | 管道 ACL 拒绝 → 客户端收到 `error`，UI 上提示"需要以管理员运行 UniSearch" |

## 4. 焦点归还

- `context` 携带 `ownerHwnd`；`Esc` 关闭时 `SetForegroundWindow(ownerHwnd)`，失败则回退到上一个前台窗口。
- 打开结果（Enter）后默认隐藏窗口；设置项 `Behavior.CloseOnOpen = true|false`。
- 窗口不得抢走 Explorer 的剪贴板监听/键盘钩子：低级键盘钩子只在阶段三的 mod 内使用，Host 只用 `RegisterHotKey`。

## 5. 阶段三（可选，风险最高）

目标：点 Explorer 右上角原生搜索框也能被接管。技术路径（已在本地读到实现样本）：

| 手段 | 出处 | 代价 |
|---|---|---|
| hook `ExplorerFrame.dll` 的 `CUniversalSearchBand::IsModernSearchBoxEnabled` / `CSearchEditBox::HideSuggestions` | `legacy-search-bar.wh.cpp` | 符号随 Windows 版本漂移，需多版本回退表 |
| `FindChildWindow(hwnd,"TravelBand")` + `RB_IDTOINDEX/RB_SHOWBAND` 隐藏搜索带 | `hide-search-bar.wh.cpp` | 仅"藏起来"，不改行为 |
| 低级键盘钩子吞掉 `Ctrl+F` 并自己唤出 UI | ET 的 `LowLevelKeyboardHook.cs` + `StartMenuSearchInterceptor.cs` | **必须等修饰键释放再 SendInput**（`WaitForModifierKeysReleased`），否则系统认为组合键仍按下 |

验收标准：任何一步失败都必须能一键关闭该功能并回到"阶段二行为"，不能影响资源管理器稳定性。

## 6. 实现清单

- [ ] `src/UniSearch.ShellContext/ForegroundProbe.cs` — 类名/进程名判定 + OwnerHwnd
- [ ] `src/UniSearch.ShellContext/ExplorerFolderResolver.cs` — ShellWindows COM 链路（STA）
- [ ] `src/UniSearch.ShellContext/HotkeyGate.cs` — RegisterHotKey + WM_HOTKEY
- [ ] `src/UniSearch.Pipe/BridgeProtocol.cs` — NDJSON 消息类型（可被 Host 与测试共用）
- [ ] `src/UniSearch.Pipe/BridgeServer.cs` — `NamedPipeServerStream` 多实例
- [ ] `tests/.../BridgeProtocolTests.cs` — 往返序列化 + 降级语义
- [ ] （阶段三）`shell/mod-UniSearchBridge.wh.cpp` — 独立小 mod，仅转发
