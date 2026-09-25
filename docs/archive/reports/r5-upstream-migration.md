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

