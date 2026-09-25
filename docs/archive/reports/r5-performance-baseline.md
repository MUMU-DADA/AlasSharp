# R5 性能基线（离线）

> 本报告由 `tools/diagnostics/r5_perf_baseline.py` 重建，不手写。
> 用途：为"替换上游引擎"留替换前后的对照底数（P2 要求之一）。

## 口径与局限（先读这段）

- 寻路两边做的是**同一件事**（同一张真实地图上算成本场），但运行时不同（.NET 10 vs CPython 3.14），Python 侧还带 logger 开销——只看**数量级**，别当精确倍数；
- C# 侧取命令自报的内部 per_operation_ms 中位（每次 3 轮 × 每轮 200 次），不含进程启动；Python 侧同样预热后计时；
- 全库计划读取两边**工作内容不同**（C# 读导出并干跑全部钩子；Python 审计解析上游源码做 AST 统计），放一起只为记录量级，不能直接相减；
- 机器状态、后台负载会影响绝对值；重跑同一脚本即可得到同口径数字。

## 寻路成本场（同一张真实地图）

| 地图 | 规模 | C# per-op（ms） | 上游 Python per-op（ms） | 量级比 |
| --- | --- | --- | --- | --- |
| `event_20240229_cn/sp` | 最大地图（20×10 = 200 格） | 2.8586 | 5.5301 | 1.9× |
| `event_20220915_cn/c2` | 中位规模（8×8 = 64 格） | 0.4847 | 0.8421 | 1.7× |

## 全库计划读取（1437 个导出 / 3019 个钩子 / 5694 步）

| 工具 | 做什么 | 墙钟（秒） |
| --- | --- | --- |
| `Alas.Server r5-plan` | 读全部导出 + 干跑全部钩子 + 统计 | 0.65 |
| `tools/diagnostics/r5_upstream_audit.py` | 审计上游 `campaign/` 源码（AST） | 5.98 |

## 复现方式

```powershell
python tools/diagnostics/r5_perf_baseline.py
& src\Alas.Server\bin\Release\net10.0\Alas.Server.exe r5-path --fixture <夹具> --repeat 200
```

