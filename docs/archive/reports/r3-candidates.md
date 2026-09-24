# R3 迁移候选：原生钩子覆盖排序

> 本页由 `tools/diagnostics/r3_candidates.py` 从 S0 关卡 IR（`data/campaign/**`）生成，**不手写**。
> 排序依据是**覆盖章节数**（路线 R3：先做高频、低耦合、可对拍的，不由某张地图是否失败决定）。

覆盖 1437 章 IR；有钩子的关卡 49 个；去重方法 17 个。

## 排序（覆盖章节数 → 方法名）

| # | 方法 | 覆盖章节 | 说明 |
| --- | --- | --- | --- |
| 1 | `map_data_init` | **15** | campaign_14_4、t4、sp 等 15 章 |
| 2 | `combat_status` | **8** | ht3、ht6、t3 等 8 章 |
| 3 | `get_map_clear_percentage` | **8** | a2、a3、c2 等 8 章 |
| 4 | `in_sight` | **4** | b3、d3、b3 等 4 章 |
| 5 | `_expected_end` | **3** | campaign_hard、sp、sp3 |
| 6 | `clear_boss` | **3** | campaign_hard、b2、d2 |
| 7 | `handle_clear_mode_config_cover` | **3** | t4、a1、c1 |
| 8 | `map_init` | **3** | campaign_16_3、campaign_16_4、c1 |
| 9 | `before_boss` | **2** | b2、d2 |
| 10 | `bored_visit` | **2** | a1、c1 |
| 11 | `brute_clear_boss` | **2** | b2、d2 |
| 12 | `find_current_fleet` | **2** | a1、c1 |
| 13 | `handle_in_stage` | **2** | c2、sp3 |
| 14 | `_campaign_ocr_result_process` | **1** | sp |
| 15 | `catch_camera_repositioning` | **1** | t4 |
| 16 | `execute_actions` | **1** | sp |
| 17 | `is_event_animation` | **1** | sp |

## 与文档既有数字的核对

| 项 | 本次独立数出 | README 记录 | 差异 |
| --- | --- | --- | --- |
| 有钩子的关卡 | 49 | 49 | +0 |
| 去重方法数 | 17 | 17 | +0 |

> 对不上的处置：先查是"文档旧了"还是"IR 读法不对"（例如 `native_overrides` 的定义变了），
> 再决定改文档还是改脚本 —— 不要直接改数字把两边凑成一样。

## 迁移前的门槛（路线 R3）

每个候选迁移时都要带：上游调用轨迹、C# 结果轨迹、失败语义对拍；对拍不完整就继续走上游宿主。
**不存在只对单张地图有效的阈值、坐标或路线** —— 单张地图只能作为现场样本。

复现：`python tools/diagnostics/r3_candidates.py`。
