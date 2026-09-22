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
| 地图正样本 | ⏳ 缺正样本 | 需要真机地图画面（见下） |

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
  "fixture": "map_2_1.png"
}
```

## 逐张 fixture

| fixture | globe | 往返误差 | map detected | 原因 |
| --- | --- | --- | --- | --- |
| `map_2_1.png` | — | — | False | No vertical line detected |
| `os_map.png` | — | — | False | TypeError: arrays to stack must be passed as a "sequence" type such as list or t |

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

## 真机战斗地图正样本：检测失败，且已定位到具体一步

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
下一步：① 再抓一张"停稳后"的地图样本做对照；② 直接测纵向分隔在取反图里的峰值高度
与横向分隔对比，确认是渲染差异还是取样问题。

## 复现

```powershell
python tools/diagnostics/verify_map_detection.py
```
