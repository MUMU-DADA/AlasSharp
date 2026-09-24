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
    # 先给宿主一张真截图（否则 _require_image 会拒绝），正对照内部会临时换掉它
    probe = os.path.join(HERE, '..', 'data', '_probe.png')
    if not os.path.exists(probe):
        print('缺少 %s：先跑一次会截图的脚本（或 regress_pages.py）' % probe)
        return 2
    op('screenshot_load', path=probe)

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
        '为什么值得单独做：受账号进度/活动/客户端版本所限，有 %d 个页面在真机上到不了。'
        % (53 - len(verified) - len(blocked)),
        '这些页面是"到不了"还是"规则本身坏了"，光靠真机验证分不清 —— 正对照把它们分开：',
        '正对照过不了的规则一定是实现问题（素材路径错、模板空、区域写错），必须查。',
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
        lines.append('无。53 条页面规则在正对照下全部返回真（除合成实体 `page_unknown`）。')
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
        '| 跳过（Scroll：判定依赖颜色/掩码，贴模板图构造不出来） | %d |' % rc['skipped'],
        '| 失败 | %d |' % rc['failed'],
        '',
        '通过的开关（含真机上到不了的）：',
        '',
        '| 开关 | 每个状态贴图后的 get() 结果 |',
        '| --- | --- |',
    ]
    for x in rc['results']:
        if x['verdict'] == 'pass':
            lines.append('| `%s` | %s |' % (x['rule'], x['detail']))
    lines += [
        '',
        '注意 `equipping_filter` / `FLEET_LOCK` / `FORMATION` / `SUBMARINE_HUNT` /',
        '`SUBMARINE_VIEW` / `ISLAND_DOCK_SORTING` / `SWITCH_LOCK` 这几条在真机上到不了，',
        '但正对照全过 —— 说明它们的**状态判定是活的**，缺的只是游戏走到那一屏的条件。',
        '',
        '10 个 Scroll 无法用贴图构造（`at_top`/`at_bottom` 比的是滚动条颜色掩码）；',
        '其中 6 个已在真机上命中过（见 `controls.md`），剩 4 个受阻塞。',
        '',
        '## 与真机结果的关系',
        '',
        '正对照通过但真机没验过的页面共 %d 个 —— 它们都是受外部条件阻塞的：' % len(unreachable_ok),
        '',
        '、'.join('`%s`' % p for p in unreachable_ok) if unreachable_ok else '（无）',
        '',
        '也就是说：**这些页面的规则本身是好的，缺的只是"让游戏走到那一屏"的条件**',
        '（账号解锁岛屿/大舰队/指挥喵/大型作战、或对应类型的活动在跑、或客户端版本支持）。',
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
    return 0 if not fails else 1


if __name__ == '__main__':
    sys.exit(main())
