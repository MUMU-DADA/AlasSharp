# R5 复合原语扫描（C# 原语 vs 上游真实方法）

> 本报告由 `tools/diagnostics/r5_composite_sweep.py` 重建，不手写。
> 覆盖：`clear_enemy` / `clear_any_enemy` / `clear_siren` / `clear_boss` 的判定，
> 含配置分支（优先级、全清、塞壬/要塞、FLEET_2 改排序键）；上游侧跑的是**它自己的方法**。

- 状态数：**20**（seed=13，每种状态 × 4 原语 × 8 配置）
- 用例数：**640**；实际比较 **624**
- 不一致：**0**
- 等价位**集合序伪影**：**2** 处（上游 `SelectedGrids.add` 走 `set`，等 weight/cost 的格子谁在前不可复现；两侧都选中同价位格子）

## 集合序伪影（前 10 条，信息项）

| 用例 | C# | 上游 |
| --- | --- | --- |
| state10-clear_any_enemy-c6 | `B1` | `A2` |
| state10-clear_any_enemy-c7 | `B1` | `A2` |

## 跳过（如实列出原因）

| 原因 | 次数 |
| --- | --- |
| 上游执行失败：AttributeError: 'NoneType' object has no attribute 'sort' | 16 |
