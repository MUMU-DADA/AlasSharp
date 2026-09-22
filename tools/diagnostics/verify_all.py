# -*- coding: utf-8 -*-
"""一条命令跑完整套验收（识别 + 控件 + 原语 + 正对照 + 文档刷新）。

为什么需要它：这套验证现在有 8 个脚本、5 份证据文件。手工按顺序跑容易漏、顺序错了
还会互相污染（比如控件验证依赖导航器，正对照又要在真截图上下文中运行）。
更需要的是**外部条件一变就能一键重跑**：活动开跑了、账号解锁了新功能、
或者换了客户端版本，跑这一条就知道哪些从"到不了"变成了"跑通"。

用法：
    python verify_all.py                # 全套（含真机，约 20 分钟）
    python verify_all.py --docs-only    # 只用现有证据重建文档（几秒）
    python verify_all.py --device-only  # 只跑真机部分，不重建文档

每一步失败都不会中断整轮：各自 try/except 并记录，最后给一张总表。
"""
import json
import os
import subprocess
import sys
import time

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
STEPS = [
    ('verify_architecture.py', '整体架构边界（宿主/数据/路径/禁止地图特例）', False, 120),
    ('regress_pages.py', '页面识别全量回归（产品导航器）', True, 1800),
    ('retry_blocked_pages.py', '此前阻塞的页面定向重试', True, 1800),
    ('verify_controls.py', '控件规则 + 滑动/开关驱动', True, 2400),
    ('verify_primitives.py', '控制原语（返回键/长按/滑动）', True, 1200),
    ('verify_text_input.py', '文本输入（装备码流程）', True, 900),
    ('verify_positive_control.py', '合成正对照（页面 + Switch）', False, 600),
    ('verify_map_detection.py', 'S2 地图识别（素材链/单应性/负样本）', False, 600),
    ('verify_product_map.py', 'S2 产品路径（alashub map + 关卡 IR 交叉校验）', False, 900),
    ('verify_config_export.py', '章节 Config 导出（继承/表达式/类型证据）', False, 300),
    ('verify_map_alignment.py', 'S2 偏移对齐（窗口 vs 地图，含活动图 9x8）', False, 600),
    ('analyze_specificity.py', '识别特异性矩阵', False, 300),
    ('report_pages.py', '重建 page-verification.md', False, 300),
    ('../sync_all.py', '上游同步一致性（--verify：导出数据/素材与上游对齐）', False, 600),
    ('verify_device_engine.py', '设备引擎回归（后端可切换/抓图/点击，需设备在线）', True, 600),
    ('device_smoke.py', '真机冒烟收口（当场抓帧 / IN_MAP 现场取值 / 有界战役冒烟）', True, 1800),
    ('verify_dryrun_purity.py', 'dry-run 纯度（不带 --run 绝不碰游戏）', False, 600),
    ('verify_s3_plan.py', 'S3 计划读取回归（协议 plan_steps == IR battle_* + 安全锁）', False, 300),
    ('verify_s3_upstream_loading.py', 'S3 上游加载链/继承配置/地图帧回归（离线）', False, 300),
    ('verify_s3_camera_compat.py', 'S3 原生相机等待空状态兼容（离线）', False, 300),
    ('verify_s3_outcome.py', 'S3 原生 run 调度与清图/撤退判别（离线上游执行）', False, 300),
    ('verify_result_contract.py', 'R0 结果合同（四类判别 + 反例拒绝 + 跨语言对拍）', False, 300),
    ('audit_real_records.py', 'R0 实机记录重新核对（重建 result-evidence.md）', False, 300),
    ('verify_runtime.py', 'R1 常驻运行时（会话只初始化一次 / 失败即停 / 取消 / 证据）', False, 300),
    ('verify_account_state.py', 'R2 账号状态域（只读状态任务 + 真机帧 + 临界判据留证）', False, 600),
    ('verify_stop.py', 'R4 停止任务（--stop-file 在任务边界生效 + 对照）', False, 300),
    ('verify_report.py', 'R2 运行报告（工件→结构化事实 + 证据完整性反例）', False, 600),
    ('verify_os_state.py', 'R2 大世界/海域只读探针（存盘帧；capture 前置条件口径）', False, 600),
    ('button_threshold_sweep.py', '判据临界扫描（重建 button-threshold-sweep.md）', False, 900),
    ('r3_hook_shapes.py', 'R3 钩子形态分类（重建 r3-hook-shapes.md）', False, 300),
    ('r3_map_data_init.py', 'R3 第一项上游轨迹（map_data_init 逐章形态）', False, 300),
    ('r3_candidates.py', 'R3 候选排序（原生钩子覆盖，重建 r3-candidates.md）', False, 300),
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
    t0 = time.time()
    try:
        r = subprocess.run([PY, path], capture_output=True, text=True,
                           encoding='utf-8', errors='replace', timeout=timeout)
        ok = r.returncode == 0
        tail = [l for l in (r.stdout or '').strip().splitlines() if l.strip()]
        return ('ok' if ok else 'fail'), time.time() - t0, (tail[-1][:120] if tail else '')
    except subprocess.TimeoutExpired:
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
    prog = load(os.path.join(DOCS, 'page-verification.json'), {}) or {}
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
    docs_only = '--docs-only' in sys.argv
    device_only = '--device-only' in sys.argv
    only = None
    for i, a in enumerate(sys.argv):
        if a == '--only' and i + 1 < len(sys.argv):
            only = {s.strip() for s in sys.argv[i + 1].split(',') if s.strip()}
    mode = '只重建文档' if docs_only else ('只跑真机' if device_only else '全套')
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
        print('%-28s %-8s %6.1fs  %s' % (script, state, secs, note))
        results.append((script, desc, state, secs))

    print()
    print('--- 证据数字 ---')
    for k, v in read_numbers().items():
        print('%-14s %s' % (k, v))

    bad = [r for r in results if r[2] in ('fail', 'timeout', 'error', 'missing')]
    print()
    print('总耗时 %.1f 分钟；%d 步异常%s'
          % ((time.time() - t_all) / 60, len(bad),
             ('：' + ', '.join('%s(%s)' % (r[0], r[2]) for r in bad)) if bad else ''))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
