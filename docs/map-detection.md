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
| 战役地图的网格识别（`View.load` 正路径） | ⏳ 必须真机战役地图截图（要出击） |

## 复现

```powershell
python tools/diagnostics/verify_map_detection.py
```
