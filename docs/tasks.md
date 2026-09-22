# 任务域与通用任务模型（R2）

R2 的目标是**按业务域**完成端到端闭环，而不是按地图逐个适配。为此先把"任务"本身
抽象出来：队列、前置条件、结论、证据、断点 —— 通用层做一次，业务域只实现自己的那部分。

## 一、模型

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

## 二、队列语义（写死，避免各处各判）

| 规则 | 行为 |
| --- | --- |
| 前置条件不满足 | 非 required → `skipped`，继续下一个任务；required → `failed` 并按失败即停停下 |
| 任务失败 | 默认停下整条队列，剩余任务如实记 `skipped`（原因写明是哪个任务失败导致） |
| 失败即停可关 | `--continue-on-error`（CLI）/ `StopOnFailure=false`（API） |
| 取消 | 在**任务边界**生效；当前任务不打断，剩余任务记 `skipped` + `cancelled` |
| 队列结论 | 有失败 → `failed`；被取消 → `cancelled`；全成功 → `succeeded`；全 dry-run → `dry_run`；其余 → `partial` |
| 跨任务复位 | 每个任务开始前记一条边界日志；**复位由任务自己负责**（战役域交给上游 `prepare_campaign_navigation`） |
| 断点续跑 | 逐任务写 `state.json`；`--resume` 跳过已成功/已 dry-run 的任务，并如实记 `skipped` + 原因 |

## 三、战役批量域（第一个垂直切片）

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

## 四、命令

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

## 六、第四个域：活动章节清点（纯离线）+ 生成队列

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

## 七、周期任务域的数据源（先查清，再动手）

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

## 八、第二个域：账号状态（只读）

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

## 六、验证

`python tools/diagnostics/verify_runtime.py` 共 11 例（6 例单批 + 5 例队列），全部走替身宿主：

| 用例 | 断言 |
| --- | --- |
| `queue_two_campaign_tasks_succeeded` | 两个任务都成功；宿主只起一次；后端调用 = 1 次设备配置 + 2 个任务 |
| `queue_precondition_is_skipped_not_failed` | 空章节 → `skipped`（不是 failed），**没有调后端**，后续任务照跑 → 队列 `partial` |
| `queue_required_precondition_stops_queue` | `required` 的前置条件不满足 → `failed` + 队列停下，后面的任务记 `skipped` |
| `queue_upstream_failure_stops_and_skips_rest` | 上游报错 → 该任务 `failed`（`upstream_error`），后续 `skipped` 且**带上同一个错误分类** |
| `queue_resume_skips_completed_task` | 已完成任务被跳过且不再调后端 |

`python tools/diagnostics/verify_account_state.py`：用 `data/*.png` 里的**真机帧**跑
`account_state` 任务（无设备），断言服务器/页面/在图内/配置要点均报出、逐任务工件落盘，
并把 `IN_MAP` 相似度写入 `data/account_state_probe.json`。本地没有归档帧时显式跳过。

另有 `verify_architecture.py` 静态保证：任务模型是接口、CLI 只解析队列文件、
`campaign` 与 `queue` 共用同一份参数解析（不复制两套），
并且**每个 `ITaskRunner` 实现都必须在 `Program.cs` 里注册**
（漏挂的话队列只会报"没有注册运行器"然后失败 —— 这种漏挂在静态上就该被查出来）。

## 七、第三域设计（大世界/海域）—— 实现前先照这份做

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

## 八、下一步（本域的缺口，不阻塞下一个域）

- 账号状态域的**真机验收待补**：目前只用了存盘真机帧（帧是现场的，但"当场抓帧"路径未跑）。
  设备在线时补一条 `capture=true` 的真机记录。
- 队列目前只有 CLI 入口；前端（R4）要读 `queue.json` / `state.json` 来展示与操作。
- 大世界/海域、活动、周期任务三个域排在后面；每个域都要按本页的模型接
  `ITaskRunner`，并带离线回归 + 一条真实产品路径证据。
