# 筛选器模板：标签栏模块化 + 按后端分叉（方案草案）

> 状态：**设计草案**（2026-09-27）。评估来源：ZCode 主机会话（接口摸底 + 现有代码实读）。
> 对齐既有遗留：**吸收 C3**（筛选器热重载）、**为 C2**（筛选器组合 + 下推）铺路、落实 **C4** 的按后端配置。
> 第一原则：**只改标签栏的"组织方式"，不改筛选器本身的匹配语义**；不动 `UniSearch.Sdk`（除非做 §7 的下推）。

---

## 1. 目标 / 不目标

**目标**

1. 标签栏按**模板**组织：一个模板 = 一组筛选器定义的有序引用，可整体切换；
2. **每个后端有默认模板**：来源栏切到 Zotero → 标签栏自动换"文献查找"；切回 Everything → "文件查找"；
3. 用户可**钉住**某后端的模板（记住选择），也可一键恢复跟随默认；
4. 定义文件**热重载**（顺带收掉 C3）；
5. 与现有两份 `filters.json`（程序模板 + 用户覆盖）的合并机制完全兼容——老文件一个字不改，行为同现状。

**不目标（本期）**

- 不改 `FilterDefinition` 的匹配语义（extensions / kinds / namePattern 并集判定照旧，它回答的是"这条结果属于哪一桶"，与后端无关）；
- 不实现多标签**组合**的交/并语义（仍归 C2；模板只是容器，不抢先定义组合语义）;
- 不做筛选器**下推**（§7 才做，且与 Zotero Provider 同期最划算）；
- 不做查询语法层的模板（`ext:` `size:` 等仍由 `QueryParser` 解析，与标签栏无关）。

---

## 2. 现状与问题

| 现状 | 出处 | 问题 |
|---|---|---|
| 筛选器 = 结果标签，前端匹配 `Matches(SearchResult)` | `FilterCatalog.cs:68` | 对毫秒级全量的 Everything 够用；对慢后端（Zotero：服务端还有大量未返回数据）是"假过滤" |
| `FilterDefinition.Providers` 已支持按后端显隐 | `FILTERS.md` 字段表、`FilterCatalog.cs:63` | 只有"显示/不显示"一档，表达不了"这组标签属于文献场景" |
| 标签栏 = 所有定义平铺一排 | `SearchSessionViewModel.cs:879` `Catalog.For(EffectiveProviderScope)` | 定义一多就是长排；不同场景（找数据文件 / 找文献 / 找代码）想看的组不同 |
| 改 `filters.json` 要重启 | C3（`US-2026-09-25-01`） | 体验割裂 |
| `providers.<id>.options` 无人读 | C4 | 按后端配置没有落点 |

关键观察：**"按后端分叉"的机制已经存在**（`Providers` 字段 + `For(providerIds)`），缺的只是上面那层"分组与默认"。本方案是加一层，不是重做。

---

## 3. 数据模型（`filters.json` v2）

在现有文件里**追加** `templates` 数组，`filters` 原样不动：

```json
{
  "version": 2,
  "comment": "……原有说明……",
  "filters": [
    { "id": "bioinformatics", "name": "生信相关", "order": 500, "extensions": ["pdb", "cif", "…"] },
    { "id": "content-hits",   "name": "仅正文命中", "providers": ["anytxt"], "kinds": ["File"] },
    { "id": "zot-itemtype",   "name": "条目类型",   "providers": ["zotero"], "kinds": ["BibliographicItem", "Note"] },
    { "id": "zot-tag",        "name": "标签",       "providers": ["zotero"], "kinds": ["BibliographicItem"] }
  ],
  "templates": [
    {
      "id": "files",
      "name": "文件查找",
      "order": 100,
      "filters": ["bioinformatics", "cad3d", "notebooks", "configs"],
      "defaultFor": ["everything"],
      "providers": ["everything"]
    },
    {
      "id": "literature",
      "name": "文献查找",
      "order": 110,
      "filters": ["zot-itemtype", "zot-tag", "content-hits"],
      "defaultFor": ["zotero"],
      "providers": ["zotero"]
    },
    {
      "id": "minimal",
      "name": "极简",
      "order": 900,
      "filters": [],
      "defaultFor": ["*"]
    }
  ]
}
```

字段语义：

| 字段 | 必填 | 说明 |
|---|---|---|
| `id` / `name` / `order` | ✅ | 同 `FilterDefinition` 的口径；`id` 稳定，改名 = 换模板 |
| `filters` | ✅（可为空数组） | 引用筛选器定义的 `id`，**只引用不复制**；同一定义可进多个模板 |
| `defaultFor` | ⬜ | 该模板可作为哪些后端的默认；`"*"` = 全局兜底默认 |
| `providers` | ⬜ | 只在哪些后端下**可选**；留空 = 所有后端可选 |

规则：

1. **合并沿用现制**：程序模板 + `%LOCALAPPDATA%\UniSearch\filters.json`，同 id 后者为准；`filters` 与 `templates` **各自独立合并**。
2. **双重显隐**：模板内可见的标签 = 模板引用的 id ∩ `FilterDefinition.AppliesTo(providerId)`。`Providers` 字段继续负责"这个标签对这个后端没意义就藏掉"（例如"仅正文命中"在 Everything 下）。
3. **兼容回退（零破坏）**：文件里没有 `templates` 节 → 回退现状：所有未 `hidden` 且 `AppliesTo` 的定义按 `order` 平铺。**升级第一天行为不变。**
4. **引用校验**：模板引用了不存在的 filterId → 剔除该引用、写进 `Problems`（现成机制），不崩。
5. `defaultFor` 冲突（两个模板都声明同一后端）→ `order` 小者胜，并记 `Problems` 提示。

---

## 4. 运行时行为

### 4.1 激活模板解析链（每个后端独立）

```
settings.filterTemplates.<providerId>        // 1. 用户钉住的（见 4.2）
  → providers.<id>.options.filterTemplate    // 2. 预留：部署级默认（C4 口子真正被读起来）
  → 模板 defaultFor 含该后端且 order 最小者   // 3. 定义文件的默认
  → defaultFor 含 "*" 且 order 最小者        // 4. 全局兜底
  → 无 templates 节                          // 5. 回退现状（平铺）
```

解析结果按 `(providerId, templateId)` 缓存在 `SearchSessionViewModel`；来源栏变化只触发重解析，不触发重查。

### 4.2 自动跟随与钉住

- `EffectiveProviderScope` 变化（`SourceViewModel` 点击/取消，`SearchSessionViewModel.cs:508`）→ 若该后端**未被钉住** → 切到解析链结果；
- 用户手动切模板 → 写入 `settings.filterTemplates.<providerId>`（钉住），之后不再自动跟随；
- 模板下拉里提供"恢复默认"：删除该键，回到解析链。

### 4.3 切换的成本语义

纯模板切换 = **重算标签栏可见集合**（内存过滤），**不重新搜索**、不改结果集——除非激活的标签里含有 §7 的"可下推筛选器"且刚被勾选（那一步本来就要重查）。

---

## 5. UI

- 标签栏**最左侧**加一枚"模板锚点"：显示当前模板名，点击弹下拉（或右键菜单，风格对齐 `SourceViewModel` 的来源栏）——列出**当前后端可选**的模板 + "恢复默认"；
- 切换即时生效，锚点上当前模板名加粗；
- 设置窗「筛选器」节（`SettingsWindow.xaml:169` 附近）追加"模板"列表：勾选定义入模板、拖动排序、指定 `defaultFor`；保存写回**用户那份** `filters.json`（程序模板永不回写）；
- 快捷键：`Ctrl+T` 循环切换当前后端可用模板（可选，进 `--selftest-keys`）。

---

## 6. 热重载（吸收 C3）

- `App.xaml.cs` 已有两份文件的加载点（`App.xaml.cs:107`），新增 `FileSystemWatcher` 监视同两处；
- 变更 → 防抖 300ms → 重跑 `FilterCatalog.Load` → 成功则 `SetFilterCatalog`（签名扩展为携带模板）+ 重解析激活模板；失败（JSON 坏）→ **保留旧配置** + 状态条/日志给出 `Problems` 提示，绝不静默用旧的或崩；
- 设置窗「打开筛选器目录」按钮（现成）成为主要编辑入口，保存即生效。

---

## 7. 与下推的衔接（C2 的一半，P3，建议与 Zotero Provider 同期）

标签栏本质是**前端过滤**，对"服务端还有更多数据"的慢后端会给出误导性的小结果集。模板本身不解决这个问题，但把口子留好：

- `FilterDefinition` 增可选字段 `pushdown`：`{ "zotero": { "param": "tag" }, "everything": { "syntax": "ext:<值>" } }`；
- Broker 侧：激活的筛选器若当前后端**能**下推 → 翻译进查询（Sdk 的 `QueryFilters` 需加通用附加袋，`ApiVersion` 升 2）；**不能** → 照旧前端 `Matches()` 过滤，且在 `ProviderOutcome.Detail` 写明"标签 X 在前端过滤"（C2 的"如实降级"原则）；
- 计数口径随之下分：下推后 `TotalAvailable` 变准，前端过滤只影响已返回子集——状态条"显示 N / 共 M 条"两种情况都要成立；
- Zotero 的自然映射：`zot-tag` → `tag=`、`zot-itemtype` → `itemType=`、`namePattern` → 无下推（前端）。**没有 Zotero Provider，这套映射没有第一个真实消费者**，所以 P3 排在它旁边，不单独立项。

### 7.1 实施记录：最后没走 `FilterDefinition.pushdown`，走了独立的 `IFacetProvider`

（2026-09-29 补记）上面那条路在真做的时候被否掉了，原因值得记下来：

**`FilterDefinition` 的值域是"枚举式"的** —— 一批扩展名、一个类型。它是封闭集合，所以
"这条定义能不能下推"是个静态问题，写在定义文件里说得通。

**标签不是**：它的候选值是用户数据，只有后端知道。于是两件事同时变了：
① 值从哪来（必须问后端）；② 界面上怎么摆（上百个候选值摊不平，得"点开才展开"）。
把这两件事硬塞进 `FilterDefinition` 会让一个"匹配规则"的记录同时承担"值域来源"和"UI 形态"，
而且 `filters.json` 是用户手写的文件 —— 让用户在里面写"这个筛选器的值去问 Zotero 要"很别扭。

所以拆成了一个**平行机制**（见 `FILTERS.md`「值域筛选器」）：

| | 宿主筛选器（filters.json） | 值域筛选器（IFacetProvider） |
|---|---|---|
| 值域来源 | 定义文件（宿主） | **后端自己**（`/tags` 这类端点） |
| 值域大小 | 小、封闭 | 可能上百、随用户数据变 |
| UI | 一枚标签，直接摊在标签栏 | 一枚锚点 + 展开面板，顶部只回显已选 |
| 计数 | 前端按已返回结果数 | **后端给**（服务端口径） |
| 下推 | 翻译成 `ext:`/`kind:` | 后端自己拼查询参数 |

两条路在标签栏上**并列显示**（锚点在最左，紧跟模板锚点），各自独立演进。
`zot-tag` 那条 `FilterDefinition` 从来没写进 `filters.json`，就被这个机制取代了。

---

## 8. 分阶段落地

| 阶段 | 内容 | 验收 |
|---|---|---|
| **F0** | schema v2 + `FilterTemplate` 记录 + 两文件合并 + 解析链 + 兼容回退；纯 Core 层 | 单测：合并/覆盖/引用校验/回退/`defaultFor` 冲突各一条；无 templates 节时行为与现在逐字节一致 |
| **F1** | UI：模板锚点 + 切换 + 钉住/恢复默认 + `EffectiveProviderScope` 自动跟随 + settings 持久化 | `--selftest-filters` 扩展：切后端换模板、钉住后不跟随、恢复默认回解析链，全部程序化断言 |
| **F2** | 热重载（C3） | 改用户 filters.json → 标签栏变化免重启；写坏 JSON → 保旧 + 明确提示 |
| **F3** | 下推映射（C2 一半）+ Zotero 模板示例 | 与 Zotero Provider 联调：Zotero 来源下勾"标签"→ 查询串含 `tag=`，`TotalAvailable` 为服务端口径 |

工作量估：F0 半天、F1 一天（WPF VM/View）、F2 半天、F3 与 Zotero Provider 打包另计。

---

## 9. 涉及文件

| 文件 | 改动 |
|---|---|
| `src/UniSearch.Core/Filters/FilterCatalog.cs` | +`FilterTemplate` 记录、+模板合并与解析链（~100 行），`Load` 读 `templates` 节 |
| `src/UniSearch.Host/App.xaml.cs` | 加载点带出模板；`FileSystemWatcher`（F2） |
| `src/UniSearch.Host/ViewModels/SearchSessionViewModel.cs` | `SetFilterCatalog` 签名扩展、激活模板状态、切换命令、`EffectiveProviderScope` 变化钩子 |
| `src/UniSearch.Host/Views/*` + `SettingsWindow.xaml` | 模板锚点控件；设置窗模板编辑节 |
| `src/UniSearch.Host/Services/SettingsStore.cs`（或等效） | `settings.filterTemplates` 节读写 |
| `tests/UniSearch.Core.Tests` + `--selftest-filters` | F0/F1 断言 |
| `docs/spec/FILTERS.md` | 增补 v2 字段说明与模板规则（实施后同步，避免两份文档打架） |

## 10. 风险与开放问题

| # | 问题 | 现在的答案 |
|---|---|---|
| 1 | 用户文件只写了 `filters` 没写 `templates`，而程序模板有 → 用谁的模板？ | 模板与筛选器**独立合并**：用户的 filters 覆盖同 id 定义（现规则），但**模板以"两份合并后的并集"为准**；用户想改模板就在自己文件里写 `templates`（同 id 覆盖）。文档里写清楚 |
| 2 | `defaultFor: ["*"]` 与"无模板回退"打架？ | 不打架：`"*"` 只在有 `templates` 节时生效；无节 = 100% 现状 |
| 3 | 组合语义（C2）将来定成交集/并集，模板要不要预埋？ | 不要。模板只保证"引用与顺序"，多选时的交/并语义届时在 `Matches` 调用处一次定死，模板层零改动 |
| 4 | 全局视图（未选后端）用哪个模板？ | 解析链第 4 步（`"*"` 兜底）；若连 `templates` 都没有 → 现状平铺 |
| 5 | `REMOTE_SEARCH_PLAN` 的 `NamedScope=erf:...` 等远程后端 | 同一套模板机制天然适用（`defaultFor: ["erf"]`），无需额外设计 |
