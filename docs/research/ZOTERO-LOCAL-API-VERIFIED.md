# Zotero 本地 API —— 已核实接口事实（Zotero 7）

> 一手来源：2026-09-27/28 在 win11_host 对运行中的 Zotero 7 实测（curl）；交叉佐证：安装目录
> `app/omni.ja` 解包（`chrome/content/zotero/xpcom/server/server.js`、`server_localAPI.js`、
> `defaults/preferences/zotero.js`）。
> **本文优先级高于任何 AI 记忆/二手描述。** 与 `EVERYTHING-IPC-VERIFIED.md` 同一地位：
> `UniSearch.Providers.Zotero` 的协议唯一事实来源。标记约定：✅ 实测可用；❌ 实测不可用；⚠ 待复核。

---

## 0. 服务形态

| 项 | 事实 | 证据 |
|---|---|---|
| 地址 | `http://127.0.0.1:23119`，仅绑 127.0.0.1 | netstat LISTENING + curl |
| 进程 | Zotero 进程内（zotero.exe）；**Zotero 不运行则服务不存在** | tasklist PID 对应 |
| 端口 | 偏好项 `extensions.zotero.httpServer.port`（默认 23119，源码注释 `// ascii "ZO"`）；配置编辑器可改，但连接器生态写死 23119，**不建议改** | omni.ja `defaults/preferences/zotero.js` |
| 本地 API 开关 | `extensions.zotero.httpServer.localAPI.enabled`（默认 false；本机为 true） | 同上 + profile prefs.js |
| 健康检查 | `GET /connector/ping` → HTML `Zotero is running` | ✅ 实测 |
| Provider 端口配置 | 用户若改过端口，Provider 须支持 `providers.zotero.options.port` 覆盖（C4 设置口子），探针随之 | 设计决定 |

## 1. 本地 API（`/api/users/0/...`）—— GET-only 只读

请求路径 user id 固定写 **0**（响应 links 里出现的真实 web id 仅展示用）。
`server_localAPI.js` 各端点 `supportedMethods=['GET']`（源码确认）。

### 1.1 ✅ 实测可用（HTTP 200 + 真实数据）

| 能力 | 参数 / 路径 | 备注 |
|---|---|---|
| 关键词搜索 | `q=` | 命中标题/作者等元数据字段，含附件标题 |
| **全文检索** | `q=` + `qmode=everything` | 实测命中 PDF 正文（标题为 "PDF" 的附件被 `q=semiconductor` 命中，parent=N35RT33I）——走 Zotero 全文索引 |
| 标签过滤 | `tag=` | |
| 条目类型 | `itemType=journalArticle`、`itemType=-attachment`（排除写法有效） | |
| 集合 | `/collections`、`/collections/<KEY>/items?q=...` | |
| 排序 / 分页 | `sort=`(dateAdded/title/creator/date…) + `direction=asc/desc`；`start`/`limit` | |
| 顶层条目 | `/items/top` | |
| 单条 | `/items/<KEY>` | |
| 机器格式 | `format=json`(默认) / `csljson`（实测返回 1621B）/ `keys` / `versions` | |

### 1.2 ❌ 实测不可用 / 陷阱

| 项 | 实测 | 结论 |
|---|---|---|
| `format=bibtex` / `format=ris` | **HTTP 200 但 body 为空**（len 0–2） | 源码有 exportFormats 映射（bibtex/biblatex/csljson/csv/mods/refer/rdf_*/tei/wikipedia → 翻译器 GUID），但翻译器导出实际返回空。要 BibTeX：拿 `csljson` 自转，或装 Better BibTeX（本机未装，无 `/better-bibtex/*` 端点） |
| `/fulltext` | 400 `Invalid 'since' value 'null'` | 同步用端点，**不是**搜索入口；全文搜索走 `qmode=everything` |
| 写操作 | — | GET-only，本地 API 只读 |
| 跨机器 | — | 仅 127.0.0.1，不出本机 |

## 2. 响应形状

与 zotero.org Web API v3 同构的 JSON 对象：`data{key, version, itemType, title, date, DOI, url, ...}`、
`links.self / alternate / up`；附件条目带 `parentItem`，且实测样例中附件带
`links.enclosure → file:///` 本地路径（样例指向 G:\数据库\Zotero\storage\...，即 Zotero 数据目录真实位置）。

## 3. ZoteroProvider 映射设计（依据以上事实）

**Descriptor**：`Id="zotero"`；`Capabilities = ReturnsDocuments | SearchesFileContent（附件全文，经 qmode=everything）| SupportsKindFilter（itemType）`；**不声明** SupportsDirectoryScope（无目录概念）；`Priority=30`（慢后端，先占位再补齐）；`LatencyHint=800ms`；`DependsOn = HttpEndpoint http://127.0.0.1:23119/connector/ping`，`Required=true`，Down 时 `Hint="启动 Zotero，并在设置→高级开启『允许其他应用程序…/本地 API』"`。

**下推映射**：

| SearchQuery | Zotero API | 备注 |
|---|---|---|
| Terms / Text | `q=` | |
| Phrase | `q="..."` | ⚠ 引号短语支持度待复核 |
| Tags | `tag=` | |
| Kinds（BibliographicItem/Note/…） | `itemType=` | 需建 ResultKind ↔ Zotero itemType 映射表 |
| sort=dateAdded / title / creator | `sort=` + `direction=` | |
| Extensions / Size / ModifiedAfter | 无对应 | 前端过滤 + Outcome.Detail 如实降级（`since=` 是版本号语义，不是日期） |
| FoldersOnly | 不适用 | 该标签在 zotero 来源下隐藏（[US-13] 模板 `Providers` 字段） |

**作用域**：`SearchContext.NamedScope = "zotero:collection/<KEY>"`（SDK 文档示例原样就是它）；无 NamedScope = 全库。

**结果映射**：顶层条目 `Path` 留空、`Uri = zotero://select/...`（⚠ 精确格式待复核）、`ReadOnly=true`、无 Snippet（API 不返回命中高亮——与 AnyTXT 的关键差异）；附件行映射 `links.enclosure` 的本地路径 → **真 Path**（shell 右键/预览原生可用）；融合键走 `Uri` 分支（与 REMOTE_SEARCH_PLAN §4.3 的 erf 做法同款，天然与本地 Path 结果不冲突）。动作：`IExternalUiProvider "在 Zotero 中打开"`（select URI）；BibTeX 复制为可选增强（csljson 自转）。

## 4. ⚠ 待复核清单（实施前先做）

1. 总数/分页总量语义：响应头 `Total-Results` 是否存在；
2. `q=` 的引号短语与 `-` 排除语法支持度；
3. `zotero://select` URI 精确格式；
4. `links.enclosure` 在各条目类型下的稳定性（样例来自单次实测）；
5. `format=bibtex` 返回空的根因（翻译器未实现还是参数缺失）；
6. ResultKind ↔ Zotero itemType 全量映射表。

## 5. 环境事实

已装扩展仅 `zoteropdftranslate@euclpts.com.xpi`（PDF 翻译），无 Better BibTeX。
本地 API 无鉴权：本机任何进程在 Zotero 运行期间可读文库（个人机可接受，须知悉）。
