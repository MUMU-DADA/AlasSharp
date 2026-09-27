"""Validate transient task inputs with upstream field rules and conversions."""
from __future__ import annotations

import copy

from module.api.config_service import ConfigService
from module.api.protocol import ApiError
from module.config.utils import parse_value


class _BoundFieldValidator(ConfigService):
    def __init__(self, args):
        # validate() and validate_shop_advanced_groups() only need field metadata.
        # Avoid ConfigService's unrelated template/menu/translation file reads.
        self.args = args


def validate_task_overrides(config, overrides):
    """Return native values only after every bound field has passed validation.

    No config.override(), config write, task construction or device access occurs
    here. The authoritative binding and descriptors belong to the native config;
    exported JSON summaries are never used as a runtime input source.
    """
    # ConfigService.validate first checks exact option membership, then uses
    # the descriptor's default type. Some upstream select fields intentionally
    # mix an integer default with float options (for example island efficiency
    # 0 / 0.04 / 0.12). Validate against the matched option's type while keeping
    # the rest of the upstream validator and all original descriptors intact.
    validator_args = copy.deepcopy(config.args)
    validator = _BoundFieldValidator(validator_args)
    converted = {}
    combined = copy.deepcopy(config.data)
    affected_tasks = set()
    for key, value in overrides.items():
        path = config.bound.get(key)
        if not isinstance(path, str):
            raise ApiError('INVALID_PARAMS', f'当前任务未绑定覆盖字段：{key}')
        parts = path.split('.')
        if len(parts) != 3 or not isinstance(validator_args.get(parts[0]), dict) \
                or not isinstance(validator_args[parts[0]].get(parts[1]), dict) \
                or not isinstance(validator_args[parts[0]][parts[1]].get(parts[2]), dict):
            raise ApiError('INVALID_PARAMS', f'当前任务未绑定有效覆盖字段：{key}')
        descriptor = validator_args[parts[0]][parts[1]][parts[2]]
        options = descriptor.get('option')
        if isinstance(options, list):
            matched = next((option for option in options
                            if type(option) is type(value) and option == value), None)
            if matched is not None and type(descriptor.get('value')) is not type(matched):
                descriptor['value'] = matched
        task, group, argument = validator.validate(path, value)
        descriptor = config.args[task][group][argument]
        converted[key] = parse_value(value, data=descriptor)
        combined.setdefault(task, {}).setdefault(group, {})[argument] = value
        affected_tasks.add(task)
    validator.validate_shop_advanced_groups(combined, affected_tasks)
    return converted
