"""Test publication gates with fake API responses; no credentials or network calls."""
import copy
import hashlib
import json
import os
from pathlib import Path
import runpy
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).parents[1].resolve() / 'scripts/publish_verified_release.py'
SHA = 'a' * 40
THUMB = 'b' * 40


class PublicationTests(unittest.TestCase):
    def setUp(self):
        temp_root = Path(__file__).parents[1] / 'build/signing-publication-tests'
        temp_root.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=temp_root)
        self.previous = Path.cwd()
        os.chdir(self.temp.name)
        self.addCleanup(self.cleanup)
        self.env = {'GITHUB_REPOSITORY': 'owner/project', 'BUILD_RUN_ID': '123', 'RELEASE_TAG': 'v0.3.0', 'REQUIRE_SIGNED_WINDOWS': 'true', 'SIGNPATH_CERTIFICATE_THUMBPRINT': THUMB}
        self.run = {'conclusion': 'success', 'event': 'workflow_dispatch', 'head_branch': 'main', 'head_sha': SHA, 'path': '.github/workflows/build.yml'}
        self.release = {'id': 1, 'tag_name': 'v0.3.0', 'draft': True, 'target_commitish': SHA, 'assets': []}
        self.receipt = {'Schema': 1, 'Provider': 'SignPath', 'Repository': 'owner/project', 'Source': SHA, 'BuildRunId': '123', 'Version': '0.3.0', 'CertificateThumbprint': THUMB, 'Files': {}, 'Packages': {}}
        for role, names in {'app': ['AiPulse.exe', 'AiPulse.dll'], 'uninstaller': ['uninstaller.exe'], 'installer': ['installer.exe']}.items():
            self.receipt['Files'][role] = [{'Name': n, 'Thumbprint': THUMB, 'TimestampSigner': 'test fixture'} for n in names]
        self.calls = []

    def cleanup(self):
        os.chdir(self.previous)
        self.temp.cleanup()

    def stage(self):
        self.files = {}
        for platform, names in {'windows': ['AI-Pulse-0.3.0-win-x64-portable.zip', 'AI-Pulse-Setup-0.3.0-x64.exe'], 'linux': ['ai-pulse_0.3.0_amd64.deb', 'AI-Pulse-0.3.0-x86_64.AppImage']}.items():
            folder = Path('build/release-upload') / (platform + '-release')
            folder.mkdir(parents=True)
            for name in names:
                p = folder / name; p.write_bytes(b'fixture')
                self.files[name] = p
        self.receipt['Packages'] = {name: self.digest(p) for name, p in self.files.items() if p.parent.name == 'windows-release'}
        receipt = Path('build/release-upload/windows-release/SIGNING-VERIFICATION.json')
        receipt.write_text(json.dumps(self.receipt))
        self.files[receipt.name] = receipt
        for platform in ('windows', 'linux'):
            folder = Path('build/release-upload') / (platform + '-release')
            manifest = folder / f'SHA256SUMS-{platform}.txt'
            manifest.write_text(''.join(f'{self.digest(p)}  {p.name}\n' for p in folder.iterdir()))
            self.files[manifest.name] = manifest

    @staticmethod
    def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest()

    def api(self, command, **kwargs):
        path = command[-1]
        if path.endswith('/actions/runs/123'): value = self.run
        elif '/commits/' in path: value = {'sha': SHA}
        elif path.endswith('/releases'): value = [self.release]
        elif path.endswith('/releases/1'):
            value = copy.deepcopy(self.release)
            value.update(html_url='https://example.test/release', assets=[{'name': n, 'digest': 'sha256:' + self.digest(p), 'state': 'uploaded'} for n, p in self.files.items()])
        else: raise AssertionError(path)
        return json.dumps(value)

    def execute(self):
        with patch.dict(os.environ, self.env), patch('sys.argv', [str(SCRIPT), 'publish']), patch('subprocess.check_output', side_effect=self.api), patch('subprocess.run', side_effect=lambda command, **kw: self.calls.append(command)):
            runpy.run_path(str(SCRIPT), run_name='__main__')

    def test_accepts_matching_successful_signed_build(self):
        self.stage(); self.execute()
        self.assertEqual(len(self.calls), 2)

    def test_rejects_wrong_commit(self):
        self.run['head_sha'] = 'c' * 40; self.stage()
        with self.assertRaises(AssertionError): self.execute()
        self.assertFalse(self.calls)

    def test_rejects_failed_build(self):
        self.run['conclusion'] = 'failure'; self.stage()
        with self.assertRaises(AssertionError): self.execute()

    def test_rejects_published_release(self):
        self.release['draft'] = False; self.stage()
        with self.assertRaises(AssertionError): self.execute()

    def test_rejects_wrong_signer(self):
        self.receipt['CertificateThumbprint'] = 'c' * 40; self.stage()
        with self.assertRaises(AssertionError): self.execute()

    def test_rejects_missing_timestamp(self):
        self.receipt['Files']['installer'][0]['TimestampSigner'] = ''; self.stage()
        with self.assertRaises(AssertionError): self.execute()

    def test_rejects_missing_receipt(self):
        self.stage(); self.files['SIGNING-VERIFICATION.json'].unlink()
        with self.assertRaises(AssertionError): self.execute()

    def test_rejects_modified_package(self):
        self.stage(); self.files['AI-Pulse-Setup-0.3.0-x64.exe'].write_bytes(b'modified')
        with self.assertRaises(AssertionError): self.execute()


if __name__ == '__main__': unittest.main()
