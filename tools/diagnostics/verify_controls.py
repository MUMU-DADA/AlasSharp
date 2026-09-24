"""Archive existing control observations without any game or device action.

The former page-specific driver used guessed coordinates and asset variants.
It is retired. Current native rule coverage is verify_upstream_coverage.py;
new action evidence must come from native tasks through the product queue.
"""
from collections import Counter
import argparse
import hashlib
import json
from pathlib import Path

from verify_privacy import PATTERNS

ROOT = Path(__file__).resolve().parents[2]


def cell(value):
    text = str(value).replace(str(ROOT), '<project>').replace(ROOT.as_posix(), '<project>')
    if any(pattern.search(text) for pattern in PATTERNS.values()):
        raise ValueError('Private content in control evidence; archive refused')
    return text.replace('|', '&#124;').replace('\r', '').replace('\n', '<br>')


def render(rows, digest):
    if not isinstance(rows, list) or not rows:
        raise ValueError('Expected nonempty historical control evidence')
    for row in rows:
        if not isinstance(row, dict) or not all(k in row for k in ('page', 'rule', 'verdict', 'detail')):
            raise ValueError('Invalid control evidence row')
        if row['verdict'] not in ('hit', 'miss', 'blocked'):
            raise ValueError('Unknown historical verdict')
    counts = Counter(row['verdict'] for row in rows)
    lines = ['# 控件历史识别与动作记录', '',
             '由 `tools/diagnostics/verify_controls.py` 只读归档；本次没有设备动作。',
             '旧逐页动作驱动已退役：其中固定坐标、推测素材变体及手工点击顺序不再执行。',
             '原始记录 `data/controls_verify.json` 保持不变，以下结果不改判、不补写未发生的验证。',
             f'原始文件 SHA-256：`{digest}`。', '',
             f'历史 {len(rows)} 条记录：hit={counts["hit"]}、miss={counts["miss"]}、blocked={counts["blocked"]}。',
             '这些记录包含重复控件、动作及守卫；不代表同等数量的独立规则通过。',
             '历史 hit 不能证明当前产品路径有效；miss/blocked 也不能直接归因为客户端版本或素材错误。', '',
             '| 页面 | 模块 | 规则 | 类型 | 原判定 | 原始细节 |',
             '| --- | --- | --- | --- | --- | --- |']
    for row in rows:
        values = [row.get(key, '') for key in ('page', 'module', 'rule', 'class', 'verdict', 'detail')]
        lines.append('| ' + ' | '.join(cell(value) for value in values) + ' |')
    lines += ['', '## 证据边界与复验', '',
              '原生声明、构造及合成识别覆盖见 [全量规则报告](upstream-coverage.md)。',
              '真实识别必须在对应原生任务状态下验证；开关/滚动动作还要核对变化及恢复后的状态。',
              '历史记录没有独立恢复成功判据时，不从动作 hit 推断已经恢复。',
              '新动作由 TaskQueue 调度原生任务，不在诊断脚本另建逐页流程。',
              '仅重建本报告：`python tools/diagnostics/verify_controls.py --report-only`。', '',
              '脱敏范围：只导出页面/模块/规则标识、原判定及原始细节；项目绝对路径替换为 `<project>`。',
              '不发布设备配置、图像或账号信息，敏感字符串会阻止归档；原始文件与哈希保持不变。', '']
    return '\n'.join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report-only', action='store_true', help='Compatibility flag; all modes are read-only')
    parser.add_argument('--input', type=Path, default=ROOT / 'data/controls_verify.json')
    parser.add_argument('--output', type=Path, default=ROOT / 'docs/archive/reports/controls.md')
    args = parser.parse_args(argv)
    if not args.input.is_file():
        print('SKIP: historical control evidence absent; existing report preserved')
        return 0
    raw = args.input.read_bytes()
    report = render(json.loads(raw), hashlib.sha256(raw).hexdigest())
    if args.input.resolve() == args.output.resolve():
        raise ValueError('Report must not overwrite raw evidence')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(report, encoding='utf-8')
    assert args.input.read_bytes() == raw, 'Historical evidence changed'
    print('PASS: archived historical control evidence; no native imports or device actions')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
