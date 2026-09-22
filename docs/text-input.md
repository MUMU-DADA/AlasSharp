# 文本输入原语验证（装备码流程）

上游唯一用到文本输入的地方是装备码（`module/equipment/equipment_code.py` 的
`d.send_keys(text=code)`，uiautomator2 后端）。本机控制后端是 adb，对应形态是
`input text` —— 这一页验的就是**我们要用的那个形态**能不能真把字打进去。

设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服）。
脚本：`tools/diagnostics/verify_text_input.py`；数据 `data/text_input_verify.json`。

| 步骤 | 结果 | 依据 |
| --- | --- | --- |
| enter ship detail | hit | EQUIPMENT_OPEN 0.9916（上游 ship_info_enter 的判据） |
| open equipment code page | hit | EQUIPMENT_CODE_PAGE_CHECK 0.9978 |
| type text into box | hit | 打入 'Alas123' 后输入框区域像素变化：{'crop': [0, 660, 1100, 714], 'diff_bbox': (16, 0, 145, 10), 'nonzero_px': 539, 'area_px': 59400}（截图 _code_box_after.png） |

打入的字符串是 `Alas123`，退出后停在 `['page_dock']` —— **没有做任何提交**（装备码页有「导入/导出」，
点了会真的改装备；红线守卫里 CONFIRM 类素材一律拒绝点击）。

## 两个值得记住的点

1. **不要用 `EQUIPMENT_CODE_TEXTBOX` 的 area 去比像素差。** 那个 area 只是输入栏的
   中段，而文字从栏的最左端开始渲染；第一版只比中段，得到"零变化"，
   把已经成功的输入误判成失败。后来直接看截图才确认成功（输入栏里清楚显示 `Alas123`）。
2. **收尾必须放 `finally`。** 第一版在 OCR 调用处抛异常，结果把游戏留在了装备码页里
   没退出 —— 这类"验证脚本崩了但设备停在半路"是最容易留下副作用的情形。

## 复现

```powershell
$env:STUB_ADB = "<adb.exe>"
python tools/diagnostics/verify_text_input.py
```
