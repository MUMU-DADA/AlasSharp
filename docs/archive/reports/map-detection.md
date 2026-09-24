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
| 检测过程 | ✅ | 构造、加载和预测错误不得当作正常未检出 |
| 逐格语义 | ✅ | 逐格标志抽取失败不能伪装成船标志为 0 |
| 船标志筛选 | ✅ | 记录网格被船标志防误报规则拒绝的帧 |
| 地图正样本 | ✅ 8 张检测到网格 | 需要真机地图画面（见下） |
| 检测 vs 关卡 IR | ✅ 6 张一致 | 检出的格数/形状必须与该关卡声明的 map_data 一致 |

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
    "similarity: 0.093",
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
| `_probe_now.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `_probe_r17.png` | — | — | True |  |
| `inmap_2-2.png` | — | — | True |  |
| `inmap_3-1.png` | — | — | True |  |
| `inmap_3-2.png` | — | — | True |  |
| `inmap_7-1.png` | — | — | False | MapDetectionError: No vertical line detected |
| `map_2_1.png` | — | — | False | MapDetectionError: No vertical line detected |
| `map_event.png` | — | — | True |  |
| `map_hard_1_4.png` | — | — | True |  |
| `map_settled.png` | — | — | True |  |
| `map_shape_9x6.png` | — | — | True |  |
| `menu_01.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `menu_02.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `menu_03.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `menu_04.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `menu_05.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `menu_06.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `menu_07.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `menu_08.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `menu_09.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `menu_10.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `os_globe_live.png` | — | — | False | MapDetectionError: Failed to find a free tile |
| `os_globe_view.png` | — | — | False | MapDetectionError: No vertical line detected |
| `os_live_2.png` | — | — | False | MapDetectionError: Failed to find a free tile |
| `os_map.png` | — | — | False | MapDetectionError: Vanish point and distant point too close |
| `subchapter_1_1.png` | — | — | False | MapDetectionError: Camera outside map: offset=(0, 1) |

## 验证范围

已登记夹具按完整章节模块加载上游 Config，包括导入和继承的检测参数。
宿主不按关卡降阈值或放大画面，不因地图行数或缺少夹具判定不支持。
Camera outside map 表示上游相机恢复分支；静态帧无法验证滑动后的状态。
大地图只显示相机窗口，局部坐标需通过 verify_map_alignment.py 与完整地图对齐。
识别通过只证明该帧的识别结果；完整通关仍需上游 CampaignEnd 和实战证据。

复现：`python tools/diagnostics/verify_map_detection.py`。
补充分析归档在 [map-detection-notes.md](../history/map-detection-notes.md)，不作为运行时规则来源。
