"""Discover native UI control declarations without executing game modules.

This catalog records source locations, not coordinates or replacement rules.
Actual controls are always resolved from the original upstream module/class.
"""
from __future__ import annotations

import ast
import importlib.util
from pathlib import Path

ROOT_TYPES = {f'module.ui.{name.lower()}.{name}': name
              for name in ('Switch', 'Scroll', 'Navbar', 'Setting')}


def discover_controls(root):
    root = Path(root)
    modules, aliases, classes, errors = {}, {}, {}, []
    for path in sorted((root / 'module').rglob('*.py')):
        if '__pycache__' in path.parts:
            continue
        name = '.'.join(path.relative_to(root).with_suffix('').parts)
        if name.endswith('.__init__'):
            name = name[:-9]
        try:
            tree = ast.parse(path.read_text(encoding='utf-8'))
        except (SyntaxError, OSError) as error:
            errors.append(dict(source=path.relative_to(root).as_posix(), error=str(error)))
            continue
        modules[name] = tree
        bindings = aliases[name] = {}
        package = name if path.name == '__init__.py' else name.rpartition('.')[0]
        for node in tree.body:
            if isinstance(node, ast.ImportFrom):
                target = node.module or ''
                if node.level:
                    target = importlib.util.resolve_name('.' * node.level + target, package)
                for alias in node.names:
                    if alias.name != '*':
                        bindings[alias.asname or alias.name] = f'{target}.{alias.name}'
            elif isinstance(node, ast.Import):
                for alias in node.names:
                    bindings[alias.asname or alias.name.split('.')[0]] = (
                        alias.name if alias.asname else alias.name.split('.')[0])
            elif isinstance(node, ast.ClassDef):
                qualified = f'{name}.{node.name}'
                bindings[node.name] = qualified
                classes[qualified] = (name, node)

    def resolve(module, node):
        if isinstance(node, ast.Name):
            return aliases[module].get(node.id, f'{module}.{node.id}')
        if isinstance(node, ast.Attribute):
            parent = resolve(module, node.value)
            return f'{parent}.{node.attr}' if parent else None
        return None

    kinds = dict(ROOT_TYPES)

    def kind(name, visiting=None):
        if name in kinds:
            return kinds[name]
        visiting = set(visiting or ())
        if name in visiting or name not in classes:
            return None
        visiting.add(name)
        module, node = classes[name]
        for base in node.bases:
            found = kind(resolve(module, base), visiting)
            if found:
                kinds[name] = found
                return found
        return None

    records = []
    for module, tree in modules.items():
        def visit(node, owner=None, function=None):
            if isinstance(node, ast.ClassDef):
                for child in node.body:
                    visit(child, node.name, None)
                return
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                for child in node.body:
                    visit(child, owner, node)
                return
            value = node.value if isinstance(node, (ast.Assign, ast.AnnAssign)) else None
            if isinstance(value, ast.Call) and owner is None and function is None:
                typ = resolve(module, value.func)
                category = kind(typ)
                targets = node.targets if isinstance(node, ast.Assign) else [node.target]
                for target in targets:
                    if category and isinstance(target, ast.Name):
                        records.append(dict(module=module, name=target.id, attr=target.id,
                                            class_name=typ, kind=category,
                                            scope='module', line=node.lineno))
            if isinstance(node, ast.Call) and function is not None:
                typ = resolve(module, node.func)
                category = kind(typ)
                if category:
                    decorators = [ast.unparse(d).rsplit('.', 1)[-1] for d in function.decorator_list]
                    records.append(dict(module=module, owner=owner, attr=function.name,
                                        class_name=typ, kind=category, line=node.lineno,
                                        scope='property' if 'cached_property' in decorators
                                        or 'property' in decorators else 'factory'))
            for child in ast.iter_child_nodes(node):
                visit(child, owner, function)
        visit(tree)
    return dict(declarations=records, errors=errors)
