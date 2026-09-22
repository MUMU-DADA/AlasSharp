# 设备引擎（方案 A）：设备 I/O 走宿主，多后端可切换

> 目的：让 C# 不再自己实现控制/采集后端，而是通过**宿主协议**调用引擎自带的多引擎设备层。
> 引擎 `module/device/method/` 里已有 15 个后端（与第三方上游 AzurPilot 同源）：
> `adb / ascreencap / droidcast / hermit / ldopengl / maatouch / minitouch / nemu_ipc / scrcpy / uiautomator_2 / wsa`
> 由 `Emulator_ScreenshotMethod`（截图）与 `Emulator_ControlMethod`（输入）两个配置键选择。

## 新增 op（`tools/alas_vision.py`）

| op | 说明 |
| --- | --- |
| `device_configure` | 选择后端：`serial` / `screenshot` / `control` |
| `device_info` | 串号、两个后端、包名 |
| `device_screencap` | 截图到 `path`，返回耗时；`raw=true` 时**绕开 ALAS 的截图间隔节流**，直接调后端的 `screenshot_<method>` |
| `device_click` / `device_swipe` / `device_back` | 输入走引擎 |

## 集成路上踩到的三个坑（都已修，记录避免重犯）

1. **必须先 `import module.device.pkg_resources`** —— adbutils 会 `import pkg_resources`，
   ALAS 靠这个桩顶替，而桩**只有先被导入才生效**。
2. **配置必须放进 `cfg.multi_set()`** —— 否则 ALAS 的配置系统会回写覆盖，`Emulator_Serial`
   变回 `auto`，设备探测失败，`Device.__init__` 重试 4 次后抛 `RequestHumanTakeover`
   （而且**消息是空的**，极具误导性）。
3. **`Device.screenshot()` 返回 numpy 数组**（BGR），不是 PIL Image：
   `.save(path)` 会报 `'numpy.ndarray' object has no attribute 'save'`；
   而 numpy 的 `.size` 是元素总数（int），`list(img.size)` 会报 `'int' object is not iterable`。

## 实测（MuMu，1280x720，`127.0.0.1:16384`）

| 后端 | 截图中位（经 `screenshot()`） | 原始截图（`raw=true`，绕过节流） | 结论 |
| --- | --- | --- | --- |
| `adb` | 378 ms | **401.7 ms**（385.8 / 387.0 / 401.7 / 404.2） | ✅ 可用；与 C# 侧量到的 433 ms 同档 |
| `ascreencap` | 377 ms | ❌ `Repositioning byte pointer failed, corrupted aScreenCap data received` | 需按模拟器/Android 版本换 ascreencap 二进制 |
| `droidcast` | 375 ms | ❌ `Retry screenshot_droidcast() failed` | 需起 DroidCast 服务端 |
| `scrcpy` | ❌ `Retry screenshot_scrcpy() failed` | — | 需推送 scrcpy server 并处理端口 |

**测量陷阱（重要）**：直接走 `screenshot()` 时，三个后端都量到 ~375 ms —— 因为
`screenshot()` 里有 `self._screenshot_interval.wait()`，**0.3 s 的节流把后端差异整个盖住**。
要比后端速度必须 `raw=true`（或先 `screenshot_interval_set(0)`）。

## 现状与下一步

- **接线完成**：多后端可切换，`adb` 后端端到端可用（构造 → 截图 → 输入）。
- **速度红利尚未拿到**：ascreencap / droidcast / scrcpy 在**本机 MuMu 上**都需要各自的
  落地配置（二进制版本 / 服务端 / 端口），这不是"接线"问题而是"每个后端的现场调试"问题。
  做完之前，"打掉 400 ms 截图瓶颈"这个目标**还不能宣称达成**。

## 逐后端现场排查（本机 MuMu：Android 12 / SDK 32 / x86_64）

设备实测：`ro.product.cpu.abi=x86_64`、`ro.build.version.release=12`、`sdk=32`。

### ascreencap —— **上游不支持，不是配置问题**

引擎的变体选择（`module/device/method/ascreencap.py:87-100`）：

```python
if   sdk in range(21, 26): ver = "Android_5.x-7.x"
elif sdk in range(26, 28): ver = "Android_8.x"
elif sdk == 28:            ver = "Android_9.x"
else:                      ver = "0"          # ← SDK 32 落到这里
filepath = os.path.join(..., ver, arc, 'ascreencap')
if not os.path.exists(filepath): ...          # 目录 "0" 不存在 → 判定不可用
```

而上游 `bin/ascreencap/` 只有 `Android_5.x-7.x / Android_8.x / Android_9.x` 三档。
**结论：Android 12 上 ascreencap 无二进制可用（上游设计如此）**，不必再试；
报错形态是 `corrupted aScreenCap data received` / retry failed，容易被误当成"数据损坏"。

### nemu_ipc（MuMu 原生快速通道）—— 需模拟器侧配置

```
INFO     NemuIpcImpl init, nemu_folder=C:\Program ...     ← 找到 MuMu 安装目录
WARNING  Failed to call nemu_connect, result=0            ← IPC 连接失败
```
即引擎找到了 MuMu，但 `nemu_connect` 返回 0。这是**模拟器侧**的事（MuMu 的 IPC/权限/实例状态），
不是代码问题；需要时再逐项试（管理员权限、MuMu 的 ADB/IPC 开关、实例号）。

### droidcast / scrcpy —— 未通，需服务端

`droidcast`：`Retry screenshot_droidcast() failed`（它走 Java 服务端，需先起服务）；
`scrcpy`：`Retry screenshot_scrcpy() failed`（需推 scrcpy server 并处理端口）。
两者都是"要额外起一个服务端"的形态，适合作为下一步的候选。

### 基线

`adb` 原始截图 **396–402 ms**（多次测量稳定），与 C# 侧自测的 433 ms 同档 —— 这就是当前基线。

## 两个真正的集成修复（不修这两个，多数后端都"看起来不可用"）

### 修 1：把项目内固定的 adb 挂到 PATH（`_device_engine()`）

引擎内部有些地方直接调**裸 `adb`**（`adb push` 推 MaaTouch / minitouch / DroidCast 的二进制），
裸名解析不到就报 `FileNotFoundError: [WinError 2]`。**它会伪装成"某个后端不可用"**：
droidcast 最初就是死在这一步（先报 u2 的 ConnectionError，紧接着 push 失败）。

### 修 2：`device_click` 要把坐标包成上游 `Button`

ALAS 的 `Control.click/swipe` 收的是 **Button 对象**（内部取 `button.button` 作为可点区域），
直接传 int 会报 `'int' object has no attribute 'button'`。

## 修完之后的实测（稳态，`raw=true`）

| 后端 | 截图 | 点击 |
| --- | --- | --- |
| `adb` | 393–402 ms | **64.5 ms** |
| `droidcast` | **337 / 343 / 344 / 342 ms（中位 ≈343）** ✅ 比 adb 快约 13% | — |
| `minitouch` | — | **68.3 ms** ✅ |
| `MaaTouch` | — | 375.8 ms（首击含初始化/push，需预热再测） |
| `ascreencap` | 上游判词：`not available for this device, please use other screenshot methods`（Android 12 无二进制） | — |
| `nemu_ipc` | `Failed to call nemu_connect, result=0`（需模拟器侧配置） | — |

**结论（本轮）**：`droidcast` 是第一个**真正跑赢 adb 基线**的截图后端（≈343 ms vs ≈397 ms，快约 13%），
代价小（一个 95 KB 的 APK + adb forward），可作为生产候选；输入侧 `minitouch`/`ADB` 都在 ~65 ms 量级。
下一步：把 droidcast 稳态再压一压（分辨率/编码参数），以及预热后重测 MaaTouch。

## C# 产品路径已接入（lashub capture，实测收益）

IVisionEngine 新增 ConfigureDevice(serial, screenshot, control) 与 CaptureViaEngine(raw)；
DeviceController 暴露 CaptureViaEngine() / ConfigureEngineDevice()；
新增 lashub capture 做两路对比（同一进程、同一设备）：

| --screenshot | A：C# 自己 adb 截图 | **B：引擎截图+置入宿主** | 提升 |
| --- | --- | --- | --- |
| db | 615 ms | **392 ms** | **−36.1%**（省 222 ms） |
| droidcast | 625 ms | **339 ms** | **−45.8%**（省 286 ms） |

两路的页面判定都是 page_main,page_main_white —— 说明 B 路拿到的是**可用帧**，不是"快但错"。

用法：

`powershell
alashub capture --adb <adb> --serial 127.0.0.1:16384 --screenshot droidcast --control ADB --repeat 3
`

意义：**换截图/输入后端不改 C# 代码**（只改 --screenshot / --control），
这正是"设备 I/O 走宿主"（方案 A）要的效果。

## 导航流程的接入与一次"反直觉"的测量（重要，别误读）

重构：`INavigationDevice` 的 `byte[] Screenshot()` 改为语义化的 **`void Capture()`**
（"让宿主拿到当前帧"），`DeviceController.CaptureForHost()` 负责选路：
`UseEngineCapture=true` → 引擎抓图直入宿主；否则沿用 `adb 取字节 → SetScreenshot`。
`goto` 新增 `--capture-engine [--screenshot droidcast] [--control ADB]`。

实测（每跳约 2.5s settle，两次都先回 page_main 再量）：

| 变体 | 导航耗时 | 结果 |
| --- | --- | --- |
| A 传统 C# adb 路径 | **7914 ms** | success=True |
| B 引擎通道（droidcast） | **8845 ms** | success=True（**慢约 930 ms**） |

**为什么反而慢**：`alashub` 是"一次调用一个进程"，B 每次都把**设备层初始化**
（DroidCast 推 APK + adb forward + uiautomator2 检查 + `Device` 构造）算进了被测时间。
所以：

- **抓图级（长驻进程稳态）确实更快**：339 ms vs 625 ms（−46%，见上一节 `alashub capture`）；
- **命令行单次导航反而更慢**：一次性的设备层初始化吃掉了收益。

结论：这条通道的收益要在**长驻进程**里兑现（正式运行时 `Device` 只构造一次），
命令行的一次性调用不是它的使用场景。下一步若要给导航侧也拿到收益，
应当在长驻进程里预热设备层（或让 `goto` 支持"进程内导航两次"以量稳态）。

## 稳态测量（`goto --rounds N`）：导航场景下收益只有 ~1.7%，别夸大

为了把"一次性设备层初始化"从数字里剔除，给 `goto` 加了 `--rounds N`：
先正常走一次，然后再跑 N-1 个回合（回 page_main → 再去目标）并逐回合计时。

实测（每变体 3 回合 = 首次 + 2 次被测；设备层在进程内只构造一次）：

| 变体 | 稳态导航中位 | 两次实测 |
| --- | --- | --- |
| A 传统 C# adb 路径 | **6421 ms** | 6405 / 6421 |
| B 引擎通道（droidcast） | **6312 ms** | 6266 / 6312 |

即 **B 只快 ~109 ms（≈1.7%）**，远小于抓图级的 286 ms/帧。原因是**导航耗时被 settle 等待主导**
（`PageNavigator.SettleMs=2500`，2 跳就 5 s），抓图只占其中一小块。

**这条测量纠正了一个容易犯的推断**：抓图快 46% ≠ 导航快 46%。
抓图频率高的场景（战斗循环里每秒多帧）才是这条通道真正兑现收益的地方，
而它属于 S3 的领域；导航场景下若想提速，动 settle 策略比换截图后端更有效。
