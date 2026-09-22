# S3 真机跑图记录（逐次留档）

> 这份表只记**真机跑过的**（`--run --allow-actions`），不是推演、不是 dry-run。
> 目的：把"覆盖到哪几张图"变成可核对的事实，避免再出现"以为跑过其实没跑"。
> 每行的证据都在 `%TEMP%\<日志>.log` 或 `docs/s3-entry-sequence.md` 里能对上。

**范围约定**：只测本账号已解锁的 **1–14 章**（15 章及以后未解锁，不测）。

命令行（两套战斗流程的区别见 `docs/s3-entry-sequence.md` 末章）：

```powershell
# 场景A：BOSS 一刷出来就打
alashub campaign <章模块> --run --allow-actions --fleet1 3 --fleet2 6 --repeat --max-rounds 20 --max-seconds 1500
# 场景B：先清光小怪再打 BOSS
alashub campaign <章模块> --run --allow-actions --clear-all --fleet1 3 --fleet2 6 --repeat --max-rounds 20 --max-seconds 1500
```

| 图 | 行数 | 场景 | 结果 | 用时 | BOSS 格 | 收尾 | 证据 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2-1 | 4 | A | ✅ 全清 | — | — | 回到章节页 | 章节页徽章 `Clear!` + ★★★（`data/_campaign125.png`） |
| 11-1 | 6 | A | ✅ | 360.6s | F3 | `In stage.`，`campaign_end=True`，无 `WITHDRAW` | `%TEMP%\hard11_full.log` |
| 11-1 | 6 | A（复核：BOSS 颜色垫片已关） | ✅ | 340.8s | G6 | 同上 | `%TEMP%\hard11_noshim.log` |
| 11-1 | 6 | A（半途续打：`battle_count=6`） | ✅ | 53.6s（单场） | F3 | 同上 | `tools/diagnostics/oneoff/resume_boss.py` |
| 11-1 | 6 | B（`--clear-all`） | ✅ | 397.6s | A2 | 同上；关键行 `Enemy remain: []` → `Brute clear BOSS` | `%TEMP%\hard11_clearall.log` |
| 12-1 | 6 | A | ✅ | 312.2s | H5 | 同上 | `%TEMP%\batch_a.log` |
| 12-1 | 6 | B（`--clear-all`） | ✅ | 356.9s | H5 | 同上；`Enemy remain` 一路收到 `[]` 才 `Brute clear BOSS` | `%TEMP%\batch_b.log` |
| 10-1 | 6 | A | ✅ | 355.3s | G3 | 同上 | `%TEMP%\batch_a.log` |
| 14-1 | 7 | A | ✅ | 530.6s | A7 | 同上；该图有自己的 `battle_5` 钩子（`campaign_14_1.py:84`），流程是 `battle_0 → battle_5 → battle_6` | `%TEMP%\batch_a.log` |

**跨关连续**：上面三关是**同一个进程**里连续驱动的（`campaign A,B,C`），两处"复位回战役页"
都是 `尝试1 success=True`，没有人工干预 —— 这是"常驻"所需的状态不跨进程丢的实证。

## 每张图的规则数据（`MAP.spawn_data`，累计口径）

| 图 | 形状 | 累计小怪 | BOSS 回合 | `may_boss` 候选格数 |
| --- | --- | --- | --- | --- |
| 2-1 | 6×4 | 6 | **2** | — |
| 10-1 | 7×6 | 8 | 6 | 2 |
| 11-1 | 8×6 | 7 | 6 | 4（`H1/A2/F3/G6`，实测分别刷在 F3、A2、G6） |
| 12-1 | 8×6 | 7 | 6 | 2（实测 H5） |
| 14-1 | 8×7 | 8 | 6 | 3 |

含义：场景 A 在"BOSS 回合"就打 BOSS（2-1 只要 2 场就刷 BOSS，11/12/14-1 要 6 场）；
场景 B 不看回合数，一直清到 `Enemy remain: []` 才打 BOSS。

## 已知不能跑的图

| 图 | 原因 | 证据 |
| --- | --- | --- |
| 1-1 | **单行图**（7 格一行）：上游检测器 `No vertical line detected`，5 档降阈值重试都无效 | `data/fixtures/subchapter_1_1.png`（真图内帧）+ `probe_backends.py` |

> 注：`data/fixtures/inmap_7-1.png` **不是图内帧**（是主界面），所以"7-1 不能跑"这条
> 没有有效证据；困难 1-4（3 行）加降阈值重试后已能识别。详见 `docs/map-detection.md` 末章。
