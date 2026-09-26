# 核心文档

日常开发只需阅读以下文档；项目构建与启动见[项目首页](../README.md)。

| 文档 | 内容 |
| --- | --- |
| [迁移路线](architecture-roadmap.md) | 当前完成度、缺口、下一步与阶段门槛 |
| [运行时](runtime.md) | 会话、控制台、停止、断点和工件 |
| [任务域](tasks.md) | 队列格式、任务种类、授权与验证入口 |
| [结果合同](result-contract.md) | 成功、撤退、失败的唯一判定规则 |
| [上游适配](s3-upstream-adaptation.md) | 章节加载、原生战役、识别与兼容边界 |
| [上游引擎重写](upstream-engine-rewrite.md) | C# 全业务重写目标、实际 Python 依赖及仅视觉桥的迁移门槛 |
| [架构梳理](architecture-notes.md) | 依赖边界、上游结构与接缝纪律（重写流程的证据来源） |
| [统一 UI](r4-ui-architecture.md) | 桌面、网页和服务器共用前端的方案 |
| [并行协作](multi-agent-development.md) | worktree 隔离、agent 通信、集成与清理 |

详细材料集中在[归档](archive/README.md)：`history/` 保存历史调查，`reports/` 保存脚本生成的验收报告及证据输入。
历史快照不代表当前实现；生成报告只证明其记录的样本与调用路径。

维护要求：核心文档按主题更新，不追加轮次日志、聊天记录或重复状态表；阶段状态只在路线中维护。
历史材料归档，生成报告修改对应脚本后重建。真实证据和原始哈希不为文档整理而改写。
