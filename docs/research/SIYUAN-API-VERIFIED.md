# 思源笔记（SiYuan）内核 API —— 已核实接口事实（v3.3.5）

> 一手来源：2026-09-28 在 win11_host 对运行中的思源 v3.3.5 实测（curl）；交叉佐证：官方 API 文档
> （github repo `siyuan-note/siyuan`，**v3.3.5 tag** 的 `API_zh_CN.md`——与实例版本精确匹配）。
> **本文优先级高于任何 AI 记忆/二手描述。** 与 `EVERYTHING-IPC-VERIFIED.md`、`ZOTERO-LOCAL-API-VERIFIED.md`
> 同一地位：`UniSearch.Providers.Siyuan` 的协议唯一事实来源。标记：✅ 实测可用；❌ 实测不可用；⚠ 待复核。

---

## 0. 服务形态

| 项 | 事实 | 证据 |
|---|---|---|
| 进程 | SiYuan-Kernel.exe（内核即 HTTP 服务端）；SiYuan.exe 是 Electron 壳 | tasklist + netstat PID 对应 |
| 端口 | 6806（固定，**只归第一个启动的工作区**）+ 53113（当前工作区实际端口） | netstat ✅ |
| 绑定 | **0.0.0.0（所有网卡）**，非 localhost-only | netstat ✅ |
| 版本 | v3.3.5；`POST /api/system/version` 免鉴权返回版本串 | ✅ 实测 |
| 鉴权 | **本机（127.0.0.1）免鉴权**：`/api/query/sql` 不带任何 token 实测通过；**非环回 401**（"使用非 127.0.0.1 访问时请设置访问授权码"，实测 192.168.200.1） | ✅ 实测 |
| API token | 设置→关于→API token，标头 `Authorization: Token xxx`（官方文档原文）；本机场景用不上 | 官方文档 + 实测 |
| MCP | **无**。`POST /mcp` 返回 200 空 text/plain（兜底路由），非 JSON-RPC | ✅ 实测 |
| 多工作区 | 6806 归属第一个启动的工作区，其余工作区随机端口 → **Provider 的 port 必须可配置** | 设计决定 |

## 1. 端点分级（官方文档核对 + 实测）

| 分级 | 端点 | 依据 |
|---|---|---|
| 公开+实测（检索主路径） | `POST /api/query/sql` `{stmt}` | 官方文档收录（示例 `{"stmt":"SELECT * FROM blocks WHERE content LIKE'%content%' LIMIT 7"}`）+ 实测 |
| 公开+实测 | `POST /api/export/exportMdContent {id}` → `{hPath, content}`（带 frontmatter 的 Markdown） | 官方收录 + 实测 |
| 公开+实测 | `POST /api/notebook/lsNotebooks` → `notebooks[]{id,name,...}` | 官方收录 + 实测 |
| 公开+实测（探活） | `POST /api/system/version` → 版本串 | 官方收录 + 实测 |
| 公开（实测未逐个调） | `/api/filetree/getHPathByID`、`getPathByID`、`getIDsByHPath`（id↔路径转换） | 官方收录；⚠ 按需复核 |
| 实测可用但**未承诺** | `POST /api/filetree/listDocsByPath {notebook, path}` → `files[]{id, path(.sy), name, size, mtime,...}` | **官方文档未收录**；实测正常 → 用但要版本锚定 |
| ❌ 不可用（明确不走） | `POST /api/search/fulltextSearchBlock` | **官方文档未收录**（非公开契约）+ 带完整 types 实测仍 200 空体 |
| ❌ 不走（只读原则） | 全部写端点（createDocWithMd/removeDoc/moveDocs…，官方收录 11 个） | UniSearch 只做检索 |

**官方文档核对结论：检索的公开契约只有 SQL。** 这与 ANYTXT 的教训同构：UI 内部端点（fulltextSearchBlock）随时可变且无承诺，公开契约（query/sql）才可依赖。

## 2. blocks 表（SQL 检索的数据面）

实测全库 **2753 块、5 笔记本**。21 字段（实测 `SELECT *`）：
`id, parent_id, root_id, hash, box, path, hpath, name, alias, memo, tag, content, fcontent, markdown, length, type, subtype, ial, sort, created, updated`

映射相关字段：

| 字段 | 含义 | Provider 用途 |
|---|---|---|
| `id` | 块 ID（如 `20251202162847-yc448pt`） | ProviderItemId、`siyuan://blocks/<id>` |
| `box` | 笔记本 ID | 作用域过滤、Subtitle |
| `root_id` | 所属文档块 ID | 单文档作用域、归属 |
| `path` | .sy 相对路径 | 拼 workspace 真实文件路径（可选） |
| `hpath` | 人类可读路径 | Subtitle |
| `content` / `fcontent` | 块文本 / 纯文本 | Snippet、文档块 Title |
| `type` / `subtype` | 块类型：d 文档 / h 标题 / c 代码 / p 段落 / l 列表 / t 表格 / m 公式… | Kind 映射 |
| `markdown` | 块 Markdown 源 | 可选预览 |
| `created` / `updated` | **YYYYMMDDHHMMSS 字符串** | ModifiedAt（需格式转换，非 Unix 秒、非 ISO） |
| `tag` | 块标签 | 标签过滤（⚠ 未实测） |

实测样例行：标题块 `GeoEvoBuilder`；代码块（酶特异性 Python 管线整段）；R ggplot 配色代码块——块级内容整段可取。

## 3. 实时可用性演示（2026-09-28）

- 查 "UniSearch" → 0 块（词不在笔记中，链路正常）；
- 查 "GEO" → 4 块（1 标题 + 3 代码块，真实研究内容）；
- 同日现场：AnyTXT(9920)/Zotero(23119) 未运行 → 连接拒绝；思源内核常驻在线 → 印证 Broker `ProviderOutcome` 的 Down+Hint 设计。

## 4. SiyuanProvider 映射设计

**Descriptor**：`Id="siyuan"`；`Capabilities = ReturnsDocuments | SearchesFileContent | SupportsKindFilter`；`Priority=40`；`LatencyHint=200ms`；`DependsOn = HttpEndpoint POST /api/system/version`（免鉴权探活 + 版本锚定），`Required=true`，Down 时 `Hint="启动思源笔记"`。

**options**（`providers.siyuan.options`）：`port`（默认 6806）、`workspace`（可选 → 拼真实 .sy 路径）、`token`/`authCode`（仅跨机场景；**凭证只存设置，不入库不入文档**）。

**下推映射**：

| SearchQuery | SQL | 备注 |
|---|---|---|
| Terms / Text | `content LIKE '%…%'` | **单引号成对转义（''）防注入——本 Provider 头号实现纪律** |
| Kinds | `type='d'/'c'/…` | d→Document、c→CodeSymbol、其余→Note |
| 日期范围 | `updated BETWEEN 'YYYYMMDDHHMMSS'` | QueryFilters 是 DateTimeOffset，需格式转换 |
| NamedScope | `box='…'`（笔记本）/ `root_id='…'`（单文档） | 建议 `"siyuan:nb/<id>"` |
| Phrase / Regex / Size | 无对应 | 前端过滤 + Outcome.Detail 如实降级 |

**结果映射**：Title←`fcontent`/`content`(文档块)/`hpath`；Subtitle←笔记本名+`hpath`；**Snippet←`content` 截断**（块级真内容，强于 Zotero 的无片段）；Kind 按上表；`Uri = siyuan://blocks/<id>`；ModifiedAt←`updated` 转换；真路径=`workspace/data/<box>/<path>`（.sy JSON 文件，可选）。融合键走 Uri 分支（无本地路径的结果与文件结果天然不冲突）。

**动作**：在思源中打开（`siyuan://blocks/<id>`）、导出 Markdown（`exportMdContent` ✅）、复制块 ID。

**并发**：本机 HTTP 毫秒级，仍 `SemaphoreSlim(1,1)` 串行（与其他 Provider 一致）。

## 5. ✅ 待复核清单（2026-09-29 全部复核完毕）

| # | 问题 | 答案 |
|---|---|---|
| 1 | `fulltextSearchBlock` 空体根因 | 仍返回空体 → **确认不走**（§1 的结论不变） |
| 2 | `siyuan://blocks/<id>` 的 GUI 点击行为 | **本机没注册该协议**（VM 上实测），跨机部署时这个动作不可用 —— 已改为"只在注册了协议时才提供该动作"，主力动作换成**导出 Markdown**（走 API，跨机可用） |
| 3 | 多工作区端口归属与 `workspace` 选项取值方式 | 6806 归第一个工作区；本机只跑一个工作区，`host`/`port` 两个选项即可（真实路径拼法未验，暂无消费者） |
| 4 | `tag` 字段过滤、大库下 LIKE 性能 | 见 §7.5（tag 是 `#标签#` 包裹）；性能：5275 块下 LIKE/instr 都是毫秒级 |
| 5 | 正则下推（内核是否注入 REGEXP） | **可用**，见 §7.3 |

---

## 7. ✅ 实施期复测（2026-09-29，win10 VM → 192.168.200.1:6806，库已长到 5275 块 / 11 笔记本）

文档写完到真正动手之间库翻了一倍（2753 → 5275 块），所以 §2/§5 的每一项都重跑了一遍。
以下都是**新发现**，前文没写的：

### 7.1 ⚠⚠ `code:0` + `data:null` = **SQL 被拒绝**，不是"0 条"

```
SELECT COUNT(*) AS n FROM blocks WHERE 1=0                  -> {"code":0,"data":[{"n":0}]}   ← 0 条
SELECT id FROM blocks WHERE 1=0                             -> {"code":0,"data":[]}          ← 0 条
SELECT COUNT(*) AS n FROM blocks WHERE xyz=1                -> {"code":0,"data":[{"n":0}]}
SELECT COUNT(*) AS n FROM blocks WHERE … LIKE … ESCAPE '\'  -> {"code":0,"data":null}        ← 被拒绝
```

**客户端必须把 `data:null` 当失败**。把它当 0 条，会让"查询写错了"永远显示成"没搜到" ——
而这两件事在界面上必须分得开（本项目反复出现的同一类坑）。

### 7.2 ⚠⚠ 不支持 `ESCAPE` 子句 → 所以**不能用 LIKE**

思源的 SQL 端点拒绝任何带 `ESCAPE` 的语句（试过 `\` 与 `#` 两种转义符，都返回 `data:null`）。
后果：`LIKE` 里的 `%` 与 `_` **没法转义**。

| 查询 | 结果 |
|---|---|
| `content LIKE '%100%%'` | **96**（`%` 当通配符，匹配所有含 "100" 的块） |
| `instr(content, '100%') > 0` | **6**（字面匹配，这才是用户要的） |

**替代方案（已采用）**：`instr(lower(列), lower('值')) > 0`

- `instr` 是**字面**子串匹配，没有通配符概念，天然免疫这个坑；
- 大小写不敏感靠两侧 `lower()` 还原 —— `lower('geo')` 命中 **46** 条，与 `LIKE '%GEO%'` 的 46 完全一致；
- 中文没有大小写，`lower()` 对它是恒等变换（`%基因%` = 204 条，与 `instr` 一致）。

### 7.3 ✅ `REGEXP` 可用（§5 待复核第 5 项的答案）

`SELECT COUNT(*) FROM blocks WHERE content REGEXP 'G[0-9]+'` → **109**。
SQLite 本身没有 `REGEXP`，是思源注册进去的。所以**正则可以下推**（不用退化到前端过滤）。

### 7.4 `updated` 可能是**空串**（413/5275 块）

不是错误，是"这个块没有该字段"。好消息：空串的字典序最小，
`updated >= '20260101000000'` 天然把它排除，不需要额外加 `!= ''`。

### 7.5 `tag` 字段是 `#标签#` 包裹的，且库里极少

```
"tag": "#标签#"                （单个）
"tag": "#注意# #内容块/组合#"   （多个，空格分隔）
tag 非空的块：43 / 5275
```

映射到结果行上的徽标时要**剥掉井号**。层级标签（`#内容块/类型#`）用 `LIKE '%#内容块#%'` 不会误命中
（中间隔着 `/`），所以前缀式过滤是安全的。

### 7.6 块类型的**完整**分布（5275 块，比 §2 列的 7 种多得多）

| type | 数量 | 含义 | | type | 数量 | 含义 |
|---|---|---|---|---|---|---|
| `p` | 2753 | 段落 | | `m` | 31 | 公式 |
| `i` | 1030 | 列表项 | | `s` | 27 | 超级块 |
| `h` | 769 | 标题 | | `tb` | 17 | 分隔线 |
| `l` | 331 | 列表 | | `b` | 9 | 引述 |
| `t` | 98 | 表格 | | `query_embed` | 7 | 嵌入查询 |
| `d` | 97 | 文档 | | `av` | 5 | 属性视图 |
| `c` | 96 | 代码块 | | `html`/`audio`/`iframe`/`video` | 5 | 其它 |

**三档 Kind 映射**（`d`→Document、`c`→CodeSymbol、其余→Note）覆盖了全部 22 种 type。

### 7.7 `blocks_fts` 存在，但**不做检索主路径**

库里确实有 FTS5 虚拟表 `blocks_fts`（列结构与 `blocks` 相同）：
`blocks_fts MATCH 'GEO'` → **32** 条（而 `LIKE`/`instr` 是 46 条，FTS 是按词项匹配）。

**决定不用它**：① 它是内部实现，随时可变，公开契约只有 `query/sql`；
② 子串语义更符合"搜索框"直觉（46 > 32，FTS 会漏掉词内命中）。
记在这里是为了下次有人问"为什么不用全文索引"时有个答案。

### 7.8 值域的**显示名与下推值必须分开**

笔记本给人看的是名字（"R语言"），SQL 里要的是 id（`20251209154600-9kzscxv`）。
第一版把名字当下推值送过去，`box IN ('R语言')` → **永远 0 条**，而界面上完全看不出哪里错了
（chip 显示正常、面板勾选正常、只是结果空）。

修法：`FacetValue` 增加 `Key`（下推值），`Value` 只做显示。**思源是第一个需要这个区分的后端**
（Zotero 的标签恰好两者相同，所以没暴露）。

### 7.9 另两条与思源无关、但由它暴露出来的 Core 缺陷

1. **「正文命中」分类只该对文件生效**：`FusionStore` 原本把"正文命中且非名称命中"的结果一律归到
   `CategoryIds.ContentMatches`。思源的块**没有路径**、内容就是它本身，于是 60 个块全落进「正文命中」，
   而那个分类在思源下被隐藏（它恒等于全部）→ 标签栏只剩一个**计数为 0** 的「全部」，结果表里却有 60 行。
   现在判据是 `Display.Path is { Length: > 0 }`：没有文件，就没有"文件名 vs 正文"这组对立。
2. **「全部」的计数不能只算可见分类**：`BuildTabs` 原本用"可见分组的和"，被隐藏分类的份额直接漏掉。
   现在用未过滤的分组求和。这两个缺陷**同时**命中了 AnyTXT（它的「全部」之前也显示 0）。

---

## 8. SiyuanProvider 实施回填（2026-09-29）

§4 的设计稿落地后有几处改动，都以"实测说了算"为准：

| 设计稿 | 实际实现 | 为什么 |
|---|---|---|
| 检索用 `content LIKE '%…%'` | **`instr(lower(内容), lower('值')) > 0`** | 端点不支持 `ESCAPE`，LIKE 的 `%`/`_` 没法转义（§7.2） |
| `options: port` | `host` + `port` + `token` | 真实部署是跨机的，`host` 与凭证都必需（§6.1） |
| 动作：在思源中打开 | 保留，但**只在注册了协议时提供**；主力改为**导出 Markdown** | VM 上没有 `siyuan://`（§5 第 2 项） |
| `Capabilities` 含 `SupportsKindFilter` | **不含** | 思源的块没有扩展名；声明了会让 `ext:pdf` 白跑一趟（Zotero 的同一条教训） |
| 真路径 = `workspace/data/<box>/<path>` | **不填 Path**（走 `siyuan://blocks/<id>`） | 跨机时磁盘上没有那个 `.sy` 文件，填了只会让"打开/预览"指向不存在的文件 |
| 未提值域 | 实现了 `IFacetProvider`（**笔记本**） | 笔记本是天然的"候选值来自后端"的维度，机制现成（复用 US-16 的值域筛选器） |
| 未提"总数" | 每条查询多跑一次 `COUNT(*)` | `SELECT` 被 `LIMIT` 截断，行数 ≠ 命中总数；状态条上的"共 N 条"必须是真的 N |

**SQL 头号纪律**：`/api/query/sql` 不接受参数绑定（请求体只有 `{stmt}`），值只能拼进 SQL ——
所有进入语句的值**必须**过 `Literal()`（单引号成对）。自检里有一条专门跑
`'; DROP TABLE blocks;--` 确认库没被炸掉。

## 6. 安全须知

- 本机免鉴权 + 0.0.0.0 绑定：**任何本机进程无需凭证即可调全部端点（含写端点）**——个人机可接受；UniSearch 只用读端点；
- 局域网：未设授权码时数据端点拒绝服务（见 6.1），当前安全；设授权码后凭证走 `providers.siyuan.options`，不入库不入文档。

### 6.1 ⚠ 跨机复测（2026-09-29，从 win10 VM 打 192.168.200.1:6806）

本文 §0 的"非环回 401"是在 win11_host 上测的；Provider 实际要跑在 **win10 VM** 上，
而 VM 上没有本地思源，所以跨机这条路必须单独验一遍。四条修正：

| 项 | 复测结果 |
|---|---|
| 探活端点 | `POST /api/system/version` **跨机也免鉴权**，200 返回 `{"code":0,"data":"3.3.5"}` —— 健康探针可以照用 |
| 数据端点 | `POST /api/query/sql`、`/api/notebook/lsNotebooks` 跨机**要凭证**（`Auth failed: … please set [Access authorization code]`） |
| **失败不是 401** | 是 **HTTP 200 + `{"code":-1,"msg":"Auth failed…"}`** —— ⚠ **客户端绝不能只看状态码判成败**，必须解析 `code` |
| 鉴权头格式 | `Authorization: Token <凭证>` 与 `Authorization: Bearer <凭证>` 都会被识别（错误值回 `Auth failed [header: Authorization]`）；`X-Auth-Token`、裸 `Authorization` 不识别（仍回"请设置访问授权码"）。**用 `Token` 前缀** |

**含义**：跨机部署时 Provider 必须有 `token` 选项，且**鉴权失败要如实报给用户**
（"思源拒绝访问：请在宿主机思源里设置访问授权码"），而不是显示成"没有结果" ——
这两者在界面上分不出来，是本文档反复出现的同一类坑。
另外 `host` 也必须是选项（不再是 §4 写的"port 可配置"就够）。
