# MVP（M1+M2）验收清单

可执行的验收条件。任何一条不满足就不算 MVP 完成；每条都注明"怎么验"。

## A. 安装与启动

| # | 条件 | 验证方法 |
|---|---|---|
| A1 | 自包含单文件可运行，不需要用户装 .NET 运行时 | `dotnet publish -c Release -r win-x64 --self-contained` 后拷到干净目录双击可开 |
| A2 | 单实例：第二次启动只是唤出已有窗口 | 连开两次 exe，托盘只有一个图标、进程数不变 |
| A3 | 开机自启可选且默认关 | 设置项切换后注册表 Run 键随之增删 |
| A4 | Everything 未安装时能正常启动并明确提示 | 本机就没有 Everything → 启动不崩、状态条显示"Everything 未在运行 + 下载指引"，仍能搜应用/内置内容 |
| A5 | 无管理员权限也能用（只有"以管理员运行"动作触发 UAC） | 标准用户账户跑通全部流程 |

## B. 搜索行为

| # | 条件 | 验证方法 |
|---|---|---|
| B1 | 输入即搜，首屏 < 300ms（Everything 在跑时） | 计时从击键停止到第一行结果出现 |
| B2 | 快速连续输入不产生错乱结果（旧请求被丢弃） | 逐字打 `transformer`，最终列表只含匹配 `transformer` 的项，无中间态残留 |
| B3 | 分类归属正确：全部/文件/文件夹/文档/图片/视频/音乐/应用/压缩包/代码/正文命中 | 造一个含各类型的测试目录，逐项核对归属（**呈现方式已改**：不再分组，见 B4） |
| B4 | 列表最多 `search.maxRows` 行（默认 500），状态条写"显示 N / 共 M 条" | 搜 `e` 这种超大结果集。（**2026-09-18 修订**：原为"每类默认最多 24 行 + 可下钻"，改成单表平铺后每组上限失去意义，见 DEVLOG 第 5 轮） |
| B5 | 隐藏/系统文件默认不出现；勾选后可见 | 在含 `.` 开头与隐藏属性的目录里搜 |
| B6 | 高级语法原样透传（`content:`、`(a|b)`、`size:>10mb`、`C:\Users`） | 输入这些串，结果与直接在 Everything 里输入一致（范围除外） |
| B7 | 后端故障可见且不阻塞其它后端 | 关掉 AnyTXT 服务 → 该分区显示降级说明，文件分区照常出结果 |
| B8 | 超时后端被标记 Timeout，不影响整体完成 | 人为把某 Provider deadline 压到 1ms |

## C. 上下文（M2 核心）

| # | 条件 | 验证方法 |
|---|---|---|
| C1 | 在 Explorer 窗口按 `Ctrl+F` → 唤出并限定当前目录（含子目录） | 打开 `D:\Research\AI` 按 Ctrl+F，状态条显示范围，结果全在该目录下 |
| C2 | Win11 **多标签页**下取的是**活动标签页**的路径 | 三个标签页之间切换，每次按 Ctrl+F 范围随之变化 |
| C3 | 切标签页后再次唤出，范围更新但保留已输入的词 | 输入 `pdf` → 换标签页 → Ctrl+F → 词还在、范围变了 |
| C4 | 桌面上按 Ctrl+F → 范围 = 桌面目录 | 结果只含 `%USERPROFILE%\Desktop` 内对象 |
| C5 | 全局快捷键（`Win+Alt+Space`）→ 范围 = 全局，且允许出现应用/文献等非文件实体 | 与非 Explorer 前台窗口下按键对比 |
| C6 | 取不到路径时**静默降级为全局**并在状态条说明，绝不弹错误框 | 在"此电脑""回收站"里按 Ctrl+F |
| C7 | Shift+热键 = 仅当前一层（不含子目录） | 与 C1 结果数量差异明显，深层文件不出现 |
| C8 | 关闭后焦点归还给原来的 Explorer 窗口 | Esc 后键盘输入回到资源管理器 |
| C9 | 不具备目录限定能力的后端在 C1 场景下**根本不被调用** | 诊断面板里 Zotero 显示 `Skipped: 不支持限定目录` |

## D. UI / 交互（对齐 docs/spec/UI-SPEC.md）

| # | 条件 | 验证方法 |
|---|---|---|
| D1 | 结果行高 20px、图标 16px，一屏 ≥ 18 行（默认窗口尺寸，传统桌面密度）；中间列字号 **9pt = 12 DIP**（与资源管理器列表同档） | 目测 + 度量断言（`Tokens.xaml` 常量）+ 175% 缩放下像素级实测（DEVLOG 第 9/10 轮） |
| D2 | Win11 观感：Mica 背板、小圆角、跟随深浅色与强调色 | 切换系统主题/强调色即时生效 |
| D3 | 纯键盘可完成：上下、Enter、Ctrl+Enter、Shift+Enter、Alt+Enter、Ctrl+1..9、Ctrl+C、Esc | 拔掉鼠标走通全流程 |
| D4 | 选中态清晰可见（边框+底色），且与悬停态可区分 | 深浅色两种主题下检查 |
| D5 | 右键菜单含宿主默认动作（打开/所在目录/复制路径/属性/以管理员/终端打开） | 对文件、文件夹、应用分别验证 |
| D6 | 结果可拖出到 Explorer（文件拖放列表）；**不得抢走列头拖拽热区 / 滚动条的鼠标**（拖出只认"按在结果行上"） | 拖一个结果到别的文件夹窗口；`--selftest-layout` 的落点过滤断言（DEVLOG 第 10 轮） |
| D7 | 窄窗口逐级降级不出现横向滚动条 | 拖窄到 480px |
| D8 | 中文路径/查询正常（编码、排序、省略号截断） | 搜"会议纪要"、路径含中文与空格 |
| D9 | Tab 补全（typeahead）可用 | 输入 `trans` 按 Tab 补成候选 |
| D10 | 结果表可横向滚动：`Shift` + 滚轮（内容比视口宽时） | `--selftest-layout` 的 `ProbeHorizontalScroll`；UI-SPEC 快捷键表 |

## E. 融合与来源

| # | 条件 | 验证方法 |
|---|---|---|
| E1 | 同一文件的名称命中与正文命中合并成一行，带"正文 N 处"chips | M3 完成后验证 |
| E2 | 大小写不同的同一路径必须合并 | 单测 `Case_and_slash_differences_produce_same_fusion_key` ✅ 已通过 |
| E3 | 多后端证实的行，标签列出每一路来源（含决定标题那一路） | 单测 `Same_path_across_three_providers_becomes_one_row` ✅ |

## F. 工程质量

| # | 条件 | 验证方法 |
|---|---|---|
| F1 | `dotnet build UniSearch.sln` 0 error；warning 数不增长 | CI/本地 |
| F2 | 纯逻辑测试不依赖任何外部后端即可运行 | 本机没装 Everything 也全绿 ✅（当前 76/76） |
| F3 | Provider 契约层零 UI/零 Core 依赖 | 检查 csproj 引用方向 |
| F4 | 所有第三方代码/包有许可证记录 | `LICENSES-THIRD-PARTY.md`（MIT/Apache-2.0 声明齐全；**无许可证的上游一行不抄**） |
| F5 | 崩溃日志可定位 | `%LOCALAPPDATA%\UniSearch\logs\`，未处理异常落盘 |

## G. 明确不在 MVP 范围内

- 接管/隐藏 Explorer 原生搜索框（M5，风险最高，独立开关）
- 任务栏 deskband 内嵌搜索框（ET 有，但我们先不做 COM band）
- 进程外插件宿主与插件市场（先保证 SDK 形状正确，再谈隔离）
- 索引自建（我们只做 Broker，不做索引器）
- **Windows 索引（SystemIndex / OLE DB）Provider** —— 明确不做：Everything 已覆盖文件名与路径检索，
  且索引质量、速度、语法能力都更好。多接一个索引源只会带来"为什么这条没出来"的解释负担，
  而正文检索交给 AnyTXT 这类专用后端更合适。原调研结论（REF-2 §2.2、REF-5 §2）已作废，见 `REUSE-CANDIDATES.md`
- 预览引擎不自研（不解析 Office、不做 PDF 排版）：
  - 图片 → WPF 原生解码；文本/代码 → 直读前 48KB（带 NUL 二进制检测）
  - **PDF → 系统自带 `Windows.Data.Pdf` 渲染首页**
  - 其余 → shell 缩略图（`IShellItemImageFactory`，注册了缩略图处理程序的类型自然有预览，没有的如实显示"没有可用的预览"）
  - 要更强的预览再考虑托管 `IPreviewHandler` 或接 QuickLook/Seer

> **目标框架约束**：宿主是 `net8.0-windows10.0.19041.0`（不是 `net8.0-windows`）——
> `Windows.Data.Pdf` 需要 Windows SDK 投影。同时 `<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>`
> 保证输出路径不随 TFM 漂移（否则脚本/文档仍指向旧目录，会一直跑陈旧 exe）。
