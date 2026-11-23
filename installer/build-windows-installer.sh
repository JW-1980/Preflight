#!/usr/bin/env bash
# Builds artifacts/Preflight-<version>-win-x64-setup.exe. Needs the .NET 10 SDK and NSIS 3 (makensis);
# works on Linux, macOS and Windows (Git Bash).
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/Preflight.App/Preflight.App.csproj)
rm -rf artifacts/publish/win-x64
dotnet publish src/Preflight.App -c Release -r win-x64 --self-contained -o artifacts/publish/win-x64
makensis -V2 -DVERSION="$VERSION" installer/preflight.nsi
ls -l "artifacts/Preflight-$VERSION-win-x64-setup.exe"
