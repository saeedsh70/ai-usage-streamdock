#!/usr/bin/env bash
# Linux/Mono test run (used by CI):
#   1. compile with C# 5 rules, as Windows' built-in csc.exe would
#   2. end-to-end test against an emulated StreamDock host, the Claude and ChatGPT
#      usage endpoints and an HTTPS Antigravity language server
#   3. render every key layout to build/gallery for visual review
#   4. build the installer with NSIS, if makensis is installed
set -euo pipefail
cd "$(dirname "$0")/.."

REFS="-r:System.Web.Extensions.dll -r:System.Drawing.dll -r:System.Management.dll"
PLUGIN=com.saeedsh.aiusage.sdPlugin
mkdir -p build

echo "== sources are ASCII (Windows csc.exe reads BOM-less files in the system code page)"
if grep -nP '[^\x00-\x7F]' src/*.cs test/*.cs; then echo "non-ASCII characters found - use \\uXXXX escapes"; exit 1; fi

echo "== compile (C# 5)"
mcs -langversion:5 -sdk:4.5 -target:winexe -optimize+ -win32icon:installer/app.ico $REFS \
    -out:$PLUGIN/AIUsage.exe src/*.cs
mcs -langversion:5 -sdk:4.5 -main:AiUsageDeck.RenderGallery $REFS -out:build/gallery.exe src/*.cs test/RenderGallery.cs

echo "== end-to-end"
python3 test/fake_host.py $PLUGIN/AIUsage.exe build/e2e-keys

echo "== installer helper never fails"
mono $PLUGIN/AIUsage.exe --close-host build/host.txt
mono $PLUGIN/AIUsage.exe --start-host build/host.txt

echo "== key gallery"
rm -rf build/gallery
mono build/gallery.exe build/gallery
echo "wrote build/gallery/gallery.png and build/gallery/gallery-actual-size.png"

if command -v makensis >/dev/null; then
  echo "== installer"
  version=$(python3 -c "import json;print(json.load(open('$PLUGIN/manifest.json'))['Version'])")
  (cd installer && makensis -V2 -DVERSION="$version" -DSRC=../$PLUGIN -DOUT=../build AIUsage.nsi)
  ls -la build/AIUsage-Setup-*.exe
fi
