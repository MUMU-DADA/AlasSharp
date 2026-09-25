#!/usr/bin/env python3
"""R5 宿主外驱 seam 核对：`s3_campaign_init` / `s3_campaign_call` / `s3_campaign_info`（离线，无设备）。

用法：
    python tools/diagnostics/verify_r5_host_seam.py

背景（一次自查纠错）：
    我一度新写了一个 `s3_campaign_driver.py` 去做"C# 决定原语、上游方法执行"，随后发现**宿主里早就有
    这套 seam**：`s3_campaign_init`（构造实例 + 种一帧）、`s3_campaign_call`（调用实例上任意方法，
    支持 `@属性` 引用、危险前缀联锁、`CampaignEnd` 走结果合同分类）、`s3_campaign_info`（只读状态，
    含关卡进度）。重复维护两套外驱机制违反项目纪律，**已删除自建模块**，改为核对既有 seam。

核对内容（把替身注入 `_CAMPAIGN`，全部离线）：
  1. 三个 op 都注册在 `OPS` 里；
  2. **安全联锁**：`execute_a_battle` 这类危险前缀方法，不带 `allow_actions` 必须被**拒绝**并说明理由；
  3. `@属性` 引用解析：参数写成 `@ENTRANCE` 时传进去的是实例上那个对象（JSON 传不过来的对象靠这条通道）；
  4. `CampaignEnd` 走**结果合同**分类：结束信号返回的是合同字段（不是裸异常）；
  5. `info` 只读：报告初始化状态与关卡进度字段，不碰游戏状态。

只读：不连设备、不改游戏状态（替身只替换 I/O，挂的是上游自己的方法对象，见 `s3_stub_campaign.py`）。
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

import alas_vision as av  # noqa: E402
from module.campaign.campaign_base import CampaignBase  # noqa: E402
from s3_stub_campaign import NativeRunCampaign  # noqa: E402

REQUIRED_OPS = ("s3_campaign_init", "s3_campaign_call", "s3_campaign_info")


def main() -> int:
    problems: list[str] = []

    # 1) op 注册
    for op in REQUIRED_OPS:
        if op not in av.OPS:
            problems.append(f"{op} 未注册在 OPS 里")

    # 2) 安全联锁：危险前缀必须显式放行
    instance = NativeRunCampaign()
    av._CAMPAIGN['obj'] = instance
    refused = av.op_s3_campaign_call({'name': 'execute_a_battle'})
    if not refused.get('refused'):
        problems.append(f"不带 allow_actions 调 execute_a_battle 应被拒绝，实际 {refused}")
    elif 'allow_actions' not in str(refused.get('reason', '')):
        problems.append("拒绝理由里应说明需要 allow_actions")

    # 3) @属性 引用解析（用替身上无副作用的记录方法验证）
    seen: dict[str, object] = {}
    instance.record_argument = lambda value: seen.setdefault('value', value)
    av.op_s3_campaign_call({'name': 'record_argument', 'args': ['@ENTRANCE']})
    if seen.get('value') is not instance.ENTRANCE:
        problems.append(f"`@ENTRANCE` 应解析成实例上的对象，实际 {seen.get('value')!r}")

    # 3b) 格参数引用：`#<节点>` / `#grids:[...]` / `#roads:[[...]]` 都要用**上游自己的对象类型**。
    # 用**真实 `CampaignMap`**（离线 load_map_data）而不是假字典：`RoadGrids` 会按格子的属性做匹配，
    # 假对象（哪怕是字符串）过不了它自己的校验——实测踩过。
    from module.map.map_base import CampaignMap  # noqa: PLC0415
    from module.map.map_grids import RoadGrids, SelectedGrids  # noqa: PLC0415

    campaign_map = CampaignMap('probe')
    campaign_map.shape = 'G1'
    campaign_map.map_data = 'SP -- -- -- -- ME MB'
    campaign_map.load_map_data()
    instance.map = campaign_map
    resolved: dict[str, object] = {}
    instance.record_grid = lambda value, roadblocks=None: resolved.update(value=value, roads=roadblocks)
    av.op_s3_campaign_call({'name': 'record_grid', 'args': ['#C1']})
    if str(resolved.get('value')) != 'C1':   # `location` 是元组坐标，`str(grid)` 才是节点名
        problems.append(f"`#C1` 应经 node2location 取到 C1 格，实际 {resolved.get('value')!r}")
    av.op_s3_campaign_call({'name': 'record_grid', 'args': ['#grids:[C1,D1]']})
    if not isinstance(resolved.get('value'), SelectedGrids) or len(resolved['value']) != 2:
        problems.append(f"`#grids:[C1,D1]` 应构造 SelectedGrids，实际 {resolved.get('value')!r}")
    av.op_s3_campaign_call({
        'name': 'record_grid',
        'args': ['#C1'],
        # 道路这一段宿主按 JSON 解析，节点名带引号；层级是"道路 → block → 格子"
        # （`[[["C1","D1"]]]` = 一条道路、一个 block、两个格子）
        'kwargs': {'roadblocks': '#roads:[[["C1","D1"]]]'},
    })
    roads = resolved.get('roads')
    if not (isinstance(roads, list) and roads and isinstance(roads[0], RoadGrids)):
        problems.append(f"道路参数应构造 list[RoadGrids]，实际 {roads!r}")
    # kwargs 必须按关键字传（不折算成位置参数）
    captured: dict[str, object] = {}
    instance.record_kwargs = lambda **kwargs: captured.update(kwargs)
    av.op_s3_campaign_call({'name': 'record_kwargs', 'kwargs': {'preserve': 1}})
    if captured.get('preserve') != 1:
        problems.append(f"kwargs 应按关键字传递，实际 {captured}")

    # 4) CampaignEnd 走结果合同分类（替身这次出击会抛 CampaignEnd）
    ended = av.op_s3_campaign_call({'name': 'execute_a_battle', 'allow_actions': True})
    contract_fields = {'cleared', 'withdrawn', 'outcome'}
    if not contract_fields & set(ended):
        problems.append(f"CampaignEnd 应返回结果合同字段，实际 {sorted(ended)}")

    # 5) info 只读
    info = av.op_s3_campaign_info({})
    if not info.get('initialized'):
        problems.append(f"info 应报告 initialized=True，实际 {info}")
    elif 'map_clear_percentage' not in (info.get('map_progress') or {}):
        problems.append(f"info 应包含关卡进度字段（map_progress.map_clear_percentage），实际 {sorted(info)}")

    # 6) 调用的是上游自己的方法（防"手工复刻"）
    bound = getattr(instance, 'execute_a_battle')
    if getattr(bound, '__func__', None) is not CampaignBase.execute_a_battle:
        problems.append("替身挂的 execute_a_battle 不是上游 CampaignBase.execute_a_battle 本身")

    print(f"[r5-host-seam] op 注册：{', '.join(REQUIRED_OPS)}")
    print(f"[r5-host-seam] 联锁拒绝={refused.get('refused')}；结束字段={sorted(contract_fields & set(ended))}；"
          f"info 进度字段={'map_clear_percentage' in (info.get('map_progress') or {})}")
    av._CAMPAIGN['obj'] = None
    if problems:
        print(f"FAIL: {len(problems)} 个问题")
        for item in problems:
            print(f"  - {item}")
        return 1
    print("PASS: 既有宿主 seam 具备联锁、@引用、结束合同分类与只读状态；调用的是上游自己的方法")
    return 0


if __name__ == "__main__":
    sys.exit(main())
