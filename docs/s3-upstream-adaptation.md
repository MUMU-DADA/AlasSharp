# S3 上游整体流程适配（2026-09-23）

适配原则：完整消费上游解析出的地图规则和原生执行流程，不在宿主增加逐地图阈值、坐标、寻敌或战斗分支。

## 根因

原宿主直接构造 `module.Campaign(cfg, device)`，遗漏上游
`CampaignRun.load_campaign()` 的 `deepcopy(config).merge(module.Config())`。
因此继承的识别阈值、边界颜色、地图能力等配置全部没有生效。
例如 1-4 导入 1-1 的 Config；不合并时失败帧报消失点退化，合并后同帧准确识别 21 格。
7-1 的现场帧也恢复舰队识别。问题不是要给各地图重新配参数。

原宿主还用手写入口、初始化及战斗循环替代上游 `run()`，容易漏掉
`ENTRANCE.area`、心情检查、自律分支和章节覆写的运行钩子。

## 当前生产链

1. 按完整模块名读 IR 并核对 `source`；不同活动的同名 `a1` 不混用。
2. 绑定上游 `Campaign_Name`、`Campaign_Event`，直接调用 `CampaignRun.load_campaign()`。
3. 按上游每次出击的顺序清设备记录、处理上一局仍在地图内的状态。
4. 调用该 Campaign 的 `ensure_campaign_ui()`，由其设置入口与难度。
5. 调用该实例的原生 `run()`；宿主只记录现有操作和结束来源。

移除了全局 scipy `brute(finish=None)`、检测阈值阶梯、放大重试、手写地图初始化恢复和
每轮额外 BOSS 扫描。既有 numpy/客户端 UI 兼容仍在；它们不包含按地图分派的规则。

首次相机纠偏还暴露一个上游通用空值缺陷：快速截图进入 0.35 秒等待窗口时，
`Camera.update` 会用未设置的 `prev_center_offset` 参与减法。
适配只在上游函数该比较前增加 `is not None` 短路；原等待时间、居中检查、滑动及重试逻辑不变。
未复制相机实现，也不依据地图名或尺寸分支。上游自带修复时跳过，源码结构发生不兼容变化时明确报错。

真跑的完整地图规则来自上游生成的 Python `MAP` / `Config` / `Campaign`。
IR JSON 是身份、计划摘要和校验信息，**当前不是独立 JSON 战斗解释器**。
导出器无法完整表达的嵌套方法仍由上游原生方法执行，不能把摘要逐条重放。

章节 `Config` 的导出已与战斗计划分开处理：导出器通过源码解析展开 `Config` 的相对导入、C3 继承和安全的常量/容器表达式，写入 `config`；`config_meta` 保留有效 MRO、每个字段的声明来源、Python 容器类型标签和未解析项；`Campaign` 类属性写入 `campaign.attributes`，不再与 `Config` 混在一起。当前快照中 1,375 个有效 `Config` 模块全部完整，`verify_export.py` 会把导出结果与源码解析结果逐字段对照，S3 离线预检还会与真实 `Config` 类的公开字段和值做独立复核。

全量原生导入对照中有 1,372 个模块逐字段通过；3 个历史活动模块无法导入，是上游 `module.campaign.assets` 缺少 `C2`、`D3` 和 `EVENT_20200312CN_SP3` 素材符号导致的 Campaign 导入错误，不是 Config 导出差异。这 3 个模块的静态 Config 仍由源码解析完整导出，待上游素材补齐后可再做原生对照。

已完成的功能不应改成从 JSON 读取运行时配置。S2 `map_detect`/`map_detect_trace` 继续调用上游 `_map_config(chapter)`，S3 初始化继续调用上游 `CampaignRun.load_campaign()`；JSON 配置仅用于 `show`、dry-run 的来源可见性和漂移校验。这样既能发现导出缺失，也不会让一份静态摘要取代上游的配置合并语义。

## 结果语义

`CampaignEnd` 仅表示出击结束。上游 `withdraw()` 也可能经 `handle_in_stage()`
抛出文本为 `In stage.` 的同一种异常，不能靠异常名或文本认定胜利。

`cleared=true` 需要观测到上游战斗结算成功，并从战斗结算链返回章节页；
撤退、失败战果、未知退出、操作限额、运行错误均不记为通关，CLI 返回非零。
威胁排除百分比是跨出击累积状态，不能证明本局成功。

运行错误会保存设备已有的原始 RGB 帧到 `data/s3_failures`，不额外截图覆盖现场。

## 验证

离线验证覆盖规则身份、继承配置、配置隔离、真实失败帧、原生 run 的执行顺序与覆写、
自律分支、撤退/战果/未知退出、入口失败中止、参数转发及 CLI dry-run。
测试调用上游真实方法；设备 I/O 使用替身。地图产品路径五张既有夹具全部通过。

实机记录（只记本次统一实现的结果）：

| 关卡 | 模式 | 结果 | 证据 |
| --- | --- | --- | --- |
| 1-4 | 默认章节规则 | 4 场战斗，218.2 秒，击败 BOSS 后回章节页，无撤退 | `data/s3_native_1_4.log`、`data/s3_after_native_1_4.png` |
| 2-1 | 先清小怪 | 清完 6 支小怪才进 BOSS，7 场、506.2 秒，`cleared=true` | `data/s3_native_2_1_clearall.log`；入口曾撤出上一局失败的 1-1，此次 2-1 没有撤退 |
| 1-1 | 默认章节规则 | 上游相机纠偏成功，2 场、84.3 秒，`cleared=true` | `data/s3_native_final_batch.log` |
| 1-4（最终复测） | 默认章节规则 | 同一进程接续 1-1，4 场、208.1 秒，`cleared=true`，整批退出码 0 | `data/s3_native_final_batch.log`、`data/s3_final_stage.png` |

最终构建 0 警告、0 错误。六组定向验收全部通过，汇总见 `data/s3_final_verification.log`。
1-1 → 1-4 连续运行全程没有人工操作和撤退；两次 1-4 均使用上游配置和原生运行流程。

新账号刚解锁第二章时出现第二舰队教学，已按界面引导完成。
这次一次性现场操作未加入地图执行逻辑。未实测的地图不据尺寸或 IR 分级判断支持与否。

### 2026-09-23 证据审计补齐

根因是 `audit_real_records.py` 只扫描旧控制台日志，遗漏了后来运行时产生的本局撤退工件，
导致运行时文档已记录撤退，而生成证据页仍显示缺口。现补充通用结构化归档消费：
复用 `sortie-result/1` 裁决，交叉核对单关结果、批次索引与会话日志，并校验归档文件完整性。
`tools/diagnostics/evidence/20260923T093800` 是既有产品运行的脱敏副本，保留原件/副本校验和；
它包含上游 `withdraw()` 调用链、撤退步骤和章节页观察结果，结论仍为 `withdrawn`、`cleared=false`。

影响仅限离线证据归档和审计，没有更改上游加载、章节配置、原生调度或结果合同。
上述四条成功结算证据继续保留；`verify_real_records.py` 覆盖篡改、文件缺失、dry-run 冒充真机、
包装/索引/会话与结果分歧，合同与架构检查继续执行。

同日活动 A1、A2、A3 的通用 `campaign_batch` 均取得成功结算，随后实时抓帧返回 `page_event`；
证据审计按章节族接受活动页，并保持抓帧、离图和章节关联条件。原始设备材料仍在本地忽略目录，
脱敏归档与反例见 `tools/diagnostics/evidence/20260923T163008/`、`20260923T171026/`、
`20260923T180804/` 和
`verify_real_records.py`。
共享导航的返回键兼容改为上游 `Device.adb_shell(['input', 'keyevent', '4'])`，因为当前上游
`Device` 不提供 `back()`；仅改变返回键调用，章节配置、地图规则和原生 `Campaign.run()` 未变。
`verify_device_back.py` 覆盖调用与失败透传，A1/A2/A3 只证明这三关，不能外推活动域全覆盖。

同日活动 A1/A2 的队列 dry-run 暴露了通用工件问题：连续战役任务的批次索引和
同名单关文件会被后续任务覆盖，报告只读取第一批；相对工件目录则会受宿主切换
进程工作目录影响。运行时现将路径基准固定在宿主启动前，按运行目录为批次和单关
分配唯一文件名，并汇总、核对每个任务引用的批次索引。单批仍使用 `index.json`。
这些修改只涉及证据保存和读取；上游章节 `Config`、`MAP`、入口准备、
`CampaignRun.load_campaign()`、原生 `Campaign.run()` 与 `sortie-result/1` 均未更改。

同日连续活动队列的第二次真机运行在 A1 第三场战后停于 S 级结算屏，
`execute_a_battle` 最终报 `GameStuckError: Wait too long`。失败帧只留在本地忽略目录
`data/mainline-device/generated-event-capture/artifacts/20260923T191826/`；入库夹具仅是
`BATTLE_STATUS_S` 固定检测区的 79×20 像素裁剪，不含账号或设备画面。
上游原生颜色比对在该帧为 11.99（默认阈值 10），原生固定区域模板相关系数为
0.917（默认阈值 0.85）；章节页和地图负样本的模板分均低于 0.85。
兼容垫片仅在原生战役执行期间启用：先保留上游颜色判据，默认阈值临界未命中时
要求颜色差仍小于默认阈值两倍，且上游素材的固定区域模板匹配也命中。
它不按章节、地图、页面、服务器或素材名称分支，不复制素材规则；显式传入的自定义
颜色阈值仍走上游原判断，执行结束或抛错即恢复原方法。
`verify_campaign_button_compat.py` 用上述脱敏裁剪及章节页、地图负样本验证判据和恢复。
这次失败仍记 `error`，不能用先前 A1/A2 成功覆盖。

兼容垫片随后以同一个生成计划真机复验：A1、A2 依次由原生 `Campaign.run()`
完成 S 级成功结算，`sortie-result/1` 均为 `cleared=true`、零违例；每关后紧接的
`account_state(capture=true)` 同会话识别 `page_event`、`in_map=false`。四任务队列
`succeeded`，两份原始单关工件审计均为 `consistent`。完整脱敏计划、两个批次索引、
任务、单关与会话工件见 `tools/diagnostics/evidence/20260923T194236/`；原始截图和
运行日志留在本地忽略目录。此结果只验证当前账号已解锁的两关和这条兼容路径，
不能推断其他地图、功能或战果画面均已覆盖。

## 使用

```powershell
# 仅查看本章的上游导出规则，不连接设备
.\src\Alas.DataTool\bin\Release\net10.0\alashub.exe campaign campaign.campaign_main.campaign_1_4

# 原生上游完整出击，默认最多 20 场战斗
.\src\Alas.DataTool\bin\Release\net10.0\alashub.exe campaign campaign.campaign_main.campaign_1_4 --run --allow-actions

# 上游全清分支：清完小怪再打 BOSS
.\src\Alas.DataTool\bin\Release\net10.0\alashub.exe campaign campaign.campaign_main.campaign_2_1 --run --allow-actions --clear-all
```

`--max-seconds` 默认 1500 秒，在上游操作边界检查，不强制打断正在执行的战斗。
`--max-rounds` 默认 20；显式限轮停止不算通关。
舰队由 `--fleet1` / `--fleet2` / `--submarine` 配置，默认 1 / 0 / 0。

```powershell
dotnet build src/Alas.DataTool/Alas.DataTool.csproj -c Release
.\.runtime\venv314\Scripts\python.exe tools/diagnostics/verify_all.py --only verify_s3_plan.py,verify_s3_upstream_loading.py,verify_s3_camera_compat.py,verify_campaign_button_compat.py,verify_s3_outcome.py,verify_dryrun_purity.py,verify_product_map.py
```
