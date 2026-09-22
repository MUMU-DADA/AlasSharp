# S3 计划词表清点（从关卡 IR 统计「要实现哪些引擎调用」）

S3 = 执行上游的**声明式关卡计划**（`campaign.battles[].calls`）。写引擎之前先清点词表，
免得凭感觉排期 —— 源码里的"方法数"不等于**被计划实际调用的集合**。

脚本：`tools/diagnostics/s3_plan_inventory.py`；数据：`data/s3_plan_inventory.json`。

## 总量

| 指标 | 数值 |
| --- | --- |
| 章节数（不含 `*_base` 基类模块） | **1374** |
| tier 分布 | A 1000 / B 212 / C 162 |
| battle_* 方法 | 3019（计划完整 2795 = 92.6%） |
| 引擎钩子（native_overrides） | 61 |
| **调用词表** | **57 个不同名字 / 6421 次出现** |

## 最小实现目标：tier A 用到的调用（9 个）

tier A 有 **1000 章**（占 73%），而它只用到这 9 个调用 —— 先把它们做出来，
就能覆盖近七成章节。其中 tier A **独有**的 %d 个（未在 B/C 出现）是更小的起步集。

| 调用 | 总次数 | 用到的章节数 | A | B | C |
| --- | --- | --- | --- | --- | --- |
| `battle_default` | 1600 | 1309 | 1150 | 275 | 175 |
| `clear_siren` | 1371 | 1160 | 1069 | 206 | 96 |
| `clear_filter_enemy` | 1041 | 823 | 908 | 42 | 91 |
| `fleet_boss.clear_boss` | 715 | 712 | 513 | 108 | 94 |
| `clear_boss` | 575 | 575 | 487 | 59 | 29 |
| `fleet_boss.goto` | 7 | 5 | 4 | 0 | 3 |
| `before_boss` | 4 | 2 | 4 | 0 | 0 |
| `focus_to` | 2 | 2 | 2 | 0 | 0 |
| `image_color_count` | 1 | 1 | 1 | 0 | 0 |

## 完整词表（57 个）

| 调用 | 总次数 | 章节数 | A | B | C | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| `battle_default` | 1600 | 1309 | 1150 | 275 | 175 |  |
| `clear_siren` | 1371 | 1160 | 1069 | 206 | 96 |  |
| `clear_filter_enemy` | 1041 | 823 | 908 | 42 | 91 |  |
| `fleet_boss.clear_boss` | 715 | 712 | 513 | 108 | 94 |  |
| `clear_boss` | 575 | 575 | 487 | 59 | 29 |  |
| `clear_enemy` | 417 | 120 | 0 | 395 | 22 |  |
| `clear_roadblocks` | 88 | 52 | 0 | 24 | 64 |  |
| `clear_all_mystery` | 57 | 33 | 0 | 17 | 40 |  |
| `clear_any_enemy` | 56 | 47 | 0 | 8 | 48 |  |
| `map.select` | 53 | 39 | 0 | 0 | 53 | 仅 tier C |
| `clear_chosen_enemy` | 49 | 12 | 0 | 0 | 49 | 仅 tier C |
| `clear_potential_roadblocks` | 45 | 39 | 0 | 12 | 33 |  |
| `fleet_2_push_forward` | 43 | 39 | 0 | 13 | 30 |  |
| `check_accessibility` | 39 | 39 | 0 | 0 | 39 | 仅 tier C |
| `fleet_2_protect` | 31 | 27 | 0 | 26 | 5 |  |
| `goto` | 24 | 10 | 0 | 0 | 24 | 仅 tier C |
| `pick_up_ammo` | 15 | 10 | 0 | 5 | 10 |  |
| `fleet_2_step_on` | 14 | 13 | 0 | 2 | 12 |  |
| `mob_move` | 13 | 7 | 0 | 0 | 13 | 仅 tier C |
| `fleet_1.clear_boss` | 13 | 13 | 0 | 13 | 0 |  |
| `fleet_at` | 12 | 4 | 0 | 0 | 12 | 仅 tier C |
| `fleet_boss.capture_clear_boss` | 12 | 12 | 0 | 12 | 0 |  |
| `clear_bouncing_enemy` | 12 | 12 | 0 | 12 | 0 |  |
| `brute_clear_boss` | 11 | 11 | 0 | 10 | 1 |  |
| `pick_up_light_house` | 10 | 4 | 0 | 10 | 0 |  |
| `clear_mechanism` | 10 | 10 | 0 | 4 | 6 |  |
| `fleet_boss.brute_clear_boss` | 9 | 9 | 0 | 9 | 0 |  |
| `device.disable_stuck_detection` | 8 | 8 | 0 | 0 | 8 | 仅 tier C |
| `fleet_boss.goto` | 7 | 5 | 4 | 0 | 3 |  |
| `__getattribute__` | 7 | 5 | 0 | 0 | 7 | 仅 tier C |
| `fleet_boss.pick_up_flare` | 6 | 5 | 0 | 4 | 2 |  |
| `clear_map_items` | 5 | 5 | 0 | 5 | 0 |  |
| `pick_up_flare` | 4 | 2 | 0 | 4 | 0 |  |
| `fleet_1.switch_to` | 4 | 4 | 0 | 1 | 3 |  |
| `fleet_2_rescue` | 4 | 4 | 0 | 0 | 4 | 仅 tier C |
| `battle_0` | 4 | 4 | 0 | 2 | 2 |  |
| `before_boss` | 4 | 2 | 4 | 0 | 0 | 仅 tier A |
| `air_strike` | 3 | 2 | 0 | 0 | 3 | 仅 tier C |
| `clear_first_roadblocks` | 3 | 3 | 0 | 2 | 1 |  |
| `appear` | 3 | 2 | 0 | 3 | 0 |  |
| `fleet_boss.clear_chosen_enemy` | 2 | 2 | 0 | 0 | 2 | 仅 tier C |
| `fleet_ensure` | 2 | 2 | 0 | 0 | 2 | 仅 tier C |
| `fleet_2.clear_chosen_mystery` | 2 | 1 | 0 | 0 | 2 | 仅 tier C |
| `ensure_no_info_bar` | 2 | 2 | 0 | 2 | 0 |  |
| `bored_visit` | 2 | 2 | 0 | 0 | 2 | 仅 tier C |
| `battle_boss` | 2 | 2 | 0 | 0 | 2 | 仅 tier C |
| `focus_to` | 2 | 2 | 2 | 0 | 0 | 仅 tier A |
| `clear_potential_boss` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `_goto` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `fleet_boss.battle_default` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `fleet_2.switch_to` | 1 | 1 | 0 | 1 | 0 |  |
| `fleet_boss.clear_potential_boss` | 1 | 1 | 0 | 1 | 0 |  |
| `device.sleep` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `execute_actions` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `fleet_1.clear_chosen_enemy` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `siren_list.pop` | 1 | 1 | 0 | 0 | 1 | 仅 tier C |
| `image_color_count` | 1 | 1 | 1 | 0 | 0 | 仅 tier A |

## tier C 独有（22 个）——实施顺序上排最后

`map.select` `clear_chosen_enemy` `check_accessibility` `goto` `mob_move` `fleet_at` `device.disable_stuck_detection` `__getattribute__` `fleet_2_rescue` `air_strike` `fleet_boss.clear_chosen_enemy` `battle_boss` `fleet_ensure` `bored_visit` `fleet_2.clear_chosen_mystery` `device.sleep` `clear_potential_boss` `_goto` `siren_list.pop` `fleet_boss.battle_default` `fleet_1.clear_chosen_enemy` `execute_actions`

## 建议的实施顺序（数据驱动，不是拍脑袋）

1. **tier A 的 9 个调用** → 解锁 1000 章（73%）；
2. **tier B 追加的调用**（A 与 B 的差集）→ 再解锁 212 章；
3. **tier C 的 22 个**（含 `map.select`/`goto`/`mob_move` 这类细粒度控制）→ 最后 162 章。

引擎钩子（%d 个）与 tier C 的细粒度调用是同一类工作：需要接近 ALAS 运行时的能力，排期时按"每章一次性验证"而不是"每个调用一次性实现"来估。
