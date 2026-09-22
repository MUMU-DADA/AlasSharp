# -*- coding: utf-8 -*-
"""从回归证据里挖"识别特异性"：每个页面规则到底在哪些页面上命中。

为什么这件事重要：
- 逐页验证只能证明"规则在自己的页面上返回真"。它证明不了**规则不会在别人的页面上乱返回真** ——
  而后者才是页面识别最容易出的问题（一个恒真的规则会让导航逻辑到处误判）。
- 回归脚本每到一个页面都会跑一次全量页面判定，所以"29 个页面 × 53 条规则"的命中矩阵
  其实已经在 data/regress_pages.json 里了，不用再碰设备。

产出的三类结论：
1. **精确命中**：只在自己的页面上命中 —— 最理想；
2. **共命中**：同一屏上多条规则同时命中（如商店三连、主界面两种皮肤）—— 要确知是上游设计；
3. **从未命中**：在 29 个可达页面上一次都没命中 —— 说明它至少不是恒真规则（对受阻塞页面
   而言这是能拿到的最强证据）；若某条"从未命中"的规则本该在可达页面上命中，那就是漏检。
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
DOCS = os.path.join(ROOT, 'docs')
DATA = os.path.join(ROOT, 'data')


def main():
    with open(os.path.join(DATA, 'regress_pages.json'), encoding='utf-8') as f:
        regress = json.load(f)
    with open(os.path.join(DOCS, 'page-verification.json'), encoding='utf-8') as f:
        progress = json.load(f)
    verified = progress['verified']
    blocked = progress['blocked']

    # 规则 -> 在哪些页面上命中（页面取自回归里每个目标页到达后的全量判定结果）
    hits = {}
    for row in regress:
        for rule in row['observed']:
            hits.setdefault(rule, set()).add(row['page'])

    exact, cohit, never = [], [], []
    for rule in sorted(verified):
        pages = sorted(hits.get(rule, set()))
        if not pages:
            never.append(rule)
        elif pages == [rule]:
            exact.append(rule)
        else:
            cohit.append((rule, pages))
    # 受阻塞/未验证的规则如果在可达页面上命中过，也算"从未命中"之外的信息
    other_never = sorted(r for r in list(blocked) + ['page_channel', 'page_unknown']
                         if r not in hits)

    # 反向检查：有没有规则命中了"不是它自己"的页面（潜在误判）
    suspicious = []
    for rule, pages in sorted(hits.items()):
        strangers = [p for p in pages if p != rule]
        if strangers and rule not in ('page_main_white', 'page_main'):
            suspicious.append((rule, sorted(strangers)))

    lines = [
        '# 识别特异性矩阵（从 29 页回归证据挖出）',
        '',
        '逐页验证只证明"规则在自己的页面上为真"；它证明不了**规则不会在别人的页面上乱为真**。',
        '而恒真/乱真的规则会让导航到处误判 —— 这一页就是查这个。',
        '',
        '数据来源：`data/regress_pages.json`。回归脚本每到一个页面都跑一次全量页面判定，',
        '所以"29 页 × 53 条规则"的命中矩阵已经在证据里，本页不重新跑设备。',
        '生成：`tools/diagnostics/analyze_specificity.py`。',
        '',
        '## 汇总',
        '',
        '| 类别 | 数量 | 含义 |',
        '| --- | --- | --- |',
        '| 精确命中 | %d | 只在自己的页面上命中（最理想） |' % len(exact),
        '| 共命中 | %d | 同一屏多条规则同时命中（见下） |' % len(cohit),
        '| 从未命中 | %d | 29 个可达页面上一次都没命中 |' % len(never),
        '',
        '## 共命中（要确知是上游设计，不是误判）',
        '',
        '| 规则 | 同时命中的页面 |',
        '| --- | --- |',
    ]
    for rule, pages in cohit:
        lines.append('| `%s` | %s |' % (rule, ', '.join('`%s`' % p for p in pages)))
    lines += [
        '',
        '已知的两组共命中都是上游设计：',
        '',
        '- `page_main` / `page_main_white`：同一张主界面的两种皮肤，本来就同时成立；',
        '- `page_shop` / `page_munitions` / `page_supply_pack`：商店是同一套界面的子页签，',
        '三条 check 同时为真（区分当前页签要靠 `ShopUI` 的 Navbar/Switch 规则）。',
        '',
        '## 疑似误判（命中了自己以外的页面）',
        '',
    ]
    if suspicious:
        lines += ['| 规则 | 意外命中的页面 |', '| --- | --- |']
        for rule, pages in suspicious:
            lines.append('| `%s` | %s |' % (rule, ', '.join('`%s`' % p for p in pages)))
    else:
        lines.append('无。所有命中都发生在规则自己的页面上（除上面已知的共命中组）。')
    lines += [
        '',
        '## 从未命中（含受阻塞页面）',
        '',
        '这些规则在 29 个可达页面上一次都没命中 —— 对**受游戏状态阻塞**的页面来说，',
        '这是能拿到的最强证据：**它们至少不是恒真规则**（恒真的规则会在任何页面上乱命中）。',
        '反过来说，如果某条这里列出的规则本该在可达页面上命中，那它就是漏检，需要排查。',
        '',
        '未在回归里命中、且属于"已验证页面"的规则（即漏检嫌疑）：',
        '',
    ]
    if never:
        lines += ['- %s' % ', '.join('`%s`' % r for r in never)]
    else:
        lines.append('- 无')
    lines += [
        '',
        '其余未命中的规则（本就不可达，属正常）：%s'
        % ', '.join('`%s`' % r for r in other_never),
        '',
    ]
    out = os.path.join(DOCS, 'specificity.md')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))
    print('精确命中 %d / 共命中 %d / 从未命中 %d（其中漏检嫌疑 %d）'
          % (len(exact), len(cohit), len(never), len(never)))
    print('疑似误判: %s' % (suspicious if suspicious else '无'))
    print('写入 %s' % out)


if __name__ == '__main__':
    main()
