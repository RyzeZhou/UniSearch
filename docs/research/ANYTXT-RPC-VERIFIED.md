# AnyTXT Searcher —— 已核实接口事实（Anytxt API v1）

> 一手来源：**官方 API 文档**（用户提供）+ **2026-09-28 在本机（win10_vm）对运行中的 AnyTXT 实测**
> （curl 直打 `9924/rpc` 与 `9924/mcp`）。交叉佐证：从 `ATGUI.exe` 提取的方法名表、`rpc.dll`（Qt QJsonRpc）。
> **本文优先级高于任何 AI 记忆/二手描述**，与 `EVERYTHING-IPC-VERIFIED.md`、`ZOTERO-LOCAL-API-VERIFIED.md`
> 同一地位：`UniSearch.Providers.Anytxt` 的协议唯一事实来源。
> 标记约定：✅ 实测通过；❌ 实测不支持；⚠ 未验。

---

## 0. 服务形态（实测 + 文档）

| 端口 | 绑定 | 是什么 | 我们用它吗 |
|---|---|---|---|
| **9924 `/rpc`** | `127.0.0.1` | **公开 JSON-RPC 2.0 API，方法命名空间 `anytxt.v1`** | ✅ **Provider 走这个** |
| 9924 `/mcp` | `127.0.0.1` | MCP Streamable HTTP | ❌（留给智能体工具箱） |
| 9924 `/` | `127.0.0.1` | 帮助页（浏览器可直接打开） | 参考 |
| 9921 | `0.0.0.0` ⚠ | Web UI（Wt 框架） | ❌ **绑在全网，注意** |
| 9920 | `127.0.0.1` | **内部 QJsonRpc 接口**（`ATRpcServer.Searcher.V1.*`） | ❌ **不是给我们用的** |

- 宿主进程：`ATGUI.exe`（所有端口同一个 PID）；索引服务 `ATService.exe`（Windows 服务
  `Anytxt Searcher Indexing Service`）。**ATGUI 不在则服务全不存在。**
- ⚠ **许可要求**：`ATGUI must be running and the SDK feature must be enabled by the current license.`
  —— SDK 功能受**许可证**控制，不是装了就一定有。
- 安装目录：`D:\Program\Anytxt Searcher\`（`rpc.dll` = QJsonRpc，`wt.dll`/`wthttp.dll` = Web UI）。
- **URL 里没有 `/v1` 段**：版本由 JSON-RPC 方法名承载（`anytxt.v1.*`）。

### ⚠ 9920 与 9924 是两套接口（踩过才知道）

上一轮我先摸到了 9920，它**也能用**，但那是内部接口，形式很别扭：

| | 9920（内部，**别用**） | **9924 `/rpc`（官方，用它）** |
|---|---|---|
| 方法名 | `ATRpcServer.Searcher.V1.GetResult` | `anytxt.v1.getResult` |
| params | **单元素数组包一个对象** `[ {…} ]` | **直接给对象** `{…}` |
| 文档 | 无 | 有（含帮助页） |
| 版本化 | 无 | 方法名里带 `v1` |

在 9920 上调 `anytxt.v1.status` → `-32601 service 'anytxt.v1' not found`（两套不互通）。

---

## 1. JSON-RPC（`http://127.0.0.1:9924/rpc`）

### 1.1 请求 / 响应形状 ✅

```jsonc
// 请求：params 直接给对象
{"jsonrpc":"2.0","id":1,"method":"anytxt.v1.status","params":{}}

// 成功响应：业务结果统一是 { errno, data: { input, output } }
{"jsonrpc":"2.0","id":1,"result":{"data":{"input":{},"output":{"return":true}},"errno":0}}
```

- `result.data.input` = **服务端规范化后的入参回显**（诊断利器：一眼看出哪些参数被改了默认值）。
- 协议/参数错误走**顶层 `error`**，业务状态走 `errno`。

### 1.2 ✅ 方法表（8 个）

| 方法 | 作用 | 参数 |
|---|---|---|
| `anytxt.v1.status` | 引擎/索引是否就绪 | 无 → `output.return` (bool) |
| `anytxt.v1.search` | **只返回总数** | 搜索参数（见 1.3，无 limit/offset/order） |
| `anytxt.v1.getResult` | 返回本页行 | 搜索参数 + `limit` `offset` `order` |
| `anytxt.v1.getFragment` | **一段**命中片段 | `fid` `pattern` |
| `anytxt.v1.getFragmentAll` | **多段**命中片段 | `fid` `pattern` `limit`/`topK` |
| `anytxt.v1.getText` | 取整篇索引文本 | `fid` |
| `anytxt.v1.syncIndex` | 同步索引（**写操作，不接**） | `folder` |
| `anytxt.v1.ocr` | 图片 OCR（**需 OCR 版构建**） | `file` |

### 1.3 ✅ 参数语义（全部实测）

| 参数 | 默认 | 实测结论 |
|---|---|---|
| `pattern` | 必填 | 查询表达式。支持 `!排除`（7 → 4 条）、`"短语"`（引号是**定界符**：`"semiconductor"` 与不加引号同为 7 条） |
| `filterDir` | **`C:`**（Windows） | ⚠ **传空串被强制成 `C:`**（回显证实）。**全盘搜索必须显式枚举盘符**。给 `D:` 实测生效（7 → 1 条） |
| `filterExt` | `*` | ✅ **两种写法都行**：`doc;pdf`（文档）与 `*.md;*.txt` 结果一致。`*.xyz` → 0 条 |
| `lastModifyBegin` | `0` | Unix 秒 |
| `lastModifyEnd` | `0` = **不设上界** | ✅ 实测 `0` 与 `2147483647` 同为 7 条 |
| `limit` | `300` | ✅ **范围 1–300**：`0`/`301`/`1000` 都被拒（`-32602 'limit' is outside its valid integer range`） |
| `offset` | `0` | ✅ 0 基，分页正确（0/2/4 首行各不同） |
| `order` | `0` | ✅ **0 默认 / 1 修改时间升 / 2 修改时间降 / 3 路径升 / 4 路径降** —— 四条都用时间戳与完整路径逐条验过 |

### 1.4 ✅ 出参形状

```jsonc
// getResult
{"result":{"data":{
  "input":{ ...规范化后的入参回显... },
  "output":{
    "count": 3,                                  // ⚠ 本页行数，不是总数
    "field": ["fid","lastModify","size","file"],
    "files": [["15774878399612516299","1757005192","276213","C:\\...\\a.txt"], ...]
  }},"errno":0}}
```

- **行 = 位置数组**，按 `output.field` 查名解读 —— **别按下标硬编码**（字段可能增删）。
- `fid` 是**无符号 64 位数字串**（如 `15774878399612516299`，超出 double 精度）
  → **必须当字符串传**（文档明说）。
- **总数**要单独调 `anytxt.v1.search`。

```jsonc
// getFragment —— text 是字符串，高亮标记 *<<* ... *>>*
{"output":{"text":"... intercollegiate *<<*semiconductor*>>* grassland ..."}}

// getFragmentAll —— text 是数组，count = 段数
{"output":{"text":["...段1...","...段2..."],"count":2}}
// ⚠ 实测（2026-09-30，Snippet 二段式踩到）：getFragmentAll 的段落是**裸文本、不带**高亮标记，
//   且**不认 limit 参数**（传入 2，input 回显强制成 8、返回 8 段）。要标记就得用单段的
//   getFragment —— UniSearch 的片段二段式因此选它。

// getText —— 上限 1 MiB，被截断时带 truncated + originalBytes
{"output":{"text":"...","truncated":true,"originalBytes":1234567}}
```

### 1.5 ⚠ 陷阱与限制汇总

| 项 | 实测 / 文档 |
|---|---|
| `filterDir` 传空 → 强制 `C:` | ✅ 回显证实 |
| `count` 是**本页行数**不是总数 | ✅ limit=2 → 2；总数另调 search |
| **`errno` 不能当失败信号** | ✅ 实测 `filterDir:"D:"` 有结果（count=1）却 `errno:1`；空结果也可能 `errno:0` |
| `pattern` **不是正则** | ❌ `sem[ic]onductor` → 0、`s.miconductor` → 0。`\|` 是它自己的 **OR**（`semiconductor\|silicon` → 44 条），不是正则交替 |
| `getFragment` vs `getFragmentAll` 的 `text` **类型不同** | ✅ 前者 `String`，后者 `Object[]` |
| `getFragmentAll` 的参数名 | ✅ `limit` 与 `topK` **都接受**（都返回 2 段） |
| 索引连系统/回收站目录都收 | ✅ 结果里出现 `C:\Windows.old\...` → 结果侧必须过滤隐藏/系统 |
| 请求体上限 **1 MiB** | 文档 |
| **非 loopback 的 Host/Origin → HTTP 403** | 文档（安全设计） |
| 批量请求支持；空批量非法 | 文档 |
| 通知（无 id）→ HTTP 204 无响应体 | 文档 |
| 错误码 | `-32700` 无效 JSON / `-32600` 无效请求 / `-32601` 方法不存在 / `-32602` 参数缺失或非法 / `-32603` 内部错误 |

---

## 2. MCP（`9924/mcp`）—— 与本 Provider 无耦合

标准 Streamable HTTP：`initialize` → `notifications/initialized`（**少这一步得
`-32600 Session not initialized`**）→ `tools/call`。响应是 `data: {json}` 单行。
服务自述 `{"name":"Anytxt","version":"1.0"}`，协议 `2025-03-26`。

**7 个工具**：`anytxt_status` · `anytxt_search` · `anytxt_get_fragment` · `anytxt_get_fragment_all` ·
`anytxt_get_text` · `anytxt_ocr` · `anytxt_sync_index`

限制：MCP 分页上限 **100**（JSON-RPC 是 300）；`get_text` 上限 1 MiB；
**最多 16 个并发会话**；空闲 **300 秒**过期；缺 session 头 → 400，未知/过期 → 404，会话满 → 503。

⚠ `tools/call` 返回**双层编码**：`content[0].text` 里是**一个 JSON 字符串**，要再解析一次。

---

## 3. AnyTXTProvider 映射设计（依据以上事实）

**Descriptor**：`Id="anytxt"`；`Capabilities = ReturnsFiles | ReturnsDocuments | SearchesFileContent
| SupportsDirectoryScope | SupportsKindFilter（经扩展名）`；`Priority=80`；`LatencyHint=500ms`；
`DependsOn = HttpEndpoint http://127.0.0.1:9924/rpc`，`Required=true`，
Down 时 `Hint="启动 AnyTXT Searcher（并确认许可证含 SDK 功能）/ 等待索引构建"`。

**下推映射**：

| SearchQuery | AnyTXT | 备注 |
|---|---|---|
| Terms / Phrase | `pattern` | 短语用 `"…"`、排除用 `!` —— **比 Everything 表达力强** |
| Filters.Extensions | `filterExt` | `doc;pdf` 形式 |
| RootPath / NamedScope | `filterDir` | **显式盘符**；多盘要拆成多次查询后归并 |
| ModifiedAfter / Before | `lastModifyBegin/End` | Unix 秒；上界用 `0` 表示不限 —— 日期下推的第一个真实消费者 |
| 排序 | `order` | 1/2/3/4 = 改升/改降/路径升/路径降 |
| Regex / MinSize / MaxSize | ❌ | 前端过滤 + `Outcome.Detail` 如实降级 |
| FoldersOnly | ❌ | 该标签在 anytxt 来源下隐藏（US-13 模板的 `Providers` 字段） |

**结果映射**：`file` → `Path`（真实本地路径，shell 右键/预览原生可用）；`fid` →
`ProviderItemId` + `Metadata["fid"]`（**同时存 path 兜底**，因 fid 跨重索引稳定性未验）；
`size` → `Size`；`lastModify` → `LastWriteTime`（Unix 秒 → UTC）；`Kind` 按扩展名走 `CategoryEngine`。

**分页**：`limit` 上限 300 → `ResultBudget` 超过 300 时按页拉，直到够或后端空。

**Snippet 二段式**：首批 `getResult(limit=ResultBudget)` 先出行保证快；首屏 Top N 异步补
`getFragment` 作为第二批 emit（Broker 快照流天然支持）。解析 `*<<*…*>>*` 成高亮区间。

**并发**：`SemaphoreSlim(1,1)` 串行（同 EverythingProvider 模式）。

**过滤**：结果侧接 `BrokerOptions.FilterHiddenAndSystemByDefault`（索引含回收站/系统目录）。

**不接**：`syncIndex`（写操作）、`ocr`（需 OCR 版构建，且语义是"从图片抽文字"不是搜索）。

---

## 4. 仍未验的

1. `fid` 跨重索引是否稳定（决定 Metadata 兜底的必要性）；
2. 多盘符怎么查（目前一次一个 `filterDir`）—— 要不要并发多次查询后归并；
3. 许可证边界：哪一档 license 才带 SDK 功能（文档只说"必须启用"）。
