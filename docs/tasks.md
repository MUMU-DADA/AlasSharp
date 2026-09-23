# 任务域与通用任务模型（R2）

**本页读法**（按主题，不按物理顺序 —— 各节是逐轮追加的）：

| 主题 | 看哪节 |
| --- | --- |
| 通用任务模型与队列语义 | 模型 · 队列语义 |
| 十类业务任务与通用任务 | 战役批量、账号状态、大世界探针、活动清点、周期任务清点/调度/勘察/放行/执行、配置读取；通用导航与观测 |
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

队列文件的 `tasks` 必须是数组，逐项必须是对象；存在的 `input` 只能是 JSON 对象或 `null`。
通用解析器只校验这个容器形状，业务字段继续由对应的 `ITaskRunner` 校验。数组、字符串等错误
`input` 会在会话启动前返回输入错误，不会被当作缺省输入执行。
战役域还会在出击前校验章节、布尔开关、时间/轮次/舰队数及字段名；错误类型、小数轮次、
越界值和拼错字段均记前置条件不满足，不会静默采用默认值。

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
| 断点续跑 | 逐任务写 `state.json`；`--resume` 只跳过当前任务及其有序前序任务的 id、kind、input、required 和会话运行参数均匹配的已成功/已 dry-run 任务，并如实记 `skipped` + 原因。队尾追加新任务不使已有完成项失效 |

断点按当前队列逐项匹配，不把同一 id 的新任务当成旧任务。dry-run 与真实运行的会话参数不同，
所以 dry-run 的完成项不会跳过后来的真实执行。旧格式仅保存任务 id、无法证明请求相同，续跑时
会重新执行；同一请求的完成项仍在后续断点中累积保留。
显式 `--resume-state <文件>` 必须与 `--resume` 同时使用；文件不存在时在会话启动前报输入错误，
不会悄悄从头执行。找到的断点若损坏或缺少 `completed` 对象，也在启动前报错；自动寻找的
断点同样如此。
完成项只接受 `succeeded`、`dry_run` 或累积续跑的 `carried_over`；其他结果或缺少任务身份的
条目会被视为损坏断点，在会话启动前报错。

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

只读设备任务使用 `--run --read-only-device`，动作任务使用 `--run --allow-actions`；两种授权都由
会话记录，队列 JSON 不能自行把 dry-run 或只读会话升级为动作会话。`run` 与 `goto` 已弃用，
只打印迁移提示，不能再作为任务执行入口。

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

真实产品路径已有两个已解锁活动样本 A1、A2：普通 `campaign_batch` 队列调用上游
`CampaignRun.load_campaign()` 与原生 `Campaign.run()` 后获得 S 级成功结算，
`sortie-result/1` 判为 `cleared` 且无违例；紧随其后的 `account_state(capture=true)`
实时识别 `page_event`、`in_map=false`。六份脱敏工件及原件哈希见
`tools/diagnostics/evidence/20260923T163008/` 与 `20260923T171026/`，由
`audit_real_records.py` 逐关交叉核对。这只覆盖当前账号已解锁的两关，
不能据此认定其他活动关或活动任务整体完成。

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
`capture=true` 与 `screenshot` 二选一；错误类型、空路径和同时提供两种来源都会在任务前置条件中拒绝，
避免把存盘帧误记成现场抓帧。

```json
{"id": "state-now", "kind": "account_state",
 "input": {"screenshot": "data/s3_final_stage.png"}}   // 或 {"capture": true}
```

判据全部来自上游，本域**不新增判据**：

| 字段 | 来自 |
| --- | --- |
| `pages` | 上游 `Page.check_button`（与 `NavigateTask` 同一套页面判定） |
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

## 验收矩阵（**任务域**的入口；套件的完整清单以 `verify_all.py` 的 `STEPS` 为准）

每个域都有自己的验收脚本。**本表只给"入口 + 断言什么"，不复制逐例清单** ——
逐例细节以脚本自己的输出为准（此前这里抄了一份 `verify_runtime` 的 5 个用例，
很快就不对了：现在是 13 例）。凡是在这里写死数字的地方，都会在最需要它的那天过期。

| 范围 | 入口 | 断言什么 |
| --- | --- | --- |
| 运行时 / 队列 | `verify_runtime.py` | 宿主只起一次、失败即停、取消在边界生效、断点续跑、任务边界快照、撤退与上游报错可区分 |
| 账号状态（只读） | `verify_account_state.py` | 真机帧上跑只读任务；逐帧相似度留证（含 `IN_MAP` 临界） |
| 大世界/海域探针 | `verify_os_state.py` | map / globe 两种上游只读检测；`capture` 在 dry-run 记 skipped；断点续跑接线 |
| 活动清点 + 生成队列 | `verify_event_state.py` | 与独立数对拍；`plan-queue` 生成物**可直接执行** |
| 周期任务清点 | `verify_task_catalog.py` | 两个来源（`task.yaml` 分组 / `args.json` 任务）可读且**不是同一集合** |
| 周期任务调度状态 | `verify_task_schedule.py` | 与独立读数对拍 + 四种边界（全禁用/全启用/缺段/配置不存在）+ **只读**（配置字节不变） |
| 周期任务勘察与放行 | `verify_periodic_plan.py` | 勘察与独立读一致；四类边界；**不 import 目标模块**；放行判定四条路径 + **executes 恒 False**（永不执行） |
| 配置开关（授权前留档） | `verify_config_get.py` | 与独立读数对拍；**缺失 ≠ false**；空输入记 skipped |
| 周期任务执行（执行环） | `verify_periodic_plan.py` | 会话未授权 → 前置跳过（required 才失败）；已授权后按上游 Scheduler.Command 绑定配置并调用原生 dispatcher；reward / opsi / event、TaskEnd / False / SystemExit 与设备配置恢复均有离线回归；历史真机两域见下文 |
| 运行报告 / 运行列表 | `verify_report.py` | 报告事实 + 3 个反例 + `runs` 同秒不覆盖 + 单批形态 |
| 运行报告的 HTML 视图（R4 第一屏） | `verify_report_html.py` | 不丢事实（数据面里的事实都要出现在界面里）+ 单文件自足 + 缺工件也能看 |
| 停止任务 | `verify_stop.py` | `--stop-file` 在任务边界生效；剩余任务记 skipped；无停止文件时照常跑完 |
| 结果合同 | `verify_result_contract.py` | 四类结果 + 20 条反例 + 两侧裁决逐例一致 |
| 实机证据 | `audit_real_records.py` | 归档日志重核：通关/撤退可解释、无自相矛盾 |
| CLI 摘要行（跨域） | `verify_cli_evidence.py` | 战役/账号状态/大世界三域的 `[任务证据]` 行必须真的打印（走队列路径；缺存档帧则显式跳过） |
| IN_MAP 垫片（跨域） | `verify_in_map_shim.py` | 上游阈值判不出、垫片后判得出、且没有无脑放宽（用归档真机帧） |
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
| 离线验收 | `tools/diagnostics/verify_os_state.py`：用 `data/fixtures/os_map.png` 与 `os_globe_view.png` 跑队列任务，分别核对地图结果和上游球面单应性/坐标证据；缺夹具时显式跳过对应分支 |
| 守卫 | 新增域名后 `verify_architecture.py` 会自动要求它在 `Program.cs` 注册（已有检查） |

**大世界动作流程（海域选择、出击）尚未完成**：通用页面导航已有独立任务，
大世界域的动作仍需自己的任务输入、结果和真实产品路径证据。
2026-09-23 真机队列从活动页请求 `navigate(to=page_os)`，退到战役菜单后连续点击上游
`CAMPAIGN_MENU_GOTO_OS`，页面保持 `page_campaign_menu`；任务失败，后续 `account_state` 与
`os_state` 按失败即停记为 `skipped`。现场截图显示“大型作战”入口带锁。
原始队列、日志与截图位于本地忽略目录 `data/mainline-device/20260923T170655-os_nav/`；
这证明当前账号入口受限，不是大世界动作域的成功或地图识别失败证据。

## 下一步与本域的缺口

- 账号状态 `capture=true` 与 `IN_MAP` 现场复核已有设备窗口记录，见 `handover-r0-r2.md` 第五节与第八节补充六。
- 队列已有 CLI 入口和静态 HTML 证据视图；统一配置、任务和运行控制前端仍未交付。
- 大世界目前为只读探针，当前账号的大型作战入口带锁；活动已有 A1、A2 普通战役队列真机通关样本，其他活动章节及活动域完整动作流程仍需验证。
- 周期任务已接通通用执行入口，reward 与 dorm 的原生调度有真机证据；dorm 本次没有收取点击，领取效果及其余执行路径不能据此视为已验证。
- 观测已进入任务队列，完成离线故障/取消回归与只读真机抓帧/识页验证；`map=main` 仅有主界面零命中的现场负样本，地图内正样本及其他后端不据此外推。

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

写完之后，周期任务的**动作**部分按通用上游调度入口接入；每条真实执行路径仍需独立真机证据，
顺序由覆盖率决定，不由某张图是否失败决定。
## 周期任务动作授权的依据（含一条实测证据）

勘察半边（`periodic_plan`）完成后，执行入口没有直接默认放开；代码审计发现了一个具体风险：

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

**据此实现的最小形状**：

| 项 | 设计 |
| --- | --- |
| op | `periodic_run(task, allow_actions, confirm)`：`confirm` 必须与 `task` 完全一致才执行（防手滑/防脚本误传） |
| 前置证据 | 复用 `periodic_plan` 的结果（Scheduler.Command、原生方法和内部调用）+ 本次要跑的任务名 |
| 结论与证据 | 复用结果合同之外的**任务账**：跑通/异常/被拒；异常带上游调用栈尾部（与 S3 同规格） |
| 验收 | 离线验拒绝路径和原生 dispatcher 行为；任何新的真跑路径仍需明确动作授权并单独留证 |

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

#### 补二：`reward` 的调用树已下钻四层，全程无花费信号

| 层 | 方法 | 花费信号 |
| --- | --- | --- |
| 1 | `run` | 无 |
| 2 | `reward_receive` / `reward_mission` | 无 |
| 3 | `reward_mission_notice` / `_reward_mission_all` / `_reward_mission_weekly` | 无 |
| 4 | `_reward_mission_collect` / `reward_side_navbar_ensure` | 无 |

`_reward_mission_collect` 只调用 `_reward_mission_claim_click` / `_reward_mission_claim_receive`
（名字都是"claim/receive"）以及若干 `record_clear`；`reward_side_navbar_ensure` 只调 `set`。

**结论（带范围）**：从 `Reward.run()` 可达的**方法级调用树（4 层）里没有任何**
`buy` / `purchase` / `spend` / `OilMaxed` / `quick_finish` / `COST` / `gem` 信号。
未读的只剩 `_reward_mission_claim_click` / `_reward_mission_claim_receive` / `_reward_wait_mission_list`
（名字语义仍是"领取"）以及上游公共设施（`ui_goto` / `appear` / `click` 这类，全项目共用）。

**但静态阅读证明不了运行时行为** —— 真正剩下的风险不是"代码里有购买分支"，
而是"**点错了地方**"：如果某个意料之外的弹窗出现，一个按坐标盲点的点击可能落到"购买/确定"上。
这正是我坚持"**第一次真跑要有人看着**"的原因，而不是形式主义。

#### 补：粗筛表里剩下两条的判定（2026-09-23，读源码）

| 模块 | 命中处 | 判定 |
| --- | --- | --- |
| `island` | `data.py:227-230` | **数据表**（`DIC_ISLAND_RECIPE`，含 `commission_cost` 字段）—— 不是动作，粗筛时的"看着是数据表"得到确认 |
| `freebies` | `battle_pass.py:39` → `handle_battle_pass_popup()` 里 `appear_then_click(PURCHASE_POPUP)` | **未判定**：点的是"买通行证"还是"关掉购买提示"，取决于调用点上下文与按钮本身的语义；`PURCHASE_POPUP` 只是**素材名**，不能凭名字当结论 |

**下一步（写给要放开 freebies 的人）**：读 `handle_battle_pass_popup` 的**调用点**——
它是在什么条件下被调的（每轮都调？只在出现某画面时调？），以及调用前有没有"先看是不是广告/取消"
之类的判断。**在没有这一步之前，不要把 `freebies` 当作可无人值守运行的任务。**

这条判定的意义不是"freebies 危险"，而是：**素材名不等于行为**。`PURCHASE_POPUP` 这种名字最容易
让人要么过度恐慌、要么直接忽略 —— 两种都不对，得看调用链。

#### 补二：`freebies` 判定完成 —— 那个按钮是"关闭"，不是"购买"

上一节的"未判定"现在有答案了。证据是**三份合起来看**才成立的：

| 证据 | 内容 |
| --- | --- |
| 模板图（`assets/cn/freebies/PURCHASE_POPUP.png`） | 打开看是**一个小 ✕（关闭）按钮**：深红色叉，位置正对该素材的 area `(907,204)-(934,229)` |
| 调用点（`battle_pass.py:52` / `:83`） | 它是作为 `ui_click(..., additional=...)` 的**附加步骤**、以及领取循环里的"处理弹窗"被调用的 —— 语境是**清掉挡路的弹窗**，不是买东西 |
| 上游自己的注释（`ui.py:402-403`） | `# 2024.12.19, PURCHASE_POPUP at main page becomes BATTLE_PASS_NEW_SEASON`，并把主界面那次点击**注释掉了** —— 说明这个素材的**含义随版本变过**，不能凭名字判 |

**结论**：`handle_battle_pass_popup()` 点的是弹窗的**关闭按钮**，`freebies` 没有在这条路径上花钱。

**但这条结论的适用范围要写清**：它只说明"**这个素材+这个调用点**"不花钱；
`freebies` 是"每日免费"类任务，是否还有别处会花，要按方法论逐条读（本节的判定方式就是模板）。

**顺带得到一条通用教训**（比结论本身更值钱）：**素材名不等于行为**。
`PURCHASE_POPUP` 这名字会让人以为在买通行证，看图才知道是关闭；
而上游自己因为"素材含义随版本变"注释掉过同一素材的另一次点击 ——
所以判"会不会花钱"时，优先级是：**模板图/调用点 > 素材名**。

#### 补三：`dorm` 的花费**由配置决定**（读完调用链 + 查本机配置）

调用链（读源码）：

* `Dorm.run()`（`dorm.py:613`）→ `self.dorm_run(feed=…, collect=…, buy_furniture=self.config.BuyFurniture_Enable)`；
* `dorm_run`（`:509`）里 `if buy_furniture: BuyFurniture(self.config, self.device).run()`（`:540-542`）；
* `BuyFurniture` 本身是**独立的任务类**（`module/dorm/buy_furniture.py:22`），上游还给它单独的调度项。

**所以判定的关键不是"dorm 危不危险"，而是"配置里开没开"** —— 这也是可离线查的：

| 本机账号配置（`.runtime/engine/config/alas.json`） | 值 |
| --- | --- |
| `Dorm.Scheduler.Enable` | True |
| `Dorm.BuyFurniture_Enable` |  |
| `BuyFurniture.Scheduler.Enable` | （配置里没有 BuyFurniture 段） |

**这条给"要不要放开某个周期任务"提供了通用检查姿势**：先读它的 `run()` 找到花费分支的**开关名**，
再查本机配置里那个开关的值 —— 比在代码里逐行推"到底会不会走到"更快也更实在。
`meowfficer/buy.py` 与 `research` 的付费项目同样适用这个姿势（尚未做）。

#### 补四：更正补三里的两处说法（读 args.json 之后）

| 我先前写的 | 实际 |
| --- | --- |
| "上游还给它单独的调度项"（指 `BuyFurniture`） | **不对**：`args.json` 里**没有** `BuyFurniture` 这个顶层任务；它是 **`Dorm` 任务下的一个分组**（`Dorm` 段的键是 `Scheduler / Dorm / BuyFurniture / Storage`）。所以配置路径是 `Dorm → BuyFurniture → Enable`，扁平键名才是 `BuyFurniture_Enable` |
| 表格里 `Dorm.BuyFurniture_Enable = （空）` | 值**确实是空的/缺席**，但**空 ≠ false**：`alas.json` 里没写的字段会走**上游默认值**。默认值我**没读**（该看 `args.json` 的 `Dorm.BuyFurniture.Enable`），所以表格那一格只能当"本机没显式设置"，不能当"没开" |

**教训**：我这次的错和上一轮 `PURCHASE_POPUP` 是**同一个毛病的两面** ——
上一轮是"凭素材名猜行为"，这一轮是"凭'有个独立文件/类'猜它是个独立任务"。
判配置时**以 `args.json` 的结构为准**（这也是第七节早就定下的：**扁平清单以 `args.json` 为准**），
判行为时以模板图/调用点为准；名字与文件结构都只是线索，不是结论。

#### 补五：`meowfficer` 同样是**配置门控**（`Meowfficer_BuyAmount > 0` 才买）

读源码：

* `meowfficer.py:36` `if self.config.Meowfficer_BuyAmount <= 0 …` / `:46` `if … > 0 …` → 买不买由这个数值决定；
* `:48` `self.meow_buy()` → `buy.py:15` 的 `MeowfficerBuy`，其中 `buy.py:186` `buy_amount = self.config.Meowfficer_BuyAmount`；
* 常量：`BUY_MAX = 15`（数量上限）、`BUY_PRIZE = 1500`（单价，货币单位按上游口径）。

本机配置（`config/alas.json`）：

| 键 | 值 |
| --- | --- |
| `Meowfficer.BuyAmount` | （本机未显式设置 → 走上游默认值） |

**这样"花费路径"的排查就有了一个可复用的结论形状**：`dorm` 与 `meowfficer` 都是
**"数值/开关 > 0 / true 才花"**，而值在本机配置里，可离线查。
剩下没判的只有 `research` 的付费项目（项目是否选到付费项，取决于它的选择规则，属于另一类判定）。

#### 补六：``research`` 的粗筛命中是**误报**，以及它真正的性质

回看粗筛表里 ``research`` 的命中：``preset_generator.py:119/132`` 匹配到的是
**``# Average time cost: 153.41…``** —— 那是**时间成本**的注释，不是花钱。**粗筛把它当成了花费信号，是误报。**

（同理 ``assets.py:8 DETAIL_COST`` / ``:36 RESEARCH_COST_CHECKER`` 也只是**素材名**里有 COST，
按本文件前几节反复得到的教训：**名字不是行为**。）

**``research`` 的真正性质**（读调用链得到，但**未逐项判定**）：
科研通过 ``research_project_start()``（``research.py:164``）等函数**开始项目**，
而科研项目本身会消耗资源（物资/魔方之类）——**这是任务的性质，不是某个分支**。
选哪个项目由上游的预设与规则决定（``research/preset.py``、``Research_Preset`` 等）。

**因此它的判定属于第三类**，与前两类并列：

| 类别 | 例子 | 判定方式 |
| --- | --- | --- |
| ① 默认路径就会花 | ``commission``（油满买食物） | 无配置可挡，要么别跑，要么改上游语义（本项目不动） |
| ② 配置门控 | ``dorm``（``BuyFurniture_Enable``）、``meowfficer``（``BuyAmount > 0``） | **查本机配置**即可 |
| ③ **任务性质就花** | ``research``（开始项目即消耗） | 只能靠**选择规则/预设**间接控制；要判定"这次会花多少"，得读它的项目数据与选择规则 —— **未做** |

**这一步的价值是"不再拿误报当依据"**：如果按粗筛表去放开 ``research``，会以为"没命中花费信号=安全"，
而真相是"命中是误报、花费是任务性质"——**两次都错**。

#### 花费路径：一页结论（**以此表为准**，下面几节是推导过程与证据）

| 模块 | 类别 | 判定 | 依据 |
| --- | --- | --- | --- |
| `commission` | ① 默认路径就花 | 油满时会**买食物消耗油**（三次失败才 `RequestHumanTakeover`） | `commission.py:563/595-600` |
| `dorm` | ② 配置门控 | **`Dorm.BuyFurniture.Enable`**（扁平键 `BuyFurniture_Enable`）为真才买家具；上游把 `BuyFurniture` 作为 **`Dorm` 下的分组**（不是独立任务） | `dorm.py:613/509/540-542`、`args.json` 结构 |
| `meowfficer` | ② 配置门控 | **`Meowfficer_BuyAmount > 0`** 才买（上限 `BUY_MAX=15`，单价常量 `BUY_PRIZE=1500`） | `meowfficer.py:36/46/48`、`buy.py:186` |
| `research` | ③ **任务性质就花** | 开始科研项目本身消耗资源；选哪个项目由上游预设/规则决定。**未逐项判定**（读项目数据与选择规则才能算"这次花多少"） | `research.py:164` 起 |
| `freebies` | 不花（这条路径） | `handle_battle_pass_popup()` 点的是弹窗**关闭按钮**（模板图是深红 ✕） | 模板图 + `battle_pass.py:39/52/83` |
| `reward` | 不花（已下钻四层） | `run → receive/mission → notice/all/weekly → collect` 全程无 `buy/purchase/OilMaxed/quick_finish/COST` | `reward.py` 各方法 |
| `tactical` | 无命中 | 粗筛无信号 | 粗筛 |
| `island` | 不是动作 | `DIC_ISLAND_RECIPE` 是**数据表**（含 `commission_cost` 字段） | `data.py:227-230` |

**用这张表回答"某个周期任务能不能无人值守跑"**：

* ①②类：查本机配置即可判定（①类无配置可挡）；
* ③类：**不能**凭本表下结论 —— 要么读它的选择规则与项目数据，要么第一次跑时有人看着；
* 其余：本表已判定不花，可直接按回归流程安排。

**方法（可复用到别的任务域）**：先读 `run()` 找花费分支与其**开关名** → 查本机配置里那个开关的值 →
判不出来时看**模板图/调用点**，**不要凭素材名或文件名下结论**（本文件里为此更正过三次）。

#### 补七：第三类（``research``）的边界 —— 花费数据在上游，**本项目不重算**

看目录结构就够了：

| 文件 | 大小 | 作用 |
| --- | --- | --- |
| ``module/research/project_data.py`` | **152.9 KB** | 上游的**项目数据库**（每个科研项目的属性/花费等） |
| ``module/research/selector.py`` | 11.1 KB | 选择逻辑（按用户预设挑项目） |
| ``module/research/preset.py`` | 32.5 KB | 预设（用户可选的研究倾向） |

**所以"某个科研项目会花多少"这个问题，答案在上游的数据表 + 选择规则里** ——
而本项目的铁律是：**上游的数据/IR 链不重写、不在适配层复制一份**（``AGENTS.md``）。
因此正确做法不是"我们算一遍花费"，而是：

1. 要**报**花费 → 由宿主读上游数据给出（像 ``periodic_plan`` 那样只报不判）；
2. 要**控**花费 → 改用户的**预设/选择规则**（上游本来就提供的旋钮），而不是在适配层加白名单；
3. 要**判**"这次会不会花" → 取决于预设与当时可选项，**静态判不出来**，属于"第一次跑时有人看着"的那一类。

**这条也把三类模型补完整了**：①②类可以静态判定（默认路径 / 配置开关），
③类**静态判不了**（数据+规则+当时选项共同决定），只能靠"报出来"或"跑时看着"。

#### 补八：新增只读 op ``config_get``（通用读配置），并更正一处旧记录

``op_config_get``：按**点分路径**读账号配置，**不写任何具体键名**（调用方给什么读什么）：

```
{"keys": ["Dorm.BuyFurniture.Enable", "Meowfficer.BuyAmount", "No.Such.Key"]}
→ values: {"Dorm.BuyFurniture.Enable": false, "Meowfficer.BuyAmount": null, "No.Such.Key": null}
→ missing: ["Meowfficer.BuyAmount", "No.Such.Key"]   （**缺失 ≠ false**，缺省走上游默认值）
```

**它当场更正了我先前的一处记录**：我此前用**扁平键** ``BuyFurniture_Enable`` 去查，得到"配置里没有"，
于是写成"本机未显式设置"；而按**嵌套路径** ``Dorm.BuyFurniture.Enable`` 读，值是 **``false``** ——
也就是**本机显式关掉了买家具**。两种读法的差别是真实存在的：配置文件是**嵌套**的，
扁平键只是上游内部拍平后的名字（第七节早就定下"以 ``args.json`` 的结构为准"）。

**这条对"能不能无人值守跑"的意义**：``dorm`` 在本机是"任务开着、但花费开关显式关闭"，
属于②类里最安全的一种组合；而判断方式就是这一条命令。

**用法建议**：要放开某个周期任务前，先用它把该任务的花费开关值报出来（配合花费路径表找键名），
再决定是否授权 —— 比读代码推"会不会走到那一支"更快，也更贴本机实际。

#### 执行环的验收口径（本人 2026-09-23 明确）

> "当前家具币不够用，你如果执行购买家具应该会提示不够钱；**但是能到这一步就说明已经能购买家具了，可以当成是购买家具闭环**。"

落到实现上就是：

* **目标动作**：`dorm` 域的"购买家具"（`BuyFurniture` 分支；本机 `Dorm.BuyFurniture.Enable=false`，
  要真跑需先在配置里打开 —— 或者由运行时按任务输入显式传，不改用户配置）；
* **验收判据**：**走到"发起购买/进入付款流程"这一步即为闭环成功**，即使游戏随后提示
  "家具币不足"、购买未完成 —— 那属于**账号资源状态**，不是功能缺陷；
* 因此结论词应当是能表达"跑到了、但结果由资源状态决定"的那种，而不是简单的 succeeded/failed；
  证据里要留下"走到了哪一步"（复用 `periodic_plan` 的勘察输出 + 运行期步骤）。

#### 执行环：第一次真机跑（2026-09-23，按本人给的验收口径）

**结论：购买家具闭环成立**（走到"发起购买"即算，家具币不足属账号资源状态）。

调用与结果：

```
op_periodic_run(task="dorm", allow_actions=true, confirm="dorm",
                overrides={"BuyFurniture_Enable": true})
→ decision=ran  target={module.dorm.dorm, RewardDorm}
  constructed=True  ran=True  elapsed_s=13.6
```

上游日志（真机）里的关键行：

```
There is a time-limited furniture available
[OCR_DORM_FURNITURE_COIN] 144
Click ( 935,  643) @ DORM_FURNITURE_BUY_ALL          ← **发起了购买**
[OCR_DORM_FURNITURE_PRICE] 1360
Not enough furniture coin, purchase is over          ← 144 < 1360，游戏侧中止
Fallback to dorm_page
```

**三条如实说明**：

1. **配置没有被我们改写**：`overrides` 只作用于本次构造出来的 config 对象（内存内），
   磁盘上的 `config/alas.json` 不含我们打开的开关 —— 但**上游任务自己会写调度状态**
   （`RewardDorm` 运行完调用 `config.task_delay()`，日志里可见
   `Save config ./config/alas.json, Alas.Scheduler.NextRun=…`），这是上游既有行为；
2. **本次只跑到"发起购买"**：因为家具币不足，购买未完成 —— 这正是本人口径认可的结果；
   要在资源充足时看完整购买（含确认弹窗与返回），需账号有足够家具币；
3. **执行入口的两道闸已验**（未授权/确认不匹配/任务名不存在/缺 task → 全部 denied 且未发生任何执行），
   真机跑通的是"两闸都通过"的那条路径。

#### 执行环第二个域：`reward`（真机跑通，2026-09-23）

**为什么选它**：执行环只跑通过 `dorm` 时，还不能说明这套入口是**通用的**（可能只是为宿舍凑巧能跑）。
`reward` 是花费路径表里**下钻四层确认无花费**的那个域 —— 用它验证既证明通用性，又是最安全的演示。

```
op_periodic_run(task="reward", allow_actions=true, confirm="reward")
→ decision=ran  target={module.reward.reward, Reward}  constructed=True  ran=True  elapsed_s=21.5
```

上游真机日志（在领取任务奖励，行为符合预期）：

```
[MissionState] MISSION_SINGLE
Click (1150, 134) @ MISSION_SINGLE
Mission claim receive
[MissionState] MISSION_UNFINISH
Mission collect finished
```

**结论**：历史记录证明 dorm / reward 两条动作路径能运行，但当时的适配器直接构造勘察到的类并
统一调用 `run()`，不能外推到 `opsi_*` 或活动入口。当前实现改为消费上游 Scheduler.Command、
任务绑定和 `AzurLaneAutoScript.run()`；域差异及参数继续由上游 `alas.py` 方法消费，没有维护任务特例表。

#### 执行环的产品路径（`kind = "periodic_run"`，2026-09-23 真机验证）

此前执行入口只有宿主 op（**只有进程外的诊断脚本能用**），产品路径是空的 ——
队列/报告/前端只认 `ITaskRunner`，用户没有正规入口。现在补齐：

```json
{"id":"run","kind":"periodic_run",
 "input":{"task":"reward","allow_actions":true,"confirm":"reward"}}
```

**历史真机验证（走队列，两条路径都验）**：

```
[任务] deny kind=periodic_run outcome=failed error_kind=internal
       error=未授权：需要显式 allow_actions=true（周期任务可能消耗账号资源）
[任务] run  kind=periodic_run outcome=succeeded elapsed=7s
```

这些记录来自旧的直接构造适配器，只证明当时的 dorm / reward 样本。新队列入口另以原生
`Scheduler.Command` 及 `AzurLaneAutoScript.reward` 跑通一次 `reward`：`periodic_plan` 后的
`periodic_run` 返回 `decision=ran`、`native_success=true`，上游实际点击 OIL、COIN 并返回主界面。
本机原始日志、配置前后快照在忽略目录 `data/mainline-device/20260923T145244-reward`，
脱敏队列、逐任务、断点和会话证据见 `docs/queue-evidence.md`，账号配置已按原始字节恢复；
点击 OIL/COIN 的控制台原件没有入库，归档只证明结构化调度与返回结果。此样本不证明其他周期域可运行。

新队列入口还以 `dorm` 跑了仅收取配置：`Dorm_Feed=false`、`Dorm_Collect=true`、
`BuyFurniture_Enable=false`。`periodic_plan` 找到上游 `Dorm` 绑定；`periodic_run` 经
`AzurLaneAutoScript.dorm` 返回 `decision=ran`、`native_success=true`；后续实时抓帧识别到
`page_dorm`。原始控制台日志显示从主页进入宿舍、进入 `DORM COLLECT`，随后上游报告
`Dorm collect timeout`，未记录 `DORM_QUICK_COLLECT` 点击；OCR 读到宿舍舰船 `0/2`。
因此只验证了原生调度、宿舍导航与空宿舍状态下的返回，**没有证明实际领取资源**。
原件留在忽略目录 `data/mainline-device/20260923T155210-dorm`；脱敏工件见
`docs/queue-evidence.md`。本次上游写入的调度时间在探针结束后按运行前配置原字节恢复。
历史家具币不足不代表当前仍不足，不能用它推断开启 `BuyFurniture_Enable` 不会消费资源。

当前产品路径先检查会话授权：会话未授权记前置条件不满足，
非 required 任务为 `skipped`、required 任务为 `failed`；两者都没有开始执行。
已授权会话中，宿主返回的 `denied` 仍带原因及 `constructed`/`ran` 证据。

**安全语义分两层**：任务侧先检查会话由 `--run --allow-actions` 授权，队列 JSON 不能把
默认 dry-run 或只读设备会话升级成动作会话。输入中的两道闸与顺序由宿主 op 保证，并由静态守卫
`periodic_run_gate_intact()` 检查（闸门必须在构造上游对象**之前**）；
`input.allow_actions` 只接受 JSON 布尔值 `true`，字符串 `"true"` 或 `"false"` 均不能授权。
任务侧将 `denied` 翻译成输入失败，将原生返回 `False`、`SystemExit` 和其他上游异常翻译成
`upstream_error`；`TaskEnd` 继续由上游 dispatcher 视为正常完成。

**`overrides` 的边界**：只接受当前任务绑定的字段，并通过上游 `config.override()` 登记为对象生命期
覆盖，不走字段赋值的自动持久化路径。**上游任务自己写调度状态**（如 `task_delay()` / `task_call()`）
仍会更新配置，那是原生调度语义，适配器不会回滚整个配置文件。
`periodic_run` 还会在任务前置校验中拒绝未知的顶层输入字段；例如把 `overrides`
误写成 `override` 时任务不启动，避免按未覆盖的上游默认配置执行。

2026-09-23 的 `freebies` 产品队列先勘察 `periodic_plan`、再由 `periodic_preflight`
确认原生绑定与不执行判定，然后以本次 `overrides` 关闭战令、钥匙、礼包和删信，
仅尝试功勋邮件领取。上游从活动页导航到邮件管理、选择功勋、点击批量领取与确认，
返回主界面；队列记录 `decision=ran`、`native_success=true`，抓帧识别主页。
但原始日志同时给出 `Mail claim success: False`，不能据调度成功声称功勋已到账。
原始日志和设备工件保留在本地忽略目录 `data/mainline-device/current-freebies-merit/`；
脱敏队列归档只证明前述调度与返页事实，不包含原始控制台点击日志。

## 通用观测任务（`observe`）

`ObserveTask` 只能由 `alashub queue --file` 调度。任务输入为
`{"seconds":20,"tick_seconds":0.5,"map":"main"}`，`map` 可省略或为上游 `main`/`os` 模式；
`navigate` 同样由队列以 `{"to":"page_campaign","max_hops":8,"rounds":1}` 输入调度。
两者复用宿主抓帧、识页、地图识别或页面图，不维护第二份规则表。

```json
{"tasks":[{"id":"observe","kind":"observe","input":{"seconds":2,"tick_seconds":0.5}}]}
```

```powershell
alashub queue --file observe.json --run --read-only-device --serial <device> --screenshot adb --control ADB
```

队列入口要求设备已配置，dry-run 的前置条件不满足时记 `Skipped`；`required` 决定其是否导致队列失败。
观测可在 tick 边界停止，故障必须留在任务证据中。其离线验收由 `observe_cases.json` 与
`verify_runtime.py` 覆盖。历史 `run` 兼容入口的只读真机记录为 4 tick、0 error，命中
`page_main` / `page_main_white`，宿主/设备各初始化一次。新队列入口已分别完成 6 tick 和导航后的 4 tick 真机观测；脱敏交叉审计见 `docs/queue-evidence.md`。
原始现场工件在忽略目录 `data/progress-audit-observe/20260923T105409`，未入库，原配置已恢复。
