# 地图模型跨语言对照

由 `tools/diagnostics/verify_map_ir.py` 生成；C# 摘要来自 `alashub map-ir`。
对照上游实际 `Campaign.MAP` 的字段摘要和逐格 `GridInfo.decode`。相机集合排序后比较成员。
辅助模块按原生对象分类；导入错误、缺少摘要和网格不一致均失败，不按文件名跳过。
这是离线数据对照，不证明可进入关卡、战斗流程或真实通关。

索引 1437 个模块，本次检查 1437 个；全量：True。

| 分类 | 数量 |
| --- | --- |
| passed | 1367 |
| support_module | 67 |
| upstream_error | 3 |

## 差异及源错误

- `event_20200227_cn/c2.json`：ImportError: cannot import name 'C2' from 'module.campaign.assets' (<project>\.runtime\engine\module\campaign\assets.py)
- `event_20200227_cn/d3.json`：ImportError: cannot import name 'D3' from 'module.campaign.assets' (<project>\.runtime\engine\module\campaign\assets.py)
- `event_20200312_cn/sp3.json`：ImportError: cannot import name 'EVENT_20200312CN_SP3' from 'module.campaign.assets' (<project>\.runtime\engine\module\campaign\assets.py)

脱敏范围：错误中的项目绝对路径替换为 `<project>`；未改变判据或错误类型。

复现：`alashub map-ir` 后运行 `python tools/diagnostics/verify_map_ir.py`。
默认检查全部模块；`SAMPLE` 环境变量仅供显式抽样诊断。
