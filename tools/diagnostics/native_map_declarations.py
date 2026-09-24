"""Compare static metadata to declarations captured while Python imports sources.

Only used by offline diagnostics. The real native constructors, property setters,
copy operations and methods still execute; no game device is constructed.
"""
import copy
from contextlib import ExitStack
import inspect
from pathlib import Path
from unittest.mock import patch


def native_typed(value):
    from module.map_detection.grid_info import GridInfo
    from upstream_config_export import _typed
    if isinstance(value, GridInfo):
        return {'type': 'grid', 'location': [int(v) for v in value.location]}
    if isinstance(value, type):
        return {'type': 'reference', 'kind': 'class', 'module': value.__module__, 'name': value.__name__}
    if type(value) in (list, tuple):
        return {'type': type(value).__name__, 'items': [native_typed(v) for v in value]}
    if type(value) is dict:
        return {'type': 'dict', 'items': [{'key': native_typed(k), 'value': native_typed(v)}
                                        for k, v in value.items()]}
    return _typed(value)


class NativeMapDeclarations:
    def __init__(self, repo):
        self.repo = Path(repo).resolve()
        self.records = {}
        self.stack = ExitStack()

    def __enter__(self):
        from module.map.map_base import CampaignMap
        original_set = CampaignMap.__setattr__
        original_copy, original_deepcopy = copy.copy, copy.deepcopy

        def source(frame):
            if frame.f_code.co_name != '<module>':
                return False
            try:
                relative = Path(frame.f_code.co_filename).resolve().relative_to(self.repo)
                return relative.parts[0] == 'campaign'
            except ValueError:
                return False

        def record(obj):
            # Keep a strong reference so ids cannot be reused during the audit.
            return self.records.setdefault(id(obj), dict(obj=obj, fields={}, calls=[]))

        def assign(obj, name, value):
            if source(inspect.currentframe().f_back):
                record(obj)['fields'][name] = native_typed(value)
            return original_set(obj, name, value)

        def copied(obj, *args, **kwargs):
            result = original_copy(obj, *args, **kwargs)
            if isinstance(obj, CampaignMap) and id(obj) in self.records:
                record(result).update(fields=original_deepcopy(record(obj)['fields']),
                                      calls=original_deepcopy(record(obj)['calls']))
            return result

        def deepcopied(obj, *args, **kwargs):
            result = original_deepcopy(obj, *args, **kwargs)
            if isinstance(obj, CampaignMap) and id(obj) in self.records:
                record(result).update(fields=original_deepcopy(record(obj)['fields']),
                                      calls=original_deepcopy(record(obj)['calls']))
            return result

        self.stack.enter_context(patch.object(CampaignMap, '__setattr__', assign))
        self.stack.enter_context(patch.object(copy, 'copy', copied))
        self.stack.enter_context(patch.object(copy, 'deepcopy', deepcopied))
        # Capture top-level native calls generically, including newly added methods.
        for name, method in list(vars(CampaignMap).items()):
            if name.startswith('_') or not inspect.isfunction(method):
                continue

            def wrap(obj, *args, _name=name, _method=method, **kwargs):
                if _name != 'flatten' and source(inspect.currentframe().f_back):
                    record(obj)['calls'].append(dict(method=_name, typed_args=native_typed(args),
                                                     typed_kwargs=native_typed(kwargs)))
                return _method(obj, *args, **kwargs)

            self.stack.enter_context(patch.object(CampaignMap, name, wrap))
        return self

    def __exit__(self, *error):
        return self.stack.__exit__(*error)

    def check(self, module, ir):
        from module.map.map_base import CampaignMap
        obj = getattr(module, 'MAP', None)
        meta = ir['map_meta']
        if not isinstance(obj, CampaignMap):
            if meta['present'] or ir['map']:
                raise AssertionError('Static MAP exists without native module MAP')
            return
        if not meta['present'] or not meta['complete']:
            raise AssertionError('Native MAP declaration absent or incomplete in export')
        observed = self.records.get(id(obj), dict(fields={}, calls=[]))
        if observed['fields'] != meta['typed_values']:
            fields = sorted(k for k in set(observed['fields']) | set(meta['typed_values'])
                            if observed['fields'].get(k) != meta['typed_values'].get(k))
            raise AssertionError(f'MAP declaration types/values differ from native execution: {fields}')
        calls = [{k: call[k] for k in ('method', 'typed_args', 'typed_kwargs')} for call in meta['calls']]
        if observed['calls'] != calls:
            raise AssertionError('MAP method declarations differ from native execution')
        if obj.name is not None and (ir['name'] != obj.name or ir['name_source'] != 'CampaignMap'):
            raise AssertionError('MAP native name differs from export')
