# S2 地图识别适配与验收

S2 的图像算法全在上游（`module/map_detection`、`module/os/globe_detection`），
本项目**不复制任何算法**，只通过识图协议驱动并核对结果。

脚本：`tools/diagnostics/verify_map_detection.py`；数据：`data/map_detection_verify.json`。

## 结果

| 项 | 结果 | 说明 |
| --- | --- | --- |
| 素材链 | ✅ | UI 遮罩 / 瓦片模板 / 检测区域由上游读出 |
| 大世界单应性 | ✅ | `GlobeDetection.load` 成功并给出单应矩阵 |
| 坐标往返自检 | ✅ | screen→globe→screen 误差 < 1e-6（同一变换的逆）
| 非地图负样本 | ✅ | 非地图画面返回"未检测到 + 原因"，不崩 |
| 地图正样本 | ✅ 9 张检测到网格 | 需要真机地图画面（见下） |
| 检测 vs 关卡 IR | ✅ 3 张一致 | 检出的格数/形状必须与该关卡声明的 map_data 一致 |

## 素材链明细

| 素材 | 形状 |
| --- | --- |
| `detecting_area` | [123, 55, 1280, 720] |
| `ui_mask` | [665, 1157] |
| `ui_mask_os` | [665, 1157] |
| `ui_mask_stroke` | [665, 1157] |
| `ui_mask_in_map` | [720, 1280] |
| `ui_mask_os_in_map` | [720, 1280] |
| `tile_center_image` | [46, 46] |
| `tile_corner_image` | [19, 19] |

## 大世界识别明细

```json
{
  "load": "ok",
  "center_loca": [
    2075.0,
    414.0
  ],
  "log_lines": [
    "[homo_storage] ((4, 3), [(np.int64(445), np.int64(180)), (np.int64(879), np.int64(180)), (np.int64(376), np.int64(497)), (np.int64(963), np.int64(497))])",
    "globe_center: (np.float64(2075.0), np.float64(414.0))",
    "0.097s      similarity: 0.093",
    "Low similarity when matching OS globe"
  ],
  "similarity": 0.093,
  "homo_size": [
    1032,
    1008
  ],
  "homo_data": [
    [
      1.6132608890533437,
      0.71821534898546,
      -517.1150512695312
    ],
    [
      -7.105427357600995e-16,
      2.8012445253396803,
      0.0
    ],
    [
      -7.049809592199102e-19,
      0.0013904287585561408,
      1.0
    ]
  ],
  "screen2globe": [
    [
      515.7623526070334,
      672.0503173058864
    ],
    [
      -39.76243659216754,
      438.35001236639954
    ]
  ],
  "globe2screen": [
    [
      639.9999999999998,
      359.99999999999994
    ],
    [
      199.99999999999994,
      199.99999999999991
    ]
  ],
  "fixture": "_probe_now.png"
}
```

## 逐张 fixture

| fixture | globe | 往返误差 | map detected | 原因 |
| --- | --- | --- | --- | --- |
| `_probe_now.png` | — | — | True |  |
| `_probe_r17.png` | — | — | True |  |
| `inmap_2-2.png` | — | — | True |  |
| `inmap_3-1.png` | — | — | True |  |
| `inmap_3-2.png` | — | — | True |  |
| `inmap_7-1.png` | — | — | False | error: OpenCV(5.0.0) D:\a\opencv-python\opencv-python\opencv\modules\core\src\al |
| `map_2_1.png` | — | — | False | No vertical line detected |
| `map_event.png` | — | — | True |  |
| `map_hard_1_4.png` | — | — | True |  |
| `map_settled.png` | — | — | True |  |
| `map_shape_9x6.png` | — | — | True |  |
| `menu_01.png` | — | — | False | fallback 网格不干净（51 格 vs 8x7=56），判为非地图画面 |
| `menu_02.png` | — | — | False | 检出网格但**没有任何船标志**（14 格），判为非战场画面 |
| `menu_03.png` | — | — | False | fallback 网格不干净（37 格 vs 8x5=40），判为非地图画面 |
| `menu_04.png` | — | — | False | 检出网格但**没有任何船标志**（14 格），判为非战场画面 |
| `menu_05.png` | — | — | False | fallback 网格不干净（37 格 vs 8x5=40），判为非地图画面 |
| `menu_06.png` | — | — | False | fallback 网格不干净（37 格 vs 8x5=40），判为非地图画面 |
| `menu_07.png` | — | — | False | 检出网格但**没有任何船标志**（14 格），判为非战场画面 |
| `menu_08.png` | — | — | False | 检出网格但**没有任何船标志**（14 格），判为非战场画面 |
| `menu_09.png` | — | — | False | fallback 网格不干净（37 格 vs 8x5=40），判为非地图画面 |
| `menu_10.png` | — | — | False | fallback 网格不干净（37 格 vs 8x5=40），判为非地图画面 |
| `os_globe_live.png` | — | — | False | Failed to find a free tile |
| `os_globe_view.png` | — | — | False | No vertical line detected |
| `os_live_2.png` | — | — | False | fallback 网格不干净（56 格 vs 9x7=63），判为非地图画面 |
| `os_map.png` | — | — | False | fallback 网格不干净（59 格 vs 8x8=64），判为非地图画面 |
| `subchapter_1_1.png` | — | — | False | No vertical line detected |

## 正样本从哪来（这是完成 S2 验收的唯一缺口）

- **战役地图**：要在地图上，也就是**真的出击**（消耗石油、会打一场）。
- **大世界地图**：免费进入，但本账号 `大型作战` 卡片是**锁**的状态（未解锁）。

把游戏停在地图画面上之后：

```powershell
python tools/diagnostics/verify_map_detection.py --capture map_1_1   # 抓正样本
python tools/diagnostics/verify_map_detection.py                     # 重新验收
```

### 实测澄清：正样本到底能验到什么

试过把上游自己的 `os_globe_map.png` 当输入图喂进 `globe_detect`，结果给出的是**同一组
单应矩阵**（`homo[0][0]=1.6133`、`homo[1][1]=2.8012`、`homo[0][2]=-517.12`，与喂 1280x720
的游戏截图完全相同）—— 说明 **OS globe 的单应性是存好的常量**
（上游日志里的 `[homo_storage] ((4,3), [(445,180),(879,180),(376,497),(963,497)])`），
不是从截图里算出来的。

所以"缺正样本"缺的不是代码通路，而是**一张真实的地图画面**：

| 要验的 | 需要什么 |
| --- | --- |
| 单应矩阵 / 坐标往返 | ✅ 已验（常量 + 逆变换自检，误差 2.84e-13） |
| `perspective_transform` 扭到 globe 坐标系 | ✅ 任意图都能跑（只验形状与不崩） |
| **当前所在位置**（`find_peaks` / screen2globe 的实际落点） | ⏳ 必须真机 OS 地图截图 |
| 战役地图的网格识别（`View.load` 正路径） | ⏳ 已抓到真机地图，但**检测失败**（见下节） |

## 真机战斗地图正样本：**已检测成功**（结论在最后，前面是完整排查过程）

> **结论**：正样本 `map_settled.png` 检出 **24 格、shape [5,3]（即 6×4）**，
> 与画面上的 A–F 列 × 1–4 行完全一致。真正的卡点是**上游与 numpy 2 不兼容**：
> `Lines.cross` 里写的是 `np.vstack(self.cross_two_lines(...))`，而 `cross_two_lines`
> 是**生成器**，numpy 2 不再接受（实测 numpy 2.4.6 报
> `TypeError: arrays to stack must be passed as a "sequence" type`）。
> 本环境是 Python 3.14，只能配 numpy 2，所以地图识别会卡在这一步 ——
> **表象极像"识别不到地图"，实际与客户端 UI 毫无关系**。
> 修法是 `apply_numpy2_compat()`：只把生成器具体化成 list，
> **上游代码一行不改**，检测算法仍全部是上游的。
> 另外 `map_2_1.png`（我第一张样本）是**入场动画期间**抓的脏样本，
> 垫片后仍失败属正常，保留它作为"取样时机很重要"的证据。

以下是完整排查过程（含两次被推翻的判断），保留下来是因为每一步都有可复现的数字。

正样本 `data/fixtures/map_2_1.png` 是真实战斗地图（网格 A-F × 1-4，即 shape `F4`，含 Lv.11 敌舰）。
`map_detect` 在它上面失败：homography 与 perspective **两个后端**都报
`No vertical line detected`（先前报过 `No horizontal line detected`）。

用 `map_detect_trace` 把上游 `Perspective.load` 的链条逐步跑出来（同一张 fixture）：

| 阶段 | peaks | 遮罩后 | HoughLines 原始 | 最终 lines | 角度(度) |
| --- | --- | --- | --- | --- | --- |
| inner_h（内部网格横线） | 221 | 221 | **0** | **0** | — |
| inner_v（内部网格竖线） | 88 | 88 | **0** | **0** | — |
| edge_h（地图边缘横线） | 1722 | 706 | 8 | 5 | 90–93 |
| edge_v（地图边缘竖线） | 464 | 249 | 1 | 1 | 0 |

**结论（顺手排除了两种常见猜测）**：

- **不是 UI 遮罩对不上**：`mask_stroke` 保留 86.4% 像素，且 `peaks_after_mask` 与 `peaks_raw` 完全相等
  （221/221、88/88）—— 峰值一个都没被遮掉；
- **不是预处理把图弄黑了**：`load_image` 输出 665x1157、均值 208.21、99.8% 非零；
- **失效点是内部网格线的检测**：`inner_*` 的峰值图里有 221/88 个峰值，但
  `cv2.HoughLines(threshold=75)` 一条都拟合不出来（`hough_raw=0`）。
  即本客户端网格线的**渲染方式**（对比度/颜色/抗锯齿）与上游 `INTERNAL_LINES_*`
  参数针对的渲染不同；地图**边缘**线还能检出（5 横 1 竖），但竖线只有 1 条，
  在后续 group/delete 里被清掉，于是报 "No vertical line detected"。

下一步查 `Perspective.load_image` 的预处理（按颜色/亮度挑线）与
`INTERNAL_LINES_FIND_PEAKS_PARAMETERS` / `INTERNAL_LINES_HOUGHLINES_THRESHOLD`，
判断是本客户端网格线颜色不同，还是阈值要按渲染调整。**全程可离线复跑**，不必再动账号。

### 参数扫描：不是"调个阈值"就能修好（实测）

先扫 Hough 阈值（`INTERNAL_/EDGE_LINES_HOUGHLINES_THRESHOLD` 同时降）：

| 阈值 | 75 | 50 | 40 | 30 | 25 | 20 | 15 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 结果 | 竖线缺失 | 竖线缺失 | 竖线缺失 | 竖线缺失 | 竖线缺失 | 后续阶段空数组 | 后续阶段空数组 |

再扫 `INTERNAL_LINES_FIND_PEAKS_PARAMETERS` 的亮度下限（`height[floor,222]`，floor 150 是上游默认），阈值取 40/25：

| floor | 150 | 120 | 100 | 80 | 60 |
| --- | --- | --- | --- | --- | --- |
| 结果 | 竖线缺失 | 竖线缺失 | 竖线缺失 | 竖线缺失 | 竖线缺失 |

单看原始 Hough 输出也能看出症结：`inner_v` 即使把阈值降到 **10** 也只有 **1** 条线，
而 `inner_h` 在同样阈值下有 59 条 —— **本客户端地图上的竖线（网格纵向分隔）
在 `load_image` 取反后的图里几乎不成线**。

所以这不是调参能解决的：要么本客户端网格纵向分隔的**渲染**与上游预期差异过大
（例如分隔是浅色而非深色、或抗锯齿跨越多个像素导致 1-D 峰值不成立），
要么该 fixture 的取样时机不对（进图立刻抓的，地图可能还在入场动画/未完全渲染）。
### 根因收敛：本客户端地图的**渲染亮度/极性**与上游假定不符

直接量像素（`rgb2gray` 后）：地图区域内**海面中位数 ≈42**、**网格线 ≈63** —— 本客户端是"**暗海 + 略亮的网格线**"，而且整片地图很暗。

而上游 `Perspective.load_image` 的做法是 `灰度 → 遮罩 → 取反`，隐含假定"线比周围**暗**"。取反后：海 →213、线 →192，于是 `find_peaks`（找**亮**峰，且要求亮度落在 `height: [150, 222]`）把**海面方块中心**当成了峰（间距 ≈113 正好等于网格列距），这些峰彼此不共线，Hough 自然一条线也拟合不出来。

两个对照实验都指向同一结论：

| 实验 | 结果 |
| --- | --- |
| 跳过我方取反（直接用灰度图） | 仍失败：线的亮度只有 63，**低于 `height` 下限 150** |
| 自适应映射（把线/海映射到 215/185 再交给上游取反） | 失败模式从「缺竖线」变成「缺横线」—— **说明输入图的亮度映射确实决定检测结果**，只是我随手给的映射还没调对 |

所以修法是**给本客户端一套输入映射（客户端 profile）**：让线在交给上游前就落在它期望的亮度区间、且极性与算法一致；这属于"适配输入数据"，不是重写算法（架构铁律 2 仍然成立：
检测算法依旧全部调用上游 `Perspective`/`View`）。

### 正确极性 + 已越过的两道关（第 10 轮）

**先纠正上一轮我自己的方向性错误**：上游 `find_peaks` 找的是"取反后的**亮**峰"＝**原始灰度里最暗**的像素。所以交给上游的图里，**线必须是最暗的**（取反后才最亮）；
上一轮我把线映射成 215（最亮），极性和上游正好相反，所以那个实验的失败模式变化不具有解释力。

按正确极性（线→暗 40、海→亮 180，取反后线＝215 落在上游 `height[150,222]` 内）重扫：

| 映射（线/海） | 取反后线亮度 | 结果 |
| --- | --- | --- |
| 40 / 180 | 215 | **越过"找不到线"** → 失败点后移 |
| 60 / 200 | 195 | 同上 |
| 80 / 200 | 175 | 同上 |
| 40 / 220 | 215 | 同上 |
| 70 / 240 | 185 | 同上 |

五个组合全部**越过了原来的 `No ... line detected`**（不再是那个失败），改为在后续阶段报 `TypeError`。抓完整回溯定位到：

```
perspective.py: self.crossings = self.horizontal.cross(self.vertical)
utils.py:203  : points = np.vstack(self.cross_two_lines(self, other))
TypeError: arrays to stack must be passed as a "sequence" type ...
```

即：**横线集与竖线集求不出有效交点**（`cross_two_lines` 返回空）。
这说明极性修正之后线是检出了，但检出的横竖线还不构成合理的网格结构 ——
下一步要量这两组线的几何（条数、角度、ρ 分布），判断是线检多了/检少了，
还是角度阈值（`HORIZONTAL/VERTICAL_LINES_THETA_THRESHOLD`）把该留的线滤掉了。

### 关键修正：第一张 fixture 是**入场动画期间**抓的（用户点进地图后重测）

用户手动把游戏点进战役地图、画面停稳后，我抓了第二张样本 `data/fixtures/map_settled.png`，
用**上游原版参数**（没有任何映射改造）重跑，结果与第一张判若两图：

| 阶段 | 动画期样本 | **停稳样本** | 停稳后 lines |
| --- | --- | --- | --- |
| inner_h | peaks 221 | peaks **2483** | **4** |
| inner_v | peaks 88 | peaks **1786** | **3** |
| edge_h | peaks 1722 | peaks 2746 | 6 |
| edge_v | peaks 464 | peaks 1384 | 5 |

即：**"竖线几乎不存在"是取样时机造成的**（地图还没渲染完），不是渲染差异；
前一节关于"暗海/亮网格线、亮度映射"的分析因此**只对那张动画期样本成立**，
不能当作本客户端渲染的结论。这条修正很重要 —— 差一点就去调一套本来不该改的映射。

停稳样本上，失败点后移到**交点求解**（与离线实验里定位到的同一处）：

```
perspective.py: self.crossings = self.horizontal.cross(self.vertical)
utils.py:203  : points = np.vstack(self.cross_two_lines(self, other))
TypeError: arrays to stack must be passed as a "sequence" type ...
```

横线 4 条、竖线 3 条都检出了，但两组线**求不出有效交点**（`cross_two_lines` 返回空）。
这正是下一步要看的地方：是交点落到了 `DETECTING_AREA` 之外被过滤、
还是两组线的角度/ρ 使交点判定失败。**完全离线可复跑**（样本已存）。

## 大世界（OS）通路：**已可进入**（用户解锁后实测）

之前「大型作战」卡片是锁的（点 8 次无反应，但按钮分数 0.9990）。用户解锁后实测通过：

```
[hop 1   ] page_main -> page_campaign_menu   ui_white/MAIN_GOTO_CAMPAIGN_WHITE  score 0.9447
[hop 2   ] page_campaign_menu -> page_os     ui/CAMPAIGN_MENU_GOTO_OS           score 0.9990
[result  ] success=True final=page_os
```

真机 OS 画面已存为 fixture `os_globe_live.png`，`globe_detect` 在其上：
`load=ok`、`homo_size=[1032,1008]`，单应矩阵与在别的画面上**完全相同**
（1.6133 / 2.8012 / -517.12），坐标往返仍精确回到原点。

这再次印证了前面那条澄清：**OS globe 的单应性是存好的常量**，不是从截图算出来的。
因此"位置检测"（`find_peaks` 的实际落点）仍未被验证 —— 它需要调用上游的位置检测接口
（`GlobeDetection` / `OSMap` 上的相关方法），是下一步要补的 op。

### 逐格语义校验：船不可能落在陆地格上（比"格数对得上"更强）

`map_detect` 现在还返回逐格标志（`grid_flags`，只回 True 的那些），
即"敌人在哪一格、己方舰队在哪一格、哪格是潜艇/神秘事件"。
据此可以对关卡 IR 做一条**可判定的不变量校验**：
船（己方/敌方/BOSS/塞壬/潜艇）**不可能落在陆地格 `++` 上** ——
识别坐标只要差一格，船就会落到陆地上，这条立刻被违反。

实测（两张真机图，同时校验 shape 一致、缺格坐标、陆地对齐）：

| fixture | IR 陆地格数 | 检出的船格 | 落在陆地上 |
| --- | --- | --- | --- |
| `map_settled.png`（2-1） | 7 | (4,0)敌 (0,1)己方 (5,2)敌 | **0** ✅ |
| `map_shape_9x6.png`（10-4） | 13 | (6,1)(3,2)(3,4)(4,5)敌 (6,4)潜艇 | **0** ✅ |

另外 2-1 上 `(4,0)` 落在 IR 的 `ME`（可能有敌人）上、`(0,1)` 落在出生点上 ——
识别语义与声明式地图在**具体格子**这一级也对得上。

### 海域（OS）内地图：已推进到"锚定网格原点"这一步（比之前进了一步）

用户进入海域后抓了 os_live_2.png（标题「陆间海C-安全海域」，完整网格 + 舰队 + 迷雾雷达）。
先确认了它**不是**环球视图（os/MAP_GOTO_GLOBE_FOG 命中 0.8986，按上游命名这是海域图上"返回环球"的按钮）。

关键判断：海域里的网格地图要用 View(config, mode='os')（会切到 ASSETS.ui_mask_os_in_map，见 view.py:47-48），而 GlobeDetection 是给**环球视图**用的—— 这也解释了为什么它在海域图上 similarity 只有 0.082：拿错了检测器。

补上 mode 参数后（map_detect(mode="os")，网格类用上游的 OSGrid）：

| 画面 | mode=main | mode=os |
| --- | --- | --- |
| os_live_2.png（海域内） | 失败 | **Failed to find a free tile** |
| map_settled.png（2-1） | 24 格 [5,3] | 24 格 [5,3]（OS 模式不影响战役图）|

也就是说：线找到了、网格建起来了，卡在**用"自由格"锚定网格原点**这一步。
上游这一步靠模板匹配找一块"空地格"来确定地图偏移；本客户端海域地图的格子渲染（浅蓝底 + 细亮格线）可能与它预期的模板不同，导致找不到锚点。
下一步：对照 	ile_center_image / 	ile_corner_image 与海域格子的实际外观，
看是模板不匹配还是锚点搜索区间的问题。

#### 继续往下：自由格搜索为何全败（离线逐步对照）

HOMO_STORAGE = None 说明这个单应性是**从图里现算**的（先找地图四角）。
把两条链在同一套上游代码下并排跑（战役图 vs 海域图）：

| | 战役 2-1（成功） | 海域（失败） |
| --- | --- | --- |
| 检出地图四角 | (351.8,116.6)(1033.9,116.6)(278.9,559.8)(1133.2,559.8) | (165.4,122.9)(1291.8,122.9)**(13.6,701.6)(1500.2,701.6)** |
| warp | (1477,1015) mean 86.4，边缘 6.63% | (1495,996) mean 115.3，边缘 10.48% |
| search_tile_center | **True** | **False** |
| search_tile_corner | True | **False** |
| search_tile_rectangle | True | **False** |

海域图检出的**底部两角 x=13.6 / 1500.2，已超出屏幕宽度 1280**（角点是无限延长线的交点，
可以落在屏幕外）—— 地图角点不可能在屏幕外，说明**边线检测把屏幕底部的 UI 边界
当成了地图下边**，算出的单应性把画面扭到错误坐标系，三种自由格模板自然全找不到。
warp 后海域的边缘密度(10.48%)反而高于战役(6.63%)，也符合"拟合进了 UI 边界"的判断。

下一步（离线）：查 Homography.detect 用的遮罩 ui_mask_homo_stroke 是否随 mode=os 切换
（View.load_image 是按 mode 选的 ui_mask_os_in_map）；若 homography 这条路用的是
战役遮罩，海域画面上就会遮错区域、让 UI 边界参与拟合。

#### 遮罩这条线查清了：接线没错，问题在客户端一侧

上游 OS 任务的真实用法（module/os/camera.py:25）就是：
View(config, mode="os", grid_class=OSGrid)，并且真机上 Scheduler_Command 以 Opsi 开头
—— 与我们的调用方式**完全一致**，所以接线不是问题。

遮罩规则也查清了（两处、不对称）：

| 位置 | 用哪个遮罩 | 是否随 OS 切换 |
| --- | --- | --- |
| perspective.py:172（找地图四角/边线） | ASSETS.ui_mask | **否，永远用战役遮罩** |
| homography.py:67-72（warp 之后的边缘过滤） | ui_mask_os / ui_mask | 是，按 Scheduler_Command 是否以 Opsi 开头

也就是说：**找四角这一步在 OS 画面上跑的是战役遮罩**。我们客户端 OS 画面底部那条 UI 栏
（第一舰队 / 储物舱 / 情报 / 作战总览）落在战役遮罩的"可见区"里，它的边界被当成地图下边
—— 这与实测吻合：检出的底部两角 y=701.6（贴近屏幕底 720）、x=13.6/1500.2（超出屏宽）。
单应性因此算错，三种自由格模板全败。

上游用户没这个问题，最可能的解释是本客户端的 UI 缩放/布局与上游素材（绝对像素遮罩，
1157x665）对不齐 —— 这与本项目此前在其它界面反复遇到的"新 UI 素材对不上"是同一类原因。

下一步（离线）：把 ui_mask_os 与 ui_mask 叠加到 os_live_2.png 上，量出
"遮罩认为的 UI 区域"与"实际 UI 区域"的差，确认是否底部栏未被覆盖、差多少像素。

#### 修法与结果：两处遮罩都要换（缺一不可）

客户端垫片 pply_os_mask_compat() + set_os_mask_mode()：把 Perspective.load_image
包一层，在 OS 模式下换用 ASSETS.ui_mask_os（上游代码一行不改）。
**但只换这一处不够**：还要让 Scheduler_Command 以 Opsi 开头，
homography.py:67-72 的 warp 后遮罩才会跟着切 —— 两半缺一不可。

实测（同一进程、逐张 fixture）：

| fixture | mode | 结果 |
| --- | --- | --- |
| os_live_2.png（海域内 9x6） | os | **detected=True grids=49 shape=[8,5]** |
| map_settled.png（战役 2-1） | main | detected=True grids=24 shape=[5,3]（未受影响）|
| map_shape_9x6.png（战役 10-4） | main | detected=True grids=48 shape=[8,5]（未受影响）|
| map_settled.png | os | detected=True grids=24 shape=[5,3] |

排错过程中我自己踩了两个坑，都记下来：① 垫片里用了 cv2 而 alas_vision 模块级没有导入它
（NameError 被吞掉，表现为"换了遮罩也没用"）；② 异常分支提前 return 时忘了复位开关，
把开关泄漏给了后续战役检测，导致 2-1 也一起失败 —— 已改为异常路径同样复位。

遗留：海域图的 grid_flags 为 0（没标出船只/敌人）—— OS 的网格类暴露的标志名可能与战役
不同，等 S3 真正需要 OS 的逐格语义时再对齐。

### 后端选择：homography 与 IR 一致，perspective 在 2-1 上会多判一行（实测）

同一张真机图上跑上游两个后端（`map_detect(backend=...)`）：

| fixture | 关卡 IR | homography | perspective |
| --- | --- | --- | --- |
| `map_settled.png`（2-1） | `F4` = [5,3] / 24 格 | **24 格 [5,3]** ✅ 与 IR 一致 | 30 格 **[5,4]** ✗ 多算一行 |
| `map_shape_9x6.png`（10-4） | `I6` = [8,5] / 54 格 | 48 格 [8,5] ✅ | 48 格 [8,5] ✅ |

也就是说 **perspective 后端在 2-1 上会把网格多判一行**（[5,4] 而非 [5,3]），
homography 后端在两张真机图上都与关卡 IR 严格一致。
这是"用哪个后端"的实测依据，而不是偏好 —— 默认走 homography（上游 `DETECTION_BACKEND` 的默认值）。

耗时（同一进程内）：**首次调用约 1.6 s**（冷启动，要加载素材与模型），
之后每张 **120–190 ms** —— 对"截图 p50 ≈ 433–441 ms 才是真瓶颈"的结论是个有用的补充：
识别本身不是瓶颈。


`GlobeDetection.load()` 的落点写进 `center_loca`（大世界坐标），匹配度 `similarity`
只打日志不存属性 —— op 挂 logging handler 把**上游自己打的那行**取回来解析，
没有在 op 里重算匹配（不重复实现算法）。

### 位置检测（ind_peaks + 模板匹配）：机制已通，**等一张海域内画面**

实测（同一套上游代码，逐张 fixture）：

| 画面 | similarity | center_loca |
| --- | --- | --- |
| `os_globe_live.png`（真机 page_os 环球视图） | 0.082 | [1151, 1564] |
| `os_map.png`（战役菜单，非 OS） | 0.128 | [2065, 1768] |
| `map_settled.png`（战斗地图） | 0.085 | [1959, 414] |

真机 OS 环球视图的匹配度**反而最低**，原因在上游实现里很清楚：
`load()` 是拿"**局部地图结构**"（透视变换后 `find_peaks` 出的地图边界）去和 globe 模板
做 `matchTemplate`，所以它需要**进入某个海域后的海域地图画面**；
`page_os` 是选海域的环球视图，上面没有局部地图结构 → 分数低是**画面不对**，不是算法不对。

补充（已更正）：上游**是有判据的** —— `globe_detection.py:133-134` 写了
`if similarity < 0.1:` 则警告 `Low similarity when matching OS globe`
（只警告、不拒绝，仍返回 `center_loca`）。我们测到的所有画面都在 0.066–0.128，
恰好压在这条线上下 —— 与"glob 检测要的是环球视图那一屏"的结论一致。
原先这里写的"上游没有对 similarity 设硬阈值"据此更正：没有硬拒绝，但有 0.1 的警告线。
`center_loca` 被 `module/os/camera.py` 当作相机中心使用。
所以"匹配度多高算认出海域"要**在真机海域画面上实测标定**，不能凭猜写死。
这一步需要用户进入任意海域后抓一张图（免费、不耗油），
预期 similarity 会显著高于上面 0.082–0.128 这一档。

## 环球视图位置检测：**已验证**（S2 最后一块拼图）

用户切到环球视图后实测，三次运行完全一致：

```
globe similarity=0.508   center_loca=[463.0, 904.0]
```

对比此前在错误画面（战役图 / 海域图）上的 0.066-0.128 —— **0.508 远高于上游
`globe_detection.py:133` 的 `if similarity < 0.1` 警告线**，说明匹配可信；
`center_loca` 给出的大世界坐标 (463, 904) 稳定可复现。

也就是说：判定用的是**上游自己的判据**，不是我们拍的门槛。
配套 fixture：`data/fixtures/os_globe_view.png`（不入库，可随时重抓）。

## 海域图逐格语义（grid_flags=0）查到的原因

海域图检出 49 格，但**所有格只有 `is_os=True`**，`predict()` 前后都一样 —— 即逐格语义为空。

机制（先更正我自己一个错判）：预测不是挂在 `view.predictor` 上（实测该属性是 None，
但那不是问题所在），而是**网格类自己的 mixin**：

```
grid.py:  class Grid(GridInfo, GridPredictor)      # 网格对象自己会 predict
view.py:  def predict(self): for grid in self: grid.predict()
```

`OSGridPredictor` 靠**模板匹配**识别目标（`_os_template_enemy = {'Akashi': TEMPLATE_SIREN_Akashi, ...}`），
所以海域图标志为空最可能仍是"**本客户端图标与上游模板不匹配**"这一类原因 ——
与本项目一路遇到的其它界面问题同源。

结论：海域图的**网格检出**已验证（49 格 / 9x6），**逐格语义**留待 S3 真需要时再对齐
（那时可以拿具体格子的截图与上游模板逐一对照，和战役图当年查网格线是同一个套路）。

## 困难图（1-4）：曾未能检出（**已过时**，见文末"复核"一节 —— 加 5 档降阈值重试后已能识别）

用户切到困难图 1-4 后抓到 `data/fixtures/map_hard_1_4.png`。画面特征：可见网格 **B-G × 1-3**、
两艘 Lv.28 敌舰、底部困难图特有的 迎击/撤退/切换 按钮；**最左列 A 被左侧舰队栏挡住**
（属"相机窗口"情形：地图比可见区宽）。

检出：mode=main 与 mode=os **都失败**，reason=`Vanish point and distant point too close`（Perspective.load 的退化几何判断）。

`map_detect_trace` 数字：

| 阶段 | peaks | 遮罩后 | HoughLines 原始 | lines |
| --- | --- | --- | --- | --- |
| inner_h | 2198 | 2198 | 5 | **3** ✅ |
| inner_v | 1461 | 1456 | **0** | **0** ✗ |
| edge_h | 2759 | 1743 | 5 | 5 |
| edge_v | 1241 | 1023 | 5 | 5 |

即困难图上**内部竖线一条都拟合不出**（1461 个峰值 → Hough 0），只剩横线与边缘线，
消失点几何因此退化。

关卡 IR：困难图复用同章节地图数据（`data/campaign/campaign_hard` 下只有 campaign_12_4 / 14_4 / campaign_hard 三个文件），故取普通 `campaign_1_4`：
`shape='G3'` → 7 列 × 3 行 = **21 格**，与画面可见的 B-G × 1-3 吻合（A 列在舰队栏之后）。

与"动画期脏样本"那次表象同类（都是竖线弱到拟合不出），但这次样本是停稳的、numpy 垫片
也已生效 —— 所以更可能是**本客户端困难图竖线的渲染/对比度**问题。
下一步：与战役 2-1（能检出）并排量竖线峰值强度与角度分布，判断是阈值还是渲染差异。

### 继续定位：是**阈值**问题（不是渲染），但不能全局降阈值

把困难图与战役 2-1 并排量竖线（同一套上游代码）：

| | 峰值数 | 峰值亮度 中位/p90 | 峰值最多的列（各列峰值数） | HoughLines(75) |
| --- | --- | --- | --- | --- |
| 2-1（成功） | 1786 | 177 / 218 | 448,450,446,730… **[28,27,25,24…]** | **3 条**（θ=175°/174°/2°）|
| 困难 1-4（失败） | 1461 | 182 / 219 | 597,446,443,579… **[48,24,24,23…]** | **None** |

**亮度几乎一致、峰值也照样成列** —— 差别在角度：2-1 的竖线近乎垂直（票数集中在少数角度桶），
困难图是倾斜的 3D 平面，竖线票数被摊开，达不到阈值 75。所以是**阈值问题，不是渲染问题**。

阈值扫描（困难图）：75→None，60→1 条，50→2 条，40→6 条；整条检测：

```
LOAD thr=75 → FAIL  Vanish point and distant point too close
LOAD thr=50 → OK    grids=21 shape=[6,2]      ← 与 IR 的 G3（7x3=21 格）完全一致 ✅
```

**但阈值不能全局降**（实测）：

| 阈值 | 2-1 | 10-4 | 困难 1-4 |
| --- | --- | --- | --- |
| 75（上游默认） | 24/[5,3] ✅ | 48/[8,5] ✅ | **FAIL** |
| 40 | **39/[7,4] ✗ 过检** | 48/[8,5] ✅ | **21/[6,2] ✅** |

降阈值会让 2-1 **多检出一整圈**（39 格、shape 比真值大）—— 多出来的线把网格撑大了。
所以正确做法是**失败后降阈值重试**的回退策略（与上游自己的 `search_tile_center → corner → rectangle` 多策略同思路），而不是改默认值。

**已在宿主 op 里实现该回退，并加了防幻觉闸门**（默认 75 → 失败降到 50 → 40）。

闸门是必需的：降阈值会把**非地图画面**也"检出"成一片网格 —— 实测战役菜单 os_map.png
在 thr=50 下报 59 格 / shape [7,7]（幻觉）。两者在 thr=75 下报的是**同一句** reason
（`Vanish point and distant point too close`），所以**不能靠 reason 区分**，
只能靠**几何合理性**：真地图在回退阈值下是**干净矩形**（困难 1-4 → 21 格 = 7x3），
幻觉则不是（59 ≠ 8x8=64）。判据：回退生效时格数必须等于 (sx+1)*(sy+1)，否则判为未检出。
（闸门只卡回退路径 —— 默认阈值下的正常结果不适用：10-4 本来就缺 6 格，那是 UI 遮挡。）

六用例实测：

| fixture | mode | detected | grids | shape | 阈值 | 判定 |
| --- | --- | --- | --- | --- | --- | --- |
| 2-1 | main | True | 24 | [5,3] | 75 | ✅ 未受影响 |
| 10-4 | main | True | 48 | [8,5] | 75 | ✅ 未受影响 |
| **困难 1-4** | main | **True** | **21** | **[6,2]** | **50** | ✅ 回退生效 |
| 海域 9x6 | os | True | 49 | [8,5] | 75 | ✅ |
| 战役菜单（非地图） | main | False | — | — | 50 | ✅ 闸门拦住幻觉 |
| 动画期脏样本 | main | False | — | — | — | ✅ 仍拒绝 |

困难图 fixture 已登记进 `map_fixtures.json`（IR 用普通 `campaign_1_4`：G3 = 21 格），
IR 交叉校验通过（shape 一致、缺格 0）。

### 困难图暴露的一个判据问题：`is_current_fleet` 不该当"敌人落陆地"处理

困难 1-4 的 IR 交叉校验在 C# 侧报 1 处不一致：船格 (6,1) 落在 IR 的陆地格上。
把两侧数据摆出来看，性质很清楚：

| 检出标志 | 格 | IR 地形 | 判定 |
| --- | --- | --- | --- |
| `is_fleet` | (0,0) | `SP`（出生点） | ✅ 一致 |
| `is_enemy` | (2,1) | `ME` | ✅ 一致 |
| `is_enemy` | (5,2) | `ME` | ✅ 一致 |
| **`is_current_fleet`** | **(6,1)** | **`++`（陆地）** | ❌ 分歧 |

**敌人三个全部落在 IR 允许的 `ME` 上**（这条最强）；唯一分歧出在 `is_current_fleet` ——
它是**"当前操作舰队"的指派标志**（预测器判定哪支检出的舰队是当前舰队），不是地形信号，
指错一格是预测器层面的问题，与"识别坐标整体偏移"不是一回事。

所以判据要拆开：**敌人/BOSS/塞壬落在陆地 = 硬失败**（地形强约束）；
**己方舰队落在陆地 = 警告**（列出但不判失败）—— 因为 `is_current_fleet`/`is_fleet`
是派生指派，可能指错，而敌人位置与地形是强相关。这一条待改进 `MapCheck.cs` 的校验 3。

顺带记录：用户此时切的画面（`pages=[]`）检出 `30 格 / shape [7,3] / thr=75 / 3 个标志`，
已存为 `data/fixtures/map_event.png` 待确认是否活动图（若是，需问用户是哪一关以登记 IR）。

## BOSS 认不出来？**先怀疑自己的取证链路**（2026-09-22 更正，含一次我自己的错误结论）

原始症状：清完小怪、只剩 BOSS 时上游 `Full scan find boss.` → `No boss found.` →
`battle_6` 的 `if boss:` 分支跳过 → 十次无战果 → `withdraw()`（用户实测"全清完小怪就主动撤退"）。

### 我当时的（错误）结论

我存了一帧（BOSS 已刷出），量到"BOSS 格子上游红色判据 -0.382 ✗ / 蓝色判据 0.981 ✓"，
于是判定**本客户端把 BOSS 眼睛画成了蓝色**，并加了一版"蓝色眼睛"垫片。
那一版垫片**是错的**：它建立在"存盘 PNG 的颜色 = 引擎给上游的颜色"这个前提上，而这个前提不成立。

### 链路错在哪（这个坑值得单独记）

```
引擎截图 E（ALAS 约定 RGB）
  → cv2.imwrite(path, E)      # cv2 把数组当 BGR 写 ⇒ 文件色相对真实屏幕 R/B 互换
  → load_image(path)          # PIL 忠实读文件 ⇒ 又是一次 R/B 互换
```

两次叠加在一次分析里，红与蓝正好看反。用设备裸 `adb exec-out screencap -p` 当真值一比就清楚了：

| 区域 | 真值 RGB | 引擎给上游的图 E |
| --- | --- | --- |
| "立即前往"按钮 | (247.2, 222.4, 157.7) | (245.7, 219.3, 149.3) ✓ 一致（黄） |
| 存盘 PNG 读回 | — | (157.7, 222.4, 247.2) ✗ R/B 互换 |

**结论：引擎给上游的图是对的（RGB，与设备真值一致）**，问题只在我的存盘/读回那一段。

### 正确的复核方式与结论

在**引擎真正交给上游的那张图 E** 上跑上游原版 `predict_boss`：

```powershell
python tools/diagnostics/oneoff/probe_boss_channel.py data/_map_now3.png
#   === 文件色 ===  上游原版 predict_boss 认出的 BOSS 格: []
#   === 引擎 E ===  上游原版 predict_boss 认出的 BOSS 格: [((3, 2), 'BO')]   ← 原版就能认出来
```

也就是说：**本客户端的 BOSS 图标本来就是上游期望的红色，`predict_boss()` 一直能工作**。
"只剩 BOSS 却撤退"的真正原因在别处：

1. **BOSS 所在格没进过相机视野** —— 上游 `full_scan()` 只走 `MAP.camera_data`
   （11-1 是 `['D3','E4']`），而 BOSS 可能刷在别的 `MB` 格（11-1 有 4 个：`H1/A2/F3/G6`，
   两次实测分别刷在 F3 和 A2）；
2. **扫描时机早于 BOSS 刷新** —— BOSS 在第 6 回合才出现，紧接着就扫是扫不到的。

执行器每轮调一次**上游自带、但上游自己没调用过**的 `full_scan_find_boss()`
（`camera.py:530`，它会依次把相机对准每一个 `may_boss` 格），正好补上第 1 条。

### 仍然成立的两条硬约束（与颜色无关）

`module/map_detection/grid_info.py:220-225` 只接受**声明为 `MB` 的格**：

```python
if info.is_boss:
    if not self.is_land and self.may_boss:   # ← map_data 里必须是 MB
        self.is_boss = True
    else:
        return False                          # ← 否则丢掉
```

所以 BOSS 刷在哪个格都行，但必须是 `map_data` 里标了 `MB` 的那几个之一 —— 11-1 实测
`may_boss = [H1, A2, F3, G6]`，两次真机分别落在 **F3**、**A2** ✓（离线对齐校验：
`probe_boss_global.py`）。

### 现在的做法

- `apply_boss_icon_color_compat()` **默认关闭**（保留为"某章图标真不是红色时"的一键对照，没有被证实需要就不开）；
- `op_device_screencap` 落盘前做 `RGB2BGR`，**存盘 PNG 从此与真实屏幕一致**
  （`load_image(png)` 读回来 == 引擎的 E）；`device_capture_set` 也统一成"宿主当前图 = E"，
  并显式处理 `raw=True` 时后端绕过 `BGR2RGB` 的情况。

### 复现与回归

```powershell
# 通道顺序自检（与设备裸 adb 截图对比，全程不落盘读回）
python tools/diagnostics/oneoff/probe_channel_order.py
# 在引擎约定的图上复核 BOSS 判据
python tools/diagnostics/oneoff/probe_boss_channel.py data/_map_now3.png
# 从"只剩 BOSS"的半途状态接着打（不消耗小怪那几场）
python tools/diagnostics/oneoff/resume_boss.py --chapter campaign.campaign_main.campaign_11_1 \
    --battle-count 6 --fleet1 3 --fleet2 6
```

真机记录（11-1，两套战斗流程各跑通一次，均 `exit 0`、无 `WITHDRAW`）：
`Full scan find boss.` → **`Boss found: [F3]`**（另一局是 `[A2]`）→ `BATTLE_6` →
`Using function: battle_6` → `Is boss: [F3]` → `<<< CLEAR BOSS >>>` → 战斗 →
回到章节页（`In stage.`，出击正常收尾）。事后 11-1 的关卡信息面板为
**威胁排除 100%**、三个条件全亮、章节页徽章是 `Clear!` + `COMPLETELY ELIMINATED` + ★★★。


## 复核：哪些"不支持"是真的（2026-09-22 夜，离线逐帧）

起因：跑 `probe_backends.py` 时发现"7-1 识别不了"这条结论**站不住** ——
`data/fixtures/inmap_7-1.png` 根本不是图内帧，而是**主界面**（秘书舰/宿舍背景那张 `page_main`）。
拿它去测地图识别，当然报 `No vertical line detected`。

`map_detect`（含 5 档降阈值重试）在存盘帧上的实测：

| 帧 | 内容 | homography | perspective |
| --- | --- | --- | --- |
| `subchapter_1_1.png` | 1-1，**7 格单行**（真图内帧 ✓） | FAIL `No vertical line detected` | FAIL |
| `map_hard_1_4.png` | 困难 1-4，**3 行 21 格** | **OK shape=[6,2]=21 格** | OK 但 [6,4]=35 格（多判一行）✗ |
| `inmap_7-1.png` | ⚠️ **其实是主界面，不是图内帧** | FAIL（无意义） | FAIL（无意义） |
| `inmap_2-2.png`（对照） | 2-2，4 行 24 格 | OK [6,4]=35 格 | OK [6,4]=35 格 |
| `inmap_3-1.png`（对照） | 3-1，4 行 28 格 | **OK [6,3]=28 格** ✓ | OK 但 [6,5]=42 格（多判一行）✗ |
| `inmap_3-2.png`（对照） | 3-2，4 行 32 格 | **OK [7,3]=32 格** ✓ | OK 但 [7,4]=40 格（多判一行）✗ |

结论：
1. **`perspective` 后端在多行图上会多判一行**（3-1/3-2/1-4 都是），所以默认仍必须是 `homography` ✓
   —— 这与既有记录一致；
2. **困难 1-4（3 行）现在是能识别的**（doc 前半段"困难图未能检出"那句已过时：那是加 5 档降阈值重试之前的结论）；
3. **真正确认失效的只剩"单行图"（1-1）**，而且那一帧是真图内帧；
4. **7-1 / 8-1 / 1-2 的"不支持"没有有效证据**（fixture 是错的或缺失），要重新抓真图内帧才能下结论。

复现：

```powershell
python tools/diagnostics/oneoff/probe_backends.py          # 两后端 × 六帧并排
python tools/diagnostics/oneoff/probe_backends.py --frames data/fixtures/inmap_7-1.png --backends homography
```

## 复现

```powershell
python tools/diagnostics/verify_map_detection.py
```
