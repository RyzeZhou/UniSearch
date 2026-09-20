# Everything IPC —— 已核实协议事实

> 一手来源：`https://www.voidtools.com/Everything-SDK.zip` → `ipc/everything_ipc.h`（903 行，2021-03-23）；
> 交叉验证：`voidtools/ES` 的 `src/everything_ipc.h`（911 行）、本地 clone 的
> `refs/everythingtoolbar/EverythingSDK/include/Everything_IPC.h`（define 集合与官方逐字一致）、
> Rust crate `everything-ipc` 0.1.4、voidtools 官方文档与论坛。
> **本文优先级高于任何 AI 记忆/二手描述。**

## 0. 需要先纠正的错误认知（重要）

社区文章与模型记忆里流传的一批常量是**不存在**的，写代码时不得使用：

| 被引用的名字 | 结论 | 真实对应 |
|---|---|---|
| `EN_INITIATE` / `EN_QUERY` / `EN_SEARCH` / `EN_SORT` / `EN_GETONLYINFO` / `EN_GETFULLPATHNAME` / `EN_GETEXTRATED` / `EN_NOCASES` / `EN_USEWILDCARDS` / `EN_WHOLEWORD` / `EN_MATCHPATH` / `EN_REGEXP` / `EN_DISABLEFILTERS` / `EN_ONLYINDEXED` / `EN_ONLYOFFLINE` / `EN_ALLOWESCAPE` / `EN_GETFOCUS` / `EN_GETFOLDERPATH` / `EN_STATUSQUERY` / `EN_GETVERSION` / `EN_GETINSTANCE` | **全部不存在**。Everything 的 IPC 头文件里没有任何 `EN_` 前缀标识符（GitHub 代码搜索命中的只有 OpenJ9 的误报） | 见下表 B 的真实名字 |
| `EVERYTHING_IPC_QUERY2_REQUEST_W` | 不存在该 typedef | 真名 `EVERYTHING_IPC_QUERY2`；ANSI/Unicode 由 `dwData` = 17 / 18 区分 |
| 字段 `hwndTarget` / `match_offset` / `match_length` / `maxitems` / `length` | 官方与所有移植版里都不存在 | 见下表 D 的真实字段名 |
| `EVERYTHING_IPC_FOLDER` / `EVERYTHING_IPC_CHILDREN` 作为结构体 | 不存在 | `EVERYTHING_IPC_FOLDER` 是 **item flag** `0x1` |
| 回复标志 `COPYDATAQUERYID` / `NOFILES` / `QUERYOPENED` | 不存在 | 回复的 `dwData` 就是请求里 `reply_copydata_message` 的回显；“无结果”= `totitems == 0` |
| WOW64/32 位专用窗口类名 | 不存在 | 用请求 `GET_TARGET_MACHINE`(5) 探测；`reply_hwnd` 存成 DWORD（x64 也只需 32 位） |
| 搜索函数 `folderparent:` | 文档与论坛均无 | 用 `parent:` / `ancestor:` / `child:` / `location:` |
| 搜索函数 `pic:` / `video:` / `audio:` / `exe:` | **未证实为函数** | 用 `kind:picture` / `kind:video` / `kind:music` / `kind:document`，或内置筛选器常量（表 B 末行） |
| `path:` 作为函数 | 它是**修饰符**（Match Path） | 函数是 `full-path:`；匹配所在目录用 `location:` |

## 表 A — 窗口类名

| 宏 | 值 | 用途 |
|---|---|---|
| `EVERYTHING_IPC_WNDCLASSA/W` | `EVERYTHING_TASKBAR_NOTIFICATION` | Everything 的 IPC 服务端窗口，运行期间始终存在；WM_COPYDATA 与 WM_USER 请求的目标 |
| `EVERYTHING_IPC_SEARCH_CLIENT_WNDCLASSA/W` | `EVERYTHING` | 每个搜索窗口一个；用于 500–523 的 UI 状态请求与 WM_COMMAND 菜单 ID |
| `EVERYTHING_IPC_CREATEDA/W` | `EVERYTHING_IPC_CREATED` | **不是类名**，是 Everything 启动时向所有顶层窗口广播的 `RegisterWindowMessage` 名 |
| 1.5 / 命名实例 | `EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)` | 实例名后缀。用 `EnumWindows` + `GetClassNameW` 前缀匹配解析出实例名；或走 1.5 命名管道 SDK `Everything3_ConnectW(instance)` |

## 表 B — 真实标志位

`search_flags`（QUERY / QUERY2）：

| 名字 | 值 |
|---|---|
| `EVERYTHING_IPC_MATCHCASE` | 0x00000001 |
| `EVERYTHING_IPC_MATCHWHOLEWORD` | 0x00000002 |
| `EVERYTHING_IPC_MATCHPATH` | 0x00000004 |
| `EVERYTHING_IPC_REGEX` | 0x00000008 |
| `EVERYTHING_IPC_MATCHACCENTS` = `MATCHDIACRITICS` | 0x00000010 |
| `EVERYTHING_IPC_MATCHPREFIX` (1.5) | 0x00000020 |
| `EVERYTHING_IPC_MATCHSUFFIX` (1.5) | 0x00000040 |
| `EVERYTHING_IPC_IGNOREPUNCTUATION` (1.5) | 0x00000080 |
| `EVERYTHING_IPC_IGNOREWHITESPACE` (1.5) | 0x00000100 |

`request_flags`（仅 QUERY2）：

| 名字 | 值 | 名字 | 值 |
|---|---|---|---|
| `NAME` | 0x1 | `ATTRIBUTES` | 0x100 |
| `PATH` | 0x2 | `FILE_LIST_FILE_NAME` | 0x200 |
| `FULL_PATH_AND_NAME` | 0x4 | `RUN_COUNT` | 0x400 |
| `EXTENSION` | 0x8 | `DATE_RUN` | 0x800 |
| `SIZE` | 0x10 | `DATE_RECENTLY_CHANGED` | 0x1000 |
| `DATE_CREATED` | 0x20 | `HIGHLIGHTED_NAME` | 0x2000 |
| `DATE_MODIFIED` | 0x40 | `HIGHLIGHTED_PATH` | 0x4000 |
| `DATE_ACCESSED` | 0x80 | `HIGHLIGHTED_FULL_PATH_AND_NAME` | 0x8000 |

item flags：`EVERYTHING_IPC_FOLDER` 0x1、`DRIVE` 0x2、`ROOT` 0x2。
内置筛选器常量：`FILTER_EVERYTHING` 0、`AUDIO` 1、`COMPRESSED` 2、`DOCUMENT` 3、`EXECUTABLE` 4、`FOLDER` 5、`PICTURE` 6（1.5 另有别名 `IMAGE` 6）、`VIDEO` 7、`CUSTOM` 8。

> **对 UniSearch 的含义**：`HIGHLIGHTED_*` 表明 Everything 自己会返回带 `*` / `**` 标记的高亮串
> （EverythingToolbar 的 `SearchResult(HighlightedPath, HighlightedFileName, …)` 就是这么来的）。
> 因此名称命中高亮**不要自己算**，直接消费 Everything 的标记并转换成统一的 `[[..]]` 记法。

## 表 C — `EVERYTHING_IPC_SORT_*`（0 未定义；DLL SDK 的 `EVERYTHING_SORT_*` 数值完全相同）

升序/降序成对：NAME 1/2、PATH 3/4、SIZE 5/6、EXTENSION 7/8、TYPE_NAME 9/10、
DATE_CREATED 11/12、DATE_MODIFIED 13/14、ATTRIBUTES 15/16、FILE_LIST_FILENAME 17/18、
RUN_COUNT 19/20、DATE_RECENTLY_CHANGED 21/22、DATE_ACCESSED 23/24、DATE_RUN 25/26。
头文件建议：`SORT_NAME_ASCENDING` 性能最好；开了对应 fast sort 后其它排序也是瞬时的（用请求 410 `IS_FAST_SORT` 探测）。

## 表 D — 请求结构（`#pragma pack(push,1)`）

`EVERYTHING_IPC_QUERYW` / `QUERYA`（dwData = 2 / 1）：

| 偏移 | 类型 | 字段 |
|---|---|---|
| 0 | DWORD | `reply_hwnd` |
| 4 | DWORD | `reply_copydata_message`（回复时 `dwData` 用它回显） |
| 8 | DWORD | `search_flags` |
| 12 | DWORD | `offset` |
| 16 | DWORD | `max_results` |
| 20 | TCHAR[] | `search_string`（NUL 结尾） |

`EVERYTHING_IPC_QUERY2`（dwData = 18 W / 17 A，**需要 Everything 1.4.1+**）：

| 偏移 | 类型 | 字段 |
|---|---|---|
| 0 | DWORD | `reply_hwnd` |
| 4 | DWORD | `reply_copydata_message` |
| 8 | DWORD | `search_flags` |
| 12 | DWORD | `offset` |
| 16 | DWORD | `max_results`（`EVERYTHING_IPC_ALLRESULTS` = 0xFFFFFFFF） |
| 20 | DWORD | `request_flags` |
| 24 | DWORD | `sort_type` |
| 28 | TCHAR[] | `search_string` → 定长头 28 字节，整体最小 30 字节 |

`cbData = sizeof(struct) - sizeof(TCHAR) + wcslen(search)*sizeof(TCHAR) + sizeof(TCHAR)`（头文件原文公式）。
`max_results = 0` 表示只要总数不要条目。**每个 reply 窗口同时只跑一个查询**：查询未结束时再发查询会取消前一个 —— 这天然适合"新输入覆盖旧输入"，也正是 Broker 侧 `RequestId` 校验存在的原因。

## 表 E — 回复记录

`WM_COPYDATA` 发到 `reply_hwnd`，`dwData` 回显 `reply_copydata_message`；**必须在返回 TRUE 前把数据拷走**。

`EVERYTHING_IPC_LISTW/LISTA`（全 DWORD）：0 `totfolders`、4 `totfiles`、8 `totitems`、12 `numfolders`、16 `numfiles`、20 `numitems`、24 `offset`（首个结果的索引）、28 `items[1]`。

`EVERYTHING_IPC_ITEMW/ITEMA`（12 字节，packed）：0 `flags`、4 `filename_offset`、8 `path_offset`；偏移量**相对 list 结构体起始**，取值方式 `(TCHAR*)((char*)list + item->path_offset)`。

`EVERYTHING_IPC_LIST2`（20 字节头）：0 `totitems`、4 `numitems`、8 `offset`、12 `request_flags`（回显有效位）、16 `sort_type`（实际用的，可能与请求不同）；随后 `numitems` × `EVERYTHING_IPC_ITEM2`；再往后是数据区。

`EVERYTHING_IPC_ITEM2`（8 字节）：0 `flags`、4 `data_offset`（相对 LIST2 起始）。

遍历要点（**没有 `length` 字段**）：v1 item 定长 12 字节步进，LIST2 item 定长 8 字节步进；真正的变长发生在数据区 —— 从 `list + data_offset` 起，每个被请求的字符串字段是 `DWORD length`（字符数，不含 NUL）+ NUL 结尾文本，所以前进 `4 + (length+1)*sizeof(TCHAR)`；`SIZE` 是 `LARGE_INTEGER`(8)，`DATE_*` 是 `FILETIME`(8)，`ATTRIBUTES`/`RUN_COUNT` 是 DWORD(4)。

其他 dwData 请求号：0 `COPYDATA_COMMAND_LINE_UTF8`（负载 `EVERYTHING_IPC_COMMAND_LINE{DWORD show_command; BYTE command_line_text[]}`）、1/2 `COPYDATAQUERYA/W`、17/18 `COPYDATA_QUERY2A/W`、19/20 `GET_RUN_COUNTA/W`、21/22 `SET_RUN_COUNTA/W`、23/24 `INC_RUN_COUNTA/W`；run-count 负载是 `EVERYTHING_IPC_RUN_HISTORY{DWORD run_count; TCHAR filename[]}`。

> **对 UniSearch 的含义**：`RUN_COUNT` 是 Everything 自己维护的"用户打开次数"。
> 排序里的 MRU/频率信号应当**优先读 Everything 的 run count**，而不是自己在 `IUsageStore` 里重复记账；
> 自建 usage 只用于非文件实体（文献/笔记/应用 URI）。

## 表 F — 搜索函数语法（voidtools 文档，2026 现行）

| 函数 | 语义 | 例子 |
|---|---|---|
| `parent:` | 直接父目录等于给定绝对路径（结尾 `\` 被忽略） | `parent:C:\Windows`、`parent:"C:\Program Files"` |
| `ancestor:` | 祖先目录（递归限定） | `ancestor:"D:\Research\AI"` |
| `child:` / `location:` (`loc:`) | 按子项名 / 按所在目录名匹配 | `child:.jpg`、`location:Downloads` |
| `full-path:` | 完整路径函数 | `full-path:c:\windows\notepad.exe` |
| `path:` | **修饰符**（Match Path 开/关，别名 `\:`），不是函数 | `path:system32 hosts` |
| `folder:` / `file:` | 只匹配目录 / 只匹配文件 | `folder:music`、`file:.mp3` |
| `ext:` (别名 `extension:`) | 扩展名，支持 `;` 列表与通配 | `ext:mp3;flac`、`ext:jpg;png`、`ext:*.txt;*.doc*`；`!extension:` 表示无扩展名 |
| `kind:` / `type:` | 类型分组（文档/图片/视频/音乐/文件夹） | `kind:picture`、`kind:document` |
| `size:` | 大小区间 | `size:>100mb`、`size:>=10mb`、`size:2mb..3mb`（或 `2mb-3mb`）、`size:!1kb`、裸 `size:1kb` == `=1kb` |
| `dm:` (别名 `date-modified:`) | 修改时间 | `date-modified:today`、`dm:2026-07-02..2026-07-03`、`dm:10mins`；另有 `dmdate/dmday/dmhour/dmtime/dmyear/dmdow` |
| `offline:` / `online:` | 索引状态（放在查询串里，没有对应 IPC 标志） | `offline:` |
| 引号 | 含空格路径**必须**加引号 | `"c:\program files\"` = 在该目录内搜索；不加引号会被拆成 AND 词项 |
| `\|` | OR，两侧的空白被忽略 | `ext:docx \| folder:` |
| `!` | NOT | `!attribute:h` |
| `<...>` | 分组 | `folder: !<child:.jpg\|child:.flac>` |
| 空格 | AND | `ext:docx content:abc` |

> **对 UniSearch 的含义（Scope 翻译）**：Explorer "只看本层" → `parent:"<dir>"`；
> "含子目录" → `ancestor:"<dir>"`（注意 Everything 的 `parent:` 不递归，别拿它当递归用）。
> 路径含空格必须整体加引号，且**目录末尾带 `\`** 才是"在该目录内"。
> 分类过滤优先翻成 `kind:` / `ext:`，而不是自己维护一张扩展名表去后过滤 —— 让 Everything 干活。

## 表 G — 状态/信息类请求

`SendMessage(FindWindow(EVERYTHING_IPC_WNDCLASS), EVERYTHING_WM_IPC /*WM_USER=0x0400*/, request, lParam) : int`

| 用途 | 请求号 |
|---|---|
| 判断 Everything 是否在跑 + 版本 | `FindWindow` 非空后依次发 `GET_MAJOR_VERSION` 0、`GET_MINOR_VERSION` 1、`GET_REVISION` 2、`GET_BUILD_NUMBER` 3（如 1.4.1.877；`everything-ipc` 以 minor >= 4 判定可用） |
| `EXIT` 4 · `GET_TARGET_MACHINE` 5（需 1.4.1；X86 1 / X64 2 / ARM 3 / ARM64 4）· `RESTART` 6（1.5） | 生命周期 |
| 快捷方式/集成 `DELETE_*` 100–105、`CREATE_*` 200–205、`IS_*` 300–306（`IS_SERVICE` 306） | 服务/注册 |
| 索引状态 `IS_NTFS_DRIVE_INDEXED` 400、`IS_DB_LOADED` 401、`IS_DB_BUSY` 402、`IS_ADMIN` 403、`IS_APPDATA` 404、`REBUILD_DB` 405、`UPDATE_ALL_FOLDER_INDEXES` 406、`SAVE_DB` 407、`SAVE_RUN_HISTORY` 408、`DELETE_RUN_HISTORY` 409、`IS_FAST_SORT` 410、`IS_FILE_INFO_INDEXED` 411、`QUEUE_REBUILD_DB` 412、`SAVE_DB_NOW` 413(1.5) | 健康探测 |
| `FILE_INFO_*`：FILE_SIZE 1、FOLDER_SIZE 2、DATE_CREATED 3、DATE_MODIFIED 4、DATE_ACCESSED 5、ATTRIBUTES 6 | 411 的 lParam |
| 发给类名 `EVERYTHING` 的 UI 状态：500 IS_MATCH_CASE、501 WHOLE_WORD、502 PATH、503 DIACRITICS、504 REGEX、505 FILTERS、506 PREVIEW、507 STATUS_BAR、508 DETAILS、509 GET_THUMBNAIL_SIZE、510 GET_SORT、511 GET_ON_TOP(0/1/2)、512 GET_FILTER、513 GET_FILTER_INDEX、514–518 PREFIX/SUFFIX/PUNCT/WHITESPACE/SEARCH_AS_YOU_TYPE(1.5)、519–523 folder sidebar / sidebar width(1.5) | 与原生 Everything 窗口同步 |

多实例：没有 `GET_INSTANCE` 请求。真机制是类名后缀 `EVERYTHING_TASKBAR_NOTIFICATION_(<instance>)`（枚举顶层窗口解析），或 1.5 命名管道 `Everything3_ConnectW(instance_name)`（错误码 `EVERYTHING3_ERROR_IPC_PIPE_NOT_FOUND` 0xE0000002）。

## 1.5 命名管道协议（纯 C# 可行，源码 MIT）

来源：`refs/everythingtoolbar/EverythingSDK3/src/Everything3.c`（9633 行，MIT，Copyright (c) 2025 voidtools / David Carpenter）。

- 管道名：`\\.\PIPE\Everything IPC`，带实例名时 `\\.\PIPE\Everything IPC (<escaped instance>)`；`:` 与 `\` 需转义。Everything 会同时监听多个服务端，连不上要重试。
- 报文头：`{ DWORD code; DWORD size /*不含头*/ }`，随后 `BYTE data[size]`。
- 请求码：`GET_IPC_PIPE_VERSION` 0、`GET_MAJOR_VERSION` 1、`GET_MINOR_VERSION` 2、`GET_REVISION` 3、`GET_BUILD_NUMBER` 4、`GET_TARGET_MACHINE` 5、`FIND_PROPERTY_FROM_NAME` 6、**`SEARCH` 7**、`IS_DB_LOADED` 8、`IS_PROPERTY_INDEXED` 9、`IS_PROPERTY_FAST_SORT` 10、`GET_PROPERTY_NAME` 11、`GET_PROPERTY_CANONICAL_NAME` 12、`GET_PROPERTY_TYPE` 13、`IS_RESULT_CHANGE` 14、`GET_RUN_COUNT` 15、`SET_RUN_COUNT` 16、`INC_RUN_COUNT` 17、`GET_FOLDER_SIZE` 18、`GET_FILE_ATTRIBUTES` 19、`GET_FILE_ATTRIBUTES_EX` 20、`GET_FIND_FIRST_FILE` 21、**`GET_RESULTS` 22**、`SORT` 23、`WAIT_FOR_RESULT_CHANGE` 24、`IS_PROPERTY_RIGHT_ALIGNED` 25、`IS_PROPERTY_SORT_DESCENDING` 26、`GET_PROPERTY_DEFAULT_WIDTH` 27、`GET_JOURNAL_INFO` 28、`READ_JOURNAL` 29。
- 响应码：`OK_MORE_DATA` 100、`OK` 200、`BAD_REQUEST` 400、`CANCELLED` 401（处理中被新请求抢占）、`NOT_FOUND` 404、`OUT_OF_MEMORY` 500、`INVALID_COMMAND` 501。
- `SEARCH` 负载顺序（见 `_everything3_search_with_extra_flags`）：
  `DWORD search_flags` → `VLQ(len)` + UTF-8 `search_text` → `SIZE_T viewport_offset` → `SIZE_T viewport_count` →
  `VLQ(sort_count)` + `sort_count × {DWORD property_id; DWORD flags}` →
  `VLQ(prop_req_count)` + `× {DWORD property_id; DWORD flags}`。
  注意 `SIZE_T` 是**指针宽度**（x64 = 8 字节），文本是 **UTF-8**，长度用 **VLQ 变长编码**。
- 属性 id：`NAME` 0、`PATH` 1、`SIZE` 2、`DATE_MODIFIED` 5、`PATH_AND_NAME` 240（EverythingToolbar 的 pipe client 只用了这几个）。
- 结果条目：每项以 `BYTE item_flags` 开头，随后按请求的属性顺序内联排布；字符串用 pstring（`BYTE len`，`len==255` 时后跟 `SIZE_T` 真实长度，**不含 NUL**）。

> **决策含义**：1.5 走命名管道（纯 C# + `FileStream`/`NamedPipeClientStream` 即可，无原生 DLL、无窗口消息泵）；
> 1.4 走 WM_COPYDATA（需要 message-only 窗口）。Provider 内部先试管道、失败回落 IPC，
> 与 EverythingToolbar 的 `EverythingClientRouter` 策略一致。

## 待验证（不要当事实用）

- `EN_*` 系列：已确认为虚构（见第 0 节）。
- AnyTXT 的 `ATRpcServer.Searcher.V1.GetResult`：仅见于二手描述，尚未找到可运行的调用样例 —— 见 `ANYTXT.md`。
- Zotero 本地 API 的确切端点集合：见 `ZOTERO.md`。
