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
| 步骤指向已实现原语 | 5456 / 5694（95.8%） |
| 涉及原语 | 31 个（含舰队前缀组合其中 9 个已实现） |
| 计划形状符合契约 | 3006 / 3019 |
| 实参完整（无 `<expr>`） | 5555 / 5694（97.6%） |

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
