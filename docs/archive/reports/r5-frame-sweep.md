# R5 真机帧扫描（识别 → 状态 → 关卡循环）

> 本报告由 `tools/diagnostics/r5_frame_sweep.py` 重建，不手写。
> 帧来自本机忽略目录 `data/fixtures/`，检出可能没有；**只证明这些帧**，不代表整类地图。

| 帧 | 关卡 | 识别 | 结论 | 轮数 | 干跑动作 | 上游调用 | 两宿主一致 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| inmap_2-2.png | campaign_main/campaign_2_2 | inmap_2-2.png（识别到 35 格） | Ended | 20 | 21 | 21 | 是 |
| inmap_3-1.png | campaign_main/campaign_3_1 | inmap_3-1.png（识别到 28 格） | Ended | 20 | 21 | 21 | 是 |
| inmap_3-2.png | campaign_main/campaign_3_2 | inmap_3-2.png（识别到 32 格） | Ended | 20 | 21 | 21 | 是 |
| inmap_7-1.png | campaign_main/campaign_7_1 | 识别失败 | - | - | - | - | — |
| map_2_1.png | campaign_main/campaign_2_1 | 识别失败 | - | - | - | - | — |
| map_hard_1_4.png | campaign_main/campaign_1_4 | map_hard_1_4.png（识别到 21 格） | Ended | 20 | 21 | 21 | 是 |

小结：6 帧中 4 帧的两宿主原语序列、轮次和结论一致。
口径：固定地图夹具和显式成功计数反馈；比较完整动作（舰队路径、目标、expected）、逐轮状态及结论。只省略未改变当前舰队的 ensure 查询，不证明实战效果。
