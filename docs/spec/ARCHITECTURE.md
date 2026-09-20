# 架构总览与开发规格（v0.1）

项目定位：**可扩展的 Windows Search Broker** —— Everything 提供文件系统底座，Provider SDK 提供扩展能力，
Broker 依据当前上下文决定该调用哪些后端，统一前端负责实体融合、分类、排序与操作。

原始需求与判断依据见 `docs/research/`（6 份仓库中文总结 + 上游审计 + 已核实的 Everything 协议）。

## 1. 分层

```
                    ┌──────────────────────────────┐
   Ctrl+F / 全局键 →│  UniSearch.Host (WPF, 单实例) │
                    └───────────────┬──────────────┘
        ┌───────────────┬───────────┼────────────┬──────────────┐
        ▼               ▼           ▼            ▼              ▼
  ShellContext     HotkeyGate   SearchBroker   IconService   SettingsStore
  (当前目录)       (Register-   (调度/融合/    (shell icon   (JSON)
                   HotKey)      分类/排序)     按扩展名)
                                   │
                     ┌─────────────┼──────────────┬───────────────┐
                     ▼             ▼              ▼               ▼
                Everything     WindowsIndex     AnyTXT          Zotero      …社区插件
                (文件名索引)   (SystemIndex     (正文检索,      (文献实体,
                               正文+名称)        闭源外部)        本地 HTTP)
```

依赖方向单向：`App/Host → Core → Sdk`；`Providers → Sdk`（**禁止** Provider 引用 Core/App，保证可跨进程演进）。

## 2. 核心概念（三条不可动摇的规则）

### 2.1 上下文驱动调度，而不是"广播给所有插件"

```
Query + SearchContext → ProviderCapability 匹配 → 只调度合适的后端 → 并行搜索
```
Explorer 里搜 `D:\Research\AI` 时，Zotero/书签/设置这类**不具备目录限定能力**的后端被直接跳过，
所以结果天然只有 文件/文件夹/图片/文档/正文命中；全局快捷键下才允许出现 应用/文献/笔记/网络。
实现：`Core/Selection/ProviderSelector.cs`（有测试覆盖"Zotero 在目录上下文中被跳过"）。

### 2.2 Provider 不决定分类

Provider 只声明语义字段：`Kind`（File/Folder/BibliographicItem/Note/…）、`Subtype`、`Match`（名称/正文/元数据）。
分类由 `Core/Categories/CategoryEngine.cs` 用一张集中表（Kind 优先 → 扩展名 → 兜底）决定，可用配置覆盖。
→ 加插件不需要改 UI，也不会出现"每个后端自己抢一个榜单"。

### 2.3 同一路径 = 同一实体

`Everything` 的 `D:\Papers\paper.pdf`、`AnyTXT` 的正文命中、`Zotero` 的附件路径合并成**一行**，
标题取权威更高的一方（文献 > 文件名），但保留文件系统一方提供的路径/大小/时间，标签列出所有证实方。
实现：`Core/Fusion/{FusedResult,FusionStore}.cs` + `Sdk.Model.Canonicalization.ToFusionKey`。
第一版只做"同路径即同实体"；DOI/ISBN/Zotero key 等二级标识留待后续（键空间已预留 `d:`/`i:`）。

## 3. 流水线

```
输入(120ms debounce) → RequestId++ → 取消上一个请求
  → ProviderSelector.Select(providers, context, query)
  → 每个入选 Provider 并行 SearchAsync（各自 deadline，超时降级为 Timeout 而非报错）
  → 归一化 SearchResult → Core 后过滤（范围/隐藏系统/类型/词项；结构化查询跳过后者）
  → FusionStore.Add（流式，先到先显示，后到原地升级）
  → CategoryEngine 定组 → Ranker 打分（名称质量·usage·邻近度·新鲜度·后端可信度）
  → SearchSnapshot{Groups, Outcomes}（节流 45ms）→ UI 整表替换
```

关键工程细节（都有注释解释"为什么"）：
- **单一活动请求**：新输入立刻让旧查询收票（Everything 每 reply 窗口也只允许一个在飞查询）。
- **快照节流**：Everything 一次可回数万条，UI 不需要数万次重绘。
- **失败可见**：每个后端的调度结局（Running/Done/Failed/Timeout/Skipped+人话原因）都在状态条与诊断面板里。
- **usage 双键**：`5×同查询次数 + 1×全局次数`（借自 Flow Launcher），避免"搜 chrome 时点过一个 pdf"污染所有排序。

## 4. Provider SDK

必备接口只有一个，其余全是可选能力接口（新增能力永远不需要改老插件）：

| 接口 | 含义 | 谁实现 |
|---|---|---|
| `ISearchProvider` | 描述符 + `SearchAsync(query, context, ct)` 流式返回 `SearchBatch` | 全部 |
| `IGlobalScopeProvider` | 可参与全局搜索 | Everything/AnyTXT/Zotero/Apps |
| `IFileSystemScopedProvider` | 能限定目录；`TranslateScope(ctx)` 给出原生范围表达式 | Everything(`ancestor:`)、AnyTXT(`filterDir`)、WindowsIndex(`scope='file:'`) |
| `IContentSearchProvider` | 搜正文 + `GetIndexStateAsync()`（索引进度） | AnyTXT、WindowsIndex |
| `IPreviewProvider` | 预览内容 | Everything(缩略图)、Zotero(摘要) |
| `IActionProvider` | 领域专属动作（通用动作由宿主统一注入） | Zotero(导出 BibTeX)、Obsidian |
| `ISuggestionProvider` | 输入补全 | Apps、历史 |
| `IDirectoryListProvider` | 列举目录内容（空查询时） | Everything、文件系统 |

约束（写进代码注释与测试）：**结果对象里不得有委托、WPF 类型、不可序列化对象** —— 这是将来支持进程外 Provider 的前提
（Flow/CmdPal 都在这上面吃过亏，见 REF-2 §3）。

## 5. 内置后端策略

| 后端 | 依赖 | 状态 | 说明 |
|---|---|---|---|
| **Everything** | 用户自装 Everything（免费闭源） | ✅ 已实现 | 传输用 Apache-2.0 纯托管 `EverythingSearchClient`（已支持 QUERY2：Size/日期/属性/排序），**不碰原生 DLL**。边界与升级路线见 REF-6 |
| **WindowsIndex** | 无（系统自带） | ⬜ 下一步 | SystemIndex OLE DB + `ISearchQueryHelper.GenerateSQLFromUserQuery`；**AnyTXT 之外的免费正文方案**，微软自己在 CmdPal 里这么干（REF-5 §2） |
| **Apps** | 无 | ⬜ | 开始菜单 `.lnk` + 注册表 App Paths；产出 `ResultKind.Application` |
| **AnyTXT** | 用户自装（闭源） | ⬜ | 官方示例插件性质：验证"正文检索型"契约。RPC 细节尚未核实（见 `docs/research/EVERYTHING-IPC-VERIFIED.md` 末节） |
| **Zotero** | 用户自装（本地 HTTP API） | ⬜ | 官方示例插件性质：验证"非文件实体"契约，是最能把 SDK 设计逼出问题的后端 |

> 主仓库**只硬依赖 Everything**；AnyTXT / Zotero 作为第一批示例插件存在 —— 用两个性质完全不同的后端来检验 API 是否设计对了。

## 6. 仓库结构

```
UniSearch/
├── docs/
│   ├── spec/        ARCHITECTURE(本文件) UI-SPEC SHELLBRIDGE QUERY-SYNTAX PROVIDERS MVP ROADMAP
│   └── research/    6 份仓库中文总结 + UPSTREAM-AUDIT + EVERYTHING-IPC-VERIFIED
├── src/
│   ├── UniSearch.Sdk/                  契约层（稳定 API）
│   ├── UniSearch.Core/                 Broker/选择器/融合/分类/排序/usage
│   ├── UniSearch.Providers.Everything/ 第一方后端
│   ├── UniSearch.Providers.WindowsIndex/  ⬜
│   ├── UniSearch.Providers.Apps/          ⬜
│   ├── UniSearch.ShellContext/            ⬜ 前台判定 + 当前目录
│   ├── UniSearch.Pipe/                    ⬜ Bridge 协议
│   └── UniSearch.Host/                    ⬜ WPF 前端 + 托盘 + 热键
├── tests/UniSearch.Core.Tests/         76 个用例（纯逻辑，不需要本机装任何后端）
└── refs/            ← 参考克隆（不在仓库内，位于 D:/tools/refs，已 gitignore 之外）
```

## 7. 配置

`%LOCALAPPDATA%\UniSearch\settings.json`：

```jsonc
{
  "hotkeys": { "global": "Win+Alt+Space", "explorer": "Ctrl+F" },
  "ui": { "rowMode": "single", "previewWidth": 320, "theme": "system", "closeOnOpen": true },
  "broker": { "perCategoryLimit": 24, "deadlineMs": 1800, "filterHidden": true },
  "categories": { "extensionOverrides": { "md": "notes" }, "hidden": ["web"] },
  "providers": {
    "everything": { "enabled": true, "instance": null, "extraArgs": "!ext:tmp;log" },
    "anytxt": { "enabled": false },
    "zotero": { "enabled": false, "baseUrl": "http://localhost:23119/api/" }
  }
}
```

## 8. 里程碑

| 阶段 | 交付 | 验收 |
|---|---|---|
| **M0**（已完成） | Sdk + Core + Everything Provider + 76 测试 | `dotnet build` 0 错、`dotnet test` 全绿 |
| **M1** | Host WPF 紧凑 UI + Everything + Apps + 托盘 + 全局热键 | 全局唤出→分类分组→Enter 打开→右键动作→状态条显示后端健康 |
| **M2** | ShellContext（非注入）+ Ctrl+F 目录限定 + 管道协议 | 在任意 Explorer 标签页按 Ctrl+F，结果只含该目录及其子目录；Zotero 类后端不出现在结果里 |
| **M3** | 正文后端：WindowsIndex（免费）+ AnyTXT（可选） | 同一 PDF 与 Everything 命中融合成一行，带"正文 N 处"chips |
| **M4** | Zotero Provider（非文件实体） | 文献/笔记分区出现且可打开 `zotero://`；附件与磁盘文件融合 |
| **M5**（可选） | Windhawk mod 接管原生搜索框 | 一键开关；关闭后行为回到 M2 |

详细验收条件见 `MVP.md`。
