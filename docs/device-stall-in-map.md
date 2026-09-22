# 真机卡点：进了地图却判"不在图内"（`IN_MAP` 阈值）

**结论（2026-09-23 真机窗口实测）**：本客户端「撤退」按钮的颜色比上游期望值略偏，
`IN_MAP` 的颜色比对相似度落在 **10.0 ~ 10.4**，而上游 `appear(button, threshold=10)` 要求 **< 10**。
于是上游 `enter_map()` 一直等不到"已进图"的信号，62 秒后由 `stuck_record_check` 抛
`GameStuckError: Wait too long`。**游戏其实已经在地图里了。**

## 证据链

| 环节 | 事实 |
| --- | --- |
| 现场帧 | `data/_live_enter_map_stall.png`（真机 1-1 卡死时刻）：舰队已就位、右下角「撤退」按钮在屏、标题「无限时」 |
| 该帧的判据 | `IN_MAP` 相似度 **10.19**（要求 < 10）→ `in_map=false` |
| 上游在等什么 | `enter_map` 的等待集含 `IN_MAP` / `MAP_PREPARATION` / `1-1` / `FLEET_PREPARATION` … |
| 其它真实帧 | 地图帧相似度：**3.33 / 10.06 / 10.19 / 10.33**；非地图帧最近 **83.11 / 92.07** |
| 判别间隔 | 地图帧 ≤ 10.33，非地图帧 ≥ 83 —— 间隔极大，**阈值 10 恰好卡在真实取值带里** |

## 为什么不是"改颜色常量"

同一客户端上该按钮存在两类真实取值：

- (210, 124, 124) —— 相似度 3.33（`_boss122.png`）
- (214.6, 131.1, 120.7) —— 相似度 10.19（本次现场帧）

把期望色改成后者，前一类帧会**反过来失败**。只有**放宽阈值**能同时覆盖两类真实帧。

## 修法（最小、按素材、保留上游语义）

`tools/alas_vision.py` 的 `apply_in_map_threshold_compat()`：只把 `handler/IN_MAP`
这一个素材的 `appear_on` 阈值放宽到 **20**（只放宽不收紧），在 `op_s3_campaign_init`
里与其它兼容垫片一起挂上。**不动全局 `appear` 语义、不改颜色常量、不影响其它按钮。**

阈值 20 的安全性由上面的分布保证：非地图帧最近 83.11，离 20 仍有 4 倍余量。

## 验证（同一台设备、同一账号、前后对比）

| 次 | `enter_map` | 结果 |
| --- | --- | --- |
| 修前 | **62.1s → `GameStuckError`** | `outcome=error`，撤退清理后结束 |
| 修后 | **5.5s** | `map_init` 2.0s → 两场战斗 → **`cleared=True` / 战果 S** / `withdrawn=False` / 退出码 0 |

修后这次是**走新运行时**（`alashub campaign` → `AlasSession` → `CampaignBatchTask`）跑出的第一条
真机通关证据；顺带验证了跨局清理：上一次卡死留下的地图由
`prepare_campaign_navigation` 撤掉（`withdrew_previous_sortie=True`）。

## 复现

```powershell
python tools\diagnostics\verify_account_state.py     # 逐帧相似度留证（含本页的现场帧口径）
alashub campaign campaign.campaign_main.campaign_1_1 --run --allow-actions --serial <设备> --max-seconds 300
```

## 遗留

- 该垫片只覆盖 `IN_MAP`。**同一客户端上是否还有别的按钮也卡在阈值边上**未系统排查 ——
  可用 `verify_account_state.py` 的逐帧留证方式扩成"全按钮相似度分布"来查（未做）。
- `enter_map` 等待集里还有 `MAP_PREPARATION` / `FLEET_PREPARATION` 等；本次是 `IN_MAP` 先触发，
  不代表其它项在本客户端一定匹配（未单独验证）。
