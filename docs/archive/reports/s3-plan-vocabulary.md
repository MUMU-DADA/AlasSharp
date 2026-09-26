# S3 导出调用词表（离线统计）

统计 `campaign.battles[].calls` 中可导出的调用，仅用于覆盖调查。
生产流程执行原生 `Campaign.run()`；IR 分级与调用频次不证明可运行、通关或迁移优先级。

脚本：`tools/diagnostics/s3_plan_inventory.py`；数据：`data/s3_plan_inventory.json`。

## 总量

| 指标 | 数值 |
| --- | --- |
| 章节数（不含 `*_base` 基类模块） | **1374** |
| tier 分布 | A 1006 / B 317 / C 51 |
| battle_* 方法 | 3019（计划完整 2945 = 97.5%） |
| 引擎钩子（native_overrides） | 56 |
| **调用词表** | **57 个不同名字 / 6421 次出现** |

## tier A 用到的调用（10 个）

同一方法可能多次调用；出现次数与章节数量分别统计。

| 调用 | 总次数 | 用到的章节数 | A | B | C |
| --- | --- | --- | --- | --- | --- |
| `battle_default` | 1600 | 1309 | 1157 | 409 | 34 |
| `clear_siren` | 1371 | 1160 | 1079 | 283 | 9 |
| `clear_filter_enemy` | 1041 | 823 | 911 | 102 | 28 |
| `fleet_boss.clear_boss` | 715 | 712 | 517 | 185 | 13 |
| `clear_boss` | 575 | 575 | 489 | 84 | 2 |
| `fleet_boss.goto` | 7 | 5 | 4 | 1 | 2 |
| `before_boss` | 4 | 2 | 4 | 0 | 0 |
| `focus_to` | 2 | 2 | 2 | 0 | 0 |
| `device.sleep` | 1 | 1 | 1 | 0 | 0 |
| `image_color_count` | 1 | 1 | 1 | 0 | 0 |

## 完整词表（57 个）

| 调用 | 总次数 | 章节数 | A | B | C | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| `battle_default` | 1600 | 1309 | 1157 | 409 | 34 |  |
| `clear_siren` | 1371 | 1160 | 1079 | 283 | 9 |  |
| `clear_filter_enemy` | 1041 | 823 | 911 | 102 | 28 |  |
| `fleet_boss.clear_boss` | 715 | 712 | 517 | 185 | 13 |  |
| `clear_boss` | 575 | 575 | 489 | 84 | 2 |  |
| `clear_enemy` | 417 | 120 | 0 | 417 | 0 |  |
| `clear_roadblocks` | 88 | 52 | 0 | 80 | 8 |  |
| `clear_all_mystery` | 57 | 33 | 0 | 55 | 2 |  |
| `clear_any_enemy` | 56 | 47 | 0 | 51 | 5 |  |
| `map.select` | 53 | 39 | 0 | 30 | 23 |  |
| `clear_chosen_enemy` | 49 | 12 | 0 | 16 | 33 |  |
| `clear_potential_roadblocks` | 45 | 39 | 0 | 42 | 3 |  |
| `fleet_2_push_forward` | 43 | 39 | 0 | 43 | 0 |  |
| `check_accessibility` | 39 | 39 | 0 | 35 | 4 |  |
| `fleet_2_protect` | 31 | 27 | 0 | 27 | 4 |  |
| `goto` | 24 | 10 | 0 | 18 | 6 |  |
| `pick_up_ammo` | 15 | 10 | 0 | 13 | 2 |  |
| `fleet_2_step_on` | 14 | 13 | 0 | 13 | 1 |  |
| `mob_move` | 13 | 7 | 0 | 1 | 12 |  |
| `fleet_1.clear_boss` | 13 | 13 | 0 | 13 | 0 |  |
| `fleet_at` | 12 | 4 | 0 | 9 | 3 |  |
| `fleet_boss.capture_clear_boss` | 12 | 12 | 0 | 12 | 0 |  |
| `clear_bouncing_enemy` | 12 | 12 | 0 | 12 | 0 |  |
| `brute_clear_boss` | 11 | 11 | 0 | 11 | 0 |  |
| `pick_up_light_house` | 10 | 4 | 0 | 10 | 0 |  |
| `clear_mechanism` | 10 | 10 | 0 | 8 | 2 |  |
| `fleet_boss.brute_clear_boss` | 9 | 9 | 0 | 9 | 0 |  |
| `device.disable_stuck_detection` | 8 | 8 | 0 | 8 | 0 |  |
| `fleet_boss.goto` | 7 | 5 | 4 | 1 | 2 |  |
| `__getattribute__` | 7 | 5 | 0 | 0 | 7 | 仅 tier C |
| `fleet_boss.pick_up_flare` | 6 | 5 | 0 | 6 | 0 |  |
| `clear_map_items` | 5 | 5 | 0 | 5 | 0 |  |
| `pick_up_flare` | 4 | 2 | 0 | 4 | 0 |  |
| `fleet_1.switch_to` | 4 | 4 | 0 | 3 | 1 |  |
| `fleet_2_rescue` | 4 | 4 | 0 | 4 | 0 |  |
| `battle_0` | 4 | 4 | 0 | 2 | 2 |  |
| `before_boss` | 4 | 2 | 4 | 0 | 0 | 仅 tier A |
| `air_strike` | 3 | 2 | 0 | 0 | 3 | 仅 tier C |
| `clear_first_roadblocks` | 3 | 3 | 0 | 3 | 0 |  |
| `appear` | 3 | 2 | 0 | 3 | 0 |  |
| `fleet_boss.clear_chosen_enemy` | 2 | 2 | 0 | 1 | 1 |  |
| `fleet_ensure` | 2 | 2 | 0 | 0 | 2 | 仅 tier C |
| `fleet_2.clear_chosen_mystery` | 2 | 1 | 0 | 2 | 0 |  |
| `ensure_no_info_bar` | 2 | 2 | 0 | 2 | 0 |  |
| `bored_visit` | 2 | 2 | 0 | 0 | 2 | 仅 tier C |
| `battle_boss` | 2 | 2 | 0 | 0 | 2 | 仅 tier C |
| `focus_to` | 2 | 2 | 2 | 0 | 0 | 仅 tier A |
| `clear_potential_boss` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `_goto` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `fleet_boss.battle_default` | 1 | 1 | 0 | 1 | 0 |  |
| `fleet_2.switch_to` | 1 | 1 | 0 | 1 | 0 |  |
| `fleet_boss.clear_potential_boss` | 1 | 1 | 0 | 1 | 0 |  |
| `device.sleep` | 1 | 1 | 1 | 0 | 0 | 仅 tier A |
| `execute_actions` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `fleet_1.clear_chosen_enemy` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `siren_list.pop` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `image_color_count` | 1 | 1 | 1 | 0 | 0 | 仅 tier A |

## tier C 独有调用（10 个）

`__getattribute__` `air_strike` `battle_boss` `bored_visit` `fleet_ensure` `_goto` `clear_potential_boss` `execute_actions` `fleet_1.clear_chosen_enemy` `siren_list.pop`

迁移边界见[当前路线](../../architecture-roadmap.md)，不能按此词表重放战役或添加地图特例。
