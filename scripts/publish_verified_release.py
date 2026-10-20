"""Publish only an existing draft from a successful main-branch build of its exact source."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys

repo = os.environ['GITHUB_REPOSITORY']
run_id = os.environ['BUILD_RUN_ID']
tag = os.environ['RELEASE_TAG']
assert run_id.isdigit() and re.fullmatch(r'v\d+\.\d+\.\d+', tag)

def api(path):
    return json.loads(subprocess.check_output(['gh', 'api', f'repos/{repo}/{path}'], text=True))

run = api('actions/runs/' + run_id)
assert run['conclusion'] == 'success' and run['event'] == 'push' and run['head_branch'] == 'main'
assert run['path'] == '.github/workflows/build.yml'
release = next(r for r in api('releases') if r['tag_name'] == tag)
assert release['draft'], 'Only an unpublished draft may be modified'
target = release['target_commitish']
assert api('commits/' + target)['sha'] == run['head_sha'], 'Release source differs from the tested build'
print('Verified successful build and matching draft source:', run['head_sha'], flush=True)
if sys.argv[1] == 'check':
    sys.exit(0)
assert sys.argv[1] == 'publish'

version = tag[1:]
expected = {
    f'AI-Pulse-{version}-win-x64-portable.zip', f'AI-Pulse-Setup-{version}-x64.exe',
    f'ai-pulse_{version}_amd64.deb', f'AI-Pulse-{version}-x86_64.AppImage',
    'SHA256SUMS-windows.txt', 'SHA256SUMS-linux.txt'
}
files = list(Path('build/release-upload').glob('*-release/*'))
assert len(files) == 6 and {p.name for p in files} == expected and all(p.is_file() for p in files)
assert {a['name'] for a in release['assets']} <= expected, 'Unexpected draft assets'
digests = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in files}
for manifest in (p for p in files if p.name.startswith('SHA256SUMS-')):
    entries = manifest.read_text(encoding='utf-8-sig').splitlines()
    assert len(entries) == 2
    for line in entries:
        digest, name = line.split(maxsplit=1)
        name = name.lstrip('*')
        assert name in expected and digests[name] == digest.lower()
subprocess.run(['gh', 'release', 'upload', tag, '--clobber', *map(str, files)], check=True)
uploaded = api('releases/' + str(release['id']))
assert len(uploaded['assets']) == 6
for asset in uploaded['assets']:
    assert asset['state'] == 'uploaded' and asset['digest'] == 'sha256:' + digests[asset['name']]
subprocess.run(['gh', 'release', 'edit', tag, '--draft=false', '--prerelease'], check=True)
print('Published all six verified files:', uploaded['html_url'], flush=True)
