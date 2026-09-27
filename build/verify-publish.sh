#!/usr/bin/env bash
# Checks a published FlowSwitch folder: every file the apps need at runtime is present, and the
# assemblies both apps share are the versions each app was compiled against.
set -euo pipefail
dir="${1:?usage: verify-publish.sh <publish folder>}"
fail=0

for f in FlowSwitch.exe FlowSwitch.dll FlowSwitch.deps.json FlowSwitch.runtimeconfig.json \
         FlowSwitch.Settings.exe FlowSwitch.Settings.dll FlowSwitch.Settings.deps.json FlowSwitch.Settings.runtimeconfig.json \
         FlowSwitch.Core.dll Microsoft.Windows.SDK.NET.dll WinRT.Runtime.dll \
         SharpGen.Runtime.dll SharpGen.Runtime.COM.dll Vortice.Direct3D11.dll Vortice.DXGI.dll \
         Vortice.Direct2D1.dll Vortice.DirectComposition.dll Vortice.D3DCompiler.dll Vortice.DirectX.dll Vortice.Mathematics.dll; do
  if [ ! -f "$dir/$f" ]; then echo "MISSING: $f" >&2; fail=1; fi
done

# assemblyVersion each app expects for the shared WinRT projection.
for asm in Microsoft.Windows.SDK.NET.dll WinRT.Runtime.dll FlowSwitch.Core.dll; do
  host=$(grep -A3 "\"$asm\": {" "$dir/FlowSwitch.deps.json" | grep -o '"assemblyVersion": "[^"]*"' | head -1 || true)
  settings=$(grep -A3 "\"$asm\": {" "$dir/FlowSwitch.Settings.deps.json" | grep -o '"assemblyVersion": "[^"]*"' | head -1 || true)
  if [ -n "$host" ] && [ -n "$settings" ] && [ "$host" != "$settings" ]; then
    echo "VERSION MISMATCH: $asm — FlowSwitch expects $host, Settings expects $settings" >&2
    fail=1
  fi
done

if [ "$fail" -ne 0 ]; then
  echo "Publish verification FAILED for $dir" >&2
  exit 1
fi
echo "Publish verification passed ($(find "$dir" -type f | wc -l) files)."
