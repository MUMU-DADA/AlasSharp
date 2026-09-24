# 界面图标与素材来源

本目录下的图标几何与位图素材来自上游只读参考 **AzurPilot**（`https://github.com/wess09/AzurPilot`，
提交 `f67259dcd`），本项目按用户要求 1:1 复刻其界面外观。

| 本目录文件 | 上游来源 | 许可 |
| --- | --- | --- |
| `Icons.axaml`（生成物） | `frontend/node_modules/lucide-react@1.45.0`（`dist/esm/icons/*.mjs` 的 `__iconData.node`） | ISC + 部分 MIT，全文见 `LICENSE-lucide.txt` |
| `generate-icons.mjs` | 本项目编写；只做坐标搬运与 SVG→Avalonia 路径语法改写 | 本项目许可 |
| `LICENSE-lucide.txt` | `node_modules/lucide-react/LICENSE` 原样复制 | ISC License, Copyright (c) 2026 Lucide Icons and Contributors；其中源自 Feather 的图标为 MIT License, Copyright (c) 2013-present Cole Bemis |
| `../Brand/azurpilot.svg`、`../Brand/azurpilot-64.png`、`../Brand/azurpilot-128.png` | `frontend/public/azurpilot.svg`（PNG 由 `../Brand/rasterize-logo.mjs` 栅格化） | GPL-3.0（上游项目） |
| `../Resources/{oil,gold,diamond,cube}.webp` | `frontend/public/*.webp` | GPL-3.0（上游项目） |
| `../Catalog/generate-task-catalog.mjs`、`../../ViewModels/TaskCatalog.cs` | `module/config/argument/menu.json`、`module/config/i18n/zh-CN.json` | GPL-3.0（上游项目数据，只搬运名称与顺序） |

## 图标许可（完整声明）

```text
ISC License

Copyright (c) 2026 Lucide Icons and Contributors

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

其中 `check`、`chevron-down`、`chevron-right`、`clock`、`code`、`compass`、`download`、
`external-link`、`maximize`/`minimize` 系列、`pause`、`play`、`plus`、`search`、`square`、
`terminal`、`trash-2`、`x` 等图标源自 [Feather](https://github.com/feathericons/feather)，
另按 MIT License 授权（Copyright (c) 2013-present Cole Bemis）。
两份许可的完整文本随生成物一起放在 `LICENSE-lucide.txt`（原样复制自 npm 包）。

## 图标生成方式

```powershell
# 需要一份包含 lucide-react 1.45.0 的 node_modules（例如上游前端的依赖目录）
node src/Alas.UI/Assets/Icons/generate-icons.mjs <lucide-react 包目录> src/Alas.UI/Assets/Icons.axaml
```

脚本只读取每个图标的 24×24 路径数据，把 `path`、`circle`、`rect`、`line`、`polyline`、`polygon`
六种基本形状改写为 Avalonia `StreamGeometry` 迷你语言；不缩放、不换锚点、不改线宽。
渲染端 `Alas.UI.Controls.Icon` 以 24×24 坐标系、`stroke-width: 2`、圆头圆角连接绘制，
与上游 lucide 默认参数一致。

## 任务目录生成方式

```powershell
node src/Alas.UI/Assets/Catalog/generate-task-catalog.mjs <上游仓库根> src/Alas.UI/ViewModels/TaskCatalog.cs
```

只搬运 `menu.json` 的分组与任务顺序、以及 `zh-CN.json` 里的 `Menu.<组>.name` / `Task.<任务>.name`，
不改写名称、不重排顺序；生成物文件头写明来源提交。

## 边界

- 上游目录从未被写入；生成所需的依赖副本放在本项目忽略目录 `.runtime/upstream-ui/`。
- 生成物为纯文本资源字典与 C# 目录，可用 `git diff` 审阅。
- 没有复制任何账号、设备、日志或个人路径相关内容。
