# R5 真机帧扫描（识别 → 状态 → 关卡循环）

> 本报告由 `tools/diagnostics/r5_frame_sweep.py` 重建，不手写。
> 帧来自本机忽略目录 `data/fixtures/`，检出可能没有；**只证明这些帧**，不代表整类地图。

| 帧 | 关卡 | 识别 | 结论 | 轮数 | 干跑动作 | 上游调用 | 两宿主一致 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| inmap_2-2.png | campaign_main/campaign_2_2 | inmap_2-2.png（识别到 35 格） | Ended | 4 | 4 | 21 | 是 |
| inmap_3-1.png | campaign_main/campaign_3_1 | inmap_3-1.png（识别到 28 格） | Ended | 4 | 4 | 21 | 是 |
| inmap_3-2.png | campaign_main/campaign_3_2 | inmap_3-2.png（识别到 32 格） | Ended | 4 | 4 | 21 | 是 |
| inmap_7-1.png | campaign_main/campaign_7_1 | 识别失败 | - | - | - | - | — |
| map_2_1.png | campaign_main/campaign_2_1 | 识别失败 | - | - | - | - | — |
| map_hard_1_4.png | campaign_main/campaign_1_4 | map_hard_1_4.png（识别到 21 格） | Ended | 20 | 21 | 21 | 是 |

小结：6 帧中 4 帧的两宿主原语序列一致（前缀比对）。
口径：序列按**共同前缀**比——录制渠道是静态桩（`battle_count` 不增长），真机宿主会打到轮次上限。
