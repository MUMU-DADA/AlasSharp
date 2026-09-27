# Engine 任务队列与业务域

产品任务位于 `Alas.Engine/Tasks`。新域实现 ITaskRunner 的 Kind、RequiresActions、Validate、Preconditions 和 RunAsync，由 TaskQueue 注册；Server、CLI 和 UI 不解释域内输入，也不复制状态机。旧 Core 队列已退役，旧任务名与 Python runner 仅留[历史对照](archive/history/core-tasks.md)。

## 请求与结果

CLI `Alas.Engine.Cli run --queue <文件>` 读取任务数组；控制 API 的 queue.tasks 使用同一数组：

```json
[
  {"id":"observe","kind":"observe","required":true,"timeoutSeconds":30},
  {"id":"map","kind":"map_observe","input":{"campaign":"campaign_main/campaign_1_1"},"dependsOn":["observe"],"timeoutSeconds":30}
]
```

id 必须唯一且为 1–64 个 ASCII 字母、数字、下划线或连字符。依赖只引用前序任务。input 由 runner 校验字段、类型、范围；未知规则和任务明确拒绝。JSON 不编码条件、循环、继承或可执行步骤。

| TaskOutcome | 含义 |
| --- | --- |
| succeeded | 满足该域完成条件，战役必须有冻结合同证据 |
| failed | 已执行但失败、超时或被中断 |
| skipped | 前置条件、依赖、边界停止或已验证断点导致未执行 |
| refused | 未支持的任务/规则、无动作授权或输入无效 |
| dry_run | 已离线验证输入，不操作设备，不写成功断点 |

required 的前置条件缺失计为队列失败；正常边界停止不计业务失败。失败即停为默认，显式 continue-on-failure 可继续独立任务。每任务都有 request、result 和动作工件；断点身份、停止与报告见[运行时](runtime.md)。

## 当前 Engine runner

| kind | 行为与边界 |
| --- | --- |
| observe | 单帧页面识别，只读 |
| navigate | C# 页面图、弹窗和设备恢复，需动作授权 |
| data_key | C# 资料密钥页面流程、OCR 与数值处理 |
| map_observe | 已编译规则的局部格子/几何观测，不证明全图定位或结算 |
| campaign_stages | 章节入口 OCR，只读 |
| campaign_select | 选择已编译章节，止于地图准备 |
| campaign_fleet_prepare | 进入/设置舰队页面，止于开战前 |
| campaign_run | 同一 C# 会话完成选关、舰队、进图与地图执行；仅经结果合同确认才计 cleared |
| campaign_resume | 从新进入的图中执行，因未验证选关身份永不记 cleared |

具体输入由各 runner.Validate 定义。已编译地图声明不代表对应章节钩子和配置已迁完；可执行 RuleCatalog 仍只有已迁移的规则。战役 fleet1/fleet2/submarine 必须明确；情绪计算未迁完，当前须明确 emotionMode=ignore。campaign_run 的可选 fleet1Formation/fleet2Formation 为 line_ahead、double_line（默认）或 diamond，非法值在操作设备前拒绝；章节配置覆盖保留并传入地图执行。fleetOrder 接收 fleet1_mob_fleet2_boss（默认）、fleet1_boss_fleet2_mob、fleet1_all_fleet2_standby、fleet1_standby_fleet2_all。初始舰队选择/反转和对应阵型已接入，章节覆盖禁用二队时不反转；战中双舰队调度与潜艇实战仍有缺口。禁止按地图/页面补特例来绕过缺失语义。

`campaign_run` / `campaign_resume` 的队列输入可设置 `hpControl: { "lowHpRetreat": true, "threshold": 0.3, "balanceWeight": "1000, 1000, 1000" }`。缺省遵循原生配置：关闭低血量撤退、阈值 0.3、等权重。权重支持中文逗号和单个整数的广播；拒绝负权重、全零、错误数量及非整数。阈值在 0–1 之间；这是加权血量，首次确认的有船槽位不会因战损归零而变为空槽。进图和战后返回地图时均读取六槽血量；工件的 `health` 保存原始值、加权值、首次槽位掩码和帧号。低血量触发撤退后须有退出动作和新帧的章节页确认，才输出 `withdrawal` 与 `sortie.outcome=withdrawn`，任务仍非成功。换位和维修须分别通过下述开关启用。

`hpControl` 另支持 `balance`（默认 false）、`balanceThreshold`（0.2）、`emergencyRepair`（false）、`repairSingleThreshold`（0.3）和 `repairMultiThreshold`（0.6）；所有阈值要求 0–1 的有限值。按照上游，维修需要同时开启 `balance` 和 `emergencyRepair`；舰队锁不禁维修，但会禁止前排换位。`campaign_run` 须另设 `fleetLock=false` 才能执行换位，`campaign_resume` 仍沿用默认舰队锁。换位按加权前排 HP 决定；维修忽略 ≤0.001 的空槽，要求前后排都有有效 HP，任一有效槽低于单槽阈值或任一排最高 HP 低于整排阈值才使用。当前 ADB 操作是原生滑动加目标补点组合，尚未真机验证换位结果。`combat-health.json` 保存每场准备的换位输入完成状态、维修点击及依据血量帧、稳定等待是否成功；失败动作另见 `actions.json`。动作完成不代表换位、维修到账或通关，权威 HP 只由之后的地图读数更新。

两个战役任务还支持 `reachLevel` 非负整数，默认 0 关闭等级观测；开启后要求配置 OCR 模型目录。`levels` 工件保存同帧六槽读数、战前基线及 `reachLevelTriggered`，遵循原生 `after >= limit > before > 0`，且只接受升一级或新等级低于 35 的跨阈值变化。初始读数不会触发停止，后续读数不会清除已触发标记；读取失败或六槽帧号不一致不发布部分状态。当前任务只执行一次出击，`reachLevel` 不会中途撤退，也不影响之后显式排列的独立任务。完整 CampaignRun 连续出击及其到级禁用调度仍待迁移，不能把标记输出当成该调度已完成。等级读数、等级达到和 LV32 标记都不能证明通关。

CLI 公共参数为 --adb、--serial、--server、--assets、--python、--artifacts；OCR 任务使用 --models，动作另需 --allow-actions 和 --package。`campaign --chapter <规则列表>` 默认 dry-run，使用 --run --allow-actions 才执行；--fleet1-formation/--fleet2-formation 选择上述阵型，--fleet-order 指定舰队顺序。运行前读取 --help 核对参数。

## 待完成能力与验证

旧周期任务、连续调度、工具、大世界和统计/策略尚未迁移为 Engine runner，旧 task 名不会自动映射或回退到 Python。它们是完整重写的剩余范围，不能以接口返回不可用当作完成。

Engine 离线输入、状态、队列与规则对照在 tests/Alas.Engine.Tests；需使用 dotnet run 执行。设备/页面观测成功只证明对应观测；实机通关必须由新 Engine 获得真实成功结算并返回章节页。旧 Core 证据和回归不能替代新执行路径验收。统一完成度见[路线](architecture-roadmap.md)。
