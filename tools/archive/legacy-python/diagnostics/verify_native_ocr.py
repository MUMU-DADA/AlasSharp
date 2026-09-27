"""Compare the OCR adapter with native preprocessing and C# wire arguments.

Synthetic RGB frames exercise native Ocr.ocr/crop/pre_process. Only the model
inference endpoint is substituted, so no model download, account or device is
needed. The C# probe uses the actual VisionEngineBase and VisionProtocol.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
import alas_vision as av
import numpy as np
from module.webui.setting import State

# Select a local inference boundary without reading or modifying deployment files.
with patch.object(State, '_deploy_config_', SimpleNamespace(UseOcrServer=False), create=True):
    import module.ocr.ocr as native


class NativeOcrTests(unittest.TestCase):
    def setUp(self):
        self.server = av.server_module.server
        self.before = av._state['image']
        self.frame = np.zeros((56, 100, 3), dtype=np.uint8)
        self.frame[:, :25] = (255, 255, 255)
        self.frame[:, 25:50] = (40, 120, 200)
        self.frame[:, 50:75] = (50, 135, 230)
        self.area = [4, 3, 96, 52]
        av._state['image'] = self.frame
        self.calls = []
        calls = self.calls

        class Models:
            def __getattribute__(self, lang):
                if lang not in ('azur_lane', 'azur_lane_jp', 'cnocr', 'jp', 'tw'):
                    return object.__getattribute__(self, lang)
                def infer(images, alphabet):
                    calls.append((lang, alphabet, [image.copy() for image in images]))
                    return [list('MODEL') for _ in images]
                return SimpleNamespace(atomic_ocr_for_single_lines=infer)

        self.model = patch.object(native, 'OCR_MODEL', Models())
        self.log = patch.object(native.Ocr, 'SHOW_LOG', False)
        self.model.start()
        self.log.start()

    def tearDown(self):
        self.log.stop()
        self.model.stop()
        av._state['image'] = self.before
        av.op_set_server({'server': self.server})

    def compare(self, arguments, native_arguments):
        expected = native.Ocr(tuple(self.area), **native_arguments).ocr(self.frame)
        expected_call = self.calls.pop()
        response = json.loads(av.handle_line(json.dumps(
            dict(id=1, op='ocr', args=dict(area=self.area, **arguments)))))
        self.assertTrue(response['ok'], response.get('error'))
        self.assertEqual(response['result']['text'], expected)
        self.assertEqual(len(self.calls), 1)
        actual_call = self.calls.pop()
        self.assertEqual(actual_call[:2], expected_call[:2], 'model language / alphabet')
        self.assertEqual(len(actual_call[2]), len(expected_call[2]))
        for actual, expected in zip(actual_call[2], expected_call[2]):
            np.testing.assert_array_equal(actual, expected, err_msg='native preprocessed pixels differ')

    def test_defaults_preserve_native_white_letter(self):
        self.compare({}, {})

    def test_null_optional_parameters_preserve_constructor_defaults(self):
        self.compare(dict(letter=None, threshold=None, alphabet=None), {})

    def test_rgb_letter_and_threshold_preserve_native_pixels(self):
        for letter in ([40, 120, 200], [0, 0, 0], [255, 255, 255]):
            for threshold in (32, 128, 255):
                with self.subTest(letter=letter, threshold=threshold):
                    self.compare(dict(letter=letter, threshold=threshold),
                                 dict(letter=tuple(letter), threshold=threshold))

    def test_alphabet_is_independent_of_rgb_letter(self):
        for alphabet in ('0123456789:/', ''):
            self.compare(dict(letter=[40, 120, 200], alphabet=alphabet),
                         dict(letter=(40, 120, 200), alphabet=alphabet))

    def test_every_server_preserves_native_language_selection(self):
        for server in av.server_module.VALID_SERVER:
            av.op_set_server({'server': server})
            for lang in ('azur_lane', 'cnocr'):
                with self.subTest(server=server, lang=lang):
                    self.compare(dict(lang=lang), dict(lang=lang))

    def test_malformed_rgb_is_rejected_before_model_inference(self):
        for letter in ('0123', [], [1, 2], [1, 2, 3, 4], [-1, 2, 3],
                       [1, 2, 256], [True, 2, 3], ['1', 2, 3]):
            with self.subTest(letter=letter):
                response = json.loads(av.handle_line(json.dumps(
                    dict(id=1, op='ocr', args=dict(area=self.area, letter=letter)))))
                self.assertFalse(response['ok'])
                self.assertIn('letter', response['error'])
                self.assertIn('RGB', response['error'])
                self.assertEqual(self.calls, [])

    def test_model_failure_remains_an_error(self):
        def fail(*args):
            raise RuntimeError('synthetic inference failure')
        model = SimpleNamespace(atomic_ocr_for_single_lines=fail)
        av.op_set_server({'server': 'cn'})
        with patch.object(native, 'OCR_MODEL', SimpleNamespace(azur_lane=model)):
            response = json.loads(av.handle_line(json.dumps(
                dict(id=1, op='ocr', args=dict(area=self.area)))))
        self.assertFalse(response['ok'])
        self.assertIn('synthetic inference failure', response['error'])
        self.assertNotIn('result', response)


HARNESS = r'''
using System.Text.Json.Nodes;
using Alas.Vision;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
using var probe = new Probe();
IVisionEngine engine = probe;
Check(engine.Ocr([4, 3, 96, 52]) == "MODEL", "result is consumed");
var defaults = probe.Request!["args"]!.AsObject();
Check(defaults["lang"]!.GetValue<string>() == "azur_lane", "language default");
Check(!defaults.ContainsKey("letter") && !defaults.ContainsKey("threshold") && !defaults.ContainsKey("alphabet"),
      "native defaults must not be replaced by empty values");
engine.Ocr([4, 3, 96, 52], lang: "cnocr", letter: [40, 120, 200], threshold: 32, alphabet: "0123:/", name: "fixture");
var supplied = probe.Request!["args"]!;
Check(supplied["letter"]!.AsArray().Select(value => value!.GetValue<int>()).SequenceEqual(new[] {40, 120, 200}),
      "letter must serialize as RGB array");
Check(supplied["alphabet"]!.GetValue<string>() == "0123:/" && supplied["threshold"]!.GetValue<int>() == 32
      && supplied["lang"]!.GetValue<string>() == "cnocr" && supplied["name"]!.GetValue<string>() == "fixture",
      "independent native arguments retained");
engine.Ocr([4, 3, 96, 52], alphabet: "");
Check(probe.Request!["args"]!["alphabet"]!.GetValue<string>() == "", "explicit empty alphabet retained");
Console.WriteLine("PASS: Core OCR typed RGB, defaults, threshold, alphabet and native protocol");

sealed class Probe : VisionEngineBase
{
    public JsonObject? Request;
    protected override JsonNode CallRaw(string op, object? args)
    {
        if (op != "ocr") throw new Exception("unexpected operation");
        Request = JsonNode.Parse(VisionProtocol.BuildRequest(NextId(), op, args))!.AsObject();
        return VisionProtocol.ParseResponse("""{"id":1,"ok":true,"result":{"text":"MODEL"}}""", op);
    }
}
'''


def verify_core():
    local = ROOT / '.runtime/verification'
    local.mkdir(parents=True, exist_ok=True)
    bundled = ROOT / '.runtime/dotnet/dotnet.exe'
    dotnet = str(bundled) if bundled.is_file() else shutil.which('dotnet')
    if not dotnet:
        raise RuntimeError('dotnet is required for the Core OCR protocol check')
    with tempfile.TemporaryDirectory(prefix='ocr-contract-', dir=local) as folder:
        work = Path(folder)
        project = work / 'OcrProbe.csproj'
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{escape(str(ROOT / 'src/Alas.Core/Alas.Core.csproj'))}" /></ItemGroup></Project>''', encoding='utf-8')
        (work / 'Program.cs').write_text(HARNESS, encoding='utf-8')
        env = dict(os.environ, DOTNET_CLI_HOME=str(ROOT / '.runtime/dotnet-home'),
                   NUGET_PACKAGES=str(ROOT / '.runtime/nuget/packages'), DOTNET_CLI_TELEMETRY_OPTOUT='1')
        for command in (
            [dotnet, 'build', str(project), '-c', 'Release', '--source',
             str(ROOT / '.runtime/nuget/source'), '-p:NuGetAudit=false'],
            [dotnet, str(work / 'bin/Release/net10.0/OcrProbe.dll')],
        ):
            result = subprocess.run(command, cwd=ROOT, env=env, capture_output=True, timeout=120)
            if result.returncode:
                raise AssertionError((result.stdout + result.stderr).decode('utf-8', errors='replace'))
        print(result.stdout.decode('utf-8', errors='replace').strip())


if __name__ == '__main__':
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(NativeOcrTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if not result.wasSuccessful():
        sys.exit(1)
    verify_core()
