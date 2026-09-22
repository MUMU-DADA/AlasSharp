"""Guard the upstream camera's comparison when no previous view exists.

During the first Camera outside map recovery, upstream _map_swipe() can call
update(wait_swipe=True) before _prev_view has been set. A screenshot faster than
the 0.35 second swipe timer then compares center_offset with None.

Only that comparison changes. Recompile the installed upstream method after
adding a short-circuit guard to its nested is_still_prev return expression, so
all timing, movement, retry, and view-data behavior stays in the upstream code.
There is no copied update implementation or map-specific condition here.
"""
from __future__ import annotations

import ast
from functools import update_wrapper
import inspect
import textwrap


def _has_none_guard(value):
    return isinstance(value, ast.BoolOp) and isinstance(value.op, ast.And) and any(
        isinstance(item, ast.Compare)
        and isinstance(item.left, ast.Name) and item.left.id == 'prev_center_offset'
        and len(item.ops) == 1 and isinstance(item.ops[0], ast.IsNot)
        and len(item.comparators) == 1
        and isinstance(item.comparators[0], ast.Constant)
        and item.comparators[0].value is None
        for item in value.values[:1]
    )


def _has_early_none_return(helper):
    if not helper.body or not isinstance(helper.body[0], ast.If):
        return False
    branch = helper.body[0]
    condition = branch.test
    return (isinstance(condition, ast.Compare)
            and isinstance(condition.left, ast.Name) and condition.left.id == 'prev_center_offset'
            and len(condition.ops) == 1 and isinstance(condition.ops[0], ast.Is)
            and len(condition.comparators) == 1
            and isinstance(condition.comparators[0], ast.Constant)
            and condition.comparators[0].value is None
            and len(branch.body) == 1 and isinstance(branch.body[0], ast.Return)
            and isinstance(branch.body[0].value, ast.Constant)
            and branch.body[0].value.value is False)


def guard_previous_view_comparison(original):
    """Return a minimally guarded upstream function, or original if already fixed.

    Refuse a changed upstream structure instead of replacing an unknown method.
    The compiled function retains the original globals and source line numbers.
    """
    if getattr(original, '_alas_previous_view_compat', False):
        return original
    lines, first_line = inspect.getsourcelines(original)
    tree = ast.parse(textwrap.dedent(''.join(lines)))
    functions = [node for node in tree.body if isinstance(node, ast.FunctionDef)]
    if len(functions) != 1 or original.__closure__:
        raise RuntimeError('Unsupported upstream Camera.update definition')
    function = functions[0]
    helpers = [node for node in function.body
               if isinstance(node, ast.FunctionDef) and node.name == 'is_still_prev']
    if len(helpers) == 1 and _has_early_none_return(helpers[0]):
        return original
    if len(helpers) != 1 or len(helpers[0].body) != 1 \
            or not isinstance(helpers[0].body[0], ast.Return):
        raise RuntimeError('Unsupported upstream Camera.update.is_still_prev definition')
    statement = helpers[0].body[0]
    if _has_none_guard(statement.value):
        return original
    comparisons = [node for node in ast.walk(statement.value)
                   if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Sub)
                   and isinstance(node.right, ast.Name)
                   and node.right.id == 'prev_center_offset']
    if len(comparisons) != 1:
        raise RuntimeError('Upstream camera no longer has the expected previous-offset comparison')
    guard = ast.Compare(left=ast.Name(id='prev_center_offset', ctx=ast.Load()),
                        ops=[ast.IsNot()], comparators=[ast.Constant(value=None)])
    statement.value = ast.copy_location(
        ast.BoolOp(op=ast.And(), values=[guard, statement.value]), statement.value)
    function.decorator_list = []
    ast.fix_missing_locations(tree)
    ast.increment_lineno(tree, first_line - 1)
    namespace = {}
    exec(compile(tree, original.__code__.co_filename, 'exec'), original.__globals__, namespace)
    patched = namespace[function.name]
    update_wrapper(patched, original)
    patched.__defaults__ = original.__defaults__
    patched.__kwdefaults__ = original.__kwdefaults__
    patched._alas_previous_view_compat = True
    return patched


def apply_camera_previous_view_compat():
    """Install once for every campaign using the upstream Camera class."""
    from module.map.camera import Camera

    original = Camera.update
    patched = guard_previous_view_comparison(original)
    if patched is original:
        return False
    Camera.update = patched
    return True
