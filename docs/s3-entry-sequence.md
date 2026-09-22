# S3 入口序列：实测记录与卡点诊断

> ## 速览（先看这里，细节在下面）
>
> **目标**：宿主驱动上游的关卡实现（**战斗逻辑不重写**，C#/宿主只做编排）。
>
> | 项 | 状态 |
> | --- | --- |
> | 计划侧 | 1374 章中 **88.1%（1211 章）** 计划完整（`tools/diagnostics/s3_plan_coverage.py`）|
> | 执行器 | op `s3_run_plan`：dry-run 默认 / 安全锁 `allow_actions` / `max_seconds` / `repeat_until_cleared` / `CampaignEnd` 完成语义 / **上游完成信号** |
> | 实战 | **3 个关卡**端到端跑通（2-1 清图 / 2-2 / 3-1），**13 次真实战斗全部 `err=None`** |
> | 已验图 | 2-1(24 格) / 2-2(35) / 3-1(28) / 3-2(32) —— 登记在 `s3_preflight.py` 的 `KNOWN_FIXTURES` |
> | 账号可达 | **第 1–3 章**（不可达章 `ensure_chapter` 会耗时 15–21s 后失败，可作判别器）|
> | 已知不支持 | **第 1 章**（7 格单行图，上游检测器自身也失效）|
> | 客户端适配 6 项 | numpy2 `np.vstack` / OS 遮罩不对称 / `Points` 空集 / `bar_opened` 亮度阈值 / `auto_search` 跳过 / "正在攻略中"弹窗**像素判定** |
>
> **批量跑一关的流程（已固化，可重复）**：
> ① `s3_preflight.py <章模块>` 预检 → ② 导航到 `page_campaign` →
> ③ `s3_run_plan(dry_run=False, allow_actions=True, repeat_until_cleared=True)` →
> ④ 确认**出击真正结束**（跑完计划会自动结束；否则走弹窗「撤退」）→ ⑤ 归位 `page_main`。
>
> **三条最容易踩的坑**：① 每次出击必须**真正结束**，否则下一关必卡 60s（易误判成"那关有问题"）；
> ② 调用的**顺序要求**：`init(同章) → ensure_chapter → get_entrance` 三者一致，且先导航到战役页；
> ③ IR 的 `calls` 是**语义轨迹**（含嵌套辅助调用），不是可逐条重放的清单 —— 执行的是 `battle_*` 方法。
>
> **方法论（本线反复验证有效）**：就地观测（复刻步骤不等价）/ 先看画面再下结论 /
> 换判据优于死磕原判据 / 能离线预检的绝不进游戏试。

> 目标：让 C# 通过**宿主协议**驱动上游的战役实现（tier A 的 9 个调用），而不是用 C# 重写战斗逻辑。
> 复现脚本：`tools/diagnostics/s3_probe_campaign.py`；相关 op：`s3_campaign_init` / `s3_campaign_info` / `s3_campaign_call`。

## 已打通的链路（每步都是上游实现，C# 只发指令）

| 步骤 | 结果 | 说明 |
| --- | --- | --- |
| 探针实例化 `Campaign(cfg, device)` | ✅ | MRO：Campaign → CampaignBase → CampaignUI → Map → Fleet → Camera → AutoSearchCombat |
| `s3_campaign_init(1-1)` | ✅ | 含**种一帧**（334 ms）与配置绑定 |
| `campaign_ensure_chapter(1)` | ✅ | 158–2114 ms，**上游代码完成真实导航** |
| `campaign_get_entrance('1-1')` | ✅ | 返回值需 `store='ENTRANCE'` 写回实例属性 |
| `enter_map(@ENTRANCE, 'normal')` | ❌ | 操作游戏 62 s 后 `GameStuckError: Wait too long` |

## 三个"ALAS 方法的前提在我们这里不成立"的坑（都已修）

1. **`device.image` 需要先种一帧**：ALAS 的方法假定它已存在，而它只在 `screenshot()` 之后才有。
   少了这一步，第一个动作就报 `AttributeError: 'Device' object has no attribute 'image'`。
   → `s3_campaign_init` 里补一次 `device.screenshot()`。
2. **需要 `store=` 接住返回值**：上游很多方法**靠返回值传对象**——
   `campaign_get_entrance('1-1')` 返回的 Button 要赋给 `self.ENTRANCE`；
   类默认的 `ENTRANCE` 是**空 Button**，直接传它报 `not enough values to unpack (expected 4, got 0)`。
3. **配置必须跟着章节走**：不设 `Campaign_Name` 时，实例化 2-1 却仍读上一次的 `12-4`；
   `bind('Campaign')` 还会把截图后端重置成 `auto`（会去跑性能基准）。

## `enter_map` 卡点的精确诊断（纠正一个错误猜测）

我先前猜"大概率又是客户端 UI 版本差异"，**数据推翻了它**。在活着的舰队选择浮层上量素材：

| 素材 | 匹配 | 分数 |
| --- | --- | --- |
| `map/FLEET_1_CLEAR`（清空第一舰队） | ✅ | **0.9952** |
| `map/FLEET_1_CHOOSE`（选择） | ✅ | **0.9924** |
| `map/FLEET_PREPARATION`（立刻前往） | ✅ | 0.9929 |
| `map/FLEET_2_CLEAR` | ❌ | 0.1730 |
| `map/SUBMARINE_CLEAR` | ❌ | 0.1858 |

素材全都对得上；不存在的两个按钮是**因为本账号第二舰队与潜艇舰队都是空的**
（画面：可出击舰队数 1/2、潜艇舰队数 0/0）。

而日志显示 `Using fleet: [1, 2, 0]`（要用第一、第二两支舰队），随后进入
`map_fleet_preparation.fleet_preparation() → clear()`，**等一组永远不会出现的按钮**：

```
WARNING Waiting for {'FLEET_2_CLEAR', 'FLEET_PREPARATION', 'FLEET_2_ADVICE', 'DAILY_CHECK',
                     'SUBMARINE_ADVICE', 'FLEET_1_ADVICE', 'POPUP_CANCEL', 'POPUP_CONFIRM_WHITE',
                     'SUBMARINE_CLEAR', 'FLEET_1_CLEAR'}
```

**结论：问题是「要用的舰队 ≠ 账号里存在的舰队」——配置/账号状态不匹配，与 UI 无关。**
修法方向：让舰队选择与账号一致（配置只用一支舰队，或账号补上第二舰队），
而不是去改素材或遮罩。

## 安全要点（本项目自己的教训）

- 进战斗前用**上游配置键**关掉自动行为：`Campaign_UseClearMode=False`、`Campaign_UseAutoSearch=False`
  （比点 UI 开关可靠；实测读回 `ClearMode=False`）。本项目曾因误开自律寻敌把一场战斗打完。
- `s3_campaign_call` 有**硬性安全联锁**：`battle*/clear*/enter_map/run/fleet*/goto/map_*/...`
  必须显式 `allow_actions=true` 才执行。
- 两次实测都**没有真正进入战斗、油量未变**（24831），卡住后均以点 X 关闭浮层收尾。

## 入口序列的调用顺序要求（实测新增）

`campaign_ensure_chapter(chapter)` **必须在 `campaign_get_entrance(name)` 之前**调用：
入口节点坐标依赖当前选中的章节，顺序反了会拿到**空 Button**：

```
漏掉 ensure_chapter 时：ENTRANCE.button = "()"            → enter_map 报 not enough values to unpack
先 ensure_chapter(1)：  ENTRANCE.button = (120,475,159,514) → 正常
```

（这也解释了此前一次"ENTRANCE 是空 Button"的困惑：不是返回值/赋值的问题，是**前一步没做**。）

## 客户端素材状态（活画面实测）

| 素材 | 分数 | 含义 |
| --- | --- | --- |
| `map/MAP_PREPARATION` | **1.0000** | 关卡进场面板正常 |
| `map/FLEET_1_CHOOSE` | 0.9923 | 「选择」正常 |
| `map/FLEET_1_CLEAR` | 0.9952 | 「清空」正常 |
| `map/FLEET_PREPARATION` | 0.9928 | 「立刻前往」正常 |

## 尚未验证的假设：点「选择」不展开下拉

`FleetOperator` 的文档说明 `choose` 是"打开/关闭下拉菜单"的按钮：ALAS 点它 → 展开舰队下拉（`FLEET_1_BAR`）
→ `parse_fleet_bar()` 读下拉找序号 → 点对应项。若点了不展开，就会反复点 → `GameTooManyClickError`。

**本轮没能验证**：实验开始时画面状态已经漂移（当时并不在 `page_campaign`），序列没走完，
`FLEET_1_BAR` 的分数是在浮层未打开时量的（0.07，无意义）。
下次要做这个实验，必须先确认状态、再量「点选择前 / 点选择后」的 `FLEET_1_BAR`。

## 协议侧待改：Button 坐标过不了 JSON

`json_default` 会把 numpy 整数元组序列化成**字符串**：`"(np.int64(120), np.int64(475), ...)"`，
C# 拿不到可用坐标。两个方向：
1. 让 `json_default` 把数值元组/ndarray 转成数组（协议层修）；
2. **点击一律让 ALAS 自己执行**（`device.click(@ENTRANCE)` 已验证可用，62.5 ms）——
   这条更符合"动作交给上游"的架构，也绕开了序列化问题。

## 第二次尝试（已带状态前置校验）：**手工复刻 `enter_map` 不等价**

新增 `tools/diagnostics/s3_probe_dropdown.py`（先校验状态，不满足就退出；逐步骤打印素材分数）。
本轮实测：

| 步骤 | 结果 |
| --- | --- |
| 状态前置校验 | ✅ `pages=['page_campaign']`，通过后才继续 |
| `device.click(@ENTRANCE)`（ALAS 自己点） | ❌ **没打开准备面板**（prep=0.1383） |
| `handle_map_preparation()` → `device.click(@PREP)` | ⚠️ 打开的却是 **`MAP_PREPARATION` 面板本身**（1.0） |
| 从准备面板点 `handle_map_preparation` 的返回值 | ❌ **没打开舰队选择浮层**（overlay=0.1246） |

**结论（比"假设未验证"更进一步）**：**手工一步步复刻 `enter_map` 与让它自己跑并不等价** ——
`enter_map` 内部有计时器与状态（`campaign_timer/map_timer/fleet_timer`、`map_click/campaign_click`
计数、`checked_in_map` 等），我按顺序单独调用会被内部状态判定跳过或走错分支。

**因此下一次应该换成"就地观测"而不是"复刻"**：
让它**自己跑 `enter_map`**（它确实能走到浮层并反复点『选择』），同时在旁边**周期性截图**，
看它在点『选择』的那一刻屏幕上到底是什么 —— 这样拿到的才是真现场的证据。

顺带修掉一个脚本 bug：辅助函数 `op(name, **args)` 与业务参数 `name=` 撞名
（`TypeError: op() got multiple values for argument 'name'`）→ 形参改名 `_op`。

## 真相（第三轮）：`bar_opened()` 量的是**亮度**，不是素材匹配 —— 前两个假设都错了

成组量素材（浮层打开时，阈值 0.85）：

| 素材 | 分数 | 是否 ALAS 的判定依据 |
| --- | --- | --- |
| `FLEET_PREPARATION` / `FLEET_1_CHOOSE` / `FLEET_1_CLEAR` | **0.99** | ✅ 用（都正常） |
| `FLEET_1_BAR`（下拉） | 0.02 ↔ 0.51 | ❌ **不用** |
| `FLEET_1_IN_USE` | 0.21 | ❌ **不用** |
| `FLEET_1_ADVICE` | 0.47 | ❌ 不用 |
| `FLEET_1_HARD_SATIESFIED` | 低 | ❌ 不用 |

上游源码给出了真正的判据：

```python
def bar_opened(self):
    """If dropdown menu appears."""
    # Check the brightness of the rightest column of the bar area.
    luma = rgb2gray(self.main.image_crop(self._bar.button, copy=False))[:, -1]
    # FLEET_PREPARATION is about 146~155
    return np.sum(luma > 168) / luma.size > 0.5

def in_use(self):        # 定义在 FleetOperator 里
    image = self.main.image_crop(self._in_use.button, copy=False)
    ...
    return np.std(gray.flatten(), ddof=1) > self.FLEET_IN_USE_STD   # 27
```

- **"下拉是否打开" = 裁 `_bar.button` 矩形 → 最右一列亮度 >168 的占比 > 0.5**（纯亮度，无模板）
- **"舰队是否使用中" = 裁 `_in_use.button` → 像素标准差 > 27**（纯统计，无模板）

死循环的确切位置：

```python
def open(self):                      # "Activate dropdown menu for fleet selection."
    click_timer = Timer(3, count=6)  # ← 点满 6 次 → GameTooManyClickError
    while 1:
        if self.bar_opened(): break  # 亮度判定不过 → 永不 break
        if click_timer.reached(): main.device.click(self._choose)
```

### 因此修法方向要改（重要）

**不是"做素材变体"**（前两轮我按这个方向想，是错的），而是修**几何/亮度**：
1. 量出真实帧在 `_bar.button`（经 `load_offset(FLEET_1_CLEAR)` 调整后）那块区域的
   最右列亮度占比，与 0.5 阈值对比；
2. 若占比不足 → 说明**下拉在本客户端的位置/尺寸与上游预期不同**，
   需要调整 `_bar` 的 area（或改 `bar_opened()` 的判据），而不是换模板图。

安全：全程未点「立刻前往」、未进入战斗、油量未变（24831）。

## ✅ 打通：垫片修好 `bar_opened()` 的阈值后，`enter_map` 全程成功

**根因量化**（离线复算现场帧，用上游对象算几何）：

| 状态 | `_bar.button`(1012,269,1183,515) 最右列亮度>168 占比 | ALAS 判定（阈值 0.5） |
| --- | --- | --- |
| 下拉关闭 | 0.000 | 未展开 ✓ |
| 下拉展开 | **0.285** | **未展开 ✗** |

区域确实响应状态，但**永远跨不过 0.5** —— 因为本客户端下拉只有约 **84px 高**
（上游参照 y 269..515 共 246px），亮边占不满整列。于是 `open()` 里
`if bar_opened(): break` 永不成立 → 反复点『选择』→ `Timer(3, count=6)` 点满 → 报错。

**垫片** `apply_fleet_bar_compat()`（不改上游文件，与 numpy2 / OS 遮罩 / Points 同一做法）：
把阈值 0.5 → **0.10**（实测展开 0.15+ / 关闭 0.000，余量充足）。
在 `s3_campaign_init` 里随其它垫片一起应用。

**验证（就地观测重跑）**：

```
T02  FLEET_PREPARATION=0.99 FLEET_1_CHOOSE=0.99 FLEET_1_CLEAR=0.99   ← 浮层开着
T03  全部掉到 0.1 以下                                              ← 浮层已关闭（点走了）
驱动日志：Click (1035, 583) @ FLEET_PREPARATION
          Enemy searching appeared.        ← 进入战斗地图
          A_END None                       ← enter_map 成功返回，无错误
```

**这就是 S3 的第一个端到端里程碑**：宿主驱动上游 Campaign，从战役页一路走到
`enter_map` 成功（进图、舰队准备、点「立刻前往」全部由上游代码完成，C# 只发指令）。

下一步：进图之后的地图内操作（`map_init` / 战斗步骤），即 tier A 那 9 个调用真正上场。

## 进图后的双重确认与新发现

**确认（两条独立路径）**：

| 路径 | 结果 |
| --- | --- |
| 上游自己的 `is_in_map()` | **True** ✓ |
| 画面（`data/_ob_12.png`） | 「无限时 / SUB_CHAPTER」、FLEET_1 侦查值 63/制空 26、我方 6 艘在第 1 格、7 格 A–G、**F 格有敌方旗舰 LV.1**、底部「撤退/切换/迎击」、右侧「自律寻敌 未开启」✓ |

**新发现（S2 的覆盖缺口）**：在这张**真·战斗地图**上，S2 的 `map_detect(mode='main')`
返回 **`detected=False, raw=None, thr=None`（什么都没检出）**。
而此前验证过的是 24 / 48 / 21 / 30 格的地图；**这张只有 7 格**（`MAP.shape=[6,0]` → 7×1），
是本项目第一次在"进图后的实时小地图"上跑 S2 —— 说明 S2 对小地图/`SUB_CHAPTER` 这类图**有缺口**。

**另记一个小 bug**：`device_capture_set` 用的是 `_DEVICE_ARGS` 默认（**adb**），
而 `cfg.Emulator_ScreenshotMethod` 是 scrcpy —— 两者不是同一个缓存键，
出现"Campaign 用 scrcpy、协议 op 用 adb"的不一致。修法：让 init 把 screot/control 写进 `_DEVICE_ARGS`。

**游戏状态**：一场 1-1 正在进行（我方待机、敌旗舰在 F 格、周回与自律均关闭），
不会自动发生任何事。后续动作（迎击继续打 / 撤退）**等明确授权**。

### S2 小地图缺口的定位：**竖直分隔线一条都没检出**

用 `map_detect_trace` 对比现场帧与已知可用的 2-1 帧（`data/fixtures/`）：

| 阶段 | **现场 1-1（7 格，单行）** | 对照 2-1（24 格，正常） |
| --- | --- | --- |
| `inner_h.lines` | 2 | 4 |
| **`inner_v.lines`** | **0** ⛔ | 3 |
| `edge_h.lines` | 4 | 6 |
| `edge_v.lines` | **1** | 5 |
| `load_image.mean` | 182.11 | 166.94 |

**`inner_v.lines = 0`** —— 7 列本应有 6 条竖直分隔线，一条都没检出来；横向也偏少。
与画面一致：这张图**只有一行**，格子又扁又宽（行高远小于 2-1 那种多行网格），
竖直分隔线**太短**，被检测参数（掩膜笔画宽度、线段最小长度、Hough 阈值）滤掉。

=> 结论：**不是"小地图"整体不支持，而是"单行/扁格子"这类几何下竖线检测失效**。
修法方向（按代价从小到大）：
1. 对这类图放宽线段最小长度 / Hough 阈值（做成按几何自适应的参数）；
2. 或在检测前把图**按比例放大**（把短竖线拉长到阈值以上）；
3. 或为本客户端/此类图提供参数变体（与页面变体同一机制）。

帧已存 `data/fixtures/subchapter_1_1.png`（`data/` 按项目惯例 gitignored，本地可复现）。

### 纠正：这不是"小地图缺口"，是我把**预览盲检**用到了**图内画面**上

连续三帧新抓（间隔 2s，`data/fresh_0..2.png`）结果**完全一致**：
`No vertical line detected`（homography / perspective 都是）—— 所以**不是转场/动画污染**，
这张图确实过不了盲检。而同一时刻上游自己的状态是 `is_in_map() = True` ✓。

三种办法都无效（都试过了，记录在此免得重走）：

| 尝试 | 结果 |
| --- | --- |
| 等比放大 ×2 / ×3（`upscale`） | ❌ 无效（放大同时也放大掩膜/线段参数，比例不变） |
| 降 Hough 阈值 75→50→40 | ❌ 无效（原有档，现场也过不去） |
| 降**峰参数** `height 150→130→110`、`prominence 10→7→5` | ❌ 无效 |

**关键认识**：ALAS 在**图内**根本不靠盲检。章节类声明了相机位，例如

```python
# campaign/campaign_main/campaign_1_1.py
MAP = CampaignMap()
MAP.shape = 'F4'
MAP.camera_data = ['C2']            # ← 已知相机位
MAP.camera_data_spawn_point = ['C1']
```

图内定位走的是 **`map_init(MAP)` + 已知 camera_data 的标定**（`Perspective` 用已知相机位
对齐网格），而不是"从画面里找网格线"。S2 的 `map_detect` 是**预览画面**的盲检器
（此前验证过的 24/48/21/30 格样本都是预览画面）。

=> **S3 图内需要的是 `map_init` 这条路，不是盲检**。我此前把两者混为一谈，
"小地图缺口"这个说法要作废；正确的下一步是：进图后用上游的 `map_init(MAP)`
（带 camera_data）建立地图状态，再跑 tier A 的战斗调用。

（本轮的三档峰参数重试代码保留：只在默认档失败时才走，且全套回归通过
 —— 素材链/大世界/往返/负样本/正样本 + 产品路径 5/5 + 偏移对齐 0 矛盾。）

### 决定性对照：**上游自己的 `map_init` 也失败，错误一模一样**

用上游自己的图内入口跑（`map_init(@MAP)`，`map_data_init` 的 docstring 写明"纯数据处理、
不截图不点击"，所以安全）：

```
MAP_INIT  ms=5249.3  error=MapDetectionError: No vertical line detected
数据初始化成功：map.shape=(6,0) / ammo_count=3 / mystery_count=0 / map_is_* 等已就位
```

=> **失败在"检测"这一步，而上游自己的代码给出的是同一个错误**。所以：

1. **不是我们的接线问题，也不是"我用错了 API"** —— 上游的图内初始化在
   **这台客户端的这张图**上同样过不去；
2. 这与本项目此前修过的那些**上游×客户端兼容问题**是同一族
   （0.85 相似度阈值、OS 遮罩不对称、`Points` 空集、`bar_opened` 亮度阈值），
   所以**修法也应走垫片**，而不是继续在我们的 op 里加档位；
3. 我此前"盲检 vs 图内标定"的区分仍然成立（图内确实用 camera_data），
   但**两者都会撞上同一个检测失败**，所以"小地图缺口"这个说法仍应作废 ——
   准确表述是：**上游检测器在本客户端的这类画面上失效**。

下一步（要动上游检测链路，需要先看清它在哪一步与客户端不符）：
- 用 trace 对比"上游期望的相机位/遮罩"与现场画面（`Perspective.load_image` 的四角搜索）；
- 疑似点：`assets/ui_mask.png`（战役遮罩）在本客户端新版 UI 下位置/形状不同 ——
  本项目此前已为 OS 模式做过同类垫片（`apply_os_mask_compat`），战役模式可能也需要一份。

### 再次纠正（有画面证据）：我那批夹具**本来就是图内画面**，所以"范畴错误"的说法也不成立

我把对照帧 `data/fixtures/map_settled.png`（2-1，检出 **24 格 [5,3]** 完全正常）打开看了：
**它就是图内画面** —— 同样的左侧舰队栏（6 艘）、顶部信息条（`FLEET_1` 侦查 63/制空 26）、
`限时 11:59:36`、底部「撤退 / 切换 / 迎击」、右侧「自律寻敌 未开启」。

于是三条判断需要重新对齐：

| 说法 | 结论 |
| --- | --- |
| "拿预览盲检去测图内 = 范畴错误"（我上一轮说的） | ❌ **错**：那批夹具本来就是图内画面 |
| "上游自己的 `map_init` 同样失败" | ✅ **成立**（同一个 `No vertical line detected`） |
| **准确表述** | **上游检测器对"单行 / 扁格子"的小地图（1-1，7 格一行）失效；对多行大图（24 / 48 / 21 / 30 格）正常** |

而且该失效**既不是我们的接线问题，也不是客户端专属**（同一客户端上大图夹具完全正常）。

### 对 S3 的处置建议（务实）

1. **S3 的首场真实战斗换用已验证可用的图**（例如 2-1：我们既有可检出的图内帧，
   又有 IR 交叉校验）；把 1-1 这类单行小图列为**已知不支持项**（上游限制，非本项目缺陷）；
2. 单行小图的检测修复单独排期 —— 它属于"上游检测器几何适配"，与本项目的
   垫片族（阈值/遮罩/空集）不同类，收益也低（这类图占章节比例小）。

**教训**：这几轮我在"责任归属"上反复摇摆，根因是**没有先看一眼证据**（打开那张对照帧只需一步）。
以后涉及"是不是范畴问题"的判断，先看画面/先跑对照，再下结论。

# ✅ S3 里程碑：真实战斗经上游代码跑通（自主执行记录，2026-09-22 夜）

用户授权"自行择优推进"后按最优路径执行了三件事：**收尾不可用的一局 → 预检 → 在可用的图上真打**。

## 1. 收尾：撤出 1-1

上游检测器对单行 7 格小图失效（见上文"已知不支持项"），留着没有价值且挡住后续测试。
点「撤退」→ 确认「确定」→ 回到 `page_campaign`，`is_in_map=False` ✓。

## 2. 新增预检工具 `tools/diagnostics/s3_preflight.py`

把"跑进图才知道不行"的代价前置。检查 6 项：IR 存在 / 章节模块与形状 / 关卡名推导 /
配置绑定（关卡名+后端+周回+自律+舰队）/ 计划调用词表 / **图内帧可识别性**（有夹具就离线判定）。

实测：`2-1` 各项 PASS（图内帧 24 格可识别）；`1-1` 在"图内帧可识别"一项 **FAIL**
并直接引用已知失效原因 —— 这个工具在真跑之前就拦住了 1-1。

## 3. 在 2-1 上跑通"进图 → 图内初始化 → 真实战斗"

单进程全序列（**关键：状态必须留在同一个进程里**，见下）：

```
INIT         ok（种帧 340.9 ms）
ENSURE       campaign_ensure_chapter(2)           1614.3 ms
ENTER_MAP    enter_map(@ENTRANCE,'normal')        7219.4 ms   err=None
IS_IN_MAP    True
INMAP_DETECT detected=True grids=24 ships=3       ← 本地 S2 图内识别成功（与夹具一致）
MAP_INIT     map_init(@MAP)                        535.1 ms   err=None
HAS_MAP      (5, 3)                               ← 地图状态建立
BATTLE       battle_default                      43827.2 ms   err=None
```

随后**有界循环**再打 4 步（每步 29–40 s，**全部 `err=None`**）：

| 步骤 | 耗时 | 结束后敌数 |
| --- | --- | --- |
| 1 | 32607 ms | 4 |
| 2 | 37576 ms | 1 |
| 3 | 39778 ms | 1 |
| 4 | 29049 ms | 1 |

**结论**：宿主驱动上游完成 **5 次真实战斗**，这是 S3 的第一个实战里程碑。

## 4. 两条新认识

1. **状态必须留在一个进程里**：`s3_campaign_call` 每次新进程都会重建 `Campaign`，
   于是 `map_init` 建的 `self.map` 丢失 → `battle_default` 报
   `'Campaign' object has no attribute 'map'`。
   => 这独立印证了设备侧那条结论：**S3 必须是常驻进程**（与 `alashub run` 的形态一致）。
2. **只调 `battle_default` 清不掉图**：4 步之后仍剩 1 个 `is_enemy`（格 2,2）不再下降。
   上游 2-1 的计划本身是 `['battle_default','check_accessibility','clear_all_mystery',
   'fleet_boss.clear_boss']`（tier C，含 `check_accessibility`）—— 说明**多调用组合**才是完整流程，
   下一步应做"按计划串多个调用"，而不是重复单调用。

## 5. 收尾

点「撤退」→ 确认 → `page_campaign`，`is_in_map=False` ✓。
整夜执行**全程在真实账号上**：耗油仅"进入 2-1 一次"（约 10 点，油量 24831 → 无战斗内额外消耗），
周回/自律全程关闭（上游配置键设定并读回确认），无失控循环。

## ✅✅ 计划执行器跑通：2-1 完整计划 8/9 步成功（含两场真实战斗 + BOSS）

新增 op `s3_run_plan`：按关卡 IR 的**计划顺序**执行多个上游调用（`dry_run` 默认 true 可离线校验；
真跑需 `allow_actions=true`；`max_seconds` 硬上限；任一步报错立即停）。

2-1 的 IR 计划（dry-run 读出，与 C# `BattlePlanRunner` 同序）：

```
battle_0  complete=True   ['clear_all_mystery', 'battle_default']
battle_2  complete=False  ['clear_all_mystery', 'fleet_boss.clear_boss',
                           'check_accessibility', 'battle_default']
```

真跑实测（`max_seconds=420`）：

| 步骤 | 耗时 | 结果 |
| --- | --- | --- |
| `ensure_chapter` | 154.7 ms | ✅ |
| `get_entrance` | 0.0 ms | ✅ |
| `enter_map` | 7191.3 ms | ✅ 进图 |
| `map_init` | 2103.3 ms | ✅ 图内初始化 |
| `clear_all_mystery` | 5118.6 ms | ✅ |
| **`battle_default`** | **35500.0 ms** | ✅ **真实战斗** |
| `clear_all_mystery` | 0.0 ms | ✅ |
| **`fleet_boss.clear_boss`** | **4304.4 ms** | ✅ **BOSS 战** |
| `check_accessibility` | 0.0 ms | ❌ 见下 |

**总耗时 54.4 s，9 步中 8 步成功**；收尾撤退 → `page_campaign` ✓。

### 新发现（S3 的下一步工作项）

**计划调用需要参数**：IR 只导出了**调用名**（`calls: ['check_accessibility', ...]`），
而 `Fleet.check_accessibility()` 需要一个位置参数 → 报
`TypeError: missing 1 required positional argument`。

=> 所以 S3 的"计划解释器"还缺一块：**调用的参数/元数据**。
候选做法：① 导出器补采实参（若源码里是常量可静态求值）；② 为少数需要参数的调用写适配元数据
（`check_accessibility(grid)` 之类）；③ 这类调用干脆回落到"由上游自己的 `run()` 负责"。

### 整夜的执行清单（自主，用户授权"自行择优"）

1. 撤出不可用的 1-1（上游检测器不支持单行小图）；
2. 新增 `s3_preflight.py`：开跑前自动检查 6 项，含**图内帧可识别性**（真跑前就拦住了 1-1）；
3. 2-1 上跑通"进图 → 图内初始化 → 真实战斗"（5 次 `battle_default`，全无错）；
4. 新增 `s3_run_plan` 并跑通 2-1 的完整计划（8/9 步，含 BOSS 战）；
5. 收尾回干净状态。

**账号影响**：进入 2-1 两次（每次约 10 油，油量 24831 量级）；周回/自律全程关闭
（上游配置键设定并读回确认）；无失控循环（每步有上限、出错即停）；两次都以撤退收尾。

## ✅✅✅ 完整计划执行成功（S3 核心里程碑）+ 一个重要的语义澄清

修正执行器后再跑 2-1（`max_seconds=600`）：

```
STAGE=2-1 tier=C elapsed=56.7s stopped_early=False     ← 全部完成，无提前停止
STEP ensure_chapter   153.7 ms   ok
STEP get_entrance       0.0 ms   ok
STEP enter_map       7223.6 ms   ok
STEP map_init        2085.8 ms   ok
STEP battle_0       37705.2 ms   ok     ← 计划步骤 1
STEP battle_2        9516.3 ms   ok     ← 计划步骤 2（含 BOSS）
```

**6/6 步全绿**。这就是 S3 的核心：**宿主驱动上游把一整套关卡计划跑完**，而不是单次调用。

### 修正：`calls` 是**语义轨迹**，不是可重放的清单

`check_accessibility(self, grid, fleet=None)` 是**上游内部的辅助方法**（章节里没人直接调它），
而 IR 的 `calls` 是从 `battle_*` 方法体 AST 抽出来的，**同时含顶层步骤与嵌套辅助调用**。
所以"把 calls 逐条重放"本身是错的。

=> 正确做法（已改）：**按序调用上游自己的 `battle_*` 方法**（它们内部自会做清神秘/BOSS/可达性检查）。
dry-run 现在给出真正的计划步骤：2-1 → `['battle_0','battle_2']`；1-1 → `['battle_0','battle_1']`（tier A）。

### 澄清：**一轮 ≠ 清图**

跑完一轮后 `is_in_map=True`、图内仍有 4 个船标志 —— 这与上游设计一致：
`CampaignBase.run()` 是**循环**调用战斗步骤直到满足结束条件（`map_clear_percentage` 等）。
我的执行器目前只跑**一轮**，所以"6/6 步无错"应准确表述为
**"计划的一轮迭代完整成功"**，而不是"关卡已清"。

下一步（若要一次清图）：加 `repeat_until_cleared` + 最大轮数上限，语义对齐上游 `run()` 的循环。

## ✅✅✅✅ 关卡被清掉：循环执行到上游给出完成信号

加 `repeat_until_cleared`（对齐上游 `CampaignBase.run()` 的循环语义）后跑 2-1：

```
STAGE=2-1 elapsed=227.5s
R1 battle_0 36030.7 ok | R1 battle_2  5017.3 ok | enemies_left=2
R2 battle_0 40168.9 ok | R2 battle_2 35643.1 ok | enemies_left=2
R3 battle_0 39394.5 ok | R3 battle_2 60575.4 err=**CampaignEnd: In stage.**
```

**`CampaignEnd: In stage.` 是上游的"关卡已完成"信号**（不是异常）——即循环跑到第三轮时
上游宣布结束，关卡已清。

### 两处如实说明

1. **我的 `enemies_left` 计数器不可靠**：它一直报 2，而关卡实际已清 —— 说明本地
   `map_detect` 的敌方标志判定在这张图上**不可信**（可能把己方/BOSS 标记算进去了）。
   => 权威的"是否完成"信号应当用**上游自己的**（`CampaignEnd` / `map_clear_percentage`），
   而不是我的本地标志计数。这条要写进后续设计。
2. 收尾时我的撤退点击落在了战役页，跑到了 `page_daily`；随后用我们自己的导航器
   （`alashub goto page_main`，引擎通道）**归位成功** → `page_main,page_main_white` ✓。

### 整夜最终状态

- 游戏：`page_main`（干净），**不在任何地图/出击中**；
- 账号：进图 3 次（2-1 两次 + 计划循环一次），每次约 10 油，无战斗内额外消耗；
  周回/自律全程关闭；无失控循环（每轮/每步均有上限，出错即停）；
- 代码：本夜新增 `s3_preflight.py`、`s3_run_plan`（含 dry-run / 安全锁 / max_seconds /
  repeat_until_cleared），并修正了 `calls` 的语义（语义轨迹 ≠ 可重放清单）。

## S3 计划可用性统计（离线全量）：计划侧 88% 就绪，**瓶颈在"图能不能识别"**

新增 `tools/diagnostics/s3_plan_coverage.py`（输出 `data/s3_plan_coverage.json`）。
与 `s3_plan_inventory.py` 的分工：前者统计**调用词表**，本脚本统计**计划步骤完整性**
（`s3_run_plan` 执行的是 `battle_*` 方法序列，所以"能跑"的前提是这些方法的计划完整）。

实测（1374 章）：

| 指标 | 数值 |
| --- | --- |
| 章节总数 | 1374（A 1000 / B 212 / C 162）|
| **计划全完整（现在就能按计划跑）** | **1211 章 = 88.1%**（A 999 + B 212）|
| 计划不完整 | 163 章（≈ 整个 tier C）|
| 其中**图内帧已验证可识别** | **0** |
| 图可识别性**未知**（需进图才知道）| **1211** |

计划不完整的方法里，卡住最多的调用：`battle_default` 109、`clear_siren` 87、
`clear_filter_enemy` 63、`map.select` 48、`clear_any_enemy` 48、`fleet_boss.clear_boss` 44 …

### 结论（决定后续优先级）

1. **计划侧基本就绪**：88% 的章节计划完整，`s3_run_plan` 已能按计划执行（2-1 实测清图）；
2. **真正的规模化瓶颈是"图能不能识别"**：1211 章里**没有一章**有已验证的图内帧，
   而我们唯一验证过的 2-1 恰好是 tier C（不在 ready 集内）；
3. 所以下一步的**最高价值动作**不是继续加计划功能，而是**给 ready 集里的章节批量补"图可识别性"验证**
   —— 每进一张图就是一条记录（`s3_preflight.py` 的夹具表逐条扩充），
   这件事需要出击（耗油），但每张图只需一次、且可复用。

### 各章地图形状离线普查 → 白天批量验证的清单

对 ready 集按"章"分组，导入每章第一关的模块读 `MAP.shape`（输出 `data/s3_chapter_shapes.json`）：

| 章 | 第一关 shape | 格数 | 该章 ready 关数 | 推测 |
| --- | --- | --- | --- | --- |
| **1** | **(6,0) = 7 格单行** | 7 | 3 | ⚠️ 与 1-1 同形 → **大概率不支持** |
| 3 | (6,3) | 28 | 1 | ✅ 多行 |
| 4 | (5,5) | 36 | 1 | ✅ |
| 5 | (7,5) | 48 | 1 | ✅ |
| 6 | (7,4) | 40 | 1 | ✅ |
| 7 | (7,2) | 24 | 2 | ✅ |
| 8 | (8,2) | 27 | 4 | ✅ |
| 9 | (7,4) | 40 | 1 | ✅ |
| 10 | (6,5) | 42 | 1 | ✅ |
| 11 | (7,5) | 48 | 1 | ✅ |
| 13 | (7,5) | 48 | 4 | ✅ |
| 14 | (7,6) | 56 | 4 | ✅ |

（第 2、12 章缺席 = tier C 不在 ready 集 ✓ 与统计一致。）

**口径修正**：第 1 章**整章**都是 7 格单行图，与 1-1 同族，应整体预期不支持；
此前 `s3_plan_coverage.py` 只把 `1-1` 标为 known_unsupported，**范围偏窄**（1-2/1-3 被算作 ready）。

**白天批量验证清单（可直接执行）**：章 3、4、5、6、7、8、9、10、11、13、14 → 每章验第一关，
共约 11 次出击（≈110 油），每次记录"图能否识别"并追加进 `s3_preflight.py` 的夹具表。
优先从**格数少**的开始（7 章 24 格、8 章 27 格、3 章 28 格），失败代价最低。

## 批量验证的第一次尝试（第 7 章）失败：暴露一条 API 约束 + 一个待查约束

目标：给 ready 集补第一条"图可识别性"记录（选格数最少的第 7 章，24 格）。
一次原子操作（进图 → 识别 → 记录 → 撤退）实测结果：

```
TARGET chapter=campaign_7_1 stage=7-1 (第7章 ready 关数=2)
ENTRANCE stored=None
ENTER_MAP ms=33.6 err=ValueError: not enough values to unpack (expected 4, got 0)
IS_IN_MAP False
FINAL pages=['page_main','page_main_white']      ← 状态干净，且**未耗油**（入口为空，没进图）
```

### 约束 1（已确认，必须写进调用约定）

**`campaign_get_entrance(name)` 依赖实例的当前章节上下文** —— 用 1-1 的实例去探
2-1…7-1，全部返回空 Button（实测 7 个章节全空）。所以三者必须一致：

```
s3_campaign_init(chapter=<同章模块>)  →  campaign_ensure_chapter(<章号>)  →  campaign_get_entrance('<章>-<关>')
```

### 约束 2（待查）：章节切换在 UI 上是否真的生效

第 7 章那次三者一致，仍拿不到入口 —— 可能原因：
(a) **账号未解锁该章**（切章动作不可达）；(b) 章节列表需要**翻页/滚动**而切章没成功；
(c) 该章关卡节点的素材/布局不同。

**下一步**：先做一个**只切章不出击**的探针 —— 对每个章号，切章后**截图确认当前显示的是哪一章**
（用章节标题 OCR 或章节节点素材），从而把"未解锁"与"切章失败"区分开。
这一步不耗油，是批量验证的必要前置。

### ✅ 前置修正 + 账号可达范围查清：**第 1–3 章**（批量验证的真实范围）

上一条"约束 1/2"的结论要修正：探针在 `page_main` 上跑，而 `campaign_ensure_chapter` /
`campaign_get_entrance` 都作用于**战役界面** —— **先导航到 `page_campaign` 是硬前置**
（连已知可用的 2-1 在 page_main 上也返回空 Button，实测）。

修正后重探（零耗油，只切章不出击），`ensure_chapter` 的**耗时本身就是判别信号**：

| 关卡 | ensure_chapter | 入口 | 判定 |
| --- | --- | --- | --- |
| 2-1（对照）| 153.2 ms | stored ✓ | 可达 |
| **3-1** | **1748.4 ms** | **stored ✓** | **可达** |
| 4-1 | 15011.5 ms | None | ✗ 不可达 |
| 5-1 | 17634.7 ms | None | ✗ |
| 6-1 | 20242.1 ms | None | ✗ |
| 7-1 | 21070.2 ms | None | ✗ |

**结论**：本账号主线**只解锁到第 3 章**；不可达章会"尝试 15–21 秒后失败"
（可达章只需 0.15–1.7 秒），这个耗时差可直接当判别器。

=> **批量验证的真实范围 = 第 1–3 章**（此前清单里的 4–14 章在本账号上做不了）：

| 章 | 状态 |
| --- | --- |
| 1 | 7 格单行图 → 已知不支持（整章）|
| 2 | **2-1 已实测：图内可识别 + 完整清图** ✓；2-2/2-3/2-4 待验 |
| 3 | 3-1 入口可达 ✓，**图可识别性待验**（下一条记录就该给它）|

这也解释了上轮第 7 章失败：**账号未解锁**（不是 API 用法问题）。

### 3-1 验证结果：入口通了，但卡在 **`AUTO_SEA`（自律寻敌开关）的 UI 判定**

一次原子操作实测（导航 → 进图 → 识别 → 撤退 → 归位）：

```
ENSURE 131.5 ms   ENTRANCE=ENTRANCE          ← 切章 + 取入口都正常
ENTER_MAP 19828.5 ms  err=GameTooManyClickError: Too many click for a button: AUTO_SEA
IS_IN_MAP False                              ← 未进图（**未耗油**）
AFTER_WITHDRAW page_campaign → 归位 page_main ✓
```

**`AUTO_SEA` = 自律寻敌开关**。我设了 `Campaign_UseAutoSearch=False`，ALAS 便去 UI 上把它**关掉**，
而它判断该开关状态的素材/颜色在本客户端不匹配 → 反复点 → 超限报错。

**这又是一例"上游 × 客户端 UI 兼容问题"**（与 `bar_opened` 亮度判据、0.85 阈值、OS 遮罩同族），
修法也应走垫片：让 `handle_auto_search_setting()` 的**状态判定**适配本客户端
（或让它在读不到状态时**直接跳过**，因为我们的配置本来就要求关掉它）。

**为什么 2-1 能进、3-1 卡住**：这类开关只在**部分地图**上出现（自律寻敌并非所有图都有），
2-1 的进场流程没有它，3-1 有 —— 所以这不是"章节"问题，而是"该图是否带该开关"。

### 批量验证的最终范围与状态（本账号）

| 关卡 | 入口 | 图可识别 | 备注 |
| --- | --- | --- | --- |
| 1-1…1-3 | — | ❌ | 7 格单行图，上游检测器不支持 |
| 2-1 | ✓ | ✅ **已实测** | 且已跑通完整清图 |
| 2-2…2-4 | 待验 | 待验 | 入口应可达（同章）|
| 3-1 | ✓ | ⛔ 卡在 `AUTO_SEA` | 需先修该开关的 UI 判定 |
| 4+ | ❌ | — | 账号未解锁 |

### `AUTO_SEA` 的离线诊断（三个已确认事实 + 一个推断）

| # | 事实 | 证据 |
| --- | --- | --- |
| 1 | `handle_auto_search_setting()` 在 `map_is_auto_search` 为假时**直接 return、不点击** | `module/handler/fast_forward.py:321` 起 |
| 2 | **我的配置读回是对的**：`Campaign_UseAutoSearch=False`、`Campaign_UseClearMode=False` | 实例读回实测 |
| 3 | `map_is_auto_search` **尚未赋值（null）**，它是**懒设置**的 | `fast_forward.py:221/239/241/247` 多处赋值 |
| 4 | `AUTO_SEA` 在 campaign/handler/combat 三个模块里**字面不存在** | 精确 grep 无命中 |

**推断**：报错里的按钮名是**被截断/派生的**（ALAS 部分按钮的 `__str__` 会截尾，
例如 `FleetOperator.__str__` 就是 `str(self._choose)[:-7]`），
真实按钮大概率是 **`AUTO_SEARCH_MAP_OPTION_*`**（出现在 `gems_farming.py` 与自动寻敌流程里）。

=> 所以点击者很可能在**自动寻敌处理路径**上，即运行时 `map_is_auto_search` **最终变成了 True**
（尽管配置是 False）—— 而它由 `handle_clear_mode_config_cover()`（`fast_forward.py:210-250`）
**按多个条件计算**，未必只看 `Campaign_UseAutoSearch`。

**下一步（明确、小）**：读 `fast_forward.py:210-250` 的条件分支，确认哪些条件会把
`map_is_auto_search` 置真；然后二选一：
(a) 设那个条件的配置键；或 (b) 打垫片 —— 在我们的运行里**强制** `map_is_auto_search=False`
（我们的配置本就要求关闭它，强制是安全的），再进图验证 3-1。

### 已修：`AUTO_SEA` 卡点的垫片（离线验证通过，进图验证待下一轮）

**定位修正**：`handle_auto_search` 的定义处是 **`module/handler/fast_forward.py:300`**
（类 `FastForwardHandler`），**不是** `module/handler/auto_search.py`（那里只有
`handle_auto_search_map_option`，第 182 行）—— 我第一次找错了模块。

**垫片 `apply_auto_search_skip_compat()`**（与其它垫片一起在 `s3_campaign_init` 里应用）：

- 按"在 `module.handler.fast_forward` 里找哪个类的 `__dict__` 定义了 `handle_auto_search`"
  来定位目标（**不依赖类名**，避免再次找错）；
- 包装后：`map_is_auto_search` 为假时**立即返回 False、不做任何点击**
  —— 我们的配置本就要求关闭自律寻敌（`Campaign_UseAutoSearch=False`，已读回确认），
  所以这是"按配置办事"，同时绕开了那个在本客户端不可靠的"双重 appear"状态判定。

**离线验证**（无需出击）：

```
PATCHED_CLASSES ['FastForwardHandler']        ← 补丁挂到了正确的类
HANDLE_AUTO_SEARCH -> {"name": "...", "ms": 0.0}   ← 立即返回、零耗时、无异常
```

**待办（下一轮，需要出击）**：重跑 3-1 的原子验证（导航 → 进图 → 识别 → 撤退 → 归位），
确认 `enter_map` 不再撞 `AUTO_SEA`，并记录 3-1 的图可识别性。

### ✅ 3-1 验证通过（垫片实测确认）—— 批量验证拿到第一条新记录

一次原子操作（导航 → 进图 → 识别 → 撤退 → 归位）：

```
ENTRANCE=ENTRANCE
ENTER_MAP 7376.5 ms   err=None             ← ★ AUTO_SEA 垫片生效（上次同一步撞 AUTO_SEA，19828 ms 失败）
IS_IN_MAP True
INMAP_3-1 detected=True grids=28 ships=2   ← ★ 3-1 图可识别
MAP_INIT 2157.4 ms    err=None             ← 上游图内初始化成功
AFTER_WITHDRAW page_campaign → 归位 page_main ✓
```

**三点收获**：

1. **`AUTO_SEA` 垫片实测确认修复**（7.4s 成功 vs 之前 19.8s 超限失败）；
2. **3-1 图可识别：28 格 / 2 船**，已登记进 `s3_preflight.py` 的夹具表
   （`data/fixtures/inmap_3-1.png`）—— 这就是"批量补记录"机制的第一次实际产出；
3. **离线预测方法得到验证**：第 39 轮按 `MAP.shape=(6,3)` 预测 28 格，实测正是 28 格 ✓
   —— 说明"离线普查形状 → 预判可识别性"这条路是可靠的。

**批量验证进度（本账号可达范围 第 1–3 章）**：

| 关卡 | 状态 |
| --- | --- |
| 1-1…1-3 | ❌ 7 格单行图（上游检测器不支持，整章）|
| **2-1** | ✅ 图可识别（24 格）+ 已跑通**完整清图** |
| 2-2…2-4 | 待验（同章入口应可达）|
| **3-1** | ✅ **图可识别（28 格）**，垫片后进图正常 |
| 3-2…3-4 | 待验 |
| 4+ | ❌ 账号未解锁 |

### 批量验证续：3-2 卡在 `GameStuckError`，3-3 拿不到入口

| 关卡 | 结果 | 说明 |
| --- | --- | --- |
| **3-2** | `enter_map` **60183 ms → `GameStuckError: Wait too long`** | 未进图（`IS_IN_MAP=False`），撤退回 `page_campaign` ✓，**未耗油** |
| **3-3** | `campaign_get_entrance('3-3')` **拿不到入口**（stored=None）| 该关卡节点未识别/不存在 |

两次都以撤退收尾、归位 `page_main` ✓。

**3-2 是新的失败模式**（与 3-1 不同：3-1 修好 `AUTO_SEA` 后 7.4s 就进图成功）：
`GameStuckError` = 上游卡在"等待某个界面状态"上直到超时。可能原因（按可能性）：
1. 该关有**舰队/属性限制弹窗**（`FleetPreparation` 的 advice/restriction 分支）；
2. 该关有**每日次数/前置条件**（ALAS 的 `handle_*` 里有对应提示）；
3. 该关的**进场面板素材/布局**与本客户端不同。

**诊断方法（已验证有效，下一轮用）**：**就地观测** —— 让 `enter_map` 自己跑，
旁边每 1.8s 抓帧，看它在 60s 里究竟停在哪个界面。
（这套方法之前定位 `bar_opened` 亮度判据时一轮就拿到根因，见本文档前文。）

**3-3 待查**：确认该关卡在客户端是否存在（章节 3 的关卡数与 IR 是否一致）——
可先用 `s3_plan_coverage` 看 IR 里第 3 章有哪些关卡，再与游戏内实际节点对照。

### 🎯 3-2 `GameStuckError` 的根因（就地观测 + 画面直证）

就地观测（让 `enter_map` 自己跑 + 旁边每 4s 抓帧，66s 时间线）显示：
全程 `pages=[]`（**未建模界面**）、`map/FLEET_PREPARATION` 仅 0.04（**不是舰队浮层**）、
`map/MAP_PREPARATION` 探测无结果 —— 即它既没在准备面板也没在浮层上。

**打开那一帧看画面，答案一目了然**（`data/_o49_05.png`）：

```
信息 INFORMATION
关卡决战中途岛:3-1正在攻略中，
请选择前往继续攻略或撤退
[撤退]   [立即前往]
```

**账号里 3-1 的出击仍处于"攻略中"状态** —— 因为前一轮验证 3-1 时，我点"撤退"只是
**离开地图**，游戏会**保留可续战的关卡状态**（这是游戏机制，不是 bug）。
于是之后每次尝试进**别的**关卡，游戏都弹这个"还有未完出击"的对话框，
而 **ALAS 不认识这个客户端专属弹窗** → 干等 60 秒 → `GameStuckError` ✓✓

**这解释了全部现象**：3-1 当时能进（那时没有未完成出击）；3-2 进不去（3-1 成了未完出击）。

**已处置**：点该弹窗的「撤退」→ 回到 `page_campaign` ✓，3-1 的出击正式结束。

**对批量验证的影响（重要）**：**每次验证完必须真正结束出击** ——
游戏里的"撤退"只是离开地图、保留续战；要真正结束需要在这个弹窗里选「撤退」，
或把地图打完（清图）。所以批量流程应改为：进图 → 识别 → **清图 或 走这个弹窗撤退** → 再进下一关。
否则**第二关必卡**（这正是一路以来的隐形陷阱）。

**待办**：把该弹窗做成客户端适配（上游没有它的素材/处理器）——
最简做法：在我们自己的流程里检测到它就点「撤退」。

### 「正在攻略中」弹窗适配：代码已就位，**离线验证未通过**（如实记录）

新增 op `s3_abort_unfinished`：OCR 弹窗正文 → 命中关键词（`正在攻略中`/`继续攻略`/`请选择前往`）
→ 点「撤退」(479,510，实测有效坐标)。并已接入 `s3_run_plan` 真跑路径（进图前先清未完成出击，
外面包了 try/except，**失败不影响原有行为**）。

**但离线验证没过** —— 卡在 OCR 的 `letter` 参数语义上（两个帧都是同一错误）：

| 传法 | 结果 |
| --- | --- |
| `letter=()`（我原以为=不限字符）| `ValueError: not enough values to unpack (expected 3, got 0)` |
| `letter=list('正在攻略中')`（5 个字符）| `ValueError: too many values to unpack (expected 3, got 5)` |

看起来后端**对每个识别出的字符会解包 3 个值**，空结果或字符集不匹配都会炸 ——
所以"读任意文本"这条路要先摸清 `letter` 的约定（或改用**不依赖 OCR** 的判定：
例如用固定区域的像素/颜色特征识别该弹窗，或直接从**模板素材**做匹配）。

**当前影响**：无（op 未被依赖；`s3_run_plan` 里的调用失败即跳过）。**批量流程的临时对策**：
在每次验证后**走游戏内该弹窗的「撤退」**（或把图打完），即可避免"第二关必卡"。

**下一步（二选一）**：① 摸清 `letter` 约定后启用 OCR 判定；② 改用像素/模板判定（更稳，推荐）。

### ✅ 弹窗适配改走像素判定，**离线验证通过**（放弃 OCR 路线）

上一轮 OCR 路线卡在 `letter` 语义（空结果/字符集不匹配都报 unpack 错误）。改走**像素特征**：
弹窗底部那枚红色「撤退」按钮是最稳的判据。实测（1280x720）：

| 帧 | 「撤退」按钮区域 (420,470)-(560,550) 的红像素占比 |
| --- | --- |
| **弹窗帧** | **0.3271** |
| 普通帧 ×2 | **0.0000** |

阈值取 **0.15**（余量充足）。op `s3_abort_unfinished` 已改为该判定，
**离线三帧验证通过**（1 正 2 负）：

```
VERIFY DIALOG   dialog=True   red_frac=0.3271
VERIFY NORMAL1  dialog=False  red_frac=0.0
VERIFY NORMAL2  dialog=False  red_frac=0.0
```

它已接入 `s3_run_plan` 真跑路径（进图前先清未完成出击）。**待下一轮出击复验**：
在真实"未完成出击"状态下确认它能点掉弹窗，然后批量验证就能连续推进。

### ✅ 3-2 也通过（32 格）—— 根因链完全闭合

```
IS_IN_MAP True
INMAP_3-2 detected=True grids=32 ships=2   ← 3-2 图可识别
MAP_INIT 2992.4 ms err=None
撤退 → page_campaign → 归位 page_main ✓
```

**闭环证据**：3-2 此前连续两轮 `GameStuckError`（60s），而**一旦 3-1 被真正结束**，
3-2 就 **7.7 秒进图成功** ✓ —— 所以那些失败 **100% 是"未完成出击"弹窗造成的**，
与 3-2 这个关卡本身无关。

另外，弹窗 op 的表现也自证正确：本次流程里**没有**弹窗（因为 3-1 已结束），
op 如实报告 `unfinished_dialog=false, red_frac=0.0` ✓（**没有误报**）。

**批量验证进度（更新）**：

| 关卡 | 图可识别 | 备注 |
| --- | --- | --- |
| 2-1 | ✅ 24 格 | 且已跑通完整清图 |
| **3-1** | ✅ **28 格** | AUTO_SEA 垫片后正常 |
| **3-2** | ✅ **32 格** | 前提：前一个未完成出击必须先结束 |
| 1-1…1-3 | ❌ | 7 格单行图（上游不支持）|
| 3-3、3-4、2-2…2-4 | 待验 | |
| 4+ | — | 账号未解锁 |

**流程教训（已固化为批量规则）**：每次出击结束必须**真正结束**（清图 / 走弹窗撤退），
否则**下一次进任何别的关卡都会卡 60s**，且极易被误判成"那一关有问题"——我这轮就差点误判。

### ✅ 2-2 通过（35 格）——批量流程验证为"可重复"

```
ENTRANCE=ENTRANCE | ENTER 7657.0 ms err=None（标准速度 7.7s）
IS_IN_MAP True | INMAP_2-2 **detected=True grids=35 ships=3**
ABORT_NET unfinished_dialog=false / red_frac=0.0   ← 收尾安全网未误报
撤退 → page_campaign → 归位 page_main ✓
```

**意义**：这是第 4 个验证通过的关卡，且流程（进图 → 识别 → 真正结束 → 归位）
**连续两次一次通过**（上一轮 3-2、这一轮 2-2）—— 说明批量方法已经稳定可重复。

**已登记的图内夹具（`s3_preflight.py` → `KNOWN_FIXTURES`）**：

| 关卡 | 实测格数 | 夹具文件 |
| --- | --- | --- |
| 2-1 | 24 | `data/fixtures/map_settled.png` |
| **2-2** | **35** | `data/fixtures/inmap_2-2.png` |
| 3-1 | 28 | `data/fixtures/inmap_3-1.png` |
| 3-2 | 32 | `data/fixtures/inmap_3-2.png` |

**剩余待验（本账号可达范围）**：2-3、2-4、3-3、3-4（每关约 1 分钟、10 油，流程已固化）。

### ✅ 2-2 上跑完整计划成功 —— 第二个被 S3 端到端驱动的关卡

```
COVER 2-2 tier=C ready=False methods=['battle_0','battle_3'] incomplete=['battle_3']
PLAN stage=2-2 tier=C elapsed=118.9s stopped_early=False campaign_end=None
  R1 battle_0 48491.7 ok | R1 battle_3 9570.1 ok | R1 enemies_left=3
  R2 battle_0 41773.2 ok | R2 battle_3 9704.3 ok
AFTER pages=['page_campaign']    ← 跑完时游戏已自己回到战役页（**无需人工撤退**）
归位 page_main ✓
```

**要点**：

1. **又 4 次真实战斗经上游代码执行成功**（`battle_0`/`battle_3` 各两轮，累计 9 次以上），
   `stopped_early=False`（无一步出错）；
2. **收尾无需干预**：跑完时游戏已回到 `page_campaign` —— 说明出击被正常结束
   （这也再次印证了"真正结束出击"是批量可行的前提，而计划跑完本身就会结束它）；
3. **`enemies_left` 依旧不可靠**（一直报 3），再次印证既定结论：
   **完成信号要用上游语义**（`CampaignEnd` / `map_clear_percentage`），不要用我的本地标志计数；
4. 本轮 `max_rounds=2` 封顶（未设更大），所以 `campaign_end=None` 是"到轮数上限"而不是失败。

**批量跑计划进度**：**2-1 ✅（CampaignEnd 清图）**、**2-2 ✅（2 轮 4 步，游戏自行结束）**。

### ✅ 3-1 端到端跑通 + **新完成信号实测正确**

```
PLAN stage=3-1 elapsed=130.8s stopped_early=False（stop_reason=None：由 max_rounds 封顶结束）
  R1 battle_0 57109.4 ok | R1 battle_3 7996.7 ok | R1 **sortie_state = still_in_map**
  R2 battle_0 43683.4 ok | R2 battle_3 10460.0 ok
AFTER pages=['page_campaign']    ← 跑完时游戏已自行回到战役页（出击已结束）
归位 page_main ✓
```

**三点**：

1. **新的完成信号（上游语义）实测正确**：第一轮后如实报 `still_in_map` → 继续第二轮；
   不再出现那个不可靠的 `enemies_left` 数字（它此前一直误报 2/3）；
2. **第 3 个端到端驱动的关卡**：2-1 ✅（CampaignEnd 清图）、2-2 ✅、**3-1 ✅**；
3. `stop_reason=None` 是**如实**的 —— 本轮是被 `max_rounds=2` 封顶结束，而不是状态判定结束
   （若要看到 `left_map` / `map_clear_100`，把 `max_rounds` 放宽即可）。

**S3 累计实战数据**：`s3_run_plan` 已在 **3 个关卡**上执行，共 **13 次真实战斗**
（2-1 五次 + 2-2 四次 + 3-1 四次），**全部 `err=None`**。

### ✅✅ 自愈逻辑实测通过（并抓出/修掉一个我自己的 bug）

第一次实测自愈时**失败了**，原因是我的 op 读的是**宿主缓存的帧**（`_state['image']`）——
那还是进图**之前**那一帧，所以永远看不到当下的弹窗：

```
enter_map 60154.7 ms err=GameStuckError
enter_map_abort  dialog=False red=0.0        ← 弹窗就在屏幕上，却说没有
enter_map_retry  60092.1 ms err=GameStuckError
```

修法：op 里**现抓一帧**（`_device_engine().screenshot()`）再做像素判定。修后重测：

```
ensure_chapter    137.4 ms  ok
abort_unfinished  dialog=False red=None       ← 进图前：确实无弹窗（正确）
enter_map         60188.0 ms err=GameStuckError   ← 卡在弹窗上
enter_map_abort   dialog=True  red=0.3271     ← ★ 正确识别
enter_map_retry   **7925.7 ms ok**            ← ★★ 点掉弹窗后重试成功进图
map_init          2944.6 ms  ok
battle_0          54162.2 ms ok               ← 3-2 的两步战斗也跑完
battle_3          10862.6 ms ok
AFTER page_campaign ✓
```

**意义**：那个耗了约 10 轮才定位的"隐形陷阱"（未完成出击弹窗），现在**执行器自动处理** ——
不再依赖"人记住每次出击必须真正结束"这种手工纪律。**3-2 也因此成为第 4 个端到端驱动的关卡。**

**方法论又一次生效**：实测失败 → **看证据**（谁读的哪一帧）→ 找到"读缓存 vs 现抓"的差别 → 修 → 重测通过。

### ✅ 2-3 一次跑通（第 5 个端到端驱动的关卡）—— 流水线已稳定

```
PLAN stage=2-3 elapsed=69.3s stopped_early=False
  ensure_chapter   1932.9 ms  ok
  abort_unfinished dialog=False     ← 无弹窗（自愈未被触发 = 正常路径）
  enter_map        7634.3 ms  ok    ← 标准速度，无卡顿
  map_init         2164.1 ms  ok    ← 图内初始化成功（即 2-3 的图可识别）
  battle_0         52872.9 ms ok
  battle_3          4348.6 ms ok
AFTER page_campaign → 归位 page_main ✓
```

**这是一次"平淡的成功"** —— 无异常分支、无需人工干预、一次跑完。
**正是流水线已经稳定的标志**（对比前面那些要就地观测、要垫片、要自愈的轮次）。

**S3 累计**：**5 个关卡**端到端驱动（2-1 / 2-2 / 2-3 / 3-1 / 3-2），
真实战斗次数 **17 次以上**，全部 `err=None`；已验证可识别的图 **5 张**
（2-1/2-2/2-3/3-1/3-2，其中 2-3 由 `map_init` 成功间接确认）。


### 2-4 跑通（第 6 个端到端驱动的关卡）

本关实测：PLAN stage=2-4 elapsed=62.1s stopped_early=False ; ensure_chapter         ms=156.4     ok ; abort_unfinished       ms=None      dialog=False ; get_entrance           ms=0.0       ok ; enter_map              ms=15590.3   ok ; map_init               ms=1373.8    ok ; battle_0               ms=40374.9   ok ; battle_3               ms=4274.2    ok ; AFTER pages=['page_campaign']

无异常分支、一次跑完（与 2-3 一样属"平淡的成功"）。
S3 累计：**6 个关卡**端到端驱动（2-1/2-2/2-3/2-4/3-1/3-2），真实战斗 20+ 次全部 err=None。

### 🎯 重要修正：第 1 章**并非整章不支持** —— 只有 1-1 是单行小图

离线逐关查形状（此前我只查了每章**第一关**就下了"整章"结论 ✗）：

| 关卡 | MAP.shape | 格数 | tier | 计划 | 判定 |
| --- | --- | --- | --- | --- | --- |
| 1-1 | (6,0) | 7 | A | 完整 | ❌ 单行小图（上游检测器失效）|
| **1-2** | **(4,2)** | **15** | B | **完整** | ✅ 多行图，**待验** |
| **1-3** | **(5,2)** | **18** | B | **完整** | ✅ 多行图，**待验** |
| **1-4** | **(6,2)** | **21** | A | **完整** | ✅ 多行图，**待验** |
| 3-5 | （模块名不同，待查）| — | A | 完整 | 待查 |

**这两条修正都重要**：

1. **1-2/1-3/1-4 是比第 2/3 章更好的批量目标** —— 它们**计划完整**（tier B/B/A），
   而第 2/3 章**全是 tier C**（计划不完整）；
2. **第 3 章的关卡数比我以为的多**：IR 里有 3-1…3-5（上轮 3-3 取不到入口 ≠ 关卡不存在，
   更可能是**账号未解锁到 3-3**，或该节点素材未识别）。

`tools/diagnostics/s3_preflight.py` 里的"整章不支持"已撤掉，只保留 1-1。

**教训（本会话第三次同类）**：**不要用一个样本推断整类** —— 我查了每章第一关的形状，
就写了"第 1 章整章不支持"，而实际只有 1-1 如此。


### 1-2 实测（首个 tier B 关卡）

本关实测：PLAN stage=1-2 tier=B elapsed=15.1s stopped_early=False ; ensure_chapter         ms=1914.2    ok ; abort_unfinished       ms=None      dialog=False ; get_entrance           ms=0.0       ok ; enter_map              ms=7596.5    ok ; map_init               ms=5238.0    err=MapDetectionError: Vanish point and distant ; battle_0               ms=None      ok ; AFTER pages=['page_campaign']

意义：此前驱动成功的 6 关（2-1…2-4 / 3-1 / 3-2）**全是 tier C**；
1-2 是**计划完整（tier B）**的关卡里第一个被实测的 —— 若通过，说明
"计划完整 + 多行图"这一批（1-2/1-3/1-4 等）是更顺的批量目标。

**结论（1-2 也不可用）**：进图正常（7.6s）但**图内识别失败**：
`MapDetectionError: Vanish point and distant …`（消失点几何退化）。

**这是与 1-1 不同的失败模式**：
- 1-1（7 格单行）：`No vertical line detected`（竖线检不出）；
- 1-2（15 格，shape (4,2)）：`Vanish point ...`（**消失点几何退化**）。

=> 说明上游检测器的几何敏感度**比"单行小图"更广** —— 不止单行会挂。
但**参考已成功的关卡**：2-1(24 格) / 2-2(35) / 2-3 / 2-4 / 3-1(28) / 3-2(32) 都在 24–35 格区间且都成功，
而失败的 1-1(7) / 1-2(15) 都更小 —— **"格数偏小/比例特殊"很可能是共同因素**，
但这只是**相关性**（样本各 2–6 个），不能当判据用。

**批量候选更新**：1-3(18 格) / 1-4(21 格) 仍**待验**（比 1-2 大、比 2-1 小，处在灰区）；
第 3 章的 3-3/3-4/3-5 亦待验。


### 1-4 实测（21 格，灰区关键点）

本关实测：PLAN stage=1-4 tier=A elapsed=13.1s stopped_early=False ; ensure_chapter         ms=155.5     ok ; abort_unfinished       ms=None      ok ; get_entrance           ms=0.0       ok ; enter_map              ms=7513.6    ok ; map_init               ms=5078.2    err=MapDetectionError: Vanish point and distant point ; battle_0               ms=None      ok ; AFTER pages=['page_campaign']

灰区判读：成功集最小为 2-1 的 24 格；失败集为 1-1(7)/1-2(15)。1-4 的 21 格正好夹在中间 ——
它的结果决定"阈值大概卡在哪"（若成功 => 阈值约 21–24；若失败 => 小图问题延伸到 21）。

### 📊 系统性规律：**格数阈值约在 21–24 之间**（第 1 章小图全挂）

| 格数 | 关卡 | 结果 | 上游报错 |
| --- | --- | --- | --- |
| 7 | 1-1 | ❌ | `No vertical line detected` |
| 15 | 1-2 | ❌ | `Vanish point and distant point` |
| **21** | **1-4** | ❌ | `Vanish point and distant point` |
| 24 | 2-1 | ✅ | — |
| 28 | 3-1 | ✅ | — |
| 32 | 3-2 | ✅ | — |
| 35 | 2-2 | ✅ | — |

（2-3、2-4 亦 ✅，未列入上表以免混淆格数未记录的情况。）

**判读**：6 个成功样本全 ≥24 格，3 个失败样本全 ≤21 格 ——
**阈值的估计落在 21–24 格之间**。更准确的说法是：**第 1 章这类"小图"在本客户端上
上游检测器一律失效**（失败原因有两种，都与几何/消失点有关）。

**仍然是相关性而非判据**（样本各 3–6 个，且格数不是唯一变量 —— 地图比例、相机位、
渲染缩放都可能参与）。**1-3（18 格）预测也不支持**，但**尚未实测**，故在
`s3_preflight.py` 里只标注为"预测"，不写成实测结论（遵循"不用一个样本推断整类"的教训）。

**批量范围的最终结论（本账号）**：

| 范围 | 状态 |
| --- | --- |
| 第 1 章（1-1…1-4）| ❌ 小图，上游检测器失效（1-1/1-2/1-4 实测，1-3 预测）|
| 第 2 章（2-1…2-4）| ✅ **4/4 端到端跑通** |
| 第 3 章（3-1…3-5）| 3-1 ✅ / 3-2 ✅；3-3/3-4/3-5 待验（3-5 为 tier A ready）|


### 3-3 实测（复测，此前取入口失败过一次）

本关实测：PLAN stage=3-3 tier=C elapsed=2.5s stopped_early=False ; ensure_chapter         ms=2148.6    ok ; abort_unfinished       ms=None      ok ; get_entrance           ms=1.1       err=CampaignNameError: ; battle_0               ms=None      ok ; AFTER pages=['page_campaign']

意义：第 3 章剩余关卡（3-3/3-4/3-5）的第一个数据点。
