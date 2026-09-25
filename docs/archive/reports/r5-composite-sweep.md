# R5 复合原语扫描（C# 原语 vs 上游真实方法）

> 本报告由 `tools/diagnostics/r5_composite_sweep.py` 重建，不手写。
> 覆盖：`clear_enemy` / `clear_any_enemy` / `clear_siren` / `clear_boss` /
> `clear_roadblocks` / `clear_potential_roadblocks` / `clear_first_roadblocks` / `pick_up_ammo`
> 的判定（含路段、弹药与 2 队推进/护航语义），
> 含配置分支（优先级、全清、塞壬/要塞、FLEET_2 改排序键）；上游侧跑的是**它自己的方法**。

- 状态数：**20**（seed=13，每种状态 × 10 原语 × 10 配置）
- 用例数：**2000**；实际比较 **1991**
- 不一致：**0**
- **动作序列对拍**：比较 1991 条，完全相同 1967；干跑前缀（有意偏离）4 条；集合序伪影（只在 `submarine_move_near_boss` 实参上不同）20 条；其余顺序差异 0 条
- 等价位**集合序伪影**：**0** 处（上游 `SelectedGrids.add` 走 `set`，等 weight/cost 的格子谁在前不可复现；两侧都选中同价位格子）

## 跳过（如实列出原因）

| 原因 | 次数 |
| --- | --- |
| 上游执行失败：IndexError: list index out of range | 9 |

### 例子（每类最多 3 条）

- **上游执行失败：IndexError: list index out of range**
  - `state5-fleet_2_protect-c8`
  - `state10-clear_boss-c8`
  - `state10-clear_boss-c9`
