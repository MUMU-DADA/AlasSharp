"""Page declarations and export integrity regressions; never imports upstream code."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from export_upstream_data import export_assets, export_campaign, export_pages, _compare_dirs
from verify_export import check


class PagesExportTests(unittest.TestCase):
    def setUp(self):
        scratch = ROOT / '.runtime/verification'
        scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix='pages-export-', dir=scratch)
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name) / 'repo'
        self.data = Path(self.temp.name) / 'data'
        self.source = self.repo / 'module/ui/page.py'
        self.source.parent.mkdir(parents=True)
        (self.repo / 'campaign').mkdir()
        (self.repo / 'campaign/fixture.py').write_text(
            'class Campaign:\n    def battle_0(self): return True\n', encoding='utf-8')
        self.source.write_text('page_a = Page(CHECK_A)\npage_b = Page(CHECK_B)\n'
                               'page_a.link(button=GO, destination=page_b)\n', encoding='utf-8')

    def export(self):
        manifest = {'errors': []}
        export_assets(str(self.repo), str(self.data), manifest)
        export_campaign(str(self.repo), str(self.data), manifest)
        export_pages(str(self.repo), str(self.data), manifest)
        (self.data / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
        return json.loads((self.data / 'pages.json').read_text(encoding='utf-8')), manifest

    def test_every_page_field_and_manifest_inventory_is_checked_against_source(self):
        baseline, metadata = self.export()
        self.assertTrue(check(str(self.repo), str(self.data))['ok'])
        for mutation in ('hidden_source', 'removed_metadata', 'removed_page', 'check_button',
                         'edge_button', 'removed_edges', 'removed_hash', 'manifest_hash',
                         'manifest_links', 'unresolved', 'dangling'):
            with self.subTest(mutation=mutation):
                document, manifest = copy.deepcopy(baseline), copy.deepcopy(metadata)
                if mutation == 'hidden_source': manifest['pages']['present'] = False
                elif mutation == 'removed_metadata': manifest.pop('pages')
                elif mutation == 'removed_page':
                    document['pages'].pop()
                    document['pages'][0]['links'] = []
                    manifest['pages']['count'] = 1
                    manifest['pages']['links'] = 0
                elif mutation == 'check_button': document['pages'][0]['check_button'] = 'WRONG'
                elif mutation == 'edge_button': document['pages'][0]['links'][0]['button'] = 'WRONG'
                elif mutation == 'removed_edges':
                    document['pages'][0]['links'] = []
                    manifest['pages']['links'] = 0
                elif mutation == 'removed_hash': document['source_files'] = {}
                elif mutation == 'manifest_hash': manifest['pages']['source_hashes'] = {}
                elif mutation == 'manifest_links': manifest['pages']['links'] = 99
                elif mutation == 'unresolved': manifest['pages']['unresolved'] = ['unsupported']
                elif mutation == 'dangling': manifest['pages']['dangling'] = ['missing']
                (self.data / 'pages.json').write_text(json.dumps(document), encoding='utf-8')
                (self.data / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
                self.assertFalse(check(str(self.repo), str(self.data))['ok'])

    def test_unsupported_page_control_flow_is_reported_and_rejected(self):
        for source in ['if enabled:\n    page_a = Page(CHECK_A)\n',
                       'page_a = Page(CHECK_A)\nconfigure(page_a)\n',
                       'page_a = Page(CHECK_A, OTHER)\n',
                       'page_a = Page(CHECK_A)\npage_a.link(button=GO, destination=page_a, extra=1)\n']:
            with self.subTest(source=source):
                self.source.write_text(source, encoding='utf-8')
                _, manifest = self.export()
                self.assertTrue(manifest['pages']['unresolved'])
                self.assertFalse(check(str(self.repo), str(self.data))['ok'])

    def test_keyword_constructor_mixed_link_and_last_write_semantics(self):
        self.source.write_text('page_a = Page(check_button=CHECK_A)\npage_b = Page(CHECK_B)\n'
                               'page_a.link(OLD, destination=page_b)\n'
                               'page_a.link(button=NEW, destination=page_b)\n', encoding='utf-8')
        document, manifest = self.export()
        self.assertEqual(manifest['pages']['unresolved'], [])
        self.assertEqual(document['pages'][0], {'name': 'page_a', 'check_button': 'CHECK_A',
                                               'links': [{'button': 'NEW', 'destination': 'page_b'}]})

    def test_export_check_owns_pages(self):
        self.export()
        other = Path(self.temp.name) / 'other'
        other.mkdir()
        self.assertTrue(any(row['file'] == 'pages.json' for row in _compare_dirs(str(self.data), str(other))))

    def test_absent_source_does_not_leave_stale_document(self):
        self.export()
        self.source.unlink()
        manifest = {'errors': []}
        export_pages(str(self.repo), str(self.data), manifest)
        self.assertFalse((self.data / 'pages.json').exists())
        self.assertFalse(manifest['pages']['present'])


if __name__ == '__main__':
    unittest.main()
