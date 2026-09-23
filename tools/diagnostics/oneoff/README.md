# oneoff —— 一次性排查脚本（归档）

这些脚本是**定位具体问题时临时写的**，问题解决后它们的价值已经沉淀到别处：

- 结论与数据 → `docs/*.md`（例如 `docs/matching.md`、`docs/primitives.md`、`docs/map-detection.md`）
- 决策过程 → 对应的 git 提交信息

保留它们而不是删除，是为了**结论可复现**：想重跑当年的某个诊断，脚本还在；
但它们**不属于常驻验收**（`verify_all.py` 不引用它们，也没有别的脚本 import 它们 —— 归档前已核查）。

| 前缀 | 数量 | 用途 |
| --- | --- | --- |
| `diag_*` | 10 | 定位匹配/灰度/分母/路径切换等具体算法行为 |
| `probe_*` | 12 | 探测素材、GIF 语义、图像模式与 OS 入口判据等 |
| `drive_*` | 2 | 驱动 cached_property / UI 规则的属性访问 |
| `sweep_*` | 1 | UI 规则扫描 |

**常驻验收在上一层目录**（`tools/diagnostics/*.py`），一键入口是 `verify_all.py`。
