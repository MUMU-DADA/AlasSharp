"""Export AzurPilot's declarative deployment UI contract without starting its runtime.

Only declarations are evaluated. No State, device, updater or network imports run.
Generated resources contain public defaults/translations, never config/deploy.yaml.
"""
from __future__ import annotations

import argparse
import ast
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'src/Alas.Core/Runtime/Resources/deploy-settings.json'


def declarations(source: Path) -> dict:
    utils = ast.parse((source / 'module/config/utils.py').read_text(encoding='utf-8'))
    languages = next(n for n in utils.body if isinstance(n, ast.Assign)
                     and any(isinstance(t, ast.Name) and t.id == 'LANGUAGES' for t in n.targets))
    namespace = {'dataclass': dataclass}
    exec(compile(ast.Module([languages], []), '<upstream-languages>', 'exec'), namespace)
    tree = ast.parse((source / 'module/runtime/deploy_settings.py').read_text(encoding='utf-8'))
    allowed = {'THEME_OPTIONS', 'REMOTE_ACCESS_MODE_OPTIONS', 'TURN_CREDENTIAL_MODE_OPTIONS',
               'DEPLOY_GROUPS', 'LEGACY_DEPLOY_FIELDS', 'DEPLOY_FIELDS'}
    nodes = [n for n in tree.body if (isinstance(n, ast.ClassDef) and n.name == 'DeployField')
             or (isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id in allowed for t in n.targets))
             or (isinstance(n, ast.AnnAssign) and isinstance(n.target, ast.Name) and n.target.id in allowed)]
    exec(compile(ast.Module(nodes, []), '<upstream-deploy-declarations>', 'exec'), namespace)
    return namespace


def export(source: Path) -> dict:
    definitions = declarations(source)
    paths = ['module/runtime/deploy_settings.py', 'module/config/utils.py', 'deploy/utils.py']
    utils = ast.parse((source / 'deploy/utils.py').read_text(encoding='utf-8'))
    reader = next(n for n in utils.body if isinstance(n, ast.FunctionDef) and n.name == 'poor_yaml_read')
    namespace = {'re': re, 'atomic_read_text': lambda path: Path(path).read_text(encoding='utf-8')}
    exec(compile(ast.Module([reader], []), '<upstream-deploy-reader>', 'exec'), namespace)
    templates, defaults, translations = {}, {}, {}
    for platform, name in [('windows', 'deploy.template.yaml'), ('unix', 'deploy.template-linux.yaml')]:
        path = 'config/' + name
        paths.append(path)
        # Preserve hierarchy and ordering; deployment comments are not UI schema.
        templates[platform] = '\n'.join(line for line in (source / path).read_text(encoding='utf-8').splitlines()
                                        if line.strip() and not line.lstrip().startswith('#')) + '\n'
        defaults[platform] = namespace['poor_yaml_read'](source / path)
        missing = set(definitions['DEPLOY_FIELDS']) - defaults[platform].keys()
        if missing:
            raise ValueError(f'{path}: fields missing from template: {sorted(missing)}')
    for language in definitions['LANGUAGES']:
        path = f'module/config/i18n/{language}.json'
        paths.append(path)
        translations[language] = json.loads((source / path).read_text(encoding='utf-8'))['Gui']['DeploySetting']

    def field(item):
        return {'key': item.key, 'type': item.kind, 'label': item.label_key,
                'help': item.help_key, 'options': list(item.options)}

    return {'contract': 'upstream-deploy-settings/1',
            'sources': {path: hashlib.sha256((source / path).read_bytes()).hexdigest() for path in paths},
            'groups': [{'key': group, 'label': f'Gui.DeploySetting.Group{group}',
                        'fields': [field(item) for item in fields]} for group, fields in definitions['DEPLOY_GROUPS']],
            'legacy': [field(item) for item in definitions['LEGACY_DEPLOY_FIELDS'].values()],
            'notice': 'Gui.DeploySetting.RestartNotice', 'templates': templates,
            'defaults': defaults, 'translations': translations}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--upstream', type=Path, default=ROOT.parent / 'others fork version/AzurPilot')
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    payload = json.dumps(export(args.upstream), ensure_ascii=False, indent=2) + '\n'
    if args.check:
        if not OUTPUT.is_file() or OUTPUT.read_text(encoding='utf-8') != payload:
            raise SystemExit('Deployment settings export differs from upstream; regenerate and review.')
        print('Deployment settings export matches upstream declarations, templates and translations.')
    else:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_text(payload, encoding='utf-8', newline='\n')
        print(OUTPUT.relative_to(ROOT).as_posix())


if __name__ == '__main__':
    main()
