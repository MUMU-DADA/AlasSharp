# R5 复合原语扫描（C# 原语 vs 上游真实方法）

> 本报告由 `tools/diagnostics/r5_composite_sweep.py` 重建，不手写。
> 覆盖：`clear_enemy` / `clear_any_enemy` / `clear_siren` / `clear_boss` /
> `clear_roadblocks` / `clear_potential_roadblocks` / `clear_first_roadblocks` / `pick_up_ammo`
> 的判定（含路段、弹药与 2 队推进/护航语义），
> 含配置分支（优先级、全清、塞壬/要塞、FLEET_2 改排序键）；上游侧跑的是**它自己的方法**。

- 状态数：**20**（seed=13，每种状态 × 15 原语 × 12 配置）
- 用例数：**3600**；实际比较 **3581**
- 不一致：**0**
- **动作序列对拍**：比较 3581 条，完全相同 3505；干跑前缀（有意偏离）4 条；集合序伪影（只在 `submarine_move_near_boss` 实参上不同）44 条；其余顺序差异 28 条
- 等价位**集合序伪影**：**6** 处（上游 `SelectedGrids.add` 走 `set`，等 weight/cost 的格子谁在前不可复现；两侧都选中同价位格子）
- 本次未跑的原语：fleet_2_rescue
- **未纳入的已知差异**：无（路障**子集**选择不同：C# 寻路定点收敛会找到更小的可达子集；登记在重写文档的差异一节）

## 顺序差异（前 10 条）

性质：**切舰队的记录时机**不同。上游 `fleet_1/2/boss` 属性在访问时就 `fleet_ensure(index)`，
C# 侧是显式 `EnsureFleet`，两条代码路径的调用点不一一对应；比的是同一批 clear/goto/submarine 动作，
只是多/少一条 `ensure_fleet`。**不记为不一致**（属诊断记录口径），但要看得见。

| 用例 | 种类 | 序列 |
| --- | --- | --- |
| state0-brute_clear_boss-c8 | primitive_brute_clear_boss | C# [('ensure_fleet', '2'), ('submarine_move_near_boss', 'A2'), ('clear_chosen_enemy', 'A2'), ('clear_chosen_enemy', 'B1')] vs 上游 [('submarine_move_near_boss', 'A2'), ('clear_chosen_enemy', 'A2'), ('clear_chosen_enemy', 'B1')] |
| state0-brute_clear_boss-c9 | primitive_brute_clear_boss | C# [('ensure_fleet', '2'), ('submarine_move_near_boss', 'A2'), ('clear_chosen_enemy', 'A2'), ('clear_chosen_enemy', 'B1')] vs 上游 [('submarine_move_near_boss', 'A2'), ('clear_chosen_enemy', 'A2'), ('clear_chosen_enemy', 'B1')] |
| state2-brute_clear_boss-c8 | primitive_brute_clear_boss | C# [('ensure_fleet', '2'), ('submarine_move_near_boss', 'A1'), ('clear_chosen_enemy', 'A1'), ('clear_chosen_enemy', 'B2')] vs 上游 [('submarine_move_near_boss', 'B3'), ('clear_chosen_enemy', 'A1'), ('clear_chosen_enemy', 'B2')] |
| state2-brute_clear_boss-c9 | primitive_brute_clear_boss | C# [('ensure_fleet', '2'), ('submarine_move_near_boss', 'A1'), ('clear_chosen_enemy', 'A1'), ('clear_chosen_enemy', 'B2')] vs 上游 [('submarine_move_near_boss', 'B3'), ('clear_chosen_enemy', 'A1'), ('clear_chosen_enemy', 'B2')] |
| state2-clear_filter_enemy-c9 | primitive_clear_filter_enemy | C# [('clear_chosen_enemy', 'B2')] vs 上游 [('clear_chosen_enemy', 'A1')] |
| state2-clear_filter_enemy-c11 | primitive_clear_filter_enemy | C# [('clear_chosen_enemy', 'B2')] vs 上游 [('clear_chosen_enemy', 'A1')] |
| state3-brute_clear_boss-c8 | primitive_brute_clear_boss | C# [('ensure_fleet', '2'), ('submarine_move_near_boss', 'B2'), ('clear_chosen_enemy', 'B2'), ('clear_chosen_enemy', 'A2')] vs 上游 [('submarine_move_near_boss', 'B2'), ('clear_chosen_enemy', 'B2'), ('clear_chosen_enemy', 'A2')] |
| state3-brute_clear_boss-c9 | primitive_brute_clear_boss | C# [('ensure_fleet', '2'), ('submarine_move_near_boss', 'B2'), ('clear_chosen_enemy', 'B2'), ('clear_chosen_enemy', 'A2')] vs 上游 [('submarine_move_near_boss', 'B2'), ('clear_chosen_enemy', 'B2'), ('clear_chosen_enemy', 'A2')] |
| state4-brute_clear_boss-c8 | primitive_brute_clear_boss | C# [('ensure_fleet', '2'), ('submarine_move_near_boss', 'D1'), ('clear_chosen_enemy', 'D1'), ('clear_chosen_enemy', 'C2')] vs 上游 [('submarine_move_near_boss', 'A1'), ('clear_chosen_enemy', 'D1'), ('clear_chosen_enemy', 'C2')] |
| state4-brute_clear_boss-c9 | primitive_brute_clear_boss | C# [('ensure_fleet', '2'), ('submarine_move_near_boss', 'D1'), ('clear_chosen_enemy', 'D1'), ('clear_chosen_enemy', 'C2')] vs 上游 [('submarine_move_near_boss', 'A1'), ('clear_chosen_enemy', 'D1'), ('clear_chosen_enemy', 'C2')] |

## 集合序伪影（前 10 条，信息项）

| 用例 | C# | 上游 |
| --- | --- | --- |
| state2-clear_filter_enemy-c9 | `B2` | `A1` |
| state2-clear_filter_enemy-c11 | `B2` | `A1` |
| state7-clear_filter_enemy-c9 | `B2` | `A1` |
| state7-clear_filter_enemy-c11 | `B2` | `A1` |
| state18-clear_filter_enemy-c9 | `A1` | `C1` |
| state18-clear_filter_enemy-c11 | `A1` | `C1` |

## 跳过（如实列出原因）

| 原因 | 次数 |
| --- | --- |
| 上游执行失败：IndexError: list index out of range | 19 |

### 例子（每类最多 3 条）

- **上游执行失败：IndexError: list index out of range**
  - `state5-fleet_2_protect-c8`
  - `state10-clear_boss-c8`
  - `state10-clear_boss-c9`
