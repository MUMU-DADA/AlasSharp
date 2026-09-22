# OCR 模型（ONNX）

本目录是 **Python 3.14 基座**使用的 OCR 模型。与 `../cnocr_models/`（上游 3.7 基座的 mxnet 模型）
并存：`cnocr_models/` 保留不删，作为 3.7 基线的回退依据；`master` 分支仍用那一套。

## 内容

| 文件 | 类别数 | 用途 |
|---|---|---|
| `azur_lane.onnx` / `.labels.txt` | 39 | 国服舰船名等，字集 `0123456789ABCDEFGHIJKLMNPQRSTUVWXYZ:/-`（不含 `O` 与空格） |
| `azur_lane_jp.onnx` / `.labels.txt` | 39 | 同上，日服字体 |
| `cnocr.onnx` / `.labels.txt` | 6426 | 通用中英数（`lang='cnocr'` 的调用点走这个） |
| `jp.onnx` / `.labels.txt` | 3052 | 日服通用 |
| `tw.onnx` / `.labels.txt` | 5322 | 繁中通用 |

类别数 = 字集行数 + 1（多出的一个是 CTC blank）。
`labels.txt` 的每一行是一个字符，**行号即类别索引**，第 `N` 行对应索引 `N`；
最后一个索引（= 行数）是 CTC blank。

## 网络与输入输出

- 结构：`densenet-lite-gru`，CNN (densenet) + 双向 GRU(128) + CTC，与上游 mxnet 模型同构
- 输入：`data`，`float32`，形状 `(1, 1, 32, W)`，取值已归一化到 `[0, 1]`
- 输出：`output`，`float32`，形状 `(W/4, num_classes)`，逐时间步已做 softmax
- 固定 32 像素高、宽度按比例缩放（与上游 `_preprocess_img_array` 一致）；
  时间步数 = 缩放后宽度 / 4

## 这两个模型是怎么来的

**没有重训。** 本仓库内没有 OCR 训练代码，5 套权重是上游在仓库外训练的，
只能在 mxnet 与 onnxruntime 两个互斥的环境之间搬运：

```powershell
# 阶段 1：CPython 3.7（mxnet 1.6.0 支持的最高版本，且只有它有 Windows 轮子）
python dev_tools/ocr_model_convert.py --stage dump

# 阶段 2：本仓库的 3.14 环境
uv run python dev_tools/ocr_model_convert.py --stage build
```

转换脚本：`dev_tools/ocr_model_convert.py`。原理、结构细节与两个易错点（GRU 门序、ONNX 的
GRU 偏置布局）都写在该文件的模块 docstring 里，此处不重复。

## 精度验收

阶段 1 为每个模型保存了一份随机输入的前向真值（mxnet 侧算出的 softmax），
阶段 2 用它断言 ONNX 与 mxnet 数值一致。实测：

| 模型 | max abs err | argmax 一致率 |
|---|---|---|
| azur_lane | 5.96e-06 | 1.0000 |
| azur_lane_jp | 5.66e-06 | 1.0000 |
| cnocr | 2.90e-05 | 1.0000 |
| jp | 1.69e-05 | 1.0000 |
| tw | 5.73e-05 | 1.0000 |

**这个验收证明的是「数值等价」，不是「识别准确率不降」。** 后者需要真实截图样本集对拍
（同一批图分别过 mxnet 与 ONNX，比最终识别串），目前**尚未做**，因为没有样本集。
在补上之前，OCR 的准确率验收应标注为未验证。
