#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
上游数据契约导出器（路线甲 · S0）。

职责边界（重要）：
    本工具**不重写上游提取器**。上游的 `dev_tools/button_extract.py`、`map_extractor.py`
    等已经把「游戏资源 → Python 数据文件」这一步做完了；本工具只做
    「上游生成的 .py 产物 → JSON」这最后一段转换。

    因此数据同步链是：
        游戏资源 --(上游 Python 提取器)--> assets.py / campaign/*.py --(本工具)--> JSON
    上游更新素材或地图时，只需重跑上游提取器 + 本工具，C# 侧零改动。

用法：
    python export_upstream_data.py --repo <ALAS 仓库> --out <输出目录>
    python export_upstream_data.py --repo <...> --out <...> --check   # 校验产物是否与源码一致

产物：
    <out>/assets.json                  素材绑定（Button/Template，四服变体）
    <out>/campaign/<path>.json         关卡 IR（地图网格 + Config 标志 + 归一化战斗计划）
    <out>/campaign_index.json          关卡索引（含是否模板化、是否需要人工复核）
    <out>/schema/assets.schema.json    JSON Schema
    <out>/schema/campaign.schema.json  JSON Schema
    <out>/manifest.json                溯源：上游 commit、源文件哈希、计数、未解析项
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import os
import re
import subprocess
import sys
from collections import Counter, defaultdict

try:
    from .upstream_config_export import ConfigResolver
except ImportError:
    from upstream_config_export import ConfigResolver

EXPORTER_VERSION = '2.0.0'
SERVERS = ('cn', 'en', 'jp', 'tw')
SKIP_DIRS = {'.venv', '.git', '__pycache__', '.pytest_cache', '.ruff_cache', '.trial-merge'}

# 上游 dev_tools/map_extractor.py 里 battle_N 模板会吐出的调用词表。
# 只用到这些调用 = 该关卡是「生成器模板产物」，可直接用 JSON 规则表驱动。
TEMPLATE_VOCAB = {'clear_siren', 'clear_filter_enemy', 'battle_default', 'clear_boss',
                  'fleet_boss.clear_boss'}

MAP_ATTRS = ('shape', 'camera_data', 'camera_data_spawn_point', 'map_data', 'map_data_loop',
             'weight_data', 'spawn_data', 'spawn_data_loop', 'portal_data', 'land_based_data',
             'wall_data')


# --------------------------------------------------------------------- 工具
def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 16), b''):
            h.update(chunk)
    return h.hexdigest()


def name_from_path(rel: str) -> str:
    """关卡名兜底：campaign_main/campaign_1_2.py -> '1-2'。

    上游有的文件写 `MAP = CampaignMap()`（不带名字），名字另在别处给。
    这里从路径派生一个稳定标识，并在 IR 里用 name_source 标注来源，避免冒充权威。
    """
    stem = os.path.basename(rel)
    if stem.endswith('.py'):
        stem = stem[:-3]
    if stem.startswith('campaign_'):
        stem = stem[len('campaign_'):]
    if re.fullmatch(r'\d+_\d+', stem):
        return stem.replace('_', '-')
    return stem


def iter_py(root: str, subdir: str):
    base = os.path.join(root, subdir)
    for dirpath, dirs, files in os.walk(base):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in sorted(files):
            if f.endswith('.py'):
                yield os.path.join(dirpath, f)


def literal(node):
    """尽力把 AST 节点求成字面量；失败返回哨兵。"""
    try:
        return ast.literal_eval(node)
    except Exception:
        return _UNRESOLVED


_UNRESOLVED = object()


def call_name(node: ast.AST):
    """self.a() -> 'a'；self.a.b() -> 'a.b'；Button(...) -> 'Button'。"""
    if not isinstance(node, ast.Call):
        return None
    f = node.func
    if isinstance(f, ast.Name):
        return f.id
    if isinstance(f, ast.Attribute):
        if isinstance(f.value, ast.Name):
            return f.attr if f.value.id != 'self' else f.attr
        if isinstance(f.value, ast.Attribute) and isinstance(f.value.value, ast.Name) \
                and f.value.value.id == 'self':
            return f'{f.value.attr}.{f.attr}'
        return f.attr
    return None


def is_self_call(node: ast.AST):
    """仅匹配 self.x() / self.a.b()。"""
    if not isinstance(node, ast.Call):
        return False
    f = node.func
    if isinstance(f, ast.Attribute) and isinstance(f.value, ast.Name) and f.value.id == 'self':
        return True
    return bool(isinstance(f, ast.Attribute) and isinstance(f.value, ast.Attribute)
                and isinstance(f.value.value, ast.Name) and f.value.value.id == 'self')


def is_super_delegate(node: ast.AST):
    """匹配 `super().x(...)`（纯委托给父类实现）。"""
    if not isinstance(node, ast.Call):
        return False
    f = node.func
    return bool(isinstance(f, ast.Attribute) and isinstance(f.value, ast.Call)
                and isinstance(f.value.func, ast.Name) and f.value.func.id == 'super')


def super_call_name(node: ast.Call):
    return f'super().{node.func.attr}'


def call_args(node: ast.Call):
    """调用实参 → {位置参数: [...], 关键字参数: {...}}，非字面量记 '<expr>'。"""
    pos, kw = [], {}
    for a in node.args:
        v = literal(a)
        pos.append(v if v is not _UNRESOLVED else '<expr>')
    for k in node.keywords:
        if k.arg is None:
            continue
        v = literal(k.value)
        kw[k.arg] = v if v is not _UNRESOLVED else '<expr>'
    return {'positional': pos, 'keyword': kw}


# --------------------------------------------------------------------- 素材
def export_assets(root: str, out_dir: str, manifest: dict):
    records = {}
    duplicates = defaultdict(list)
    names_seen = defaultdict(list)
    unresolved = []
    per_module = Counter()
    source_files = []

    for path in iter_py(root, 'module'):
        if os.path.basename(path) != 'assets.py':
            continue
        source_files.append(path)
        rel = os.path.relpath(path, root).replace('\\', '/')
        module = rel[len('module/'):-len('/assets.py')] if rel.count('/') else ''
        try:
            tree = ast.parse(open(path, encoding='utf-8').read())
        except SyntaxError as e:
            manifest['errors'].append({'file': rel, 'error': f'SyntaxError: {e}'})
            continue

        for node in tree.body:
            if not isinstance(node, ast.Assign) or len(node.targets) != 1:
                continue
            target = node.targets[0]
            if not isinstance(target, ast.Name):
                continue
            name = target.id
            kind = call_name(node.value) if isinstance(node.value, ast.Call) else None
            if kind not in ('Button', 'Template', 'Mask'):
                continue

            fields, bad = {}, []
            for k in node.value.keywords:
                v = literal(k.value)
                if v is _UNRESOLVED:
                    bad.append(k.arg)
                else:
                    fields[k.arg] = v
            if bad:
                unresolved.append({'asset': f'{module}/{name}', 'file': rel, 'fields': bad})
                continue

            # 归一：把 dict 形式的字段按键排序，并算出该绑定覆盖了哪些服
            norm = {}
            for key in ('area', 'color', 'button', 'file'):
                v = fields.get(key)
                if isinstance(v, dict):
                    norm[key] = {s: v[s] for s in SERVERS if s in v}
                elif v is not None:
                    norm[key] = v
            # 服别从**所有**分服字段取并集：Template 只有 file 没有 area，
            # 只看 area 会让 441 个 Template 的服务器信息丢失（实测踩过）。
            server_sets = [set(v) for v in norm.values() if isinstance(v, dict)]
            servers = sorted(set().union(*server_sets)) if server_sets else []

            # 结构校验：Button/Mask 必须有 area 才能定位；任何绑定都必须有 file
            if kind in ('Button', 'Mask') and not norm.get('area'):
                unresolved.append({'asset': f'{module}/{name}', 'file': rel,
                                   'issue': f'{kind} 缺 area'})
            if not norm.get('file'):
                unresolved.append({'asset': f'{module}/{name}', 'file': rel,
                                   'issue': '缺 file'})

            asset_id = f'{module}/{name}' if module else name
            names_seen[name].append(asset_id)
            records[asset_id] = {
                'id': asset_id,
                'name': name,
                'module': module,
                'kind': kind,
                **norm,
                'servers': servers,
                'all_servers': set(servers) >= set(SERVERS),
                'source': rel,
            }
            per_module[module] += 1

    os.makedirs(out_dir, exist_ok=True)
    _write_json(os.path.join(out_dir, 'assets.json'),
                {'version': EXPORTER_VERSION, 'servers': list(SERVERS),
                 'assets': records})

    manifest['assets'] = {
        'count': len(records),
        'all_four_servers': sum(1 for r in records.values() if r['all_servers']),
        'by_kind': dict(Counter(r['kind'] for r in records.values())),
        'by_module': dict(sorted(per_module.items())),
        'duplicate_names': {k: v for k, v in names_seen.items() if len(v) > 1},
        'unresolved': unresolved,
        'source_files': len(source_files),
        'source_hashes': {os.path.relpath(p, root).replace('\\', '/'): sha256_file(p)
                          for p in source_files},
    }
    return records


# --------------------------------------------------------------------- 关卡
def _is_bare_return_true(body) -> bool:
    """判断分支体是否只是 `return True`（上游生成器模板的唯一条件体形态）。"""
    stmts = [s for s in body
             if not (isinstance(s, ast.Expr) and isinstance(s.value, ast.Constant))]
    if len(stmts) != 1:
        return False
    s = stmts[0]
    return (isinstance(s, ast.Return) and isinstance(s.value, ast.Constant)
            and s.value.value is True)


def derive_plan(body: list, where: str):
    """
    把 battle_N 方法体归一成步骤序列。

    只归一**能完整表示**的语句形态：
        if self.X(args) and <body 仅为 return True>    → conditional
        if not self.X(args) and <body 仅为 return True> → conditional_negated
        return self.X(args)                            → terminal
        self.X(args)                                   → call
        var = self.X(args)                             → assign

    任何**表示不了**的（赋值运算、对变量取条件、分支体内还有别的语句、循环…）都记进 `unparsed`，
    此时 plan_complete=false 且 steps 作废。

    ⚠️ 这一条是**保真红线**，有真实教训：早期版本把
        `if not self.X(): return self.Y()`
    也当成 conditional_negated，结果把分支体里的 `self.Y()` **静默丢掉**——
    计划看起来完整，实际少调用一次，而当时的校验只查「计划里的算子在源码中出现」，
    查不出这种**丢步**。是 S3 解释器对拍（执行序列 vs 计划序列）才把它暴露出来（26 个关卡）。
    """
    steps, unparsed, dead = [], [], []
    terminated = False

    def self_call_step(node, kind, **extra):
        s = {'op': call_name(node), 'args': call_args(node), 'kind': kind}
        s.update(extra)
        return s

    for stmt in body:
        if terminated:
            # Python 语义：`return` 之后的语句**不可达**。
            # 上游确实存在这种手滑留下的死代码，实测 campaign/event_20211028_tw/c3.py：
            #     return self.battle_default()
            #     return self.battle_default()      ← 永不执行
            # 记进 dead 以便追溯，但绝不能当成步骤 —— 否则解释器会执行一次永不发生的调用
            # （实测会让解释器执行序列与计划序列对不上，2 个关卡）。
            dead.append(type(stmt).__name__)
            continue

        if isinstance(stmt, ast.If):
            t = stmt.test
            if not stmt.orelse and _is_bare_return_true(stmt.body):
                if is_self_call(t):
                    steps.append(self_call_step(t, 'conditional'))
                    continue
                if isinstance(t, ast.UnaryOp) and isinstance(t.op, ast.Not) \
                        and is_self_call(t.operand):
                    steps.append(self_call_step(t.operand, 'conditional_negated'))
                    continue
            # 分支体不是单纯的 return True（例如分支里还有调用/返回别的值）：
            # 表示不了就如实标为未解析，绝不给出一份少几步的"完整"计划。
            unparsed.append('If(nested)')
        elif isinstance(stmt, ast.Return):
            if stmt.value is not None and is_self_call(stmt.value):
                steps.append(self_call_step(stmt.value, 'terminal'))
                terminated = True
            elif is_super_delegate(stmt.value):
                # `return super().X(...)`：纯委托，本类没有新增逻辑，只是覆写钩子。
                steps.append({'op': super_call_name(stmt.value), 'args': call_args(stmt.value),
                              'kind': 'super_delegate'})
                terminated = True
            else:
                unparsed.append('Return(expr)')
        elif isinstance(stmt, ast.Expr):
            if is_self_call(stmt.value):
                steps.append(self_call_step(stmt.value, 'call'))
            else:
                unparsed.append('Expr')
        elif isinstance(stmt, ast.Assign):
            v = stmt.value
            if is_self_call(v) and len(stmt.targets) == 1 \
                    and isinstance(stmt.targets[0], ast.Name):
                steps.append(self_call_step(v, 'assign', target=stmt.targets[0].id))
            else:
                unparsed.append('Assign')
        elif isinstance(stmt, ast.Pass):
            continue
        else:
            unparsed.append(type(stmt).__name__)

    plan_complete = not unparsed
    if not plan_complete:
        steps = []
    return steps, plan_complete, unparsed, dead


def export_campaign(root: str, out_dir: str, manifest: dict):
    index, unresolved_all = [], []
    source_files = []
    stats = Counter()
    config_resolver = ConfigResolver(root)
    # 有的章节地图不是字面量，而是**从别的章节拷来的**（例：campaign_15_4_121 里
    # `from .campaign_15_4 import MAP as MAP_15_4` + `MAP = copy.copy(MAP_15_4)`）。
    # 只抓字面量的话这种章节会导出成空地图 —— 而空地图不会表现成"识别不准"，
    # 只会让引擎在一张空地图上做规划。所以下面记下引用关系，循环结束后统一补。
    derived = {}          # rel -> 被引用的模块名
    rel_by_module = {}    # 'campaign_main.campaign_15_4' -> 'campaign/campaign_main/campaign_15_4.py'

    for path in iter_py(root, 'campaign'):
        rel = os.path.relpath(path, root).replace('\\', '/')
        source_files.append(path)
        try:
            with open(path, encoding='utf-8') as source:
                tree = ast.parse(source.read())
        except SyntaxError as e:
            manifest['errors'].append({'file': rel, 'error': f'SyntaxError: {e}'})
            continue

        module = rel[len('campaign/'):-3].replace('/', '.')
        rel_by_module[module] = rel
        pkg = module.rsplit('.', 1)[0] if '.' in module else ''

        # 相对导入别名：`from .campaign_15_4 import MAP as MAP_15_4`
        #   → aliases['MAP_15_4'] = 'campaign_main.campaign_15_4'
        aliases = {}
        for node in tree.body:
            if isinstance(node, ast.ImportFrom):
                base = node.module or ''
                target = f'{pkg}.{base}' if pkg and base else (base or pkg)
                for a in node.names:
                    aliases[a.asname or a.name] = target

        # 模块级常量符号表（用于解析 ENEMY_FILTER = ENEMY_FILTER 这类引用）
        consts = {}
        for node in tree.body:
            if isinstance(node, ast.Assign) and len(node.targets) == 1 \
                    and isinstance(node.targets[0], ast.Name):
                v = literal(node.value)
                if v is not _UNRESOLVED:
                    consts[node.targets[0].id] = v

        def resolve(node):
            v = literal(node)
            if v is not _UNRESOLVED:
                return v
            if isinstance(node, ast.Name):
                if node.id in consts:
                    return consts[node.id]
                return '<self>'
            return _UNRESOLVED

        ir = {'source': rel, 'name': None, '_name_source': None, 'map': {}, 'config': {},
              'config_meta': {}, 'campaign': {'battles': [], 'attributes': {}},
              'unresolved': []}

        for node in tree.body:
            if isinstance(node, ast.Assign) and len(node.targets) == 1:
                tgt = node.targets[0]
                if isinstance(tgt, ast.Attribute) and isinstance(tgt.value, ast.Name) \
                        and tgt.value.id == 'MAP':
                    v = resolve(node.value)
                    if v is _UNRESOLVED:
                        ir['unresolved'].append(f'MAP.{tgt.attr}')
                    elif v != '<self>':
                        ir['map'][tgt.attr] = v
                elif isinstance(tgt, ast.Name) and tgt.id == 'MAP':
                    v = node.value
                    # `MAP = copy.copy(MAP_X)` / `MAP = MAP_X`：记下引用，稍后补地图
                    ref = None
                    if isinstance(v, ast.Name):
                        ref = v.id
                    elif isinstance(v, ast.Call) and isinstance(v.func, ast.Attribute) \
                            and v.func.attr in ('copy', 'deepcopy') and v.args \
                            and isinstance(v.args[0], ast.Name):
                        ref = v.args[0].id
                    if ref and ref in aliases:
                        derived[rel] = aliases[ref]
                        ir['_map_derived_alias'] = ref
                    if isinstance(v, ast.Call):
                        args = [literal(a) for a in v.args]
                        if args and args[0] is not _UNRESOLVED and args[0] is not None:
                            ir['name'] = args[0]
                            ir['_name_source'] = 'CampaignMap'
                        else:
                            ir['name'] = name_from_path(rel)
                            ir['_name_source'] = 'path'
            elif isinstance(node, ast.ClassDef) and node.name == 'Campaign':
                cls_attrs = {}
                for sub in node.body:
                    if isinstance(sub, ast.Assign) and len(sub.targets) == 1 \
                            and isinstance(sub.targets[0], ast.Name):
                        attr = sub.targets[0].id
                        v = resolve(sub.value)
                        if v is _UNRESOLVED:
                            ir['unresolved'].append(f'{node.name}.{attr}')
                        elif v != '<self>':
                            cls_attrs[attr] = v
                    elif isinstance(sub, (ast.FunctionDef, ast.AsyncFunctionDef)) \
                            and node.name == 'Campaign':
                        body = [x for x in sub.body if not (isinstance(x, ast.Expr)
                                and isinstance(x.value, ast.Constant))]
                        steps, plan_complete, unparsed, dead = derive_plan(body, sub.name)
                        calls = []
                        for x in ast.walk(sub):
                            if is_self_call(x):
                                calls.append(call_name(x))
                        ir['campaign']['battles'].append({
                            'method': sub.name, 'calls': calls, 'steps': steps,
                            'plan_complete': plan_complete, 'unparsed': unparsed,
                            'dead_code': dead,
                            'stmt_count': len(body),
                        })
                if node.name == 'Campaign':
                    ir['campaign']['attributes'].update(
                        {k: v for k, v in cls_attrs.items() if k != 'MAP'})
                    ir['campaign']['class'] = node.name
                    ir['campaign']['bases'] = [ast.unparse(b) for b in node.bases]

        # Config is a separate inheritance graph from Campaign. Resolve the effective
        # chapter overrides from source so imported/re-exported Config classes, C3
        # inheritance and constant expressions are preserved in one general path.
        config_export = config_resolver.export('campaign.' + module)
        ir['config'] = config_export['values']
        ir['config_meta'] = {
            key: config_export[key]
            for key in ('present', 'complete', 'mro', 'origins', 'typed_values',
                        'source_files', 'unresolved')
        }
        if not config_export['complete']:
            for issue in config_export['unresolved']:
                field = issue.get('field')
                prefix = f'Config.{field}' if field else 'Config'
                ir['unresolved'].append(f"{prefix}: {issue.get('reason', issue)}")

        # 关卡名兜底
        if not ir['name']:
            ir['name'] = name_from_path(rel)
            ir['_name_source'] = 'path'

        # 派生字段与难度分级
        #
        # 分级只看 battle_* 方法（它们才驱动战斗计划）；非 battle_* 的覆写是「引擎钩子」，
        # 单独统计成 native_overrides —— 纯 `return super().X()` 的委托不算新增逻辑。
        battles = ir['campaign']['battles']
        battle_methods = [b for b in battles if b['method'].startswith('battle_')]
        hooks = [b for b in battles if not b['method'].startswith('battle_')]
        boss = None
        for b in battle_methods:
            if b['method'][7:].isdigit():
                boss = max(boss or 0, int(b['method'][7:]))
        all_calls = {c for b in battle_methods for c in b['calls']}
        plan_complete = bool(battle_methods) and all(b['plan_complete'] for b in battle_methods)
        template_only = plan_complete and all_calls <= TEMPLATE_VOCAB
        # 难度分级：A = JSON 规则表即可；B = JSON 计划完整但用了词表外算子；C = 需插件/原生实现
        tier = 'A' if template_only else ('B' if plan_complete else 'C')
        native_overrides = sorted(b['method'] for b in hooks if not b['plan_complete'])
        super_delegates = sorted(b['method'] for b in hooks if b['plan_complete'])
        ir['campaign']['boss_battle'] = boss
        ir['campaign']['template_only'] = template_only
        ir['campaign']['plan_complete'] = plan_complete
        ir['campaign']['tier'] = tier
        ir['campaign']['has_siren'] = 'clear_siren' in all_calls
        ir['campaign']['native_overrides'] = native_overrides
        ir['campaign']['super_delegates'] = super_delegates
        if not battle_methods:
            ir['unresolved'].append('无 Campaign.battle_* 方法')
        if ir['unresolved']:
            unresolved_all.append({'file': rel, 'items': ir['unresolved']})

        stats['files'] += 1
        stats['template_only'] += 1 if template_only else 0
        stats['with_unresolved'] += 1 if ir['unresolved'] else 0
        stats[f'tier_{tier}'] += 1
        stats['native_override_methods'] += len(native_overrides)
        stats['super_delegate_methods'] += len(super_delegates)
        stats['incomplete_battles'] += sum(1 for b in battle_methods if not b['plan_complete'])

        ir['name_source'] = ir.get('_name_source')
        name_source = ir.pop('_name_source', None)

        dest = os.path.join(out_dir, 'campaign', rel[len('campaign/'):-3] + '.json')
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        _write_json(dest, ir)

        index.append({
            'source': rel,
            'json': os.path.relpath(dest, out_dir).replace('\\', '/'),
            'name': ir['name'],
            'name_source': name_source,
            'tier': tier,
            'plan_complete': plan_complete,
            'template_only': template_only,
            'boss_battle': boss,
            'has_siren': ir['campaign']['has_siren'],
            'battle_methods': [b['method'] for b in battle_methods],
            'native_overrides': native_overrides,
            'super_delegates': super_delegates,
            'config_keys': sorted(ir['config'].keys()),
            'config_present': ir['config_meta']['present'],
            'config_complete': (ir['config_meta']['present']
                               and ir['config_meta']['complete']),
            'map_keys': sorted(ir['map'].keys()),
            'needs_review': tier == 'C' or not ir['config_meta']['complete'],
        })

    # ---- 第二遍：补"从别的章节拷地图"的那些章节
    # 支持链式（A 拷 B、B 拷 C）：迭代到收敛，最多 5 轮（够用且防环）。
    fixed = []
    for _ in range(5):
        changed = False
        for rel, src_module in sorted(derived.items()):
            src_rel = rel_by_module.get(src_module)
            if not src_rel or src_rel == rel:
                continue
            dest = os.path.join(out_dir, 'campaign', rel[len('campaign/'):-3] + '.json')
            src_dest = os.path.join(out_dir, 'campaign', src_rel[len('campaign/'):-3] + '.json')
            if not (os.path.exists(dest) and os.path.exists(src_dest)):
                continue
            with open(dest, encoding='utf-8') as f:
                me = json.load(f)
            local = dict(me.get('map') or {})
            # "自己有地图"要看**实质内容**，不是看 dict 非空：
            # campaign_15_4_121 的 map 里有 `name`（`MAP.name = '15-4-121'`）却没有地图数据，
            # 第一版按"dict 非空"判断，于是把它当成"自己有地图"跳过了（补全数 0）。
            if local.get('map_data') or local.get('shape'):
                continue
            with open(src_dest, encoding='utf-8') as f:
                src = json.load(f)
            src_map = dict(src.get('map') or {})
            if not (src_map.get('map_data') or src_map.get('shape')):
                continue                      # 源头也还没补上，下一轮再说
            merged = dict(src_map)
            merged['derived_from'] = src.get('source') or src_rel
            merged.update(local)              # 本地字段（name 等）覆盖来源
            me['map'] = merged
            _write_json(dest, me)
            for row in index:
                if row['source'] == rel:
                    row['map_keys'] = sorted(me['map'].keys())
                    row['map_derived_from'] = me['map']['derived_from']
            fixed.append(rel)
            changed = True
        if not changed:
            break
    if fixed:
        manifest.setdefault('notes', []).append(
            '这些章节的地图是从别的章节拷来的，已按引用补全: ' + ', '.join(sorted(fixed)))

    index.sort(key=lambda r: r['source'])
    _write_json(os.path.join(out_dir, 'campaign_index.json'),
                {'version': EXPORTER_VERSION, 'chapters': index})

    manifest['campaign'] = {
        **stats,
        'config_modules': sum(1 for r in index if r['config_present']),
        'config_complete': sum(1 for r in index
                               if r['config_present'] and r['config_complete']),
        'config_fields': sum(len(r['config_keys']) for r in index),
        'template_only_pct': round(100 * stats['template_only'] / max(stats['files'], 1), 1),
        'needs_review': sum(1 for r in index if r['needs_review']),
        'unresolved_detail': unresolved_all,
        'source_files': len(source_files),
        'source_hashes': {os.path.relpath(p, root).replace('\\', '/'): sha256_file(p)
                          for p in source_files},
    }
    return index


# --------------------------------------------------------------------- Schema
ASSETS_SCHEMA = {
    '$schema': 'https://json-schema.org/draft/2020-12/schema',
    'title': 'ALAS upstream asset bindings',
    'type': 'object',
    'required': ['version', 'servers', 'assets'],
    'properties': {
        'version': {'type': 'string'},
        'servers': {'type': 'array', 'items': {'enum': list(SERVERS)}},
        'assets': {
            'type': 'object',
            'additionalProperties': {
                'type': 'object',
                'required': ['id', 'name', 'module', 'kind', 'servers', 'source'],
                'properties': {
                    'id': {'type': 'string'},
                    'name': {'type': 'string'},
                    'module': {'type': 'string'},
                    'kind': {'enum': ['Button', 'Template', 'Mask']},
                    'area': {'$ref': '#/$defs/perServerArea'},
                    'button': {'$ref': '#/$defs/perServerArea'},
                    'color': {'$ref': '#/$defs/perServerColor'},
                    'file': {'$ref': '#/$defs/perServerPath'},
                    'servers': {'type': 'array', 'items': {'enum': list(SERVERS)}},
                    'all_servers': {'type': 'boolean'},
                    'source': {'type': 'string'},
                },
            },
        },
    },
    '$defs': {
        'area4': {'type': 'array', 'prefixItems': [{'type': 'integer'}] * 4,
                  'minItems': 4, 'maxItems': 4},
        'color3': {'type': 'array', 'prefixItems': [{'type': 'integer'}] * 3,
                   'minItems': 3, 'maxItems': 3},
        'perServerArea': {'type': 'object',
                          'additionalProperties': {'$ref': '#/$defs/area4'}},
        'perServerColor': {'type': 'object',
                           'additionalProperties': {'$ref': '#/$defs/color3'}},
        'perServerPath': {'type': 'object',
                          'additionalProperties': {'type': 'string'}},
    },
}

CAMPAIGN_SCHEMA = {
    '$schema': 'https://json-schema.org/draft/2020-12/schema',
    'title': 'ALAS campaign IR',
    'type': 'object',
    'required': ['source', 'map', 'config', 'config_meta', 'campaign'],
    'properties': {
        'source': {'type': 'string'},
        'name': {'type': ['string', 'null'],
                 'description': "CampaignMap('1-2') 里的关卡名"},
        'name_source': {'enum': ['CampaignMap', 'path'],
                        'description': 'path = 从文件名兜底派生，不是上游的权威名字'},
        'map': {'type': 'object', 'additionalProperties': True},
        'config': {'type': 'object', 'additionalProperties': True},
        'config_meta': {
            'type': 'object',
            'required': ['present', 'complete', 'mro', 'origins', 'typed_values',
                         'source_files', 'unresolved'],
            'properties': {
                'present': {'type': 'boolean'},
                'complete': {'type': 'boolean'},
                'mro': {'type': 'array', 'items': {'type': 'string'}},
                'origins': {'type': 'object', 'additionalProperties': {
                    'type': 'object',
                    'required': ['module', 'class', 'line', 'expression'],
                    'properties': {
                        'module': {'type': 'string'},
                        'class': {'type': 'string'},
                        'line': {'type': 'integer'},
                        'expression': {'type': 'string'},
                    },
                }},
                'typed_values': {'type': 'object', 'additionalProperties': True},
                'source_files': {'type': 'array', 'items': {'type': 'string'}},
                'unresolved': {'type': 'array', 'items': {'type': 'object'}},
            },
        },
        'campaign': {
            'type': 'object',
            'required': ['battles'],
            'properties': {
                'class': {'type': 'string'},
                'bases': {'type': 'array', 'items': {'type': 'string'}},
                'boss_battle': {'type': ['integer', 'null']},
                'plan_complete': {
                    'type': 'boolean',
                    'description': 'true = 所有 battle_* 方法体都被归一成 steps，无未识别语句'},
                'template_only': {
                    'type': 'boolean',
                    'description': 'true = 战斗计划可完全由 JSON 规则表驱动，无需插件'},
                'tier': {
                    'enum': ['A', 'B', 'C'],
                    'description': 'A=JSON 规则表即可；B=计划完整但用了词表外算子；C=需插件或原生实现',
                },
                'has_siren': {'type': 'boolean'},
                'attributes': {
                    'type': 'object',
                    'additionalProperties': True,
                    'description': 'Campaign 类数据属性；与章节 Config 分开保存',
                },
                'native_overrides': {
                    'type': 'array', 'items': {'type': 'string'},
                    'description': '非 battle_* 的覆写钩子且含真实逻辑，C# 引擎必须实现'},
                'super_delegates': {
                    'type': 'array', 'items': {'type': 'string'},
                    'description': '纯 return super().X() 的覆写，C# 侧只需虚方法分派，无新增逻辑'},
                'battles': {
                    'type': 'array',
                    'items': {
                        'type': 'object',
                        'required': ['method', 'calls'],
                        'properties': {
                            'method': {'type': 'string'},
                            'calls': {'type': 'array', 'items': {'type': 'string'}},
                            'steps': {
                                'type': 'array',
                                'description': 'plan_complete=false 时恒为空数组，防止残缺计划被误用',
                                'items': {
                                    'type': 'object',
                                    'required': ['op', 'kind'],
                                    'properties': {
                                        'op': {'type': 'string'},
                                        'kind': {'enum': ['conditional',
                                                          'conditional_negated',
                                                          'terminal', 'call', 'assign',
                                                          'super_delegate']},
                                        'args': {'type': 'object'},
                                        'target': {'type': 'string'},
                                    },
                                },
                            },
                            'plan_complete': {'type': 'boolean'},
                            'unparsed': {'type': 'array', 'items': {'type': 'string'},
                                         'description': '未能归一的语句类型，空数组才代表计划完整'},
                            'stmt_count': {'type': 'integer'},
                        },
                    },
                },
            },
        },
        'unresolved': {'type': 'array', 'items': {'type': 'string'}},
    },
}


# --------------------------------------------------------------------- 落盘
def _write_json(path: str, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(obj, f, ensure_ascii=False, indent=1, sort_keys=True)
        f.write('\n')


def git_rev(repo: str):
    try:
        top = subprocess.run(['git', '-C', repo, 'rev-parse', '--show-toplevel'],
                             capture_output=True, text=True, timeout=30)
        # A runtime snapshot inside the host checkout is not an upstream Git
        # checkout. Never label its sources with the enclosing host commit.
        if top.returncode or os.path.normcase(os.path.realpath(top.stdout.strip())) != \
                os.path.normcase(os.path.realpath(repo)):
            return None, None
        out = subprocess.run(['git', '-C', repo, 'rev-parse', 'HEAD'],
                             capture_output=True, text=True, timeout=30)
        if out.returncode:
            return None, None
        rev = out.stdout.strip()
        out2 = subprocess.run(['git', '-C', repo, 'status', '--porcelain'],
                              capture_output=True, text=True, timeout=30)
        return rev, out2.stdout.strip() if out2.returncode == 0 else None
    except Exception as e:
        return None, None


def main():
    ap = argparse.ArgumentParser(description='导出上游数据契约为 JSON')
    default_repo = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '..', '.runtime', 'engine')
    ap.add_argument('--repo', default=os.path.normpath(default_repo))
    ap.add_argument('--out', default=os.path.normpath(
        os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'data')))
    ap.add_argument('--check', action='store_true',
                    help='重新计算并与磁盘产物比对，不写入（用于 CI 检测上游数据漂移）')
    args = ap.parse_args()

    if not os.path.isdir(os.path.join(args.repo, 'module')):
        print(f'不是有效的 ALAS 仓库: {args.repo}', file=sys.stderr)
        return 2

    rev, dirty = git_rev(args.repo)
    manifest = {
        'exporter_version': EXPORTER_VERSION,
        'upstream_repo': os.path.abspath(args.repo),
        'upstream_commit': rev,
        'upstream_dirty': bool(dirty) if dirty is not None else None,
        'errors': [],
    }

    if args.check:
        tmp = os.path.join(args.out, '__check_tmp__')
        _run_export(args.repo, tmp, manifest)
        diffs = _compare_dirs(os.path.join(args.out), tmp)
        _rmtree(tmp)
        print(json.dumps({'mode': 'check', 'differences': diffs,
                          'ok': not diffs}, ensure_ascii=False, indent=2))
        return 0 if not diffs else 1

    _run_export(args.repo, args.out, manifest)
    summary = {
        'mode': 'export',
        'out': os.path.abspath(args.out),
        'upstream_commit': rev,
        'upstream_dirty': bool(dirty) if dirty is not None else None,
        'assets': {k: v for k, v in manifest['assets'].items()
                   if k not in ('source_hashes', 'by_module', 'unresolved')},
        'campaign': {k: v for k, v in manifest['campaign'].items()
                     if k not in ('source_hashes', 'unresolved_detail')},
        'errors': manifest['errors'],
        'assets_unresolved': len(manifest['assets']['unresolved']),
        'campaign_unresolved_files': len(manifest['campaign']['unresolved_detail']),
    }
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


def _run_export(repo, out, manifest):
    for name in ('assets', 'campaign'):
        manifest[name] = {}
    export_assets(repo, out, manifest)
    export_campaign(repo, out, manifest)
    _write_json(os.path.join(out, 'schema', 'assets.schema.json'), ASSETS_SCHEMA)
    _write_json(os.path.join(out, 'schema', 'campaign.schema.json'), CAMPAIGN_SCHEMA)
    _write_json(os.path.join(out, 'manifest.json'), manifest)


def _compare_dirs(a, b):
    """
    只比对**本导出器自己的产物**。

    注意不要把整个输出目录都拿进来比：data/ 下还会有别的工具产物
    （例如 tools/make_imaging_fixture.py 生成的 fixtures/），
    它们不在临时目录里，会被误报成差异（实测踩过）。
    """
    OWNED_FILES = {'assets.json', 'campaign_index.json', 'manifest.json'}
    OWNED_DIRS = ('campaign/', 'schema/', 'rules/')

    def owned(rel):
        return rel in OWNED_FILES or rel.startswith(OWNED_DIRS)

    diffs = []

    def walk(base):
        found = set()
        for dirpath, dirs, files in os.walk(base):
            dirs[:] = [d for d in dirs if d != '__check_tmp__']
            for f in files:
                rel = os.path.relpath(os.path.join(dirpath, f), base).replace('\\', '/')
                if owned(rel):
                    found.add(rel)
        return found

    fa, fb = walk(a), walk(b)
    for rel in sorted(fa ^ fb):
        diffs.append({'file': rel, 'issue': 'only-in-one-side'})
    for rel in sorted(fa & fb):
        pa, pb = os.path.join(a, rel), os.path.join(b, rel)
        if rel == 'manifest.json':
            # Paths/working-tree state are machine-specific. The remaining
            # provenance (including hashes) must participate in drift checks.
            def stable_manifest(path):
                with open(path, encoding='utf-8') as stream:
                    result = json.load(stream)
                for key in ('upstream_repo', 'upstream_dirty'):
                    result.pop(key, None)
                return result
            if stable_manifest(pa) != stable_manifest(pb):
                diffs.append({'file': rel, 'issue': 'provenance-differs'})
            continue
        if open(pa, 'rb').read() != open(pb, 'rb').read():
            diffs.append({'file': rel, 'issue': 'content-differs'})
    return diffs


def _rmtree(path):
    import shutil
    shutil.rmtree(path, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main())
