"""Audit native campaign entry handling without clicking a real device.

Archived frames are optional diagnostic inputs, never fabricated replacements.
All click requests are recorded in memory. Original files/hashes stay unchanged.
"""
import hashlib
import json
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import Mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
import alas_vision as av
from module.base.utils import load_image
from module.campaign.campaign_ui import CampaignUI
from module.exception import CampaignEnd
from module.handler.info_handler import InfoHandler
from module.map.assets import WITHDRAW


def main():
    # Exercise the actual native additional-handler decisions and error boundary.
    for present in (False, True):
        for error in (None, CampaignEnd('Withdraw'), RuntimeError('fixture failure')):
            receiver = SimpleNamespace(appear=Mock(return_value=present),
                                       ensure_no_info_bar=Mock(), withdraw=Mock(side_effect=error))
            try:
                handled = CampaignUI.handle_campaign_ui_additional(receiver)
            except RuntimeError:
                assert present and isinstance(error, RuntimeError)
            else:
                assert not (present and isinstance(error, RuntimeError))
                assert handled is present
            receiver.appear.assert_called_once_with(WITHDRAW, offset=(30, 30))
            assert receiver.withdraw.call_count == int(present)
            assert receiver.ensure_no_info_bar.call_count == int(present)

    host = (ROOT / 'tools/alas_vision.py').read_text(encoding='utf-8')
    assert '_UNFINISHED_RED_BOX' not in host
    assert '_proactive_abort_worker' not in host
    assert 'enter_with_dialog_handler' not in host
    assert "'s3_abort_unfinished':" not in host
    assert 'prepare_campaign_navigation(inst)' in host
    assert "'name': 'ensure_campaign_ui'" in host

    rows = []
    # These are historic evidence labels, not production rule or page dispatch.
    for name in ('_shot_dialog.png', '_after_abort.png', '_live_enter_map_stall.png', '_o49_05.png'):
        path = ROOT / 'data' / name
        if not path.is_file():
            rows.append(dict(frame=name, available=False))
            continue
        before = hashlib.sha256(path.read_bytes()).hexdigest()
        shim = av._make_main_shim(load_image(str(path)))
        clicks = []
        shim.device.click = lambda button: clicks.append(button.name)
        withdraw = bool(shim.appear(WITHDRAW, offset=(30, 30)))
        confirm = bool(InfoHandler.handle_popup_confirm(
            shim, name='PROBE', offset=InfoHandler._popup_offset, interval=0))
        assert before == hashlib.sha256(path.read_bytes()).hexdigest()
        rows.append(dict(frame=name, available=True, sha256=before,
                         withdraw_native=withdraw, popup_confirm_native=confirm,
                         click_intents=clicks))
    output = ROOT / '.runtime/verification/native-campaign-entry.json'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(rows, indent=2) + '\n', encoding='utf-8')
    lines = ['# 原生关卡入口边界复核', '',
             '由 `tools/diagnostics/verify_native_campaign_entry.py` 生成。无设备连接，点击仅记录为内存意图。',
             '上游 `CampaignRun.run()` 先清设备记录并处理上一局在图状态，再执行 `ensure_campaign_ui()` 与 `Campaign.run()`。',
             '`CampaignUI.handle_campaign_ui_additional()` 使用原生 WITHDRAW 素材、`withdraw()` 和 `CampaignEnd` 清理；',
             '`MapOperation.enter_map()` 自带准备、舰队、退役、道具、心情与剧情处理，不并发运行另一套点击流程。', '',
             '已删除宿主固定红色区域/坐标撤退线程及入口覆写。该线程绕开共享设备后端、吞掉异常，',
             '且只等待退出 5 秒，不能保证其截图子进程结束后不会再次点击。冻结结果词表中的旧步骤名保留以读取历史工件。', '',
             '原生 additional-handler 的未命中、成功、CampaignEnd 和其他异常传播共 6 种输入通过。', '',
             '## 存盘帧原生判据对照', '',
             '| 本地证据标签 | SHA-256 | WITHDRAW | 原生确认弹窗 |', '| --- | --- | --- | --- |']
    for row in rows:
        lines.append(f'| `{row["frame"]}` | `{row.get("sha256", "未提供")}` | '
                     f'{row.get("withdraw_native", "未验证")} | {row.get("popup_confirm_native", "未验证")} |')
    lines += ['', '## 尚未验证的行为', '',
              '上述模板判据不证明整条弹窗操作完成；未提供的存盘帧不产生测量结论。']
    residual = next(row for row in rows if row['frame'] == '_o49_05.png')
    if residual['available'] and not residual['withdraw_native'] and not residual['popup_confirm_native']:
        lines.append('本次提供的历史 `_o49_05.png` 残留出击弹窗在原生 WITHDRAW/确认判据中均未命中。')
    lines += ['删除旁路不意味着该客户端状态已被修好。后续必须复核原生撤退和导航后的现场状态及上游支持情况，',
              '不能恢复单页颜色阈值/固定坐标，也不能从其他关卡成功推断它可用。',
              '本次未新增真机通关；原有成功结算与撤退记录不变。', '',
              '脱敏范围：仅导出本地文件标签、内容哈希和布尔判据；不发布图像、账号、绝对路径或原始运行日志。', '']
    (ROOT / 'docs/archive/reports/native-campaign-entry.md').write_text('\n'.join(lines), encoding='utf-8')
    print('PASS: 6 native handler cases; archived-frame predicates recorded separately from live completion')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
