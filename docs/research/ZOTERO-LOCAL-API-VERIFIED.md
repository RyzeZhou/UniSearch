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

## 1. 本地 API（`/api/users/0/...`）—— 读无需鉴权（Zotero 10+ 写要本地 key）

请求路径 user id 固定写 **0**（响应 links 里出现的真实 web id 仅展示用）。

> ⚠ 本文早期版本写的是"GET-only 只读、源码 `supportedMethods=['GET']`" —— 那是 **Zotero 7** 的事实。
> **Zotero 10+ 的本地 API 支持写**（`POST`/`PUT`/`PATCH`/`DELETE`，需 `POST /api/local/authorize`
> 拿本地 key）。见 §4.8。

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

## 3. 搜索能力实测（2026-09-28 本机，用**真实库**逐条试）

**基数**：本机 `/items` 共 **106** 条（44 顶层 + 65 附件 + 子条目）。用已知真实值去试，结论如下。

### 3.1 `q=` 到底搜哪些字段

| 字段 | `q=` 默认模式 | `qmode=everything` | 依据 |
|---|---|---|---|
| 标题 | ✅ | ✅ | `AlphaGenome` → 1；`semiconductor` → 1 |
| 作者（姓/名） | ✅ | ✅ | `Avsec` → 1；`Jung` → 1 |
| **期刊名** | ✅ | ✅ | `Nature Electronics` → 1（命中的是半导体那篇，标题里没有 Electronics） |
| **期刊缩写** | ✅ | ✅ | `Nat Electron` → 1 |
| 年份 / 日期 | ✅ | ✅ | `2026`→11 · `2025`→8 · `2024`→3 · `2023`→3 · `2019`→2（与库里真实年份分布吻合） |
| Zotero key | ✅ | ✅ | `N35RT33I` → 1 |
| **DOI** | ❌ | ⚠ 70（PDF 正文里印着 DOI） | `10.1038` 默认 → **0** |
| **摘要 abstractNote** | ❌ | ⚠ | `genomic` 默认 → **0**；`Deep learning models`（摘要短语）→ **0** |
| **标签** | ❌ | ⚠ 9 | `蛋白设计` 默认 → **0**（`tag=` 才是 3） |
| **集合名** | ❌ | — | `AI发酵` → 0、`抗体成药性` → 0 |
| 附件文件名 | ⚠ | ⚠ | `Parallel enzymatic` → 1（但父条目标题也含此串，无法区分） |

**一句话**：默认 `q=` 覆盖 **标题 / 作者 / 期刊名与缩写 / 年份 / key**；
**DOI、摘要、标签、集合名都搜不到** —— 后三者必须走各自的专用参数。

### 3.2 `qmode` 合法值

| 值 | 结果 |
|---|---|
| 不传 | = `titleCreatorYear` |
| `titleCreatorYear` | ✅ 1 条（`semiconductor`） |
| `everything` | ✅ 4 条 —— 多了 PDF 正文命中（`AlphaGenome` 1 → 5；`genomic` 0 → 21） |
| `fulltext` | ❌ **HTTP 500**（非法值把服务端打崩，不是 400） |

### 3.3 短语与排除（原「待复核」项，已结案）

- **引号 = 真短语，词序敏感** ✅：`"semiconductor chip"` → 1，`"chip semiconductor"`（词序反）→ **0**。
- **`-` 排除** ✅：`semiconductor` → 1，`semiconductor -chip` → **0**。

### 3.4 组合语义：**AND** ✅

| 查询 | 结果 |
|---|---|
| `tag=DNA合成` | 3 |
| `tag=DNA合成&itemType=journalArticle` | 2 |
| `tag=DNA合成&itemType=preprint` | 1 ← **2 + 1 = 3**，确凿的 AND |
| `q=semiconductor&itemType=journalArticle` | 1 |
| `q=semiconductor&itemType=preprint` | 0 |
| `/collections/7GSUHR6G/items/top?q=semiconductor` | 0（该集合 17 条里没有它）→ 集合 + 关键词也是 AND |

### 3.5 `itemType` 排除写法 ✅

`itemType=-attachment` → 41（= 34 期刊论文 + 6 预印本 + 1 笔记），
而 `itemType=attachment` → 65。**负号排除有效。**

### 3.6 `sort` 合法值 ✅

`dateAdded` · `title` · `creator` · `date` · `itemType` · `publisher` · `accessDate` 都接受；
`sort=bogusSort` → **HTTP 400**。

### 3.7 ⚠⚠ 最危险的陷阱：未知**参数名**被静默忽略

| 查询 | 结果 |
|---|---|
| 不带任何过滤 | TR = 106 |
| `bogus=1` | TR = **106**（不报错） |
| `date=2026` | TR = **106**（不报错，**根本没在筛日期**） |
| `dateAfter=2026-01-01` | TR = **106**（同上） |
| `sort=bogusSort` | **HTTP 400** |

**即：未知的参数名被悄悄丢掉，未知的 `sort` 值才报错** —— 这种不对称最坑：
写了个 `date=` 过滤器，返回一堆结果，看起来"有结果"，其实是**完全没过滤**。

**结论：Zotero 本地 API 没有任何日期范围过滤参数。** 想做"最近一年"这类筛选，
只能靠 `q=<年份>` 做"某年"这种**包含式**搜索，或者 `sort=date&direction=desc` 拉回来前端截断。

### 3.8 一条条目的完整字段面（真实样例）

```
key · version · itemType · title · date · DOI · url · accessDate · language
libraryCatalog · volume · pages · publicationTitle · ISSN · issue · journalAbbreviation
creators[] · tags[] · collections[] · relations · dateAdded · dateModified
```

其中**能用来筛的只有** `itemType` / `tag` / `collection` 三项（其余要么只能搜、要么只能排序、要么只能展示）。

---

## 4. 官方文档读后：新增事实与对前文的修正（2026-09-28 夜）

> 来源：官方 Web API v3 文档（basics / local_api / types_and_fields / fulltext_content，
> 用户提供入口，逐页读过）。**本节是文档事实，与前面的"本机实测"互相印证；
> 冲突处以文档为准并标注。** 本机 Zotero 正在升级到 **10.0.3**，下面的 ⏳ 项待服务起来后复测。

### 4.1 目标版本确认：**Zotero 10**（不是 7）

文档多处写 `Zotero 10+`。与本项目相关的三条：

| 特性 | 说明 |
|---|---|
| **`Zotero-Server-ID` 响应头** | 每个本地 API 响应都有，标识这个 Zotero 实例（存在数据库里，**跟着数据走**，重启/升级不变）。读请求可不带；带了就必须匹配，否则 **412 Precondition Failed**；**写请求必带**，不带 **428 Precondition Required**。缓存数据必须按 server ID 分区 |
| **本地对象版本** | Zotero 10+ 的 `version` / `Last-Modified-Version` / `?since=` **都是本地版本**，与 Web API 版本**毫无关系**。老版本报的是同步版本 |
| **写入要本地 API key** | `POST /api/local/authorize` 弹窗向用户申请（`Allow` / `Always Allow` / `Deny`）；**不是 zotero.org 的 key**；不 `remember` 的话**一次性**，用完即失效；弹窗每分钟最多 5 次，超了 429 |

### 4.2 ⚠ 修正一：**本地 API 不默认分页**

文档原文：`The local API does not impose a default or maximum limit. If limit is omitted,
all matching objects are returned in one response.`

- Web API：`limit` 默认 25、上限 100；
- **本地 API：省略 `limit` = 一次返回全部**（本机 106 条）。`limit`/`start` 与 `Link` 头仍可用。

⏳ 待复测：`/items` 不带 `limit` 是否真返回 106 条（升级前最后一次调用正好赶上服务停）。

### 4.3 ⚠ 修正二：`q` 的文档口径与我实测不一致（**已解释**）

文档：`q` = Quick search，**"Searches titles and individual creator fields by default"**。
但我实测 `Nature Electronics`（只存在于 `publicationTitle`）与 `Nat Electron`（`journalAbbreviation`）
**都命中了**。文档自己也给了答案：

> `The local API accepts the same search parameters but uses Zotero's local quicksearch
> implementation, so the set of items returned by a given q value may not match the Web API exactly.`

**即：本地 `q=` 走的是 Zotero 桌面端的本地 quicksearch，比 Web API 文档描述搜得宽。**
→ **以本机实测为准**（§3.1 那张表），但要知道这是"实现细节"而非契约，Zotero 升级后需复测。

### 4.4 ✅ 新增：`itemType` / `tag` 支持**布尔语法**（我上轮漏了）

文档 Search Syntax 节原文示例：

| 写法 | 语义 |
|---|---|
| `itemType=book \|\| journalArticle` | **OR** |
| `itemType=-attachment` | **NOT** |
| `tag=foo bar` | 带空格的标签 |
| `tag=foo&tag=bar` | **AND**（多个同名参数） |
| `tag=foo bar \|\| bar` | **OR** |
| `tag=-foo` | **NOT** |
| `tag=\-foo` | 字面连字符开头的标签 |

⏳ 待复测：以上五种在**本机真实库**上逐条验（升级前只验了单值 `tag=` 与 `itemType=-attachment`）。

### 4.5 ✅ 新增：本地 API **独有的三个端点**

| 端点 | 作用 | 为什么重要 |
|---|---|---|
| **`/searches/<searchKey>/items`** | **真正执行保存的搜索** | Web API 只暴露搜索的元数据、不执行。本机 `/searches` 现在是 `[]`（用户没建保存的搜索），**但这是"用户在 Zotero 里配好筛选条件，UniSearch 直接复用"的天然接口** |
| `/items/<itemKey>/file` | **302 重定向到 `file://`** | 比解析 `links.enclosure` 更正规，直接拿到附件在磁盘上的路径 |
| `/file/view/url` | 同上但返回纯文本 URL | 不跟随重定向也能拿到路径 |

### 4.6 ✅ 新增：`sort` 的**完整**合法值（我上轮只试了 7 个）

`dateAdded` · `dateModified` · `title` · `creator` · `itemType` · `date` · `publisher` ·
`publicationTitle` · `journalAbbreviation` · `language` · `accessDate` · `libraryCatalog` ·
`callNumber` · `rights` · `addedBy` · `numItems`(tags)

**默认 `dateModified`**（不是 dateAdded）。`direction` = `asc`/`desc`。

### 4.7 ✅ 新增：其它可用参数与端点

- **`itemKey=`**：逗号分隔的条目 key 列表，**单次最多 50 个**。
- **`includeTrashed=0/1`**：是否含回收站（`/items/trash` 默认含）。
- **`since=`**：库**版本号**（文档再次确认不是日期）。
- **`format=`**：`json` / `keys`（换行分隔的 key 列表，**无上限**）/ `versions`（无上限）/ `bib`（仅条目，**上限 150**）/ 导出格式；`atom` 在本地 API **501**。
- **缓存**：多对象读返回 `Last-Modified-Version`；带 `If-Modified-Since-Version` 且无变化 → **304**。本地 API 同样支持（但本地本来就快，不急）。
- **Tags 端点族**（比我想的多）：`/tags` · `/items/tags` · `/items/top/tags` · `/items/trash/tags` ·
  `/collections/<key>/tags` · `/collections/<key>/items/tags` · `/collections/<key>/items/top/tags` ·
  `/publications/items/tags`。tags 的 `qmode` = `contains`（默认）/ `startsWith`。
- **tags-within-items 专用参数**：`itemQ` / `itemQMode` / `itemTag` —— 即在"按条目条件取标签"时，
  主参数作用于**标签**，`item*` 参数作用于**条目**。
- **Schema 端点**（写 UI 才需要，但能给出权威清单）：`/itemTypes` · `/itemFields` ·
  `/itemTypeFields?itemType=…` · `/itemTypeCreatorTypes?itemType=…` · `/creatorFields` ·
  `/items/new?itemType=…`。**本地 API 返回用户 locale 的本地化名字**（`locale` 参数被忽略；
  `/creatorFields` 例外，永远英文）。全量 schema 可一次下载：`https://api.zotero.org/schema`。

### 4.8 💡 值得单独记一笔：本地 API **能写**

Zotero 10+ 的本地 API 支持 `POST`/`PUT`/`PATCH`/`DELETE`（条目、集合、保存的搜索），
另有**标签删除**、全文写入、文件上传。改动立即在 Zotero UI 可见，下次同步上传到 zotero.org。

**对 UniSearch 的含义**：将来不只是"搜 Zotero"，还能**改**（比如给条目批量打标签、
把搜索结果存成一个集合）。这超出当前任务范围，但说明这条路是通的 —— 前提是用户授权
（`POST /api/local/authorize` 弹窗）。

---

## 4.9 ✅ Zotero 10.0.3 复测结果（2026-09-28 夜，服务起来后逐条验）

本机已升级到 **Zotero 10.0.3**。§4 里标的 ⏳ 项**全部复测通过**，另有一个新发现。

### 4.9.1 响应头实测（`GET /api/`）

```
HTTP/1.0 200 OK
X-Zotero-Version: 10.0.3
X-Zotero-Connector-API-Version: 3
Zotero-API-Version: 3
Zotero-Schema-Version: 44
Zotero-Server-ID: C5ZPRimItfZv
```

- 版本确凿是 **10.0.3**；schema 版本 44。
- ⚠ **服务说的是 `HTTP/1.0`** —— 不是 1.1。用 HttpClient/WebRequest 时别假设 keep-alive。
- `Total-Results` · `Link`（含 `rel="last"` / `rel="next"` / `rel="alternate"`）· `Last-Modified-Version` 都在。

### 4.9.2 ✅ 布尔语法全部验证通过（§4.4 的五种写法）

| 写法 | 实测 | 验算 |
|---|---|---|
| `itemType=journalArticle` | 34 | — |
| `itemType=preprint` | 6 | — |
| `itemType=journalArticle \|\| preprint` | **40** | 34+6 ✅ OR |
| `itemType=-attachment` | 41 | 44 顶层 - 3 ✅ |
| `itemType=-journalArticle` | **72** | 106−34 ✅ NOT |
| `tag=DNA合成` / `tag=亲和力` | 3 / 6 | — |
| `tag=DNA合成&tag=亲和力` | **0** | 没有条目同时有这两个 ✅ AND |
| `tag=DNA合成 \|\| 亲和力` | **9** | 3+6 ✅ OR |
| `tag=-DNA合成` | **103** | 106−3 ✅ NOT |

### 4.9.3 ✅ 省略 `limit` 确实返回全部

`/items` → **106 条**、`/items/top` → **44 条**，都是一次响应给全。文档准确。

### 4.9.4 ✅ schema 端点返回**中文名**（可直接当 UI 标签用）

- `/itemTypes` → **40 种**，`localized` 是中文：`注释` `艺术品` `附件` `音频` `法案` `博客帖文`
  `图书` `图书章节` `司法案例` `软件` `会议论文` `数据集` …
- `/itemFields` → **246 个**字段，中文名：`标题` `摘要` `日期` `DOI` `引用关键词` `网址` …
- `/itemTypeFields?itemType=journalArticle` → **31 个**字段：
  `title` `abstractNote` `publicationTitle` `publisher` `place` `date` `volume` `issue` `section`
  `partNumber` `partTitle` `pages` `series` `seriesTitle` `seriesText` `journalAbbreviation` `DOI`
  `citationKey` `url` `accessDate` `PMID` `PMCID` `ISSN` `archive` `archiveLocation` `shortTitle`
  `language` `libraryCatalog` `callNumber` `rights` `extra`

### 4.9.5 ✅ `q=` 字段边界：**与升级前完全一致**

Zotero 10 下重跑 §3.1 的探针，结论一字不差：标题/作者/期刊名/期刊缩写/年份/key ✅；
DOI/摘要/标签/集合名 ❌。所以 §4.3 那条"本地 quicksearch 比文档搜得宽"依然成立，
且**不是 Zotero 10 引入的**。

### 4.9.6 ✅ 本地独有端点

- `/file/view/url` → 纯文本 `file:///C:/Users/zhou/Zotero/storage/FGRH7LG3/Jung%20…pdf`（**URL 编码，要解码**）。
- `/items/<key>/file` → **302** + `Location: file:///…`（同上）。
- 附件 JSON 里 `links.enclosure` **依然在**，含 `href` / `type` / `title` / `length` 四项。
- `/searches` → `[]`（用户还没建保存的搜索，所以 `/searches/<key>/items` **暂时无法实测**）。
- `format=keys` → 44 行；`format=versions` → JSON 对象（`{"N35RT33I": 0, …}`）。

### 4.9.7 ⚠⚠ 新发现：本地对象版本**全是 0**

```
44 条顶层条目  version 全部 = 0        （对照：Zotero 7 时代是 113）
Last-Modified-Version: 0
?since=0     -> Total-Results = 106    （since=0 等于"不过滤"，符合文档默认值语义）
dateModified 却有 42 个不同值           → 条目确实在不同时间改过
```

**含义（对 Provider 设计是硬约束）**：

- **`?since=` / `If-Modified-Since-Version` 在这台机器上没法用来做增量** —— 所有对象版本都是 0，
  问"比 0 新的"要么返回全量、要么语义失效。
- 好在**本地 API 本来就快**（106 条一次给全），**Provider 直接每次全量拉取**即可，
  不必费劲做增量缓存。这条要写进 Provider 的实现约定。
- 猜测成因：Zotero 10 的本地版本"每次保存/删除按事务递增"，而这套库是**同步下载来的**
  （不是本地逐条改出来的），所以计数器停在 0。**未证实**，但现象确凿。

---

## 4.10 ✅ 标签值域实测（2026-09-29，为「值域筛选器」而测）

标签是 Zotero 唯一"候选值属于用户数据"的筛选维度（不是类型那种固定枚举），
所以它的读取方式与筛选语义都要实测过才能拿去做 UI。

### 4.10.1 标签库 `GET /users/0/tags`

```
GET /api/users/0/tags?limit=0&format=json   -> 200，17 条（= 库里全部标签）
响应头 Total-Results: 17
每条形状：
{ "tag": "AAV",
  "links": { "self": {...}, "alternate": {...} },
  "meta": { "type": 0, "numItems": 1 } }
```

| 事实 | 值 | 用途 |
|---|---|---|
| `limit=0` = 返回**全部** | 17 条 | 不用翻页 —— 标签是给人勾选的短列表 |
| 默认顺序 | **字母序**（与 Zotero 自己的标签选择器一致） | 不按条目数重排，两处看到的顺序相同 |
| 计数在 `meta.numItems` | 见下 | UI 上显示的数字 |
| **`meta.numItems` 与 `items?tag=` 的 `Total-Results` 逐条比对：17/17 完全一致** | ✅ | **UI 上显示的计数就是筛出来的条数** —— 这一条必须验，否则又是"计数 3、点开 0 条"那种对不上的老坑 |

### 4.10.2 ⚠⚠ `tag=` 的三种口径（全部实测，不是照文档抄）

库内基线 106 条；「蛋白设计」3 条、「小分子」2 条（两者无交集）。

| 写法 | 结果 | 语义 |
|---|---|---|
| `tag=蛋白设计` | 3 | 单值 |
| `tag=蛋白设计&tag=小分子` | **0** | 重复参数名 = **AND**（交集；这俩确实没有共同条目） |
| `tag=蛋白设计\|\|小分子` | **5** | `\|\|` = **OR** |
| `tag=蛋白设计 \|\| 小分子`（带空格） | 5 | 空格不影响，`\|\|` 才是分隔符 |
| `tag=-蛋白设计` | 103 | `-` 前缀 = 排除（106-3） |
| `tag=机器学习&tag=智能体` | 0 | AND（这两个标签也没交集） |
| `tag=zzz不存在` | 0 | **不存在的标签静默返回 0，不报错** |
| **`tag="蛋白设计"`（引号）** | **0** ⚠ | **引号不是"精确短语"语法** |

**引号那条是本轮最值钱的发现**：官方 v3 文档写的是 `tag="exact phrase"`（用于含空格的标签），
**但本地端点实测返回 0 条** —— 引号被当成标签名的一部分去字面匹配（库里没有带引号的标签，
所以必然是 0）。**结论：标签值原样送，一个字都不要加**。这条已写进翻译器的注释与单测
（`Translate_never_wraps_a_tag_in_quotes`），防止将来有人"照文档顺手补个引号"。

**⚠ 与 §4.4 的关系**：§4.4 记的是"官方文档说 `tag` 支持布尔语法"，本轮把**具体写法**补齐了
（`||` / 重复参数 / `-`），并推翻其中引号那半条。文档说支持布尔 ≠ 文档里每个例子都对。

### 4.10.3 落到实现

| 设计点 | 决定 | 理由 |
|---|---|---|
| 值域 id | `tag`（**与查询参数同名**） | 少一层"域 id → 参数名"的映射，就少一处两边名字能对不上的地方 |
| 默认口径 | **任一命中（OR）** | AND 时两个冷门标签的交集常常是 0 条（实测两次都是 0），用户会以为筛选坏了 |
| 多值口径开关 | UI 上给一个「任一/全部」，默认任一 | 两种口径在 URL 上是不同写法（`\|\|` vs 重复参数），下推层都支持，暴露出来成本极低 |
| 含 `\|\|` 的标签名 | **丢弃并记降级说明** | 送过去只会被拆成两个不存在的标签 —— 给一个"看起来筛了其实筛了别的"的结果比不给更糟 |

---

## 5. ZoteroProvider 映射设计（依据以上事实）

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

## 6. ⚠ 待复核清单（2026-09-28 更新）

| # | 项 | 状态 |
|---|---|---|
| 1 | 总数/分页总量语义 | ✅ **结案**：响应头 `Total-Results` 存在且正确（§3.7 全用它测的） |
| 2 | `q=` 的引号短语与 `-` 排除 | ✅ **结案**：引号是词序敏感的真短语；`-` 排除有效（§3.3） |
| 3 | `zotero://select` URI 精确格式 | ⬜ 仍未验（"在 Zotero 中打开"要用） |
| 4 | `links.enclosure` 在各条目类型下的稳定性 | ⚠ 半结案：3 个 PDF 附件实测都有，且**本机路径是 `C:\Users\zhou\Zotero\storage\…`，与 win11_host 的 `G:\数据库\Zotero\storage\…` 不同** → 绝不能写死 |
| 5 | `format=bibtex` 返回空的根因 | ⬜ 未验（不影响 Provider） |
| 6 | ResultKind ↔ Zotero itemType 映射表 | ⚠ 半结案：本机实测类型只有 4 种（journalArticle 34 / preprint 6 / note 1 / attachment 65）；映射表可先覆盖这 4 种 + 常见兜底 |

**本轮新增的未验项**：`qmode=everything` 为什么会把标签也带进来（`蛋白设计` 默认 0、everything 9）
—— 是"everything 包含标签与注释"还是"PDF 正文里真有这些中文词"，未区分。
**这不影响 Provider 设计**（标签一律走 `tag=`），但值得记一笔。

## 7. 环境事实

已装扩展仅 `zoteropdftranslate@euclpts.com.xpi`（PDF 翻译），无 Better BibTeX。
本地 API 无鉴权：本机任何进程在 Zotero 运行期间可读文库（个人机可接受，须知悉）。
