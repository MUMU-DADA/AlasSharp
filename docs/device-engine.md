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
