#!/usr/bin/env bash
# Publishes FlowSwitch (resident app + Settings) into one folder, ready to copy or zip.
# Same steps as publish.ps1, for Linux/macOS build machines and CI.
#
#   ./build/publish.sh                          # framework-dependent win-x64 → artifacts/FlowSwitch
#   ./build/publish.sh --self-contained --zip   # includes the .NET runtime, plus a .zip
#   ./build/publish.sh --runtime win-arm64
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
runtime=win-x64
sc=false
zip=false
out="$root/artifacts/FlowSwitch"
while [ $# -gt 0 ]; do
  case "$1" in
    --runtime) runtime="$2"; shift 2 ;;
    --self-contained) sc=true; shift ;;
    --zip) zip=true; shift ;;
    --output) out="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

staging="$root/artifacts/.staging"
rm -rf "$out" "$staging"
mkdir -p "$out" "$staging"

# Each project is published into its own folder first, then merged. Both apps share one folder at
# runtime, so a file that exists in both outputs must be byte-identical — otherwise one app would
# silently run with the other's copy (this is exactly how a mismatched Microsoft.Windows.SDK.NET.dll
# once broke the renderer). Any such conflict fails the build.
for project in FlowSwitch FlowSwitch.Settings; do
  echo "Publishing $project ($runtime, self-contained: $sc)"
  dotnet publish "$root/src/$project/$project.csproj" -c Release -r "$runtime" --self-contained "$sc" \
    -o "$staging/$project" -nologo
done

# Known, intended overlap: a self-contained .NET runtime carries a tiny WindowsBase.dll facade
# (4.0.0.0), WPF carries the real WindowsBase.dll (10.0.0.0). FlowSwitch.exe never uses it and a
# higher version satisfies any reference, so the Settings (WPF) copy is the one to keep.
settings_wins=" ./WindowsBase.dll "
for rel in $settings_wins; do
  if [ -f "$staging/FlowSwitch.Settings/$rel" ]; then
    mkdir -p "$(dirname "$out/$rel")"
    cp "$staging/FlowSwitch.Settings/$rel" "$out/$rel"
    rm -f "$staging/FlowSwitch/$rel"
  fi
done

for project in FlowSwitch FlowSwitch.Settings; do
  (cd "$staging/$project" && find . -type f) | while read -r rel; do
    src="$staging/$project/$rel"
    dst="$out/$rel"
    if [ -e "$dst" ]; then
      case "$settings_wins" in *" $rel "*) continue ;; esac
      if ! cmp -s "$src" "$dst"; then
        echo "CONFLICT: $rel differs between FlowSwitch and FlowSwitch.Settings" >&2
        echo x >> "$staging/conflicts"
      fi
    else
      mkdir -p "$(dirname "$dst")"
      cp "$src" "$dst"
    fi
  done
done
if [ -s "$staging/conflicts" ]; then
  echo "Publish failed: the two apps ship different versions of the same file (see above)." >&2
  exit 1
fi

find "$out" -maxdepth 1 -name '*.pdb' -delete
"$root/build/verify-publish.sh" "$out"
rm -rf "$staging"

if [ "$zip" = true ]; then
  zipPath="$out-$runtime.zip"
  rm -f "$zipPath"
  (cd "$out" && zip -qr "$zipPath" .)
  echo "Created $zipPath"
fi
echo "FlowSwitch published to $out"
echo "Start FlowSwitch.Settings.exe (or FlowSwitch.exe); FlowSwitch lives in the notification area."
