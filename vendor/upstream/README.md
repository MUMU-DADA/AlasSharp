# vendor/upstream —— 上游静态资源快照

本目录是上游 ALAS 仓库**静态资源的逐字节镜像**。它属于本仓库，不依赖旁边那份
Python checkout 存在。

## 为什么入库

识别规则的真值来自这些文件：`module/*/assets.py` 里的 `area`/`color`/`button`
只是坐标与颜色，**判定用的模板图就是这里的 PNG**。同理，OCR 权重、以及
`ascreencap`/`MaaTouch` 这类要推到设备上的二进制，也都是运行时必需品。

只依赖旁边的 Python fork 会有三个问题：

1. **仓库不自洽** —— 换台机器 clone 本仓库，没有 fork 就跑不起来；
2. **复现不出来** —— 上游改一张图，历史构建的行为就无法重建；
3. **查不到版本** —— 出问题时说不清"当时用的是哪一版素材"。

## 快照来源（MANIFEST.json 为准）

| 项 | 值 |
|---|---|
| 仓库 | AzurLaneAutoScript 来源工作区（个人 fork 身份已脱敏） |
| commit | `f6db21563bd89f046ae22cb2f2fcb1352a0ee6aa` |
| 分支 | `migrate/py314-base` |
| 上游工作区是否干净 | 是（快照等于该 commit，不是"某次本地改动"） |
| 文件数 / 体积 | 7436 / 153.6 MB |

来源元数据不保存个人远端 URL 或本机绝对路径；commit、分支及逐文件 SHA-256 保持原值。
`source.path` 使用 `<upstream-root>` 占位，实际工作区由同步命令的 `--source` 或环境配置提供。

## 范围

| 目录 | 文件数 | 体积 | 内容 |
|---|---|---|---|
| `assets/` | 7393 | 77.4 MB | 四服务器模板图（cn/en/jp/tw）+ 共用目录（shop / island / map_detection / mask / gui / gooey / stats_basic / research_blueprint） |
| `bin/ascreencap/` | 12 | 8.5 MB | 设备端快速截屏二进制（Android 5–9 × 4 个 ABI） |
| `bin/cnocr_models/` | 15 | 32.4 MB | 上游 mxnet OCR 权重（onnx 的来源） |
| `bin/ocr_models/` | 11 | 32.2 MB | 转换出的 onnx 权重 + 字符表 |
| `bin/MaaTouch/` `bin/DroidCast/` `bin/hermit/` `bin/scrcpy/` | 5 | 3.0 MB | 触控/投屏/截屏后端 |

**没有入库**：上游的 Python 源码（那是 fork 仓库的职责，本项目按"识图调用上游模块"
的架构在运行时使用它）、`config/`、`log/`、`campaign/` 等随任务产生的数据。

## 同步与校验

```powershell
# 从上游 fork 同步（幂等：无变化时不产生 diff）
python tools/sync_upstream_assets.py --source "<上游 fork 路径>"

# 校验：① vendor 与清单逐文件 sha256 一致 ② 上游是否已走在我们快照之前
python tools/sync_upstream_assets.py --check
python tools/sync_upstream_assets.py --check --strict-drift   # CI 用：上游一领先就失败
```

## 铁律

1. **不要手改这里的文件。** 改了 `--check` 会报"被改动"；需要变更是去上游改，
   再重新同步（这样 MANIFEST 的来源 commit 才有意义）。
2. **不要重命名目录。** `bin/`、`assets/` 这些路径与上游一致才有可追溯性；
   `.gitignore` 里为 `vendor/upstream/bin/` 单独开了例外就是这个原因。
3. 换行与 diff 已在 `.gitattributes` 里关掉（`vendor/upstream/** -text -diff`）。
   这些文件必须逐字节一致 —— 例如 OCR 的 `*.labels.txt` 若被转成 CRLF，
   字符表会混进 `\r`，识别结果直接错，而 diff 看不出来。

## 授权

上游仓库为 **GPL-3.0**，本仓库同样以 GPL-3.0 发布（见仓库根 `LICENSE`），
因此这些素材与权重的再分发与上游保持一致。
