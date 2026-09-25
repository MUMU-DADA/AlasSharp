# R5 选择分支扫描（C# `SelectGrids` vs 上游 `Map.select_grids`）

> 本报告由 `tools/diagnostics/r5_selectgrids_sweep.py` 重建，不手写。
> 覆盖：`nearby` / `is_accessible` / `scale`（无序 vs 有序）/ `genre`（含大小写不敏感）/
> `strongest` / `weakest` / `sort`（多键稳定排序）；上游侧**真调用** `Map.select_grids`。

- 状态数：**120**（seed=11）
- 用例数：**1680**
- 不一致：**0**
