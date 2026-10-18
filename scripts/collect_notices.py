"""Collect license texts and pinned source links from restored NuGet metadata."""
import json, pathlib, shutil, urllib.request, xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'docs' / 'licenses'
OUT.mkdir(parents=True, exist_ok=True)
lock = json.loads((ROOT/'src/AiPulse/packages.lock.json').read_text())
packages = {name: d['resolved'] for deps in lock['dependencies'].values() for name,d in deps.items() if 'resolved' in d}
rows=[]
for name, version in sorted(packages.items()):
    folder=ROOT/'packages/nuget'/name.lower()/version
    spec=next(folder.glob('*.nuspec'),None)
    if not spec: continue
    tree=ET.parse(spec)
    for e in tree.iter(): e.tag=e.tag.split('}')[-1]
    m=tree.find('metadata'); license=m.findtext('license') or m.findtext('licenseUrl') or 'See upstream'
    repo=m.find('repository'); source=m.findtext('projectUrl') or ''
    if repo is not None:
        source=repo.get('url','').removesuffix('/').removesuffix('.git')
        if repo.get('commit'): source+='/tree/'+repo.get('commit')
    rows.append(f'| {name} | {version} | {license} | {source} |')
    for f in folder.iterdir():
        if f.is_file() and f.name.upper().startswith(('LICENSE','NOTICE','THIRD-PARTY','COPYING')):
            shutil.copyfile(f,OUT/(name+'-'+f.name+'.txt' if not f.suffix else name+'-'+f.name))

sources={
 'Avalonia-LICENSE.md':'https://raw.githubusercontent.com/AvaloniaUI/Avalonia/2d79ba55fae1b1e71c69f93a668080c9029e6a31/licence.md',
 'MicroCom-LICENSE.txt':'https://raw.githubusercontent.com/kekekeks/MicroCom/76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE',
 'Tmds.DBus-LICENSE.txt':'https://raw.githubusercontent.com/tmds/Tmds.DBus/b4a7fed0b878f74cb54f7cca84d2889af4e596ba/COPYING',
 'MPL-2.0.txt':'https://www.mozilla.org/media/MPL/2.0/index.txt',
 'SQLitePCLRaw-LICENSE.txt':'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/main/LICENSE.TXT',
 'SQLitePCLRaw-NOTICE.txt':'https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/main/NOTICE.TXT',
 'Mono-LICENSE.txt':'https://raw.githubusercontent.com/mono/mono/main/LICENSE',
}
for name,url in sources.items():
    if not (OUT/name).exists():
        try:
            with urllib.request.urlopen(url,timeout=30) as r: (OUT/name).write_bytes(r.read())
        except Exception as e: print('License download needs attention:',name,type(e).__name__)

(ROOT/'THIRD-PARTY-NOTICES.md').write_text('''# Third-party notices

AI Pulse includes unmodified open-source libraries and a self-contained .NET runtime. Their licenses and copyright notices apply independently of AI Pulse's MIT license. Package license texts are included under [docs/licenses](docs/licenses). Runtime license and third-party notices are also supplied with self-contained builds.

LibreHardwareMonitor, BlackSharp, DiskInfoToolkit and RAMSPDToolkit use MPL-2.0. Their corresponding source is available at the pinned repository links below; no changes were made to those libraries. The source remains under MPL-2.0. SQLite is in the public domain; SQLitePCLRaw wrappers use Apache-2.0. Skia and HarfBuzz native component notices are included with their packages. ANGLE and HidSharp have bundled license files with their full terms.

PawnIO is an optional, separately installed driver, not bundled in these installers. Obtain its official signed installer and source from https://github.com/namazso/PawnIO. Ollama, Codex and Claude are independently installed products, not distributed as part of AI Pulse.

The following inventory is generated from the resolved dependency lock file. Some entries are build-only or platform-specific and are not present in every installer.

| Package | Version | Declared license | Source |
| --- | --- | --- | --- |
'''+ '\n'.join(rows)+'\n',encoding='utf-8')
print('Wrote notices for',len(rows),'dependencies')
