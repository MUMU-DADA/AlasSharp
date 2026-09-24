# -*- coding: utf-8 -*-
"""按上游页面图批量验证页面识别（一次进程内跑多段导航）。

三个安全约束（都是踩过坑换来的）：

1. **起点页必须在屏幕上**。`asset_button_center` 用的是资产里写死的坐标，不做检测；
   在错误的页面上盲点会点到别的东西（曾误点出"个人信息"页）。
   起点不对先 GOTO_MAIN 回主界面，回不去就 BLOCKED 跳过。
2. **同分数的变体择优**。本机模拟器跑的是新版主界面，`ui/MAIN_GOTO_X` 旧模板早已不在
   （实测 score ≤ 0.25），真正在屏上的是 `ui_white/MAIN_GOTO_X_WHITE`（0.94~0.997）。
   点了旧坐标只会点到空气。所以对每个按钮同时评估白版变体，取分数高的那个。
3. **分数用上游 match 自身二分反解**（见 alas_vision.op_button_match），不复制算法。

段定义从环境变量 SEGMENTS 读入 JSON：
  [{"from": "page_main", "button": "ui/MAIN_GOTO_REWARD", "expect": "page_reward"}]
"""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
import alas_vision as av  # noqa: E402

ADB = os.environ['STUB_ADB']
SERIAL = os.environ.get('SERIAL', '127.0.0.1:16384')
PROBE = os.path.join(HERE, '..', 'data', '_probe.png')
WAIT = float(os.environ.get('TAP_WAIT', '3.0'))
SEGMENTS = json.loads(os.environ['SEGMENTS'])
MIN_SCORE = float(os.environ.get('MIN_SCORE', '0.85'))
RECOVER_FLOOR = float(os.environ.get('RECOVER_FLOOR', '0.20'))


def op(name, **args):
    resp = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(resp.get('error'))
    return resp['result']


def shot():
    subprocess.run([ADB, '-s', SERIAL, 'exec-out', 'screencap', '-p'],
                   stdout=open(PROBE, 'wb'), check=False)
    op('screenshot_load', path=PROBE)
    return sweep()


def sweep():
    return op('ui_rules_sweep')


def tap(x, y):
    subprocess.run([ADB, '-s', SERIAL, 'shell', 'input', 'tap',
                    str(x), str(y)], check=False)


def resolvable(asset):
    """该资产在当前服务器/版本里是否声明过（ui/ 与 ui_white/ 命名并不对称）。"""
    try:
        op('asset_button_center', asset=asset)
        return True
    except Exception:
        return False


def candidates(asset):
    """按钮本身 + 它的白版变体（新版主界面用），只保留真的声明过的资产。

    注意命名不对称：`ui/MAIN_GOTO_MEMORIES` 不存在、只有
    `ui_white/MAIN_GOTO_MEMORIES_WHITE`；而 `ui/DORMMENU_GOTO_*` 只有 ui/ 版。
    所以两侧都要探测，解析不到的候选直接跳过（早期版本会因此整批崩掉）。
    """
    out = []
    name = asset.split('/', 1)[1] if '/' in asset else asset
    for cand in (asset, 'ui_white/%s_WHITE' % name):
        if cand not in out and resolvable(cand):
            out.append(cand)
    if not out:
        raise RuntimeError('资产不可解析（ui/ 与 ui_white/ 两侧都没有）: %s' % asset)
    return out


def pick(asset):
    """返回 (最高分资产, 分数, 实际点击坐标)。

    点击坐标优先用**模板匹配到的实际区域**中心：上游 `Button.button` 属性在 match
    之后返回 `_button_offset`（匹配点），ALAS 的 appear+click 流程点的就是这个位置。
    只点资产里写死的标称坐标会在 ±offset 偏移时点偏——这是实测踩到的。
    """
    best = None
    for cand in candidates(asset):
        m = op('button_match', asset=cand, probe_score=True)
        nominal = op('asset_button_center', asset=cand)['center']
        box = m.get('button_offset')
        point = (round((box[0] + box[2]) / 2), round((box[1] + box[3]) / 2)) if box else tuple(nominal)
        score = m['score'] if m['score'] is not None else -1.0
        if best is None or score > best[1]:
            best = (cand, score, point, tuple(nominal))
    return best


results = []
state = shot()
print('start pages=%s' % (state['pages']['hit'],))

for seg in SEGMENTS:
    src, button, expect = seg['from'], seg['button'], seg['expect']
    # 单段出错不能毁掉整批：累计进度文件写在循环之后，早期版本因一个未捕获的
    # 资产解析异常丢掉了一整批的分析结果。
    try:
        hit = sweep()['pages']['hit']
        if src not in hit:
            if src == 'page_main':
                # 回主界面也走择优，但闸门更低（RECOVER_FLOOR）：GOTO_MAIN 的旧版资产在
                # 新 UI 下只有 ~0.23 分，实测点它的匹配点仍能正确回主界面；用 0.85 会误拒。
                # 分数太低就干脆不动，宁可 BLOCKED 也不盲点。
                chosen, score, point, _ = pick('ui/GOTO_MAIN')
                if score >= RECOVER_FLOOR:
                    tap(*point)
                    time.sleep(WAIT)
                    shot()
                    hit = sweep()['pages']['hit']
                if src not in hit:
                    print('%-36s BLOCKED cannot return to main (GOTO_MAIN best %s %.4f)'
                          % (button, chosen, score))
                    results.append({'button': button, 'expect': expect,
                                    'verdict': 'blocked-no-return', 'observed': hit})
                    continue
            if src not in hit:
                print('%-36s BLOCKED from=%s not on screen (now %s)'
                      % (button, src, hit))
                results.append({'button': button, 'expect': expect,
                                'verdict': 'blocked', 'observed': hit})
                continue
        chosen, score, point, nominal = pick(button)
        tap(*point)
        time.sleep(WAIT)
        observed = shot()['pages']['hit']
        if expect in observed:
            verdict = 'OK'
        elif observed == hit:
            verdict = 'NG-nochange'
        else:
            verdict = 'NG-different'
        flag = '' if score >= MIN_SCORE else '  [low-score %.4f]' % score
        if point != nominal:
            flag += '  [matched %s vs nominal %s]' % (point, nominal)
        print('%-36s -> %-13s expect=%-22s got=%-40s via %s %.4f%s'
              % (button, verdict, expect, observed, chosen, score, flag))
        results.append({'button': button, 'chosen': chosen, 'score': round(score, 4),
                        'tap': list(point), 'nominal': list(nominal),
                        'expect': expect, 'verdict': verdict,
                        'observed': observed, 'before': hit})
    except Exception as e:
        print('%-36s ERROR %s: %s' % (button, type(e).__name__, e))
        try:
            now = sweep()['pages']['hit']
        except Exception:
            now = None
        results.append({'button': button, 'expect': expect, 'verdict': 'error',
                        'error': '%s: %s' % (type(e).__name__, e), 'observed': now})

ok = sum(1 for r in results if r['verdict'] == 'OK')
print()
print('batch: OK %d / %d' % (ok, len(results)))
out = os.path.join(HERE, '..', 'data', 'page_verify_batch.json')
with open(out, 'w', encoding='utf-8') as f:
    json.dump(results, f, ensure_ascii=False, indent=2, default=str)
print('detail written to %s' % os.path.abspath(out))

# 累计进度写进 docs/（版本库内，作为证据），批次明细留在 data/（运行期产物）
prog_path = os.path.join(HERE, '..', 'docs', 'archive/reports/page-verification.json')
try:
    with open(prog_path, encoding='utf-8') as f:
        prog = json.load(f)
except Exception:
    prog = {'verified': {}, 'blocked': {}}
for r in results:
    key = r['expect']
    if r['verdict'] == 'OK':
        prog['verified'][key] = r.get('observed')
        prog['blocked'].pop(key, None)
    elif key not in prog['verified']:
        # setdefault：不要用"NG-xxx"这类机械结论覆盖人工记录的阻塞原因
        # （游戏状态阻塞不是识别缺陷，原因才是重点）
        prog['blocked'].setdefault(key, r['verdict'])
with open(prog_path, 'w', encoding='utf-8') as f:
    json.dump(prog, f, ensure_ascii=False, indent=2, sort_keys=True, default=str)
print('cumulative: verified %d, blocked %d -> %s'
      % (len(prog['verified']), len(prog['blocked']), os.path.abspath(prog_path)))
print('verified pages: %s' % (sorted(prog['verified']),))
print('blocked pages: %s' % (sorted(prog['blocked']),))
