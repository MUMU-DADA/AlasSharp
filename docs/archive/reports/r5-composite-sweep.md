# R5 复合原语扫描（C# 原语 vs 上游真实方法）

> 本报告由 `tools/diagnostics/r5_composite_sweep.py` 重建，不手写。
> 覆盖：`clear_enemy` / `clear_any_enemy` / `clear_siren` / `clear_boss` /
> `clear_roadblocks` / `clear_potential_roadblocks` / `clear_first_roadblocks` / `pick_up_ammo`
> 的判定（含路段与弹药语义），
> 含配置分支（优先级、全清、塞壬/要塞、FLEET_2 改排序键）；上游侧跑的是**它自己的方法**。

- 状态数：**20**（seed=13，每种状态 × 8 原语 × 8 配置）
- 用例数：**1280**；实际比较 **1280**
- 不一致：**0**
- 等价位**集合序伪影**：**0** 处（上游 `SelectedGrids.add` 走 `set`，等 weight/cost 的格子谁在前不可复现；两侧都选中同价位格子）
