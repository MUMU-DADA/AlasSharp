#!/usr/bin/env python3
"""R5 接缝核对：C# 原语 ↔ 上游执行方法（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_seam.py

为什么需要这份核对：
    `loop` 域要由 C# 执行，就必须决定**每个原语的设备动作由谁做**。实测 C# 生产路径上没有任何寻路/
    原语调用点，唯一的将来调用点是 C# 关卡循环内部；而地图点击几何、相机对齐、进战流程这些机制都在
    上游（`Fleet.goto` / `Camera.ensure_edge_insight` / `MapOperation.withdraw` …），**不能在 C# 侧复刻**
    （复刻就是另建一套逐地图/逐界面适配，违反项目纪律）。

    所以正确的接缝是：**C# 决定"做哪个原语、对哪个格子"，上游自己的方法负责"怎么在设备上做"**。
    本脚本用** introspection**逐个核对这条接缝，并如实列出"上游没有对应方法、必须由 C# 自己实现"
    的原语——那张表就是设备宿主的待办。

核对内容：
  1. 每个 C# 原语映射到的上游方法**存在**，且**定义在该类里**（不是别名/继承来的别的东西）；
  2. 打印它的签名，便于确认参数语义（如 `Fleet.goto(location, expected='', …)`）；
  3. 汇总"没有上游对应实现"的原语——这些只能在 C# 侧实现设备动作，属于真机宿主的实际工作量。

只读：不连设备、不导入游戏循环、不改变任何状态。
"""
from __future__ import annotations

import importlib
import inspect
import os
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
UPSTREAM = ROOT / ".runtime" / "engine"

# C# 原语 → 上游执行方法（`module.Class.method`）。写的是**执行设备动作的那一层**：
# 例如 C# 的 `clear_enemy`（选目标）在上游是 `Map.clear_enemy`，而真正走位是 `Fleet.goto`。
SEAM = {
    # 目标选择 / 战斗
    "clear_enemy": "module.map.map.Map.clear_enemy",
    "clear_chosen_enemy": "module.map.map.Map.clear_chosen_enemy",
    "clear_filter_enemy": "module.map.map.Map.clear_filter_enemy",
    "clear_any_enemy": "module.map.map.Map.clear_any_enemy",
    "clear_boss": "module.map.map.Map.clear_boss",
    "clear_potential_boss": "module.map.map.Map.clear_potential_boss",
    "clear_siren": "module.map.map.Map.clear_siren",
    "clear_mechanism": "module.map.map.Map.clear_mechanism",
    "clear_bouncing_enemy": "module.map.map.Map.clear_bouncing_enemy",
    "clear_all_mystery": "module.map.map.Map.clear_all_mystery",
    "brute_clear_boss": "module.map.map.Map.brute_clear_boss",
    "capture_clear_boss": "module.map.map.Map.capture_clear_boss",
    "battle_default": "module.campaign.campaign_base.CampaignBase.battle_default",
    "battle_boss": "module.campaign.campaign_base.CampaignBase.battle_boss",
    # 路障
    "clear_roadblocks": "module.map.map.Map.clear_roadblocks",
    "clear_potential_roadblocks": "module.map.map.Map.clear_potential_roadblocks",
    "clear_first_roadblocks": "module.map.map.Map.clear_first_roadblocks",
    # 拾取
    "pick_up_ammo": "module.map.map.Map.pick_up_ammo",
    # 舰队机动（实测这些都在 `Map` 里，不是 `Fleet`）
    "switch_to": "module.map.fleet.Fleet.switch_to",
    "goto": "module.map.fleet.Fleet.goto",
    "brute_fleet_meet": "module.map.map.Map.brute_fleet_meet",
    "fleet_2_push_forward": "module.map.map.Map.fleet_2_push_forward",
    "fleet_2_rescue": "module.map.map.Map.fleet_2_rescue",
    "fleet_2_step_on": "module.map.map.Map.fleet_2_step_on",
    "fleet_2_protect": "module.map.map.Map.fleet_2_protect",
    "fleet_2_break_siren_caught": "module.map.map.Map.fleet_2_break_siren_caught",
    "submarine_move_near_boss": "module.map.fleet.Fleet.submarine_move_near_boss",
    "withdraw": "module.map.map_operation.MapOperation.withdraw",
    # 地图/相机（由宿主动作触发）
    "update_map": "module.map.fleet.Fleet.update",
    "map_swipe": "module.map.fleet.Fleet.map_swipe",
    "focus_to": "module.map.fleet.Fleet.focus_to",
    "ensure_edge_insight": "module.map.camera.Camera.ensure_edge_insight",
    "handle_boss_appear_refocus": "module.map.fleet.Fleet.handle_boss_appear_refocus",
}

# **关卡层方法**：实现不在 `module/` 通用代码里，而在 `campaign/**` 的关卡/活动基类中。
# 这类原语**不能**由 C# 复刻（复刻等于把上游的关卡层代码搬进引擎，正是"逐地图适配"被禁止的形态），
# 设备宿主必须把调用委托给**上游 campaign 对象**（它才带这些覆写）。
CAMPAIGN_LAYER = {
    "pick_up_flare": "campaign/campaign_main/campaign_14_base.py",
    "pick_up_light_house": "campaign/campaign_main/campaign_14_base.py",
    "clear_map_items": "campaign/event_20221124_cn/campaign_base.py",
}


def resolve(path: str):
    """`module.Class.method` → (类对象, 方法对象)；解析不了返回 (None, None)。"""
    parts = path.split(".")
    for split in range(len(parts) - 1, 0, -1):
        module_name = ".".join(parts[:split])
        try:
            module = importlib.import_module(module_name)
        except Exception:            # noqa: BLE001 —— 导入失败就是"解析不了"，原因由调用方报告
            continue
        owner = module
        for attribute in parts[split:-1]:
            owner = getattr(owner, attribute, None)
            if owner is None:
                return None, None
        method = getattr(owner, parts[-1], None)
        return owner, method
    return None, None


def csharp_primitives() -> list[str]:
    """从 C# 侧的期望清单取原语名（与 verify_r5_execution 用同一份，避免两处漂移）。"""
    sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
    from verify_r5_execution import EXPECTED_PRIMITIVES  # noqa: PLC0415
    return list(EXPECTED_PRIMITIVES)


def main() -> int:
    if not UPSTREAM.is_dir():
        raise SystemExit(f"缺少上游快照 {UPSTREAM}（或设置 ALAS_REPO）")
    if str(UPSTREAM) not in sys.path:
        sys.path.insert(0, str(UPSTREAM))

    problems: list[str] = []
    rows: list[tuple[str, str, str]] = []
    for primitive in csharp_primitives():
        if primitive in CAMPAIGN_LAYER:
            origin = CAMPAIGN_LAYER[primitive]
            if not (UPSTREAM / origin).is_file():
                problems.append(f"{primitive}: 关卡层来源文件不存在：{origin}")
                continue
            text = (UPSTREAM / origin).read_text(encoding="utf-8")
            if f"def {primitive}(" not in text:
                problems.append(f"{primitive}: {origin} 里找不到 def {primitive}(")
                continue
            rows.append((primitive, f"关卡层 {origin}", "委托上游 campaign 对象执行（C# 不复刻）"))
            continue
        seam = SEAM.get(primitive)
        if seam is None:
            rows.append((primitive, "（无）", "上游没有对应方法 → 设备动作必须在 C# 侧实现"))
            continue
        owner, method = resolve(seam)
        if method is None:
            problems.append(f"{primitive}: 接缝方法不存在或不是可导入的类方法：{seam}")
            continue
        # 必须定义在该类里：避免"名字对上了，其实继承自别处"的假接缝
        if inspect.isfunction(method) and method.__qualname__.rsplit(".", 1)[0] != owner.__name__:
            problems.append(f"{primitive}: {seam} 不是定义在 {owner.__name__} 里（实际 {method.__qualname__}）")
            continue
        signature = str(inspect.signature(method)).replace("(self, ", "(")
        rows.append((primitive, seam, signature))

    delegated = [row for row in rows if row[1].startswith("module.")]
    campaign_layer = [row for row in rows if row[1].startswith("关卡层")]
    csharp_only = [row for row in rows if row[1] == "（无）"]

    print(f"[r5-seam] C# 原语 {len(rows)} 个：委托上游通用方法 {len(delegated)}，"
          f"委托上游关卡层 {len(campaign_layer)}，必须 C# 侧实现 {len(csharp_only)}")
    for primitive, seam, detail in rows:
        print(f"  {primitive:<28}{seam:<52}{detail}")
    if csharp_only:
        print("\n[需在 C# 侧实现设备动作]")
        for primitive, _, detail in csharp_only:
            print(f"  {primitive:<28}{detail}")
    if campaign_layer:
        print("\n[必须委托上游 campaign 对象（关卡层覆写，C# 复刻即为逐地图适配）]")
        for primitive, seam, _ in campaign_layer:
            print(f"  {primitive:<28}{seam}")
    if problems:
        print(f"\nFAIL: {len(problems)} 个接缝问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("\nPASS: 每个 C# 原语都能追到上游执行方法（通用 / 关卡层）或明确标为需 C# 侧实现")
    return 0


if __name__ == "__main__":
    sys.exit(main())
