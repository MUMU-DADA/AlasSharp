# 真实队列脱敏工件

仅保存真实 `queue` 入口产生的 `account_state`、`observe`、`navigate` 成功队列。
当前验证器明确拒绝其他任务域，不将战役或周期任务的结果套入这些判据。
每个目录包含队列、逐任务工件、断点和会话结构化日志；`archive.json` 保留项目相对来源、
原件及脱敏件的 SHA-256 和脱敏范围。原始截图、控制台日志、账号配置与设备信息不入库。

生成归档（只读读取原件，无设备动作，不覆盖已有归档）：

```powershell
python tools/diagnostics/audit_queue_evidence.py --archive-run data/<run>/artifacts/<timestamp>
```

`python tools/diagnostics/audit_queue_evidence.py` 校验全部归档并重建 `docs/queue-evidence.md`。
加 `--check` 则只校验文档，无写入。`verify_queue_evidence.py` 覆盖缺失工件、校验和篡改、
观测零 tick、计数矛盾、导航未到目标、会话重启和断点身份等反例；全部离线执行。

原件存在时验证其校验和及可重复脱敏；其他检出环境中只能核对已归档字节和各工件间的一致性，
不会把原件缺失宣称成真机重跑。摘要只记录实际观测，不证明未解锁功能可用或战役通关。
