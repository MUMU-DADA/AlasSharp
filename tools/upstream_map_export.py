"""Static MAP declarations, with symbolic native grid/class references.

This is export metadata, never a runtime map implementation. No game module is
executed. Native CampaignMap owns setters, geometry, detection and all actions.
"""
from __future__ import annotations

import ast
import copy
from dataclasses import dataclass, field
import re

try:
    from .upstream_config_export import ConfigResolver, ResolutionError, _Class, _Module, _Field, _Unknown, _plain, _typed
except ImportError:
    from upstream_config_export import ConfigResolver, ResolutionError, _Class, _Module, _Field, _Unknown, _plain, _typed


@dataclass(frozen=True)
class _Grid:
    location: tuple[int, int]


@dataclass(frozen=True)
class _Copy:
    name: str


@dataclass
class _Map:
    module: str
    identity: tuple = ()
    name: str | None = None
    fields: dict = field(default_factory=dict)
    issues: list = field(default_factory=list)
    calls: list = field(default_factory=list)
    grid_locations: list | None = field(default_factory=list)
    derived_from: str | None = None


def typed(value):
    if isinstance(value, _Grid):
        return {'type': 'grid', 'location': list(value.location)}
    if isinstance(value, _Class):
        return {'type': 'reference', 'kind': 'class', 'module': value.module,
                'name': value.node.name if value.node else 'object'}
    if type(value) in (list, tuple, set, frozenset):
        if type(value) in (set, frozenset):
            value_items = sorted(value, key=repr)
        else:
            value_items = value
        return {'type': type(value).__name__, 'items': [typed(item) for item in value_items]}
    if type(value) is dict:
        return {'type': 'dict', 'items': [{'key': typed(key), 'value': typed(item)}
                                        for key, item in value.items()]}
    return _typed(value)


def plain(value):
    if isinstance(value, _Grid):
        x, y = value.location
        return chr(x + ord('A')) + str(y + 1)
    if isinstance(value, _Class):
        return {'$ref': value.name, 'kind': 'class'}
    if type(value) in (list, tuple, set, frozenset):
        return [plain(item) for item in (sorted(value, key=repr)
                if type(value) in (set, frozenset) else value)]
    if type(value) is dict:
        return _plain({key: plain(item) for key, item in value.items()})
    return _plain(value)


class MapResolver(ConfigResolver):
    """Resolve source assignments generically; unknown fields are never dropped."""

    def export(self, module):
        module = self.module_name(module)
        self._sources, self._symbols, self._class_stack, self._classes = set(), set(), set(), {}
        self._map_cache = {}
        result = dict(values={}, name=None, present=False, complete=True,
                      origins={}, typed_values={}, source_files=[], unresolved=[], derived_from=None, calls=[])
        try:
            value = self._symbol(module, 'MAP')
            if not isinstance(value, _Map):
                raise ResolutionError('MAP does not resolve to a native CampaignMap declaration')
            result.update(present=True, name=value.name, derived_from=value.derived_from)
            result['calls'] = value.calls
            result['unresolved'].extend(value.issues)
            for name, item in sorted(value.fields.items()):
                result['origins'][name] = item.origin
                try:
                    if isinstance(item.value, _Unknown):
                        raise ResolutionError(item.value.reason)
                    result['typed_values'][name] = typed(item.value)
                    result['values'][name] = plain(item.value)
                except (ResolutionError, TypeError, ValueError) as error:
                    result['unresolved'].append(dict(item.origin, field=name, reason=str(error)))
        except KeyError as error:
            if error.args != ((module, 'MAP'),):
                result['unresolved'].append(dict(module=module, reason=str(error)))
        except (ResolutionError, OSError, SyntaxError, ValueError, TypeError, ArithmeticError, RecursionError) as error:
            result['unresolved'].append(dict(module=module, reason=str(error)))
        result['complete'] = not result['unresolved']
        result['source_files'] = sorted(self._sources)
        return result

    def _imported(self, module, node, alias):
        if isinstance(node, ast.ImportFrom) and self._import_module(module, node) == 'copy' \
                and alias.name in ('copy', 'deepcopy'):
            return _Copy(alias.name)
        return super()._imported(module, node, alias)

    def _attribute(self, value, name):
        if isinstance(value, _Module) and value.name == 'copy' and name in ('copy', 'deepcopy'):
            return _Copy(name)
        if isinstance(value, _Map):
            if name == 'name':
                return value.name
            tree, _ = self._module('module.map.map_base')
            native = next(c for c in tree.body if isinstance(c, ast.ClassDef) and c.name == 'CampaignMap')
            if any(isinstance(m, ast.FunctionDef) and m.name == name
                   and any(isinstance(d, ast.Name) and d.id == 'property' for d in m.decorator_list)
                   for m in native.body):
                raise ResolutionError(f'MAP.{name} requires a native property getter, not its source declaration')
            item = value.fields.get(name)
            if item is None or isinstance(item.value, _Unknown):
                raise ResolutionError(f'MAP.{name} is not statically available')
            return item.value
        return super()._attribute(value, name)

    def _symbol(self, module, name, before=None):
        key = module, name, before
        if key in self._map_cache:
            return copy.deepcopy(self._map_cache[key])
        value = super()._symbol(module, name, before)
        if not isinstance(value, _Map):
            return value
        # Find the last binding already selected by ConfigResolver; apply only
        # subsequent declarations, stopping before the requesting expression.
        tree, _ = self._module(module)
        binding = 0
        for node in tree.body:
            if before is not None and node.lineno >= before:
                break
            if isinstance(node, (ast.Assign, ast.AnnAssign)):
                targets = node.targets if isinstance(node, ast.Assign) else [node.target]
                if any(name in self._target_names(target) for target in targets):
                    binding = node.lineno
            elif isinstance(node, (ast.Import, ast.ImportFrom)):
                if any((alias.asname or alias.name.split('.')[0]) == name for alias in node.names):
                    binding = node.lineno
        value = copy.deepcopy(value)
        if value.module != module and name == 'MAP':
            value.derived_from = value.module.replace('.', '/') + '.py'
            value.module = module
        for node in tree.body:
            if node.lineno <= binding or (before is not None and node.lineno >= before):
                continue
            if isinstance(node, (ast.Assign, ast.AnnAssign, ast.AugAssign)):
                if isinstance(node, ast.AnnAssign) and node.value is None:
                    continue
                targets = node.targets if isinstance(node, ast.Assign) else [node.target]
                for target in targets:
                    if not (isinstance(target, ast.Attribute) and isinstance(target.value, ast.Name)
                            and target.value.id == name):
                        self._check_mutation(value, target, module, node, name)
                        continue
                    origin = dict(module=module, line=node.lineno, expression=ast.unparse(node.value))
                    try:
                        resolved = self._eval(node.value, module, node.lineno, {name: value})
                        if isinstance(node, ast.AugAssign):
                            resolved = self._binary(node.op, self._attribute(value, target.attr), resolved)
                    except (ResolutionError, KeyError, ValueError, TypeError, IndexError, ArithmeticError) as error:
                        resolved = _Unknown(str(error))
                    value.fields[target.attr] = _Field(resolved, origin)
                    if target.attr == 'name':
                        value.name = resolved if isinstance(resolved, str) else None
                    if target.attr == 'shape' or (target.attr == 'map_data' and value.grid_locations == []):
                        try:
                            self._update_grids(value, target.attr, resolved)
                        except ResolutionError as error:
                            value.grid_locations = None
                            value.issues.append(dict(origin, field=target.attr, reason=str(error)))
            elif not isinstance(node, (ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
                if not isinstance(node, ast.Expr) and any(
                        isinstance(child, ast.Name) and child.id == name for child in ast.walk(node)):
                    value.issues.append(dict(module=module, line=node.lineno,
                                             reason='unsupported control flow or deletion referencing MAP',
                                             expression=ast.unparse(node)))
                mutations = [child for child in ast.walk(node) if isinstance(child, ast.Attribute)
                             and isinstance(child.value, ast.Name) and child.value.id == name
                             and isinstance(child.ctx, ast.Store)]
                for target in mutations:
                    if target.attr == 'shape':
                        value.grid_locations = None
                    origin = dict(module=module, line=node.lineno, expression=ast.unparse(node))
                    value.fields[target.attr] = _Field(_Unknown('conditional MAP mutation'), origin)
                if isinstance(node, ast.Expr) and isinstance(node.value, ast.Call) \
                        and any(isinstance(child, ast.Name) and child.id == name for child in ast.walk(node)):
                    call = node.value
                    origin = dict(module=module, line=node.lineno, expression=ast.unparse(node))
                    if isinstance(call.func, ast.Attribute) and isinstance(call.func.value, ast.Name) \
                            and call.func.value.id == name:
                        try:
                            # Preserve native calls as source metadata, never execute
                            # them or translate them into adapter map behavior.
                            native_tree, _ = self._module('module.map.map_base')
                            native = next(c for c in native_tree.body
                                          if isinstance(c, ast.ClassDef) and c.name == 'CampaignMap')
                            if not any(isinstance(m, ast.FunctionDef) and m.name == call.func.attr for m in native.body):
                                raise ResolutionError('MAP method is not declared by native CampaignMap')
                            args = self._eval(ast.Tuple(elts=call.args, ctx=ast.Load()), module, node.lineno, {name: value})
                            kwargs = self._eval(ast.Dict(keys=[ast.Constant(k.arg) if k.arg else None for k in call.keywords],
                                                        values=[k.value for k in call.keywords]), module, node.lineno, {name: value})
                            value.calls.append(dict(method=call.func.attr, args=plain(args), kwargs=plain(kwargs),
                                                    typed_args=typed(args), typed_kwargs=typed(kwargs), origin=origin))
                        except (ResolutionError, KeyError, ValueError, TypeError, StopIteration) as error:
                            value.issues.append(dict(origin, reason=str(error)))
                    else:
                        value.issues.append(dict(origin, reason='unsupported MAP method statement'))
        self._map_cache[key] = copy.deepcopy(value)
        return value

    @staticmethod
    def _update_grids(value, attr, data):
        if attr == 'shape':
            if not isinstance(data, str) or not re.fullmatch(r'[A-Za-z][1-9][0-9]*', data):
                raise ResolutionError('MAP.shape is not a static native grid bound')
            width, height = ord(data[0].upper()) - ord('A') + 1, int(data[1:])
        else:
            if not isinstance(data, str):
                raise ResolutionError('MAP.map_data is not static text')
            # Match CampaignMap._parse_text including repeated spaces/blank rows.
            rows = data.strip().split('\n')
            width, height = max(len(row.strip().split(' ')) for row in rows), len(rows)
        if width * height > 100000:
            raise ResolutionError('MAP grid declaration exceeds static evaluation limit')
        if value.grid_locations is None:
            raise ResolutionError('prior MAP grid declaration is unresolved')
        locations = dict.fromkeys(value.grid_locations)
        locations.update(((x, y), None) for y in range(height) for x in range(width))
        value.grid_locations = list(locations)

    def _check_mutation(self, value, target, module, node, name):
        # A nested/subscript or alias mutation must not leave a stale field marked
        # complete. Such imperative code is deliberately outside this declaration
        # contract; native runtime semantics remain owned by CampaignMap.
        root = target
        while isinstance(root, (ast.Attribute, ast.Subscript)):
            root = root.value
        if not isinstance(root, ast.Name) or root is target:
            return
        related = root.id == name
        if not related:
            try:
                other = self._symbol(module, root.id, node.lineno)
                related = isinstance(other, _Map) and other.identity == value.identity
            except (ResolutionError, KeyError):
                pass
        if related:
            value.issues.append(dict(module=module, line=node.lineno,
                                     reason='unsupported nested or alias MAP mutation',
                                     expression=ast.unparse(node)))

    def _eval(self, node, module, before, local):
        if isinstance(node, ast.Call):
            if isinstance(node.func, ast.Attribute) and node.func.attr == 'flatten':
                value = self._eval(node.func.value, module, before, local)
                if isinstance(value, _Map) and not node.args and not node.keywords:
                    if value.grid_locations is None:
                        raise ResolutionError('MAP.flatten has unresolved grid declarations')
                    if value.calls:
                        raise ResolutionError('MAP.flatten follows imperative native calls')
                    return [_Grid(location) for location in value.grid_locations]
            function = self._eval(node.func, module, before, local)
            if isinstance(function, _Class) and function.name == 'module.map.map_base.CampaignMap':
                if len(node.args) > 1 or any(k.arg != 'name' for k in node.keywords) \
                        or (node.args and node.keywords):
                    raise ResolutionError('unsupported CampaignMap constructor arguments')
                arg = node.args[0] if node.args else next((k.value for k in node.keywords), None)
                name = self._eval(arg, module, before, local) if arg is not None else None
                if name is not None and not isinstance(name, str):
                    raise ResolutionError('CampaignMap name must be a string or None')
                return _Map(module=module, identity=(module, node.lineno), name=name)
            if isinstance(function, _Copy) and len(node.args) == 1 and not node.keywords:
                original = self._eval(node.args[0], module, before, local)
                if not isinstance(original, _Map):
                    raise ResolutionError('copy target is not a static CampaignMap')
                value = copy.deepcopy(original)
                value.derived_from = original.module.replace('.', '/') + '.py'
                value.module = module
                value.identity = (module, node.lineno)
                return value
            raise ResolutionError(f'unsupported MAP expression: {ast.unparse(node)}')
        return super()._eval(node, module, before, local)
