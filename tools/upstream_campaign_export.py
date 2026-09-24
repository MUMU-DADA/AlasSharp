"""Lossless Campaign class declarations for offline provenance, never execution.

This records the selected class's own assignments (not inherited runtime state).
Native CampaignRun and Python's MRO remain the authority for effective behavior.
"""
from __future__ import annotations

import ast
from dataclasses import dataclass

try:
    from .upstream_config_export import ResolutionError, _Class, _Field, _Unknown
    from .upstream_map_export import MapResolver, plain, typed
except ImportError:
    from upstream_config_export import ResolutionError, _Class, _Field, _Unknown
    from upstream_map_export import MapResolver, plain, typed


@dataclass(frozen=True)
class _Function:
    module: str
    name: str


class CampaignResolver(MapResolver):
    def export(self, module):
        module = self.module_name(module)
        self._sources, self._symbols, self._class_stack, self._classes = set(), set(), set(), {}
        self._map_cache = {}
        result = dict(values={}, present=False, complete=True, scope='declared',
                      class_reference=None, origins={}, typed_values={},
                      method_aliases={}, source_files=[], unresolved=[])
        try:
            cls = self._symbol(module, 'Campaign')
            if not isinstance(cls, _Class) or cls.node is None:
                raise ResolutionError('Campaign does not resolve to a source class')
            result.update(present=True, class_reference=cls.name)
            fields, local = {}, {}
            for statement in cls.node.body:
                if isinstance(statement, (ast.FunctionDef, ast.AsyncFunctionDef)):
                    # A method definition shadows a prior field/alias. Decorated
                    # methods are not evaluated; aliases to them stay unresolved.
                    local[statement.name] = (_Unknown('alias to a decorated method requires native execution')
                        if statement.decorator_list else _Function(cls.module, cls.node.name + '.' + statement.name))
                    fields.pop(statement.name, None)
                    continue
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
                        value = self._eval(statement.value, cls.module, cls.node.lineno, local)
                        if isinstance(statement, ast.AugAssign):
                            if not isinstance(statement.target, ast.Name):
                                raise ResolutionError('only name augmented assignment is supported')
                            previous = self._eval(ast.Name(id=statement.target.id, ctx=ast.Load()),
                                                  cls.module, cls.node.lineno, local)
                            if type(previous) in (list, dict, set):
                                raise ResolutionError('mutable augmented assignment requires native alias semantics')
                            value = self._binary(statement.op, previous, value)
                        for target in targets:
                            self._assign(target, value, assigned)
                    except (ResolutionError, KeyError, TypeError, ValueError, IndexError, ArithmeticError) as error:
                        assigned = {name: _Unknown(str(error)) for name in names}
                        if not names:
                            result['unresolved'].append(self._issue(cls, statement, str(error)))
                    for name, value in assigned.items():
                        local[name] = value
                        if name != 'MAP':  # Independently exported and checked by MapResolver.
                            fields[name] = _Field(value, self._origin(cls, statement))
                    continue
                result['unresolved'].append(self._issue(cls, statement,
                    f'unsupported Campaign declaration: {type(statement).__name__}'))
                for child in ast.walk(statement):
                    if isinstance(child, ast.Name) and isinstance(child.ctx, ast.Store):
                        unknown = _Unknown('binding depends on unsupported Campaign declaration')
                        local[child.id] = unknown
                        if child.id != 'MAP':
                            fields[child.id] = _Field(unknown, self._origin(cls, statement))
            if cls.node.decorator_list or cls.node.keywords or getattr(cls.node, 'type_params', []):
                result['unresolved'].append(self._issue(cls, cls.node, 'unsupported class transformation'))
            for name, item in sorted(fields.items()):
                result['origins'][name] = item.origin
                try:
                    if isinstance(item.value, _Unknown):
                        raise ResolutionError(item.value.reason)
                    if isinstance(item.value, _Function):
                        result['method_aliases'][name] = dict(module=item.value.module, name=item.value.name)
                    else:
                        value_type, value_plain = typed(item.value), plain(item.value)
                        result['typed_values'][name] = value_type
                        result['values'][name] = value_plain
                except (ResolutionError, TypeError, ValueError) as error:
                    result['unresolved'].append(dict(item.origin, field=name, reason=str(error)))
        except KeyError as error:
            if error.args != ((module, 'Campaign'),):
                result['unresolved'].append(dict(module=module, reason=str(error)))
        except (ResolutionError, OSError, SyntaxError, ValueError, TypeError, ArithmeticError, RecursionError) as error:
            result['unresolved'].append(dict(module=module, reason=str(error)))
        result['complete'] = not result['unresolved']
        result['source_files'] = sorted(self._sources)
        return result
