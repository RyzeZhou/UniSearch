# AnyTXT Searcher —— 已核实接口事实

> 一手来源：**2026-09-28 在本机（win10_vm）对运行中的 AnyTXT 实测**（curl 直打 9920 / MCP 握手 9924 /
> 从 `ATGUI.exe` 提取方法名表）。交叉佐证：`rpc.dll` 为 Qt 的 QJsonRpc 库（MIT）。
> **本文优先级高于任何 AI 记忆/二手描述**，与 `EVERYTHING-IPC-VERIFIED.md`、`ZOTERO-LOCAL-API-VERIFIED.md`
> 同一地位：`UniSearch.Providers.Anytxt` 的协议唯一事实来源。
> 标记约定：✅ 实测通过；❌ 实测不支持；⚠ 未验。

---

## 0. 服务形态（实测）

| 端口 | 绑定 | 是什么 | 备注 |
|---|---|---|---|
| **9920** | `127.0.0.1` | **JSON-RPC**（QJsonRpc over HTTP） | **Provider 走这个** —— 无会话、零协议依赖 |
| 9921 | `0.0.0.0` ⚠ | **Web UI**（Wt 框架） | ⚠ **全网可达**；我们不用它 |
| 9924 | `127.0.0.1` | **MCP**（Streamable HTTP） | 给智能体工具箱直连，与本 Provider 无耦合 |

- 宿主进程：`ATGUI.exe`（三个端口同一个 PID）；索引服务 `ATService.exe`（Windows 服务
  `Anytxt Searcher Indexing Service`）。**ATGUI 不在则三个端口都不存在。**
- 安装目录：`D:\Program\Anytxt Searcher\`（`rpc.dll` = QJsonRpc，`wt.dll`/`wthttp.dll` = Web UI）。
- 实测环境：索引**已加载**，全盘 3000+ 条，仍在建。

---

## 1. JSON-RPC（9920）—— 这是 Provider 要用的

### 1.1 ⚠ 两个必须知道的形式（**踩了才知道**）

1. **方法名带服务名前缀**：`ATRpcServer.Searcher.V1.<方法>`。
   只写 `Search` 会得到 `-32601 service '' not found`；写 `anytxt.Search` 会得到
   `service 'anytxt' not found` —— 错误信息里的"service"就是方法名的**点号前缀**。
   完整方法名从 `ATGUI.exe` 的字符串表提取（见 §1.2）。
2. **参数是「单元素数组包一个对象」**：`"params":[ { ... } ]`。
   直接给对象 `"params":{...}` 或数组里放位置参数都会得到 `-32602 invalid parameters`。
   无参方法（`IsSearchEngineStarted`）用 `"params":[]` 或不写 `params` 都行。

```jsonc
// 可用
{"jsonrpc":"2.0","id":1,"method":"ATRpcServer.Searcher.V1.GetResult",
 "params":[{"pattern":"semiconductor","filterDir":"C:","limit":3}]}
```

### 1.2 ✅ 方法表（从 ATGUI.exe 提取，全部实测）

| 方法 | 作用 | 参数 |
|---|---|---|
| `ATRpcServer.Searcher.V1.IsSearchEngineStarted` | 引擎/索引是否就绪 | 无 → `{"return":true}` |
| `ATRpcServer.Searcher.V1.Search` | **只返回总数** | 同 GetResult 的入参 → `{"count":N}` |
| `ATRpcServer.Searcher.V1.GetResult` | 返回本页行 | `pattern` `filterDir` `filterExt` `lastModifyBegin` `lastModifyEnd` `limit` `offset` `order` |
| `ATRpcServer.Searcher.V1.GetFragment` | 单段命中片段（带高亮） | `fid` `pattern` |
| `ATRpcServer.Searcher.V1.GetFragmentAll` | 多段片段 | `fid` `pattern` `topK` |
| `ATRpcServer.Searcher.V1.GetRawTextByFID` | 取整篇索引文本 | `fid` |
| `ATRpcServer.Searcher.V1.SyncIndex` | 同步索引（**写操作**，不接） | `folder` |
| `ATRpcServer.Searcher.V1.OCR` | 图片 OCR | `file` |

另有一套小写别名（`anytxt.v1.search` / `getResult` / `getFragment` / `getFragmentAll` / `getText` /
`ocr` / `status` / `syncIndex`），是 MCP 侧用的；**Provider 用上面那套大写正式名**。

### 1.3 ✅ 入参语义（全部实测）

| 参数 | 默认 | 实测结论 |
|---|---|---|
| `pattern` | — | 查询表达式。支持 `!排除词`（实测 7 → 4 条）、`"短语"`（引号是**定界符**不是字面量：`"semiconductor"` 与不加引号同为 7 条） |
| `filterDir` | **`C:`** | ⚠ **传空串会被服务端强制成 `C:`**（回显证实）。**全盘搜索必须显式枚举盘符**。给 `D:` 实测生效（7 → 1 条） |
| `filterExt` | `*` | ✅ **两种写法都行**：`*.md;*.txt`（官方 MCP schema 推荐）与 `md;txt` 结果一致。`*.xyz` → 0 条（确实在过滤） |
| `lastModifyBegin` / `End` | `0` / `2147483647` | Unix 秒；实测回显确认默认值 |
| `limit` / `offset` | 不传=不限制 | ✅ 分页正确（offset 0/2/4 首行各不相同） |
| `order` | `0` | ⚠ 1/2/3/4 都不报错，但**回显里不含 order**，本轮未能区分排序语义 |

### 1.4 ✅ 出参形状

```jsonc
// GetResult
{"result":{"data":{
  "input":{ ...回显规范化后的入参（含被强制成 C: 的 filterDir）... },
  "output":{
    "count": 2,                                    // ⚠ 本页行数，不是总数
    "field": ["fid","lastModify","size","file"],
    "files": [["15774878399612516299","1757005192","276213","C:\\...\\english_wikipedia.txt"], ...]
  }},"errno":0}}
```

- **行 = 位置数组**，按 `field` 表解读。**新字段出现时不能靠下标硬编码** —— 按 `field` 查名。
- `fid` 是**数字串**（如 `15774878399612516299`，超出 double 精度）→ 必须当**字符串**传，别过 JSON number。
- **总数**要单独调 `Search`（它的 `count` 才是总数）。

```jsonc
// GetFragment —— 高亮标记是 *<<* ... *>>*
{"output":{"text":"... intercollegiate *<<*semiconductor*>>* grassland ..."}}
```

### 1.5 ⚠ 陷阱汇总

| 陷阱 | 实测 |
|---|---|
| `filterDir` 传空 → 强制 `C:` | ✅ 回显证实 |
| `count` 是**本页行数**不是总数 | ✅ limit=2 → 2；总数另调 Search |
| **`errno` 不能当失败信号** | ✅ 实测 `filterDir:"D:"` 有结果（count=1）却 `errno:1`；空结果也可能 `errno:0` |
| `pattern` **不是正则** | ❌ `sem[ic]onductor` → 0、`s.miconductor` → 0。`\|` 是它自己的 **OR**（`semiconductor\|silicon` → 44 条），不是正则交替 |
| 索引连系统/回收站目录都收 | ✅ 结果里出现 `C:\Windows.old\...`、`C:\$Recycle.Bin\...` → 结果侧必须过滤隐藏/系统 |
| 失败判定 | 看 JSON-RPC **error 信封**（无效方法 -32601、参数错 -32602），不要看 errno |

---

## 2. MCP（9924）—— 与本 Provider 无耦合，但值得知道

标准 Streamable HTTP：`initialize` → `notifications/initialized`（**少这一步会得到
`-32600 Session not initialized`**）→ `tools/call`。响应是 `data: {json}` 单行。
服务自述：`{"name":"Anytxt","version":"1.0"}`，协议 `2025-03-26`。

**7 个工具**（比任务帖记的 5 个多 2 个 —— AnyTXT 已更新）：

`anytxt_status` · `anytxt_search` · `anytxt_get_fragment` · `anytxt_get_fragment_all` ·
`anytxt_get_text` · **`anytxt_ocr`** · **`anytxt_sync_index`**

⚠ MCP 的 `tools/call` 返回**双层编码**：`content[0].text` 里是**一个 JSON 字符串**，要再解析一次。

---

## 3. AnyTXTProvider 映射设计（依据以上事实）

**Descriptor**：`Id="anytxt"`；`Capabilities = ReturnsFiles | ReturnsDocuments | SearchesFileContent
| SupportsDirectoryScope | SupportsKindFilter（经扩展名）`；`Priority=80`；`LatencyHint=500ms`；
`DependsOn = HttpEndpoint http://127.0.0.1:9920`，`Required=true`，
Down 时 `Hint="启动 AnyTXT Searcher / 等待索引构建"`。

**下推映射**：

| SearchQuery | AnyTXT | 备注 |
|---|---|---|
| Terms / Phrase | `pattern` | 短语用 `"…"`，排除用 `!` —— **比 Everything 表达力强** |
| Filters.Extensions | `filterExt` | `*.a;*.b` |
| RootPath / NamedScope | `filterDir` | **显式盘符**；多盘要拆成多次查询 |
| ModifiedAfter / Before | `lastModifyBegin/End` | Unix 秒 —— 日期下推的第一个真实消费者 |
| Regex / MinSize / MaxSize | ❌ | 前端过滤 + `Outcome.Detail` 如实降级 |
| FoldersOnly | ❌ | 该标签在 anytxt 来源下隐藏（US-13 模板的 `Providers` 字段） |

**结果映射**：`file` → `Path`（真实本地路径，shell 右键/预览原生可用）；`fid` →
`ProviderItemId` + `Metadata["fid"]`（**同时存 path 兜底**，因 fid 跨重索引稳定性未验）；
`size` → `Size`；`lastModify` → `LastWriteTime`（Unix 秒 → UTC）；`Kind` 按扩展名走 `CategoryEngine`。

**Snippet 二段式**：首批 `GetResult(limit=ResultBudget)` 先出行保证快；首屏 Top N 异步补
`GetFragment` 作为第二批 emit（Broker 快照流天然支持）。解析 `*<<*…*>>*` 成高亮区间。

**并发**：`SemaphoreSlim(1,1)` 串行（同 EverythingProvider 模式）。

**过滤**：结果侧接 `BrokerOptions.FilterHiddenAndSystemByDefault`（索引含回收站/系统目录）。

---

## 4. 仍未验的

1. `order` 各取值的排序语义；
2. `fid` 跨重索引是否稳定；
3. 多盘符怎么查（目前只能一次一个 `filterDir`）—— 要不要并发多次查询后归并，待定。
