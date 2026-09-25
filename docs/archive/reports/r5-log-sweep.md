# R5 历史日志扫描（决策层对照）

> 本报告由 `tools/diagnostics/r5_log_sweep.py` 重建，不手写。
> 日志来自本机忽略目录 `data/`，按关卡头切分；只证明这些**历史运行**，不代表其他关卡或配置。
> 变体由运行配置**显式声明**（不是从日志的 `Using function:` 推断，避免循环论证）。

| 日志 | 关卡 | 声明变体 | 轮数 | 一致 | 不一致 | 跳过 | 结论 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| s3_native_1_4.log | campaign_1_4 | default_hooks | 4 | 4 | 0 | 0 | 一致 |
| s3_native_2_1_clearall.log | campaign_2_1 | clear_all | 7 | 7 | 0 | 0 | 一致 |
| s3_native_final_batch.log | campaign_1_1 | default_hooks | 2 | 2 | 0 | 0 | 一致 |
| s3_native_final_batch.log | campaign_1_4 | default_hooks | 4 | 4 | 0 | 0 | 一致 |

小结：4 段合计 17 轮出击，决策层不一致 0 轮。

## 跳过的日志（如实列出原因，不当作通过）

| 日志 | 原因 |
| --- | --- |
| _verify_s3_camera_compat.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| _verify_s3_upstream_loading.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| progress-audit-final.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| progress-audit-observe.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| progress-audit-offline.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| s3_after_capture.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| s3_final_plan_verification.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| s3_final_verification.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| s3_loading_verification.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| s3_native_1_1_retry.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| s3_native_small_and_clearall.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| s3_outcome_verification.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| s3_plan_verification.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
| s3_product_map_verification.log | 日志里没有 `Using function:`（不含出击决策，无可比对） |
