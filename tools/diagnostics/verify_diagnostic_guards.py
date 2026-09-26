#!/usr/bin/env python3
"""Offline counterexamples for the diagnostics themselves; no upstream imports or devices."""
from __future__ import annotations

import contextlib
import io
import os
from pathlib import Path
import socket
import sys
import tempfile
import unittest
from unittest.mock import patch

import dotnet_env
import r5_capability_matrix as capability
import r5_incomplete_hooks as incomplete
import r5_silent_fallback_audit as fallback
import r5_state_mutation_audit as mutation
import verify_all

ROOT = Path(__file__).resolve().parents[2]


class Guards(unittest.TestCase):
    def setUp(self):
        local = ROOT / '.runtime/verification'
        local.mkdir(parents=True, exist_ok=True)
        self.folder = tempfile.TemporaryDirectory(prefix='diagnostic-guards-', dir=local)
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)

    def test_dotnet_empty_directory_is_not_a_runtime(self):
        (self.root / '.runtime/dotnet/shared').mkdir(parents=True)
        with patch.object(dotnet_env.shutil, 'which', return_value=None):
            self.assertIsNone(dotnet_env.local_root(self.root))
            self.assertNotIn('DOTNET_ROOT', dotnet_env.apply({'DOTNET_ROOT': str(self.root)}, self.root))
            with self.assertRaises(RuntimeError):
                dotnet_env.executable(self.root, {'PATH': ''})

    def test_dotnet_precedence_and_environment_copy(self):
        sdk = self.root / 'sdk'
        sdk.mkdir()
        host = sdk / 'dotnet.exe'
        host.touch()
        environment = {'DOTNET_ROOT': str(sdk), 'PATH': ''}
        self.assertEqual(dotnet_env.executable(self.root, environment), host.resolve())
        self.assertEqual(dotnet_env.apply(environment, self.root)['DOTNET_ROOT'], str(sdk.resolve()))
        self.assertEqual(environment, {'DOTNET_ROOT': str(sdk), 'PATH': ''})
        local = self.root / '.runtime/dotnet'
        local.mkdir(parents=True)
        (local / 'dotnet.exe').touch()
        self.assertEqual(dotnet_env.executable(self.root, environment).parent, local.resolve())

    def test_dotnet_path_fallback(self):
        host = self.root / 'dotnet.exe'
        host.touch()
        with patch.object(dotnet_env.shutil, 'which', return_value=str(host)) as which:
            self.assertEqual(dotnet_env.resolved_root(self.root, {'PATH': 'fixture-path'}), self.root.resolve())
            which.assert_called_once_with('dotnet', path='fixture-path')

    def test_unreadable_method_is_not_no_mutation(self):
        with self.assertRaisesRegex(RuntimeError, '源码'):
            mutation.scan(len)

    def test_missing_hook_sources_and_syntax_error_fail(self):
        with patch.object(mutation, 'UPSTREAM', self.root):
            with self.assertRaises(FileNotFoundError):
                mutation.hook_mutation_scan()
            source = self.root / 'campaign/a.py'
            source.parent.mkdir()
            source.write_text('class Campaign: broken ???', encoding='utf-8')
            with self.assertRaises(SyntaxError):
                mutation.hook_mutation_scan()

    def test_empty_primitive_registry_fails(self):
        registry = self.root / 'CampaignPrimitives.cs'
        registry.write_text('// no declarations', encoding='utf-8')
        with patch.object(mutation, 'REGISTRY', registry), self.assertRaises(ValueError):
            mutation.registry_primitives()

    def test_missing_exports_cannot_lower_incomplete_count(self):
        with patch.object(incomplete, 'UPSTREAM', self.root), patch.object(incomplete, 'DATA', self.root / 'data'):
            with self.assertRaises(FileNotFoundError):
                incomplete.main()
            (self.root / 'campaign').mkdir()
            (self.root / 'campaign/a.py').write_text('class Campaign:\n    def battle_0(self):\n        return True\n', encoding='utf-8')
            (self.root / 'data').mkdir()
            (self.root / 'data/other.json').write_text('{}', encoding='utf-8')
            with self.assertRaisesRegex(FileNotFoundError, 'a.json'):
                incomplete.main()

    def test_comment_and_literal_reporting_markers_are_not_evidence(self):
        for body in ('// throw; Log( Result( .Message\nreturn false;',
                     'var text = "throw; Log( Result( .Message }"; return false;',
                     '/* throw; } */ return false;'):
            blocks = list(fallback.blocks('try {} catch (Exception e) { ' + body + ' }'))
            self.assertEqual(len(blocks), 1)
            self.assertIsNone(fallback.REPORTED.search(blocks[0][2]))
        self.assertTrue(fallback.REPORTED.search(list(fallback.blocks('catch { throw; }'))[0][2]))
        self.assertTrue(fallback.REPORTED.search(list(fallback.blocks('catch { Log("why"); return false; }'))[0][2]))

    def test_bare_catch_detected_and_unknown_syntax_rejected(self):
        blocks = list(fallback.blocks('try {} catch { return false; }'))
        self.assertEqual(blocks[0][1], '裸 catch')
        with self.assertRaises(ValueError):
            list(fallback.blocks('catch (Exception e) when (Check(e)) { return false; }'))
        with self.assertRaises(ValueError):
            list(fallback.blocks('catch { return false;'))

    def test_review_is_body_specific_and_cannot_cover_new_catch(self):
        source = self.root / 'SortieResult.cs'
        with patch.object(fallback, 'ENGINE', self.root), contextlib.redirect_stdout(io.StringIO()):
            source.write_text('catch (Exception) { return false; }', encoding='utf-8')
            self.assertEqual(fallback.main(), 1)
            source.write_text('catch (Exception) { return true; }\ncatch (Exception) { return true; }', encoding='utf-8')
            self.assertEqual(fallback.main(), 1)

    def test_empty_interface_fails(self):
        source = self.root / 'IVisionEngine.cs'
        source.write_text('interface IVisionEngine {}', encoding='utf-8')
        with patch.object(capability, 'INTERFACE', source), self.assertRaises(ValueError):
            capability.interface_methods()

    def test_runner_uses_root_and_preserves_failure(self):
        script = self.root / 'probe.py'
        script.write_text('import pathlib, sys\nassert pathlib.Path.cwd() == pathlib.Path(__file__).parent\nprint("failure-marker")\nsys.exit(7)\n', encoding='utf-8')
        with patch.object(verify_all, 'HERE', str(self.root)), patch.object(verify_all, 'ROOT', str(self.root)):
            state, _, note = verify_all.run_step('probe.py', False, 10)
        self.assertEqual(state, 'fail')
        self.assertEqual(note, 'failure-marker')
        self.assertIn('failure-marker', (self.root / '.runtime/verification/verify_all/probe.log').read_text())

    def test_timeout_terminates_spawned_server(self):
        # A live TCP child proves cleanup includes descendants, not only the
        # verifier parent. It never binds a device or reads runtime configuration.
        script = self.root / 'hang.py'
        child = 'import socket,time; s=socket.socket(); s.bind(("127.0.0.1",0)); s.listen(); print(s.getsockname()[1],flush=True); time.sleep(60)'
        script.write_text('import pathlib,subprocess,sys,time\np=subprocess.Popen([sys.executable,"-c",' + repr(child) + '],stdout=subprocess.PIPE,text=True)\npathlib.Path("port.txt").write_text(p.stdout.readline())\ntime.sleep(60)\n', encoding='utf-8')
        with patch.object(verify_all, 'HERE', str(self.root)), patch.object(verify_all, 'ROOT', str(self.root)):
            state, _, _ = verify_all.run_step('hang.py', False, 2)
        self.assertEqual(state, 'timeout')
        port = int((self.root / 'port.txt').read_text())
        with socket.socket() as probe:
            probe.settimeout(1)
            self.assertNotEqual(probe.connect_ex(('127.0.0.1', port)), 0)


if __name__ == '__main__':
    unittest.main()
