# 文档归档

日常使用与开发请看[核心文档](../README.md)。这里保存详细依据，不作为新的开发待办。

## 验收报告

`reports/` 由诊断脚本重建；页面累计证据 `page-verification.json` 也保留在此。
它们只证明记录中的样本，旧入口的报告不代表当前产品路径已验证。

| 范围 | 报告 |
| --- | --- |
| 总览与页面 | [识别总账](reports/status.md)、[页面命中](reports/page-verification.md)、[历史回归](reports/regression.md) |
| 实际结果 | [战役结果审计](reports/result-evidence.md)、[队列工件审计](reports/queue-evidence.md) |
| 识别与控制 | [地图](reports/map-detection.md)、[地图 IR](reports/map-ir.md)、[控件](reports/controls.md)、[原语](reports/primitives.md)、[文本输入](reports/text-input.md) |
| 判据对照 | [特异性](reports/specificity.md)、[合成正对照](reports/positive-control.md)、[阈值扫描](reports/button-threshold-sweep.md) |
| 迁移调查 | [钩子覆盖](reports/r3-candidates.md)、[形态](reports/r3-hook-shapes.md)、[地图初始化](reports/r3-map-data-init.md)、[调用词表](reports/s3-plan-vocabulary.md) |

使用 `tools/diagnostics/verify_all.py --docs-only` 执行离线验收并重建相关报告。
设备脚本的报告可用其 `--report-only`（提供该选项时）从本地记录重建，不为整理文档操作设备。

## 历史调查

`history/` 包含旧交接、S3 入口/跑图调查、设备/导航诊断及精简前的长文快照。
它们可能含已撤销的结论或退役命令，引用前必须核对核心文档与现行实现。
原始运行材料仍在本地忽略目录，脱敏结构化证据保留在 `tools/diagnostics/evidence/` 和 `queue-evidence/`，不在整理时改写。
