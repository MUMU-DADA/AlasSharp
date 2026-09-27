# 退役 Python 宿主归档

本目录保存旧 Core/S3/R5 迁移阶段的 Python 业务宿主、原生调度器和诊断脚本，
仅用于历史对照、证据追溯和迁移审阅。它们不属于产品发布输入，也不应从 Server、
桌面 UI 或 `Alas.Engine` 调用。

产品运行时唯一允许的 Python 进程是
`src/Alas.Engine/Imaging/Worker/vision_worker.py`。该 worker 只接收图像、模板和
数值识别参数并返回观测；规则、状态、设备、导航、任务和结果判定全部在 C# Engine。

归档脚本保留原始依赖路径，移动后不保证可直接执行。需要对照上游时应在隔离的离线
环境中显式指定脚本路径，不得把它们移回 `tools/` 或 `tools/diagnostics/`。
