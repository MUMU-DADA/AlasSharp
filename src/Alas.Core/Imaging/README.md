# Imaging —— 参考实现，**不是产品路径**

这里是用 C# 手工移植的 OpenCV 原语：PNG 解码、`crop` / `get_color` / `color_similar`、
灰度化、OTSU、模板匹配。

**产品路径不在这里。** 识图走 `Alas.Core.Vision`（进程内 CPython 直调上游模块），
见仓库根 README 的「架构铁律 2」。

## 保留它的理由

它是「手工移植不可行」这一结论的**证据与回归基准**：

- 它能对齐的部分确实对齐了：6192 例素材对拍中，通道语义、`appear` 判定、容差、
  均值颜色全部零不一致（均值最大偏差 2.84e-14）。
- 它对齐不了的部分也定位清楚了：800 例模板匹配中残差最大 **0.25**，
  来源是 OpenCV 按模板/搜索区尺寸比切换相关算法（实测同位置 0.7487 vs 1.0000），
  叠加不同转换的定点精度差异（2GRAY 系列 15 位、RGB2YUV 的 Y 是 14 位）
  与退化情形的非对称处理（模板方差 0 → 1.0，窗口方差 0 → 0.0）。

如果将来有人再想"用别的语言重写识图"，这些基准可以直接拿来回答他。

## 已经确认的语义（照抄上游，不是我们的选择）

| 约定 | 依据 |
|---|---|
| 通道顺序是 **RGB** | 上游 5 个截图方法在 `cv2.imdecode` 后统一 `COLOR_BGR2RGB` |
| RGBA **直接丢 alpha** | 上游走 `cvtColor(RGBA2RGB)`，不与背景合成 |
| 灰度图**保持单通道** | 304 张 PNG 是 8 位灰度，`cv2.mean()[:3]` 因此返回 `(mean, 0, 0)` |
| `crop()` 用**银行家舍入** | 上游是 Python `round()`；C# 必须 `MidpointRounding.ToEven` |
| 越界**补零黑边** | 内容尺寸 = `min(x2,w) − max(x1,0)`，再加补边 |

## 相关命令

```powershell
& $py tools\make_imaging_fixture.py            # 生成对拍基准（真值由 ALAS 函数产出）
.\Alas.Server.exe imaging                      # 校验原语
& $py tools\make_matching_fixture.py           # 生成匹配基准（真值由 cv2 现算）
.\Alas.Server.exe matching                     # 校验匹配（会出现已知残差）
```
