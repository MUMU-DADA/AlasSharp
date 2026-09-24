# 上游规则与原生战役适配

生产路径完整消费上游规则，禁止逐地图、逐界面另建坐标、阈值、路线或流程。
本页描述当前边界；完整历史调查与实测记录见[适配归档](archive/history/s3-upstream-adaptation-20260924.md)。

## 生产调用链

1. 以完整 `campaign.<目录>.<模块>` 读取 IR 并校验 `source`，不混用同名活动章节。
2. 绑定 `Campaign_Name` 与 `Campaign_Event`，调用 `CampaignRun.load_campaign()` 合并章节及继承的 `Config`。
3. 清理设备 stuck/click 记录；上一局仍在地图内时先通过上游撤退，再导航。该清理撤退不参与本局通关判定。
4. 由上游 `ensure_campaign_ui()` 处理主线、困难、活动和档案入口，设置 `ENTRANCE`。
5. 调用实例原生 `Campaign.run()`，保留章节覆写、继承钩子、相机恢复与战斗调度。
6. 按[结果合同](result-contract.md)裁决并写入逐关工件，不从单个结束字段判断通关。

早期少行地图失败的通用根因是漏合并章节 `Config`，随后又发现手写入口/战斗循环遗漏上游流程。
现已移除阈值阶梯、画面放大、手写地图恢复和每轮额外 BOSS 扫描；不能把这些旧方案重新当成修法。

## 导出与视觉边界

- JSON 的 `config/config_meta` 保留有效字段、C3 继承、来源、类型和未解析项；`Campaign` 类属性独立保存。
- `plan_steps`、`semantic_trace` 和 IR 完整度是离线摘要，不是可逐条重放的战斗计划。
- 地图识别继续走上游 `_map_config(chapter)` 和 `module.map_detection.utils_assets.Assets`。
- 页面、按钮、OCR 和模板由 `tools/alas_vision.py` 解析上游对象；不得从 `assets.json` 重建运行时视觉规则。
- 截图按上游 RGB 语义传递，存 PNG 时正确转换通道。旧文件颜色错误不能证明 BOSS 素材失配。
- 正常未检出与识别执行故障分开，详见[任务域](tasks.md)。

导出快照曾完成 1,375 个 Config 静态解析、1,372 个原生对照；3 个历史活动因上游缺少素材符号无法导入。
这些是记录时的覆盖数字，当前覆盖以导出校验为准，不能据此宣称所有章节已实战验收。

## 保留的通用兼容

| 范围 | 根因与边界 |
| --- | --- |
| 相机恢复 | 上游首次等待时 `prev_center_offset` 可能为空；只补空值短路，保留原等待、居中、滑动与重试语义 |
| 战后按钮 | 颜色差临界未命中时，同时要求上游固定区域模板命中；仅在原生战役执行期间启用，结束恢复，自定义阈值仍沿用原判据 |
| 返回键 | 使用上游 `Device.adb_shell(['input','keyevent','4'])`，不新增页面分支 |

兼容变更必须有通用根因、上游对照、影响范围与回归证据；不能用单图成功外推整类地图。
详细失败帧和后续复验保留在归档，四条主线成功日志及活动脱敏证据仍由审计脚本核对。

## 使用与验收

```powershell
alashub campaign campaign.campaign_main.campaign_1_4
alashub campaign campaign.campaign_main.campaign_1_4 --run --allow-actions
alashub campaign campaign.campaign_main.campaign_2_1 --run --allow-actions --clear-all
```

第一条只读取规则；真跑默认最多 20 场、1500 秒，在上游操作边界检查限制。
舰队默认 `1 / 0 / 0`，由 `--fleet1 / --fleet2 / --submarine` 指定；撤退、超时、限轮和未知退出不算通关。

受影响专项回归：`verify_s3_plan.py`、`verify_s3_upstream_loading.py`、`verify_s3_camera_compat.py`、
`verify_campaign_button_compat.py`、`verify_s3_outcome.py`、`verify_dryrun_purity.py`、`verify_product_map.py`。
结果判据变化必须同步 Python/C# 并跑 `verify_result_contract.py`、`verify_architecture.py`。

真实结算与撤退结论见[结果审计](archive/reports/result-evidence.md)，当前未完成范围见[路线](architecture-roadmap.md)。
