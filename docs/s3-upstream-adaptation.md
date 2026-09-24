# 上游规则与原生战役适配

生产路径完整消费上游规则，禁止逐地图、逐界面另建坐标、阈值、路线或流程。
本页描述当前边界；完整历史调查与实测记录见[适配归档](archive/history/s3-upstream-adaptation-20260924.md)。

## 生产调用链

1. 以完整 `campaign.<目录>.<模块>` 读取 IR 并校验 `source`，不混用同名活动章节。真跑在创建设备前导入原生模块并检查 `Campaign.MAP`；源依赖缺失和辅助模块明确拒绝，dry-run 仍不导入业务模块。
2. 绑定 `Campaign_Name` 与 `Campaign_Event`，调用 `CampaignRun.load_campaign()` 合并章节及继承的 `Config`。
3. 清理设备 stuck/click 记录；上一局仍在地图内时先通过上游撤退，再导航。该清理撤退不参与本局通关判定。
4. 由上游 `ensure_campaign_ui()` 处理主线、困难、活动和档案入口，设置 `ENTRANCE`。
5. 调用实例原生 `Campaign.run()`，保留章节覆写、继承钩子、相机恢复与战斗调度。
6. 按[结果合同](result-contract.md)裁决并写入逐关工件，不从单个结束字段判断通关。

早期少行地图失败的通用根因是漏合并章节 `Config`，随后又发现手写入口/战斗循环遗漏上游流程。
现已移除阈值阶梯、画面放大、手写地图恢复和每轮额外 BOSS 扫描；不能把这些旧方案重新当成修法。

## 导出与视觉边界

- JSON 的 `config/config_meta` 保留有效字段、C3 继承、来源、类型和未解析项；`Campaign` 类属性独立保存。
- `map/map_meta` 导出 MAP 源声明、符号格子与类引用、嵌套容器类型、复制来源和原生方法调用参数；`derived_from` 是元数据，不混入地图字段。导出器 2.1.0 用通用符号解析替代未知名称静默丢弃及最多五轮复制补丁；未知/条件/嵌套修改明确记为不完整。
  当前 1,437 个模块包含 1,370 份 MAP 声明、8,322 个赋值字段和 22 个原生方法调用；格子引用包含堡垒和弹跳敌人等上游机制，类引用包含复制后的继承来源。静态 JSON 不执行这些声明或替换原生 setter。
  Schema、C# 模型、双端完整性校验、索引/manifest 与同步漂移检查共同消费新契约；原生导入审计在实际构造、setter、复制和方法调用处记录参数，再逐字段对拍。源导入失败仍失败，不能据静态完整宣称实战可用。
- `plan_steps`、`semantic_trace` 和 IR 完整度是离线摘要，不是可逐条重放的战斗计划。
- 地图识别继续走上游 `_map_config(chapter)` 和 `module.map_detection.utils_assets.Assets`。
- 页面、按钮、OCR 和模板由 `tools/alas_vision.py` 解析上游对象；不得从 `assets.json` 重建运行时视觉规则。
- 页面单帧判定直接调用 `UI.ui_page_appear()` 与 `ModuleBase.appear()`，保留短路、offset 和服务器分支；页面图只保留原生链接的按钮对象，不按命名推测替代素材。服务器切换调用上游 `set_server()` 释放全部资源缓存。
- 控件目录从上游构造声明、导入别名和继承关系发现，包含模块实例、延迟属性与任务内工厂。实例及属性由原生类构造；工厂留给原生任务上下文调用，不把“构造成功”当成识别命中。
- 延迟属性中的 Scroll 与列表/元组/字典内原生控件使用同一只读识别路径；无滚动条不伪造位置，部分识别异常保留明细并阻止整体命中。四服验收采用独立进程，避免导入时固定的服务器规则串用；任务内工厂用有界合成反馈运行原生循环，新声明没有对应执行证据即失败。
- 素材元数据分别遵循 Button 和 Template 的原生图像加载方式，包括 GIF；缺少服务器变体、字段或源文件时校验失败，不回退到其他服。
- 嵌套模块素材 id 保留导出器的目录分隔形式，宿主按最后一个 `/` 分离对象名并转换 Python 模块路径；反向页面图使用同一形式，旧点分模块 id 仍可解析。合成嵌套模块验证原生对象身份，导出模型、校验与夹具仍消费原契约字段，不重建视觉对象。
- 截图按上游 RGB 语义传递，存 PNG 时正确转换通道。旧文件颜色错误不能证明 BOSS 素材失配。
- 通用 `ocr` 探针保留上游 `Ocr` 的 RGB 字色、阈值、字符白名单与语言选择；缺省/null 参数不覆盖原生默认值。
  `IVisionEngine.Ocr` 的 `letter` 是三个整数的 RGB 数组，字符白名单使用独立 `alphabet`；旧字符串字色用法会明确拒绝。
- 正常未检出与识别执行故障分开，详见[任务域](tasks.md)。

全量对照及源缺陷由[自动化规则覆盖](archive/reports/upstream-coverage.md)和[地图模型对照](archive/reports/map-ir.md)生成。
静态 Config 数包含辅助模块，不能当作可运行关卡数；原生导入失败会令总体检查失败，不能改成跳过或以地图特例修补。
加载/绑定、模板正对照、原生导航合成状态和真实游戏结果分别记账，不能据此宣称所有章节已实战验收。

已修复的通用根因包括：页面判据的局部重写偏离上游短路/offset 语义、服务器切换只清理局部素材缓存、Template 被误用 Button 的初始化 API，以及控件扫描遗漏子类和延迟构造。
这些修复经四服原生对象和合成输入对照；没有新增真机通关结论，已有成功结算证据保持原样。

入口复核还发现旧的固定红色区域/坐标撤退线程仍覆写 `enter_map()`，会绕过共享设备与原生弹窗流程，吞掉识别错误并可能在退出等待超时后继续点击。
现已删除该旁路及无生产调用的 C# 自建导航点击算法；保留原生上一局撤退、入口准备、`Campaign.run()`、相机及战斗判据兼容。
[入口对照](archive/reports/native-campaign-entry.md)记录原生处理器、存盘帧与内容哈希。历史残留出击弹窗仍无法由当前原生判据确认，属于未解决的客户端状态；删除旁路不等于完成该状态的适配。
未以新地图或页面坐标补丁绕过；需要原生流程现场复验。冻结结果词表保留历史 `abort_unfinished` 步骤名，原始工件不改写。

旧控件与页面诊断中的逐页固定坐标、推测素材变体和点击顺序也已退役；`verify_controls.py` 只读归档历史记录并保留原始哈希与判定，`verify_page(s).py` 仅返回迁移提示，不再连接设备。新动作通过队列调用原生任务。只读 `account_state` 中的页面、地图判据或配置读取异常向任务层传播为失败，不将部分读取当作完整状态。

全量验收发现 dry-run 在关卡边界取消时，汇总把被跳过关卡的空结果误判为读取失败。批次先保留实际读取/合同错误，再判断取消；任务层沿用 `skipped/cancelled`，不修改 `sortie-result/1` 或原生实战流程。确定性边界用例与控制服务停止/关闭回归均覆盖该路径。

连续调度验收还暴露 Windows 快照替换的 `PermissionError`：短暂文件占用会令收尾异常逸出。
工件写入现对该平台的共享/访问冲突有界重试，持续失败保留旧快照、返回调用栈并释放日志资源；
原生调度调用、任务顺序和失败重试策略不变。真实文件占用与确定性收尾失败用例验证通用 IO 边界，
不据此增加真实任务成功结论。

## 保留的通用兼容

| 范围 | 根因与边界 |
| --- | --- |
| 相机恢复 | 上游首次等待时 `prev_center_offset` 可能为空；只补空值短路，保留原等待、居中、滑动与重试语义 |
| 战后按钮 | 颜色差临界未命中时，同时要求上游固定区域模板命中；仅在原生战役执行期间启用，结束恢复，自定义阈值仍沿用原判据 |
| 返回键 | 使用上游 `Device.adb_shell(['input','keyevent','4'])`，不新增页面分支 |
| 部署存储 | 旧部署模板会丢弃新增 UI 字段；宿主分发前装配通用文件事务垫片，保留上游读取、默认值和重定向，写入仅合并实际修改并与 Core 共用锁；不改变战役加载、导航或结果合同 |

兼容变更必须有通用根因、上游对照、影响范围与回归证据；不能用单图成功外推整类地图。
详细失败帧和后续复验保留在归档，四条主线成功日志及活动脱敏证据仍由审计脚本核对。

共享设备宿主新增显式实例配置入口，修复周期任务使用默认账号初始化包名/服务器的问题。
无参 S3 路径仍沿用 `_map_config()` 和原初始化语义；同一设备会话固定实例及设备配置，避免只替换 `device.config` 却保留旧连接缓存。
`verify_instance_device_binding.py` 覆盖首次绑定、跨任务复用、不相容拒绝及异常恢复；`verify_s3_upstream_loading.py` 保持加载链验收。
本次为离线修复，未新增真机通关结论，已有真实结算证据保留。
部署存储由 `verify_deploy_storage.py` 使用两个上游原始类及合成文件验收；不读取账号、不操作设备。原有成功结算证据继续保留，文件事务测试不代替实战。

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

免设备的全量规则入口：

```powershell
python tools/verify_export.py
python tools/diagnostics/verify_map_export.py
alashub verify
alashub map-ir
python tools/diagnostics/verify_map_ir.py
python tools/diagnostics/verify_upstream_coverage.py
python tools/sync_all.py --verify
```

`sync_all --verify` 在检查模式与更新模式都执行离线验收；缺脚本、导入故障、网格差异和源漂移均非零退出。
失败检查会显示具体步骤；逐项原始输出与汇总保存在忽略目录 `.runtime/verification/verify_all/`，不提交本机日志。
完整性反例由 `verify_export_integrity.py` 同时检查 Python/C#，同步与失败分类由 `verify_validation_contracts.py` 检查。
图像夹具按 `--repo` / `ALAS_REPO` / 项目运行时选源，调用方的相对 `--data` 和 `--out` 不因上游切换工作目录而改变。

2026-09-25 完整离线检查：63 项通过，7 项设备步骤跳过；`verify_map_ir.py` 和
`verify_upstream_coverage.py` 均因相同的 3 个上游历史素材依赖缺失而失败。严格素材同步通过，
7,436 个快照文件与源一致；Release 构建 0 警告、0 错误，独立调度生命周期检查通过。
这组结果不能记为整体通过，也不增加真实通关或界面操作完成证据。

同日控件补验：修复延迟 Scroll/容器控件仅输出描述而未识别的问题，10 项发现与错误合同回归通过；
四服 252 项控件检查及 7 个工厂的 192 个合成场景通过。全量规则检查重跑后仍仅报告上述 3 个上游缺失依赖；
页面原生判据 6 项、账号只读状态、架构与隐私检查通过。此补验未重复无关构建或设备步骤。

原生任务入口补验发现两处通用状态问题：全新宿主未先执行战役/地图探针时，周期任务、连续调度和独立工具缺少
NumPy 生成器与空 Points 的兼容初始化；上一关显式 `clear_all` 还可能影响后续原生任务。三类入口现在在授权之后
使用同一运行上下文安装原有数值兼容、暂时关闭上一关的显式全清覆盖，并在返回或异常后恢复。上游任务配置、选择、
调度与地图/界面规则不变；没有扩大现有客户端阈值垫片的适用范围。
`verify_native_runtime_compat.py` 在三个新进程中复现原生线交点计算，并验证拒绝路径零初始化、正常/异常恢复；
周期任务、独立工具、连续调度、14 项实例绑定、Core 调度生命周期、20 项战役计划及 35 项结果合同通过。

OCR 参数复核发现通用探针把 `letter` 误作字符白名单、缺省时传空元组，并遗漏 `threshold/alphabet`。
修复仅恢复上游构造参数：预处理、模型和原生任务内 OCR 对象不变，没有页面/服务器补丁或素材导出改动。
`verify_native_ocr.py` 的 7 组回归用合成 RGB 图逐像素对照原生裁剪/预处理，覆盖四服语言选择、空白名单、
参数错误与模型异常；只替换推理端点。独立 C# 探针核对真实协议序列化，旧接口没有仓库内生产调用者。
本项不证明模型识别准确率或真实游戏业务完成；既有成功结算和原始失败记录不改写。

真实结算与撤退结论见[结果审计](archive/reports/result-evidence.md)，当前未完成范围见[路线](architecture-roadmap.md)。
