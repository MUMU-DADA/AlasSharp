# S3 入口序列：实测记录与卡点诊断

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
