# R5 不完整钩子普查（事实计数与棘轮）

> 本报告由 `tools/diagnostics/r5_incomplete_hooks.py` 重建，不手写。
> 计数首先来自导出 JSON 的 `plan_complete=false`；源代码形态只用于解释和历史棘轮。
> 本审计不宣称生产战役可由静态计划替代；生产路径仍由上游 `Campaign.run()` 负责。

- 导出钩子条目总数：**3019**
- `plan_complete=false` 条目：**74**
- 其中 `battle_*`：**18**；其它钩子/方法：**56**
- 源体语句数 > 1（解释性统计）：**62**；其中 `battle_*`：**18**
- 历史棘轮基线（`battle_*` 源体语句数 > 1）：**5**；当前值高于基线即失败，不能调高或调低掩盖变化。

## 不完整原因（仅来自导出 `unparsed`）

| 原因前缀 | 次数 |
| --- | ---: |
| `Assign` | 64 |
| `Expr` | 32 |
| `Return(expr)` | 12 |
| `If(cond)` | 8 |
| `variadic method signature requires native execution` | 8 |
| `If(nested)` | 7 |
| `Arguments(battle_function): unsupported argument: f'Enemy remain: {remain}'` | 4 |
| `Arguments(battle_function): unsupported argument: f'Using function: {func}'` | 3 |
| `For` | 2 |
| `Arguments(battle_0): unsupported argument: f'A1.battle_0() did not cleared siren'` | 2 |
| `Arguments(in_sight): unsupported argument: 'In sight: %s' % location2node(location)` | 2 |
| `Arguments(before_boss): unsupported argument: grid` | 2 |
| `Arguments(clear_boss): unsupported argument: 'May boss: %s' % self.map.select(may_boss=True)` | 1 |
| `Arguments(battle_1): unsupported argument: 3 - self.fleet_boss_index` | 1 |
| `Arguments(battle_0): unsupported argument: 3 - self.fleet_boss_index` | 1 |
| `Arguments(battle_5): unsupported argument: f'Unexpected boss grid: {boss}'` | 1 |
| `Arguments(handle_in_stage): unsupported argument: ENTRANCE` | 1 |
| `Arguments(handle_in_stage): unsupported argument: CAMPAIGN_GOTO_DAILY` | 1 |
| `Arguments(battle_0): unsupported argument: self.siren_list.pop()` | 1 |
| `Arguments(battle_0): unsupported argument: self.is_left` | 1 |
| `method transformation requires native execution` | 1 |

## 源体条件形态（解释性统计）

| 条件形态 | 语句体形态 | 次数 |
| --- | --- | ---: |
| self_call | return | 20 |
| boolean_expression | expr | 9 |
| boolean_expression | assign+expr+return | 8 |
| compare | return | 6 |
| other | assign+expr+if | 6 |
| other | return | 5 |
| compare | assign | 5 |
| other | if+return | 3 |
| self_call | assign | 3 |
| other | if | 3 |
| boolean_expression | expr+return | 2 |
| compare | expr+if+return | 2 |
| compare | if | 2 |
| local_name | if | 2 |
| local_name | assign+expr+raise | 1 |
| other | for | 1 |
| other | expr+if+return | 1 |
| other | expr | 1 |
| boolean_expression | assign+if | 1 |
| self_call | expr+if+return | 1 |
| boolean_expression | assign+return | 1 |
| boolean_expression | return | 1 |
| local_name | assign+if | 1 |
| not_self_call | return | 1 |
| self_call | expr+return | 1 |
| local_name | expr | 1 |
| compare | raise | 1 |
| compare | break | 1 |
| other | assign+for | 1 |
| other | expr+return | 1 |
| other | assign | 1 |

## 例子

- `campaign_hard/campaign_hard` `clear_boss`：`if grids` → assign+expr+raise
- `campaign_main/campaign_14_4` `map_data_init`：`if not self.map_is_clear_mode` → for
- `campaign_main/campaign_15_1` `battle_function`：`if self.config.MAP_CLEAR_ALL_THIS_TIME and self.battle_count == 0 a` → assign+expr+return
- `campaign_main/campaign_15_2` `battle_function`：`if self.config.MAP_CLEAR_ALL_THIS_TIME and self.battle_count == 0 a` → assign+expr+return
- `campaign_main/campaign_15_3` `battle_function`：`if not self.config.MAP_CLEAR_ALL_THIS_TIME` → return
- `campaign_main/campaign_15_3` `battle_function`：`if self.battle_count == 3 or (self.battle_count == 0 and (not self.` → assign+expr+return
- `campaign_main/campaign_15_4` `battle_function`：`if not self.config.MAP_CLEAR_ALL_THIS_TIME` → return
- `campaign_main/campaign_15_4` `battle_function`：`if self.battle_count in [3, 6] or (self.battle_count in [0, 1] and ` → assign+expr+return
- `campaign_main/campaign_16_3` `battle_1`：`if self.map_has_mob_move` → expr+if+return
- `campaign_main/campaign_16_3` `battle_1`：`if self.use_support_fleet and (not self.map_is_clear_mode)` → expr
- `campaign_main/campaign_16_3` `battle_1`：`if not self.use_single_fleet` → expr
- `campaign_main/campaign_16_4` `battle_0`：`if self.map_has_mob_move and (not self.use_single_fleet)` → assign+if

## 全部不完整条目

| 模块 | 方法 | 源体语句数（不含 docstring） | 导出原因 |
| --- | --- | ---: | --- |
| `campaign_hard/campaign_hard` | `_expected_end` | 1 | Return(expr)@39: 'in_stage' |
| `campaign_hard/campaign_hard` | `clear_boss` | 9 | Arguments(clear_boss): unsupported argument: 'May boss: %s' % self.map.select(may_boss=True) |
| `campaign_main/campaign_14_4` | `map_data_init` | 2 | Expr@109: super().map_data_init(map_); If(nested)@110: not self.map_is_clear_mode; For@111: for override_grid in OVERRIDE: self.map[override_grid.location].may_en |
| `campaign_main/campaign_15_1` | `battle_function` | 2 | Arguments(battle_function): unsupported argument: f'Using function: {func}' |
| `campaign_main/campaign_15_2` | `battle_function` | 2 | Arguments(battle_function): unsupported argument: f'Using function: {func}' |
| `campaign_main/campaign_15_3` | `battle_function` | 3 | Arguments(battle_function): unsupported argument: f'Using function: {func}' |
| `campaign_main/campaign_15_4` | `battle_function` | 3 | If(cond)@83: self.battle_count in [3, 6] or (self.battle_count in [0, 1] and (not s |
| `campaign_main/campaign_16_3` | `map_init` | 3 | Expr@74: super().map_init(map_); Assign@76: self.use_single_fleet = 'standby' in self.config.Fleet_FleetOrder |
| `campaign_main/campaign_16_3` | `battle_1` | 3 | Arguments(battle_1): unsupported argument: 3 - self.fleet_boss_index |
| `campaign_main/campaign_16_4` | `map_init` | 4 | Expr@80: super().map_init(map_); Assign@83: self.use_single_fleet = 'standby' in self.config.Fleet_FleetOrder |
| `campaign_main/campaign_16_4` | `battle_0` | 2 | Arguments(battle_0): unsupported argument: 3 - self.fleet_boss_index |
| `campaign_main/campaign_16_4` | `battle_1` | 7 | Assign@108: grid = grids.delete(grids.select(enemy_genre='Main')).first_or_none(); If(cond)@109: grid is not None and self.mob_move(F5, F6) |
| `campaign_main/campaign_16_4` | `battle_3` | 6 | If(nested)@133: self.F5_is_moved; If(cond)@134: I6.enemy_genre == 'Main' and self.mob_move(I6, I7) |
| `campaign_main/campaign_7_3` | `battle_5` | 4 | Arguments(battle_5): unsupported argument: f'Unexpected boss grid: {boss}' |
| `campaign_main/campaign_9_2` | `battle_0` | 6 | If(nested)@68: self.fleet_at(D5, fleet=2); Assign@69: self.map.weight_data = '\n 10 10 30 10 10 20 30 40 10\n 10 10 10 10 10; If(nested)@76: self.fleet_at(F4, fleet=2); Assign@77: self.map.weight_data = '\n 10 10 30 10 10 10 10 10 10\n 10 10 20 30 10; If(nested)@84: self.fleet_at(F5, fleet=2); Assign@85: self.map.weight_data = '\n 10 10 30 10 10 10 10 10 10\n 10 10 20 30 10 |
| `event_20200227_cn/c2` | `handle_in_stage` | 1 | Arguments(handle_in_stage): unsupported argument: ENTRANCE |
| `event_20200312_cn/sp3` | `handle_in_stage` | 1 | Arguments(handle_in_stage): unsupported argument: CAMPAIGN_GOTO_DAILY |
| `event_20210121_cn/a2` | `get_map_clear_percentage` | 1 | Return(expr)@72: super().get_map_clear_percentage() * 1.4 |
| `event_20210121_cn/a3` | `get_map_clear_percentage` | 1 | Return(expr)@75: super().get_map_clear_percentage() * 1.4 |
| `event_20210121_cn/c2` | `get_map_clear_percentage` | 1 | Return(expr)@72: super().get_map_clear_percentage() * 1.4 |
| `event_20210121_cn/c3` | `get_map_clear_percentage` | 1 | Return(expr)@75: super().get_map_clear_percentage() * 1.4 |
| `event_20211125_cn/t4` | `catch_camera_repositioning` | 3 | If(cond)@91: super().catch_camera_repositioning(destination); If(cond)@93: not self.map_is_clear_mode and destination.is_fortress |
| `event_20211125_cn/t4` | `map_data_init` | 2 | Assign@102: self.config.MAP_HAS_FORTRESS = True; Expr@103: super().map_data_init(map_) |
| `event_20211125_cn/t4` | `handle_clear_mode_config_cover` | 1 | Assign@107: self.map.fortress_data = [self.map.fortress_data[0], ()] |
| `event_20220915_cn/sp` | `map_data_init` | 5 | Expr@92: super().map_data_init(map_); Assign@93: D4.is_siren = True; Assign@94: D6.is_siren = True; Assign@95: F4.is_siren = True; Assign@96: F6.is_siren = True |
| `event_20230223_cn/sp` | `map_data_init` | 5 | Expr@81: super().map_data_init(map_); Assign@82: D4.is_siren = True; Assign@83: D6.is_siren = True; Assign@84: F4.is_siren = True; Assign@85: F6.is_siren = True |
| `event_20230525_cn/ht3` | `combat_status` | 2 | Expr@89: super().combat_status(*args, **kwargs); variadic method signature requires native execution |
| `event_20230525_cn/ht6` | `combat_status` | 2 | Expr@102: super().combat_status(*args, **kwargs); variadic method signature requires native execution |
| `event_20230525_cn/sp` | `execute_actions` | 2 | For@118: for action in self.action[step]: fleet_index, movement, step, battle = |
| `event_20230525_cn/sp` | `battle_0` | 2 | Arguments(battle_0): unsupported argument: self.siren_list.pop() |
| `event_20230525_cn/t3` | `combat_status` | 2 | Expr@86: super().combat_status(*args, **kwargs); variadic method signature requires native execution |
| `event_20230525_cn/t6` | `combat_status` | 2 | Expr@87: super().combat_status(*args, **kwargs); variadic method signature requires native execution |
| `event_20231026_cn/sp` | `map_data_init` | 4 | Expr@89: super().map_data_init(map_); Assign@90: C2.is_siren = True; Assign@91: D3.is_siren = True; Assign@92: E2.is_siren = True |
| `event_20231221_cn/sp` | `map_data_init` | 4 | Expr@85: super().map_data_init(map_); Assign@86: C2.is_siren = True; Assign@87: E2.is_siren = True; Assign@88: G2.is_siren = True |
| `event_20240425_cn/sp` | `map_data_init` | 5 | Expr@83: super().map_data_init(map_); Assign@84: D4.is_siren = True; Assign@85: D6.is_siren = True; Assign@86: F4.is_siren = True; Assign@87: F6.is_siren = True |
| `event_20240521_cn/a1` | `map_data_init` | 4 | Expr@99: super().map_data_init(map_); Assign@103: self.config.FLEET_BOSS = 1 |
| `event_20240521_cn/a1` | `battle_function` | 2 | Arguments(battle_function): unsupported argument: f'Enemy remain: {remain}' |
| `event_20240521_cn/a1` | `battle_0` | 3 | Arguments(battle_0): unsupported argument: f'A1.battle_0() did not cleared siren' |
| `event_20240521_cn/b3` | `in_sight` | 5 | Arguments(in_sight): unsupported argument: 'In sight: %s' % location2node(location) |
| `event_20240521_cn/c1` | `map_init` | 4 | Expr@99: super().map_init(map_); Assign@103: self.config.FLEET_BOSS = 1 |
| `event_20240521_cn/c1` | `battle_function` | 2 | Arguments(battle_function): unsupported argument: f'Enemy remain: {remain}' |
| `event_20240521_cn/c1` | `battle_0` | 3 | Arguments(battle_0): unsupported argument: f'A1.battle_0() did not cleared siren' |
| `event_20240521_cn/d3` | `in_sight` | 5 | Arguments(in_sight): unsupported argument: 'In sight: %s' % location2node(location) |
| `event_20240521_cn/sp` | `map_data_init` | 4 | Expr@103: super().map_data_init(map_); Assign@104: B7.is_siren = True; Assign@105: C8.is_siren = True; Assign@106: D7.is_siren = True |
| `event_20240521_cn/sp` | `battle_0` | 5 | Arguments(battle_0): unsupported argument: self.is_left |
| `event_20240815_cn/b2` | `before_boss` | 4 | Arguments(before_boss): unsupported argument: grid |
| `event_20240815_cn/b2` | `clear_boss` | 2 | Expr@93: super().clear_boss() |
| `event_20240815_cn/b2` | `brute_clear_boss` | 2 | Expr@97: super().brute_clear_boss() |
| `event_20240815_cn/d2` | `before_boss` | 4 | Arguments(before_boss): unsupported argument: grid |
| `event_20240815_cn/d2` | `clear_boss` | 2 | Expr@102: super().clear_boss() |
| `event_20240815_cn/d2` | `brute_clear_boss` | 2 | Expr@106: super().brute_clear_boss() |
| `event_20240815_cn/sp` | `map_data_init` | 4 | Expr@97: super().map_data_init(map_); Assign@98: E5.is_siren = True; Assign@99: D6.is_siren = True; Assign@100: F6.is_siren = True |
| `event_20241024_cn/sp` | `map_data_init` | 4 | Expr@70: super().map_data_init(map_); Assign@71: I2.is_siren = True; Assign@72: J3.is_siren = True; Assign@73: L3.is_siren = True |
| `event_20241121_cn/sp` | `map_data_init` | 4 | Expr@87: super().map_data_init(map_); Assign@88: D5.is_siren = True; Assign@89: E4.is_siren = True; Assign@90: E6.is_siren = True |
| `event_20250520_cn/b3` | `battle_function` | 3 | Arguments(battle_function): unsupported argument: f'Enemy remain: {remain}' |
| `event_20250520_cn/d3` | `battle_function` | 3 | Arguments(battle_function): unsupported argument: f'Enemy remain: {remain}' |
| `event_20250724_cn/sp` | `_campaign_ocr_result_process` | 3 | Assign@100: result = CampaignBase._campaign_ocr_result_process(result); If(cond)@101: result in ['ysp', 'usp', 'iisp', 'ijsp', 'jjsp']; Return(expr)@103: result; method transformation requires native execution |
| `event_20250912_cn/sp` | `map_data_init` | 12 | Expr@94: super().map_data_init(map_); Assign@96: B4.is_enemy = True; Assign@97: B5.is_enemy = True; Assign@98: C3.is_enemy = True; Assign@99: C6.is_enemy = True; Assign@100: G3.is_enemy = True; Assign@101: G6.is_enemy = True; Assign@102: H4.is_enemy = True; Assign@103: H5.is_enemy = True; Assign@105: D3.is_siren = True; Assign@106: E4.is_siren = True; Assign@107: F3.is_siren = True |
| `event_20251023_cn/sp` | `map_data_init` | 4 | Expr@98: super().map_data_init(map_); Assign@99: F4.is_siren = True; Assign@100: F6.is_siren = True; Assign@101: G5.is_siren = True |
| `event_20260326_cn/sp` | `map_data_init` | 4 | Expr@98: super().map_data_init(map_); Assign@99: C1.is_siren = True; Assign@100: D2.is_siren = True; Assign@101: E1.is_siren = True |
| `event_20260417_cn/sp` | `_expected_end` | 2 | If(nested)@112: self.battle_count == 3; Return(expr)@113: self.event_animation_end |
| `event_20260417_cn/sp3` | `_expected_end` | 2 | If(nested)@89: self.battle_count == 3; Return(expr)@90: self.event_animation_end |
| `event_20260520_cn/b3` | `in_sight` | 4 | Assign@82: location = location_ensure(location); Assign@83: node = location2node(location); If(cond)@84: node == 'E3' |
| `event_20260520_cn/d3` | `in_sight` | 4 | Assign@99: location = location_ensure(location); Assign@100: node = location2node(location); If(cond)@101: node == 'E3' |
| `war_archives_20190911_cn/a2` | `get_map_clear_percentage` | 1 | Return(expr)@72: super().get_map_clear_percentage() * 1.4 |
| `war_archives_20190911_cn/a3` | `get_map_clear_percentage` | 1 | Return(expr)@77: super().get_map_clear_percentage() * 1.4 |
| `war_archives_20190911_cn/c2` | `get_map_clear_percentage` | 1 | Return(expr)@72: super().get_map_clear_percentage() * 1.4 |
| `war_archives_20190911_cn/c3` | `get_map_clear_percentage` | 1 | Return(expr)@75: super().get_map_clear_percentage() * 1.4 |
| `war_archives_20211229_cn/a1` | `handle_clear_mode_config_cover` | 2 | Expr@81: super().handle_clear_mode_config_cover(); Assign@82: self.config.MAP_HAS_MISSILE_ATTACK = False |
| `war_archives_20211229_cn/c1` | `handle_clear_mode_config_cover` | 2 | Expr@81: super().handle_clear_mode_config_cover(); Assign@82: self.config.MAP_HAS_MISSILE_ATTACK = False |
| `war_archives_20230525_cn/ht3` | `combat_status` | 2 | Expr@89: super().combat_status(*args, **kwargs); variadic method signature requires native execution |
| `war_archives_20230525_cn/ht6` | `combat_status` | 2 | Expr@102: super().combat_status(*args, **kwargs); variadic method signature requires native execution |
| `war_archives_20230525_cn/t3` | `combat_status` | 2 | Expr@86: super().combat_status(*args, **kwargs); variadic method signature requires native execution |
| `war_archives_20230525_cn/t6` | `combat_status` | 2 | Expr@87: super().combat_status(*args, **kwargs); variadic method signature requires native execution |

## 口径边界

- 全部条目都保留在事实计数中；单语句、继承方法和非 `battle_*` 条目不会被静默删掉。
- `battle_*` 棘轮只衡量历史上用于计划语言回归的源体语句数；它不等价于全部缺口，也不产生“0 gap/0 blocked”的结论。
- 本报告不按地图名称给出迁移价值判断。每个未解析原因仍需结合上游调用链、设备状态和真实证据处理。
