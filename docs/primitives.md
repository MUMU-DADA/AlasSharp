# 控制原语真机验证记录（返回键 / 长按 / 滑动）

页面与控件的**识别**见 `page-verification.md` 与 `controls.md`；这里验的是把动作
真正发到设备上的那三种原语。判定一律基于**上游规则的返回值变化**或**上游自己的判据**，
不是"命令发出去了就算过"。

设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服）。
脚本：`tools/diagnostics/verify_primitives.py`；原始数据 `data/primitives_verify.json`。

## 结果

| 原语 | 上游形态 | 结果 | 依据 |
| --- | --- | --- | --- |
| keyevent BACK | `adb shell input keyevent 4` | hit | input keyevent 4 |
| keyevent BACK | `adb shell input keyevent 4` | hit | input keyevent 4 |
| long click (adb form) | `input swipe x y x y 1000~1200`（同点滑动） | hit | input swipe x y x y 1100（上游 long_click 的 adb 形态）→ EQUIPMENT_OPEN match=True score=0.9914；判据取自上游 ship_info_enter |
| swipe (real device) | `input swipe x1 y1 x2 y2 <ms>` | hit | at_top: 起始 True -> 拖着滚动条滑块往下 6 次 False -> 往上 3 次 False：判定随滑动翻转，说明 input swipe 在真的驱动画面。上游的时长系数 ×2.5 未在此隔离验证（布尔判据分辨不出） |

长按那一条值得单独说：判据用的是**上游自己的验收条件**。
`module/equipment/equipment.py` 的 `ship_info_enter()` 等的就是 `EQUIPMENT_OPEN` 出现，
实测在船坞长按舰船卡片后该素材 score=0.9922 命中，即"已进入舰船详情"——
与上游判定同口径，不是我们自己定的标准。

## 未覆盖（如实列出）

| 项 | 原因 |
| --- | --- |
| 文本输入 | 上游只在「装备码」功能里用 `d.send_keys(text=code)`（uiautomator2 后端，见 module/equipment/equipment_code.py）。本机控制后端是 adb，且该功能入口需要特定界面，未验证。要用时得先决定后端（adb 的 `input text` 与 uiautomator2 的 send_keys 语义不同：前者不支持中文/清空）。 |
| 长按的"返回后状态" | 长按进舰船详情后再按返回能回船坞（本次已顺带走到），但上游 gems_farming 那条真实用法的完整流程（出击→长按→装备）未验。 |
| 滑动时长系数 ×2.5 | 上游 Device.swipe 对 adb 分支把时长乘 2.5（注释 "ADB needs to be slow, or swipe doesn't work"）。本项目已照抄进 DeviceController.SwipeUpstream，但"少了它会不会滑不动"没有隔离验证 —— at_top/at_bottom 是布尔量，分辨不出时长差异。 |

## 复现

```powershell
$env:STUB_ADB = "<adb.exe>"
python tools/diagnostics/verify_primitives.py
```

脚本内所有设备访问都走 `tools/diagnostics/adb_util.py`：本机 adb server 会被新起的
客户端抢占，表现是 `connect` 说成功但 `screencap` 返回 **0 字节**（看着像图像问题，
其实是连接问题），所以"connect → devices 校验 → 操作"必须在同一进程内完成并重试。
