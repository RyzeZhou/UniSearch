# 参考依赖 6：EverythingSearchClient（NuGet 包，不是仓库 clone）

- 包：`EverythingSearchClient` **0.9.0.148**（2025-06-26 发布），作者 SGrottel
- 许可证 **Apache-2.0**（NuGet 元数据 `licenseExpression` 确认）；仓库 `github.com/sgrottel/EverythingSearchClient`（master 分支）
- 定位：**纯托管 .NET 客户端，不依赖 voidtools 原生 `everything.dll`** —— 走 message-only window + WM_COPYDATA IPC
  正好是本项目想要的"AnyCPU、无原生二进制"路线，因此 **Everything Provider v1 直接依赖它**，
  省掉整套自写 IPC（我此前打算照 `everything_ipc.h` 手搓，现确认没必要先做）
- 已在 `src/UniSearch.Providers.Everything` 还原并编译通过

## 1. 实际 API（用 `pwsh` 反射 `lib/net8.0/EverythingSearchClient.dll` 得到，README 只写了一半）

```csharp
namespace EverythingSearchClient;

class SearchClient {
    // 全参重载（我们用的就是这个）
    Result Search(string query, SearchFlags flags, uint maxResults, uint offset,
                  BehaviorWhenBusy whenBusy, uint timeoutMs, SortBy sort, SortDirection dir);
    Result Search(string query, SearchFlags flags, BehaviorWhenBusy whenBusy, uint timeoutMs);
    Result Search(string query, BehaviorWhenBusy whenBusy, uint timeoutMs);
    Result Search(string query, uint maxResults, uint offset, BehaviorWhenBusy whenBusy, uint timeoutMs);
    Result Search(string query, uint maxResults, BehaviorWhenBusy whenBusy, uint timeoutMs);

    static bool   IsEverythingAvailable();     // ← 静态！不能用实例调用（编译期踩过）
    static Version GetEverythingVersion();     // ← 静态
    bool   IsEverythingBusy();

    QueryApi UseQueryApi { get; set; }         // Any | Query1only | Query2only
    TimeSpan ReceiveTimeout { get; set; }
    const  uint AllItems;                      // 0xFFFFFFFF

    // 内置筛选器串（与 Everything 自带 filter 一致，可省去自己维护扩展名表）
    string FilterAudio, FilterZipped, FilterDocuments, FilterExecutables, FilterPictures, FilterVideo;
}

class Result { uint TotalItems; uint NumItems; uint Offset; Item[] Items; }
class Result.Item {
    ItemFlags Flags;                 // None | Folder | Drive | Unknown
    string Name; string Path;
    ulong? Size;                     // ← 需要 QUERY2；转 long? 时要显式转换
    DateTime? CreationTime; DateTime? LastWriteTime;
    ItemFileAttributes? FileAttributes;   // None|ReadOnly|Hidden|System|Directory|Archive|Normal
}
[Flags] enum SearchClient.SearchFlags { None, MatchCase, MatchWholeWord, MatchPath, RegEx }
enum SearchClient.SortBy { None, Name, Path, Size, Extension, DateCreated, DateModified }
enum SearchClient.SortDirection { Ascending, Decending }   // ← 上游拼写错误，照抄别改
enum SearchClient.BehaviorWhenBusy { WaitOrError, WaitOrContinue, Error, Continue }
enum QueryApi { Any, Query1only, Query2only }

class EverythingBusyException; class ResultsNotReceivedException;

// 内部私有结构体（证明它确实实现了官方 QUERY2 布局，与已核实文档一致）
EverythingIPC.EVERYTHING_IPC_QUERY  { reply_hwnd, reply_copydata_message, search_flags, offset, max_results }
EverythingIPC.EVERYTHING_IPC_QUERY2 { + request_flags, sort_type }
```

要点：
- **支持 QUERY2**，所以 `Size / CreationTime / LastWriteTime / FileAttributes` 是**真实拿得到的**（不是永远 null）。
  这解决了"只要文件名"的担心：列表可以显示大小/日期，Broker 的隐藏/系统文件过滤（依赖 `attributes` 元数据）也能工作。
- `BehaviorWhenBusy` 正好对应 Everything 的"每个 reply 窗口只允许一个在飞查询"限制（见 `EVERYTHING-IPC-VERIFIED.md` 表 D）。
  UniSearch 用 **`Continue`**：忙时不抛异常、不排队等待，让上一层的新查询覆盖旧查询。
- 隐藏/系统文件通过 `ItemFileAttributes` 暴露，Provider 把它塞进 `SearchResult.Metadata["attributes"]`，
  Core 的 `Filter()` 就能统一过滤（与 `ProviderOptions.FilterHiddenAndSystemByDefault` 协同）。

## 2. 它的边界（决定我们后面要不要自写客户端）

| 缺口 | 影响 | 处置 |
|---|---|---|
| 没有 `HIGHLIGHTED_NAME/PATH`（Everything 自己算的高亮） | 名称高亮要我们自己算 | 用 `Core.Matching.NameMatcher` + `[[..]]` 记法；将来换 QUERY2 直取（更准、含模糊命中） |
| 没有 `RUN_COUNT`（Everything 记录的打开次数） | 排序少一路强信号；且无法"打开后回写" | 阶段二自写客户端补 `INC_RUN_COUNT`（dwData 23/24） |
| 没有 `EXTENSION`/`FILE_LIST_FILE_NAME`/`DATE_ACCESSED`/`DATE_RECENTLY_CHANGED` 属性位 | 次要列缺失 | 同上 |
| 只走 1.4 IPC，不接 **1.5 命名管道** | 拿不到 1.5 的属性系统（如 `filecontent:`、folder size、更细的 sort） | 阶段二做 `EverythingPipeClient`（协议已核实，见 `EVERYTHING-IPC-VERIFIED.md` "1.5 命名管道协议"） |
| `Search()` 是**同步阻塞** | Provider 必须自己丢到线程池并串行化 | 已处理：`SemaphoreSlim(1,1)` + `Task.Run`（Everything 每窗口单查询，串行是对的） |
| 不支持多实例 `EVERYTHING_TASKBAR_NOTIFICATION_(instance)` | 用户跑 1.5a + 稳定版双实例时可能连错 | `EverythingLocator.ProbeAll()` 已能枚举出全部实例；接进设置页让用户选 |
| 版本较低（作者刻意只做子集） | — | 用 `IEverythingBackend` 抽象隔离，随时可换实现 |

## 3. 隔离策略（已在代码里）

- Provider 只在 `EverythingProvider.cs` 里 `using ESC = EverythingSearchClient;`，
  对外暴露的是 `UniSearch.Sdk` 的 `SearchResult` / `SearchContext`；
- `EverythingQueryTranslator` **自己定义** `EverythingSearchFlags` 枚举镜像包内标志位，
  这样将来换成自研客户端时翻译器（及其单测）一行都不用改；
- 第三方声明写进 `LICENSES-THIRD-PARTY.md`（Apache-2.0 需要保留声明）。
