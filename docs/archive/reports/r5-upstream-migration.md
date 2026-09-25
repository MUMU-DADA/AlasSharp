# R5 上游引擎迁移审计（P0）

> 本报告由 `tools/diagnostics/r5_upstream_audit.py` 重建，不手写；只做 AST 解析与静态计数，
> 不导入游戏代码、不连设备。目标与流程见 [上游引擎重写](../../../UPSTREAM-ENGINE-REWRITE.md)。

上游检出：`campaign/` 1437 个文件（解析失败 0）

## A. 关卡覆写：形态与可原语化程度

- 类 **2796** 个，方法 **3192** 个，`Config` 属性 **14619** 个，模块级声明赋值 **11118** 个
- 每文件方法数：中位 **2**，最多 9
- **不同方法名仅 81 个** ← 覆写面是"少数钩子 × 大量关卡"，不是 3192 种不同逻辑

### 方法体形态

| 形态 | 数量 | 占比 | 含义 |
| --- | --- | --- | --- |
| `logic` | 1904 | 59.6% | 真实方法体（见下方复杂度分布） |
| `return_other` | 679 | 21.3% | 单条 `return <子对象>.<方法>()`（如 `self.map.…`），按原语处理 |
| `return_self` | 598 | 18.7% | 单条 `return self.<helper>()`，等价于"选哪个原语" |
| `return_super` | 8 | 0.3% | 单条 `return super().x()`，委托父类 |
| `pass` | 2 | 0.1% | 空实现 |
| `assign` | 1 | 0.0% | 单条赋值 |

### 薄覆写的目标 helper（`return_self` + `call_only`，全部）

| 目标 | 次数 |
| --- | --- |
| `clear_boss` | 570 |
| `battle_default` | 15 |
| `brute_clear_boss` | 10 |
| `ui_goto_archives_campaign` | 2 |
| `clear_chosen_enemy` | 1 |

### `logic` 方法的复杂度

| 语句数 | 方法数 |
| --- | --- |
| 1-2 | 591 |
| 3-5 | 1263 |
| 6-10 | 46 |
| >10 | 4 |

行数：中位 7，P90 11，最多 65

### `logic` 方法调用的 helper（原语候选）

| helper | 调用次数 | 归入原语族 |
| --- | --- | --- |
| `battle_default` | 1585 | battle |
| `clear_siren` | 1371 | clear |
| `clear_filter_enemy` | 1041 | clear |
| `clear_enemy` | 417 | clear |
| `clear_roadblocks` | 88 | clear |
| `clear_all_mystery` | 57 | clear |
| `clear_any_enemy` | 56 | clear |
| `campaign_ensure_mode` | 53 | campaign |
| `campaign_ensure_chapter` | 49 | campaign |
| `clear_chosen_enemy` | 48 | clear |
| `clear_potential_roadblocks` | 45 | clear |
| `fleet_2_push_forward` | 43 | fleet |
| `check_accessibility` | 39 | check |
| `fleet_2_protect` | 31 | fleet |
| `goto` | 27 | goto |
| `ui_goto_event` | 26 | ui |
| `ui_page_appear` | 24 | ui |
| `appear` | 19 | appear |
| `pick_up_ammo` | 15 | pick |
| `fleet_2_step_on` | 14 | fleet |
| `mob_move` | 13 | 其它 |
| `fleet_at` | 12 | fleet |
| `_campaign_ball_set` | 12 |  |
| `clear_bouncing_enemy` | 12 | clear |
| `ui_goto_sp` | 11 | ui |

不同 helper 总数 **100**；按前缀归族后：

| 原语族 | 不同 helper 数 | 调用次数 |
| --- | --- | --- |
| `clear` | 14 | 3159 |
| `battle` | 3 | 1591 |
| `campaign` | 5 | 113 |
| `fleet` | 6 | 106 |
| `ui` | 9 | 90 |
| `其它` | 27 | 69 |
| `` | 15 | 46 |
| `check` | 1 | 39 |
| `pick` | 3 | 29 |
| `goto` | 1 | 27 |
| `is` | 7 | 24 |
| `appear` | 2 | 21 |
| `handle` | 4 | 7 |
| `get` | 2 | 6 |
| `map` | 1 | 2 |

### 调用序列收敛度（决定原语 DSL 规模）

`logic` 方法归一化后的**不同调用序列 138 种**，覆盖 1904 个方法：

| 序列（去重后按名字排序） | 方法数 |
| --- | --- |
| `battle_default+clear_filter_enemy+clear_siren` | 857 |
| `battle_default+clear_siren` | 259 |
| `` | 135 |
| `battle_default+clear_enemy+clear_siren` | 118 |
| `battle_default+clear_filter_enemy` | 95 |
| `battle_default+clear_any_enemy+clear_filter_enemy+clear_siren` | 43 |
| `battle_default+clear_enemy` | 34 |
| `ui_page_appear` | 16 |
| `battle_default+clear_siren+fleet_2_protect` | 14 |
| `check_accessibility+clear_roadblocks` | 12 |
| `battle_default+clear_all_mystery` | 12 |
| `battle_default+clear_bouncing_enemy+clear_filter_enemy+clear_siren` | 12 |
| `battle_default+check_accessibility+clear_all_mystery` | 11 |
| `clear_chosen_enemy` | 10 |
| `battle_default+clear_potential_roadblocks+clear_roadblocks` | 9 |

Top 20 序列覆盖 **1676/1904（88.0%）**；Top 50 覆盖 **1792/1904（94.1%）**

### 原语实现位置与规模（定义侧）

| helper | 调用次数 | 定义位置 | 类 | 语句/行 | 首行说明 |
| --- | --- | --- | --- | --- | --- |
| `battle_default` | 1585 | `module\campaign\campaign_base.py:14` | `CampaignBase` | 3/6 |  |
| `clear_siren` | 1371 | `module\map\map.py:453` | `Map` | 7/26 | Returns: |
| `clear_filter_enemy` | 1041 | `module\map\map.py:663` | `Map` | 10/40 | If EnemyPriority_EnemyScaleBalanceWeight |
| `clear_enemy` | 417 | `module\map\map.py:191` | `Map` | 6/24 | Methods to clear a enemy. May not do any |
| `clear_roadblocks` | 88 | `module\map\map.py:216` | `Map` | 7/29 | Clear roadblocks. |
| `clear_all_mystery` | 57 | `module\map\map.py:171` | `Map` | 3/19 | Methods to pick up all mystery. |
| `clear_any_enemy` | 56 | `module\map\map.py:480` | `Map` | 6/28 | Returns: |
| `campaign_ensure_mode` | 53 | `module\campaign\campaign_ui.py:119` | `CampaignUI` | 3/30 | Args: |
| `campaign_ensure_chapter` | 49 | `module\campaign\campaign_ui.py:62` | `CampaignUI` | 6/47 | Args: |
| `clear_chosen_enemy` | 48 | `module\map\map.py:15` | `Map` | 11/22 | Args: |
| `clear_potential_roadblocks` | 45 | `module\map\map.py:246` | `Map` | 7/29 | Avoid roadblock that only has one grid e |
| `fleet_2_push_forward` | 43 | `module\map\map.py:569` | `Map` | 13/36 | Move fleet 2 to the grid with lower grid |
| `check_accessibility` | 39 | `module\map\fleet.py:976` | `Fleet` | 4/27 | Args: |
| `fleet_2_protect` | 31 | `module\map\map.py:629` | `Map` | 4/33 | Mob fleet moves around boss fleet, clear |
| `goto` | 27 | `module\map\fleet.py:470` | `Fleet` | 4/41 | Args: |
| `ui_goto_event` | 26 | `module\campaign\campaign_event.py:151` | `CampaignEvent` | 3/14 |  |
| `ui_page_appear` | 24 | `module\ui\ui.py:27` | `UI` | 3/19 | Args: |
| `appear` | 19 | `module\base\base.py:214` | `ModuleBase` | 6/51 | Args: |
| `pick_up_ammo` | 15 | `module\map\map.py:49` | `Map` | 2/24 | Args: |
| `fleet_2_step_on` | 14 | `module\map\map.py:509` | `Map` | 9/36 | Fleet step on a grid which can reduce th |
| `mob_move` | 13 | （上游 module/ 内未找到同名定义） | — | — | — |
| `fleet_at` | 12 | `module\map\fleet.py:960` | `Fleet` | 2/15 | Args: |
| `_campaign_ball_set` | 12 | （上游 module/ 内未找到同名定义） | — | — | — |
| `clear_bouncing_enemy` | 12 | `module\map\map.py:704` | `Map` | 10/43 | Clear enemies which are bouncing in a fi |
| `ui_goto_sp` | 11 | `module\campaign\campaign_event.py:166` | `CampaignEvent` | 3/14 |  |
| `pick_up_light_house` | 10 | （上游 module/ 内未找到同名定义） | — | — | — |
| `clear_mechanism` | 10 | `module\map\map.py:74` | `Map` | 6/27 | Args: |
| `_campaign_separate_name` | 10 | `module\campaign\campaign_ocr.py:62` | `CampaignOcr` | 4/26 | Args: |
| `ui_click` | 9 | `module\ui\ui.py:78` | `UI` | 6/52 | Args: |
| `is_in_stage` | 8 | `module\handler\enemy_searching.py:72` | `EnemySearchingHandler` | 3/6 |  |

原语定义在 module/ 内可定位 **75/100** 个；其余为动态属性或子对象方法。

### 原语参数形态（调用侧）

| helper | 参数形态分布 |
| --- | --- |
| `battle_default` | 无参=1585 |
| `clear_siren` | 无参=1370，关键字=1 |
| `clear_filter_enemy` | 上文字段+关键字=1033，关键字+字面量=8 |
| `clear_enemy` | 关键字=417 |
| `clear_roadblocks` | 表达式=72，关键字+表达式=12，上文字段=4 |
| `clear_all_mystery` | 无参=55，关键字=2 |
| `clear_any_enemy` | 关键字=56 |
| `campaign_ensure_mode` | 字面量=53 |
| `campaign_ensure_chapter` | 上文字段=45，字面量=4 |
| `clear_chosen_enemy` | 上文字段=35，上文字段+关键字=13 |
| `clear_potential_roadblocks` | 表达式=37，关键字+表达式=5，上文字段=3 |
| `fleet_2_push_forward` | 无参=43 |
| `check_accessibility` | 关键字+表达式=30，上文字段+关键字=9 |
| `fleet_2_protect` | 无参=31 |
| `goto` | 上文字段+关键字=16，上文字段=11 |

### 按钩子看形态（Top 12）

| 钩子 | 合计 | 形态分布 |
| --- | --- | --- |
| `battle_0` | 1337 | logic=1302，return_self=35 |
| `battle_5` | 645 | return_other=390，logic=253，return_self=2 |
| `battle_4` | 514 | return_self=472，logic=24，return_other=18 |
| `battle_6` | 228 | return_other=201，logic=27 |
| `battle_3` | 114 | return_self=82，logic=29，return_other=3 |
| `battle_7` | 71 | return_other=66，logic=5 |
| `map_data_init` | 20 | logic=20 |
| `battle_2` | 18 | logic=16，return_self=2 |
| `handle_exp_info` | 17 | logic=17 |
| `_campaign_get_chapter_index` | 17 | logic=17 |
| `battle_1` | 14 | logic=12，return_self=2 |
| `get_map_clear_percentage` | 11 | logic=11 |

### 类基类分布

| 基类 | 次数 |
| --- | --- |
| `CampaignBase` | 1370 |
| `ConfigBase` | 855 |
| `CampaignBase_` | 63 |
| `Grid` | 8 |
| `Config41` | 2 |
| `Campaign_15_4` | 1 |
| `GridInfo` | 1 |
| `Config31` | 1 |

## B. `IVisionEngine` 接口面与初判归属

接口方法 **25** 个（初判仅供评审）：

| 方法 | 初判 | 说明 |
| --- | --- | --- |
| `Ping` | 设备帧 | 保留：宿主探活，随识图通道一起保留 |
| `SetServer` | 可静态化 | 静态：服务器变体来自配置 |
| `LoadScreenshot` | 设备帧 | 保留：帧输入管道，属识图前置 |
| `SetScreenshot` | 设备帧 | 保留：帧输入管道，属识图前置 |
| `ScaleScreenshot` | 设备帧 | 可自研：纯几何缩放，但属识图前置，优先级低 |
| `PageList` | 可静态化 | 静态：页面清单可由上游页面图导出 |
| `PageCurrent` | 识图保留 | 保留：依赖素材匹配判断当前页面 |
| `PageGraph` | 可静态化 | 静态：页面关系（Page.links）可导出为数据 |
| `AssetButtonCenter` | 识图保留 | 保留：素材坐标由上游素材对象解析 |
| `PageAppear` | 识图保留 | 保留：页面出现判定依赖素材匹配 |
| `AppearOn` | 识图保留 | 保留：模板/颜色匹配 |
| `AppearOnBatch` | 识图保留 | 保留：批量匹配 |
| `ButtonMatch` | 识图保留 | 保留：按钮匹配 |
| `TemplateMatch` | 识图保留 | 保留：模板匹配 |
| `Ocr` | 识图保留 | 保留：OCR 模型推理 |
| `AccountState` | 必须自研 | 自研：账号状态读屏后的语义与状态机 |
| `TaskCatalog` | 可静态化 | 静态：任务目录/参数 schema 可导出 |
| `StatisticsReport` | 必须自研 | 自研：统计口径与计算（依赖静态采集数据） |
| `RefreshStatisticsLoot` | 必须自研 | 自研：掉落统计刷新（依赖未迁移的采集链） |
| `MeowfficerReport` | 必须自研 | 自研：指挥喵报告计算 |
| `ClearMeowfficerReport` | 必须自研 | 自研：报告清理 |
| `ValidateShopStrategy` | 必须自研 | 自研：策略脚本校验（需与上游语义对拍） |
| `ConfigureDevice` | 设备帧 | 可自研：设备配置（ADB 传输层已有 C# 骨架） |
| `CaptureViaEngine` | 设备帧 | 可自研：抓帧路径（可用 C# 第三方库） |
| `RunCampaignPlan` | 必须自研 | 自研：**重写核心目标**——关卡流程执行（当前由上游 Campaign.run() 承担） |

Core 内调用点分布（按接口方法名 + 视觉宿主接收者统计，含实现类内部转发）：

| 文件 | 调用次数 |
| --- | --- |
| `src\Alas.Core\Diagnostics\DeviceCheck.cs` | 9 |
| `src\Alas.Core\Device\DeviceController.cs` | 4 |
| `src\Alas.Core\Tasks\ObserveTask.cs` | 3 |
| `src\Alas.Core\Diagnostics\CaptureCheck.cs` | 2 |
| `src\Alas.Core\Tasks\OsStateTask.cs` | 2 |
| `src\Alas.Core\Diagnostics\MapCheck.cs` | 1 |
| `src\Alas.Core\Navigation\PageNavigator.cs` | 1 |
| `src\Alas.Core\Runtime\AlasSession.cs` | 1 |
| `src\Alas.Core\Runtime\CampaignBatchRunner.cs` | 1 |
| `src\Alas.Core\Tasks\AccountStateTask.cs` | 1 |
| `src\Alas.Core\Tasks\NavigateTask.cs` | 1 |
| `src\Alas.Core\Tasks\TaskCatalogTask.cs` | 1 |
| `src\Alas.Core\Tasks\TaskQueue.cs` | 1 |
| **合计** | **28** |

## C. 上游重依赖的用法面（按子系统）

> 统计范围：上游 `module/**`（不含 `campaign/**` 与 `deploy/**`）；计数为"导入该库的文件数"。

| 依赖 | 涉及文件数 | 主要子系统 |
| --- | --- | --- |
| `numpy` | 55 | `module/device`(7)、`module/map_detection`(6)、`module/map`(5)、`module/os`(5)、`module/island_handler`(4)、`module/island`(3) |
| `cv2` | 27 | `module/device`(5)、`module/island_handler`(4)、`module/shop`(3)、`module/base`(2)、`module/island`(2)、`module/retire`(2) |
| `adbutils` | 13 | `module/device`(13) |
| `scipy` | 10 | `module/handler`(2)、`module/map_detection`(2)、`module/commission`(1)、`module/island_handler`(1)、`module/map`(1)、`module/research`(1) |
| `uiautomator2` | 6 | `module/device`(5)、`module/handler`(1) |
| `jellyfish` | 5 | `module/island`(2)、`module/island_handler`(2)、`module/commission`(1) |
| `imageio` | 2 | `module/base`(2) |
| `lz4` | 1 | `module/device`(1) |
| `matplotlib` | 0 | — |

> 识别用途与逻辑用途需按文件逐一区分：识别用途按目标不要求 C# 重写；
> 逻辑用途（设备输入、截图处理、字符串相似度等）在允许使用 C# 第三方库的前提下可逐个替代。

## D. 静态导出对引擎实际读取字段的覆盖（P1 依据）

- 导出文件 **1437**；`campaign.battles` 条目 **3019**，
  其中 `plan_complete` **2795（92.6%）**、未完成 **224**；
- 导出 `config` 段出现的键共 **80** 个；
- 关卡覆写实际读取的配置字段 **15** 个，其中未出现在导出里的 **6** 个：

| 配置字段 | 关卡内读取次数 | 导出状态 |
| --- | --- | --- |
| `MAP_HAS_MOVABLE_ENEMY` | 16 | 已导出 |
| `MAP_CLEAR_ALL_THIS_TIME` | 8 | 缺失 |
| `override` | 7 | 缺失 |
| `FLEET_BOSS` | 4 | 已导出 |
| `SERVER` | 4 | 缺失 |
| `MAP_HAS_MISSILE_ATTACK` | 3 | 已导出 |
| `Fleet_FleetOrder` | 2 | 缺失 |
| `MAP_SIREN_TEMPLATE` | 2 | 已导出 |
| `MAP_HAS_SIREN` | 2 | 已导出 |
| `FLEET_2` | 2 | 已导出 |
| `MAP_CHAPTER_SWITCH_20241219` | 2 | 已导出 |
| `STAGE_ENTRANCE` | 2 | 已导出 |
| `Campaign_Event` | 1 | 缺失 |
| `MAP_HAS_FORTRESS` | 1 | 已导出 |
| `Campaign_Name` | 1 | 缺失 |

`unparsed` 原因分布（未完整静态表达的覆写）：

| 原因 | 次数 |
| --- | --- |
| `If(nested)` | 212 |
| `Assign` | 80 |
| `Expr` | 47 |
| `Return(expr)` | 28 |
| `Raise` | 4 |
| `For` | 1 |

`MAP` 声明读取面（关卡覆写内）：

| MAP 字段 | 读取次数 |
| --- | --- |

## E. 导出的可执行计划（DSL 面）

- `campaign.battles[].steps` 非空的钩子 **2795/3019（92.6%）**，共 **5694 步**；
- 步骤类型 **4 种**，原语 **32 个**（C# 引擎的执行面）：

| 步骤类型 | 次数 |
| --- | --- |
| `conditional` | 2814 |
| `terminal` | 2772 |
| `call` | 101 |
| `super_delegate` | 7 |

| 原语 | 出现次数 |
| --- | --- |
| `battle_default` | 1489 |
| `clear_siren` | 1284 |
| `clear_filter_enemy` | 978 |
| `fleet_boss.clear_boss` | 671 |
| `clear_boss` | 575 |
| `clear_enemy` | 402 |
| `clear_roadblocks` | 48 |
| `clear_all_mystery` | 36 |
| `clear_potential_roadblocks` | 35 |
| `fleet_2_protect` | 26 |
| `fleet_2_push_forward` | 22 |
| `fleet_1.clear_boss` | 13 |
| `fleet_boss.capture_clear_boss` | 12 |
| `clear_bouncing_enemy` | 12 |
| `fleet_2_step_on` | 11 |
| `brute_clear_boss` | 11 |
| `pick_up_light_house` | 10 |
| `fleet_boss.brute_clear_boss` | 9 |
| `clear_any_enemy` | 8 |
| `super().handle_boss_appear_refocus` | 7 |

实参形态（`positional`）：

| 取值 | 次数 |
| --- | --- |
| `1L > 1M > 1E > 1C > 2L > 2M > 2E > 2C > 3L > 3M > 3E > 3C` | 915 |
| `1T > 1L > 1E > 1M > 2T > 2L > 2E > 2M > 3T > 3L > 3E > 3M` | 27 |
| `<expr>` | 20 |
| `1L > 1M > 2L > 2M > 3L > 3M > 1E > 2E > 3E > 1C > 2C > 3C` | 12 |
| `1L > 1M > 1E > 2L > 3L > 2M > 2E > 1C > 2C > 3M > 3E > 3C` | 10 |
| `1L > 1M > 2L > 2M > 3L > 2E > 3E > 2C > 3C > 3M` | 8 |

> C# 侧读取这一层的产品代码：`src/Alas.Core/Campaign/CampaignPlan.cs` + 只读命令 `Alas.Server r5-plan`（统计口径与本报告一致，可跨语言对拍）。
> 执行侧骨架（角色划分、形状契约校验、原语注册表、干跑）见 `src/Alas.Core/Campaign/CampaignEngine.cs`：
> 全库 `r5-plan` 概览输出 3019 个钩子（形状符合契约 3006）、5694 步 / 31 个原语 / 已实现 0。

### 步骤实参完整度（决定哪些步骤能被 C# 直接执行）

| 实参形态 | 步骤数 | 占比 |
| --- | --- | --- |
| 无参 | 4172 | 73.3% |
| 字面量 | 1500 | 26.3% |
| 含未求值表达式 | 22 | 0.4% |

| 原语 | 无参 | 字面量 | 含未求值表达式 |
| --- | --- | --- | --- |
| `battle_default` | 1489 | 0 | 0 |
| `clear_siren` | 1283 | 1 | 0 |
| `clear_filter_enemy` | 0 | 972 | 6 |
| `fleet_boss.clear_boss` | 671 | 0 | 0 |
| `clear_boss` | 575 | 0 | 0 |
| `clear_enemy` | 0 | 402 | 0 |
| `clear_roadblocks` | 0 | 45 | 3 |
| `clear_all_mystery` | 36 | 0 | 0 |
| `clear_potential_roadblocks` | 0 | 32 | 3 |
| `fleet_2_protect` | 26 | 0 | 0 |
| `fleet_2_push_forward` | 22 | 0 | 0 |
| `fleet_1.clear_boss` | 13 | 0 | 0 |

> 含未求值表达式（`"<expr>"`）的步骤无法直接执行——这是 P1 要补的导出侧缺口（最大一块是 `clear_filter_enemy` 的过滤串）。

### 轨迹对拍（计划 vs 原始调用列表）

不变量：除 `super_delegate` 外，`steps.op` 序列应等于导出器给出的 `calls`。
实测 **一致 2793 / 不一致 2 / steps 为空 224**（合计 3019）。

例外（上游源码里的死代码：重复的 `return self.battle_default()`——`calls` 收了两次，
`steps` 正确地只保留一次；导出器的 `dead_code` 字段未记录该处）：

- `event_20211028_tw\c3.json::battle_0 calls=['clear_siren', 'clear_enemy', 'battle_default', 'battle_default'] steps=['clear_siren', 'clear_enemy', 'battle_default']`
- `event_20211028_tw\d1.json::battle_0 calls=['clear_siren', 'clear_enemy', 'battle_default', 'battle_default'] steps=['clear_siren', 'clear_enemy', 'battle_default']`

