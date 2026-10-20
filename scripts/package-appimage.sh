#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
mkdir -p build/AppDir/usr/lib/ai-pulse build/releases
cp -a build/linux-x64/. build/AppDir/usr/lib/ai-pulse/
chmod +x build/AppDir/usr/lib/ai-pulse/AiPulse
cat > build/AppDir/AppRun <<'EOF'
#!/bin/sh
HERE="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
exec "$HERE/usr/lib/ai-pulse/AiPulse" "$@"
EOF
chmod +x build/AppDir/AppRun
cat > build/AppDir/ai-pulse.desktop <<'EOF'
[Desktop Entry]
Type=Application
Name=AI Pulse
Exec=AiPulse
Icon=ai-pulse
Terminal=false
Categories=System;Monitor;
EOF
cp src/AiPulse/Assets/pulse.png build/AppDir/ai-pulse.png
cp src/AiPulse/Assets/pulse.png build/AppDir/.DirIcon
# AppImage's own tooling is needed only on the build machine, never for an installed app.
tool="${APPIMAGETOOL:-appimagetool}"
ARCH=x86_64 "$tool" build/AppDir build/releases/AI-Pulse-0.3.0-x86_64.AppImage
