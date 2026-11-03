import copy, hashlib, io, json, pathlib, sys, tempfile, unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'deploy'))
import release_sources as sources
import apk_sync
import service_catalog


class Sources(unittest.TestCase):
    def setUp(self):
        self.data = b'checked apk bytes'
        self.source = {'provider': 'gitverse', 'repo': 'Lepi4/lampa-home-module'}
        self.asset = {'file': 'release/2026.11.03.1/LMPC-1.16.16-1134.apk',
                      'sha256': hashlib.sha256(self.data).hexdigest(), 'bytes': len(self.data),
                      'versionCode': 1134, 'versionName': '1.16.16-mc', 'package': 'app.lampac.home.mc'}
        self.manifest = {'artifact_ref': 'a' * 40, 'homemedia': '2026.11.03.1', 'apk': self.asset,
                         'components': {'homemedia': {'version': '2026.11.03.1', 'file': 'release/2026.11.03.1/homemedia.tar.gz',
                                        'sha256': 'b' * 64, 'bytes': 123}}}
        self.requests = []

    def opener(self, request):
        self.requests.append(request.full_url)
        if request.full_url.endswith('/module-release.json'):
            return io.BytesIO(json.dumps(self.manifest).encode())
        self.assertIn('/raw/commit/' + 'a' * 40 + '/', request.full_url)
        return io.BytesIO(self.data)

    def test_public_manifest_and_immutable_commit_artifact(self):
        result = sources.discover_apk(self.source, self.opener)
        self.assertEqual(result['version_code'], 1134)
        self.assertEqual(result['provider'], 'gitverse')
        self.assertIn('/raw/commit/' + 'a' * 40 + '/', result['url'])
        self.assertEqual(self.requests, ['https://gitverse.ru/api/repos/Lepi4/lampa-home-module/raw/branch/master/module-release.json'])

    def test_module_uses_its_own_version_and_checksum(self):
        result = sources.discover_component(dict(self.source, asset='homemedia.tar.gz'), 'homemedia', self.opener)
        self.assertEqual((result['version'], result['sha256'], result['bytes']), ('2026.11.03.1', 'b' * 64, 123))

    def test_commit_is_required(self):
        for value in [None, 'master', '../evil', 'a' * 7]:
            self.manifest['artifact_ref'] = value
            with self.subTest(value=value), self.assertRaisesRegex(RuntimeError, 'artifact_commit_required'):
                sources.discover_apk(self.source, self.opener)

    def test_paths_cannot_escape_repository_or_change_origin(self):
        for value in ['../x.apk', 'release/../x.apk', 'https://other.test/a.apk', '/release/a.apk', 'release//a.apk', 'release/a.apk?x=1', 'release/a\\b.apk']:
            self.asset['file'] = value
            with self.subTest(value=value), self.assertRaisesRegex(RuntimeError, 'artifact_path'):
                sources.discover_apk(self.source, self.opener)

    def test_checksum_and_size_are_mandatory(self):
        for field, value in [('sha256', ''), ('sha256', 'c' * 63), ('bytes', 0), ('bytes', True), ('bytes', 2_000_000_001)]:
            original = self.asset[field]; self.asset[field] = value
            with self.subTest(field=field, value=value), self.assertRaises(RuntimeError):
                sources.discover_apk(self.source, self.opener)
            self.asset[field] = original

    def test_apk_metadata_must_match_file_name(self):
        for field, value in [('versionCode', 1133), ('versionCode', True), ('versionName', 'other'), ('package', 'other.app')]:
            original = self.asset[field]; self.asset[field] = value
            with self.subTest(field=field), self.assertRaises(RuntimeError):
                sources.discover_apk(self.source, self.opener)
            self.asset[field] = original

    def test_other_github_components_keep_native_release_support(self):
        m = {'tag_name': 'v1', 'assets': [{'name': 'tool', 'digest': 'sha256:' + 'd' * 64, 'size': 42,
             'browser_download_url': 'https://github.com/org/tool/releases/download/v1/tool'}]}
        result = sources.discover_component({'repo': 'org/tool', 'asset': 'tool'}, 'yt-dlp', lambda r: io.BytesIO(json.dumps(m).encode()))
        self.assertEqual((result['provider'], result['sha256']), ('github', 'd' * 64))

    def test_github_cannot_redirect_metadata_to_other_repository(self):
        m = {'tag_name': 'v1', 'assets': [{'name': 'tool', 'digest': 'sha256:' + 'd' * 64, 'size': 42,
             'browser_download_url': 'https://github.com/other/repo/releases/download/v1/tool'}]}
        with self.assertRaisesRegex(RuntimeError, 'unexpected_release_url'):
            sources.discover_component({'repo': 'org/tool', 'asset': 'tool'}, 'tool', lambda r: io.BytesIO(json.dumps(m).encode()))

    def test_apk_download_is_verified_before_publishing(self):
        with tempfile.TemporaryDirectory() as name:
            root = pathlib.Path(name)
            result = apk_sync.sync(root, self.source, self.opener)
            self.assertTrue(result['updated'])
            self.assertEqual((root / self.asset['file'].split('/')[-1]).read_bytes(), self.data)
            self.assertEqual(json.loads((root / 'latest.json').read_text())['source'], 'gitverse')

    def test_failed_download_keeps_existing_manifest_and_apk(self):
        with tempfile.TemporaryDirectory() as name:
            root = pathlib.Path(name); (root / 'latest.json').write_text('{"version_code":1132}'); (root / 'old.apk').write_bytes(b'old')
            self.asset['sha256'] = '0' * 64
            with self.assertRaisesRegex(RuntimeError, 'checksum'):
                apk_sync.sync(root, self.source, self.opener)
            self.assertEqual((root / 'latest.json').read_text(), '{"version_code":1132}')
            self.assertEqual((root / 'old.apk').read_bytes(), b'old')
            self.assertFalse(list(root.glob('*.part')))

    def test_no_downgrade_or_payload_download(self):
        with tempfile.TemporaryDirectory() as name:
            root = pathlib.Path(name); (root / 'latest.json').write_text('{"version_code":1200}')
            result = apk_sync.sync(root, self.source, self.opener)
            self.assertEqual(result['reason'], 'newer_installed'); self.assertEqual(len(self.requests), 1)

    def test_same_version_source_migration_keeps_previous_version_backup(self):
        with tempfile.TemporaryDirectory() as name:
            root = pathlib.Path(name); apk_sync.sync(root, self.source, self.opener)
            (root / 'previous-latest.json').write_text('{"version_code":1132}')
            m = json.loads((root / 'latest.json').read_text()); m['source'] = 'github'; (root / 'latest.json').write_text(json.dumps(m))
            self.requests.clear(); result = apk_sync.sync(root, self.source, self.opener)
            self.assertEqual(result['reason'], 'already_current'); self.assertEqual(len(self.requests), 1)
            self.assertEqual(json.loads((root / 'latest.json').read_text())['source'], 'gitverse')
            self.assertEqual((root / 'previous-latest.json').read_text(), '{"version_code":1132}')

    def test_same_version_different_bytes_is_rejected(self):
        with tempfile.TemporaryDirectory() as name:
            root = pathlib.Path(name); apk_sync.sync(root, self.source, self.opener)
            before = (root / 'latest.json').read_bytes(); self.asset['sha256'] = '0' * 64
            with self.assertRaisesRegex(RuntimeError, 'immutable_version_conflict'):
                apk_sync.sync(root, self.source, self.opener)
            self.assertEqual((root / 'latest.json').read_bytes(), before)

    def test_disabled_job_does_not_fetch_or_touch_latest(self):
        with patch.object(apk_sync, 'config', return_value={'enabled': False}), patch.object(apk_sync, 'sync') as sync:
            apk_sync.main(); sync.assert_not_called()


if __name__ == '__main__':
    unittest.main()
