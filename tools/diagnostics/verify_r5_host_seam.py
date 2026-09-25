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
