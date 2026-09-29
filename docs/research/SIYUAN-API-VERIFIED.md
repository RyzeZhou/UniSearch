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

## 5. ⚠ 待复核清单（实施前先做）

1. `fulltextSearchBlock` 空体根因——若官方后续修复，可用其分词+高亮替代 LIKE；
2. `siyuan://blocks/<id>` 的 GUI 点击行为（未实测）；
3. 多工作区端口归属与 `workspace` 选项取值方式；
4. `tag` 字段过滤实测；大库下 LIKE 性能（当前 2753 块无压力）；
5. 正则下推（SQLite 无原生 REGEXP，内核是否注入函数待查）。

## 6. 安全须知

- 本机免鉴权 + 0.0.0.0 绑定：**任何本机进程无需凭证即可调全部端点（含写端点）**——个人机可接受；UniSearch 只用读端点；
- 局域网：未设授权码时 API 一律 401（实测），当前安全；设授权码后凭证走 `providers.siyuan.options`，不入库不入文档。
