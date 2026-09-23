# 项目本地协作规则

## 提交隐私边界

- 提交内容不得包含开发者隐私：姓名、个人账号、邮箱、凭据、私钥、本机用户目录或其他可识别个人的绝对路径。路径应从项目位置或环境配置推导。
- 原始运行日志、设备配置、截图和账号证据只保留在本地忽略目录。需要入库的证据先脱敏，并说明脱敏范围；不得修改结果判据或伪造运行事实。
- 提交前检查本轮改动和当前已跟踪文件。Git 历史只报告风险，不擅自改写历史。

## S3 战役：完整适配上游，禁止逐地图补丁

这是用户明确要求，后续修改必须严格遵守：**不要走针对每个地图做适配的方向。**

- 目标是完整消费上游解析出的地图规则，获得与上游一致的过图行为。上游已经实现的加载、配置合并、继承、钩子、相机恢复和战斗调度，应完整复用或忠实迁移。
- 某张地图失败时，先检查整体调用链和状态是否遗漏，尤其是章节 `Config` 合并、`MAP` 规则、入口准备和原生 `Campaign.run()`；不能先为该地图增加特殊处理。
- 禁止在适配层按地图名称、编号、尺寸或少数地图分组，另写专用阈值、坐标、机位、路线、寻敌或战斗分支来绕过问题。
- 上游规则本身包含的地图差异必须完整读取并保留；禁止在本项目的适配层重复维护一份地图特例表。
- 确有运行时或客户端兼容问题时，必须用证据定位通用根因，作最小修复并保留上游语义，不得包装成逐地图兜底。
- 测试可以使用具体地图和现场截图，但不能据单张图成功或失败推断整类地图。通关必须以真实成功结算验证，撤退不算通关。

本轮根因、实现边界和验证证据见 [S3 上游整体流程适配](docs/s3-upstream-adaptation.md)。

## 已完善能力：修改前必须保持边界

以下两组能力已经完成整体适配并通过对应验收，视为本项目的稳定基线。后续排查新问题时，不能擅自回退、简化、替换其调用链，也不能因为单张地图或单次现场结果另写一套旁路逻辑：

- 上一个提交 `bbb4ffb`（`fix(s3): 补齐上游完整关卡流程并禁止逐地图适配`）已经完成 S3 上游流程：章节 `Config` 合并、`MAP` 规则加载、入口准备、上一局撤退清理、原生 `Campaign.run()`、继承钩子、相机恢复兼容，以及成功结算/撤退/失败的结果区分。生产路径必须继续复用 `CampaignRun.load_campaign()` 和原生运行调度。
- 提交 `0827fae`（`fix(export): 完善上游 Config 导出与校验`）已经完善章节 `Config` 导出：相对导入、别名、C3 继承、常量表达式、嵌套容器类型、字段来源和未解析项均由通用导出器处理；`Campaign` 类属性与 `Config` 分开保存；静态导出、原生对照和 C# 验收均覆盖这些内容。

后续修改涉及上述范围时必须遵守：

- 先用完整调用链和可复现证据定位通用根因，再做最小修复；不得按地图名称、编号、尺寸或少数案例增加阈值、坐标、机位、路线、寻敌或战斗分支。
- JSON 导出配置用于离线展示、来源追踪和漂移校验；地图识别和实战必须继续使用上游 `_map_config(chapter)` 与 `CampaignRun.load_campaign()`，不得把静态摘要改成运行时配置源。
- 修改加载、导出、结果判定或兼容垫片时，必须同步更新相关回归检查和 `docs/s3-upstream-adaptation.md`，并保留已有成功结算证据；不能以撤退、超时或单张截图成功代替通关验证。
- 如果确实需要改变已完善能力，提交说明必须明确根因、影响边界、保持的上游语义和新增验证；没有这些证据不得改动稳定实现。

## 导出素材规则：使用模块与完整性边界

导出的素材规则有明确的离线消费范围；运行时视觉识别有独立的上游对象解析路径。后续修改不能混用两条路径，也不能遗漏已有消费者：

- `data/assets.json`、`data/schema/assets.schema.json` 和 `manifest.json` 中的素材统计、来源及哈希属于离线导出契约。`src/Alas.Core/UpstreamData.cs` 的 `Catalog.Open()` 与 `src/Alas.Core/UpstreamModels.cs` 的 `AssetCatalog` / `AssetBinding` 负责加载和建模；`src/Alas.DataTool/Program.cs` 的 `alashub verify`、`tools/verify_export.py`、`tools/make_imaging_fixture.py`、`tools/make_matching_fixture.py` 负责完整性校验和夹具生成；`tools/export_upstream_data.py`、`tools/sync_all.py`、`tools/sync_upstream_assets.py` 负责生成、契约校验和上游素材快照同步。修改素材导出结构时必须逐项检查这些模块，不能只更新其中一个调用点。
- 运行时页面、按钮、模板和 OCR 素材必须继续由 `tools/alas_vision.py` 的 `_resolve()` 等操作从上游 `module.<module>.assets` 解析。`src/Alas.Core/Vision/IVisionEngine.cs` 只传递素材 id 和调用参数，`src/Alas.Core/Navigation/PageNavigator.cs`、`src/Alas.DataTool/DeviceCheck.cs` 等调用方通过该视觉宿主消费上游规则；不得从 `assets.json` 重建运行时视觉对象，不得复制维护另一份素材规则表，也不得绕开服务器变体和上游匹配语义。
- 地图识别必须使用 `tools/alas_vision.py` 的 `map_detection_assets` 路径和上游 `module.map_detection.utils_assets.Assets`，包括其遮罩、瓦片模板和检测区域；`src/Alas.Core/MapDetection/MapDetection.cs` 只消费视觉宿主返回的地图识别配置。不得把 `assets.json` 中的普通 UI 绑定误当成地图识别素材，也不得在 C# 侧按地图补写坐标、模板或阈值。
- 导出器必须完整保留素材的所属模块、唯一 id、kind、服务器变体、`area`、`button`、`color`、`file` 和来源信息；不能因为字段暂时没有被某个单一调用点使用就省略。任何模块遗漏、服务器变体丢失、文件引用错误或字段错误映射都必须由导出校验失败暴露，不能静默回退。
- 调整素材导出字段或语义时，必须同步更新 JSON Schema、C# 数据模型、`alashub verify`、`tools/verify_export.py`、两个夹具生成脚本以及 manifest / 同步校验链，并验证所有消费者。不得按地图、页面、服务器或少数素材名称增加专用坐标、阈值或 fallback；素材失败时先检查模块来源、服务器变体、文件路径和上游调用链。

## 结果判定：只能走 sortie-result/1 合同

Frozen：结论口径写在 `docs/result-contract.md`，生产方 `tools/sortie_contract.py`、消费方
`src/Alas.Core/Campaign/SortieResult.cs` 各实现一份，`tools/diagnostics/verify_result_contract.py` 逐例对拍。
改动结果判定时必须遵守：

- 不得在任何调用点用单个字段拼通关结论；`CampaignEnd` 只表示"出击结束"（撤退也抛它），不能单独证明通关。
- 改词表、不变量或违例码，必须**同时**改 Python 与 C# 两侧，并跑
  `python tools/diagnostics/verify_result_contract.py`（35 例）与
  `python tools/diagnostics/verify_architecture.py`（词表/违例码漂移会直接失败）。
- `docs/result-evidence.md` 由 `tools/diagnostics/audit_real_records.py` 从 `data/*.log` 重建，不手写；
  发现"声称结果与原始证据对不上"时，先修证据链或补真机验证，不得改小核对规则来让它变绿。
- 失败必须可定位：`error` 带调用栈尾部，存下来的失败帧必须登记在 `failure_frames`。

## 运行时边界：编排只在 Alas.Core/Runtime

R1 起，业务编排（跑哪几关、怎么判成功、什么时候停、工件写到哪）属于
`src/Alas.Core/Runtime/`。改动时必须遵守：

- `alashub`（`src/Alas.DataTool/Program.cs`）只解析参数、调用运行时、排版输出；
  不得在命令分支里直接调用 `RunCampaignPlan`、自己驱动宿主或复制一套状态机
  （`verify_architecture.py` 会静态报错）。
- 会话（宿主 + 设备后端）一个进程只起一次；新增任务域时复用 `AlasSession`，不要另开宿主。
- 取消在**关卡边界**生效，不打断正在执行的上游出击；失败即停是默认行为，
  需要跑完时必须显式开启并说明理由。
- 每个关卡（含失败、被跳过）都要有工件；工件按运行批次分目录，见 `docs/runtime.md`。

## 任务域边界：新域先接通用任务模型

R2 起，新业务域必须实现 `Alas.Core/Tasks/ITaskRunner`（`Kind` / `Preconditions` / `Run`），
再通过 `TaskQueue` 调度；细节见 `docs/tasks.md`。改动时必须遵守：

- **"没跑"与"跑失败"分开**：前置条件不满足记 `Skipped`（`required` 的才算失败），
  不许为了报告好看把两者合并。
- 任务结论只用 `TaskOutcome` 的五个值；域内不许发明自己的成功词。
- 任务输入模型放在 `TaskRequest.Input`（JSON），由该域的 runner 校验与消费；
  通用层不解释业务字段。
- **禁止按地图/编号/服务器分支**：战役域只把 `chapters` 翻译成上游调用参数，
  章节差异全部由上游 `MAP`/`Config`/`Campaign.run()` 消费。
- 每个任务都要有工件（含失败与被跳过的），队列写 `queue.json` 与 `state.json`；
  断点续跑只能跳过"已完成"的任务，并如实标注跳过原因。
- CLI 只解析队列文件与公共参数（`ParseRunFlags`），不得在命令分支里解释任务内容。

## 后续整体迁移方向：R0-R5 路线与长期不可变边界

完整路线、阶段门槛和依赖见 [整体迁移路线 v2 与不可变边界](docs/architecture-roadmap.md)。后续开发必须按 **R0 真值与证据 → R1 常驻运行时 → R2 任务域垂直切片 → R3 原生钩子迁移 → R4 前端 → R5 宿主替换评估** 推进。不能跳过前置阶段，也不能把“独立 C# 关卡引擎”提前当成当前交付目标。

以下约束视为本地协作规范，明确不能变动：

- 生产战役继续走上游 `CampaignRun.load_campaign()`、章节 `Config` 合并和原生 `Campaign.run()`；JSON 只用于离线展示、溯源和漂移校验。
- 视觉、页面和地图识别继续通过上游对象和 `IVisionEngine`；不得从 `assets.json` 重建运行时规则，也不得复制素材或地图特例表。
- 任务域按通用状态和依赖建设，不能按地图名称、编号、尺寸、服务器或少数案例增加坐标、阈值、机位、路线、寻敌或战斗分支。
- C# 图像代码仅作参考和对拍；没有逐项对拍、性能基准和真实产品路径证据，不能替换上游宿主。
- `CampaignEnd`、威胁百分比、单张截图、撤退、超时和未知退出不能单独证明通关；必须有成功结算并返回章节页的证据。
- 新业务域先进入 `Alas.Core` 的通用任务/状态模型，再接入 CLI 或前端；禁止在命令分支复制业务状态机。
- 修改上述边界时，提交必须写明通用根因、上游语义、影响范围、阶段门槛和新增验证，并通过 `python tools/diagnostics/verify_architecture.py`。
