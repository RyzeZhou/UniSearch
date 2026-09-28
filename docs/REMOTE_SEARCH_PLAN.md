# 远程目录搜索：把易远传的远程目录纳入 UniSearch（方案草案）

> 状态：**设计草案**（2026-09-20）。作者：Win10 侧（易远传 = ERF 的实现方）。
> **前提**：ERF 目前**冻结**（用户明确"不要修改了"）。本方案第一原则是
> **不改 ERF 也能先跑起来**；ERF 侧需要新增的东西**集中列在 §7**，作为"请求清单"，
> 等你解冻时再评估，不在本次实施范围。
>
> 与既有决策对齐：`UniSearch/docs/DEVLOG.md` 2026-09-20 第 8 轮已定
> 「远端搜索：对接点定在 **ERF 常驻服务协议**，待它暴露列举/搜索 RPC 后，
> UniSearch 侧新增一个 Provider 包（只引用 Sdk），与 Everything 同构」。
> 本方案就是这句话的展开。

---

## 1. 目标 / 不目标

**目标**

1. 在 UniSearch 里能搜到**远程站点**（易远传里已配置的 SFTP/FTP/FTPS 站点）里的文件与目录；
2. 结果可操作：在易远传里定位、下载到本地、复制远程路径、看属性；
3. 首选体验与本地搜索**同构**：同一个窗口、同一套筛选/分类/排序/来源栏；
4. 代价可控：**给得出结果就给**，给不出就明说"扫到哪儿了"，绝不把 UI 卡住。

**不目标（本期）**

- 不做服务端索引（SFTP/FTP 没有那个东西，见 §2）；
- 不做远程**文件内容**全文检索（见 §3-C 的选项与代价）；
- 不接管远程浏览（浏览仍由易远传在资源管理器里做）；
- 不把 UniSearch 变成索引器 —— `docs/spec/MVP.md` §G 已明确"我们只做 Broker，不做索引器"。
  **这条正是本方案要绕开的难点**，见 §3-B：让**已经拥有缓存的一方**（ERF）出索引，UniSearch 只查询。

---

## 2. 为什么"远程搜索"与本地搜索不是一回事

| | 本地 | 远程（SFTP/FTP） |
|---|---|---|
| 索引 | Everything/Windows 索引**常驻**，毫秒级 | **没有**服务端索引；协议只有"列目录" |
| 枚举成本 | 内存/系统调用，微秒级 | **每次 `List` = 一次网络往返**（局域网 20~120 ms，公网 100~600 ms） |
| 深度代价 | 万级目录也就几十毫秒 | **目录数线性放大**：500 个目录 ≈ 15~60 s |
| 内容检索 | 有索引/可 mmap | 只能**下载**才有内容 → 不能全量做 |
| 时间戳 | 100 ns | **秒级**（SFTP/FTP 普遍如此），FTP 还带服务器本地时区不归一 |

**结论：两点必须承认**

1. **"全站实时搜索"在协议层就不可能快** —— 只能靠"索引"或"服务端执行"（§3）；
2. 所以远程搜索要么**限定范围**（用户在某个远程目录里搜，或指定站点+根），要么**吃索引**。

**现成的资产（很重要）**：易远传浏览过的目录，**已经把目录快照写进了本地 SQLite**
（`erf-cache.db` 的 `dir_cache(site, path, tick, items BLOB)`，`items` 是 `FTPENTRY[]` 原始字节，
见 `explore-remote-files/docs/KNOWN_ISSUES_2026-09-19-win11-shell.md`）。
也就是说：**用户"走过的路"天然是一份远程文件索引**。把它用起来，远程搜索的体验会立刻从
"几十秒"变成"几毫秒" —— 这是本方案的核心杠杆。

---

## 3. 三种可行形态（对比与推荐）

| | A. 实时遍历 | B. 查询 ERF 的索引 | C. 服务端执行 |
|---|---|---|---|
| 做法 | 用现有 `LIST` 桥接协议 BFS 逐层列目录 | 查 `dir_cache`（或 ERF 新增只读查询 RPC） | SSH 上跑 `find`/`grep` |
| ERF 改动 | **零** | 零（直接读库）或**一个小 RPC** | 新增 exec 能力（较大，安全面要单独议） |
| 速度 | 慢（线性于目录数），必须先给预算 | **毫秒级**（本地 SQLite + 前缀 WHERE） | 快（把计算放到服务器） |
| 覆盖 | 扫到哪儿算哪儿 | 只覆盖**浏览过的**目录 | 全站，且能做**内容**检索 |
| 适用 | 目录内定位、小范围找文件 | 默认体验（"我浏览过的地方"） | 后期"重量级搜索"（需站点支持 SSH） |

**推荐组合：B 为主 + A 兜底 + C 留作后期**，理由：

- B 立刻可用（今天就能读那个 SQLite），且体验最好；它天然回答"我见过的东西在哪"；
- A 保证"没浏览过也能找"，用预算把代价封顶，并顺手把结果**写回**自己的小索引/或提示用户先浏览；
- C 是唯一能让"内容搜索"成立的路线，但需要 ERF 有远程 shell 通道，且要单独过安全与权限，先不做。

> ⚠️ B 的**直接读库**是**格式耦合**（`items` 的二进制布局 = ERF 私有 `FTPENTRY[]`）。
> 短期可以照着格式读（只读、WAL 允许并发读）；长期应改成 §7 的**只读查询 RPC**，
> 把格式关进 ERF 内部。方案里两条都写明，实施时先做前者、后换后者。

---

## 4. 落到 UniSearch：Provider 设计

### 4.1 工程与契约

新增 `src/UniSearch.Providers.Erf`（**只引用 `UniSearch.Sdk`**，与 `Providers.Everything` 同构；
遵守 `docs/spec/ARCHITECTURE.md` §2「Provider 不得引用 Core/Host」）：

```csharp
[UniSearchProvider("erf", "易远传（远程站点）", ApiVersion = 1,
    RequiredApp = "易远传", InstallHint = "https://…")]
public sealed class ErfProvider
    : ISearchProvider,            // 必须
      IFileSystemScopedProvider,  // 目录内搜索（TranslateScope → 远程路径）
      IGlobalScopeScopeProvider?, // ⚠️ 见下
      IDirectoryListProvider,     // 浏览某远程目录（复用 Results 流水线）
      IActionProvider,            // 下载 / 定位 / 复制路径 / 属性
      IExternalUiProvider,        // 「在易远传中打开」
      ISuggestionProvider         // 站点名、站点根路径、历史路径
```

`Descriptor` 要点：

| 字段 | 取值 | 说明 |
|---|---|---|
| `Id` | `"erf"` | 小写、稳定、全局唯一 |
| `Priority` | `60` | 比 Everything(100) 低、比慢速在线源(10) 高 |
| `LatencyHint` | `200ms`（索引路径）/ `数秒`（遍历路径） | ⚠️ 求 Core 用它放宽截止时间，见 §5 |
| `DependsOn` | `{ Name="易远传", ProbeKind=NamedPipe, ProbeValue="ExplorerRemoteFs.Bridge.v1", Required=true }` | SDK 已有 `NamedPipe` 探针类型，正好用 |

能力位（`ProviderCapability`）：
`ReturnsFiles | ReturnsFolders | ReturnsImages | … | SearchesFileName | SupportsKindFilter |
SupportsDirectoryScope | SupportsListing`。
**不声明** `SearchesFileContent`（内容搜索本期不做，见 §3-C）。

> ⚠️ `IGlobalScopeProvider` 的取舍：声明它就意味着"全局搜索也要包含远程" ——
> 那会让每次全局搜索都拖着远程网络。建议：**默认不参与全局**（不实现该接口），
> 只在用户**选中来源栏的"易远传"**或**在远程目录里发起**时才出现；这样成本可控、行为可预期。

### 4.2 搜索流程（实时遍历路径）

```
SearchAsync(query, context, ct)
  ├ 解析作用域：NamedScope（"erf:site:/path"）> 设置里的默认根 > 站点 StartPath
  ├ BFS 队列：从根开始，逐层 List（并发度 1~2，站点会话非线程安全）
  │   ├ 命中 → SearchBatch.Of(...) 立即 yield（不等整棵树）
  │   ├ 预算：目录数上限（默认 300）、深度上限（默认 6）、结果上限（query.ResultBudget）
  │   └ 每层检查 ct
  ├ 每次 yield 都带 TotalAvailable=null（未知）与 IsLast=false；扫完/超预算 → IsLast=true
  └ 结束时把"扫了多少目录、是否被预算截断"放进最后一批的子标题/Metadata，
     让 UI 能如实显示"已扫描 120/300 个目录（未完成）"
```

**索引路径（B，默认）**：直接对 `erf-cache.db` 执行前缀查询（如
`SELECT site, path, tick, items FROM dir_cache WHERE site=? AND path LIKE ?`），
再在内存里把 `items` 解成条目做名字匹配；毫秒级返回、可 `TotalAvailable` 给准数。
两条路径的**结果模型完全一致**，UI 无感。

### 4.3 结果模型映射（**关键，有坑**）

| `SearchResult` 字段 | 远程值 | 说明 |
|---|---|---|
| `ProviderId` | `"erf"` | |
| `ProviderItemId` | `"<site>\|<remotePath>"` | 站点内稳定唯一 |
| `Kind` | 目录 → `Folder`；其余按扩展名 → `Image/Video/Audio/Document/Archive/File` | 复用 `CategoryEngine` 的判定口径 |
| `Subtype` | 小写扩展名 | |
| `Title` | 文件名 | 名字匹配打分的对象 |
| `Subtitle` | `"<站点名> : <父目录>"` | 结果列表第二行 |
| **`Path`** | **留空**（不要填远程路径！） | `Canonicalization.NormalizePath` 会把 `/`→`\`、大写盘符，**远程路径会被改坏**，融合键也会错 |
| **`Uri`** | `"erf://<site>/<path>"`（或 `"erf:<site>:/<path>"`） | 融合键走 `u:` 分支，安全；也是"打开/下载"的稳定标识 |
| `Match` | `NameWord`（名字）/ `Path`（路径命中） | |
| `Snippet` | 无（不做内容） | |
| `SizeBytes` / `ModifiedAt` | 来自条目 | ⚠️ FTP 时间戳可能是服务器本地时区，见 §8 |
| `Metadata` | `site` / `remotePath` / `mode` / `owner` / `group` / `isSymlink` / `cacheAge` | 自由键值，给详情面板与排序用 |
| `Payload` | 远程条目（同进程内私有） | |
| `Icon` | 只给 `IconHint { IconByExtensionOnly = true, Badge = 扩展名大写 }` | ⚠️ **不要**给 `ShellIconPath=远程路径`：`docs/research/REF-1-…` 已警告图标提取会在网络路径上阻塞 |
| `ReadOnly` | `true` | SDK 文档里正是为"只读的远端结果"预留的 |
| `DisablePreview` | `true`（除非已缓存到本地） | SDK 文档原话："远端未缓存" |
| `Tags` | `[站点名]` + 可选 `[索引]`/`[实时]` | 一眼看出结果从哪来、是否新鲜 |
| `CopyText` | 远程路径（`site:/path`） | 「复制路径」直接可用 |

### 4.4 动作 / 预览 / 图标 / 健康

- `IActionProvider.GetActions`：`在易远传中打开`、`下载到本地…`（走 ERF 的 `FETCH`/`OPEN-TICKET`，
  复用它的队列与进度）、`复制远程路径`、`属性`（`erf:` 属性页）；
- `IExternalUiProvider`：`ExternalUiName="易远传"`，`OpenExternalUi(query)` 用命名空间形式
  `::{C816CE0E-…-384597}\<site>:/<path>` 打开（**天然落在当前标签**，见
  `explore-remote-files/docs/CHANGE_HANDOVER_2026-09-20.md` §6）；
- `IPreviewProvider`：**本期不实现**（预览=下载，见 §3-C 的代价）；
- `ProbeHealthAsync`：管道在否 + `connections.json` 是否非空 → `Down(..., Hint="启动易远传 / 先添加站点")`；
- `ISuggestionProvider`：站点名、站点 `StartPath`、历史搜索过的远程目录。

### 4.5 设置项（`settings.json` → `providers.erf.options`）

| 键 | 默认 | 含义 |
|---|---|---|
| `mode` | `index-then-live` | `index`（只搜索引，最快）/ `live`（只实时扫）/ `index-then-live` |
| `defaultRoots` | 空 | 每站点默认搜索根（`site=/path`，可多行） |
| `maxDirsPerQuery` | `300` | 实时遍历的目录预算（成本上限） |
| `maxDepth` | `6` | 深度上限 |
| `concurrency` | `1` | 每站点并发 LIST 数（站点会话非线程安全，别调大） |
| `cacheMaxAgeHours` | `24` | 索引结果新鲜度标记阈值（只影响标签，不影响命中） |
| `deadlineMsOverride` | `8000` | 实时遍历的截止毫秒（见 §5） |

### 4.6 作用域怎么传进来（本方案**依赖的 UniSearch 侧新增**）

`SearchContext.RootPath` 是**本地 Windows 路径**（`IsDirectoryBounded` 也基于它），
而 ERF 的命名空间**没有本地路径**（只有 PIDL）。所以远程作用域必须另开一条信道。
现成且代价最小的是 `SearchContext.NamedScope`（字段已存在、当前无人消费，文档示例是
`"zotero:collection/ABC123"`）—— 约定：

```
NamedScope = "erf:<site>:/<path>"       // 例如 erf:WSL-SFTP:/home/zhou/AI_work
```

于是 Provider 侧 `IFileSystemScopedProvider.TranslateScope(context)` 返回该远程路径即可；
Provider 还要在 `CanScopeTo` 里**同时接受** `ScopeKind.NamedScope` 与目录型作用域。

**宿主侧要补的两件事**（`UniSearch.Host` / 将来的 `UniSearch.ShellContext`）：

1. 在易远传的远程目录里按热键/搜索框时，把当前远程位置填进 `NamedScope`；
   取当前位置可以用 **ERF 现有的桥接协议**（无需改 ERF：查询当前标签的地址/`ParseDisplayName` 结果），
   或者退一步：让用户在 UniSearch 里用 `erf:site:/path` 语法显式指定作用域。
2. 来源栏选择"易远传"时，`ProviderScope=["erf"]`（现成机制），并把 UI 的截止时间放长。

---

## 5. 必须配套的 UniSearch 侧小改动（两处，都是"补洞"）

| # | 现状 | 问题 | 建议 |
|---|---|---|---|
| 1 | 每 Provider 截止时间**只有** `query.Deadline`（默认 1500 ms，`BrokerOptions.DefaultDeadline` 1800 ms） | 远程遍历 1.5 s 就被砍，等于"永远搜不完" | 让 Core 取 `max(query.Deadline, Descriptor.LatencyHint)`，或给 `ProviderDescriptor` 加 `DeadlineHint`；**只影响慢源**，Everything 仍是 1.5 s |
| 2 | `ProbeHealthAsync` **无人调用**（Core/Host 都不调），`SkipReason.HealthUnavailable` 从不产生 | 易远传没运行时，用户只看到"没结果"，不知道原因 | Host 在启动/来源栏刷新时探一次，`Down` 就显示提示与 `Hint` |

（这两条都是既有代码里"声明了但没接线"的能力，改动小、风险低。）

---

## 6. 分阶段落地

| 阶段 | 内容 | 验收 | 依赖 |
|---|---|---|---|
| **P0** | Provider 骨架 + **索引查询**（直接读 `erf-cache.db`，只读）+ 结果映射 + 来源栏 + 健康提示；作用域先用 `providerScope`/`defaultRoots`（不依赖宿主改） | 在 UniSearch 选中"易远传"→ 输入名字 → **毫秒级**列出"浏览过的地方"的匹配；结果能"在易远传中打开/下载" | 无（ERF 零改动） |
| **P1** | 实时遍历（`LIST` 桥接）+ 预算/进度/截断提示 + 两处配套小改动（截止时间、健康） | 没浏览过的远程目录也能搜到；**UI 全程不卡**；超预算时明确显示"已扫 N/M" | 无（ERF 零改动） |
| **P2** | 作用域打通：在易远传远程目录里发起搜索（`NamedScope`），`IDirectoryListProvider` 让浏览结果复用流水线 | 站在远程目录里按热键 → 只搜该子树，结果与本地搜索同窗同构 | Host/ShellContext 改动 |
| **P3** | "深度索引"：后台按需爬取（限速、可取消、只在你选定的站点/根），或 ERF 侧查询 RPC（§7-1） | 全站搜索从"分钟级"降到"秒级" | ERF 解冻后 |
| **P4** | 服务端执行的内容搜索（仅 SSH 可达站点，单独的安全设计） | 能按内容搜远程文本 | ERF 新增 exec 能力 |

---

## 7. ERF 侧需要的改动（**请求清单**，等解冻后评估；本次不动 ERF）

按价值/成本排序：

1. **只读查询 RPC**（替代直接读库）：
   `ERFSEARCH` / site / pathPrefix / namePattern / limit → 结果行（或 `SEARCH-END`）。
   好处：把 `FTPENTRY[]` 的私有布局关进 ERF，UniSearch 不碰二进制格式；
   且能顺带返回"索引新鲜度/覆盖范围"，让 UI 说明"这是浏览过的快照"。
2. **遍历 API**：一次请求拿一个子树的目录条目（深度/条数上限、可取消、分批回），
   避免 UniSearch 自己 BFS 造成 N 次往返与协议细节耦合。
3. **`STAT`（或 `LIST` 带单条目精确属性）**：单条目查询不必列整个目录。
4. **索引可观测性**：`dir_cache` 的**覆盖范围与年龄**（哪些站点/目录有快照、什么时候写的），
   好让 UI 如实标注"索引结果 vs 实时结果"。
5. （后期）**远程 exec**：SSH 站点上执行 `find`/`grep`，为内容搜索与超大站点搜索铺路；
   需要单独的权限/安全设计与白名单。

> 以上任何一条都**不阻塞 P0/P1** —— 它们只影响"更快、更准、更省"，不影响"能不能搜"。

---

## 8. 风险与开放问题

| # | 问题 | 现在怎么办 |
|---|---|---|
| 1 | **FTP 时间戳时区不归一**（SFTP 当 UTC，FTP 用服务器本地时间） | 结果上只做展示、不参与排序权重；`Metadata.cacheAge` 与 `ModifiedAt` 标注来源；需要时加每站点时区偏移设置（ERF 侧） |
| 2 | 索引是**浏览快照**，可能过期/不全 | 结果打 `[索引]` 标签 + 显示快照年龄；`mode=index-then-live` 时对未命中自动补实时扫 |
| 3 | 直接用 SQLite 读 `items` 二进制 = 私有格式耦合 | 只读、加版本校验（`PRAGMA user_version` 或表结构探测）；失败就退化为"仅实时"，绝不崩 |
| 4 | 站点会话**非线程安全**，`LIST` 在服务端还是串行的 | 并发度固定 1；把"慢"如实展示为进度，而不是靠并发硬冲 |
| 5 | 凭据 | **不读凭据**：UniSearch 只通过 ERF（管道/库）取数据，凭据始终在 Windows 凭据管理器里、由 ERF 使用 |
| 6 | 远程结果与本地结果同名同路径？ | 融合键走 `Uri`（`erf://…`），与本地 `Path` 键天然不冲突；`FusionStore` 只会把"同一远程文件的多来源"合并 |
| 7 | 大量结果下的图标/预览开销 | `IconByExtensionOnly`、`DisablePreview=true`，不做任何网络 IO |
