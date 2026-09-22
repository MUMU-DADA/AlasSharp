# AlasSharp

把 [AzurLaneAutoScript](https://github.com/LmeSzinc/AzurLaneAutoScript)（ALAS，碧蓝航线自动化脚本）
重写为统一 C# 应用的工程。

> 状态：**早期**。数据契约与识图桥接已跑通并验收，业务引擎尚未开始。
> 本仓库只是重写工程本身，不包含上游代码。

---

## 三条架构铁律

### 1. C# 全量重写

前端、调度、任务域、关卡引擎都用 C# 重写。目标是单一应用进程，而不是「C# 前端 + Python 后端」。

### 2. 识图引擎不重写 —— 用进程内 CPython 直调上游模块

图像识别**不在 C# 里重新实现**。C# 通过**进程内嵌 CPython**（Python.NET）直接调用上游
`module.base.*` 的 `Button` / `Template` / `Ocr`，底层就是上游那套 cv2 调用序列。

**为什么必须这样**——手工移植 cv2 已被逐项实测证伪：

| 发现 | 实测数据 |
|---|---|
| OpenCV 会按**模板/搜索区的尺寸比切换相关算法** | 同一块内容、同一位置：搜索区 70×26 时得分 **0.7487**，加常量边到 370×326 后 **1.0000** |
| 不同转换用的**定点精度不同** | `COLOR_BGR2GRAY` / `COLOR_RGB2GRAY` 是 15 位定点；`COLOR_RGB2YUV` 的 Y 是 14 位 |
| 退化情形**非对称** | 模板方差为 0 → 整张结果 **1.0**；窗口方差为 0 → **0.0** |
| 上游存在**通道权重错位** | `match_binary()` 把 `COLOR_BGR2GRAY` 的权重套在 RGB 数据上（R 吃到 B 的权重） |

这些叠加起来，逐位一致在工程上不可达。本仓库保留了一套 C# 参考实现（`src/Alas.Core/Imaging/`）
与 800 例对拍基准：它能通过 99%，但**残差最大 0.25**，足以让判定翻转。所以它只作证据与
回归基准，**不是产品路径**。

### 3. 上游数据同步链不重写

上游的素材与关卡数据本身就是 Python 提取器从游戏资源生成的
（`dev_tools/button_extract.py`、`map_extractor.py` 从游戏 Lua 生成 `campaign/*.py`）。
本工程只在其后加一层格式转换：

```
游戏资源 ──(上游 Python 提取器，不改)──> assets.py / campaign/*.py
                                              │
                                              │  tools/export_upstream_data.py（只做格式转换）
                                              ▼
                                          data/*.json ──> C# 运行时
```

**不要**用 C# 重写 `map_extractor.py` / `button_extract.py`：那等于多养一套必须与上游同步的
游戏资源解析器，上游改数据格式时要跟两次。

---

## 已完成并验收

### S0 · 上游数据契约

| 项目 | 结果 |
|---|---|
| 素材绑定 | **1793** 条，0 条需要求值，四服齐全 |
| 素材缺图 | **0** |
| 关卡 IR | **1437** 个，网格不自洽 **0**，计划不变量违例 **0** |
| 计划可还原性 | tier A 关卡 **1000/1000** 的步骤都能在源码中找到 |
| 导出确定性 | 重算与磁盘产物**逐字节一致** |
| C# 侧独立校验 | 退出码 0 |

关卡按「C# 要写多少代码」分级（决定引擎工作量）：

| 级别 | 数量 | 占比 | C# 侧怎么做 |
|---|---|---|---|
| **A** | 1000 | 69.6% | JSON 规则表驱动，零代码 |
| **B** | 221 | 15.4% | 计划完整，补齐词表外算子 |
| **C** | 216 | 15.0% | 计划不完整（含赋值/嵌套条件）—— 需插件或原生实现 |

另外查出：非 `battle_*` 的**引擎钩子**涉及 48 个关卡，去重后仅 **17 个方法**
（另有 8 处是纯 `super` 委托，无需新增逻辑）。这是引擎侧最精确的工作量清单。

### S1 · 识图桥接

C# 驱动宿主调用上游 Python 识图代码，与独立算出的真值比对。
**两种宿主（进程内 CPython / 进程外 worker）跑同一份 `alas_vision.handle_line()`，都通过验收**：

| 项目 | 进程内 CPython | 进程外 worker |
|---|---|---|
| appear 判定（60 例） | **零不一致** | **零不一致** |
| 期望色解析（分服） | 零不一致 | 零不一致 |
| 调用异常 | 0 | 0 |
| 冷启动（解释器 + numpy/cv2） | 441 ms | 460 ms |
| **稳态单次判定** | **0.073 ms** | **0.074 ms** |
| 协议栈单次往返（ping） | **0.021 ms** | **0.084 ms** |
| 同图重复单次调用 | **0.053 ms** | **0.145 ms** |
| 截图加载（含 PNG 解码） | 5.4 ms | 5.1 ms |
| **首调（一次性导入上游模块链）** | **541 ms** | **527 ms** |

**两处自查更正**（都是我自己的测量方法问题，记录在此以免复现）：

1. 早期只测「批量 64 条总耗时 5.3 ms」就推断「其中 4.4 ms 是进程间通信」——
   **错的**。补 ping 判别实验（ping 在 Python 侧几乎零计算，其往返耗时即协议栈真实成本）
   后发现：进程外 0.084 ms、进程内 0.021 ms，**进程间通信只占约 0.06 ms**。
2. 曾报「单次 appear_on 实测 7~14 ms」并当成待查问题——**也是错的**，是平均值被首调污染：
   第 1 次调用要导入 ALAS 模块链，实测 **541 ms**；把它摊进 60 次平均就得到 ~9 ms 的假象。
   分段计时（`require_image` / `resolve` / `appear_on` / `get_color` / `color_of`）显示
   第 2 次起 `resolve` 只要 0.002 ms。

**结论：每帧真正的判定成本是 ~0.07 ms/次**，一帧几十次判定的量级是几毫秒，
进程内/进程外都能满足。进程内在「每次调用的固定成本」上快 2~4×，但绝对量都很小。
真正需要注意的是**首调 530 ms 的一次性导入**——应在应用启动时预热，别落在第一帧上。

### S3 地基 · 关卡规则解释器

关卡规则不写代码，用 JSON 计划驱动。解释器按上游生成器模板的语义执行
（`if self.X(): return True` → 条件为真则本阶段结束；`return self.Y()` → 终结步骤…）。

验收方式是**两个独立推导互相对拍**：解释器实际执行的算子序列（C# 运行时）
vs 导出器从源码 AST 归一出的计划序列（S0 冻结的 JSON）。三种场景：

| 场景 | 结果 |
|---|---|
| 完整执行顺序（1221→1212 个关卡、5407 步） | **0 不一致** |
| 提前结束语义（条件算子为真） | **0 不一致** |
| tier C 计划不完整 → 必须拒绝执行 | 拒绝 125，**漏放行 0** |

**这个对拍抓出了两个真实的保真缺陷**（S0 当时的校验查不出来，因为它只验证
「计划里的算子在源码中出现」，不验证「源码里的调用是否都在计划里」）：

1. **静默丢步**：`if not self.X(): return self.Y()` 曾被当成 `conditional_negated`，
   分支体里的 `self.Y()` 被丢掉——计划看着完整，实际少调用一次。影响 9 个关卡；
   现在这类分支一律标为未解析（降级 tier C），**绝不给出一份少几步的"完整"计划**。
2. **把死代码当步骤**：上游有手滑留下的不可达语句（`return X` 后面又一句 `return X`，
   见 `campaign/event_20211028_tw/c3.py`）。现在按 Python 语义标注为 `dead_code` 而不执行。
   影响 2 个关卡。

### S1 参考实现（仅作证据）

用 `assets/` 全部素材跑的图像原语对拍：**6192 例**（4790 RGB / 1098 RGBA / 304 灰度），
通道语义、`appear` 判定、容差值、均值颜色全部零不一致（均值最大偏差 2.84e-14）。
它证明了「能对齐的部分」，也划出了「对不齐的部分」——见铁律 2。

---

## 目录

```
src/Alas.Core/          数据模型 + 读取器 + 识图客户端
  UpstreamModels.cs       上游数据契约的 C# 模型
  UpstreamData.cs         契约读取入口
  Vision/VisionWorker.cs  识图引擎客户端（C# 一侧不实现任何图像算法）
  Imaging/                【参考实现，非产品路径】手工移植的 cv2 原语
src/Alas.DataTool/      命令行工具 alashub
tools/                  构建期脚本（需要 Python）
  export_upstream_data.py   上游 .py 产物 → JSON + Schema + 溯源清单
  verify_export.py          数据契约校验（Python 侧）
  vision_worker.py          识图引擎 worker（调用上游模块）
  make_*_fixture.py         对拍基准生成
  diagnostics/              定位过程留下的诊断脚本
```

## 快速开始

需要：.NET 8 SDK、一份 ALAS 仓库（含 Python 环境）。

```powershell
$alas = "..\my fork project\AzurLaneAutoScript"   # 你的 ALAS 仓库路径
$py   = "$alas\.venv\Scripts\python.exe"

# 1) 导出上游数据契约
& $py tools\export_upstream_data.py --repo $alas

# 2) 双向校验
& $py tools\verify_export.py --repo $alas
dotnet build src\Alas.DataTool\Alas.DataTool.csproj -c Release
.\src\Alas.DataTool\bin\Release\net8.0\alashub.exe verify

# 3) 识图桥接验收
& $py tools\make_imaging_fixture.py --repo $alas
.\src\Alas.DataTool\bin\Release\net8.0\alashub.exe vision --repo $alas

# 4) 查看某个关卡被理解成了什么
.\src\Alas.DataTool\bin\Release\net8.0\alashub.exe show campaign_main/campaign_1_1.py
```

`alashub` 子命令：`verify` / `list` / `show` / `imaging` / `matching` / `vision`。

## 路线图

| 阶段 | 内容 | 状态 |
|---|---|---|
| S0 数据契约 | 素材 + 关卡 IR → JSON，双向校验 | ✅ |
| S1 识图桥接 | 识图不重写：进程内 CPython / 进程外 worker 直调上游模块 | ✅ 两种宿主双双验收 |
| S2 地图识别 | 单应性变换 + 网格判定 | 待开始 |
| S3 关卡引擎 | 规则解释器已完成；引擎实现（120 方法 + 17 钩子）待开始 | 🔵 地基完成 |
| S3 关卡引擎 | 120 方法契约 + 17 个引擎钩子 | 待开始 |
| S4 任务域 | 大世界 / 岛屿 / 科研 / 活动… | 待开始 |
| S5 前端 | 读上游 `args.json` 渲染配置 | 待开始 |

---

## 环境限制（本机实测，供排障参考）

- **NuGet / PyPI 均不通**（`SSL connection could not be established`）。离线还原见
  `NuGet.config.example`；离线包缓存里**没有 Python.NET**。
- 因此进程内嵌入**没有用 Python.NET**，而是直接 P/Invoke CPython 的 C API
  （`python314.dll` 本机自带，用到的 8 个函数全部导出）。这条路的可行性已实测：
  `alashub vision --mode inproc` 通过全部 80 例。
- 若将来能装 Python.NET，可以只替换 `PythonHost` 的实现，`IVisionEngine` 之上的代码不动。
- `dotnet build Alas.sln` 在受限沙箱内会失败（解决方案级 `Restore` 静默失败，0 错误 0 警告）。
  已验证可用的入口是**项目级**构建：`dotnet build src\Alas.DataTool\Alas.DataTool.csproj -c Release`。
- 本机 PowerShell 直接 `Invoke-WebRequest` 访问 GitHub / NuGet / PyPI 均 SSL 失败，
  但 `gh` CLI（Go 自带 TLS 栈）正常——排查网络问题时别只看 PowerShell 的结论。

## 上游关系与许可

- 本仓库以 **GPL-3.0** 发布（见 `LICENSE`），**与上游 ALAS 保持一致**。
  **本仓库的全部提交（含历史）均以 GPL-3.0 授权。**
- **为什么不能用更宽松的许可**：识图引擎在**进程内导入并执行上游 ALAS 的代码**
  （`module.base.*` 的 `Button` / `Template` / `Ocr`），构建期也读取上游生成的产物。
  这种结合方式构成衍生作品，许可证必须与上游的 GPL-3.0 兼容 ——
  选 MIT / Apache-2.0 之类的宽松许可会与上游冲突。
- 上游：`LmeSzinc/AzurLaneAutoScript`（GPL-3.0）。
- 分发二进制时请一并履行 GPL-3.0 的源码提供义务。
