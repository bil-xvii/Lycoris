#!/usr/bin/env bash
# Runs the unit tests on Linux (cloud sessions). WPF can't run here, so this installs the managed
# Microsoft.WindowsDesktop.App assemblies from the NuGet runtime pack as a private shared framework.
# Expected on Linux: ~170 failures that need Windows (registry, P/Invoke, Windows paths, cmd/PowerShell,
# file locks). Everything else (catalogs, texts, search, journal trust, profile import, drift…) runs for real.
# Usage: tools/test-on-linux.sh [dotnet test args…], e.g. --filter "FullyQualifiedName~JournalSecurityTests"
set -euo pipefail
cd "$(dirname "$0")/.."
SYS=$(dirname "$(readlink -f "$(command -v dotnet)")")
ROOT="${TMPDIR:-/tmp}/lycoris-dnroot"
VER=$(ls "$SYS/shared/Microsoft.NETCore.App" | sort -V | tail -1)
dotnet build tests/Lycoris.Tests/Lycoris.Tests.csproj -p:EnableWindowsTargeting=true -v q -nologo >/dev/null
PACK="$HOME/.nuget/packages/microsoft.windowsdesktop.app.runtime.win-x64/$VER/runtimes/win-x64/lib/net10.0"
[ -d "$PACK" ] || { echo "runtime pack $VER not in the NuGet cache (run dotnet publish -r win-x64 --self-contained once)"; exit 1; }
if [ ! -d "$ROOT/shared/Microsoft.WindowsDesktop.App/$VER" ]; then
  mkdir -p "$ROOT/shared/Microsoft.WindowsDesktop.App/$VER"
  for x in host sdk packs sdk-manifests templates metadata; do [ -e "$SYS/$x" ] && ln -sfn "$SYS/$x" "$ROOT/$x"; done
  ln -sfn "$SYS/shared/Microsoft.NETCore.App" "$ROOT/shared/Microsoft.NETCore.App"
  cp "$PACK"/*.dll "$PACK"/*.json "$ROOT/shared/Microsoft.WindowsDesktop.App/$VER/"
  cp "$SYS/dotnet" "$ROOT/dotnet"
fi
DOTNET_ROOT="$ROOT" "$ROOT/dotnet" test tests/Lycoris.Tests/Lycoris.Tests.csproj --no-build -p:EnableWindowsTargeting=true "$@"
