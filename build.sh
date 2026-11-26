#!/usr/bin/env bash
# Build from WSL: Windows tools cannot run inside \\wsl$ folders, so build in a temp folder on C:.
set -euo pipefail
cd "$(dirname "$0")"
STAGE=$(wslpath "$(cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')")/wslim-build
rm -rf "$STAGE"; mkdir -p "$STAGE"
cp -r src assets tools build.cmd "$STAGE/"
[ -f assets/wslim.ico ] || (cd /mnt/c && powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$STAGE/tools/make-icon.ps1")" -Out "$(wslpath -w "$STAGE/assets/wslim.ico")") && cp "$STAGE"/assets/wslim.* assets/
(cd "$STAGE" && cmd.exe /c build.cmd)
mkdir -p bin && cp "$STAGE/bin/wslim.exe" bin/
ls -l bin/wslim.exe
