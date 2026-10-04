# UniSearch 容易搜

**可替换搜索后端的全局聚合搜索器**（Windows 桌面，WPF）。
一个搜索框，同时打多个后端：文件名、文件正文、文献库、笔记块 —— 各后端以 Provider 形式接入，统一结果模型、统一筛选与排序。

> 0.1 Alpha：功能可用、接口（Provider Sdk）未冻结，欢迎试用与反馈。

## 功能

- **四个内置后端**
  - **Everything** —— NTFS 文件名搜索，毫秒级（经 IPC，不是命令行拼凑）
  - **AnyTXT** —— 文件**正文**全文检索（含命中片段）
  - **Zotero** —— 文献元数据 + PDF 全文（本地 API）
  - **思源笔记** —— 知识库块级检索；**跨机部署**（思源在另一台机器上也能搜，支持把块导出成 Markdown 预览）
- **融合与筛选**：多后端结果融合去重、内置分类标签 + 自定义筛选器（`filters.json` 定义，**保存即热重载**）、值域筛选器（候选值来自后端自己，如 Zotero 标签 / 思源笔记本）
- **真 Shell 右键**：单选弹原生资源管理器菜单（打开方式 / 发送到 / 7-Zip…），宿主动作追加不覆盖；多选走批量动作菜单（一键压缩为 ZIP / 复制路径 / 引号列表等）
- **预览面板**：图片 / 文本 / 代码 / PDF 首页（系统自带渲染器），无本地文件的结果由 Provider 供内容（思源跨机 Markdown 预览）
- **细节**：Ctrl+F 只在资源管理器前台接管（低级键盘钩子）、托盘常驻、列宽/排序/布局持久化

## 下载与运行

从 [Releases](https://github.com/RyzeZhou/UniSearch/releases) 下载 `UniSearch-*-win64.zip`，解压即用（免安装，配置在 `%LOCALAPPDATA%\UniSearch\`）。

要求：**Windows 10 1809+ / .NET 8 Desktop Runtime**（[下载](https://dotnet.microsoft.com/download/dotnet/8.0/runtime)）。

后端依赖：

| 后端 | 要求 |
|---|---|
| Everything | 需运行 Everything 1.4+（便携安装也可，会自动定位） |
| AnyTXT | 需安装 AnyTXT Searcher（含 SDK 功能的许可证） |
| Zotero | 需运行 Zotero 7+（本地 API 默认开启） |
| 思源笔记 | 可选；跨机时在设置里填 `host` / `port` / 凭证 |

## 从源码构建

```powershell
dotnet build -c Debug
dotnet test  -c Debug
dotnet publish src/UniSearch.Host -c Release -o dist
```

## 文档

开发日志（做到哪了、怎么验证的、踩过什么坑）在 [`docs/DEVLOG.md`](docs/DEVLOG.md)；
规格与协议实测见 [`docs/spec/`](docs/spec/)、[`docs/research/`](docs/research/)（Everything IPC / AnyTXT RPC / Zotero API / 思源内核 API 的"唯一事实来源"实测文档）。

## 许可

[MIT](LICENSE)。第三方依赖全部为 MIT / Apache-2.0（审计记录见 [`docs/research/UPSTREAM-AUDIT.md`](docs/research/UPSTREAM-AUDIT.md)）；Zotero / 思源等外部软件只经其公开 HTTP API 交互，不链接其代码。
