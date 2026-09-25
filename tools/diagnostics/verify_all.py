# -*- coding: utf-8 -*-
"""统一验收入口。

--docs-only：执行离线检查并重建归档报告，跳过设备步骤。
--device-only：仅执行设备步骤。省略模式参数则运行全套（包含设备动作）。
每步独立记录结果，任何失败、超时或缺失都会令最终退出码非零。
"""
import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))     # csharp/
DOCS = os.path.join(ROOT, 'docs')
DATA = os.path.join(ROOT, 'data')
PY = sys.executable

# 本机控制台默认 GBK，打印子进程输出里的替换字符（U+FFFD）会直接抛
# UnicodeEncodeError 把整轮验收打断。统一按 UTF-8 + errors=replace 输出。
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

# (脚本, 说明, 需要真机, 超时秒)
# verify_pages.py / verify_page.py 均为退役入口，不再执行逐页点击。
# 页面产品回归由 regress_pages.py 通过原生任务执行。
STEPS = [
    ('verify_privacy.py', '隐私边界（个人目录/明确凭据/本机工件不入库）', False, 120),
    ('verify_architecture.py', '整体架构边界（宿主/数据/路径/禁止地图特例）', False, 120),
    ('regress_pages.py', '页面识别全量回归（产品导航器）', True, 1800),
    ('retry_blocked_pages.py', '此前阻塞的页面定向重试', True, 1800),
    ('verify_controls.py', '控件历史证据归档（旧逐页动作脚本已退役）', False, 120),
    ('verify_primitives.py', '控制原语（返回键/长按/滑动）', True, 1200),
    ('verify_text_input.py', '文本输入（装备码流程）', True, 900),
    ('verify_positive_control.py', '合成正对照（页面 + Switch）', False, 600),
    ('verify_map_ir.py', 'S2 地图模型跨语言对照（C# 解析 vs 上游活对象）', False, 900),
    ('verify_map_detection.py', 'S2 地图识别（素材链/单应性/负样本）', False, 600),
    ('verify_map_detect_failures.py', 'S2 地图检测故障分类与 OS 遮罩复位（离线）', False, 120),
    ('verify_product_map.py', 'S2 产品路径（Alas.Server map + 关卡 IR 交叉校验）', False, 900),
    ('verify_config_export.py', '章节 Config 导出（继承/表达式/类型证据）', False, 300),
    ('verify_map_export.py', 'MAP 声明导出（符号格子/类/复制/未知语义）', False, 300),
    ('verify_campaign_export.py', 'Campaign 类声明导出（引用/别名/类型/未知语义）', False, 300),
    ('verify_export_integrity.py', '导出完整性破坏用例：Python/C# 同时拒绝', False, 300),
    ('verify_validation_contracts.py', '同步验收与地图失败分类反例（离线）', False, 120),
    ('verify_map_alignment.py', 'S2 偏移对齐（窗口 vs 地图，含活动图 9x8）', False, 600),
    ('analyze_specificity.py', '识别特异性矩阵', False, 300),
    ('report_pages.py', '重建 page-verification.md', False, 300),
    ('../sync_all.py', '上游同步一致性（导出数据/素材与上游对齐）', False, 600),
    ('verify_device_engine.py', '设备引擎回归（后端可切换/抓图/点击，需设备在线）', True, 600),
    ('verify_device_capture_color.py', '设备抓帧 raw/普通路径像素通道一致（离线）', False, 120),
    ('verify_native_ui_ensure.py', '上游原生页面导航宿主合同（动作门禁/复用设备/异常）', False, 120),
    ('verify_native_page_rules.py', '全部页面四服原生判据与服务器素材释放（离线）', False, 120),
    ('verify_native_ocr.py', 'OCR 原生字色/阈值/白名单/四服预处理与 C# 参数合同（离线）', False, 300),
    ('verify_ui_rule_catalog.py', '上游控件继承/别名/延迟构造发现与错误合同（离线）', False, 120),
    ('verify_upstream_coverage.py', '全部章节/素材/页面/导航/控件/调度入口；源缺陷也失败', False, 900),
    ('verify_native_tools.py', '上游独立工具原生分派与队列工件（离线）', False, 300),
    ('verify_native_dispatch_catalog.py', '全部周期任务与独立工具的原生分派、配置绑定和重试写入（离线）', False, 300),
    ('verify_native_tool_devices.py', '全部独立工具原生构造器复用会话设备与错误检测恢复（离线）', False, 120),
    ('verify_native_task_devices.py', '原生嵌套任务设备复用及逐任务检测状态恢复（离线）', False, 120),
    ('verify_native_runtime_compat.py', '新宿主周期任务/连续调度/独立工具的原生数值兼容（离线）', False, 120),
    ('verify_native_campaign_runtime.py', '原生任务战役加载/舰队/相机/结算兼容与作用域恢复（离线）', False, 120),
    ('verify_native_scheduler.py', '上游连续调度与边界停止（离线）', False, 300),
    ('verify_scheduler_hoarding.py', '原生囤积状态按调度运行隔离及异常恢复（离线）', False, 120),
    ('verify_scheduler_evidence.py', '真实连续调度边界停止证据及反例（离线）', False, 120),
    ('verify_instance_device_binding.py', '实例配置与共享设备绑定（离线）', False, 120),
    ('verify_screenshot_auto.py', '原生截图 auto 解析与跨任务配置重绑（离线）', False, 120),
    ('verify_device_back.py', '导航返回键（上游设备接口与失败透传）', False, 120),
    ('device_smoke.py', '真机冒烟收口（当场抓帧 / IN_MAP 现场取值 / 有界战役冒烟）', True, 1800),
    ('verify_device_smoke.py', '真机冒烟证据审计（退出码/缺工件/报告异常/通关反例，无设备）', False, 120),
    ('verify_dryrun_purity.py', 'dry-run 纯度（不带 --run 绝不碰游戏）', False, 600),
    ('verify_s3_plan.py', 'S3 计划读取回归（协议 plan_steps == IR battle_* + 安全锁）', False, 300),
    ('verify_s3_upstream_loading.py', 'S3 上游加载链/继承配置/地图帧回归（离线）', False, 300),
    ('verify_native_campaign_entry.py', 'S3 原生入口处理与存盘帧对照（无设备）', False, 120),
    ('verify_s3_camera_compat.py', 'S3 原生相机等待空状态兼容（离线）', False, 300),
    ('verify_campaign_button_compat.py', 'S3 原生战役按钮颜色临界值与模板对拍（脱敏局部帧）', False, 300),
    ('verify_s3_outcome.py', 'S3 原生 run 调度与清图/撤退判别（离线上游执行）', False, 300),
    ('verify_result_contract.py', 'R0 结果合同（四类判别 + 反例拒绝 + 跨语言对拍）', False, 300),
    ('audit_real_records.py', 'R0 实机记录重新核对（重建 result-evidence.md）', False, 300),
    ('verify_real_records.py', 'R0 原始工件证据链（缺失/篡改/合同与会话分歧）', False, 300),
    ('audit_queue_evidence.py', '真实队列工件交叉核对（重建 queue-evidence.md）', False, 120),
    ('verify_queue_evidence.py', '队列证据反例（缺工件/校验和/时长/目标页/会话/断点）', False, 120),
    ('verify_runtime.py', 'R1 常驻运行时（会话只初始化一次 / 失败即停 / 取消 / 证据）', False, 300),
    ('verify_artifact_paths.py', 'R1 相对工件、断点和停止文件路径', False, 300),
    ('verify_account_state.py', 'R2 账号状态域（只读状态任务 + 真机帧 + 临界判据留证）', False, 600),
    ('verify_account_state_cache.py', 'R2 任务边界使用原生设备缓存并保留帧来源（无设备）', False, 120),
    ('verify_stop.py', 'R4 停止任务（--stop-file 在任务边界生效 + 对照）', False, 300),
    ('verify_report_html.py', 'R4 HTML 视图（不丢事实 / 单文件自足 / 缺工件也能看）', False, 600),
    ('verify_report.py', 'R2 运行报告（工件→结构化事实 + 证据完整性反例）', False, 600),
    ('verify_control.py', 'R4 本地控制原型（真实 HTTP / dry-run / 授权 / 边界停止）', False, 300),
    ('verify_control_shutdown.py', 'R4 Kestrel 关闭（拒绝延迟请求 / 边界取消 / 工件落盘）', False, 180),
    ('verify_control_client.py', 'R4 共享客户端（真实 HTTP / 无反射 JSON / 取消与重试边界）', False, 300),
    ('verify_control_events.py', 'R4 状态流（游标 / 日志尾部 / 慢订阅 / 关闭 / 工件共享读取）', False, 180),
    ('verify_r5_selection.py', 'R5 目标选择对拍（C# 移植 vs 上游 Filter，离线无设备）', False, 180),
    ('verify_r5_execution.py', 'R5 计划→原语→动作闭环（干跑记录 vs 上游选敌规则）', False, 180),
    ('verify_r5_loop.py', 'R5 关卡循环（run/execute_a_battle/battle_function vs 上游钩子选择规则）', False, 180),
    ('verify_r5_path.py', 'R5 寻路成本场（vs 上游 find_path_initial / _find_path，逐格对拍）', False, 180),
    ('verify_r5_shadow.py', 'R5 影子模式（C# 只算不执行 vs 上游实际运行日志）', False, 180),
    ('verify_r5_actions.py', 'R5 原语级动作轨迹（上游日志 → 原语名 + C# 覆盖对照）', False, 180),
    ('verify_r5_state.py', 'R5 识别结果→引擎状态（声明地图 + 识别叠加 + 成本场）', False, 180),
    ('verify_r5_run.py', 'R5 端到端干跑（帧/识别 → 状态 → 关卡循环 → 原语动作）', False, 600),
    ('verify_r5_diff.py', 'R5 原语动作层对照（上游日志动作 vs C# 干跑动作，含目标一致判定）', False, 600),
    ('verify_r5_switch.py', 'R5 域级开关（默认不改行为 / csharp 需二次闸门 / 非法值退回）', False, 180),
    ('verify_r5_seam.py', 'R5 接缝表（C# 原语 ↔ 上游执行方法，含关卡层归属）', False, 180),
    ('verify_r5_host_seam.py', 'R5 宿主外驱 seam（s3_campaign_init/call/info：联锁、@引用、结束分类）', False, 180),
    ('verify_os_state.py', 'R2 大世界/海域只读探针（存盘帧；capture 前置条件口径）', False, 600),
    ('verify_os_action.py', 'R2 大世界动作入口（真实 CLI dry-run 拒绝动作并留工件）', False, 300),
    ('verify_os_combat_reentry.py', 'R2 大世界自动寻敌跳过准备画面后重新接管战斗', False, 120),
    ('button_threshold_sweep.py', '判据临界扫描（重建 button-threshold-sweep.md）', False, 900),
    ('r3_hook_shapes.py', 'R3 钩子形态分类（重建 r3-hook-shapes.md）', False, 300),
    ('r3_map_data_init.py', 'R3 第一项上游轨迹（map_data_init 逐章形态）', False, 300),
    ('r3_candidates.py', 'R3 候选排序（原生钩子覆盖，重建 r3-candidates.md）', False, 300),
    ('verify_in_map_shim.py', 'IN_MAP 阈值垫片（上游判不出 / 垫片后判得出 / 没无脑放宽）', False, 300),
    ('verify_device_backends.py', '截图后端对照(须过三条成功判据; 耗时只在帧有效时计入)', True, 900),
    ('verify_cli_errors.py', 'CLI 错误路径与退出码契约（0/1/2 三档 + 文案可读）', False, 600),
    ('verify_cli_evidence.py', 'CLI [任务证据] 行（战役/账号状态/大世界三域）', False, 600),
    ('verify_config_get.py', '配置开关域（独立对拍 + 缺失≠false + 空输入记 skipped）', False, 300),
    ('verify_periodic_plan.py', 'R2 周期任务勘察（独立对拍 + 边界 + 不 import 目标模块）', False, 300),
    ('verify_periodic_overrides.py', 'R2 一次性覆盖复用原生字段校验与值转换（无设备）', False, 120),
    ('verify_periodic_run_result.py', 'R2 周期任务/大世界原生分派响应一致性与失败断点（无设备）', False, 120),
    ('verify_task_schedule.py', 'R2 周期任务调度状态（独立对拍 + 四种边界 + 只读保证）', False, 600),
    ('verify_task_catalog.py', '周期任务域数据源（上游任务目录两个来源可读/差异如实标注）', False, 300),
    ('verify_event_state.py', 'R2 活动域清点（与独立数对拍 / only_complete / 空集口径）', False, 300),
    ('s3_plan_inventory.py', 'S3 计划词表清点（重建 s3-plan-vocabulary.md）', False, 300),
    ('status.py', '重建 status.md', False, 300),
]


def run_step(script, need_device, timeout, docs_only=False, device_only=False):
    if docs_only and need_device:
        return 'skipped', 0.0, '--docs-only'
    if device_only and not need_device:
        return 'skipped', 0.0, '--device-only'
    path = os.path.join(HERE, script)
    if not os.path.exists(path):
        return 'missing', 0.0, path
    log = Path(ROOT) / '.runtime/verification/verify_all' / (Path(script).stem + '.log')
    log.parent.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    try:
        r = subprocess.run([PY, path], capture_output=True, text=True,
                           encoding='utf-8', errors='replace', timeout=timeout)
        log.write_text((r.stdout or '') + '\n--- stderr ---\n' + (r.stderr or ''), encoding='utf-8')
        ok = r.returncode == 0
        output = (r.stderr or r.stdout or '') if not ok else (r.stdout or '')
        tail = [l for l in output.strip().splitlines() if l.strip()]
        return ('ok' if ok else 'fail'), time.time() - t0, (tail[-1][:120] if tail else '')
    except subprocess.TimeoutExpired as error:
        def output_text(value):
            return value.decode('utf-8', 'replace') if isinstance(value, bytes) else (value or '')
        log.write_text(output_text(error.stdout) + '\n--- stderr ---\n'
                       + output_text(error.stderr) + '\nTimed out after %d seconds.\n' % timeout,
                       encoding='utf-8')
        return 'timeout', time.time() - t0, '超过 %ds' % timeout
    except Exception as e:
        return 'error', time.time() - t0, '%s: %s' % (type(e).__name__, e)


def read_numbers():
    """从各证据文件读关键数字，用于总表。"""
    def load(p, d=None):
        try:
            with open(p, encoding='utf-8') as f:
                return json.load(f)
        except Exception:
            return d
    prog = load(os.path.join(DOCS, 'archive/reports/page-verification.json'), {}) or {}
    reg = load(os.path.join(DATA, 'regress_pages.json'), []) or []
    ctrl = load(os.path.join(DATA, 'controls_verify.json'), []) or []
    prim = load(os.path.join(DATA, 'primitives_verify.json'), []) or []
    text = (load(os.path.join(DATA, 'text_input_verify.json'), {}) or {}).get('results', [])
    pc = load(os.path.join(DATA, 'positive_control.json'), {}) or {}
    rc = load(os.path.join(DATA, 'rule_positive_control.json'), {}) or {}
    return {
        '页面命中': '%d/%d' % (len(prog.get('verified', {})), 53),
        '页面阻塞': len(prog.get('blocked', {})),
        '回归': '%d/%d' % (sum(1 for x in reg if x['verdict'] == 'ok'), len(reg)),
        '控件命中': sum(1 for x in ctrl if x['verdict'] == 'hit'),
        '原语': '%d/%d' % (sum(1 for x in prim if x['verdict'] == 'hit'), len(prim)),
        '文本输入': '%d/%d' % (sum(1 for x in text if x['verdict'] == 'hit'), len(text)),
        '页面正对照': '%d/%d' % (pc.get('passed', 0), pc.get('total', 0)),
        'Switch 正对照': '%d 通过 / %d 跳过 / 共 %d'
                         % (rc.get('passed', 0), rc.get('skipped', 0), rc.get('total', 0)),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--docs-only', action='store_true')
    modes.add_argument('--device-only', action='store_true')
    parser.add_argument('--only', help='逗号分隔的已登记检查名')
    try:
        args = parser.parse_args()
    except SystemExit as error:
        return error.code
    docs_only, device_only = args.docs_only, args.device_only
    only = None if args.only is None else {s.strip() for s in args.only.split(',') if s.strip()}
    if only is not None and (not only or only - {s[0] for s in STEPS}):
        print('--only 包含未知或空检查名')
        return 2
    mode = '离线检查与归档报告' if docs_only else ('只跑真机' if device_only else '全套')
    if only:
        mode += '，只跑 %s' % ', '.join(sorted(only))
    print('=== 全套验收（%s）===' % mode)
    results = []
    t_all = time.time()
    for script, desc, need_dev, timeout in STEPS:
        if only and script not in only:
            results.append((script, desc, 'skipped', 0.0))
            continue
        state, secs, note = run_step(script, need_dev, timeout, docs_only, device_only)
        print('%-28s %-8s %6.1fs  %s' % (script, state, secs, note), flush=True)
        results.append((script, desc, state, secs))

    print()
    print('--- 证据数字 ---')
    for k, v in read_numbers().items():
        print('%-14s %s' % (k, v))

    bad = [r for r in results if r[2] in ('fail', 'timeout', 'error', 'missing')]
    summary = Path(ROOT) / '.runtime/verification/verify_all/results.json'
    summary.parent.mkdir(parents=True, exist_ok=True)
    summary.write_text(json.dumps(dict(mode=mode, ok=not bad, results=[
        dict(script=r[0], description=r[1], state=r[2], seconds=round(r[3], 3))
        for r in results]), ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print()
    print('总耗时 %.1f 分钟；%d 步异常%s'
          % ((time.time() - t_all) / 60, len(bad),
             ('：' + ', '.join('%s(%s)' % (r[0], r[2]) for r in bad)) if bad else ''))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
