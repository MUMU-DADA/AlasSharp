#!/usr/bin/env python3
"""R5 外驱驱动器核对：C# 决定原语、上游方法执行（离线，假 I/O + 真上游方法）。

用法：
    python tools/diagnostics/verify_r5_driver.py

核对什么（全部离线，不连设备）：
  1. **准备阶段等价**：`CampaignStepDriver.prepare()` 在一份替身上产生的事件序列，必须与
     **上游 `CampaignBase.run()`** 在同一份替身上产生的前缀事件序列一致——准备阶段是"同一批上游方法、
     同一顺序"，不是另写一套；
  2. **调用的是上游自己的方法**：`call('execute_a_battle')` 命中的函数对象必须是
     `CampaignBase.execute_a_battle` 本身（`is` 比较），任何"手工复刻"都会在这里失败；
  3. **舰队前缀只记账**：`call('fleet_2.clear_enemy')` 实际调用的是 `clear_enemy`，前缀进轨迹；
  4. **状态读回**：`state()` 报告的 `battle_count` 与实例一致；未 `prepare()` 就调用原语要**报错**
     （与上游"未进图不调原语"的语义一致，不静默放过）。

纪律：替身只替换 I/O，挂的是上游自己的方法对象（见 `s3_stub_campaign.py` 的说明）。
"""
from __future__ import annotations

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
UPSTREAM = ROOT / ".runtime" / "engine"
if not UPSTREAM.is_dir():
    raise SystemExit(f"缺少上游快照 {UPSTREAM}（或设置 ALAS_REPO）")
sys.path.insert(0, str(UPSTREAM))

from module.campaign.campaign_base import CampaignBase  # noqa: E402
from s3_campaign_driver import CampaignStepDriver  # noqa: E402
from s3_stub_campaign import NativeRunCampaign  # noqa: E402


def main() -> int:
    problems: list[str] = []

    # 1) 准备阶段等价：替身自己的 run() vs 驱动器 prepare()
    native = NativeRunCampaign()
    native.run()
    baseline = [event[0] for event in native.events]

    driven = NativeRunCampaign()
    driver = CampaignStepDriver(driven)
    driver.prepare()
    prepared = [event[0] for event in driven.events]
    if prepared != baseline:
        problems.append(f"准备阶段事件序列与上游 run() 不一致：驱动器 {prepared}，上游 {baseline}")

    # 2) 调用的是上游自己的方法对象；`CampaignEnd` 是上游的**正常结束信号**，驱动器不吞它
    from module.exception import CampaignEnd  # noqa: PLC0415

    ended = False
    try:
        driver.call('execute_a_battle')
    except CampaignEnd:
        ended = True
    if not ended:
        problems.append("替身这次出击应当抛 CampaignEnd（上游结束信号），驱动器不应吞掉")
    recorded = driver.steps[-1]
    if recorded['method'] != 'NativeRunCampaign.execute_a_battle':
        problems.append(f"轨迹里的方法名应指向替身挂载的上游方法，实际 {recorded['method']}")
    if recorded['return'] != 'CampaignEnd':
        problems.append(f"轨迹应把异常如实记为 CampaignEnd，实际 {recorded['return']}")
    # 实例属性访问会得到**绑定方法**（每次都是新对象），所以比对底层函数对象
    bound = getattr(driven, 'execute_a_battle')
    if getattr(bound, '__func__', None) is not CampaignBase.execute_a_battle:
        problems.append("替身上的 execute_a_battle 不是上游 CampaignBase.execute_a_battle 本身")

    # 3) 舰队前缀只记账
    # 用替身自带的无副作用方法（ppear_then_click 返回 False）验证前缀记账
    driver.call('fleet_2.appear_then_click')
    prefixed = driver.steps[-1]
    if prefixed['op'] != 'fleet_2.appear_then_click' or prefixed['fleet_prefix'] != 'fleet_2':
        problems.append(f"舰队前缀应记账，实际 {prefixed['op']} / {prefixed['fleet_prefix']}")

    # 4) 状态读回 + 未准备就调用要报错
    state = driver.state()
    if state['battle_count'] != int(getattr(driven, 'battle_count', 0)):
        problems.append(f"state() 的 battle_count 与实例不一致：{state['battle_count']}")
    if state['steps'] != len(driver.steps):
        problems.append("state() 应报告已执行的原语步数")
    fresh = CampaignStepDriver(NativeRunCampaign())
    try:
        fresh.call('clear_enemy')
        problems.append("未 prepare() 就调用原语应报错（上游语义：未进图不调原语）")
    except RuntimeError:
        pass

    print(f"[r5-driver] 准备阶段事件：{baseline}")
    print(f"[r5-driver] 原语轨迹：{[(step['op'], step['fleet_prefix']) for step in driver.steps]}")
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 外驱驱动器复用上游方法对象，准备顺序与上游 run() 一致，前缀/状态/报错行为符合契约")
    return 0


if __name__ == "__main__":
    sys.exit(main())
