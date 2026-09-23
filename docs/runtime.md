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
  QueueExecution.cs        队列文件入口：runner 注册、断点、停止文件、任务调度
  SessionLog.cs            结构化日志（内存条目 + JSONL 落盘）
  RuntimeErrors.cs         统一错误分类与 AlasRuntimeException
src/Alas.DataTool/RuntimeSelfCheck.cs   `alashub selftest-runtime`：替身宿主下的离线自检
```

CLI（`alashub campaign`）现在只做三件事：解析参数 → `AlasSession.Start` → `CampaignBatchRunner.Run`，
然后把结果排版出来。**它不再直接调用 `RunCampaignPlan`，也不再自己判定成功/失败**；
`alashub queue` 则把队列文件和公共运行参数交给 `QueueExecution.RunFile`，由运行时创建单会话、
注册任务、处理断点与停止请求。两条边界由 `verify_architecture.py` 静态守着。

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
  index-2.json          同一队列的第二个战役批次（后续依次编号）
  sortie-<关卡>.json    单关：合同裁决 + 完整结果文档（失败关与被跳过的关也有一份）
  session-log.jsonl     结构化日志（每行一条，含 scope/level/fields）
```

失败帧仍然由上游侧写在同一个运行目录里（`failure_frame` 指向它），
因此"结果 → 步骤 → 调用栈 → 现场帧"整条链在**一个目录**里闭合。
队列里多次运行战役批次时，各批索引和单关文件必须互不覆盖；任务证据中的
`index_artifact` 指向本任务的批次索引，报告汇总所有批次并核对引用。
多个批次结论不一致时，报告的单值 `batch_outcome` 记为 `mixed`，逐任务结论仍以
任务工件里的 `evidence.batch_outcome` 为准。
项目相对的 `--artifacts`、`--resume-state` 和 `--stop-file` 在宿主启动前
固定到调用方工作目录，避免宿主切换当前目录后读写到另一处。
`verify_artifact_paths.py` 覆盖这三个相对路径入口。

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
| `duplicate_batch_index` | 多个战役任务引用同一个批次索引，证据关联不完整 |
| `log_missing` | 缺 `session-log.jsonl` |
| `task_failed` / `task_skipped` / `stage_not_cleared` / `batch_failed` | 运行本身的失败项 |
| `contract_violation` | 单关结果没过结果合同（从 `sortie-*.json` 里读出来） |
| `state_incomplete` | `state.json` 记的已完成数超过队列里成功/跳过的任务数 |
| `run_not_found` | 运行目录不存在（退出码非 0） |

报告自身的退出码只反映"读得出来读不出来"：**一次失败的运行，报告照样是成功的**。
它给 R4 前端提供的就是这份 `--json`。

## 七、已知边界

- 战役取消粒度是**关卡**：正在跑的 `Campaign.run()` 不会被 Ctrl-C 中途打断。
  显式本局撤退请求在战斗边界调用上游 `withdraw()`（见第十七节）；只读观测可在 tick 边界停止。
- `session-log.jsonl` 在会话释放时一次性落盘；进程被强杀时只有控制台输出，
  没有文件。要更强的保证需要边写边刷盘，等出现"强杀后查不到日志"的真实需求再做。
- 战役、观测与导航都复用常驻会话；观测和导航只能通过通用任务队列进入
  `ObserveTask` / `NavigateTask`。`run` 与 `goto` 只保留弃用提示，不能再各自解释任务输入、
  创建会话或驱动任务。
- 导航遇到未建模画面时的返回键由宿主调用上游 `Device.adb_shell(['input', 'keyevent', '4'])`。
  当前上游 `Device` 没有 `back()` 方法；该接口的成功和失败透传由 `verify_device_back.py` 覆盖。

本轮观测验收：离线运行时用例包含故障注入、旧帧隔离、输入校验、会话复用和取消；
历史 `run` 兼容入口的真机记录为 4 tick、0 error，命中 `page_main` / `page_main_white`，
宿主/设备各初始化一次。
原始工件保存在忽略目录 `data/progress-audit-observe/20260923T105409`，不入库；设备参数留在本机，
原账号配置已按字节恢复。新 `queue --file` 入口另有 6 tick、6/6 抓帧及往返导航后 4 tick、4/4 抓帧的真机记录，队列/任务/断点/会话交叉审计见 `docs/queue-evidence.md`。
这些证据不代表所有地图模式或后端均已验收。

当前产品入口如下。观测使用只读设备授权；导航是动作任务，必须显式授权动作会话。

```json
{"tasks":[{"id":"observe","kind":"observe",
  "input":{"seconds":2,"tick_seconds":0.5}}]}
```

```powershell
alashub queue --file observe.json --run --read-only-device --serial <device> --screenshot adb --control ADB
```

```json
{"tasks":[{"id":"navigate","kind":"navigate","required":true,
  "input":{"to":"page_campaign","max_hops":8,"rounds":1}}]}
```

```powershell
alashub queue --file navigate.json --run --allow-actions --serial <device> --screenshot adb --control ADB
```

导航沿用上游页面图、变体择优和未建模画面的返回自救；多回合、失败即停与取消都在核心任务中处理。
默认设备 I/O 使用上游配置，显式 `--screenshot` / `--control` 优先，`--adb` 仅保留参数兼容。
导航新队列入口已完成一次主界面到战役页往返及两轮战役页导航；脱敏证据见 `docs/queue-evidence.md`。未解锁的页面与其他导航路径仍须单独验证。

2026-09-23 战术页导航的首次真机队列在侧边功能面板失去识页结果。对同一静止画面重复抓帧时，
`device_capture_set(raw=true)` 不能识别 `page_reward`，普通抓帧却能识别；连续约 13 秒的
raw 采样仍为空命中，排除瞬时动画。根因是视觉宿主在 raw 分支把截图后端已返回的颜色通道
额外交换了一次。现已统一按后端返回像素保存，再由 `load_image` 读回；
`verify_device_capture_color.py` 用带有非对称 RGB 通道的帧逐像素验证 raw 与普通路径。
人工进入战术页后的只读抓帧先确认当前账号可访问该页。修复后的五任务产品队列
从功能面板回主页、导航至 `page_tactical`、抓帧识页、返回主页并再次抓帧，五项均成功；
宿主与设备各初始化一次。脱敏工件见 `tools/diagnostics/queue-evidence/20260923T203555/`。
这只验证当前账号的战术页导航与返页，不证明周期 `tactical` 执行或其他导航路径。

## 八、复现

```powershell
dotnet build src\Alas.DataTool\Alas.DataTool.csproj -c Release
python tools\diagnostics\verify_runtime.py          # 50 例：会话 / 批次 / 队列 / 导航 / 观测
python tools\diagnostics\verify_device_capture_color.py # raw / 普通抓帧像素一致
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

当前 `state.json` 的完成项同时保存请求身份（kind、input、required）与会话运行参数。
`--resume` 只继承当前队列中身份完全一致的完成项；同 id 改输入、改域或从 dry-run 切到
真实运行都会重跑。旧版只有完成 id 的断点不再用于跳过任务，因为无法验证它对应的请求。
`verify_cli_errors.py` 用真实 CLI 覆盖改输入和 dry-run 切真实运行，`verify_os_state.py` 继续
覆盖同请求的累积续跑。
显式 `--resume-state <文件>` 直接读取该文件，不再忽略文件名并转读同目录的 `state.json`。
以及"清空"这条负例（当前行为）应当变红。

**过程说明（为什么先记、下一轮才改）**：这属于会**花资源的路径**（改动影响哪些任务会被重新执行）。
按本项目的规矩，改它要先有现场证据 + 回归；现在只有代码级证据，且我的上下文预算已尽。

## 十三、怎么看运行报告（界面用法）

```powershell
# 一次运行 → 那一份的单文件视图
python tools\report_html.py <artifacts>\<时间戳>            # 产物：<artifacts>-views\<时间戳>.html

# 工件根目录（多次运行）→ 索引 + 每次运行各一页
python tools\report_html.py <artifacts>                     # 产物：<artifacts>\index.html
                                                            #       <artifacts>-views\<时间戳>.html
```

* 产物是**单文件 HTML**（自带样式、无外部引用），双击就能看；它可能包含本机路径和任务证据，对外分享前必须脱敏；
* 页面里能看到：队列/批次结论、提前停止与原因、**宿主启动/设备配置次数**、日志条目与错误、
  日志来源（scope 分布）、任务表（含**边界快照**和可展开的完整任务证据）、关卡表、以及**发现**（证据完整性问题）；
* **页面写在运行目录之外**（`<artifacts>-views\`）：运行目录里的文件数是"工件数"的一部分，
  往里放生成物等于篡改证据（第十一节）；
* 验收在 `tools/diagnostics/verify_report_html.py`：不丢事实（数据面里的事实都要出现在界面里）、
  单文件自足、缺工件也能看、生成视图不改动运行目录。

**它为什么长这样**：R4 的"框架选型"后来定了"**暂不引入前端框架**"（理由与重评触发条件见 `docs/architecture-roadmap.md` 附二）—— 而"先把数据面看得见"本来就不需要等选型：
静态单文件既能立刻用，又是对数据面的**检查**（界面上显示不出来的东西，往往是数据面缺了字段）。

### 交互：暂时不做，以及要做时走哪条路（写给下一轮）

现状是**静态**视图（结论卡片 + 任务表 + 关卡表 + 发现 + 完整原始 JSON）。
原始报告和逐任务证据可展开；尚未做筛选。
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

#### 交互进度：第 1、2 级已做（第 133 轮）

| 级 | 状态 | 实现 | 验证方式 |
| --- | --- | --- | --- |
| 1 页首锚点 | **已做** | 顶部一行"跳到：失败与未通关 / 任务 / 关卡 / 发现 / 原始数据面" | 断言 HTML 含这些 `id` |
| 2 失败优先区块 | **已做** | 卡片下方先给"失败与未通关"表（任务侧看结论、关卡侧看 `cleared`），无失败时明确写"无" | 造一次含失败任务的运行，断言该表里出现它 |
| 3 `<details>` 折叠 | 未做 | — | 断言标签存在 |
| 4 JS 筛选框 | 未做（按计划最后做） | — | 只能人工验 |

实测（一次含失败任务的运行）：`id="failures"` / `id="tasks"` / `id="findings"` / `id="raw"` 均在，失败区块里出现该任务；
队列结论 failed、条目 2、发现 1。

**两个如实标注的不足**：

1. **锚点可能悬空**：某份运行没有关卡时，`#stages` 这个链接没有对应目标（点了没反应）。
   要修的话按"渲染了哪几节再决定导航项"来做（不是难事，但没做）；
2. **第 1、2 级还没有断言**：本轮只手工验过。下一步应在 `verify_report_html.py` 里补两条——
   (a) 四个锚点 `id` 必须存在（按实际渲染的节）；(b) 构造一个含失败任务的运行，断言失败区块
   **只**列失败项、且不含已通关项。

#### 交互进度补充（第 134-136 轮）：第 1–3 级已完成

| 级 | 状态 | 断言（都在 `verify_report_html.py`，可离线跑） |
| --- | --- | --- |
| 1 页首锚点 | **已做** | 锚点 `id` 存在 + **无悬空锚点**（抓出页首所有 `href="#x"`，要求文档里都有 `id="x"`） |
| 2 失败优先区块 | **已做** | 无失败 → 区块写"无"；有失败 → 区块**只**列失败项（造一次含失败任务的运行来验） |
| 3 `<details>` 折叠原始数据面 | **已做** | HTML 含 `<details>`/`<summary>`；`id="raw"` 在 `<details>` 上，锚点跳过来仍能展开 |
| 4 JS 筛选框 | 未做（按计划最后做；**只能人工验**） | — |

**一条从缺陷里长出来的设计规则（写给以后改这个界面的人）**：

> **界面上的"失败"按结论判，不按"有没有通关"判。**

第一版我写的是"关卡侧看 `cleared` 是否 false"，于是 **dry-run 的关卡全被列进失败区块**
（dry-run 本来就不会通关）—— 正好违反本项目那条硬规矩：**"没跑"与"跑失败"必须分开**
（`AGENTS.md` 任务域边界）。现在的判据只收失败类结论
（`failed` / `error` / `incomplete` / `defeated` / `ended_unknown` / `withdrawn`），
`dry_run` / `skipped` 一律不算。

**值得记的是它怎么被发现的**：不是审阅看出来的，是**写完"无失败时必须写无"这条断言、一跑就红**。
所以第十三节开头那句"先验数据面再谈交互"还应该补半句：**断言要跟功能一起写**——
这一轮里，写断言的动作为我抓出了一个语义错误、并顺手把悬空锚点变成了通用断言。

## 十四、导航不到某个页面时：排查顺序（第 152-153 轮总结）

这三步是这一夜找 `page_guild` / `page_meowfficer` / `page_os` 时**实际走通的顺序**。
先按它走，能省掉两次错误推断（我当时先后怀疑"模板不匹配"与"坐标错了"，都被证据否掉）。

| 步 | 做什么 | 命令 / 判据 | 说明什么 |
| --- | --- | --- | --- |
| 1 | **入口在不在屏上** | `probe_asset_match.py <真机帧> <素材id>` —— 看模板分与颜色 | 分低 → 素材/判据问题（如 `IN_MAP` 那次）；分高 → 继续第 2 步 |
| 2 | **点了有没有反应** | 对比点击前后两帧的页面判定（或直接抓帧看） | 画面**完全不变** → 入口惰性（多半未解锁）；**弹回上一页/主界面** → 也是未解锁，但游戏会"弹回"；**画面变了但认不出** → 页面规则问题 |
| 3 | **是不是账号没解锁** | 看卡片美术是否发灰、查该功能的解锁条件 | 本账号已知三处：`page_guild`（惰性）· `page_os`（惰性，模板分 0.9990）· `page_meowfficer`（弹回主界面） |

识页结果本身异常时，还要在同一静止画面比较 raw 与普通抓帧的像素和命中结果。
战术页首次失败就属于 raw 颜色通道错误，不能从空命中直接推断页面未解锁或上游模板失效。

**两种最容易犯的错**（我都犯过）：

1. **凭一次日志里的低分推断"整类素材不匹配"** —— 真机帧上一量就翻（`STRATEGY_OPEN` 其实 1.000）；
2. **凭"入口在屏"推断"是导航/点击链路的 bug"** —— 其实入口可能**渲染着但不可用**。

**所以处置顺序永远是**：先量（第 1 步），再看行为（第 2 步），最后才谈账号或代码；
**不要**先去改素材、改坐标或放宽闸门 —— 那三者会把一个正确的实现改坏。

## 十五、小型导航环境（**已实现**，第 160-161 轮；下面是规格与用例位置）

**为什么现在写**：第 43 轮只写了目标（"替身宿主扩成小型导航环境"），没写清**替身要回答哪些 op、
状态怎么迁移、以及它要撑起哪几条断言**。而它现在**同时服务两个需求**（所以优先级比当时更高）：

1. **导航任务的断言**：多跳成功 / 不可达 / 中途自救（第 43 轮的原需求）；
2. **失败诊断后缀的断言**：让"点击后画面不变"，看第 156 轮加的那句后缀有没有出现（新需求）。

### 替身要实现的行为（最小集）

| op | 替身应回答 |
| --- | --- |
| `ui_page_graph` | 一张**固定的小图**：如 `page_a →(ui/A_TO_B)→ page_b →(ui/B_TO_C)→ page_c`，另加一条 `page_b →(ui/B_TO_DEAD)→ page_dead`；每页带 `check`（替身自己判"当前页"，不必真识别） |
| `page_current` | 返回**状态机里的当前页**（初始给 `page_a`） |
| `appear_on` / 按钮匹配 | 对"当前页的出边按钮"返回命中；对其它返回不命中（够用即可，不必真算模板） |
| `device_click(x,y)` | 按坐标反查是哪条边的按钮中心 → **迁移当前页**；查不到就**不变**（这正是"点了没反应"的模拟） |
| `device_capture_set` | 空实现（导航器会调，但替身不需要像素） |

### 三条断言（写进 `selftest-runtime`，全部离线）

1. `navigate_to_c_succeeds`：`page_a` 出发到 `page_c` → `Succeeded`，证据里 2 跳、`final_pages=[page_c]`；
2. `navigate_to_dead_reports_inert_entry`：目标 `page_dead`（点了不变）→ `Failed`，且失败原因里**含那句诊断后缀**
   —— 这条把第 156 轮的改动钉住；
3. `navigate_unknown_target_skipped`：目标不在替身图里 → 前置条件不满足记 `skipped`（不是 failed）。

### 边界与纪律

* **替身不许复制上游规则**：图是**测试夹具**，不是第二份页面表；它只服务断言，不参与产品路径
  （产品路径永远用 `ui_page_graph` 的真上游数据）；
* 断言只依赖**行为**（跳数、结论、失败原因片段），不依赖替身内部实现，将来换替身不用改断言。

**做完之后**，`docs/handover-r0-r2.md` 第十节 C 类里那两条（导航任务断言、诊断后缀断言）一起打勾。

## 十六、CLI 的可见性：`[任务证据]` 行只属于**队列路径**

第 173 轮写验收时踩到一件事，值得单独记：**同一个任务域，在不同命令路径下可见性不同。**

| 命令路径 | 打印什么 | 例子 |
| --- | --- | --- |
| `queue`（队列） | 每个任务一行 `[任务证据] <该域的摘要>` + `[任务工件]` + `[队列结果]` | `[任务证据] batch_outcome=dry_run cleared=False stages=1` |
| `campaign`（单批） | `[合同]` / `[批次]` / `[工件]` / `[失败]` / `[结算证据]` —— **没有** `[任务证据]` | `[合同] 合规 outcome=dry_run cleared=False` |

**为什么会这样**：`[任务证据]` 是**队列打印机**（`PrintQueueReport`）按域写的摘要行；
单批命令有自己的一套输出（`[合同]` 那行已经把结论说清了，再打一遍证据是重复）。

**因此验收脚本要注意**：想断言某个域的 `[任务证据]` 行，必须走**队列路径**
（`queue --file … --artifacts …`），而不是对应的单批命令 —— 第 173 轮第一版就是走错了路径才红的。

**已经断言到的域**（`verify_cli_evidence.py` + 各域脚本）：

| 域 | 断言在哪 |
| --- | --- |
| 战役批量 · 账号状态 · 大世界探针 | `verify_cli_evidence.py`（队列路径，缺存档帧则显式跳过） |
| 调度状态 | `verify_task_schedule.py` |
| 配置开关 | `verify_config_get.py` |
| 周期任务清点 | `verify_task_catalog.py` |

**要不要给单批路径也加 `[任务证据]`**：不加。单批路径的 `[合同]` 已经覆盖结论，
再打一份摘要只会让两条输出互相漂移（这一夜的老问题：**副本必然漂移**）。

## 十七、运行中请求撤退（`withdraw.request`）

**用法**：跑战役时，在**运行目录**里创建一个名为 `withdraw.request` 的文件即可（内容随意）：

```powershell
alashub campaign campaign.campaign_main.campaign_1_1 --run --allow-actions --artifacts runs\demo
# 另开一个终端（运行目录在 artifacts 下，名字是时间戳）：
New-Item -ItemType File runs\demo\<时间戳>\withdraw.request
```

**语义**：文件出现即请求。宿主在**下一次战斗之前**（那时正是地图界面、可撤退的状态）
调用**上游自己的** `withdraw()`，本局以 `CampaignEnd('Withdraw')` 结束，
合同据此判 `outcome=withdrawn`、`cleared=False`（**撤退不是通关**）。

与 `--stop-file` 的区别：`--stop-file` 是"停下队列"（在任务边界生效），
`withdraw.request` 是"让**本局**按玩家撤退结束"（在战斗边界生效）。

**为什么这样实现**（`tools/s3_campaign_execution.py` 的挂钩）：

* 挂钩点选"每次战斗之前" —— 那是唯一既在地图界面、又不与上游的出击循环抢时间的位置；
* 调用上游的 `withdraw()` 而不是自己点按钮 —— 它的收尾是 `raise CampaignEnd('Withdraw')`
  （`module/map/map_operation.py:410`），于是异常调用栈里有 `withdraw` 帧，
  正好命中合同既有的判据（`tools/s3_campaign_outcome.py:72`）。**没有新写一套撤退逻辑。**

**真机证据（2026-09-23，1-1）**：战斗 1 打到一半时创建请求文件 →

```
step=execute_a_battle round=2 outcome=withdrawn
step=withdraw outcome=withdrawn
[结算证据] withdrawn=True  rank=-  combat_status=False  stage_observed=True
[合同]     合规 outcome=withdrawn cleared=False
[批次]     outcome=withdrawn 原因=关卡未通过: withdrawn
```

这是 R0 需要的"**本局撤退判 `withdrawn`**"真机记录；它只补齐本局撤退，
不能据此声称合同七个结论都有真机覆盖。脱敏工件归档在
`tools/diagnostics/evidence/20260923T093800`，`audit_real_records.py` 同时核对合同、
批次索引、会话日志和调用链，并重建 `result-evidence.md`。

**防线**：`verify_architecture.py` 的 `withdraw_hook_present()` 断言挂钩与路径约定都还在 ——
它们失效的方式是静默的（请求文件出现却没人理，不报错、不失败）。
