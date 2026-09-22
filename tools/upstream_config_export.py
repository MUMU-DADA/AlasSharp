"""Export effective upstream Config attributes without importing game code.

Python imports, aliases and C3 inheritance are resolved from source. Evaluation
is deliberately restricted to constants, containers and pure operators: a call
or another unsupported construct is recorded explicitly and cannot make an
export complete. Only the public data attributes of Config are exported.
"""
from __future__ import annotations

import ast
from dataclasses import dataclass, field
import importlib.util
import json
import math
import operator
from pathlib import Path
from typing import Any


class ResolutionError(ValueError):
    """Source semantics which this static contract cannot evaluate safely."""


@dataclass(frozen=True)
class _Module:
    name: str


@dataclass(frozen=True)
class _Class:
    module: str
    node: ast.ClassDef | None

    @property
    def name(self):
        return f'{self.module}.{self.node.name}' if self.node else 'builtins.object'


@dataclass
class _Unknown:
    reason: str


@dataclass
class _Field:
    value: Any
    origin: dict


@dataclass
class _ClassInfo:
    mro: list[_Class] = field()
    fields: dict[str, _Field]
    issues: list[dict]


_OBJECT = _Class('builtins', None)
_BINARY = {
    ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul,
    ast.Div: operator.truediv, ast.FloorDiv: operator.floordiv,
    ast.Mod: operator.mod, ast.Pow: operator.pow, ast.LShift: operator.lshift,
    ast.RShift: operator.rshift, ast.BitOr: operator.or_, ast.BitXor: operator.xor,
    ast.BitAnd: operator.and_,
}
_UNARY = {ast.UAdd: operator.pos, ast.USub: operator.neg,
          ast.Invert: operator.invert, ast.Not: operator.not_}
_COMPARE = {
    ast.Eq: operator.eq, ast.NotEq: operator.ne, ast.Lt: operator.lt,
    ast.LtE: operator.le, ast.Gt: operator.gt, ast.GtE: operator.ge,
    ast.In: lambda a, b: a in b, ast.NotIn: lambda a, b: a not in b,
    ast.Is: operator.is_, ast.IsNot: operator.is_not,
}


def _typed(value):
    """Lossless type tags, including tuple/list and non-string dictionary keys."""
    kind = type(value).__name__
    if value is None or type(value) in (bool, int, float, str):
        if isinstance(value, float) and not math.isfinite(value):
            raise ResolutionError('non-finite float has no portable JSON value')
        return {'type': kind, 'value': value}
    if type(value) in (list, tuple, set, frozenset):
        items = [_typed(item) for item in value]
        if type(value) in (set, frozenset):
            items.sort(key=lambda item: json.dumps(item, sort_keys=True))
        return {'type': kind, 'items': items}
    if type(value) is dict:
        return {'type': 'dict', 'items': [
            {'key': _typed(key), 'value': _typed(item)} for key, item in value.items()
        ]}
    raise ResolutionError(f'unsupported Config value type: {kind}')


def _plain(value):
    if type(value) in (set, frozenset):
        return [_plain(item) for item in sorted(value, key=lambda item: repr(_typed(item)))]
    if type(value) in (list, tuple):
        return [_plain(item) for item in value]
    if type(value) is dict:
        # Match JSON's ordinary object key encoding; typed_values keeps the exact
        # Python key type. Reject collisions instead of silently losing a key.
        result = {}
        for key, item in value.items():
            if type(key) not in (str, int, float, bool, type(None)):
                raise ResolutionError(f'dictionary key is not JSON-compatible: {key!r}')
            encoded_key = next(iter(json.loads(json.dumps({key: None}))))
            if encoded_key in result:
                raise ResolutionError(f'dictionary keys collide in JSON: {encoded_key!r}')
            result[encoded_key] = _plain(item)
        return result
    _typed(value)
    return value


class ConfigResolver:
    """Reusable resolver for a repository; source files are parsed only once.

    ``export(module)`` accepts a dotted module or a repository-relative .py path.
    ``values`` has ordinary JSON values for existing readers. ``typed_values``
    retains Python container and scalar types; ``origins`` records winning field
    declarations. ``complete`` is false whenever any required source semantics
    cannot be resolved. This exports chapter overrides, not AzurLaneConfig's
    global defaults, which are supplied by the upstream config merge at runtime.
    """

    def __init__(self, repo: str | Path):
        self.repo = Path(repo).resolve()
        self._trees: dict[str, tuple[ast.Module, Path | None, str]] = {}

    def module_name(self, module: str | Path):
        name = str(module).replace('\\', '/')
        if name.endswith('.py'):
            path = Path(module)
            if path.is_absolute():
                name = path.resolve().relative_to(self.repo).as_posix()
            name = name[:-3].replace('/', '.')
            if name.endswith('.__init__'):
                name = name[:-len('.__init__')]
        return name

    def export(self, module: str | Path):
        module = self.module_name(module)
        self._sources: set[str] = set()
        self._symbols: set[tuple[str, str, int | None]] = set()
        self._class_stack: set[_Class] = set()
        self._classes: dict[_Class, _ClassInfo] = {}
        result = dict(values={}, typed_values={}, origins={}, mro=[],
                      complete=True, unresolved=[], present=False, source_files=[])
        try:
            symbol = self._symbol(module, 'Config')
            if not isinstance(symbol, _Class):
                raise ResolutionError('Config does not resolve to a class')
            result['present'] = True
            info = self._class(symbol)
            result['mro'] = [item.name for item in info.mro]
            for item in info.mro:
                result['unresolved'].extend(self._class(item).issues)
            effective = {}
            # C3 lookup selects the first class defining a name, not the last
            # recursively merged base; this matters for diamonds.
            for item in reversed(info.mro):
                effective.update(self._class(item).fields)
            for name, field in sorted(effective.items()):
                if name.startswith('_'):
                    continue
                result['origins'][name] = field.origin
                try:
                    if isinstance(field.value, _Unknown):
                        raise ResolutionError(field.value.reason)
                    typed = _typed(field.value)
                    plain = _plain(field.value)
                except (ResolutionError, TypeError, ValueError) as error:
                    result['unresolved'].append(dict(field.origin, field=name,
                                                     reason=str(error)))
                    continue
                result['values'][name] = plain
                result['typed_values'][name] = typed
        except KeyError as error:
            # Missing Config is normal for helper modules. A missing imported
            # dependency is converted to ResolutionError at the import boundary.
            if error.args != ((module, 'Config'),):
                result['unresolved'].append({'module': module, 'reason': str(error)})
        except (ResolutionError, OSError, SyntaxError, ValueError) as error:
            result['unresolved'].append({'module': module, 'reason': str(error)})
        result['complete'] = not result['unresolved']
        result['source_files'] = sorted(self._sources)
        return result

    def _module(self, module):
        if module not in self._trees:
            relative = Path(*module.split('.'))
            path = self.repo / relative.with_suffix('.py')
            package = module.rpartition('.')[0]
            if not path.is_file():
                path = self.repo / relative / '__init__.py'
                package = module
            if path.is_file():
                tree = ast.parse(path.read_text(encoding='utf-8-sig'), filename=str(path))
            elif (self.repo / relative).is_dir():
                tree, path, package = ast.Module(body=[], type_ignores=[]), None, module
            else:
                raise ResolutionError(f'module source is unavailable: {module}')
            self._trees[module] = tree, path, package
        tree, path, package = self._trees[module]
        if path is not None:
            self._sources.add(path.relative_to(self.repo).as_posix())
        return tree, package

    def _import_module(self, module, node):
        if not node.level:
            return node.module or ''
        _tree, package = self._module(module)
        try:
            return importlib.util.resolve_name('.' * node.level + (node.module or ''), package)
        except (ValueError, ImportError) as error:
            raise ResolutionError(str(error)) from error

    def _imported(self, module, node, alias):
        if isinstance(node, ast.Import):
            target = alias.name if alias.asname else alias.name.split('.')[0]
            return _Module(target)
        target = self._import_module(module, node)
        try:
            return self._attribute(_Module(target), alias.name)
        except (KeyError, ResolutionError) as error:
            raise ResolutionError(f'cannot import {alias.name} from {target}: {error}') from error

    def _symbol(self, module, name, before=None):
        key = module, name, before
        if key in self._symbols:
            raise ResolutionError(f'cyclic symbol reference: {module}.{name}')
        self._symbols.add(key)
        try:
            tree, _package = self._module(module)
            binding = None
            for node in tree.body:
                if before is not None and node.lineno >= before:
                    break
                if isinstance(node, ast.ClassDef) and node.name == name:
                    binding = ('class', node)
                elif isinstance(node, (ast.Import, ast.ImportFrom)):
                    for alias in node.names:
                        local = alias.asname or (alias.name.split('.')[0]
                                                  if isinstance(node, ast.Import) else alias.name)
                        if local == name:
                            binding = ('import', node, alias)
                        elif alias.name == '*':
                            target = self._import_module(module, node)
                            try:
                                exported = self._symbol(target, '__all__')
                            except KeyError:
                                exported = None
                            if (exported is not None and name in exported) or (
                                    exported is None and not name.startswith('_')):
                                try:
                                    candidate = self._symbol(target, name)
                                except KeyError:
                                    continue
                                binding = ('value', candidate)
                elif isinstance(node, (ast.Assign, ast.AnnAssign, ast.AugAssign)):
                    targets = node.targets if isinstance(node, ast.Assign) else [node.target]
                    if any(name in self._target_names(target) for target in targets):
                        if isinstance(node, ast.AnnAssign) and node.value is None:
                            continue
                        binding = ('assignment', node, targets)
                elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == name:
                    binding = ('unsupported', node)
                elif not isinstance(node, (ast.Import, ast.ImportFrom, ast.Expr, ast.Pass,
                                           ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
                    # Do not return an earlier constant when a conditional or
                    # another unsupported statement may replace that binding.
                    for child in ast.walk(node):
                        if isinstance(child, ast.Name) and isinstance(child.ctx, ast.Store) and child.id == name:
                            binding = ('unsupported', node)
            if binding is None:
                if name == 'object':
                    return _OBJECT
                raise KeyError((module, name))
            kind, *payload = binding
            if kind == 'class':
                return _Class(module, payload[0])
            if kind == 'import':
                return self._imported(module, *payload)
            if kind == 'value':
                return payload[0]
            if kind == 'assignment':
                node, targets = payload
                value = self._eval(node.value, module, node.lineno, {})
                if isinstance(node, ast.AugAssign):
                    previous = self._symbol(module, name, node.lineno)
                    return self._binary(node.op, previous, value)
                assigned = {}
                for target in targets:
                    self._assign(target, value, assigned)
                return assigned[name]
            raise ResolutionError(f'unsupported binding {module}.{name} at line {payload[0].lineno}')
        finally:
            self._symbols.remove(key)

    @staticmethod
    def _target_names(target):
        return [node.id for node in ast.walk(target)
                if isinstance(node, ast.Name) and isinstance(node.ctx, ast.Store)]

    def _attribute(self, value, name):
        if isinstance(value, _Module):
            try:
                return self._symbol(value.name, name)
            except KeyError:
                submodule = value.name + '.' + name
                self._module(submodule)
                return _Module(submodule)
        if isinstance(value, _Class):
            info = self._class(value)
            if any(self._class(item).issues for item in info.mro):
                raise ResolutionError(f'class {value.name} has unresolved statements')
            for item in info.mro:
                if name in self._class(item).fields:
                    result = self._class(item).fields[name].value
                    if isinstance(result, _Unknown):
                        raise ResolutionError(result.reason)
                    return result
            raise ResolutionError(f'class attribute is unavailable: {value.name}.{name}')
        raise ResolutionError(f'attribute access on {type(value).__name__} is unsupported')

    def _class(self, cls):
        if cls in self._classes:
            return self._classes[cls]
        if cls is _OBJECT:
            result = _ClassInfo([_OBJECT], {}, [])
            self._classes[cls] = result
            return result
        if cls in self._class_stack:
            raise ResolutionError(f'cyclic class inheritance: {cls.name}')
        self._class_stack.add(cls)
        try:
            node = cls.node
            if node.decorator_list or node.keywords or getattr(node, 'type_params', []):
                raise ResolutionError(f'unsupported class transformation: {cls.name}')
            bases = [self._eval(base, cls.module, node.lineno, {}) for base in node.bases] or [_OBJECT]
            if any(not isinstance(base, _Class) for base in bases):
                raise ResolutionError(f'non-class base in {cls.name}')
            if len(set(bases)) != len(bases):
                raise ResolutionError(f'duplicate base class in {cls.name}')
            pending = [self._class(base).mro.copy() for base in bases] + [bases.copy()]
            mro = [cls]
            while any(pending):
                pending = [items for items in pending if items]
                candidate = next((items[0] for items in pending
                                  if all(items[0] not in other[1:] for other in pending)), None)
                if candidate is None:
                    raise ResolutionError(f'inconsistent C3 method resolution order in {cls.name}')
                mro.append(candidate)
                for items in pending:
                    if items[0] == candidate:
                        items.pop(0)
            fields, local, issues = {}, {}, []
            for statement in node.body:
                if isinstance(statement, ast.Pass) or (
                        isinstance(statement, ast.Expr) and isinstance(statement.value, ast.Constant)
                        and isinstance(statement.value.value, str)):
                    continue
                if isinstance(statement, (ast.Assign, ast.AnnAssign, ast.AugAssign)):
                    if isinstance(statement, ast.AnnAssign) and statement.value is None:
                        continue
                    targets = statement.targets if isinstance(statement, ast.Assign) else [statement.target]
                    names = [name for target in targets for name in self._target_names(target)]
                    assigned = {}
                    try:
                        value = self._eval(statement.value, cls.module, node.lineno, local)
                        if isinstance(statement, ast.AugAssign):
                            name = statement.target.id if isinstance(statement.target, ast.Name) else None
                            if name is None:
                                raise ResolutionError('only name augmented assignment is supported')
                            previous = self._eval(ast.Name(id=name, ctx=ast.Load()), cls.module, node.lineno, local)
                            value = self._binary(statement.op, previous, value)
                        for target in targets:
                            self._assign(target, value, assigned)
                    except (ResolutionError, KeyError, TypeError, ValueError, ArithmeticError) as error:
                        assigned = {name: _Unknown(str(error)) for name in names}
                        if not names:
                            issues.append(self._issue(cls, statement, str(error)))
                    for name, value in assigned.items():
                        local[name] = value
                        fields[name] = _Field(value, self._origin(cls, statement))
                else:
                    issues.append(self._issue(cls, statement,
                                              f'unsupported Config statement: {type(statement).__name__}'))
                    # Preserve uncertainty for names assigned by that statement,
                    # so they cannot masquerade as an inherited resolved value.
                    for child in ast.walk(statement):
                        if isinstance(child, ast.Name) and isinstance(child.ctx, ast.Store):
                            unknown = _Unknown('binding depends on an unsupported Config statement')
                            fields[child.id] = _Field(unknown, self._origin(cls, statement))
                            local[child.id] = unknown
            result = _ClassInfo(mro, fields, issues)
            self._classes[cls] = result
            return result
        finally:
            self._class_stack.remove(cls)

    @staticmethod
    def _origin(cls, node):
        return {'module': cls.module, 'class': cls.node.name, 'line': node.lineno,
                'expression': ast.unparse(node.value) if isinstance(node, (ast.Assign, ast.AnnAssign, ast.AugAssign))
                else ast.unparse(node)}

    def _issue(self, cls, node, reason):
        return dict(self._origin(cls, node), reason=reason)

    def _assign(self, target, value, assigned):
        if isinstance(target, ast.Name):
            assigned[target.id] = value
            return
        if not isinstance(target, (ast.Tuple, ast.List)) or type(value) not in (tuple, list):
            raise ResolutionError('unsupported assignment target or unpacking value')
        stars = [i for i, item in enumerate(target.elts) if isinstance(item, ast.Starred)]
        if len(stars) > 1 or (not stars and len(target.elts) != len(value)) or (
                stars and len(value) < len(target.elts) - 1):
            raise ResolutionError('invalid unpacking assignment')
        if not stars:
            for item, item_value in zip(target.elts, value):
                self._assign(item, item_value, assigned)
        else:
            index = stars[0]
            suffix = len(target.elts) - index - 1
            for item, item_value in zip(target.elts[:index], value[:index]):
                self._assign(item, item_value, assigned)
            self._assign(target.elts[index].value,
                         list(value[index:len(value) - suffix if suffix else None]), assigned)
            if suffix:
                for item, item_value in zip(target.elts[index + 1:], value[-suffix:]):
                    self._assign(item, item_value, assigned)

    @staticmethod
    def _binary(op, left, right):
        if type(op) not in _BINARY or isinstance(left, (_Module, _Class, _Unknown)) or isinstance(right, (_Module, _Class, _Unknown)):
            raise ResolutionError(f'unsupported constant operator: {type(op).__name__}')
        # Bound operations before allocating large sequences or integers.
        if isinstance(op, (ast.Pow, ast.LShift, ast.RShift)) and (
                not isinstance(right, (int, float)) or abs(right) > 10000):
            raise ResolutionError('constant exponent or shift exceeds evaluation limit')
        if isinstance(op, ast.Mult):
            for seq, count in ((left, right), (right, left)):
                if isinstance(seq, (str, list, tuple)) and isinstance(count, int) and len(seq) * count > 100000:
                    raise ResolutionError('constant repetition exceeds evaluation limit')
        try:
            result = _BINARY[type(op)](left, right)
        except (TypeError, ValueError, ArithmeticError) as error:
            raise ResolutionError(str(error)) from error
        if isinstance(result, int) and result.bit_length() > 65536:
            raise ResolutionError('constant integer exceeds evaluation limit')
        return result

    def _eval(self, node, module, before, local):
        if isinstance(node, ast.Constant):
            return node.value
        if isinstance(node, ast.Name):
            value = local[node.id] if node.id in local else self._symbol(module, node.id, before)
            if isinstance(value, _Unknown):
                raise ResolutionError(value.reason)
            return value
        if isinstance(node, ast.Attribute):
            return self._attribute(self._eval(node.value, module, before, local), node.attr)
        if isinstance(node, (ast.Tuple, ast.List, ast.Set)):
            values = []
            for item in node.elts:
                if isinstance(item, ast.Starred):
                    starred = self._eval(item.value, module, before, local)
                    if type(starred) not in (list, tuple, set, frozenset, dict, str):
                        raise ResolutionError('unsupported starred constant')
                    values.extend(starred)
                else:
                    values.append(self._eval(item, module, before, local))
            return {ast.Tuple: tuple, ast.List: list, ast.Set: set}[type(node)](values)
        if isinstance(node, ast.Dict):
            result = {}
            for key, value in zip(node.keys, node.values):
                item = self._eval(value, module, before, local)
                if key is None:
                    if type(item) is not dict:
                        raise ResolutionError('dictionary unpacking requires a constant dictionary')
                    result.update(item)
                else:
                    result[self._eval(key, module, before, local)] = item
            return result
        if isinstance(node, ast.BinOp):
            return self._binary(node.op, self._eval(node.left, module, before, local),
                                self._eval(node.right, module, before, local))
        if isinstance(node, ast.UnaryOp) and type(node.op) in _UNARY:
            return _UNARY[type(node.op)](self._eval(node.operand, module, before, local))
        if isinstance(node, ast.BoolOp):
            value = self._eval(node.values[0], module, before, local)
            for item in node.values[1:]:
                if (isinstance(node.op, ast.And) and not value) or (isinstance(node.op, ast.Or) and value):
                    break
                value = self._eval(item, module, before, local)
            return value
        if isinstance(node, ast.IfExp):
            branch = node.body if self._eval(node.test, module, before, local) else node.orelse
            return self._eval(branch, module, before, local)
        if isinstance(node, ast.Compare):
            left = self._eval(node.left, module, before, local)
            for op, comparator in zip(node.ops, node.comparators):
                if type(op) not in _COMPARE:
                    raise ResolutionError(f'unsupported comparison: {type(op).__name__}')
                right = self._eval(comparator, module, before, local)
                if not _COMPARE[type(op)](left, right):
                    return False
                left = right
            return True
        if isinstance(node, ast.Subscript):
            value = self._eval(node.value, module, before, local)
            if type(value) not in (list, tuple, dict, str):
                raise ResolutionError('subscript requires a constant container')
            if isinstance(node.slice, ast.Slice):
                index = slice(*(self._eval(item, module, before, local) if item else None
                                for item in (node.slice.lower, node.slice.upper, node.slice.step)))
            else:
                index = self._eval(node.slice, module, before, local)
            return value[index]
        raise ResolutionError(f'unsupported constant expression: {ast.unparse(node)}')
