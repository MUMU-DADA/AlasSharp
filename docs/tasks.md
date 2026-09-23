# 任务域与通用任务模型（R2）

**本页读法**（按主题，不按物理顺序 —— 各节是逐轮追加的）：

| 主题 | 看哪节 |
| --- | --- |
| 通用任务模型与队列语义 | 模型 · 队列语义 |
| 六个域（按实现顺序） | 战役批量域 → 账号状态 → 大世界/海域 → 活动章节清点 → 周期任务清点 → 周期任务调度状态 |
| 怎么跑 | 命令 |
| 怎么验 | 验收矩阵 |
| 还差什么 | 下一步与本域的缺口 |

R2 的目标是**按业务域**完成端到端闭环，而不是按地图逐个适配。为此先把"任务"本身
抽象出来：队列、前置条件、结论、证据、断点 —— 通用层做一次，业务域只实现自己的那部分。

## 模型

```
src/Alas.Core/Tasks/
  TaskModel.cs        TaskRequest / TaskResult / TaskOutcome / ITaskRunner / TaskContext
  TaskQueue.cs        队列调度：前置条件、跨任务复位边界、失败即停、取消、证据、断点
  TaskQueueFile.cs    队列文件与断点文件的读写（输入模型）
  CampaignBatchTask.cs  战役批量域：把输入翻译成上游调用，并把合同裁决搬进任务结果
```

| 概念 | 说明 |
| --- | --- |
| `TaskRequest` | `id` + `kind` + `input`（**业务域自己的 JSON 输入模型**）+ `required` |
| `ITaskRunner` | `Kind`、`Preconditions(...)`、`Run(...)`；新业务域先实现它，再接 CLI/前端 |
| `TaskOutcome` | 只有五个值：`Succeeded` / `Failed` / `Skipped` / `Refused` / `DryRun` |
| `TaskResult` | 结论 + `error_kind` + `evidence`（结构化事实）+ 工件路径 |
| `TaskQueue` | 顺序调度，逐任务写工件与断点，产出 `queue.json` |

**为什么把 `Skipped` 单独拿出来**：前置条件不满足是"没跑"，不是"跑失败"。
混在一起报告会撒谎 —— 用户看到"失败"会去查游戏，其实只是这轮不该跑它。
`required: true` 的任务前置条件不满足时才算失败（并停队列）。

## 队列语义（写死，避免各处各判）

| 规则 | 行为 |
| --- | --- |
| 前置条件不满足 | 非 required → `skipped`，继续下一个任务；required → `failed` 并按失败即停停下 |
| 任务失败 | 默认停下整条队列，剩余任务如实记 `skipped`（原因写明是哪个任务失败导致） |
| 失败即停可关 | `--continue-on-error`（CLI）/ `StopOnFailure=false`（API） |
| 取消 | 在**任务边界**生效；当前任务不打断，剩余任务记 `skipped` + `cancelled` |
| 队列结论 | 有失败 → `failed`；被取消 → `cancelled`；全成功 → `succeeded`；全 dry-run → `dry_run`；其余 → `partial` |
| 跨任务复位 | 每个任务开始前记一条边界日志；**复位由任务自己负责**（战役域交给上游 `prepare_campaign_navigation`） |
| 断点续跑 | 逐任务写 `state.json`；`--resume` 跳过已成功/已 dry-run 的任务，并如实记 `skipped` + 原因 |

## 战役批量域（第一个垂直切片）

输入模型：

```json
{
  "chapters": ["campaign.campaign_main.campaign_1_1"],
  "stop_on_failure": true,
  "max_seconds": 1500, "max_rounds": 20,
  "repeat_until_cleared": true, "clear_all": false,
  "fleet1": 1, "fleet2": 0, "submarine": 0
}
```

- **前置条件**：章节必须是完整 `campaign.<包>.<模块>`（禁同名兜底）；真跑必须有 `allow_actions`。
- **结论**：`cleared` 且每关都过结果合同 → `Succeeded`；`refused` → `Refused`；
  被取消 → `Skipped`；其余（撤退/战败/报错/没打完/合同违例）→ `Failed`。
- **证据**：逐关结论 + 违例码 + 工件路径都在 `TaskResult.Evidence` 里，报告与前端只读它。
- **不认地图**：章节差异全部由上游 `MAP`/`Config`/`Campaign.run()` 消费；本域只翻译参数。

真实产品路径证据：`docs/result-evidence.md` 里 4 条真实通关即由这条链路跑出
（该链路在 R1 时已是产品路径，R2 只是把它接进通用任务模型并补了队列语义）。

## 命令

```powershell
# 队列文件只描述"跑什么"，不描述"怎么算成功"
alashub queue --file queue.json [--run --allow-actions] [--serial <设备>] `
              [--artifacts <目录>] [--resume] [--continue-on-error] [--max-rounds 20] ...
```

队列文件示例：

```json
{"tasks": [
  {"id": "clear-1-1", "kind": "campaign_batch",
   "input": {"chapters": ["campaign.campaign_main.campaign_1_1"]}},
  {"id": "clear-1-2", "kind": "campaign_batch",
   "input": {"chapters": ["campaign.campaign_main.campaign_1_2"], "clear_all": true}}
]}
```

## 活动章节清点（第四个域，纯离线）+ 生成队列

`EventStateTask`（`kind = "event_state"`）回答"跑活动之前必须先知道的事"：导出的契约里
**有哪些活动章节、计划完整度如何、哪些能跑**。

- **没有第二份章节表**：数据来自 S0 冻结的同一份 `data/campaign` 契约（`alashub campaign` 用的也是它）；
- **不认地图名/编号**：筛选条件是输入给的"来源目录前缀"（默认 `event_`），代码里没有关卡清单；
- **输入**：`folder_prefix` / `only_complete` / `limit`；
- **结论**：清单读出来即 `Succeeded` —— **"一个活动都没有"是有效状态**；契约读不出来才 `Failed`。

把清点接到执行上的是 `alashub plan-queue`：

```powershell
# 生成的是**普通队列文件**：后面照样 queue / --resume / report，不需要新机制
alashub plan-queue --out events.json --only-complete --limit 5
alashub queue --file events.json --artifacts runs\        # 默认 dry-run；真跑加 --run --allow-actions
```

筛选规则**只有一处**（`EventStateTask.Select`），清点任务与生成器共用 —— 两边各写一套
筛选迟早会走偏。实测：`event_*` 匹配 879 章、其中计划完整 768 章（A 644 / B 124）；
生成的 3 关队列 dry-run 全部跑通并逐任务落盘工件。

> 纪律提醒：`plan-queue` 只做"翻译成任务"，**不新增判据**；队列里的任务默认 `required=false`
> （一个活动关跑不动不该把整条队列停掉），但失败仍按"失败即停"停下，要跑完请显式
> `--continue-on-error`。

## 周期任务清点（第五个域）与它的数据源

科研/建造/委托/每日这类周期任务的清单与分组定义在上游，**两个来源不是一回事**（实测）：

| 来源 | 是什么 | 本机数量 |
| --- | --- | --- |
| `module/config/argument/task.yaml`（上游源） | 顶层键是**分组**（Alas / Event / Reward / DailyMission / EventDaily / Farm / Island / Opsi / Tool） | **9 个分组** |
| `module/config/argument/args.json`（上游生成产物） | **扁平的任务清单**，就是"有哪些任务" | **68 个任务** |

交集只有 3 个（Alas / Event / Reward），所以**把 task.yaml 的顶层键当成任务清单是错的** ——
这一条是第一版验收脚本把两者当同一集合比对时被自己的对拍当场证伪的（正是对拍该干的事）。

- 宿主 op：`task_catalog`（只读，用上游 loader 读源 + 读生成产物，两个都报、各自标明是什么）；
- 验收：`python tools/diagnostics/verify_task_catalog.py`（两个来源可读、差异如实打印）；
- **做周期任务域时**：任务清单取 `args.json`，分组关系去 `task.yaml` 找；本项目**不另维护任务表**。

## 账号状态（第二个域，只读）

`AccountStateTask`（`kind = "account_state"`）回答"现在是什么状态"：当前页面、**在不在图里**、
服务器、章节实例与账号配置要点。它是**只读**的 —— 不点击、不导航，只有 `capture=true`
才让设备抓一帧；因此 dry-run 里也能跑，没有设备时用存盘帧即可验收。

```json
{"id": "state-now", "kind": "account_state",
 "input": {"screenshot": "data/s3_final_stage.png"}}   // 或 {"capture": true}
```

判据全部来自上游，本域**不新增判据**：

| 字段 | 来自 |
| --- | --- |
| `pages` | 上游 `Page.check_button`（与 `alashub goto` 同一套页面判定） |
| `in_map` | 上游 `is_in_map()` 用的同一个 `handler/IN_MAP` 按钮（`ModuleBase.appear` → 颜色比对） |
| `in_map_tolerance` | 该比对的实测相似度，**留数值不只留布尔** |
| `config` | 账号配置要点（章节名/模式/舰队/心情/后端），只读快照 |

**为什么先做它**：后续每个域开跑前都要问"在不在图里、停在哪一页"（跨任务复位的前提），
把它做成通用只读任务，比在每个域里各写一遍探针可靠。

### 临界判据留证（未改判据）

`IN_MAP` 是**颜色比对**（上游阈值 10）。在一批**真机**归档帧上实测：

| 帧 | 画面（人工核对） | 相似度 | `in_map` |
| --- | --- | --- | --- |
| `_boss122.png` | 7-1 地图，撤退按钮在屏 | **3.33** | `true` |
| `_14_inmap.png` | 1-4 地图，撤退按钮在屏 | **10.33** | `false` |
| `_71_inmap.png` | 7-1 地图，撤退按钮在屏 | **10.06** | `false` |
| `s3_final_stage.png` | 第 1 章选择页 | 140.21 | `false` |

即：同一客户端上，真实地图帧的相似度落在 3.33 ~ 10.33，**跨过了阈值 10**。非地图帧离得很远
（本批 9 帧里非地图帧最低 92.07，另一批帧里最低 83.11），所以判别区间本身很宽，
只是阈值正好卡在真实帧的取值带上。逐帧数值见 `data/account_state_probe.json`。

> 附带查实一个**帧的坑**：早期有几张帧是用 `cv2.imwrite` 存的，通道序与宿主内部（RGB）相反，
> 相似度会离谱地大（`_21_inmap.png` 174.67、`_map_now3.png` 182.71）。用存档帧做判据类
> 验收前，先确认存盘通道序 —— 否则会把"帧存错了"当成"判据坏了"。

处置：**这一轮不改上游判据、不调阈值** —— 存盘帧的颜色不足以证明现场行为
（同一客户端两种取值说明还有未知变量，例如按钮动画相位）。已把逐帧数值写进
`data/account_state_probe.json`，并列出临界区间；等设备在线时用真机复核
（记录 `is_in_map()` 的现场取值）再决定是否做兼容垫片。
影响面：`prepare_campaign_navigation` 判断"上一局残留"会漏判，最坏情况是多走一次
客户端「正在攻略中」弹窗处理（已有垫片），不会把撤退记成通关。

## 验收矩阵（每个域的入口）

每个域都有自己的验收脚本。**本表只给"入口 + 断言什么"，不复制逐例清单** ——
逐例细节以脚本自己的输出为准（此前这里抄了一份 `verify_runtime` 的 5 个用例，
很快就不对了：现在是 13 例）。凡是在这里写死数字的地方，都会在最需要它的那天过期。

| 范围 | 入口 | 断言什么 |
| --- | --- | --- |
| 运行时 / 队列 | `verify_runtime.py` | 宿主只起一次、失败即停、取消在边界生效、断点续跑、任务边界快照、撤退与上游报错可区分 |
| 账号状态（只读） | `verify_account_state.py` | 真机帧上跑只读任务；逐帧相似度留证（含 `IN_MAP` 临界） |
| 大世界/海域探针 | `verify_os_state.py` | 只读探针；`capture` 在 dry-run 记 skipped 而非失败；断点续跑接线 |
| 活动清点 + 生成队列 | `verify_event_state.py` | 与独立数对拍；`plan-queue` 生成物**可直接执行** |
| 周期任务清点 | `verify_task_catalog.py` | 两个来源（`task.yaml` 分组 / `args.json` 任务）可读且**不是同一集合** |
| 周期任务调度状态 | `verify_task_schedule.py` | 与独立读数对拍 + 四种边界（全禁用/全启用/缺段/配置不存在）+ **只读**（配置字节不变） |
| 运行报告 / 运行列表 | `verify_report.py` | 报告事实 + 3 个反例 + `runs` 同秒不覆盖 + 单批形态 |
| 停止任务 | `verify_stop.py` | `--stop-file` 在任务边界生效；剩余任务记 skipped；无停止文件时照常跑完 |
| 结果合同 | `verify_result_contract.py` | 四类结果 + 20 条反例 + 两侧裁决逐例一致 |
| 实机证据 | `audit_real_records.py` | 归档日志重核：通关/撤退可解释、无自相矛盾 |
| 静态守卫 | `verify_architecture.py` | 生产路径、素材边界、词表一致、**每个域名必须注册**、真机清单的两处授权标注 |

## 大世界/海域（第三个域，只读探针）

大世界（OS）与战役的差别：它的"地图"是**海域 + 球面导航**，识别入口是宿主已有的
`map_detect`（`mode="os"`）与 `globe_detect`，不是 `s3_run_plan`。所以第三域同样**不需要**
在 C# 里写地图逻辑，只需要按下面的形状接通用任务模型。

**第一刀只做只读探针**（设备不在线时可完整验收），动作流程等设备上线再加：

| 项 | 设计 |
| --- | --- |
| 域标识 | `kind = "os_state"`，类 `OsStateTask : ITaskRunner`（放 `Alas.Core/Tasks/`） |
| 输入模型 | `{"screenshot": "<帧路径>", "detect": "map"｜"globe", "capture": false}`；`screenshot` 与 `capture` 二选一 |
| 前置条件 | `screenshot` 必须存在；`capture=true` 需要真跑会话与 serial（与 `AccountStateTask` 同规则） |
| 调用 | `LoadScreenshot(path)`（或宿主当前帧）→ `CallTyped<MapDetectResult>("map_detect", new { mode = "os" })`；`globe` 走 `globe_detect` |
| 结论 | **探针跑通即 `Succeeded`**（"没检测到"是有效状态，不是失败）；宿主/设备异常才 `Failed` |
| 证据 | `detected`、`grid_count`、`center_loca`、`globe_center`、`log_lines`（已归一计时）、来源（帧路径 / 设备抓帧） |
| 离线验收 | `tools/diagnostics/verify_os_state.py`：用 `data/fixtures/os_map.png`（宿主 `alashub map` 的默认夹具）跑队列任务，断言证据字段齐全、工件落盘；没有夹具时显式跳过 |
| 守卫 | 新增域名后 `verify_architecture.py` 会自动要求它在 `Program.cs` 注册（已有检查） |

**动作流程（导航、海域选择、出击）暂不做**：它们必须真机验收，而现在
`adb devices` 为空 —— 先做能验收的部分，避免造出无法验收的域（这是 R2 一贯的口径）。

## 下一步与本域的缺口

- 账号状态域的**真机验收待补**：目前只用了存盘真机帧（帧是现场的，但"当场抓帧"路径未跑）。
  设备在线时补一条 `capture=true` 的真机记录。
- 队列目前只有 CLI 入口；前端（R4）要读 `queue.json` / `state.json` 来展示与操作。
- 大世界/海域、活动、周期任务三个域排在后面；每个域都要按本页的模型接
  `ITaskRunner`，并带离线回归 + 一条真实产品路径证据。

## 周期任务调度状态（第六个域，只读；已实现并验收）

为什么是它：周期任务（科研/建造/委托/每日…）的**动作**要真机，但"**哪些任务开着、下次什么时候跑**"
完全在配置里 —— 而 R4 前端、以及"跑之前先知道会跑什么"都需要它。离线可验、零账号消耗。

| 项 | 设计 |
| --- | --- |
| 域标识 | `kind = "task_schedule"`，类 `TaskScheduleTask : ITaskRunner` |
| 数据源（宿主侧，只读） | ① `module/config/argument/args.json`（任务表，见第七节：**扁平清单以它为准**）；② 账号配置里的 `Scheduler` 段（每个任务的 `Enable` / `NextRun` / `ServerUpdate` 等） |
| 宿主 op | 新增 `task_schedule`（只读：读 args.json + 读配置，**不写配置、不触发任务**） |
| 输入模型 | `{"only_enabled": true, "limit": 30}` |
| 前置条件 | 无（不需要设备；配置读不到时 Failed 而不是 skipped —— 那是环境问题不是"没跑"） |
| 结论与证据 | 跑通即 `Succeeded`；证据给：启用任务数 / 每个任务的 `Enable`、`NextRun`、分组（分组来自 `task.yaml`，见第七节）、以及配置来源路径 |
| 离线验收 | 新增 `verify_task_schedule.py`：用**真配置**跑一次，断言任务数 = args.json 里的任务数、启用集合与配置里的 `Enable` 一致；再用**构造的假配置**（临时目录 + 一份手写配置）验证边界：全禁用 / 全启用 / 缺 `Scheduler` 段 三种情形都有明确输出而不是崩 |
| 真机相关性 | 无（这是纯配置面）；但它是"跑周期任务"的前置：先能列出要跑什么，再谈怎么跑 |

**注意两条边界**（写在这里免得下一轮又踩）：

1. **只读**：`Scheduler` 的 `NextRun` 是上游调度器写进去的，本域只**报**不改 —— 要改调度得走上游自己的配置入口；
2. **不要重算"下次运行时间"**：那是上游调度器的逻辑（含 `ServerUpdate` 语义），本域只透传；
   自己实现一版等于养第二份真相。

写完之后，周期任务的**动作**部分再按域逐个做（每个都要真机 + 真机证据），顺序由覆盖率决定，不由某张图是否失败决定。
## 周期任务的动作半边：暂不做，以及为什么（含一条实测证据）

勘察半边（`periodic_plan`）已完成并验收。**执行半边故意没做**，理由不是"没时间"，而是查过之后
发现一个具体风险：

> `module/commission/commission.py` 的 `run()`（第 603 行起）在处理 **油满** 时会
> **买食物把油消耗掉**：第 563 行 `if self.appear(OIL_MAXED, ...): raise OilMaxed`，
> 第 595-600 行捕获后 `logger.info("Oil maxed, buy food to consume oil")`，失败三次才
> `RequestHumanTakeover`。

也就是说：**连"收委托"这种看起来纯收益的周期任务，都存在消耗资源的路径**。
由此得出两条对实现方式的约束（不是我加的限制，是代码要求的）：

1. **不能做"看起来安全的默认白名单"** —— 判断某个任务是否花资源，必须逐个查它的 `run()`
   以及异常分支（油满/物资满这类"看起来是状态处理"的地方恰恰会花钱）；
2. **执行入口必须按任务显式授权**，而不是"授权一次就都能跑"。第一版应当只支持
   **单个任务名 + 显式确认**，并把"这次会调用哪个类、它可能花什么"作为运行的**前置证据**打印出来。

**下一步的最小可行形状**（写下来，免得下次重新推）：

| 项 | 设计 |
| --- | --- |
| op | `periodic_run(task, allow_actions, confirm)`：`confirm` 必须与 `task` 完全一致才执行（防手滑/防脚本误传） |
| 前置证据 | 复用 `periodic_plan` 的结果（哪个类、哪一行）+ 本次要跑的任务名，**先打印再执行** |
| 结论与证据 | 复用结果合同之外的**任务账**：跑通/异常/被拒；异常带上游调用栈尾部（与 S3 同规格） |
| 验收 | 离线只能验**拒绝路径**（没授权/confirm 不匹配 → 明确拒绝）；真跑路径必须有人当场看着做第一次 |

**未查**：其它周期任务（research / dorm / reward / freebies …）是否也有类似的隐性花费路径。
要放开任何一个，先按上面第 1 条查它的 `run()` 与异常分支。

### 花费路径排查（粗筛，2026-09-23）

对常见周期任务模块按 `OilMaxed|quick_finish|buy food|CoinMaxed|spend|purchase|COST` 粗筛一遍：

| 模块 | 命中 | 读法 |
| --- | --- | --- |
| `commission` | `commission.py:564/595/596`（油满 → **买食物消耗油**） | 已确认为**默认路径**上的花费（上一节） |
| `dorm` | **`buy_furniture.py`**、`dorm.py:461/502` | 存在购买家具的代码；**默认 run 是否走到未读** |
| `meowfficer` | **`buy.py`:22/66** | 存在购买代码；同上 |
| `research` | `preset_generator.py`:119/132（COST） | 科研项目有花费；默认路径是否选付费项未读 |
| `freebies` | `assets.py`:24、`battle_pass.py`:39 | 待读 |
| `island` | `data.py`:227-230 | 看着是数据表，不是动作 |
| `reward` / `tactical` | **无命中** | 目前**唯一**谈得上"可能安全"的候选 |
| `mission` | 路径不存在（任务名与模块名不一致） | 需要先查它对应哪个模块 |

**必须说清的限度**：**命中 ≠ 该任务的默认路径会花**（购买代码可能只被某个分支或另一个入口用到），
**无命中 ≠ 一定不花**（花费可能通过别的写法发生，例如直接调 `device.click` 买某个东西）。
所以这张表只能用来**排序**：下一步要放开哪个任务，就读它的 `run()` 与所有能到达花费代码的分支，
而不是拿这张表当授权依据。

**建议的下一个动作**（小、可验证）：先读 `reward`（无命中里最常用）的 `run()`，
确认它的完整调用链上没有花费；若确认，它就是执行半边第一个可以谈"无人值守也安全"的候选 ——
但**第一次真跑仍然要有人看着**。

### 执行半边的第一个候选：`reward`（读源码得到，附已验证范围）

用上一节的方法确认了它跑谁（`alas.py:208` → `from module.reward.reward import Reward`），
再读它的 `run()`：

```python
def run(self):
    """Pages: in: Any page / out: page_main or page_mission, may have info_bar"""
    self.ui_ensure(page_reward)
    self.reward_receive(oil=..., coin=..., exp=...)          # 领取
    self.ui_goto(page_main)
    self.reward_mission(daily=..., weekly=...)               # 领取任务奖励
    self.config.task_delay(success=True)
```

**已核对**：`run()` 本体里没有 `OilMaxed` / `quick_finish` / 购买类调用；全文件的"花费/点击"信号只有
两处 `device.click`（属导航与领取按钮）。调用链是 **导航 → 领取 → 领取 → 记调度**。

**未核对（如实标注）**：

1. `reward_receive()` 与 `reward_mission()` 的方法体**没读** —— 领取类里出现"买"的分支概率低，但没读过就不能说没有；
2. 它会**写配置**（`config.task_delay(success=True)` 推进调度）—— 所以它不是"只读任务"，
   验收时不能照搬 `task_schedule` 的"配置字节不变"那条断言；
3. 它**要导航**（`ui_ensure` / `ui_goto`）—— 上游的页面判据风险在这条路径上照样存在。

**结论**：`reward` 是执行半边目前最合适的第一个候选（无花费信号 + 调用链短），
但**放开它之前**至少要读掉 `reward_receive` / `reward_mission` 两个方法体。
第一次真跑仍要有人看着 —— 这条不变。

顺带查实：`alas.py` 里**没有** `mission` 方法（`periodic_plan` 返回 found=false），
所以"任务"这个界面入口不对应独立的周期任务名，先别按它去做映射。

#### 补：`reward_receive` / `reward_mission` 已读（下钻两层，未见花费）

| 方法 | 看到什么 | 花费信号 |
| --- | --- | --- |
| `reward_receive(oil, coin, exp)` | `for _ in self.loop():` + `click_timer`（0.3s 间隔，"游戏反应没那么快"）按 flag 点领取按钮 | **无** |
| `reward_mission(daily, weekly)` | `reward_mission_notice()` → `ui_goto(page_mission)` → `_reward_mission_all()` / `_reward_mission_weekly()` | **无** |

即 `run → receive/mission` 这一层已确认没有 `buy` / `purchase` / `OilMaxed` / `quick_finish` / `COST`。

**再深一层未读**：`reward_mission_notice()`、`_reward_mission_all()`、`_reward_mission_weekly()`
（名字看都是"领"，但没读过就不写"确认无花费"）。所以准确的说法是：

> **`reward` 已下钻两层未见花费路径；要把它作为执行半边第一个放开对象，
> 还差把这三个最深的方法读掉。**第一次真跑仍要有人看着 —— 这条不变。
