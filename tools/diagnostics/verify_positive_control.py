# -*- coding: utf-8 -*-
"""页面规则的**合成正对照**：把每条规则的 check 素材贴到它自己的区域，看规则返回真。

这不是真机命中，口径必须说清楚：
- 真机命中 = 真实游戏画面上规则返回真（`docs/archive/reports/page-verification.md`）；
- 合成正对照 = 把素材自己的模板图贴到它自己的 area 上，规则应当返回真。
  它证明的是"规则是活的"：素材能加载、区域与模板配对正确、判定方向没写反。

为什么值得做：受账号/活动/客户端版本所限，24 个页面在真机上到不了。这些页面到底是
"到不了"还是"规则本身坏了"，光靠真机验证分不清 —— 正对照能把这两件事分开。
正对照过不了的规则，一定是实现问题（素材路径错、模板空、区域写错），必须查。
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
import alas_vision as av  # noqa: E402


def op(name, **args):
    resp = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': name, 'args': args})))
    if not resp.get('ok'):
        raise RuntimeError(resp.get('error'))
    return resp['result']


def main():
    # 正对照在内部构造画布，不读取账号截图，也不操作设备。

    r = op('page_positive_control')
    print('=== 页面规则合成正对照：%d 条 ===' % r['total'])
    print('通过 %d / 跳过 %d / 失败 %d' % (r['passed'], r['skipped'], r['failed']))
    for x in r['results']:
        if x['verdict'] in ('fail', 'error'):
            print('  [%s] %s：%s' % (x['verdict'], x['page'], x['detail']))

    out = os.path.join(HERE, '..', 'data', 'positive_control.json')
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(r, f, ensure_ascii=False, indent=2, default=str)
    print('明细: %s' % os.path.abspath(out))

    # 控件规则（模块级 Switch）的正对照
    rc = op('rule_positive_control')
    print('=== 控件规则正对照：%d 条 ===' % rc['total'])
    print('通过 %d / 跳过 %d / 失败 %d' % (rc['passed'], rc['skipped'], rc['failed']))
    for x in rc['results']:
        if x['verdict'] == 'fail':
            print('  [fail] %s：%s' % (x['rule'], x['detail']))
    out2 = os.path.join(HERE, '..', 'data', 'rule_positive_control.json')
    with open(out2, 'w', encoding='utf-8') as f:
        json.dump(rc, f, ensure_ascii=False, indent=2, default=str)
    print('明细: %s' % os.path.abspath(out2))

    # 与真机验证结果对照：哪些页面"正对照过了但真机到不了"
    with open(os.path.join(HERE, '..', 'docs', 'archive/reports/page-verification.json'),
              encoding='utf-8') as f:
        prog = json.load(f)
    verified = set(prog['verified'])
    blocked = set(prog['blocked'])
    passed = {x['page'] for x in r['results'] if x['verdict'] == 'pass'}
    unreachable_ok = sorted(passed - verified)

    lines = [
        '# 页面规则的合成正对照',
        '',
        '**这不是真机命中。** 真机命中见 `page-verification.md`（覆盖数见 `status.md`）。',
        '正对照的做法是：把每条页面规则的 check 素材**自己的模板图**贴到**它自己的 area** 上，',
        '再跑一次上游的 `ui_page_appear` —— 规则此时应当返回真。',
        '',
        '它证明的是"规则是活的"：素材文件能加载、区域与模板配对正确、判定方向没写反。',
        '',
        '本次清单中有 %d 个页面没有历史真机命中证据。' % len(unreachable_ok),
        '正对照只能证明合成输入上的原生判定；真实画面、导航入口和业务结果仍需各自验证。',
        '',
        '生成：`tools/diagnostics/verify_positive_control.py`；数据 `data/positive_control.json`。',
        '',
        '## 结果',
        '',
        '| 项 | 数量 |',
        '| --- | --- |',
        '| 总计（上游 Page 数） | %d |' % r['total'],
        '| 正对照通过 | **%d** |' % r['passed'],
        '| 跳过（无 check 素材 / 素材区域超出画面） | %d |' % r['skipped'],
        '| 失败 | %d |' % r['failed'],
        '',
        '## 失败项（实现问题，必须查）',
        '',
    ]
    fails = [x for x in r['results'] if x['verdict'] in ('fail', 'error')]
    if fails:
        lines += ['| 页面 | 详情 |', '| --- | --- |']
        for x in fails:
            lines.append('| `%s` | %s |' % (x['page'], x['detail']))
    else:
        lines.append('无。本次 %d 条页面规则返回真，%d 条跳过。' % (r['passed'], r['skipped']))
    lines += [
        '',
        '## 跳过项',
        '',
    ]
    skips = [x for x in r['results'] if x['verdict'] == 'skip']
    if skips:
        lines += ['| 页面 | 原因 |', '| --- | --- |']
        for x in skips:
            lines.append('| `%s` | %s |' % (x['page'], x['detail']))
    else:
        lines.append('无')
    lines += [
        '',
        '## 控件规则（模块级 Switch）的正对照',
        '',
        '做法：对开关的**每个状态**，单独把该状态的 check 素材贴到它自己的区域，',
        '再调上游 `Switch.get()` —— 应当正好返回那个状态名。',
        '',
        '| 项 | 数量 |',
        '| --- | --- |',
        '| 模块级规则总数 | %d |' % rc['total'],
        '| Switch 正对照通过 | **%d** |' % rc['passed'],
        '| 跳过（颜色掩码或子类原生识别流程不适用模板贴图） | %d |' % rc['skipped'],
        '| 失败 | %d |' % rc['failed'],
        '',
        '合成正对照通过的开关：',
        '',
        '| 开关 | 每个状态贴图后的 get() 结果 |',
        '| --- | --- |',
    ]
    for x in rc['results']:
        if x['verdict'] == 'pass':
            lines.append('| `%s` | %s |' % (x['rule'], x['detail']))
    lines += ['', '## 控件跳过或失败', '', '| 规则 | 结果 | 原因 |', '| --- | --- | --- |']
    for x in rc['results']:
        if x['verdict'] != 'pass':
            lines.append('| `%s` | %s | %s |' % (x['rule'], x['verdict'], x['detail']))
    lines += [
        '',
        '## 与真机结果的关系',
        '',
        '正对照通过但没有历史真机命中证据的页面共 %d 个：' % len(unreachable_ok),
        '',
        '、'.join('`%s`' % p for p in unreachable_ok) if unreachable_ok else '（无）',
        '',
        '未覆盖原因需查对应现场证据；不能由合成模板命中推断真实客户端兼容或导航可达。',
        '',
        '## 复现',
        '',
        '```powershell',
        'python tools/diagnostics/verify_positive_control.py',
        '```',
        '',
    ]
    path = os.path.join(HERE, '..', 'docs', 'archive/reports/positive-control.md')
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))
    print('报告: %s' % os.path.abspath(path))
    return 0 if not fails and not rc['failed'] else 1


if __name__ == '__main__':
    sys.exit(main())
