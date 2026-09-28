# UniSearch 查询语法与透传规则

> 实现位置：`src/UniSearch.Core/Parsing/QueryParser.cs`；测试：`tests/UniSearch.Core.Tests/CoreLogicTests.cs`。
> 规则来源：PowerToys CmdPal 的 Ext.Indexer "Query Handling Contract"（见 `docs/research/REF-5-powertoys-cmdpal.md` §2.1）。

## 1. 两条通路的划分原则

用户输入只有两种命运，**必须先判定再决定**：

| 通路 | 触发条件 | Core 行为 | Provider 收到什么 |
|---|---|---|---|
| **解析（normalize）** | 自由文本，或只含"自家过滤器动词" | 拆词项、建 `QueryFilters`、做统一后过滤与打分 | `q.ProviderText` = 由过滤器重建的中立串 |
| **透传（passthrough）** | 看起来是**后端专有语法** | 不拆、不加词项、**不做词项后过滤** | `q.ProviderText` = 原样输入 |

理由：把 `content:abc`、`(a|b)`、`C:\Users` 拆开会破坏语义；而把 `ext:pdf` 也整串透传又会丢掉词项、导致跨后端无法统一打分。**所以动词表分成两套**（这是被单元测试逼出来的设计）：

```csharp
OwnVerbSet     = { ext, extension, kind, type, size, dm, dc, regex, re, folder, file }
ForeignVerbSet = { content, filelist, parent, ancestor, child, location, full-path, path, name,
                   attrib(utes), date-created/modified/accessed/run, run-count, count, app-name,
                   product-name, company-name, copyright, description, version, audio-format,
                   video-format, frame-rate, bit-rate, duration, dimension, width, height, is,
                   boolean, diacritics, case, wholeword, wildcards, regexp, prefix, suffix,
                   punctuation, whitespace, drive, volume, ntfs, usn, junction, hardlink, symlink,
                   empty, duplicates, offline, online, list(s) }
```

> `ForeignVerbSet` 只收录 **已在 `docs/research/EVERYTHING-IPC-VERIFIED.md` 表 F 核实存在**的函数。
> 社区流传的 `pic:` / `exe:` / `folderparent:` 未获证实，**不得**加入。

## 2. `LooksStructured` 判定顺序（逐条可测）

1. 以盘符路径开头：`C:\Users`、`d:/x` → 透传（拆成词项就废了）
2. 以 `\\` 开头（UNC）→ 透传
3. 含 AQS 布尔词 ` AND ` / ` OR ` / ` NOT ` → 透传
4. 逐 token：
   - 前导 `!`（取反，如 `!temp`）→ 透传
   - `verb:value` 且 verb ∈ OwnVerbSet → **继续解析**（不因此透传）
   - `verb:value` 且 verb ∈ ForeignVerbSet → 透传
   - 含 `*` 或 `?`（通配）→ 透传
   - 含 `(` `)` `|`（分组/OR）→ 透传
   - 形如路径（含 `\` 且以 `\` 结尾或含 `:\`）→ 透传

### 边界情形（都有测试覆盖）

| 输入 | 结果 | 说明 |
|---|---|---|
| `ext:pdf\|docx` | 解析 | `\|` 出现在**自家过滤器值内部**，按扩展名列表处理，不当 OR 运算符 |
| `ext:pdf \| folder:` | 透传 | 独立 `\|` 是真 OR，Core 无法表达，交给 Everything |
| `kind:pdf x` | 解析 | `pdf` 命中扩展名表 → 变成 `Extensions=["pdf"]`，词项保留 `["x"]` |
| `kind:folder report` | 解析 | → `FoldersOnly=true` + 词项 `["report"]` |
| `size:>10mb` | 解析 | → `MinSizeBytes=10485760` |
| `name:foo OR name:bar` | 透传 | `name:` 属 Foreign（Everything 的 `name:` 与我们的语义不同），且含 OR |
| `会议纪要 2024` | 解析 | 中文按空白分词，不做任何特殊处理 |

## 3. 类型过滤与调度联动（避免"问了不该问的后端"）

`ProviderSelector.CanSatisfyTypeFilter`：

- `Kinds` 非空 → 要求该 Provider 能产出其中至少一种（`ReturnsFiles` 天然覆盖 文件/文档/图片/视频/音频/压缩包/代码；细分位留给"只产出一类"的后端）。
- `Extensions` 非空 → 要求 `ReturnsFiles` 或 `SupportsKindFilter`。
  → 搜 `ext:pdf` 时**不会**去问 Zotero（它只能返回文献条目）。

## 4. 各 Provider 如何消费

| Provider | 消费方式 |
|---|---|
| Everything | 用 `q.ProviderText`（透传时=原文，解析时=重建串），前面拼范围前缀 `ancestor:"D:\x\"` / `parent:"D:\x\"`。**绝不重排用户串**。 |
| WindowsIndex (SystemIndex) | 透传时交给 `ISearchQueryHelper.GenerateSQLFromUserQuery(...)`（微软自己懂 AQS）；解析时用 `scope='file:<dir>'` + 名称/正文条件 |
| **AnyTXT** | **不透传**（Everything 的函数名对它毫无意义）→ 只用 `q.Text` / `Filters.Phrase`；扩展名→`filterExt`、目录→`filterDir`、`ModifiedAfter`→`lastModifyBegin`；**全盘搜索逐盘枚举**（`filterDir` 传空会被强制成 `C:`）。正则/大小/文件夹/类型下推不了 → 逐条记 Notes 如实降级 |
| **Zotero** | **不透传** → `q.Text`→`q=`、`Filters.Kinds`→`itemType=`（近似映射）、集合走 `NamedScope`。扩展名/大小/日期范围 API **根本不支持** → 前端过滤 + Notes。⚠ 未知参数会被**静默忽略**，所以绝不塞 `date=` 假装在筛 |
| Obsidian | 只用 `q.Text`；`Extensions`/`size:` 之类对它无意义（调度阶段多半已被跳过） |

> **"不透传"是刻意的**：`ForeignVerbSet` 收的是 **Everything 的**函数名。把它原样送给 AnyTXT/Zotero
> 只会得到 0 条，而用户看到的只是"搜不到"—— 所以这两个后端一律退化成纯文本，并明确说一句
> "该后端不支持高级查询语法，已按纯文本搜索"。

## 5. UI 侧约定

- 输入框右侧显示一个**语法模式徽标**：`普通` / `高级(原样)`，让用户知道我们没在改写他的查询（`SearchQuery.SyntaxNotice == "raw-syntax"` 时点亮）。
- 分类标签点击 = `q.ForcedCategory`，由 Core 做硬过滤，**不写进查询串**（保持输入框内容即用户所见）。
- 空输入 + Explorer 上下文 = 列举当前目录（`ListScopeContents`），不是"匹配全库"。
