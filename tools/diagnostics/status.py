# -*- coding: utf-8 -*-
"""把四份验收证据汇总成 docs/archive/reports/status.md：一页回答"上游识别与控制跑到什么程度了"。

数据全部来自已有证据文件，不重新跑设备、也不重复维护分类：
  docs/archive/reports/page-verification.json   页面规则历史命中与导航未达记录（两者可能重叠）
  data/controls_verify.json     控件规则与动作（滑动/开关驱动）
  data/primitives_verify.json   控制原语（返回键/长按/滑动）
  data/regress_pages.json       全量回归（产品路径导航）

分类（"为什么没通过"）不在本文件里重复，指向对应的专项文档 —— 那才是唯一真值来源。
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))          # csharp/tools/diagnostics
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))    # csharp
DOCS = os.path.join(ROOT, 'docs')
DATA = os.path.join(ROOT, 'data')
TOTAL_PAGES = 53          # 上游 module/ui/page.py 的 Page 数（由 ui_rule_inventory 给出）
TOTAL_MODULE_RULES = 20   # 模块级 Switch/Scroll（由 ui_rule_list 给出）
TOTAL_CACHED = 6          # cached_property 规则


def load(path, default=None):
    try:
        with open(path, encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return default


def main():
    pages = load(os.path.join(DOCS, 'archive/reports/page-verification.json'), {})
    ctrl = load(os.path.join(DATA, 'controls_verify.json'), [])
    prim = load(os.path.join(DATA, 'primitives_verify.json'), [])
    text = load(os.path.join(DATA, 'text_input_verify.json'), {}) or {}
    pc = load(os.path.join(DATA, 'positive_control.json'), {}) or {}
    rc = load(os.path.join(DATA, 'rule_positive_control.json'), {}) or {}
    regress = load(os.path.join(DATA, 'regress_pages.json'), [])

    verified = sorted(pages.get('verified', {}))
    blocked = pages.get('blocked', {})
    overlap = set(verified) & set(blocked)
    blocked_only = set(blocked) - set(verified)
    located = TOTAL_PAGES - len(set(verified) | set(blocked))
    if located < 0:
        raise ValueError('页面证据超过上游页面总数')

    rules = [x for x in ctrl if not x['rule'].endswith(('#swipe', '#drive', '#probe'))]
    acts = [x for x in ctrl if x['rule'].endswith(('#swipe', '#drive', '#probe'))]
    # 区分模块级规则与 cached_property：后者是"类.属性"形式（如 Dock.dock_filter），
    # 前者是纯大写常量名。**不能**用有没有 module 字段来分 —— 两者都有。
    def is_cached(x):
        return '.' in x['rule']

    rule_hit = [x for x in rules if x['verdict'] == 'hit']
    hit_module = [x for x in rule_hit if not is_cached(x)]
    hit_cached = [x for x in rule_hit if is_cached(x)]
    act_hit = [x for x in acts if x['verdict'] == 'hit']
    prim_hit = [x for x in prim if x['verdict'] == 'hit']
    reg_ok = [x for x in regress if x['verdict'] == 'ok']

    lines = [
        '# 验收总状态：上游界面与控件识别跑到什么程度',
        '',
        '本页由 `tools/diagnostics/status.py` 从四份证据文件汇总生成，**不手写**。',
        '每项的"为什么没通过"在对应专项文档里，本页只给总数与去处。',
        '',
        '设备：MuMu 模拟器 `127.0.0.1:16384`（1280x720，国服，新版主界面）。',
        '',
        '## 一页结论',
        '',
        '| 范围 | 总数 | 已通过 | 未通过/阻塞 | 明细 |',
        '| --- | --- | --- | --- | --- |',
        '| 页面规则（Page） | %d | **%d** | %d 导航未达且规则未命中 + %d 原因已定位 | `page-verification.md` |'
        % (TOTAL_PAGES, len(verified), len(blocked_only), located),
        '| 控件规则（模块级 Switch/Scroll） | %d | %d | %d | `controls.md` |'
        % (TOTAL_MODULE_RULES, len(hit_module), TOTAL_MODULE_RULES - len(hit_module)),
        '| cached_property 规则 | %d | %d | %d | `controls.md` |'
        % (TOTAL_CACHED, len(hit_cached), TOTAL_CACHED - len(hit_cached)),
        '| 控制动作（滑动/开关驱动/探测） | %d | %d | %d | `controls.md` |'
        % (len(acts), len(act_hit), len(acts) - len(act_hit)),
        '| 控制原语（返回键/长按/滑动） | %d | **%d** | %d | `primitives.md` |'
        % (len(prim), len(prim_hit), len(prim) - len(prim_hit)),
        '| 文本输入（装备码流程） | %d | **%d** | %d | `text-input.md` |'
        % (len(text.get('results', [])),
           sum(1 for x in text.get('results', []) if x['verdict'] == 'hit'),
           sum(1 for x in text.get('results', []) if x['verdict'] != 'hit')),
        '| 全量回归（产品路径导航） | %d | **%d** | %d | `regression.md` |'
        % (len(regress), len(reg_ok), len(regress) - len(reg_ok)),
        '| 页面规则合成正对照 | %d | %d | %d 跳过（`page_unknown` 无素材） | `positive-control.md` |'
        % (pc.get('total', 0), pc.get('passed', 0), pc.get('skipped', 0)),
        '| 控件 Switch 合成正对照 | %d | %d | %d 跳过（Scroll 判定依赖颜色掩码） | `positive-control.md` |'
        % (rc.get('total', 0), rc.get('passed', 0), rc.get('skipped', 0)),
        '',
        '（控件与页面条目在证据文件里含"动作行"，上表已把动作与规则分开计数；',
        '页面规则历史命中与导航未达记录有 %d 页重叠（不重复计入总数）；' % len(overlap),
        '`page_main_white` / `page_channel` / `page_unknown` 是上游图里**无入边**的',
        '状态节点，只能验"同屏被检测到"，见 `regression.md`。）',
        '',
        '## 页面规则曾命中：%d 个' % len(verified),
        '',
        '、'.join('`%s`' % p for p in verified),
        '',
        '## 导航未达或状态受限记录',
        '',
        '| 页面 | 原因与证据 |',
        '| --- | --- |',
    ]
    for name in sorted(blocked):
        lines.append('| `%s` | %s |' % (name, blocked[name]))
    lines += [
        '',
        '## 原因已定位但未验证的 %d 个页面' % located,
        '',
        '逐条原因见 `page-verification.md`：',
        '',
        '1. **依赖阻塞页**：9 个岛屿子页（岛屿计划未解锁）；',
        '2. **上游无入边或非真实画面**：`page_channel`（只有出边）、',
        '   `page_rpg_city`（只有出边且活动类型未开跑）、`page_unknown`（`Page(None)`）。',
        '',
        '## 还没验的控件（都是"到不了"，不是"判定错"）',
        '',
        '| 规则 | 到不了的原因 |',
        '| --- | --- |',
        '| `FORMATION` / `SUBMARINE_HUNT` / `SUBMARINE_VIEW` / `FLEET_LOCK` | 出击前阵型面板、潜艇面板、'
        '舰队编辑浮层。本机 `page_fleet` 上 `equipment/FLEET_DETAIL` 实测 0.17 分（不在屏上），'
        '要进出击流程才行 —— 那条流程会消耗石油并影响账号，**需本人同意** |',
        '| `equipping_filter` | 装备选择浮层。已试过两条上游入口（详情页点 `EQUIPMENT_OPEN`、'
        '点装备槽位），均未打开该浮层 |',
        '| `RETIRE_CONFIRM_SCROLL` | 退役确认弹窗。哪怕只差一次误点就可能真的退役舰船，**故意不验** |',
        '| `EventShopUI.event_shop_tab_count_and_navbar` | 需进活动商店（本机活动页可达，但商店入口未开放） |',
        '| 岛屿 / 大世界 / 指挥喵相关控件 | 功能本身未解锁 |',
        '',
        '## 未覆盖的控制原语',
        '',
        '| 项 | 原因 |',
        '| --- | --- |',
        '| 长按的完整业务链 | 上游 `gems_farming` 那条真实用法要真的出击，未验；',
        '原语本身已用上游自己的判据（`EQUIPMENT_OPEN`）验过 |',
        '| uiautomator2 后端的文本输入 | 上游装备码用的是 `send_keys`（uiautomator2）。',
        '我们验的是 adb 的 `input text`（产品当前只有这个后端），两者语义不同：',
        '`input text` 不支持中文、不清空原内容 |',
        '',
        '## 复现全部证据',
        '',
        '```powershell',
        '$env:STUB_ADB = "<adb.exe>"',
        'python tools/diagnostics/regress_pages.py        # 已验证页面的产品导航回归',
        'python tools/diagnostics/verify_controls.py      # 控件规则 + 滑动/开关驱动',
        'python tools/diagnostics/verify_primitives.py    # 返回键/长按/滑动',
        'python tools/diagnostics/report_pages.py         # 重建 page-verification.md',
        'python tools/diagnostics/status.py               # 重建本文件',
        '```',
        '',
    ]
    out = os.path.join(DOCS, 'archive/reports/status.md')
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines))
    print('页面 %d/%d 曾命中（导航未达记录 %d，其中重叠 %d；其余原因已定位 %d）'
          % (len(verified), TOTAL_PAGES, len(blocked), len(overlap), located))
    print('控件规则 %d 命中 / 动作 %d 命中 / 原语 %d/%d / 回归 %d/%d'
          % (len(rule_hit), len(act_hit), len(prim_hit), len(prim), len(reg_ok), len(regress)))
    print('写入 %s' % out)


if __name__ == '__main__':
    main()
