# 统一 UI 技术方案

采用 **React + TypeScript + Vite，ASP.NET Core 10 / Kestrel，Electron 桌面壳**。
目标是一份前端覆盖桌面窗口、远程网页与无桌面服务器，布局和主题尽量接近 AzurPilot。

## 各层职责

| 层 | 职责 |
| --- | --- |
| React 前端 | 共用布局、任务配置、队列、运行状态和报告；CSS 变量统一明暗主题、字体、间距与响应式抽屉 |
| Kestrel 服务 | 同源静态资源与 API；调用 `Alas.Core/Runtime`，不复制任务规则与结果判定 |
| Electron | 系统窗口、菜单、托盘、服务进程和退出生命周期；也可作为远程客户端 |
| 纯服务器 | 仅运行 .NET 与 Python/设备依赖；前端预构建后随包发布，运行时不需要 Node.js 或桌面显示服务 |

桌面采用系统窗口中的 Web 渲染；若要求每个控件都由操作系统原生绘制，就需要额外渲染层。
Tauri 可在后续有包体数据时重新评估；当前不增加第二套 UI，也不迁移到 Python.NET。

## 数据与界面

- 参照 AzurPilot 的顶栏、窄工具栏、任务导航、总览卡片和日志区域；先统一主题与公共组件。
- 配置表单消费上游元数据，输入校验、授权、调度、停止与结论由运行时提供。
- 初期使用 HTTP 命令/查询与 SSE 状态推送；服务迁移时固定事件游标、重连与慢客户端合同。
- 结果展示只消费 `TaskOutcome` 与 `sortie-result/1`；静态单文件报告继续保留归档能力。
- 游戏内界面操作仍复用上游原生流程，产品 UI 建设不改变逐界面适配禁令。

## 实施顺序

1. 固定运行、状态、报告、日志和停止 API，将当前 HttpListener 原型迁入 Kestrel。
2. 建设 React 公共布局、队列配置与报告，同一构建产物供桌面和网页使用。
3. 完成远程认证、读写权限、HTTPS、来源/CSRF 和受信任代理检查后，才开放非回环访问。
4. 接入 Electron 生命周期与平台打包，验证桌面、网页、纯服务器执行相同 dry-run 队列并展示相同结果。

桌面渲染器关闭 Node integration，开启 context isolation 和 sandbox，限制导航与外链。
关闭窗口与边界停止分别处理，不直接杀掉出击；日志、截图和配置读取受同一访问控制约束。

## 平台验收

先完成 Windows x64 全链，再验证 Linux x64/ARM64 服务器，其余桌面平台逐目标验收。
.NET/Electron 提供 Windows、Linux、macOS 的 x64/ARM64 目标，不等于本项目整包已支持这些组合。
每个目标都需验证发布、路径权限、Python/OCR 原生依赖、ADB、设备后端和服务生命周期；
网页另验 Chromium/Firefox/WebKit、窄屏、主题、键盘与断线恢复。
依赖和缓存存项目忽略目录，产品界面不依赖 CDN 或运行时在线构建。

当前仅本地控制原型完成验收，使用方式见[运行时](runtime.md)。
React、Kestrel 远程服务、Electron 包和跨平台自动化尚未交付。

## 依据

- AzurPilot 使用 React/TypeScript/Vite；上游调查以提交 `f67259dcd` 为依据。
- [AzurPilot 前端](https://github.com/wess09/AzurPilot/blob/master/frontend/README.md)、[原 ALAS Web 应用](https://github.com/LmeSzinc/AzurLaneAutoScript/tree/master/webapp)
- [Electron 平台](https://github.com/electron/electron#platform-support)、[安全建议](https://www.electronjs.org/docs/latest/tutorial/security)、[.NET RID](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog)
- 选型比较、性能未知项和来源细节见[调研归档](archive/history/r4-ui-architecture-20260924.md)。复用代码时保留 GPL-3.0 许可和来源，游戏图片另核授权。
