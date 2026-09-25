# 上游引擎重写：目标与流程

> 本文档记录"用 C# 重写上游执行引擎、最终只依赖上游静态规则"这一目标的**口径、范围与推进流程**。
> 依赖边界、上游结构与 A/B/C 分类的证据见 [架构梳理](ARCHITECTURE-NOTES.md)。
> 定位：属路线图 **R5（宿主替换评估）** 级别的工作；在满足门槛之前，生产路径保持现状不变。

## 1. 目标

### 1.1 最终形态

1. **关卡自动化流程由 C# 执行**：地图移动与寻路、战斗回合决策、战役流程、任务调度、周期任务、大世界状态机等"引擎逻辑"，不再调用上游 Python 代码；
2. **上游只提供静态规则与静态配置**：关卡 `MAP` / `Config` / `Fleet` / `Enemy` 等声明、任务目录、页面关系、阈值参数、素材索引，全部以数据形式（`data/*.json`）消费；
3. **识图引擎不在重写范围内**（明确例外）：图像识别——模板匹配、颜色匹配、峰值检测、OCR、素材解析——**允许保留现有 Python/上游实现**，以受控接口方式调用。它**不计入"上游逻辑依赖"**。

### 1.2 术语口径（避免歧义）

| 说法 | 含义 |
| --- | --- |
| "只依赖上游静态规则" | 指**引擎逻辑**不依赖上游 Python 代码；规则与配置以静态数据消费 |
| "识图不重写" | 识别能力保持现状（可继续经 `IVisionEngine` / Python 宿主），但必须与引擎逻辑解耦：引擎只通过接口取**识别结果**，不继承上游的流程代码 |
| "上游引擎" | `module/**` 的执行逻辑 + `campaign/**` 的关卡覆写方法（实测 3192 个覆写方法 / 1396 个文件） |

### 1.3 现状与目标的差距（量化）

| 项 | 现状 |
| --- | --- |
| 静态规则消费点（已达成） | 32 处（`Catalog.Open` / `UpstreamData` / `AssetCatalog` / `MapIR` 等） |
| 上游逻辑调用点（待迁移） | 33 处，集中在 `Device/DeviceController`、`Tasks/ObserveTask`、`Runtime/AlasSession`、`Runtime/CampaignBatchRunner`、各 Task 与 Diagnostics |
| 待拆接口面 | `IVisionEngine` 24 个方法（其中识别类方法按第 1.1.3 条可保留） |
| 规模最大的待处理项 | `campaign/**` 关卡覆写方法 3192 个 / 1396 文件 |
| 上游逻辑总量 | `module/**` 385 个 py，16 个子系统 |

## 2. 范围界定

### 2.0 实施约束（已确认）

- **允许使用 C# 第三方库（NuGet）**：第 2.3 节"可写但成本高"的项可借助现成库（ADB 客户端、压缩与图像编解码、数值库、YAML/解析器、相似度算法等），不必只用 BCL；
- **有模拟器可用于真实验证**：P2 要求的"真实路径证据"（成功结算 + 返回章节页）可在模拟器上执行；设备动作仍按仓库规则**串行**，用户正在操作账号时停止全部设备动作；
- **识别引擎不重写**：见第 1.1 节第 3 条与第 2.2 节。

### 2.1 在范围内（要迁移到 C#）

| 域 | 现状 | 目标 |
| --- | --- | --- |
| 战役流程与关卡执行 | 直接调原生 `CampaignRun.load_campaign()` + `Campaign.run()` | C# 按静态规则执行关卡流程 |
| 地图移动与寻路 | 上游 `run()` 内部完成 | C# 在静态 `MapIR` 上做图算法与移动决策 |
| 战斗回合决策 | 上游内部 + 各关卡 `battle_*` 覆写 | C# 决策（覆写语义需先归纳，见 P0-2） |
| 任务调度 / 周期任务 / 大世界 | 经 `IVisionEngine` 调用上游分派 | C# 状态机与调度（已有 `TaskQueue`/`AlasSession` 基础） |
| 统计口径与报告计算 | 问上游要结果 | 静态数据 + C# 计算 |
| 配置语义（字段含义/校验/派生） | `ConfigWorkspace` 已自研，语义仍靠上游 | 语义以静态导出为准 |
| 结果判定 | 已用 `sortie-result/1` 合同 | 保持不变（唯一真值） |

### 2.2 不在范围内（明确不做）

- **图像识别**：模板/颜色/峰值/OCR/素材匹配 → 允许保留 Python 实现，只要求接口化；
- **上游应用形态**：`module/webui`（FastAPI + `alas-webapp` 包）、`discord_presence`、`fake_pil_module` → 本项目不需要；
- **部署形态**：`deploy/`、Docker、启动器脚本。

### 2.3 待评估（先量化成本与收益，再决定做不做）

| 项 | 规模 | 为什么要单独评估 |
| --- | --- | --- |
| `Config.when` 装饰器 DSL | 85 处 / 23 文件 | 属"声明式规则"的求值语义，可能必须用原语覆盖或改写规则层 |
| `importlib` 动态加载 | 15 处 / 4 文件 | 按配置动态选模块，C# 需等价的可配置装配 |
| 关卡覆写方法 | 3192 个 / 1396 文件 | 能否归纳为有限原语，直接决定"强形式"是否可达 |
| `uiautomator2` 设备 agent 协议 | 8 + 1 文件 | C# 无等价实现；要么自研协议、要么保留该能力走 Python |
| `adbutils` | 13 文件 | ADB 传输有 C# 替代，但截图/输入/性能路径需重建 |
| 非识别路径上的 cv2/numpy/scipy 使用 | cv2 30 文件 / numpy 66 文件 / scipy 10 文件 | **需按文件区分**是识别用途还是逻辑用途；识别用途划出范围 |

## 3. 流程（按域推进）

### P0 前期分析（必须先做，决定可行性）

1. **能力归属矩阵**：逐 `IVisionEngine` 方法 + Core 调用点，标注【识图保留 / 可静态化 / 必须自研 / 设备帧】；
2. **关卡覆写模式归纳**：3192 个方法按语义聚类 → 输出"N 个原语 + 覆盖率 + 长尾清单"；
3. **静态化可达性**：每域给出三档结论——纯静态可达 / 需补导出（列出字段）/ 必须自研；
4. 产出：**可行性 + 工作量 + 长尾**报告，作为立项与排期依据。

#### P0 已完成（2026-09-25）

- 工具：`tools/diagnostics/r5_upstream_audit.py`（纯 AST 解析，不导入游戏代码、不连设备）
- 报告：`docs/archive/reports/r5-upstream-migration.md`（由脚本重建，不手写；195 行）
- 关键结论：
  - **覆写面是"少数钩子 × 大量关卡"**：`campaign/` 2796 个类、3192 个方法，但**不同方法名只有 81 个**；
  - **40.4% 是薄覆写**：`return self.<helper>()` 598 + `return self.<子对象>.<方法>()` 679 + `return super()…` 8 + 空实现/赋值 3 = 1288 个，等价于"选哪个原语"；
  - **剩余 1904 个 `logic` 方法中 97.4% ≤5 条语句**（1–2 句 591、3–5 句 1263；>10 句仅 4 个），行数中位 7、P90 11、最多 65；
  - 被调用 helper 共 **100 个名字**，按前缀可归入少数原语族（`clear_` / `battle_` / `campaign_` / `ui_` / `fleet_` / `map_` / `handle_` …），薄覆写目标只有 5 个；
  - `IVisionEngine` **25 个方法**的初判归属：识图保留 8、必须自研 7（含重写核心 `RunCampaignPlan`）、设备帧 6、可静态化 4；Core 内接口调用点 28 处；
  - **结论**：把关卡覆写"原语化"是可行的（强形式在覆写这一块可达），弱形式目标（静态规则 + 有限原语 + 保留识图）有现实路径。
- P0 余项：把 100 个 helper 收敛成**原语清单**（每个原语的语义、参数、对拍方式）；按域给出静态化可达性三档结论。

#### P0 结论（第二批：原语清单与收敛度）

- **调用序列收敛度（决定 DSL 规模）**：1904 个 `logic` 方法归一化后**只有 138 种调用序列**；
  Top 20 覆盖 **88.0%**、Top 50 覆盖 **94.1%**；最大单一序列
  `battle_default+clear_filter_enemy+clear_siren` 覆盖 **857** 个方法（45.0%），
  其次 `battle_default+clear_siren` 259、`battle_default+clear_enemy+clear_siren` 118。
- **原语实现位置**（定义侧，`module/` 内可定位 22/100 个，其余为动态属性或子对象方法）：
  `module/map/map.py`（`Map.clear_siren`/`clear_filter_enemy`/`clear_enemy`/`clear_roadblocks`/`fleet_2_*`/`pick_up_ammo`…）、
  `module/map/fleet.py`（`check_accessibility`/`goto`/`fleet_at`）、
  `module/campaign/campaign_base.py`（`battle_default`）、
  `module/campaign/campaign_ui.py`（`campaign_ensure_mode`/`campaign_ensure_chapter`）、
  `module/ui/ui.py`（`ui_page_appear`）、`module/base/base.py`（`appear`）。
  定义体规模普遍很小：**2–13 条语句 / 14–51 行**。
- **分层证据（抽查 `battle_default` 与 `clear_enemy`）**：原语内部是**控制流 + 地图状态查询 + 配置读取**——
  `self.clear_enemy()`、`self.map.select(is_enemy=True, is_boss=False)`、
  `self.config.EnemyPriority_EnemyScaleBalanceWeight`、`self.select_grids(grids, **kwargs)`；
  只有再往下（`clear_chosen_enemy(grids[0])`）才落到识别与设备动作。

#### P0-3 静态化可达性（三档结论）

| 层 | 内容 | 结论 |
| --- | --- | --- |
| 关卡覆写层 | 81 个钩子 / 3192 个方法 / **138 种序列** | **纯静态可达**：导出为关卡规则数据（钩子 + 序列 + 参数） |
| 战役与地图原语层 | `battle_default` / `clear_*` / `fleet_2_*` / `select_grids` / `campaign_ensure_*` 等约 100 个名字，实现 2–13 条语句 | **C# 可实现**：控制流 + 地图状态查询 + 配置读取；内部的地图查询 DSL（`select` / `SelectedGrids`）需一并实现 |
| 动作与感知底层 | 模板/颜色/OCR/页面判定、ADB/MaaTouch 输入、抓帧 | **保留**（识图不重写；设备动作可在允许第三方库的前提下逐步替代） |
| 规则与参数 | `Config` 属性 14619 个、模块级声明 11118 个、`MAP` 网格 | **需补导出**：确认现有导出器覆盖"引擎实际读取的字段"（P1 的工作） |

**据此，P0 的可行性判断成立**：关卡层可完全数据化、原语层可 C# 实现、只有感知与设备动作保留 Python，
因此"静态规则 + 有限原语 + 保留识图"的弱形式目标是可落地的；强形式（连感知也不依赖 Python）不在本轮目标内。

### P1 静态导出补全

把引擎需要的规则与配置**全部**导出为数据，包括目前只在运行时才存在的字段；导出器沿用静态解析（不导入游戏代码），并纳入现有导出校验链（`tools/verify_export.py` / `alashub`/`Alas.Server verify`）。

#### P1 覆盖盘点结果（2026-09-25，报告 D 节）

现有导出**已经覆盖大部分需求**，缺口具体如下：

| 项 | 现状 | 缺口 |
| --- | --- | --- |
| 关卡覆写（`campaign.battles`） | 3019 条，`plan_complete` **2795（92.6%）** | **224 条未完整表达**：`If(nested)` 212、`Assign` 80、`Expr` 47、`Return(expr)` 28、`Raise` 4、`For` 1 |
| 关卡覆写读取的配置字段 | 15 个，已导出 9 个 | **6 个缺失**：`MAP_CLEAR_ALL_THIS_TIME`、`override`、`SERVER`、`Fleet_FleetOrder`、`Campaign_Event`、`Campaign_Name` |
| `MAP` 声明 | 已导出 `map_data` / `shape` / `spawn_data` / `camera_data` / `camera_data_spawn_point` / `weight_data` / `spawn_data_loop` | 与覆写实际读取面一致，暂无需补 |
| `config` 段键 | 共 80 个 | 覆盖识别与地图参数；与上表 6 个字段的差值即缺口 |
| 非 `battles` 钩子 | 3192 − 3019 = **173 个方法**不在 `battles` 段 | 需确认导出是否有对应段（`native_overrides` / `super_delegates` 抽样为空） |

**P1 待办**：
1. 224 条未完整表达的覆写——需决定扩展导出器（把条件/赋值也数据化）还是在 C# 侧用"原语 + 条件表达式"实现；
2. 6 个缺失配置字段——逐个确认来源：`SERVER`、`Campaign_Name` 属运行时/派生；`override` 需确认是否方法名误判；
3. 173 个非 `battles` 钩子——确认覆盖或补导出；
4. **导出器 `dead_code` 未记录死代码调用**：`event_20211028_tw/c3.json` 与 `d1.json` 的 `battle_0` 里重复的
   `return self.battle_default()` 被 `calls` 收了两次（`steps` 正确只保留一次），导致轨迹对拍出现 2 个例外；
5. 上述四项完成后重跑 `tools/diagnostics/r5_upstream_audit.py` 复核。

#### P1 关键发现：导出的 `steps` 就是可执行计划（DSL 面已具备）

导出器除了 `calls`（原语名列表）还输出 **`steps`：结构化步骤**，字段为
`{ kind, op, args{positional, keyword} }`。实测（报告 E 节）：

| 指标 | 数值 |
| --- | --- |
| 带 `steps` 的钩子 | **2795 / 3019（92.6%）** |
| 步骤总数 | **5694 步** |
| 步骤类型 | **4 种**：`conditional` 2814、`terminal` 2772、`call` 101、`super_delegate` 7 |
| 原语（`op`） | **32 个**（`battle_default` 1489、`clear_siren` 1284、`clear_filter_enemy` 978、`fleet_boss.clear_boss` 671、`clear_boss` 575、`clear_enemy` 402…） |
| 实参 | 绝大多数是 `"<expr>"` 占位（导出器未求值的表达式），仅 8 处字面量 |

**含义**：C# 引擎的执行面只有 **32 个原语 × 4 种步骤类型**；"关卡层数据化"不是待办，而是**已经完成 92.6%**。

### P2 逐域迁移（每个切片独立交付）

#### P2-0 已完成：C# 只读计划层（第一个切片，不切换生产路径）

| 项 | 内容 |
| --- | --- |
| 代码 | `src/Alas.Core/Campaign/CampaignPlan.cs`（关卡计划模型 + 读取器 + 统计 + **计划解释器**）、`src/Alas.Core/Diagnostics/CampaignPlanCheck.cs`（只读命令） |
| 命令 | `Alas.Server r5-plan`（全部章节概览 + 全库轨迹对拍）/ `r5-plan <章节>`（章节明细、DSL 统计、轨迹对拍）/ `r5-plan <章节> --level <关卡>`（单关卡逐步骤计划，`kind`/`op`/实参 + 与 `calls` 的一致性标记） |
| 解释器 | `CampaignPlanInterpreter`：把 `steps` 按类型分流为**无条件**（`terminal`/`call`，C# 可直接执行）、**条件**（`conditional`，待条件求值）、**委托**（`super_delegate`，由父类承担），并标记未求值实参（`"<expr>"`） |
| 对拍不变量 | 除 `super_delegate` 外，`steps.op` 序列应等于导出器的 `calls`：实测 **一致 2793 / 不一致 2 / steps 为空 224**（合计 3019） |
| 例外根因 | 那 2 个例外来自上游源码里的死代码（重复的 `return self.battle_default()`）：`calls` 收了两次、`steps` 正确地只保留一次；导出器的 `dead_code` 字段未记录该处 → 已列入 P1 待办 |
| 只读保证 | 不执行关卡、不导入游戏代码、不连设备；生产战役仍走上游 `CampaignRun.load_campaign()` + 原生 `Campaign.run()` |
| 跨语言对拍 | C# 与 Python 审计一致：**1437 个关卡导出 / 3019 个钩子 / 可表达 2795（92.6%）**，无读取失败 |
| 构建 | `dotnet build Alas.sln -c Release`：0 警告 0 错误 |

#### P2-1 已完成：执行侧骨架与计划契约（2026-09-25）

| 项 | 内容 |
| --- | --- |
| 代码 | `src/Alas.Core/Campaign/CampaignEngine.cs`：执行角色、形状校验、原语注册表、干跑执行器 |
| **执行契约（实测）** | 步骤类型序列为 `k* c* t? s*`（`call` 前置 → `conditional` 尝试 → `terminal` 兜底 → `super_delegate` 委托）：**3006/3019 符合**；13 个交错样本（`kckt`、`ckct`、`cckct` 等）仍按同一规则执行 |
| **执行语义** | 按顺序执行：`call` 无条件前置；`conditional` 按序尝试，**成功即短路返回真**；`terminal` 是兜底调用；`super_delegate` 交父类 |
| 关键结论 | `conditional` 步**不需要单独的条件表达式**——契约本身就是"调用成功即短路"，导出里的 `<expr>` 占位不影响执行语义 |
| 全库执行面 | 3019 个钩子 / 5694 步 / **31 个原语** / 已实现 **0**；角色分布：前置 101、尝试 2814、兜底 2772、委托 7 |
| 命令输出 | `r5-plan` 概览给出执行面与契约符合数；`r5-plan <章> --level <关>` 逐钩子给出角色划分、契约形状与每步"已实现/未实现"标记 |
| 含义 | P2 的待办面被量化：实现 **31 个原语**（2873 步为无条件/兜底调用，2814 步为条件尝试），原语实现后干跑状态会从"未实现"变为"可执行" |

#### P2-2 已完成：第一批原语（目标选择）与上游直接对拍（2026-09-25）

| 项 | 内容 |
| --- | --- |
| 代码 | `src/Alas.Core/Campaign/CampaignGrid.cs`（格子模型 + 格子集合 + 类型化过滤）、`CampaignTextFilter.cs`（敌人优先级过滤器）、`CampaignTargetSelector.cs`（选择链路） |
| 移植范围 | 逐条对应上游 `module/map/map.py`：`Map.select_grids`（nearby / is_accessible / ignore / scale / genre / strongest / weakest / sort 的处理顺序照抄）、`clear_enemy(**kwargs)`、`clear_filter_enemy(string, preserve)` 的**决策部分**；`ENEMY_FILTER` 对应上游 `module/base/filter.py` 的 `Filter` |
| 语义细节（照抄上游） | `is_accessible = cost < 9999`、`is_nearby = cost < 20`、敌人编码 `str = scale + genre 首字母`、`sort('weight','cost')` 升序、元组 scale/genre 是并集而列表是"取到即止"、`preserve` 截断、`S3/S1_enemy_first` 覆盖过滤串（S3 同时强制 `preserve=0`） |
| 未移植（显式报出） | `MAP_HAS_MOVABLE_NORMAL_ENEMY` 分支的 `clear_any_enemy(sort=('cost_2',))`（依赖 `cost_2` 排序键）：返回 `unsupported` 说明而不是静默给错结果；动作本身（点击/移动）属设备动作，不在本层 |
| 对拍证据 | `tools/diagnostics/verify_r5_selection.py`：15 个夹具用例全部通过，其中 **4 个直接调用上游 `module.base.filter.Filter`** 逐例比对选中结果；已登记进 `tools/diagnostics/verify_all.py` |
| 命令 | `Alas.Server r5-select --fixture tools/diagnostics/r5-selection-fixture.json`（输出每个用例的分支、选中格子与未移植说明，JSON） |
| 边界 | 只做"选哪个格子"的决策：不连设备、不执行游戏动作；原语注册表仍未登记实现（要等动手/移动侧接通后才算真正可执行） |

#### P2-3 已完成：动作侧闭环与首批原语登记（2026-09-25）

| 项 | 内容 |
| --- | --- |
| 代码 | `src/Alas.Core/Campaign/CampaignPrimitives.cs`（执行上下文 + 干跑宿主 + 原语实现 + 注册表 + 钩子执行循环）、`src/Alas.Core/Diagnostics/CampaignExecutionCheck.cs`（只读命令） |
| 执行上下文 | `ICampaignPrimitiveHost`：地图状态（真机来自识别、干跑来自夹具）+ 运行时配置 + **动作接口**（`ClearChosenEnemy` / `ClearChosenMystery`）；原语本身**不含坐标、点击顺序或设备细节** |
| 已登记原语（4 个） | `clear_enemy`、`battle_default`、`clear_all_mystery`、`clear_filter_enemy` —— `r5-plan` 概览已显示"已实现 4 个" |
| 执行循环 | 按契约驱动：`call` 前置 → `conditional` 尝试并短路 → `terminal` 兜底 → `super_delegate` 委托；无 terminal 且全部失败时按上游"落到方法末尾（None→假）"处理 |
| 诚实边界 | 实参含 `<expr>`、原语未实现、走到未移植分支时**停止执行并给出原因**（不跳过、不猜测），避免发出错误设备动作 |
| 对拍证据 | `tools/diagnostics/verify_r5_execution.py`：5 个用例通过，其中 4 个用上游选敌规则独立算出应打格子并与实际记录的动作比对；已登记进 `verify_all.py` |
| 命令 | `Alas.Server r5-exec --fixture tools/diagnostics/r5-execution-fixture.json`（输出每步结果、干跑动作、日志、阻塞原因，JSON） |

**P1 新缺口（本轮量化）——步骤实参未求值**：

| 实参形态 | 步骤数 | 占比 |
| --- | --- | --- |
| 无参 | 4172 | 73.3% |
| 字面量 | 419 | 7.4% |
| **含未求值表达式（`"<expr>"`）** | **1103** | **19.4%** |

最大一块是 `clear_filter_enemy`（970 个步骤的过滤串是表达式），其次 `clear_roadblocks`（48）、
`clear_potential_roadblocks`（35）。**这些步骤当前无法直接执行**——需要导出器对常见表达式求值
（多数是 `self.config.*` 常量或同文件常量），列入 P1 待办。

#### P1-1 已完成：类属性链字面量解析（`<expr>` 1103 → 139）（2026-09-25）

- **根因**：关卡的实参写成 `self.clear_filter_enemy(self.ENEMY_FILTER, preserve=1)`，而 `ENEMY_FILTER`
  定义在**基类**（如 `.campaign_14_base` 的 `CampaignBase.ENEMY_FILTER = '1T > 1L > …'`）；
  导出器原先只解析 `Campaign` 类自身声明，于是实参被记成 `'<expr>'`。
- **改动**：`tools/export_upstream_data.py` 新增 `campaign_literal_attributes()` 与
  `attribute_literal_resolver()`——沿**相对导入的基类链**收集类属性/模块级声明里的**字面量**
  （纯 AST，不导入游戏代码；表达式一律忽略、宁缺勿猜），`call_args()` 增加 `resolve` 回调，
  导出器版本 2.2.0 → 2.3.0。
- **结果**（同一上游检出、重跑导出后对比）：

| 指标 | 改动前 | 改动后 |
| --- | --- | --- |
| 含未求值表达式步骤 | 1103（19.4%） | **139（2.4%）** |
| `clear_filter_enemy` 表达式实参 | 970 | **6** |
| 导出差异 | — | 775 个文件 / 964 处步骤实参，**只有 `<expr>` → 解析值**，无其它结构变化 |

- **效果**：88 个此前"第一跳就卡在 `<expr>`"的关卡现在可以执行（已加进 `verify_r5_execution.py` 夹具：
  `campaign_15_1 battle_1` 的 `preserve=1` 跳过一个、`campaign_14_2 battle_5` 的 `1T` 优先命中）。
- **既有问题（未引入、未修复）**：`Alas.Server verify` 报 3 个 Campaign 声明不完整
  （`event_20200227_cn/c2.py`、`d3.py`、`event_20200312_cn/sp3.py`）——用改动前的导出器重导到临时目录对比，
  **失败项与计数完全一致**，确认与本次改动无关。
- **剩余 139 个表达式实参**：主要是 `clear_roadblocks`（48）与 `clear_potential_roadblocks`（35）的
  `roads` 参数——它们是 `RoadGrids([...])` 这类**地图对象**（引用具体格子），不是标量字面量，
  需要单独设计"地图对象实参"的导出表达，列入 P1 待办。

#### P2-5 已完成：舰队前缀规则（一次解锁 685 步）（2026-09-25）

- **上游语义**（读源码确认）：`Fleet.fleet_1` / `fleet_2` / `fleet_submarine` / `fleet_boss` 是
  **返回 `self` 的 property**，只在当前舰队不同时才 `fleet_ensure(index)`（`fleet_boss` 的索引按上游
  `fleet_boss_index`：`FLEET_BOSS == 2 and FLEET_2` 时取 2，否则 1）。因此
  `self.fleet_boss.clear_boss()` ≡ "必要时切到 boss 舰队 + `clear_boss()`"，`fleet_1.clear_boss` 同理。
- **实现**：注册表按**前缀规则**解析（`fleet_1` / `fleet_2` / `fleet_submarine` / `fleet_boss` + 已登记原语），
  宿主新增 `FleetCurrentIndex` 与 `EnsureFleet(index)`（与上游一致：索引相同则不记录切换）——
  没有按关卡、按编号写任何特例。
- **规模**：`fleet_boss.clear_boss` 671 步、`fleet_1.clear_boss` 13 步、`fleet_boss.clear_potential_boss` 1 步
  直接转为可执行；`fleet_boss.brute_clear_boss`(9) 与 `fleet_boss.capture_clear_boss`(12) 仍待实现。
- **对拍**：`verify_r5_execution.py` 扩到 **16 个用例**（新增：`fleet_boss.clear_boss` 正常执行、
  `FLEET_BOSS+FLEET_2` 时先记录 `fleet_ensure(2)`、已在目标舰队时不重复切换），全部通过。
- **新增进度指标**：`r5-plan` 现在输出**按步骤计的覆盖率**——全库
  **步覆盖 5456/5694（95.8%）**（步骤指向已实现原语；仍可能被未求值实参或未移植分支挡住，
  详见下一节的诚实边界）。

**当前状态一览**（全库 3019 个钩子 / 5694 步）：

| 指标 | 数值 |
| --- | --- |
| 步骤指向已实现原语 | 5625 / 5694（**98.8%**） |
| 涉及原语 | 31 个（其中 19 个已实现，含舰队前缀组合） |
| 计划形状符合契约 | 3006 / 3019 |
| 实参完整（无 `<expr>`） | 5661 / 5694（**99.4%**） |

#### P1-2 已完成：路段（`RoadGrids`）实参导出（`<expr>` 139 → 61）（2026-09-25）

- **根因**：`clear_roadblocks([road_main])` 的实参是模块级**路段对象**
  （`road_main = RoadGrids([[H3, B6, C5]])`），标量字面量表达不了；格子符号本身由
  `A1, B1, … = MAP.flatten()` 元组解包绑定。
- **改动**：导出器新增 `campaign_map_shape()` / `campaign_road_table()` / `road_argument_resolver()`
  （版本 2.3.0 → 2.4.0）：把路段解析成坐标数组
  `{"__roads__": [路段, …]}`（路段 = block 列表，block = `[x, y]` 数组）。
  **形状自校验**：符号数必须等于 `列数 × 行数`，否则整表作废、实参照旧记 `<expr>`，不猜。
- **结果**：`<expr>` 139 → **61**；**78 个步骤**拿到结构化路段实参；
  `verify_export.py` 的 `plan_issues = 0`（结构化实参没有破坏计划校验），
  仅剩**既有的 3 模块 Campaign 声明问题**（与本次无关，见 P1-1）。
- **剩余 61 个 `<expr>`**：`fleet_2_step_on` 11、`pick_up_light_house` 10、
  `super().handle_boss_appear_refocus` 7（委托父类，本就不执行）、`clear_filter_enemy` 6、
  `clear_map_items` 5、`pick_up_flare` 4、`fleet_boss.pick_up_flare` 4、`fleet_2_rescue` 4 等
  （多为"具体格子/对象"实参，需按类型逐个补导出表达）。

#### P2-6 已完成：路段原语三件套（步覆盖 95.8% → 97.3%）（2026-09-25）

| 新增原语 | 对应上游 | 关键语义 |
| --- | --- | --- |
| `clear_roadblocks` | `Map.clear_roadblocks(roads, **kwargs)` | 整块都是敌人时整块算路障；按 `EnemyPriority_*` / `MAP_CLEAR_ALL_THIS_TIME` 决定 strongest/weakest |
| `clear_potential_roadblocks` | `Map.clear_potential_roadblocks` | 跳过含舰队或已清格子的块；只剩一格非敌人时取该块敌人 |
| `clear_first_roadblocks` | `Map.clear_first_roadblocks` | 跳过含舰队/已清格子的块；块里有敌人就取敌人；**不做优先级覆盖**（照抄上游） |

配套：`CampaignRoad`（`RoadGrids` 模型，三个判定方法逐条对应上游）、
`CampaignLocations.ToNode()`（上游 `location2node` 约定：列字母 + 行号）、
格子模型增加 `is_fleet` / `is_cleared` 与对应过滤条件、
注册表增加 `DecodeRoads()`（解析 `__roads__`，非该结构时明确报错）。

**对拍**：`verify_r5_execution.py` 扩到 **19 个用例**（新增：单格 block 全敌人 → `clear_roadblocks` 命中；
多格 block 只剩一格非敌人 → `clear_potential_roadblocks` 命中；block 含舰队 → potential 跳过并交给
`battle_default`），全部通过；注册原语 **10 个**（含舰队前缀组合共 12 个已实现）。

#### P1-3 已完成：格子符号实参导出（`<expr>` 61 → 33）（2026-09-25）

- **根因**：`pick_up_flare(H9)`、`fleet_2_rescue(G2)`、`clear_map_items([F1, I1])` 这类实参传的是
  **具体格子符号**（由 `A1, B1, … = MAP.flatten()` 绑定），标量字面量表达不了。
- **改动**：导出器抽出统一的 `campaign_symbol_locations()`（符号 → `[x, y]`，行优先，带形状自校验），
  `campaign_road_table()` 复用它；新增 `symbol_argument_resolver()` 产出
  `{"__grid__": [x, y]}` / `{"__grids__": [[x, y], …]}`。导出器版本 2.4.0 → 2.5.0。
- **结果**：`<expr>` 61 → **33**；结构化实参 **106** 个（78 路段 + 28 格子/格子表）；
  `verify_export` 的 `plan_issues = 0`。
- **剩余 33 个 `<expr>`**：`fleet_2_step_on` 11（实参是**方法内局部变量** `step_on`）、
  `super().handle_boss_appear_refocus` 7（委托父类，本就不执行）、`clear_filter_enemy` 6、
  `clear_roadblocks` / `clear_potential_roadblocks` / `clear_first_roadblocks` 共 7（局部路段变量）、
  `clear_mechanism` 2 —— 都需要"方法内数据流"才能解析，属下一阶段。

#### P2-7 已完成：拾取类原语（步覆盖 97.3% → 97.7%）（2026-09-25）

| 新增原语 | 定义位置 | 关键语义 |
| --- | --- | --- |
| `pick_up_ammo` | `module/map/map.py:49` | 未指定格子时自动找 `may_ammo`；有弹药且可达 → `goto` + `ensure_no_info_bar` + 回收弹药（`recover = min(3, 5 - fleet_ammo)`）；上游结尾**没有** return True |
| `pick_up_light_house` | **关卡基类**（如 `campaign_14_base.py`） | 已拾取则跳过，否则 `goto` + 记账 + 关信息条；**恒返回假** |
| `pick_up_flare` | **关卡基类**（同上） | 同上，并会把格子标记为 flare；**恒返回假** |

配套：宿主增加 `Goto` / `EnsureNoInfoBar` / `AmmoCount` / `FleetAmmo` / `PickedLightHouse` / `PickedFlare`；
格子模型增加 `may_ammo`；导出器新实参形态 `__grid__` 的解码（`DecodeGrid`）。

**修掉一个保真 bug**：格子实参最初被我构造成"裸格子"（默认 `cost = 0`），于是 fixture 里 `cost = 9999`
的不可达格子被误判成可拾取。现在 `DecodeGrid` 必须**在宿主的地图状态里查回真实格子**，查不到就如实报错，
不用默认值糊过去（对拍用例当场抓到了这个错误）。

**对拍**：`verify_r5_execution.py` 扩到 **23 个用例**（新增：`pick_up_ammo` 自动找 `may_ammo` 并回收弹药、
没有 `may_ammo` 时记 `Map has no ammo.`、三连拾取后路段原语短路、灯塔不可达 + filter 串仍是 `<expr>`
时诚实阻塞），全部通过；注册原语 **13 个**（含舰队前缀组合共 16 个已实现）。

> 注：`pick_up_light_house` / `pick_up_flare` 定义在**关卡树**（`campaign/**`）而不是 `module/**`——
> 这是"关卡侧 helper"这一类需要迁移的代码，属 P2 逐域迁移范围；它们的语义务必与上游逐字对齐，
> 不能因为"看起来只是 goto"就简化。

#### P2-8 已完成：舰队机动三件套（步覆盖 97.7% → 98.8%）（2026-09-25）

| 新增原语 | 对应上游 | 关键语义 |
| --- | --- | --- |
| `capture_clear_boss` | `Map.capture_clear_boss()` | 打 boss（或"被塞壬抓住的 may_boss"），最后**无条件撤退**（`withdraw()`）；上游无 return（落到末尾为假） |
| `fleet_2_push_forward` | `Map.fleet_2_push_forward()` | `fleet_boss_index != 2` 直接返回假；否则把道中队推向 weight 最低的**可达海域**（`is_accessible_2 + is_sea`，排除两支舰队所在格），推进后切回 1 队 |
| `fleet_2_protect` | `Map.fleet_2_protect()` | 无 `FLEET_2`/`MAP_HAS_MOVABLE_ENEMY` 直接返回假；最多 20 轮：附近（`cost_2 ∈ {1,2}`）有塞壬/敌人就打（`expected='siren'`），否则游走到最近的可去格 |

配套模型（都照抄上游属性语义）：格子增加 `is_land` / `is_sea`（`is_sea` 按上游公式推导）、
`cost_1` / `is_accessible_1`，过滤条件支持 `cost_2`，排序键支持 `cost_1`；
配置增加 `fleet_boss_index`（`FLEET_BOSS==2 and FLEET_2` → 2）与 `MAP_HAS_MOVABLE_ENEMY`；
宿主增加 `Fleet1Location` / `Fleet2Location` / `GridAt()` / `Withdraw()`。

**对拍**：`verify_r5_execution.py` 扩到 **28 个用例**（新增：`capture_clear_boss` 打到 boss 后撤退、
`fleet_2_push_forward` 推进到 weight 更低的海域、`fleet_boss_index != 2` 时直接返回假（断言**没有**推进日志）、
`fleet_2_protect` 清靠近的塞壬并短路、没有靠近敌人时游走一轮后交给 `clear_siren`），全部通过；
注册原语 **16 个**（含舰队前缀组合 19 个已实现）。

> 两条诚实说明：① `capture_clear_boss` 结尾是**撤退**，它不是"清完继续打"的步骤；
> ② `fleet_2_protect` 上游会循环 20 轮（每轮 goto 后重新识别地图），干跑宿主不刷新状态，
> 因此一轮后停止并记日志——真机路径需要用模拟器验证这一循环。

**剩余未实现（69 步 / 12 个 op）**：`clear_bouncing_enemy` 12、`fleet_2_step_on` 11（实参是局部变量）、
`brute_clear_boss` 11（依赖 `brute_find_roadblocks` 寻路）、`super().handle_boss_appear_refocus` 7（委托父类，本就不执行）、
`clear_map_items` 5、`fleet_2_rescue` 4（寻路）、`clear_mechanism` 4、`battle_0` 2、
`fleet_1.switch_to` / `fleet_2.switch_to` 各 1、`clear_chosen_enemy` 1、`fleet_boss.clear_potential_boss` 1。

#### P2-9 已完成：收尾易做 op + 跨钩子调用（步覆盖 98.8% → 99.1%）（2026-09-25）

| 新增原语 | 对应上游 | 关键语义 |
| --- | --- | --- |
| `switch_to` | `Fleet.switch_to()` | **上游实现就是 `pass`**（空方法）——切舰队发生在 `fleet_1`/`fleet_2` 取值时，因此这里也只是"记日志 + 返回假"，切舰队仍由前缀规则完成 |
| `clear_potential_boss` | `Map.clear_potential_boss()` | 单独注册（此前只被 `clear_boss` 内部调用），覆盖 `fleet_boss.clear_potential_boss` 这类直接调用 |
| `clear_chosen_enemy` | `Map.clear_chosen_enemy(grid, expected='')` | 打**指定格子**的动作入口（从 `__grid__` 实参解码，设备动作交给宿主） |
| `clear_map_items` | **关卡基类**（`event_20221124_cn/campaign_base.py`） | 按 `cost` 升序逐个 `goto`；上游无 return（为假） |
| `clear_mechanism` | `Map.clear_mechanism(grids=None)` | 无 `MAP_HAS_LAND_BASED` 返回假；选可触发且未被阻挡的机关格 → `goto` → **上游在此 `raise MapEnemyMoved`** |

配套：格子增加 `is_mechanism_trigger` / `is_mechanism_block` 与过滤条件；配置增加 `MAP_HAS_LAND_BASED`；
**新增控制流信号类型** `CampaignControlFlowSignal`——上游用异常做控制流（`MapEnemyMoved` 由战役循环捕获后重新识别），
执行器**如实转成阻塞原因**，不假装继续往下跑。

**跨钩子调用**：上游存在 `self.battle_0()` 这种"调用同关卡其它钩子"的写法，执行器现在会递归执行该钩子
（限深 3 层；被阻塞时向上传播原因），并且这类步骤也计入覆盖率。

**对拍**：`verify_r5_execution.py` 扩到 **32 个用例**（新增：`fleet_2.switch_to` 仍是空操作但按前缀切舰队、
`clear_map_items` 按 cost 升序 goto 后接 boss 清理、`clear_mechanism` 抛 `MapEnemyMoved` 如实阻塞、
`terminal battle_0` 跨钩子递归执行），全部通过；注册原语 **21 个**（含舰队前缀组合 25 个已实现）。

**剩余 54 步（已收敛到三类）**：

| 类别 | 步骤 | 说明 |
| --- | --- | --- |
| 依赖寻路 | **35** | `brute_clear_boss` 11 + `fleet_boss.brute_clear_boss` 9 + `fleet_2_rescue` 4 + `fleet_2_step_on` 11——都需要 `brute_find_roadblocks` / `find_path_initial`（独立的大块，属寻路域迁移） |
| 导出缺口 | **12** | `clear_bouncing_enemy`：需要导出 `MAP.bouncing_enemy_data`（当前 `map` 段没有这个声明） |
| 设计上不执行 | **7** | `super().handle_boss_appear_refocus`（委托父类，本层不执行） |

#### P2-10 已完成：关卡循环（run / execute_a_battle / battle_function）（2026-09-25）

`src/Alas.Core/Campaign/CampaignBattleLoop.cs`，逐条对应上游 `module/campaign/campaign_base.py`：

| 上游 | C# 移植 | 关键语义 |
| --- | --- | --- |
| `run()` | `CampaignBattleLoop.Run` | 最多 **20 轮**出击；收到 `CampaignEnd` 即成功结束；否则记 `Battle function exhausted.`，再按 `Error_HandleError` 决定撤退或 `ScriptError` |
| `execute_a_battle()` | `ExecuteABattle` | 最多 **10 次尝试**；`MapEnemyMoved` → 期间 `battle_count` 增长即算成功，否则重试；没打成时记 `No combat executed.` |
| `battle_function()`（默认变体） | `SelectHook` | 从 `battle_{battle_count}` 往回找最多 **10** 个已定义钩子，找不到就用 `battle_default` |
| 变体 `clear_all` / `battle_with_poor_map_data` | `BattleFunctionVariant` | **明确阻塞**并给出原因（需要 `fleet_2_break_siren_caught` / `brute_clear_boss` / `clear_bouncing_enemy`），绝不按默认策略静默跑错 |

**本轮发现的结构问题（已修）**：`battle_default` / `battle_boss` / `battle_function` 定义在**基类**
`CampaignBase` 里——因此 `battle_count` 超过 10 的回看窗口后，上游 `hasattr(self, 'battle_default')` 会命中
基类实现。第一版只看关卡自身导出，误报"没有钩子 battle_default"；现在按 MRO 语义落到已移植的
`CampaignPrimitives.BattleDefault`。

**关卡循环的对拍**：新增只读命令 `Alas.Server r5-loop --fixture tools/diagnostics/r5-loop-fixture.json`
（输出每轮选的钩子、结果、动作与日志）+ 检查脚本 `tools/diagnostics/verify_r5_loop.py`
（5 个用例、**44 轮**用上游钩子选择规则独立算出应选钩子后逐轮比对），已登记进 `verify_all.py`。
用例覆盖：打不成 → 默认 `Error_HandleError` 下撤退结束；`Error_HandleError=false` → `ScriptError` 不撤退；
每轮都打成 → 20 轮耗尽；耗尽后默认配置会撤退结束；`MAP_CLEAR_ALL_THIS_TIME=true` → 变体未迁移即阻塞。

**干跑口径的一处调整**：录制宿主在"真的打了一场"（`ClearChosenEnemy`）时让 `battle_count` 增长，
这样循环才能推进、"第 N 轮选哪个钩子"才能被夹具验证；这与上游"打完一场战斗后 battle_count 增长"一致，
但**真机以战斗结果为准**。

> 待真机确认：标准流程里 `CampaignEnd` 由 `MapOperation.withdraw()` 在检测到已回到章节页时抛出
> （`module/map/map_operation.py:410`）。干跑把"撤退 ⇒ 本关结束"当作近似；**成功通关时的结束时机**
> 仍需模拟器验证（这是 P2 硬要求里剩下的关键一项）。

#### P2-11 已完成：寻路成本场（对拍逐格一致）（2026-09-26）

`src/Alas.Core/Campaign/CampaignPathfinder.cs`，逐条对应上游 `module/map/map_base.py`：

| 上游 | C# 移植 | 关键语义 |
| --- | --- | --- |
| `grid_connection_initial()` | `Neighbours()` | 四邻接（上下左右），只连形状范围内存在的格子；越界坐标不抛异常（上游用 `if arr in total` 天然过滤） |
| `find_path_initial(location, has_ambush, has_enemy)` | `FindPathInitial()` | 起点 cost=0；进入代价 `1`（或 `may_ambush` 时 `ambush_cost=10/1`）；陆地与机关阻挡不可进入；**非海域格不继续扩散**（除非 `has_enemy=False`）；等代价且横向相邻（x 差 1）时改写 connection——上游的确定性 tie-break 照抄 |
| `_find_path(location)` | `FindPath()` | 沿 connection 回溯路线；上游的 `Route too long > 30` 只记 warning |

**`may_ambush` 的来源照抄上游 `GridInfo.decode()`**：令牌不是 `ME/MB/MM/MA` 之一即为真
（`--` 空地 → may_ambush=True；`ME` 可能有敌 → False）。

**对拍方式（真跑上游实现）**：新增只读命令 `Alas.Server r5-path --fixture tools/diagnostics/r5-path-fixture.json`
+ 检查脚本 `tools/diagnostics/verify_r5_path.py`——后者**构造上游 `CampaignMap` 并在其上跑
`find_path_initial` / `_find_path`**，与 C# 的成本场、连接、路线逐格比对：

```
[r5-path] 用例 5 个，逐格比较 132 格；连接差异（等代价）0 处 → PASS
```

用例覆盖：陆地阻挡、`ME` 可进不扩散、`has_ambush=true`（代价 10）、`has_enemy=false`（敌人格也扩散）、
识别结果标成敌人（非海域不扩散）、机关阻挡绕行。已登记进 `verify_all.py`。

**这一步的意义**：剩余 54 步里最大的一块（依赖寻路的 35 步：`brute_clear_boss` 20、`fleet_2_rescue` 4、
`fleet_2_step_on` 11）此前完全没有地基；现在成本场与路线已经与上游逐格一致，`brute_find_roadblocks`
等上层算法可以在其上继续移植。

#### P2-12 已完成：暴力找路障与 boss 救援（步覆盖 99.1% → 99.5%）（2026-09-26）

| 新增 | 对应上游 | 关键语义 |
| --- | --- | --- |
| `CampaignBruteFinder.FindRoadblocks` | `Fleet.brute_find_roadblocks(grid, fleet)` | 先按目标舰队算成本场（可达即返回空）；否则枚举敌人**可重复子集**（`itertools.product(enemies, repeat=r)`，r 从 1 到敌人数），每次把该子集临时当非敌人重算成本场，第一个让目标可达的子集就是路障；枚举完仍不可达即 `Enemy roadblock try exhausted.` |
| `brute_clear_boss` | `Map.brute_clear_boss()` | 找 boss → 暴力找路障 → 有路障先 `brute_fleet_meet` 再打；没找到路障退回 `fleet_boss.clear_boss()`；无 boss 但"被塞壬抓住的 may_boss"→ 切 2 队清掉；都没有 → `clear_potential_boss()` |
| `brute_fleet_meet` | `Map.brute_fleet_meet()` | `fleet_boss_index != 2` 或没 2 队位置 → 假；否则为 1 队清出通往 2 队的路障 |
| `fleet_2_rescue` | `Map.fleet_2_rescue(grid)` | `fleet_boss_index != 2` → 假；暴力找挡在目标格前的敌人 → 按**恢复后**的成本场过滤 `is_accessible` → 按 weight/cost 打第一个 |

与上游的两点差异（如实记录）：① 上游临时改写 `grid.is_enemy` 再改回来，这里用不可变副本；
② 上游枚举没有上限，这里保留 `maxTries`（默认 20 万）以防指数爆炸，触顶时**明确报出**
`Exhausted`，不静默当成"没有路障"。

**对拍**：`verify_r5_execution.py` 扩到 **35 个用例**（新增：boss 被敌人挡住时暴力枚举出 `C1` 并打掉；
boss 本来就可达时退回 `fleet_boss.clear_boss`；`fleet_2_rescue` 清掉挡在 `G2` 前的 `D2`），全部通过；
注册原语 **24 个**（含舰队前缀组合 28 个已实现）。

**剩余 30 步（收敛到三类）**：

| 类别 | 步骤 | 说明 |
| --- | --- | --- |
| 导出缺口 | **12** | `clear_bouncing_enemy`：需要导出 `MAP.bouncing_enemy_data` |
| 实参是方法内局部变量 | **11** | `fleet_2_step_on(step_on, roadblocks=[...])`：实参是模块级 `SelectedGrids([E4, D3, …])` 与局部路段变量，需要导出器解析 `SelectedGrids([符号])` 与关键字路段参数 |
| 设计上不执行 | **7** | `super().handle_boss_appear_refocus`（委托父类，本层不执行） |

#### P1-4 已完成：模块级格子表实参导出（`<expr>` 32 → 22）（2026-09-26）

- **根因**：`self.fleet_2_step_on(step_on, roadblocks=[roadblocks_d4])` 的位置实参是**模块级变量**
  `step_on = SelectedGrids([E4, D3, G4, C3])`——既不是字面量也不是格子符号本身。
- **改动**：导出器新增 `campaign_grid_list_variables()`（模块级 `name = SelectedGrids([符号…])` /
  `= [符号…]` → `{name: [[x, y], …]}`），`symbol_argument_resolver()` 先查变量表再查符号表，
  并支持 `SelectedGrids([符号…])` 这种包装调用；导出器版本 2.6.0 → 2.7.0。
- **结果**：`<expr>` 32 → **22**；结构化实参 **117** 个；`verify_export` 的 `plan_issues = 0`、`map_issues = 0`。
- **剩余 22 个 `<expr>`**：`super().handle_boss_appear_refocus` 7（委托父类，本就不执行）、
  `clear_filter_enemy` 6、`clear_roadblocks` / `clear_potential_roadblocks` / `clear_first_roadblocks` 共 7
  （方法内局部路段变量）、`fleet_2_step_on` 2。
- **更正一处此前的错误判断**：`MAP.bouncing_enemy_data`（`clear_bouncing_enemy` 依赖的巡逻路线）
  **其实早已导出**（12 个关卡），我在 P2-12 里把它记成"导出缺口"是错的——实际缺口只在"格子表变量"这一处。

#### P2-13 已完成：步覆盖 **99.9%**（巡逻敌人 + 道中队踩点）（2026-09-26）

| 新增原语 | 对应上游 | 关键语义 |
| --- | --- | --- |
| `clear_bouncing_enemy` | `Map.clear_bouncing_enemy()` | 无 `MAP_HAS_BOUNCING_ENEMY` → 假；选第一条"有可达巡逻敌人"的路线（`MAP.bouncing_enemy_data`），沿路线循环走过去，直到 `battle_count` 增长或超过 12 次尝试（上游循环上限照抄）；成功时上游会把该路线的 `may_bouncing_enemy` 置假并重新识别——干跑不改地图状态，只记日志 |
| `fleet_2_step_on` | `Map.fleet_2_step_on(grids, roadblocks)` | 无 `FLEET_2` → 假；2 队已在其中任一格 → 假；跳过敌人格（`all_cleared` 时也跳过已清格）→ 逐格 `check_accessibility(grid, fleet=2)`，可达就走过去并返回**假**（上游如此）；都走不过去 → 切回 1 队 `clear_roadblocks(roadblocks)` + `clear_all_mystery()`，返回前者结果 |
| `Fleet.check_accessibility`（内部） | 同上游 | 当前舰队直接看现有成本；否则按**该舰队**位置重算成本场再判断可达性 |

配套：格子模型增加 `may_bouncing_enemy`；配置增加 `MAP_HAS_BOUNCING_ENEMY`、`MAP_HAS_AMBUSH`；
`CampaignPlanMap` 增加 `bouncing_enemy_data`；宿主增加 `BouncingRoutes`；
`DecodeRoads` 现在同时扫描**位置与关键字**实参（`roadblocks=[…]` 是关键字，之前只扫位置参数）。

**对拍**：`verify_r5_execution.py` 扩到 **39 个用例**（新增：巡逻路线走 13 次仍未打成 → 记 12 次失败；
没有可达巡逻敌人 → 直接返回假；`fleet_2_step_on` 踩到可达格 → 切 2 队并返回假；都走不过去 → 转清路障），
全部通过；注册原语 **26 个**（含舰队前缀组合 30 个已实现）。

**当前状态（全库）**：

| 指标 | 数值 |
| --- | --- |
| 步骤指向已实现原语 | **5687 / 5694（99.9%）** |
| 剩余未实现 | **7 步**——全部是 `super().handle_boss_appear_refocus`（委托父类，本层设计上不执行） |
| 涉及原语 | 31 个（其中 30 个已实现） |
| 实参完整（无 `<expr>`） | 5672 / 5694（99.6%） |

> 夹具设计的三点教训（都被检查脚本当场抓到）：① 格子集合必须是**连续矩形**，否则邻接为空、什么都不可达；
> ② 引用的符号（如 `G4`）必须存在于地图状态，否则如实报错；③ 可达性由**几何 + 成本场**决定，
> 不能靠夹具里手写的 `cost` 值伪造。

#### P1-5 已完成：`<expr>` 22 → 7，全库干跑 3019/3019 无阻塞（2026-09-26）

三处导出器根因（导出器 2.7.0 → 2.8.0）：

| 根因 | 例子 | 修法 |
| --- | --- | --- |
| **绝对导入的基类**没被追 | `from campaign.campaign_main.campaign_14_base import CampaignBase` | `_relative_import_origin` 同时支持相对导入与 `campaign.` 绝对导入 |
| **别名导入回错类名** | `from .campaign_15_4 import Campaign as Campaign_15_4` → 在基类模块里按 `Campaign_15_4` 找不到类 | 返回 `(模块, 原始类名)`，按 `Campaign` 找 |
| **路段表达式不全** | `road_a1 = RoadGrids([...]).combine(RoadGrids([...]))`（`combine` = 块的两两并集）、`roads = [road_a, …]`（模块级路段列表）、`roadblocks=[]`（空表也有语义） | 新增 `_parse_road_expr`（支持 `combine` 链）、`campaign_road_list_variables`、空列表解析；C# 侧 `DecodeRoads` 接受空数组 |

**结果**：`<expr>` **22 → 7**（只剩 `super().handle_boss_appear_refocus` 这类委托父类的占位）；
`verify_export` 的 `plan_issues = 0`、`map_issues = 0`。

**新增更强的口径指标：全库干跑**（`Alas.Server r5-plan --dry-run-all`）——用**导出的真实地图**
（`map.map_data` 令牌，按上游 `GridInfo.decode()` 推导 may_* 与 may_ambush）构造地图状态，
逐个钩子跑一遍 `CampaignHookRunner`：

```
[干跑全库] 钩子 3019 个：跑完 3019（返回真 1281）/ 被阻塞 0
[地图状态] 134 章中 67 个关卡导出没有可用 map_data
```

口径说明（写在命令注释里）：识别结果（哪个格子真有敌人/boss）不在导出里，因此这是**控制流与实参解码的
干跑**——"跑完"不等于"真机能打通"，但"被阻塞"确实说明引擎缺东西（现在为 0）。

> 一处夹具期望随之更新：`campaign_14_4 battle_3` 原来断言"filter 串是 `<expr>` → 诚实阻塞"，
> 现在 filter 串已能解析，该钩子走完全程——**这正是本轮想要的结果**，期望改为断言完整执行。

#### P2-4 已完成：原语扩到 7 个（含 boss/siren/any_enemy）（2026-09-25）

| 新增原语 | 对应上游 | 关键语义 |
| --- | --- | --- |
| `clear_any_enemy` | `Map.clear_any_enemy(**kwargs)` | 敌人 + （`MAP_HAS_SIREN`）塞壬 + （`MAP_HAS_FORTRESS`）要塞；`expected` 取 fortress/siren/空；支持 `sort` 关键字（如 `cost_2`） |
| `clear_siren` | `Map.clear_siren(**kwargs)` | 无塞壬且无要塞配置时**直接返回假**；`FLEET_2` 时 `sort=('weight','cost_2')`；`expected` 取 fortress/siren |
| `clear_boss` | `Map.clear_boss()` | `is_boss+is_accessible` ＋「被塞壬抓住的 may_boss」；都没有时退回 `clear_potential_boss`；上游注释已标 deprecated 但关卡里仍有 575 处调用，按原样移植 |
| `clear_potential_boss` | `Map.clear_potential_boss()` | 依次踩可达 may_boss（`fleet_boss.clear_chosen_enemy`），用 `battle_count` 判断猜中；**不可达 may_boss 分支需要 `brute_find_roadblocks`（寻路）——未移植，遇到即报错** |

配套扩展：格子模型增加 `may_boss` / `is_caught_by_siren` / `cost_2`（含 `is_accessible_2`），
`Sort` 支持 `weight`/`cost`/`cost_2`；宿主接口增加 `BattleCount`、`SubmarineMoveNearBoss`，
`ClearChosenEnemy` 增加 `fleet` 维度（当前舰队 / `fleet_boss`）。

**对拍**：`verify_r5_execution.py` 扩到 **11 个用例**（含"有塞壬短路"、"无塞壬交给 clear_enemy"、
"有 boss 走潜艇机动 + 打 boss"、"只有 may_boss 时踩格子（fleet_boss）"、"不可达 may_boss 明确报未移植"、
"`cost_2` 排序"），全部通过；`r5-plan` 概览显示 **已实现 7 个**原语。

**本轮对拍还纠正了我自己的一个错误期望**：`clear_any_enemy(sort=['cost_2'])` 下应选 `cost_2` 最小的格子
（敌人 A1 的 1 < 要塞 E5 的 40），我最初误以为要塞优先——C# 输出正确，已按上游语义修正夹具并补一例只想要塞的用例。

每个切片必须齐四样，缺一不算完成：

1. **对拍夹具**：上游行为录制、脱敏、可复跑；
2. **性能基线**：替换前后对照，并给出波动区间说明；
3. **真实路径证据**：成功结算并返回章节页（撤退、超时、单帧截图都不算）；
4. **回退开关**：域级可切回上游实现。

建议顺序（由易到难、由只读到有动作）：
只读状态与目录 → 统计口径 → 页面图与导航 → 地图移动 → 战斗回合决策 → 完整战役流程。

### P3 切换与观察

域级开关双跑，观察漂移；每域稳定后，再把上游那条路径标记为废弃。

#### 切换与回退设计（P2 要求项，**尚未启用**）

现状：C# 侧引擎（关卡计划 / 原语 / 关卡循环 / 寻路）已可在离线干跑，但**生产路径仍全部走上游**
`CampaignRun.load_campaign()` + 原生 `Campaign.run()`——开关当前等效于"全程走上游"。

| 项 | 设计 |
| --- | --- |
| 开关粒度 | **按域**（关卡循环 / 寻路 / 原语执行），每域一个开关，默认全部走上游；禁止"整体一把梭" |
| 开关位置 | 由运行时会话（`Alas.Core/Runtime/AlasSession`）决定后端；`Alas.Server` 只解析参数、不解释业务开关（架构边界） |
| 翻转前置条件（缺一不可） | ① 该域对拍夹具全绿；② 性能基线不劣化（见 [R5 性能基线](docs/archive/reports/r5-performance-baseline.md)）；③ **模拟器真实路径证据**：成功结算并返回章节页；④ 结果判定仍只走 `sortie-result/1` |
| 影子模式（先做这个） | C# 只**计算**不执行：记录"如果由 C# 跑，会选哪个钩子/打哪个格子"，与上游同一次运行的实际动作逐步比对；差异即漂移 |
| 回退 | 开关置回上游即可，不需要改代码；回退时必须保留当次运行的 C# 决策日志与上游对照 |
| 启用后仍需留的证据 | 每域启用时的工件（按运行批次存 `.runtime/`，脱敏后归档）；与原语/规则版本绑定 |
| 禁止事项 | 未拿到真实路径证据就打开开关；用"单张截图成功""撤退""超时"充当通关证据；在命令分支里复制业务状态机 |

**为什么"影子模式"必须先做**：离线干跑的步覆盖 99.9% 只说明"步骤指向的原语都有实现"，
不代表原语在真机上的动作与上游一致——`capture_clear_boss` 结尾撤退、`fleet_2_protect` 的 20 轮循环、
`CampaignEnd` 的成功时机，这三处都只能靠真机对照确认（见 P2-8 / P2-10 的诚实说明）。

#### P2-14 已完成：影子模式脚手架（只算不执行）（2026-09-26）

`src/Alas.Core/Campaign/CampaignShadow.cs` + 命令 `Alas.Server r5-shadow`：

| 组成 | 做什么 |
| --- | --- |
| `UpstreamLogParser` | 解析上游运行日志里的既有信号：`logger.hr(...)` 的 `──── BATTLE_N ────` 规则行（给出该轮的 `battle_count`）、`Using function: <钩子>`、`ScriptError, No combat executed.`、`Campaign end` / `Battle function exhausted.`。**只认这些真信号，解析不出一律跳过**，日志格式变化时表现为"轮次变少"而不是错误结论 |
| `CampaignShadow.Compare` | 对上游实际发生的每一轮，用 `CampaignBattleLoop.SelectHook(plan, battle_count)` 算出 C# 会选哪个钩子，与 `Using function:` 逐轮比对；输出 一致 / 不一致 / 跳过（非默认变体 `clear_all` / `battle_with_poor_map_data` 尚未迁移，明确跳过） |
| 命令 | `r5-shadow --chapter <章> --level <关> --log <上游日志> [--json]`；**有漂移即非零退出**，可直接当门禁 |

**真实日志验证**（本地已有的一次成功运行）：`campaign_main/campaign_1_4` 4 轮出击，
C# 影子选择与上游实际 `Using function:` **4/4 完全一致**——包括"`battle_1`/`battle_2` 不存在时回退到
`battle_0`"这种回看逻辑，这一条是离线干跑证明不了的。

**对拍**：新增 `tools/diagnostics/verify_r5_shadow.py`（两份确定性夹具日志：一致一例、漂移一例——
含"第 1 轮不一致 + 第 2 轮变体跳过 + 第 3 轮没打成"），已登记进 `verify_all.py`。

**已知限制（如实记录）**：走 `MAP_CLEAR_ALL_THIS_TIME=True` 或 `POOR_MAP_DATA=True` 的关卡
（如 `campaign_2_1` 的一次运行日志）上游用的是另两个 `battle_function` 变体，C# 侧尚未迁移，
影子比对会**全部跳过**——要让这些关卡也能比对，需要先迁移那两个变体。

#### P2-15 已完成：三个 `battle_function` 变体全覆盖（2026-09-26）

| 新增 | 对应上游 | 关键语义 |
| --- | --- | --- |
| `fleet_2_break_siren_caught` | `Map.fleet_2_break_siren_caught()` | `fleet_boss_index != 2` 或无塞壬/无可移动敌人 → 假；没有被抓的格子 → 记 `No fleet caught by siren.`；被抓的不是 2 队 → 警告并清全图标记；否则切 2 队 + 相机对齐（新宿主动作 `EnsureEdgeInsight`）+ 打该格 + 切回 1 队 + 清标记 |
| `battle_boss` | `CampaignBase.battle_boss()` | `brute_clear_boss()` 打成就真，否则记 `No battle executed.` |
| `ClearAllVariant` | `battle_function` 的 `@Config.when(MAP_CLEAR_ALL_THIS_TIME=True)` | 挣脱塞壬 → `clear_all_mystery` →（`battle_count ≥ 3` 时捡弹药）→ 统计 `remain`（敌人+塞壬+要塞，去掉 boss）：有剩余时按 `MAP_HAS_MOVABLE_NORMAL_ENEMY` 走 `clear_any_enemy(sort=('cost_2',))` 或 `clear_bouncing_enemy → clear_siren → clear_mechanism → battle_default`；没有剩余则 `battle_boss()` |
| `PoorMapDataVariant` | `@Config.when(POOR_MAP_DATA=True)` | 挣脱塞壬 → `clear_all_mystery` →（≥3 捡弹药）→ 有 boss 则 `brute_clear_boss`，否则 `clear_siren → clear_enemy` |

`CampaignBattleLoop.ExecuteABattle` 现在**按配置分发变体**（不再是"变体未迁移即阻塞"），
两个变体与默认路径共用同一套 `MapEnemyMoved` 重试语义（`battle_count` 增长即算成功，否则最多 10 次）。

**影子模式随之升级**：`r5-shadow` 增加 `--variant <default_hooks|clear_all|battle_with_poor_map_data>`，
在**未声明的变体名出现在日志里**时判为**不一致**并给出提示（"若该运行确实配置了它，请用 `--variant` 重跑"）——
这样既不再全部跳过，也不会用日志自己证明自己（循环论证）。

**验证**：
- `verify_r5_loop.py` 扩到 **6 个用例**（新增 `clear_all` 变体跑满 20 轮、`battle_with_poor_map_data` 变体）；
- `verify_r5_shadow.py` 扩到 **3 个用例**（新增"声明 `clear_all` 时日志里的 `clear_all` 全部一致"）；
- **真实日志**：`campaign_2_1` 的 `clear_all` 运行，声明 `--variant clear_all` 后 **7/7 一致**；
- 注册原语 **28 个**；执行面/步覆盖不变（5687/5694，99.9%），全库干跑仍 3019/3019 无阻塞。

#### P2-16 真实路径证据（决策层）：模拟器实跑 1-1 + 影子比对 2/2 一致（2026-09-26）

**授权**：用户确认"有模拟器可以进行测试验证流程"，并在本轮明确同意跑一次最小关卡。

**运行**（动作会话，串行执行，仅此一次）：

```powershell
# 只读探测先确认设备与当前页面
Alas.Server queue --file .runtime/device-probe/observe-queue.json --run --read-only-device `
  --serial <模拟器> --screenshot adb --control ADB --adb <adb>
# 观测结果：pages=["page_main","page_main_white"]，ticks=3 errors=0

# 真机跑一次 1-1（动作会话）
Alas.Server queue --file .runtime/device-probe/campaign-1-1-queue.json --run --allow-actions `
  --serial <模拟器> --screenshot adb --control ADB --adb <adb> --artifacts .runtime/device-probe/artifacts-1-1
```

**结果**（原始日志与工件保留在本机忽略目录 `.runtime/device-probe/`，**不入库**——日志含本机绝对路径）：

| 证据 | 值 |
| --- | --- |
| 设备 | 本机模拟器（`127.0.0.1:<port>`，包名 `com.bilibili.azurlane`，server=cn） |
| 队列结论 | `outcome=succeeded tasks=1 failed=0 skipped=0 elapsed_s=72` |
| 批次结论 | **`cleared=true`**、`stages=1`（走 `sortie-result/1`，成功结算） |
| 上游实际出击 | `BATTLE_0 → Using function: battle_0`；`BATTLE_1 → Using function: battle_1`；`<<< CAMPAIGN END >>>` |
| **影子比对** | `r5-shadow --chapter campaign_main --level campaign_1_1 --log <本次运行日志>` → **一致 2 / 不一致 0 / 跳过 0** |

**这证明了什么**：C# 引擎的**关卡循环决策**（按 `battle_count` 选钩子 + 基类回退）在一次**真实设备运行**上
与上游逐步一致——这是离线干跑与历史日志都给不了的那一层证据（历史日志只能证明"过去的运行"，
这次是"当前引擎面对当前上游版本"）。

**这还不能证明什么**（如实记录）：
- **没有**证明原语在真机上的动作与上游一致——本次运行是**上游**在驱动设备，C# 只做了影子计算；
- `capture_clear_boss` 结尾撤退、`fleet_2_protect` 的 20 轮循环、`CampaignEnd` 的成功时机，仍未经真机对照；
- 只覆盖 1-1 这一关，不能外推到其他关卡、其他 `battle_function` 变体或困难/活动图。

**下一步（真机口径）**：把影子模式接进真实运行（同一次运行里既跑上游又记录 C# 决策），
并对 `fleet_2_protect` 这类有内部循环的原语做真机对照；在拿到这些证据前，域级开关保持关闭。

#### P2-17 已完成：影子模式接进运行时（运行内比对，只加观测）（2026-09-26）

`Alas.Core/Runtime/CampaignBatchRunner` 在每关结束后调用 `CompareShadow`：

| 步骤 | 做法 |
| --- | --- |
| 找上游日志 | 读引擎仓库 `log/` 下**本次关卡运行期间**写的 `.txt`（按修改时间取最新），找不到就跳过 |
| 解析关卡 | `CampaignPlanReader.TryReadModule(dataDir, "campaign.campaign_main.campaign_1_1")` → 目录 `campaign_main` + 关卡 `campaign_1_1`；**读不出来就跳过，不兜底到别的关卡** |
| 比对 | `UpstreamLogParser` + `CampaignShadow.Compare(plan, observation, variant)`；变体由运行设置决定（`clear_all` 开关 → `clear_all`，否则默认变体） |
| 落盘 | 本次运行目录写 `shadow-<模块名>.json`（逐轮 一致/不一致/跳过 + 上游日志文件名），并记一条会话日志：一致记 INFO，漂移记 **WARN** |

**边界（写在代码注释里）**：这是**观测项**——漂移不改关卡结论、不改任何设备动作、不影响队列 outcome；
日志或计划缺失时静默跳过，不猜。

**同一映射也开放给离线命令**：`r5-shadow --chapter-module <上游模块名>`（与运行内用的是同一个
`CampaignPlanReader.TryReadModule`），`verify_r5_shadow.py` 增加该形式的用例 → 4 例全 PASS。

> 说明：运行内这条路径的**端到端**要等下一次授权的真机运行才会首次产出 `shadow-*.json`
> （本轮只做了离线可验证的部分：映射、解析、比对、落盘与日志；设备动作没有重复执行）。

#### P2-18 已完成：原语级动作轨迹（真机口径的覆盖对照）（2026-09-26）

`src/Alas.Core/Campaign/CampaignActionTrace.cs` + 命令 `Alas.Server r5-actions --log <日志> [--chapter --level]`：

| 组成 | 做什么 |
| --- | --- |
| `UpstreamActionParser` | 把上游日志行的**动作标记**映射回我们的原语名：`<<< CLEAR FILTER ENEMY >>>`→`clear_filter_enemy`、`Clear enemy: F1`→`clear_chosen_enemy(F1)`、`Pick up ammo: B2`→`pick_up_ammo(B2)`、`Fleet_2 step on C3`→`fleet_2_step_on(C3)`、`Brute clear BOSS`/`Enemy roadblock: D2`→`brute_clear_boss`、`Clear mechanism:`/`Mechanism all cleared`→`clear_mechanism` 等；表里没有的行若"像动作"则收进**认不出的动作行**供人工核对（不猜、不编造） |
| 覆盖对照 | 输出"本次运行用到的原语"里 C# **已实现 / 未实现**的清单——这是**真机口径**的缺口，离线干跑发现不了（干跑只覆盖计划里出现的算子） |
| 计划对照 | 给了关卡时，对照该关计划的算子集合，列出"计划里有、这次运行没走到"的部分（例如 1-1 的 `battle_default`：`clear_boss` 成功了就没走到兜底） |

**真实 1-1 运行的结果**（本轮实测）：

```
[动作轨迹] 共 4 次原语级动作，涉及 3 个原语
  clear_enemy / clear_chosen_enemy(F1) / clear_boss / clear_chosen_enemy(G1)
[C# 覆盖] 已实现 3 / 未实现 0
[计划对照] 该关计划算子 2 个，本次运行没走到的 1 个：battle_default
```

**对拍**：新增 `tools/diagnostics/verify_r5_actions.py`（夹具日志按上游真实格式写，覆盖 10 次动作 /
8 个原语，并特意放一条"表里没有但像动作"的 `BOSS not detected, …` 来验证"认不出的动作行"这条路），
已登记进 `verify_all.py`。噪声修复：`[Emotion fleet_2]`、`Hard satisfied: Fleet_1` 这类属性行
一度被误当成动作，已用收窄后的"像动作"特征排除。

#### P2-19 已完成：识别结果 → 引擎状态适配器（最终引擎的边界）（2026-09-26）

`src/Alas.Core/Campaign/CampaignMapState.cs` + 命令 `Alas.Server r5-state`：

| 组成 | 做什么 |
| --- | --- |
| `FromPlan` | 关卡**声明的静态地图**（`map.map_data` 令牌）→ 引擎格子；标记两支舰队所在格；再按上游 `find_path_initial_multi_fleet` 的顺序算成本场——**非当前舰队先算、当前舰队最后算**，因此 `cost` 最终是当前舰队的成本，`cost_1` / `cost_2` 各留一份 |
| `OverlayDetection` | 叠加地图识别的运行期标志（`MapDetectResult.GridFlags` 的 `"x,y"` → 标志名形态）：`is_enemy`/`is_boss`/`is_siren`/`is_fortress`/`is_mystery`/`is_ammo`/`is_fleet`/`is_cleared`/`is_caught_by_siren`/`may_bouncing_enemy`/`is_mechanism_block`；**认不出的标志名收进 `unknownFlags` 报出**，不静默丢弃也不猜语义 |
| 边界 | 识别只覆盖它认得的格子；**没认到的格子保持声明状态**（不猜成"没有敌人"）。真机识别结果由 `map` 等命令落盘后传进来，本命令**不连设备** |
| 管道 | `--json` 输出的字段与执行夹具的 `grids` 同形 → 可以拼成"帧 → 状态 → `r5-exec` 干跑"这条链 |

用法：
```powershell
Alas.Server r5-state --chapter campaign_main --level campaign_1_1 --fleet-1 A1
Alas.Server r5-state --chapter <章> --level <关> --detection <识别.json> --json   # 可喂给 r5-exec
```

**对拍**：新增 `tools/diagnostics/verify_r5_state.py`（1-1 的声明侧七格 / `ME` 格 `may_enemy` /
`MB` 格 `may_boss` / 舰队格 cost=0 / 全图可达 / 舰队格 `is_fleet`；叠加识别后 F1 变敌人、G1 变 boss、
夹具里故意放的 `is_teleporter` 必须出现在 `unknown_flags`、未被识别的格子保持声明状态），已登记进
`verify_all.py`。实测 1-1 确实是 `G1` 一行七格（`SP -- -- -- -- ME MB`），与上游源码一致。

#### P2-20 已完成：端到端干跑 `r5-run`（真机帧上闭合整条链）（2026-09-26）

`Alas.Server r5-run --chapter <章> --level <关> [--frame <地图帧> | --detection <识别.json>]`：
读计划 → 造引擎状态（声明地图 + 舰队 + 成本场）→ **进程内跑上游地图识别**并叠加运行期标志 →
跑 C# 关卡循环（**干跑**，动作只被记录）。不连设备、不点任何东西。

**在真机帧上的实测**（本地 `data/fixtures/` 的既有帧）：

| 帧 | 关卡 | 结果 |
| --- | --- | --- |
| `inmap_3-1.png` | `campaign_3_1` | 识别 **28 格** → 循环：`battle_0` 真 ×3 → `battle_3` 假 → 撤退结束；干跑动作 **`clear_chosen_enemy(D2, expected=)` ×3**（D2 正是该帧识别出的敌人） |
| `inmap_2-2.png` | `campaign_2_2` | 识别 **35 格** → 首轮 `battle_0` 假（该帧未识别到敌人）→ 撤退结束 |

**意义**：这是"重写后的引擎"第一次在**真机画面**上走完 `识别 → 状态 → 决策 → 原语 → 动作（干跑）`
整条链——之前每一步都是分开验证的。它同时也是"原语动作层对拍"的最后一块前置：
现在可以让同一帧既喂给上游路径、又喂给 C# 干跑，逐步对照动作。

**踩到并修掉的两处接口问题**（都写在代码注释里）：
① `--frame` 传相对路径会失败（识图宿主会把工作目录切到 engine 目录）→ 入口统一转绝对路径；
② 地图识别必须传**完整模块名**（`campaign.campaign_main.campaign_2_1`），只给目录名会
`ModuleNotFoundError`。

**对拍**：新增 `tools/diagnostics/verify_r5_run.py`——可复现用例（识别夹具驱动 `campaign_1_1`：
先打 F1、再打 G1、最后撤退结束）+ 真机帧用例（`inmap_3-1.png`：识别 28 格、首轮 `battle_0`、
动作含 `clear_chosen_enemy(`）；**帧在忽略目录里，缺帧时跳过并说明**，不把"没有帧"当失败。已登记进
`verify_all.py`（R5 检查现共 **8** 个）。

#### P2-21 已完成：原语动作层对照 `r5-diff`（"打的是不是同一格"）（2026-09-26）

`Alas.Server r5-diff --log <上游日志> (--chapter --level | --chapter-module) [--frame | --detection]`：
一边是**上游实际动作**（日志解析），一边是 **C# 干跑动作**（同一关卡跑关卡循环，动作只被记录），
逐原语比对**集合**与**目标格子**。`CampaignActionComparator` 是纯函数（无 I/O），单独可测。

**实测（帧驱动 + 上游格式夹具日志）**：

```
[状态来源] 上游=现场识别（来自日志）；C#=inmap_3-1.png（识别到 28 格）
原语                     上游  C#   上游目标  C# 目标  目标一致
clear_chosen_enemy       2     3    D2       D2       是
clear_enemy              2     0    —        —        —
withdraw                 0     1    —        —        —
[结论] 原语集合不同；目标格子至少一个原语打到同一格（正面证据）
```

**这就是"原语动作层"的正面证据**：C# 引擎在被喂进**真机帧识别结果**后，选了**上游实际打的那一格 D2**。
两处差异也如实列出并说明来源：① 上游会多打一层包装表头（`clear_enemy`），C# 轨迹记录的是叶子动作；
② C# 侧出现 `withdraw`（打不成 → 按 `Error_HandleError` 撤退），夹具日志里没有这一段。

**口径（命令里也打印）**：**不比重复次数**——干跑状态在一次运行内不刷新，同一目标会被反复选中；
差异也可能来自**状态来源不同**（现场识别 vs 声明地图/某张帧），所以结论必须带状态来源。

**对拍**：新增 `tools/diagnostics/verify_r5_diff.py`（识别夹具 `detection-3-1.json` 把 D2 标成敌人 +
夹具日志 `actions-3-1.log`，断言"两边都打 D2、目标交集为真、无目标不一致、只有上游用的包装层原语被列出"；
帧可用时追加一次帧驱动对照，缺帧跳过）。已登记进 `verify_all.py`（R5 检查现共 **9** 个）。

### P4 收口

`IVisionEngine` 只保留识图相关方法；上游目录只剩规则文件与识图组件；文档同步（`docs/architecture-roadmap.md`、本文件、[架构梳理](ARCHITECTURE-NOTES.md)）。

## 4. 验收判据（沿用仓库既有口径，不得放宽）

- **结果**：`sortie-result/1` 合同——`CampaignEnd` 只表示"出击结束"，撤退也抛它；必须有成功结算且返回章节页的证据；
- **对拍**：逐项对拍通过 + 反例被拒绝；
- **性能**：不劣于基线，并说明波动区间；
- **边界**：`tools/diagnostics/verify_architecture.py` 与 `verify_privacy.py` 通过；不新增可执行入口；不把上游逻辑复制进适配层；
- **证据**：夹具、日志、截图只留忽略目录；入库前脱敏，不得改写判据。

## 5. 替换前必须守住的纪律

沿用 [架构梳理](ARCHITECTURE-NOTES.md) 的三个接缝纪律：

1. `IVisionEngine` 不轻易扩容（每个新方法都是将来要拆的债）；
2. 静态导出"能导就导"（它将是 C# 引擎的输入）；
3. 每次新增上游交互都留夹具与性能基线。

追加两条：

4. **不新增"上层直接调用上游流程"的代码路径**（否则将来要拆的范围继续扩大）；
5. 识别结果与流程决策**分开**：引擎只消费识别结果，不继承上游流程代码。

## 6. 风险与前提

1. **强形式可能不可达**：若不处理 3192 个覆写方法与 `Config.when` 这类 DSL，"只依赖静态规则"无法成立；现实目标是**弱形式**——静态规则 + 有限原语/钩子 DSL + 保留的识图组件；
2. **上游持续漂移**：上游更新会改变规则与覆写，需要版本锚定与漂移检测（现有 `tools/sync_all.py --verify` 可作基础）；
3. **"完成"判据要先定义**：是功能面覆盖，还是"每域都有真实路径证据"？否则无法判断"完成后替换"的起点；
4. **性能与失败模式**：识别仍跨语言/跨进程时，时延与失败模式需单独验收；
5. **回退能力**：没有域级开关就不具备上线替换的条件。

## 7. 与既有规范的关系

本目标属 **R5（宿主替换评估）**。在满足门槛前，`AGENTS.md` 的现行边界保持不变：

- 生产战役继续走上游 `CampaignRun.load_campaign()`、章节 `Config` 合并和原生 `Campaign.run()`；
- 视觉、页面和地图识别继续通过上游对象与 `IVisionEngine`；
- JSON 只用于离线展示、溯源与漂移校验。

本文档只记录**目标与流程**，不改变当前实现；任何域开始替换前，都要先补齐 P0 的三份分析并单独评审。
