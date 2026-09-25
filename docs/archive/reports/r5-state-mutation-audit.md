# R5 状态写入审计（上游会改状态的方法 vs C# 替换的原语）

> 本报告由 `tools/diagnostics/r5_state_mutation_audit.py` 重建，不手写。
> 用途：C# 逐条替换了上游原语，而上游有些方法**顺手写状态**；替换掉就丢了那些写入。
> 只做静态扫描（`inspect.getsource` + 写入模式匹配），不执行游戏动作。

- 注册表原语：**30** 个
- 检测到**状态写入**的条目：**3**（已核对 **3**）
- **待确认**：**0**

| 原语 | 写入类型 | 上游源码行 | 结论 |
| --- | --- | --- | --- |
| `battle_boss` | 无状态写入（module/campaign/campaign_base.py:CampaignBase） |  | 无需处理 |
| `battle_default` | 无状态写入（module/campaign/campaign_base.py:CampaignBase） |  | 无需处理 |
| `brute_clear_boss` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `brute_fleet_meet` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `capture_clear_boss` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `check_accessibility` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `clear_all_mystery` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `clear_any_enemy` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `clear_boss` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `clear_bouncing_enemy` | SelectedGrids.set（module/map/map.py:Map） | `route.select(may_bouncing_enemy=True).set(may_bouncing_enemy=False)` | `may_bouncing_enemy = False`：C# 成功分支已按上游顺序置假 + `UpdateMap()`（置假走 `SetGridFlag` 同步到上游） |
| `clear_chosen_enemy` | 设备侧（仍由上游执行） |  | 不需要 C# 镜像 |
| `clear_enemy` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `clear_filter_enemy` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `clear_first_roadblocks` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `clear_map_items` | 无状态写入（campaign.event_20221124_cn.campaign_base:CampaignBase） |  | 无需处理 |
| `clear_mechanism` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `clear_potential_boss` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `clear_potential_roadblocks` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `clear_roadblocks` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `clear_siren` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `fleet_2_break_siren_caught` | 格子标志赋值（module/map/map.py:Map） | `grid.is_caught_by_siren = False` | `grid.is_caught_by_siren = False`：C# `ClearCaughtBySirenFlags` 覆盖（两宿主都改自己模型） |
| `fleet_2_protect` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `fleet_2_push_forward` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `fleet_2_rescue` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `fleet_2_step_on` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `handle_boss_appear_refocus` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `pick_up_ammo` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |
| `pick_up_flare` | 格子标志赋值（campaign.campaign_main.campaign_14_base:CampaignBase） | `grid.is_flare = True` | `grid.is_flare = True`：C# 走宿主 `SetGridFlag` → 渠道 `set` 同步（r5-device 自检） |
| `pick_up_light_house` | 无状态写入（campaign.campaign_main.campaign_14_base:CampaignBase） |  | 无需处理 |
| `switch_to` | 无状态写入（module/map/map.py:Map） |  | 无需处理 |

## 关卡钩子体里的状态写入

用 `ast` 扫了全库 **2991** 个 `battle_*`/`handle_*` 钩子，命中状态写入 **9** 处。

口径：钩子体是被导出成**计划**并执行的东西，所以这里的写入**不会**由上游执行；
要么由计划里的原语覆盖，要么必须显式同步（见宿主 `SetGridFlag`）。

| 模块 | 位置 | 行 |
| --- | --- | --- |
| `campaign/event_20210121_cn/a2.py` | `battle_0: may_siren = …` | 77 |
| `campaign/event_20210121_cn/a3.py` | `battle_0: may_siren = …` | 80 |
| `campaign/event_20210121_cn/c2.py` | `battle_0: may_siren = …` | 77 |
| `campaign/event_20210121_cn/c3.py` | `battle_0: may_siren = …` | 80 |
| `campaign/event_20240521_cn/sp.py` | `battle_0: is_left = …` | 109 |
| `campaign/war_archives_20190911_cn/a2.py` | `battle_0: may_siren = …` | 77 |
| `campaign/war_archives_20190911_cn/a3.py` | `battle_0: may_siren = …` | 82 |
| `campaign/war_archives_20190911_cn/c2.py` | `battle_0: may_siren = …` | 77 |
| `campaign/war_archives_20190911_cn/c3.py` | `battle_0: may_siren = …` | 80 |

