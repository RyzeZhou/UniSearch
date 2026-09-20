# UniSearch 文档索引

| 目录 / 文件 | 内容 |
|---|---|
| `DEVLOG.md` | **开发日志** —— 做到哪了、怎么验证的、踩过什么坑、下次从哪继续。**接手先读这个** |
| `spec/` | 规格：`MVP.md`（范围与不做什么）、`ARCHITECTURE.md`、`QUERY-SYNTAX.md`、`UI-SPEC.md`、`SHELLBRIDGE.md` |
| `research/` | 上游调研：每个参考仓库一份中文文档 + `REUSE-CANDIDATES.md`（能直接用的轮子与许可证陷阱）+ `EVERYTHING-IPC-VERIFIED.md`（协议唯一事实来源） |

## 快速上手

```powershell
dotnet build -c Debug          # 0 错误（Host 进 sln 后有 36 条分析器警告，F1 只要求"不增长"）
dotnet test  -c Debug          # 94/94
dotnet run --project tools/Probe -- "pdf"      # 绕开 UI 打数据链路
```

可执行文件在 `src\UniSearch.Host\bin\Debug\UniSearch.exe`（**路径不含 TFM**，见 DEVLOG §3.4）。

界面自检（每条都是"程序化断言 + 日志结论"，不靠肉眼）：`--selftest-layout`（来源/排序/列装配/列宽拖拽/
热区可点性/拖出落点过滤/Shift+滚轮横向滚动/列布局落盘）、`--selftest-keys`、`--selftest-filters`、
`--selftest-settings` 等，用法见 `DEVLOG.md`。
截图统一放 `out/`；`--dump-render` 的输出按显示器实际缩放渲染（175% → 2240×1155，与用户屏幕 1:1）。

## 三条硬约束

1. **只复用 MIT / Apache-2.0 代码**；无许可证的仓库一行都不抄（清单见 `research/REUSE-CANDIDATES.md`）
2. **宿主目标框架是 `net8.0-windows10.0.19041.0`**（PDF 预览需要 Windows SDK 投影），且输出路径不带 TFM
3. **不实现 Windows 索引 Provider** —— Everything 已覆盖，理由见 `spec/MVP.md` §G
