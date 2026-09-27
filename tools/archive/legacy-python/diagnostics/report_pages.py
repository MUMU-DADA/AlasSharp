# -*- coding: utf-8 -*-
"""页面识别验证报告生成器：清单（53 个 Page）× 验证进度 → docs/archive/reports/page-verification.md。

不手写报告：手写会漂移，而"哪些页面真的在真机上命中过"必须能复现。
读取旧 verify_pages.py 留下的历史观察；不连接设备或改写原始结果。
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
import alas_vision as av  # noqa: E402

DOCS = os.path.join(HERE, '..', 'docs')
PROG = os.path.join(DOCS, 'archive/reports/page-verification.json')
OUT = os.path.join(DOCS, 'archive/reports/page-verification.md')

# 未验证页面的原因分类（人工判定，附证据出处）
REASONS = {
    'page_channel': '上游页面图里**没有入边**，只有出边（`page_channel.link(GOTO_MAIN...)`）；'
                    '世界频道是临时浮层。而且 `CHANNEL_CHECK` 在本客户端实测只有 0.11~0.13'
                    '（旧版 UI 素材：主界面同位置现在是「任务」按钮）—— 即使打开了频道，'
                    '这条规则也不会命中。找入口时我在主界面聊天条右侧误点了一次，'
                    '结果是「屏蔽聊天」的确认弹窗（fail-safe 的返回键已取消，未确认、无副作用）；'
                    '上游与新 UI 都没有世界频道的入口素材，故此项无法在真实画面上验证',
    'page_unknown': '`Page(None)` —— 合成实体，没有 check 按钮，不是真实画面',
    'page_private_quarters': '换账号后**已命中**（原记录：宿舍菜单里没有该入口）',
}

# 手工入口验证的页面：页面规则本身在真机上命中了，但**产品导航器到不了**
# （边上的按钮素材在本客户端不匹配）。这类必须单独标注，否则会被误读成"导航也能到"。
MANUAL_ENTRY = {
    'page_event_list': '点主界面右上角「活动汇总」卡片 (1235,125) 进入，'
                       '`EVENT_LIST_CHECK` 实测 0.9958 命中。但上游的白版素材 '
                       '`MAIN_GOTO_EVENT_LIST_WHITE` 在本客户端只有 0.088（新版 UI 的卡片样式变了），'
                       '导航器点不中它 —— 即"页面规则已验证、导航边还缺客户端素材"',
}
ISLAND_SUB = [
    'page_island_manage', 'page_island_map', 'page_island_order', 'page_island_phone',
    'page_island_season', 'page_island_shop', 'page_island_storage',
    'page_island_technology', 'page_island_transport',
]
for _p in ISLAND_SUB:
    REASONS[_p] = '**按用户要求跳过**：岛屿计划相关不在本轮验证范围内'
REASONS['page_island'] = '**按用户要求跳过**：岛屿计划相关不在本轮验证范围内（换账号前实测入口点得通但会退回主界面＝当时未解锁）'
REASONS['page_main_white'] = ''   # 已命中，占位避免误列
# 活动类型页：同一按钮 CAMPAIGN_MENU_GOTO_EVENT 按当前活动指向不同页面
for _p in ('page_raid', 'page_sp', 'page_coalition', 'page_hospital',
           'page_rpg_stage', 'page_rpg_story'):
    REASONS[_p] = ('活动类型未开跑：`CAMPAIGN_MENU_GOTO_EVENT` 按当前活动指向 event/sp/raid/'
                   'coalition/rpg/hospital 之一，本机当前活动是普通活动，只命中 `page_event` '
                   '（定向重试确认：导航到了 campaign_menu / page_campaign / page_event，但目标页不出现）')
REASONS['page_rpg_city'] = ('RPG 活动的城内界面，**上游页面图里没有入边**（只有回主界面/回剧情页的出边），'
                            '既不可能是导航目标；且需要 RPG 类活动在跑才可能出现')


def main():
    inv = json.loads(av.handle_line(json.dumps(
        {'id': 1, 'op': 'ui_rule_inventory', 'args': {}})))['result']
    pages = inv['modules']['module.ui.page']
    with open(PROG, encoding='utf-8') as f:
        prog = json.load(f)
    ver, blk = prog['verified'], prog['blocked']
    inventory = set(pages)
    verified = set(ver)
    blocked = set(blk)
    unknown_pages = (verified | blocked) - inventory
    if unknown_pages:
        raise ValueError(f'页面证据含上游清单之外的页面: {sorted(unknown_pages)}')
    overlap = verified & blocked

    stuck, unknown = [], []
    for name in sorted(pages):
        if name in ver or name in blk:
            continue
        (stuck if name in REASONS else unknown).append(name)

    lines = [
        '# 界面识别验证记录（真机导航）',
        '',
        '历史判定口径：旧逐段点击诊断通常从主界面进入目标页，点击后用 **全量',
        '页面扫描**（`ui_rules_sweep`）看目标页规则是否在该页上真正返回真。',
        '「可驱动」（不抛异常）不算通过 —— 只有**在它自己的页面上命中**才算。',
        '少数手工进入后命中的规则单独列出，不能据此声称产品导航可达。',
        '',
        '设备：MuMu 模拟器 1280x720 @ `127.0.0.1:16384`（国服，新主界面 UI）。',
        '生成脚本：`tools/diagnostics/report_pages.py`（数据源 `docs/archive/reports/page-verification.json`，',
        '由已退役的 `tools/diagnostics/verify_pages.py` 逐批累积，原始记录保持不变）。',
        '旧驱动含本地变体择优与固定点击流程，不能证明当前上游原生导航通过；当前入口见 `regress_pages.py`。',
        '',
        '## 汇总',
        '',
        '| 类别 | 数量 | 含义 |',
        '| --- | --- | --- |',
        '| 已验证规则命中 | %d | 历史真机画面上规则返回真；不等于当前账号可导航到 |' % len(ver),
        '| 导航未达或状态受限记录 | %d | 含导航素材问题与账号/活动门禁 |' % len(blk),
        '| 两项重叠 | %d | 规则曾命中，但另一次导航未达；已计在上述两项中 |' % len(overlap),
        '| 尚未命中且未列为阻塞（原因已定位） | %d | 依赖阻塞页或上游没有入边 |' % len(stuck),
        '| 未分类 | %d | 需要继续排查 |' % len(unknown),
        '| 合计 | %d | 上游 `page.py` 的全部 Page |' % len(pages),
        '',
        '上述两项是独立证据维度，不能相加当作页面总数；去重后全部 %d 页都有分类，' % len(pages),
        '其中只有已验证规则命中的页面有真机正样本。',
        '',
        '## 已验证规则命中',
        '',
        '| 页面 | 该页实际命中的规则 |',
        '| --- | --- |',
    ]
    for name in sorted(ver):
        lines.append('| `%s` | %s |' % (name, ', '.join('`%s`' % v for v in ver[name])))
    lines += ['', '## 导航未达或状态受限记录', '',
              '| 页面 | 原因与证据 |', '| --- | --- |']
    for name in sorted(blk):
        lines.append('| `%s` | %s |' % (name, blk[name]))
    lines += ['', '## 未验证但原因已定位', '', '| 页面 | 原因 |', '| --- | --- |']
    for name in stuck:
        lines.append('| `%s` | %s |' % (name, REASONS[name]))
    if unknown:
        lines += ['', '## 未分类', '']
        lines += ['- `%s`' % n for n in unknown]

    manual = [n for n in sorted(ver) if n in MANUAL_ENTRY]
    if manual:
        lines += ['', '## ⚠️ 手工入口验证（页面规则命中，但产品导航器到不了）', '',
                  '这类页面必须单独看：规则在真机上确实命中了，但**导航边上的按钮素材在本客户端不匹配**，',
                  '所以导航任务到不了它。回归里的历史标签 `goto-failed` 表示导航失败，那是导航边的问题、',
                  '不是识别问题。', '', '| 页面 | 入口与实测 |', '| --- | --- |']
        for n in manual:
            lines.append('| `%s` | %s |' % (n, MANUAL_ENTRY[n]))

    lines += [
        '',
        '## 归档与当前验证入口',
        '',
        '```powershell',
        'python tools/diagnostics/report_pages.py      # 重新生成本文件',
        'python tools/diagnostics/verify_native_page_rules.py  # 离线原生判据对照',
        'python tools/diagnostics/regress_pages.py     # 当前原生导航回归；会操作游戏',
        '```',
        '',
        '## 历史驱动与当前边界',
        '',
        '旧驱动的白版素材推测、按分择优和固定点击顺序已删除；不再作为验证入口或可复用导航算法。',
        '当前导航只通过队列调用上游 `UI.ui_ensure()`，识别使用原生 `UI.ui_page_appear()`。',
        '本报告中的历史命中、导航失败和手工入口分别保留，不能由单帧分数推断整个界面操作成功。',
        '',
    ]
    with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))
    print('verified=%d blocked=%d stuck=%d unknown=%d total=%d'
          % (len(ver), len(blk), len(stuck), len(unknown), len(pages)))
    if unknown:
        print('unknown: %s' % unknown)
    print('written %s' % os.path.abspath(OUT))


if __name__ == '__main__':
    main()
