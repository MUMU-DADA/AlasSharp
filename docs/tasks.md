# Engine 任务队列与业务域

产品任务位于 `Alas.Engine/Tasks`。新域实现 ITaskRunner 的 Kind、RequiresActions、Validate、Preconditions 和 RunAsync，由 TaskQueue 注册；Server、CLI 和 UI 不解释域内输入，也不复制状态机。旧 Core 队列已退役，旧任务名与 Python runner 仅留[历史对照](archive/history/core-tasks.md)。

当前可执行战役规则为前十二章四十八关（1-1 至 12-4），使用各自编译的 Config、MAP 和钩子。第二、三章显式使用逻辑 BOSS 一队，保留原生道路清理、可达性、默认战斗分派及经验页覆写；3-1 至 3-3 启用航母谜题，3-4 保留关闭。二队推进/救援属于公共 Engine 操作，按角色门控执行，不在任务层解释章节步骤。第四、五章的 BOSS 观察、道路组和覆盖层参数由编译规则执行，不在队列 JSON 中编码步骤。第六章接入二队占位、后期谜题和 6-4 战前补给；占位完成本身不计战斗，目标被另一舰队占用的交互仍拒绝执行。第七章规则接通规模筛选、谜题忽略及二队指定拾取，7-1 先切 BOSS 舰队再清路，7-4 按实际舰队角色补给；这些条件由 C# 规则执行。第八章加入首段路障、拾取后谜题计数门控与章节相机倍率，保留原生道路分组和直接 brute 清路顺序；仍由编译规则与公共 Engine 操作执行。第九章加入 homography storage、运行时权重替换、二队位置门控和 BOSS 舰队补给，所有变化都在 C# CampaignState 与规则钩子中执行。第十章保留独立道路组、二队推进/占位和 BOSS 前的实际/潜在路障顺序；10-3 只在两类路障均未触发战斗后拾取谜题。第十一章保留无条件清路、BOSS 不可达时直接返回和指定镜头恢复；第十二章继承统一检测/倍率配置，并保留 12-4 第三战起补给的顺序。配置覆盖由通用检测/相机工厂消费，任务 JSON 不承载执行计划。其余 MAP 即使已有类型化声明，也不能越过 RuleCatalog 作为已迁移战役执行；上述四十八关的规则/组合离线验证不等于新 Engine 实机通关。

## 请求与结果

章节规则的 MysteryHasCarrier 开启时，增援动画经 C# 处理，确认到达后按原生最后谜题类型决定 carrier 扫描；这不是任务 JSON 的业务开关。正常战役结果的 carrierEncounters 保存逻辑舰队、目的格、动画等待依据帧和退出原因，carrierScans 保存观测增援计数、新敌人格及扫描记录。扫描预测不证明增援已被击败；等待/扫描失败终止本局，已确认状态不回滚。未确认到达仍可有动画观察，不能据此推断位置或通关；异常退出目前没有这两项独立工件，沿用异常与动作记录。可执行章节范围仍由上述 RuleCatalog 控制。

campaign_run 与 campaign_resume 支持 ambushEvade（布尔，默认 true；null 和其他类型拒绝），false 表示按上游主动迎击。伏击处理使用本次有效配置，与章节差异无关。正常战役返回中的 ambushEncounters 记录途中伏击的逻辑舰队、目的格、帧号、点击数、回避信息、独立战果及是否读取舰队状态；失败结果也保留已观察的伏击，但列表不代表目的格已到达。异常退出目前仍只沿用异常/动作工件。伏击不增加地图战斗数或消耗地图弹药，单独伏击后返回章节页不算通关；成功任务仍要求真实目标战斗的完整结算链。

心情控制由 Engine 的 C# 规则、会话和配置事务执行。campaign_run 缺省 calculate；campaign_resume 与 campaign 命令保留 ignore 缺省值。campaign_run 支持 calculate、calculate_ignore、ignore、nothing；前两者估算并写回两队心情，包含 ignore 的模式确认低心情提示。campaign_resume 没有进图前的双倍书观测，仅支持 ignore / nothing；计算模式在设备动作前拒绝，不能把未知消耗倍率当作单倍。值是原生算法估算，不是 OCR 实测。

计算模式要求会话绑定配置数据根目录与实例：CLI run/campaign 使用 --config-root 和 --instance；Server/桌面直接传入选定实例。campaign 使用 --emotion-mode 与 --config-task，队列输入使用 emotionMode 与 configTask（缺省 Main）。恢复策略、誓约、控制阈值、当前值和记录时间读取实例配置，不在任务 JSON 复制初值。每次读写核对实例串号、游戏包与素材服务器；不同实例的队列请求、配置冲突和非法字段均拒绝。配置目录只提供数据，不加载 Python 业务。

进图前按两队预期战斗次数计算恢复时间，不足则保存两队记录及该配置任务的 Scheduler.NextRun，返回 Skipped / emotion_recovery_required；没有设备动作，也不计完成。required 任务仍按队列合同使本次批次失败，默认停止后续任务。当前不会自动重调度；再次运行或断点续跑会重做该任务。已入图的 resume 不重复执行入图延后判断。战前按六十秒轮询恢复，确认战斗已加载后扣减并原子写回；后续战果异常、取消或失败不撤销已经发生的扣减。

emotion.json 保存入图、等待、扣减的时间、两队估算值、舰队、战斗帧及写回状态，写入失败也保留尝试；缺失或无效工件使报告不完整。非法“船坞恢复 + 保持快乐经验”组合提前拒绝，不沿用原生先写记录再报错的副作用；两队记录与 NextRun 在同一事务提交，避免半更新。长期客户端心情 bug 的随机阈值/重启任务和完整周期调度仍未迁移；campaign_run 的双倍书观测现已连接实际战前等待及加载后的扣减。

campaign_run 支持布尔输入 clearMode（默认 true）和 doubleBook（默认 false）；campaign 命令对应 --clear-mode / --double-book，值为 true 或 false。选关后先按原生进度条动画与信息条遮挡规则等待，读取百分比、三星和安全状态，再设置周回并确认自动寻敌关闭；周回的地图/出生波次及机制覆盖在章节 Config 之后应用。星级也参与全清和地图剧情标记覆盖；这些观测不证明本次出击通关。

mapAchievement（默认 non_stop）接受 non_stop、100_percent_clear、map_3_stars、threat_safe、threat_safe_without_3_stars；stageIncrease（默认 false）控制达成后的递增。CLI 对应 --map-achievement / --stage-increase。启用成就停止必须绑定 config-root、instance 和 configTask，配置里的 Campaign.Event/Name 须匹配本次规则，否则设备动作前拒绝。进度须严格大于 .95，三星/安全目标再要求对应状态；未取得全敌星时按上游要求选择全清。达成后取消准备页、观察新章节页帧，再禁用 Scheduler.Enable，或在递增有下一项时更新 Campaign.Name。相关配置被同时修改或设备身份变化均拒绝写回，不覆盖用户修改。

成就停止返回 Skipped / map_achievement_reached，不产生 sortie 成功结论；required 任务仍使批次失败并按默认策略停止，非 required 可用于允许跳过的队列。map-stop.json 记录进度观察、取消次数、新返页帧、停止决策及 Persisted；写回失败仍保存部分记录。下一关仅写入配置，不自动再次排队；主线原生递增不检查可执行规则，可运行范围仍由 RuleCatalog 验证。

舰队页使用原生素材偏移、颜色判据和三秒点击间隔设置双倍书；未出现选项时按原生确认等待判为不可用，点击后无法确认则失败，最多四次点击。map-preparation.json 保存地图观察、已确认开关及双倍书部分尝试；Enabled=null 表示动作后尚未确认，不能按关闭处理。只有已确认倍率传入战前等待与每战扣减（2 或 4）；入图预估按原生同时考虑请求双倍书。此选项仅支持完整 campaign_run 入口，图内 resume 无法重建先前周回/双倍书状态，也不生成通关结论。所有状态仍需实际成功结算合同，星级、进度条和开关成功不能证明本次通关。

战中需要切换舰队时，Engine 复用初始编号选择和上游角色映射，按新截图重定位后更新成本、HP、等级基线与对应阵型。`fleet-switch.json` 保存逻辑来源/目标、位置、已确认编号/帧、首次定位帧和 `Ready`；`Ready=false` 表示整个切换准备未完成，但已确认的物理身份不会回滚。未知编号、定位失败或输入错误使任务失败，当前相机不可复用；下一任务不继承切换证据。报告拒绝缺失工件、旧定位帧或无确认帧的 `Ready=true`，该字段不代表战役通关。

已迁移规则开启陆基机关时，地图循环在全清或规则显式调用中选择可达触发点，等待原生动画时长并持续确认新帧后解除整组阻挡、刷新路径并重新分派。普通路径落点同样执行机关语义；未确认到达不会先修改地图。正常战役结果的 mechanismReleases 记录规则提交的位置、关联格、等待与帧号，不代表独立识别屏障消失，也不增加纯机关操作的战斗数。潜艇等尚未迁移组合仍明确拒绝，支持范围由可执行 RuleCatalog 决定。

已迁移规则开启移动敌人时，C# 在首次扫描后初始化出生回合，按舰队步数移动；确认到达后更新回合并按上游要求重扫、追踪和重新选敌。正常战役结果的 movableScans 记录旧位置、扫描机位、匹配和遮挡预测，不能据预测或扫描结束判通关。重扫失败后不撤销已经确认的位置/战斗数，也不复用部分更新的状态；失败任务沿现有异常与动作工件处理，movableScans 目前只随正常战役结果返回。迷宫路点已接入邻格等待和相位切换后的重新分派；正常结果的 mazeWaits 记录确认位置、前后回合、等待和帧号。邻格战斗/物品复用既有证据，补给仅确认点击不推断库存恢复；未知到达不推进回合。同格救援和弹跳敌人已接入同一战役分派：救援切换第二队并恢复相机边缘，确认胜方战斗后才切回；弹跳按原声明循环整条路线，最多 13 次访问，确认战斗后才清理所选路线。回合异常与返章节页中断后续动作，战后扫描失败不复用本局状态。诱饵开关开启时，普通 combat 的空目标在延迟确认后清除并重新选敌，不计战斗或弹药消耗；普通扫描使用 decoy 模式，初始/移动扫描模式不变。正常结果的 decoyArrivals 记录已确认空目标的位置、舰队、战斗计数、等待阈值和帧号；它不证明战斗或结算，异常退出目前不单独输出该列表。未知战斗、不可达路线恢复和完整活动规则仍未迁完。

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

具体输入由各 runner.Validate 定义。已编译地图声明不代表对应章节钩子和配置已迁完；可执行 RuleCatalog 仍只有已迁移的规则。战役 fleet1/fleet2/submarine 必须明确；campaign_run 缺省 emotionMode=calculate；持久化绑定和其他模式见上方心情控制说明。campaign_run 的可选 fleet1Formation/fleet2Formation 为 line_ahead、double_line（默认）或 diamond，非法值在操作设备前拒绝；章节配置覆盖保留并传入地图执行。fleetOrder 接收 fleet1_mob_fleet2_boss（默认）、fleet1_boss_fleet2_mob、fleet1_all_fleet2_standby、fleet1_standby_fleet2_all。初始舰队选择/反转和对应阵型已接入，章节覆盖禁用二队时不反转；战中双舰队调度与潜艇实战仍有缺口。禁止按地图/页面补特例来绕过缺失语义。

`campaign_run` / `campaign_resume` 的队列输入可设置 `hpControl: { "lowHpRetreat": true, "threshold": 0.3, "balanceWeight": "1000, 1000, 1000" }`。缺省遵循原生配置：关闭低血量撤退、阈值 0.3、等权重。权重支持中文逗号和单个整数的广播；拒绝负权重、全零、错误数量及非整数。阈值在 0–1 之间；这是加权血量，首次确认的有船槽位不会因战损归零而变为空槽。进图和战后返回地图时均读取六槽血量；工件的 `health` 保存原始值、加权值、首次槽位掩码和帧号。低血量触发撤退后须有退出动作和新帧的章节页确认，才输出 `withdrawal` 与 `sortie.outcome=withdrawn`，任务仍非成功。换位和维修须分别通过下述开关启用。

`hpControl` 另支持 `balance`（默认 false）、`balanceThreshold`（0.2）、`emergencyRepair`（false）、`repairSingleThreshold`（0.3）和 `repairMultiThreshold`（0.6）；所有阈值要求 0–1 的有限值。按照上游，维修需要同时开启 `balance` 和 `emergencyRepair`；舰队锁不禁维修，但会禁止前排换位。`campaign_run` 须另设 `fleetLock=false` 才能执行换位，`campaign_resume` 仍沿用默认舰队锁。换位按加权前排 HP 决定；维修忽略 ≤0.001 的空槽，要求前后排都有有效 HP，任一有效槽低于单槽阈值或任一排最高 HP 低于整排阈值才使用。当前 ADB 操作是原生滑动加目标补点组合，尚未真机验证换位结果。`combat-health.json` 保存每场准备的换位输入完成状态、维修点击及依据血量帧、稳定等待是否成功；失败动作另见 `actions.json`。动作完成不代表换位、维修到账或通关，权威 HP 只由之后的地图读数更新。

两个战役任务还支持 `reachLevel` 非负整数，默认 0 关闭等级观测；开启后要求配置 OCR 模型目录。`levels` 工件保存同帧六槽读数、战前基线及 `reachLevelTriggered`，遵循原生 `after >= limit > before > 0`，且只接受升一级或新等级低于 35 的跨阈值变化。初始读数不会触发停止，后续读数不会清除已触发标记；读取失败或六槽帧号不一致不发布部分状态。当前任务只执行一次出击，`reachLevel` 不会中途撤退，也不影响之后显式排列的独立任务。完整 CampaignRun 连续出击及其到级禁用调度仍待迁移，不能把标记输出当成该调度已完成。等级读数、等级达到和 LV32 标记都不能证明通关。

两个战役任务的队列输入可带 `retirement` 对象：`mode` 为 `one_click_retire`（缺省）、`old_retire` 或 `disabled`；`keepLimitBreak` 缺省 true；`rarities` 缺省 `["N","R"]`，只允许 N/R/SR/SSR 且不得为空或重复；`amount` 为 `retire_all`（缺省，原生上限 3000）或 `retire_10`。稀有度与数量仅控制 old 模式，一键模式按游戏的一键选择结果操作；`keepLimitBreak` 控制一键失败后的最后设置回退，不会覆盖首次尝试时已有的游戏设置。`enhance` 和其他未迁模式明确拒绝。禁用退役时遇容量提示或退役页失败，不自动整理。`campaign_fleet_prepare` 不启用自动退役。

退役由 Engine 的通用船坞筛选、排序/收藏开关、一键设置和船/装备/奖励确认流程执行，进图与战斗准备共用同一会话处理器；`emotionMode=ignore` / `calculate_ignore` 的低心情确认已接入。`retirement.json` 保存开始/返回帧、确认动作及未完成尝试；`NativeSelectionEstimate` 是原生选择估算，不是实测退役数。未确认设置、缺少实际确认动作或最后动作后的新帧、超时无奖励证据均失败；只见离开船坞不证明返回特定页面，更不证明通关。强化、GemsFarming 保留航母/退役旗舰与长期心情重启调度仍未迁移，没有新增实机退役证据。

CLI 公共参数为 --adb、--serial、--server、--assets、--python、--artifacts；OCR 任务使用 --models，动作另需 --allow-actions 和 --package。`campaign --chapter <规则列表>` 默认 dry-run，使用 --run --allow-actions 才执行；--fleet1-formation/--fleet2-formation 选择上述阵型，--fleet-order 指定舰队顺序。自定义退役选项使用队列输入；直接 campaign 命令沿用缺省选项。运行前读取 --help 核对参数。

## 待完成能力与验证

旧周期任务、连续调度、工具、大世界和统计/策略尚未迁移为 Engine runner，旧 task 名不会自动映射或回退到 Python。它们是完整重写的剩余范围，不能以接口返回不可用当作完成。

Engine 离线输入、状态、队列与规则对照在 tests/Alas.Engine.Tests；需使用 dotnet run 执行。设备/页面观测成功只证明对应观测；实机通关必须由新 Engine 获得真实成功结算并返回章节页。旧 Core 证据和回归不能替代新执行路径验收。统一完成度见[路线](architecture-roadmap.md)。
