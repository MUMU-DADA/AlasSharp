# S3 规则身份与执行语义

`s3_run_plan` 用完整模块名选择规则。例如
`campaign.event_20220210_cn.a1` 只读取
`data/campaign/event_20220210_cn/a1.json`，并要求文件的 `source` 为
`campaign/event_20220210_cn/a1.py`。其他活动同名的 `a1.json` 不参与匹配。

缺少文件返回 `stage=rules, error_code=ir_not_found`；来源不符返回
`ir_source_mismatch`；无效模块名或 JSON 返回对应结构化错误。上述检查在
Campaign 初始化之前完成。默认 `dry_run=true` 只读 JSON，不连接设备、不截图、
不初始化 Campaign。

响应中 `plan_steps` 保留为已导出 `battle_*` 方法名清单，按数字排序；
`semantic_trace` 是 AST 中出现过的辅助调用。它们不是实际执行次序。
`ir_plan_status` 区分 `complete`、`incomplete` 和
`no_exported_battle_methods`。没有导出本地方法时可能存在上游继承实现，
不能将其认作 JSON 计划完整。

真实执行入口由 `runtime_entrypoint=Campaign.run` 标示：导航完成后，宿主通过
`run_native_campaign` 调用该实例的原生 `run()`，保留章节覆写的 run 和继承的钩子，
进图、map_init、地图扫描及战斗循环都由上游执行。MAP 提供地图、相机、出生规则，
Config 合并章节及继承配置。`runtime_dispatch=execute_a_battle` 标示常规战斗
分派入口，它根据实际 `battle_count` 选择 `battle_N`，并包含全清分支；
不完整 IR 也不会被当成残缺调用列表执行。例如 2-1 的 `battle_2` 有嵌套分支，
导出器标记为不完整，原生方法仍保有该逻辑。

导航调用上游 `ensure_campaign_ui(loader.stage, Campaign_Mode)`，由上游处理
主线、困难、活动和档案的页面及模式，并设置 ENTRANCE。档案入口还依赖
`Campaign_Event`，因此加载前必须把完整模块的目录与文件名绑定到账号配置，
与 `CampaignRun.run` 的加载顺序保持一致。

同一设备连续运行多关时，每关导航前清空上游 stuck/click 记录；仍在上一关地图中时，
先调用上游 withdraw，再做章节导航。该撤退产生的 CampaignEnd 只用于准备下一关，
不计作本次通关。导航完成后再清空一次记录，与上游进入 campaign.run 前的准备一致。

真跑响应的 `execution=upstream_run` 表示委托上游 run；`steps` 记录导航和实际观察到的
上游操作。`max_seconds` 在操作边界检查，`max_rounds` 限制战斗调用数，
`stop_after=map_init` 是诊断出口。到达这些限制不表示通关。初始化或导航失败时，
协议会停止，不进入原生 run。

这条路径仍依赖上游 Python Campaign 与 Config，并非独立的 JSON 解释器。
JSON 在此提供身份和可审查的导出元数据；通关能力需要运行时和现场结果证明，
不能由 `ir_plan_complete=true` 或 dry-run 成功推出。

离线回归命令：

```powershell
& .runtime\venv314\Scripts\python.exe tools\diagnostics\verify_s3_plan.py
```

覆盖同名活动隔离、来源错配、缺 IR 拒绝兜底、不完整/无本地计划语义、全部
1,437 个导出文件的身份解析，以及协议 dry-run 不初始化 Campaign/设备。
协议集成测试使用假 Campaign 和 loader，验证导航先于原生 run、参数转发、
初始化/导航错误中止、入口包装恢复；测试不调用真实设备或启动弹窗观察线程。
