"""Produce a Debian package with the self-contained runtime, SQLite and desktop integration.
Uses only Python's standard library; can run on Windows or Linux.
"""
import io, pathlib, tarfile, time, argparse, os, hashlib, shutil, subprocess, tempfile
p=argparse.ArgumentParser();p.add_argument('--source',default='build/linux-x64');p.add_argument('--output',default='build/releases');p.add_argument('--version',default='0.2.2');a=p.parse_args()
source=pathlib.Path(a.source);out=pathlib.Path(a.output);out.mkdir(parents=True,exist_ok=True)
assert (source/'AiPulse').is_file(),'Publish linux-x64 first'

def archive(items):
    buffer=io.BytesIO()
    with tarfile.open(fileobj=buffer,mode='w:gz',format=tarfile.GNU_FORMAT) as tar:
        directories={str(parent) for name,_,_ in items for parent in pathlib.PurePosixPath(name).parents if str(parent)!='.'}
        for directory in sorted(directories,key=lambda p:(p.count('/'),p)):
            info=tarfile.TarInfo(directory+'/');info.type=tarfile.DIRTYPE;info.mode=0o755;info.uid=info.gid=0;info.uname=info.gname='root';info.mtime=0;tar.addfile(info)
        for name,data,mode in items:
            info=tarfile.TarInfo(name);info.size=len(data);info.mode=mode;info.uid=info.gid=0;info.uname=info.gname='root';info.mtime=0
            tar.addfile(info,io.BytesIO(data))
    return buffer.getvalue()

size=sum(f.stat().st_size for f in source.rglob('*') if f.is_file())//1024
control=f'''Package: ai-pulse
Version: {a.version}
Section: utils
Priority: optional
Architecture: amd64
Maintainer: AI Pulse contributors
Installed-Size: {size}
Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, zlib1g, libssl3 | libssl3t64, libicu74 | libicu76 | libicu78, libx11-6, libice6, libsm6, libfontconfig1, libfreetype6
Homepage: https://github.com/nikhilgs817/AI_PULSE
Description: Local hardware and AI model monitoring dashboard
 Compact multi-model dashboard with bundled SQLite history and backups.
 Self-contained runtime; no separate .NET installation required.
'''
files=[]
for f in sorted(source.rglob('*')):
    if f.is_file():files.append(('usr/lib/ai-pulse/'+f.relative_to(source).as_posix(),f.read_bytes(),0o755 if f.name=='AiPulse' or f.suffix=='.so' else 0o644))
files.append(('usr/bin/ai-pulse',b'#!/bin/sh\nexec /usr/lib/ai-pulse/AiPulse "$@"\n',0o755))
files.append(('usr/share/applications/ai-pulse.desktop',b'[Desktop Entry]\nType=Application\nName=AI Pulse\nComment=Hardware and AI model monitoring\nExec=ai-pulse\nIcon=ai-pulse\nTerminal=false\nCategories=System;Monitor;\nStartupWMClass=AiPulse\n',0o644))
files.append(('usr/share/pixmaps/ai-pulse.png',pathlib.Path('src/AiPulse/Assets/pulse.png').read_bytes(),0o644))
if shutil.which('dpkg-deb'):
    # Use the native builder to handle long filenames and Debian archive details.
    target=out/f'ai-pulse_{a.version}_amd64.deb'
    with tempfile.TemporaryDirectory(prefix='deb-stage-',dir=source.parent) as staging:
        stage=pathlib.Path(staging);(stage/'DEBIAN').mkdir();(stage/'DEBIAN/control').write_text(control)
        for name,data,mode in files:
            f=stage/name;f.parent.mkdir(parents=True,exist_ok=True);f.write_bytes(data);f.chmod(mode)
        subprocess.run(['dpkg-deb','--root-owner-group','-Zxz','--build',str(stage),str(target)],check=True)
    print(target);print(hashlib.sha256(target.read_bytes()).hexdigest());raise SystemExit(0)
components=[('debian-binary',b'2.0\n'),('control.tar.gz',archive([('control',control.encode(),0o644)])),('data.tar.gz',archive(files))]
target=out/f'ai-pulse_{a.version}_amd64.deb'
with target.open('wb') as f:
    f.write(b'!<arch>\n')
    for name,data in components:
        header=f'{name+"/":<16}{0:<12}{0:<6}{0:<6}{"100644":<8}{len(data):<10}`\n'.encode('ascii')
        assert len(header)==60;f.write(header);f.write(data)
        if len(data)%2:f.write(b'\n')
print(target)
print(hashlib.sha256(target.read_bytes()).hexdigest())
