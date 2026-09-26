# R5 状态写入审计（源文本事实）

> 本报告由 `tools/diagnostics/r5_state_mutation_audit.py` 重建，不手写。
> 只解析上游源文本和 C# 注册表；不导入生产模块、不执行游戏动作。
> 生产战役仍由上游 `Campaign.run()` 执行，静态导出计划仅用于离线展示、溯源和漂移校验。

扫描 module/map、module/campaign/campaign_base.py 和 campaign 的同名方法定义，保留所有候选。
这不是运行时 MRO 解析或调用图；`.sort`/`.update` 等只表示潜在原地修改，接收对象可能是局部副本。
未扫描到直接写入不表示无副作用；C# 同步、执行顺序及设备结果须由专项对照证明。

- 注册表原语：**34** 个
- 源文本待定位或状态写入待核对：**39**

| 原语 | 源文本命中 | 片段 | 审计结论 |
| --- | --- | --- | --- |
| `battle_boss` | 源文本未发现直接写入（module/campaign/campaign_base.py:CampaignBase:battle_boss） |  | 仍需核对被调用方法的副作用 |
| `battle_default` | 源文本未发现直接写入（module/campaign/campaign_base.py:CampaignBase:battle_default） |  | 仍需核对被调用方法的副作用 |
| `brute_clear_boss` | 源文本未发现直接写入（campaign/event_20240815_cn/b2.py:Campaign:brute_clear_boss） |  | 仍需核对被调用方法的副作用 |
| `brute_clear_boss` | 源文本未发现直接写入（campaign/event_20240815_cn/d2.py:Campaign:brute_clear_boss） |  | 仍需核对被调用方法的副作用 |
| `brute_clear_boss` | 潜在原地修改 .sort（module/map/map.py:Map:brute_clear_boss） | `L423: grids.sort('weight', 'cost')` | 待确认：静态命中不证明 C# 已同步 |
| `brute_fleet_meet` | 潜在原地修改 .sort（module/map/map.py:Map:brute_fleet_meet） | `L446: grids.sort('weight', 'cost')` | 待确认：静态命中不证明 C# 已同步 |
| `capture_clear_boss` | 潜在原地修改 .sort（module/map/map.py:Map:capture_clear_boss） | `L366: grids.sort('weight', 'cost')` | 待确认：静态命中不证明 C# 已同步 |
| `check_accessibility` | 属性赋值（module/map/fleet.py:Fleet:check_accessibility） | `L996: self.fleet_current_index = fleet` | 待确认：静态命中不证明 C# 已同步 |
| `check_accessibility` | 属性赋值（module/map/fleet.py:Fleet:check_accessibility） | `L1000: self.fleet_current_index = backup` | 待确认：静态命中不证明 C# 已同步 |
| `clear_all_mystery` | 容器下标赋值（module/map/map.py:Map:clear_all_mystery） | `L177: kwargs['sort'] = ('cost',)` | 待确认：静态命中不证明 C# 已同步 |
| `clear_any_enemy` | 源文本未发现直接写入（module/map/map.py:Map:clear_any_enemy） |  | 仍需核对被调用方法的副作用 |
| `clear_boss` | 潜在原地修改 .sort（campaign/campaign_hard/campaign_hard.py:Campaign:clear_boss） | `L50: grids.sort('weight', 'cost')` | 待确认：静态命中不证明 C# 已同步 |
| `clear_boss` | 源文本未发现直接写入（campaign/event_20240815_cn/b2.py:Campaign:clear_boss） |  | 仍需核对被调用方法的副作用 |
| `clear_boss` | 源文本未发现直接写入（campaign/event_20240815_cn/d2.py:Campaign:clear_boss） |  | 仍需核对被调用方法的副作用 |
| `clear_boss` | 潜在原地修改 .sort（module/map/map.py:Map:clear_boss） | `L339: grids.sort('weight', 'cost')` | 待确认：静态命中不证明 C# 已同步 |
| `clear_bouncing_enemy` | 潜在原地修改 .set（module/map/map.py:Map:clear_bouncing_enemy） | `L737: route.select(may_bouncing_enemy=True).set(may_bouncing_enemy=False)` | 待确认：静态命中不证明 C# 已同步 |
| `clear_chosen_enemy` | 设备侧（本审计不判定 C# 镜像） |  | 需由上游生产路径与设备证据核对 |
| `clear_enemy` | 容器下标赋值（module/map/map.py:Map:clear_enemy） | `L201: kwargs['strongest'] = True` | 待确认：静态命中不证明 C# 已同步 |
| `clear_enemy` | 容器下标赋值（module/map/map.py:Map:clear_enemy） | `L203: kwargs['weakest'] = True` | 待确认：静态命中不证明 C# 已同步 |
| `clear_enemy` | 容器下标赋值（module/map/map.py:Map:clear_enemy） | `L205: kwargs['strongest'] = True` | 待确认：静态命中不证明 C# 已同步 |
| `clear_filter_enemy` | 潜在原地修改 .sort（module/map/map.py:Map:clear_filter_enemy） | `L692: grids.sort('weight', 'cost')` | 待确认：静态命中不证明 C# 已同步 |
| `clear_first_roadblocks` | 源文本未发现直接写入（module/map/map.py:Map:clear_first_roadblocks） |  | 仍需核对被调用方法的副作用 |
| `clear_map_items` | 潜在原地修改 .sort（campaign/event_20221124_cn/campaign_base.py:CampaignBase:clear_map_items） | `L109: SelectedGrids(grids).sort('cost')` | 待确认：静态命中不证明 C# 已同步 |
| `clear_mechanism` | 源文本未发现直接写入（module/map/map.py:Map:clear_mechanism） |  | 仍需核对被调用方法的副作用 |
| `clear_potential_boss` | 潜在原地修改 .sort（module/map/map.py:Map:clear_potential_boss） | `L377: self.map.select(may_boss=True, is_accessible=True).sort('weight', 'cost')` | 待确认：静态命中不证明 C# 已同步 |
| `clear_potential_boss` | 潜在原地修改 .sort（module/map/map.py:Map:clear_potential_boss） | `L388: grids.sort('weight', 'cost')` | 待确认：静态命中不证明 C# 已同步 |
| `clear_potential_boss` | 潜在原地修改 .sort（module/map/map.py:Map:clear_potential_boss） | `L397: self.map.select(may_boss=True, is_accessible=False).sort('weight', 'cost')` | 待确认：静态命中不证明 C# 已同步 |
| `clear_potential_boss` | 潜在原地修改 .sort（module/map/map.py:Map:clear_potential_boss） | `L403: roadblocks.sort('weight', 'cost')` | 待确认：静态命中不证明 C# 已同步 |
| `clear_potential_roadblocks` | 容器下标赋值（module/map/map.py:Map:clear_potential_roadblocks） | `L261: kwargs['strongest'] = True` | 待确认：静态命中不证明 C# 已同步 |
| `clear_potential_roadblocks` | 容器下标赋值（module/map/map.py:Map:clear_potential_roadblocks） | `L263: kwargs['weakest'] = True` | 待确认：静态命中不证明 C# 已同步 |
| `clear_potential_roadblocks` | 容器下标赋值（module/map/map.py:Map:clear_potential_roadblocks） | `L265: kwargs['strongest'] = True` | 待确认：静态命中不证明 C# 已同步 |
| `clear_roadblocks` | 容器下标赋值（module/map/map.py:Map:clear_roadblocks） | `L231: kwargs['strongest'] = True` | 待确认：静态命中不证明 C# 已同步 |
| `clear_roadblocks` | 容器下标赋值（module/map/map.py:Map:clear_roadblocks） | `L233: kwargs['weakest'] = True` | 待确认：静态命中不证明 C# 已同步 |
| `clear_roadblocks` | 容器下标赋值（module/map/map.py:Map:clear_roadblocks） | `L235: kwargs['strongest'] = True` | 待确认：静态命中不证明 C# 已同步 |
| `clear_siren` | 容器下标赋值（module/map/map.py:Map:clear_siren） | `L462: kwargs['sort'] = ('weight', 'cost_2')` | 待确认：静态命中不证明 C# 已同步 |
| `ensure_fleet` | 属性赋值（module/map/fleet.py:Fleet:fleet_ensure） | `L98: self.camera = self.fleet_current` | 待确认：静态命中不证明 C# 已同步 |
| `ensure_fleet` | 潜在原地修改 .update（module/map/fleet.py:Fleet:fleet_ensure） | `L99: self.update()` | 待确认：静态命中不证明 C# 已同步 |
| `fleet_2_break_siren_caught` | 属性赋值（module/map/map.py:Map:fleet_2_break_siren_caught） | `L557: grid.is_caught_by_siren = False` | 待确认：静态命中不证明 C# 已同步 |
| `fleet_2_break_siren_caught` | 属性赋值（module/map/map.py:Map:fleet_2_break_siren_caught） | `L566: grid.is_caught_by_siren = False` | 待确认：静态命中不证明 C# 已同步 |
| `fleet_2_protect` | 源文本未发现直接写入（module/map/map.py:Map:fleet_2_protect） |  | 仍需核对被调用方法的副作用 |
| `fleet_2_push_forward` | 潜在原地修改 .sort（module/map/map.py:Map:fleet_2_push_forward） | `L585: self.map.select(is_land=False).sort('weight', 'cost')` | 待确认：静态命中不证明 C# 已同步 |
| `fleet_2_rescue` | 源文本未发现直接写入（module/map/map.py:Map:fleet_2_rescue） |  | 仍需核对被调用方法的副作用 |
| `fleet_2_step_on` | 源文本未发现直接写入（module/map/map.py:Map:fleet_2_step_on） |  | 仍需核对被调用方法的副作用 |
| `fleet_at` | 源文本未发现直接写入（module/map/fleet.py:Fleet:fleet_at） |  | 仍需核对被调用方法的副作用 |
| `fleet_ensure` | 属性赋值（module/map/fleet.py:Fleet:fleet_ensure） | `L98: self.camera = self.fleet_current` | 待确认：静态命中不证明 C# 已同步 |
| `fleet_ensure` | 潜在原地修改 .update（module/map/fleet.py:Fleet:fleet_ensure） | `L99: self.update()` | 待确认：静态命中不证明 C# 已同步 |
| `goto` | 设备侧（本审计不判定 C# 镜像） |  | 需由上游生产路径与设备证据核对 |
| `handle_boss_appear_refocus` | 源文本未发现直接写入（campaign/campaign_main/campaign_11_2.py:Campaign:handle_boss_appear_refocus） |  | 仍需核对被调用方法的副作用 |
| `handle_boss_appear_refocus` | 源文本未发现直接写入（campaign/campaign_main/campaign_1_1.py:Campaign:handle_boss_appear_refocus） |  | 仍需核对被调用方法的副作用 |
| `handle_boss_appear_refocus` | 源文本未发现直接写入（campaign/campaign_main/campaign_2_1.py:Campaign:handle_boss_appear_refocus） |  | 仍需核对被调用方法的副作用 |
| `handle_boss_appear_refocus` | 源文本未发现直接写入（campaign/campaign_main/campaign_7_1.py:Campaign:handle_boss_appear_refocus） |  | 仍需核对被调用方法的副作用 |
| `handle_boss_appear_refocus` | 源文本未发现直接写入（campaign/event_20200521_en/a1.py:Campaign:handle_boss_appear_refocus） |  | 仍需核对被调用方法的副作用 |
| `handle_boss_appear_refocus` | 源文本未发现直接写入（campaign/event_20231221_cn/a3.py:Campaign:handle_boss_appear_refocus） |  | 仍需核对被调用方法的副作用 |
| `handle_boss_appear_refocus` | 源文本未发现直接写入（campaign/event_20231221_cn/c3.py:Campaign:handle_boss_appear_refocus） |  | 仍需核对被调用方法的副作用 |
| `handle_boss_appear_refocus` | 潜在原地修改 .update（module/map/fleet.py:Fleet:handle_boss_appear_refocus） | `L1085: self.update()` | 待确认：静态命中不证明 C# 已同步 |
| `handle_boss_appear_refocus` | 潜在原地修改 .update（module/map/fleet.py:Fleet:handle_boss_appear_refocus） | `L1092: self.update()` | 待确认：静态命中不证明 C# 已同步 |
| `pick_up_ammo` | 增量赋值（module/map/map.py:Map:pick_up_ammo） | `L71: self.ammo_count -= recover` | 待确认：静态命中不证明 C# 已同步 |
| `pick_up_ammo` | 增量赋值（module/map/map.py:Map:pick_up_ammo） | `L72: self.fleet_ammo += recover` | 待确认：静态命中不证明 C# 已同步 |
| `pick_up_flare` | 属性赋值（campaign/campaign_main/campaign_14_base.py:CampaignBase:pick_up_flare） | `L43: grid.is_flare = True` | 待确认：静态命中不证明 C# 已同步 |
| `pick_up_flare` | 潜在原地修改 .append（campaign/campaign_main/campaign_14_base.py:CampaignBase:pick_up_flare） | `L50: self.picked_flare.append(grid)` | 待确认：静态命中不证明 C# 已同步 |
| `pick_up_light_house` | 潜在原地修改 .append（campaign/campaign_main/campaign_14_base.py:CampaignBase:pick_up_light_house） | `L69: self.picked_light_house.append(grid)` | 待确认：静态命中不证明 C# 已同步 |
| `switch_to` | 源文本未发现直接写入（module/map/fleet.py:Fleet:switch_to） |  | 仍需核对被调用方法的副作用 |

## 关卡方法中的状态写入

用 AST 扫描了 **2991** 个 `battle_*`/`handle_*` 源代码方法，命中 **30** 处。
这些是上游源代码事实，不代表静态导出计划执行了这些写入，也不构成逐地图迁移结论。

| 模块 | 方法/写入 | 行 |
| --- | --- | ---: |
| `campaign/campaign_main/campaign_16_3.py` | battle_0: 属性赋值 `self.map_has_mob_move = False` | 82 |
| `campaign/campaign_main/campaign_16_4.py` | battle_0: 属性赋值 `self.map_has_mob_move = False` | 94 |
| `campaign/campaign_main/campaign_16_4.py` | battle_1: 属性赋值 `self.F5_is_moved = True` | 110 |
| `campaign/campaign_main/campaign_16_4.py` | battle_1: 属性赋值 `self.F5_is_moved = False` | 113 |
| `campaign/campaign_main/campaign_9_2.py` | battle_0: 属性赋值 `self.map.weight_data = '\n                10 10 30 10 10 20 30 40 10\n                10 10 10 10 10 30 10 50 10\n                30 10 10 10 10 10 10 60 10\n                10 10 10 10 10 10 10 70 10\n                10 30 10 10 10 10 10 10 10\n            '` | 69 |
| `campaign/campaign_main/campaign_9_2.py` | battle_0: 属性赋值 `self.map.weight_data = '\n                10 10 30 10 10 10 10 10 10\n                10 10 20 30 10 30 10 10 10\n                30 10 20 10 10 10 10 10 10\n                10 10 10 10 10 10 10 10 10\n                10 30 10 10 10 10 10 10 10\n            '` | 77 |
| `campaign/campaign_main/campaign_9_2.py` | battle_0: 属性赋值 `self.map.weight_data = '\n                10 10 30 10 10 10 10 10 10\n                10 10 20 30 10 30 10 10 10\n                30 10 20 10 10 10 10 10 10\n                10 10 10 10 10 10 10 10 10\n                10 30 10 10 10 10 10 10 10\n            '` | 85 |
| `campaign/event_20210121_cn/a2.py` | battle_0: 属性赋值 `grid.may_siren = True` | 77 |
| `campaign/event_20210121_cn/a3.py` | battle_0: 属性赋值 `grid.may_siren = True` | 80 |
| `campaign/event_20210121_cn/c2.py` | battle_0: 属性赋值 `grid.may_siren = True` | 77 |
| `campaign/event_20210121_cn/c3.py` | battle_0: 属性赋值 `grid.may_siren = True` | 80 |
| `campaign/event_20211125_cn/t4.py` | handle_clear_mode_config_cover: 属性赋值 `self.map.fortress_data = [self.map.fortress_data[0], ()]` | 107 |
| `campaign/event_20220224_cn/campaign_base.py` | handle_clear_mode_config_cover: 属性赋值 `self.config.MAP_SIREN_TEMPLATE = ['SS']` | 8 |
| `campaign/event_20220224_cn/campaign_base.py` | handle_clear_mode_config_cover: 属性赋值 `self.config.MAP_HAS_SIREN = True` | 9 |
| `campaign/event_20230525_cn/sp.py` | battle_0: 动态属性修改 setattr `setattr(self, f'battle_{battle_count}', self.battle_0)` | 148 |
| `campaign/event_20230525_cn/sp.py` | battle_0: 属性赋值 `self.patched = True` | 149 |
| `campaign/event_20230525_cn/sp.py` | battle_0: 潜在原地修改 .pop `self.siren_list.pop()` | 153 |
| `campaign/event_20230525_cn/sp.py` | battle_0: 属性赋值 `self.action = actions[self.fleet_1_location[0]]` | 160 |
| `campaign/event_20230914_cn/sp.py` | battle_0: 属性赋值 `self._is_a2 = True` | 83 |
| `campaign/event_20240521_cn/sp.py` | battle_0: 属性赋值 `self.is_left = self.fleet_current == B10.location` | 109 |
| `campaign/event_20250227_cn/sp.py` | battle_0: 属性赋值 `self._is_D9 = True` | 95 |
| `campaign/war_archives_20190911_cn/a2.py` | battle_0: 属性赋值 `grid.may_siren = True` | 77 |
| `campaign/war_archives_20190911_cn/a3.py` | battle_0: 属性赋值 `grid.may_siren = True` | 82 |
| `campaign/war_archives_20190911_cn/c2.py` | battle_0: 属性赋值 `grid.may_siren = True` | 77 |
| `campaign/war_archives_20190911_cn/c3.py` | battle_0: 属性赋值 `grid.may_siren = True` | 80 |
| `campaign/war_archives_20211229_cn/a1.py` | handle_clear_mode_config_cover: 属性赋值 `self.config.MAP_HAS_MISSILE_ATTACK = False` | 82 |
| `campaign/war_archives_20211229_cn/c1.py` | handle_clear_mode_config_cover: 属性赋值 `self.config.MAP_HAS_MISSILE_ATTACK = False` | 82 |
| `campaign/war_archives_20211229_cn/campaign_base.py` | handle_clear_mode_config_cover: 属性赋值 `self.config.MAP_HAS_MISSILE_ATTACK = True` | 17 |
| `campaign/war_archives_20220224_cn/campaign_base.py` | handle_clear_mode_config_cover: 属性赋值 `self.config.MAP_SIREN_TEMPLATE = ['SS']` | 8 |
| `campaign/war_archives_20220224_cn/campaign_base.py` | handle_clear_mode_config_cover: 属性赋值 `self.config.MAP_HAS_SIREN = True` | 9 |
