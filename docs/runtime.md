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
