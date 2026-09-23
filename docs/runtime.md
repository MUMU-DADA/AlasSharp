# 常驻运行时（R1）

R1 的目标是把 CLI 里的临时编排收敛成**可复用的运行时**：设备/宿主会话只起一次、
取消与超时是明确的、日志与证据是结构化的、错误有统一分类。业务判定（什么算通关）
不在这里 —— 那是 [结果合同](result-contract.md) 的事。

## 一、组成

```
src/Alas.Core/Runtime/
  SessionOptions.cs        一次会话/一批任务的输入（CLI 只负责把参数填进来）
  AlasSession.cs           常驻会话：宿主 + 设备后端 + 日志 + 工件目录 + 释放
  CampaignBatchRunner.cs   战役批量任务：逐关驱动 → 合同裁决 → 工件 → 失败即停/取消
  SessionLog.cs            结构化日志（内存条目 + JSONL 落盘）
  RuntimeErrors.cs         统一错误分类与 AlasRuntimeException
src/Alas.DataTool/RuntimeSelfCheck.cs   `alashub selftest-runtime`：替身宿主下的离线自检
```

CLI（`alashub campaign`）现在只做三件事：解析参数 → `AlasSession.Start` → `CampaignBatchRunner.Run`，
然后把结果排版出来。**它不再直接调用 `RunCampaignPlan`，也不再自己判定成功/失败**；
这一条由 `verify_architecture.py` 静态守着（`CLI 不直接驱动引擎`）。

## 二、三条阶段门槛怎么被证明

| 门槛 | 做法 | 断言位置 |
| --- | --- | --- |
| 同一进程连续运行多个任务时宿主和设备只初始化一次 | 替身宿主统计构造次数；设备配置次数单独统计；两次都必须 ≤ 1 | `verify_runtime.py`（`host_start_count`/`device_configure_count`；3 关连跑时后端调用 = 3 关 + 1 次设备配置） |
| 取消/超时/异常都能释放资源并保留证据 | 取消在**关卡边界**生效；未跑的关卡如实记为 `skipped`；宿主必须被 Dispose；每个关卡（含失败与被跳过的）都有工件 | 同上（`cancel_at_grade_boundary_keeps_evidence` 等 6 例） |
| CLI 参数不再复制一套业务状态机 | CLI 不出现 `RunCampaignPlan`/`InProcessVisionEngine`；编排只在 `Alas.Core/Runtime` | `verify_architecture.py` |

## 三、批处理的判定规则（明确写死，避免各处各判）

- **失败即停**（默认）：某一关失败后不再跑后面的关卡，后面的关卡记为 `skipped`。
  理由：出错后地图/账号状态未知，继续下一关会白耗石油与心情。
  需要旧行为（跑完全部关卡）时显式加 `--continue-on-error`。
- **批次的结论是最坏者**：`error` &gt; `refused` &gt; 未通关（撤退/战败/说不清/没打完）&gt; `cleared`。
  批次 `cleared` 只在**每一关都过合同且结算是 cleared** 时成立。
- **合同违例即失败**：每关都过 `SortieContract.Violations`；违例的关卡即使上游说通关也不算。
- **取消在关卡边界生效**：不打断正在执行的上游出击（它没有可中断点），
  与 `--max-seconds` 的既有语义一致（时间上限同样只在上游操作边界检查）。

## 四、证据（工件）布局

```
<--artifacts 目录>/<运行时间戳>/
  index.json            批次总表：每关结论、失败原因、宿主/设备初始化次数、是否提前停止
  sortie-<关卡>.json    单关：合同裁决 + 完整结果文档（失败关与被跳过的关也有一份）
  session-log.jsonl     结构化日志（每行一条，含 scope/level/fields）
```

失败帧仍然由上游侧写在同一个运行目录里（`failure_frame` 指向它），
因此"结果 → 步骤 → 调用栈 → 现场帧"整条链在**一个目录**里闭合。

## 六、运行报告（工件的只读汇总）

```powershell
alashub report --run <运行目录> [--json <报告.json>]
alashub report --artifacts <工件根目录>        # 取最新一次运行
```

报告做两件事，都是**只读**（不跑游戏、不改工件、不重判通关）：

1. **汇总事实**：队列/批次结论、逐任务与逐关卡条目、日志计数（条目/错误/警告）、
   宿主与设备初始化次数、工件数量；
2. **查证据完整性**：把发现写成机器可读的 findings：

| finding 码 | 含义 |
| --- | --- |
| `missing_artifact` | 引用的工件不存在（证据链断了） |
| `relocated_artifact` | 工件随运行目录搬迁（同名文件就在本目录，不算缺失） |
| `unreadable_artifact` | 工件读不出来 / 日志里有非 JSON 行 |
| `log_missing` | 缺 `session-log.jsonl` |
| `task_failed` / `task_skipped` / `stage_not_cleared` / `batch_failed` | 运行本身的失败项 |
| `contract_violation` | 单关结果没过结果合同（从 `sortie-*.json` 里读出来） |
| `state_incomplete` | `state.json` 记的已完成数超过队列里成功/跳过的任务数 |
| `run_not_found` | 运行目录不存在（退出码非 0） |

报告自身的退出码只反映"读得出来读不出来"：**一次失败的运行，报告照样是成功的**。
它给 R4 前端提供的就是这份 `--json`。

## 七、已知边界

- 取消粒度是**关卡**，不是单次操作：正在跑的 `Campaign.run()` 不会被中途打断。
  真要中途打断，得在上游操作边界插检查点，那是 R3 的活（现在没有证据说明需要）。
- `session-log.jsonl` 在会话释放时一次性落盘；进程被强杀时只有控制台输出，
  没有文件。要更强的保证需要边写边刷盘，等出现"强杀后查不到日志"的真实需求再做。
- 目前只有战役任务走运行时；`run`（观测循环）、`goto`（导航）仍在 `Alas.DataTool`，
  等 R2 做任务域切片时一并搬。

## 八、复现

```powershell
dotnet build src\Alas.DataTool\Alas.DataTool.csproj -c Release
python tools\diagnostics\verify_runtime.py          # 11 例：只初始化一次 / 失败即停 / 取消 / 工件 / 队列
python tools\diagnostics\verify_report.py           # 报告读得出事实；缺工件/缺日志/目录不存在都会被指出
python tools\diagnostics\verify_architecture.py     # CLI 不复制业务状态机
```

## 九、报告内联什么、不内联什么（设计取舍，别当成漏做）

审计"同一份数据的多个出口"时核对过一处，结论是**有意为之**，写在这里免得以后被当成缺陷修掉：

| 内容 | 在哪 | 报告里 |
| --- | --- | --- |
| 任务结论 / `error_kind` / `error` | `queue.json`、`task-*.json` | **内联**（列表与详情都用） |
| 停止信息（`stopped_early` / `stop_reason`） | `queue.json`、`index.json` | **内联**（列表第一屏就要） |
| 任务边界快照 | `task-*.json` | **内联**（判断跨任务复位就靠它） |
| 日志计数与 scope 分布 | `session-log.jsonl` | **内联**（是摘要，不是明细） |
| **各域的证据明细**（如战役的逐关 `stages`、调度的 `listed`） | `task-*.json` 的 `evidence` | **不内联，只给 `artifact` 路径** |

理由：域证据是**可以很大的对象**（战役域带逐关结论、调度域带任务清单），
把它们复制进报告等于同一份数据两处存在、两处过期；而报告要给的是"**发生了什么 + 去哪个文件看**"。
`artifact` 路径已经在每个条目里，消费方按需读。

**什么时候该改这个决定**：如果出现"只读报告、拿不到工件文件"的消费方（例如只能拿 JSON 发出去的前端），
那就该给它一个**单独的导出**（按需内联），而不是把报告本身变重。

## 十一、"什么算一次运行"——判定只有一处

规则：**一个目录算一次运行，当且仅当它自己有 `queue.json`（队列运行）或 `index.json`（单批运行）。**
实现在 `RunReport.IsRunDirectory(directory)`，三个消费者共用：

| 消费者 | 不共用时的表现（都实测过） |
| --- | --- |
| `report --artifacts`（取最近一次） | 工件根目录里一个排序靠后的杂目录会"赢"，报告去读它 → 报出 `log_missing` 之类**误导性发现** |
| `runs`（列表） | 列表里多出一条不是运行的目录 |
| `--resume`（找上一次断点） | 读到**别的运行**的 `state.json` → **静默跳过其实没跑过的任务**（最难查的那种错） |

为什么值得单列一节：这条规则以前只存在于**约定**里（"工件都落在 `<artifacts>/<时间戳>/`"），
所以每个消费者各自扫目录、各自判"最近"。三次修复（`report`/`runs` 一次、`--resume` 一次）
都是同一个根因的不同表现。**再有人需要"最近一次运行"，请调 `IsRunDirectory`，不要另写一遍扫描。**

配套的两条（同一类问题的另一半）：

* **派生数据不写进运行目录**：生成的视图写在 `<artifacts>-views/`（根目录**之外**）——
  运行目录里的文件数是"工件数"的一部分，往里塞页面等于篡改证据；
* **回归守着这两条**：`verify_report.py` 有"混进来的目录不算运行"反例；
  `verify_report_html.py` 有"生成视图不改动运行目录里的文件"不变量。

## 十二、断点文件的**累积语义**（曾经会被"什么都没干的一次运行"清空；已修）

**曾经的样子（读代码得到，2026-09-23）**：`TaskQueue.WriteState()` 每次都**重写** `state.json`，
内容只包含**本次** `queue.Tasks` 里结论为 `Succeeded` / `DryRun` 的任务：

```csharp
var done = new JsonObject();
foreach (var task in queue.Tasks)
    if (task.Outcome is TaskOutcome.Succeeded or TaskOutcome.DryRun)
        done[task.Id] = task.OutcomeName;
File.WriteAllText(path, new JsonObject { ["completed"] = done, ... }.ToJsonString(...));
```

**后果（静默、且会浪费资源）**：

| 次 | 发生了什么 | `state.json` 里剩什么 |
| --- | --- | --- |
| 第 1 次 | A 成功、B 失败 | `{A}` |
| 第 2 次 `--resume` | A 被跳过、B 重试成功 | **`{B}` —— A 没了** |
| 第 3 次 `--resume` | 读到的"已完成"只有 B | **A 被重新执行** |

对战役批量域来说，"重新执行"就是**再打一遍、再花一次石油**。而这一切不会报错，
只会在日志里表现为"这次怎么又多打了几个图"。

**为什么会这样**：`completed` 是**累积语义**（"到目前为看已经做完的"），但写入时用的是
**本次运行的切片**。第 109 轮我给 `--resume` 补断言时就撞上过这个语义不清楚的地方
（那次我选择只断言"断点来源"，没断言跳过数量 —— 现在知道为什么数不对了）。

**已修（提交 `28bdb8a`）**：写入时**与上一次的断点合并**——
`TaskQueue` 在被 `--resume` 唤起时已经知道上一份 `state.json`（`TaskQueueFile.LatestState`），
把它的 `completed` 读进来做并集，再写新的；**不要清空**。
配套回归：`verify_os_state.py` 的断点续跑段第三次续跑断言"**跳过 1 个已完成任务**"且点名 `a-os`（即累积语义生效、A 不重跑），同一条还盯着"排序靠后的杂目录不算运行"。原本设想的负例（"清空"）已被这条覆盖：改回旧写法它会立刻变红。
以及"清空"这条负例（当前行为）应当变红。

**过程说明（为什么先记、下一轮才改）**：这属于会**花资源的路径**（改动影响哪些任务会被重新执行）。
按本项目的规矩，改它要先有现场证据 + 回归；现在只有代码级证据，且我的上下文预算已尽。

## 十三、怎么看运行报告（界面用法）

```powershell
# 一次运行 → 那一份的单文件视图
python tools\report_html.py <artifacts>\<时间戳>            # 产物：<时间戳>\report.html

# 工件根目录（多次运行）→ 索引 + 每次运行各一页
python tools\report_html.py <artifacts>                     # 产物：<artifacts>\index.html
                                                            #       <artifacts>-views\<时间戳>.html
```

* 产物是**单文件 HTML**（自带样式、无外部引用），双击就能看，也可以直接发给别人；
* 页面里能看到：队列/批次结论、提前停止与原因、**宿主启动/设备配置次数**、日志条目与错误、
  日志来源（scope 分布）、任务表（含**边界快照**）、关卡表、以及**发现**（证据完整性问题）；
* **页面写在运行目录之外**（`<artifacts>-views\`）：运行目录里的文件数是"工件数"的一部分，
  往里放生成物等于篡改证据（第十一节）；
* 验收在 `tools/diagnostics/verify_report_html.py`：不丢事实（数据面里的事实都要出现在界面里）、
  单文件自足、缺工件也能看、生成视图不改动运行目录。

**它为什么长这样**：R4 的"框架选型"一直悬着，但"先把数据面看得见"不需要等选型 ——
静态单文件既能立刻用，又是对数据面的**检查**（界面上显示不出来的东西，往往是数据面缺了字段）。

### 交互：暂时不做，以及要做时走哪条路（写给下一轮）

现状是**静态**视图（结论卡片 + 任务表 + 关卡表 + 发现 + 原始 JSON）。没做筛选/展开。
这不是漏了，而是**有意的顺序**：先把"数据面够不够撑起一屏"验掉（第 101-104 轮），再谈交互。

**要做时按这个顺序，且优先无 JS**：

| 优先级 | 做法 | 为什么这样做 | 怎么离线验 |
| --- | --- | --- | --- |
| 1 | **页首锚点**：任务 / 关卡 / 发现 / 原始数据面 四个跳转链接 | 一次运行可能有几十个关卡，翻页成本高；纯 HTML 就能做 | 断言 HTML 含这些锚点与其对应 `id` |
| 2 | **失败优先区块**：先列一个"失败与未通关"的小表（只含 `failed` / `error` / 非 `cleared` 的条目），再给全表 | 打开报告的第一诉求是"哪儿出问题了"；**生成期过滤**，不需要运行时筛选 | 有失败 → 该区块出现且**只**含失败项；无失败 → 明确写"无" |
| 3 | 折叠（`<details>`）长表格 | 纯 HTML 标签，零 JS | 断言 `details` 标签存在即可（行为靠浏览器） |
| 4 | 真正的筛选框 | 需要 JS，且**离线无法验行为** —— 只有到这一步才值得引入脚本 | 只能人工验；引入时保持单文件、无外部依赖 |

**明确不做的事**：引入前端框架。到目前为止所有需求（看结论、看失败、看证据、跳转）都能由
"生成期把结构排好"满足；框架会带来构建链与依赖，而收益要在**多人长期使用**时才出现 ——
那正是 R4 框架选型该讨论的问题，不是现在顺手决定的事。
