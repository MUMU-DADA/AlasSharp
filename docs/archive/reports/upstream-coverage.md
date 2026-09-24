# 上游自动化规则全量离线覆盖

由 `tools/diagnostics/verify_upstream_coverage.py` 生成；无设备动作、无账号配置。
生产流程继续由原生 `CampaignRun.load_campaign()`、`Campaign.run()`、`UI.ui_ensure()` 执行。
本报告证明当前源的加载、绑定及合成输入语义，不证明所有关卡通关或所有页面实机可达。

- 上游提交：运行时为无 Git 元数据的源快照，以实际内容哈希识别。
- 实际源文件：1824；路径/内容哈希清单总摘要：`4a56d2a8de5d61b8e7dac42cb172c78fface66b30d5a929657ed8257b0ed0db1`。
- 验证器 SHA-256：`0f0f0ca7eeeca02588300f37c5e067181d945304691b68ac422c2cc479ee0cd2`。
- 全部检查段执行：True；总体通过：False。

| 范围 | 当前结果 |
| --- | --- |
| 关卡原生加载/Config/MAP 声明/继承方法 | passed=1367；support_module=67；upstream_error=3 |
| 素材原生加载及四服字段 | passed=7172 |
| 页面四服合成正对照 | pass=208；skip=4 |
| 原生导航可达图对 | passed=2551 |
| 四服控件声明、识别及工厂合成循环 | passed=252 |
| 任务调度绑定及依赖导入 | configuration_group=11；passed=57 |

## 阻塞与失败

- `campaign.event_20200227_cn.c2`：ImportError: cannot import name 'C2' from 'module.campaign.assets' (<project>\.runtime\engine\module\campaign\assets.py)
- `campaign.event_20200227_cn.d3`：ImportError: cannot import name 'D3' from 'module.campaign.assets' (<project>\.runtime\engine\module\campaign\assets.py)
- `campaign.event_20200312_cn.sp3`：ImportError: cannot import name 'EVENT_20200312CN_SP3' from 'module.campaign.assets' (<project>\.runtime\engine\module\campaign\assets.py)

## 证据边界

- 辅助模块通过导入后的 `Campaign.MAP` 类型识别，不按文件名排除；源导入失败会令检查退出码为 1。
- MAP 静态声明与原生导入时的赋值和方法参数逐项对拍，覆盖格子/类引用、复制、声明顺序及原生调用；不执行 JSON 规则。
- 页面正对照使用模板画布；`Page(None)` 无可识别素材，明确跳过。
- 导航运行原生页面图和控制循环，识别与点击反馈为合成状态；共享活动入口假定目标活动可用。
- 控件按服务器在独立进程导入，保留导入时的服务器分支；普通实例和延迟属性调用原生识别，包含容器内控件。
- 任务内工厂检查包含 7 个声明、192 个四服合成场景；运行原生任务/控件循环，覆盖切换、原生附加处理、滚动到顶/分页到底及无滚动条，不证明真实点击或业务完成。
- 调度检查覆盖 Scheduler.Command 方法及其声明依赖；原生任务返回不等于领取、购买或目标完成。
- 错误中项目绝对路径替换为 `<project>`；不发布账号、帧或原始日志，不改变错误类型或判据。

复现：`python tools/diagnostics/verify_upstream_coverage.py`。
原始逐项报告在忽略目录 `.runtime/verification/upstream-coverage.json`，不进入提交。
