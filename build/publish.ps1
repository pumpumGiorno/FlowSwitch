<#
.SYNOPSIS
    Publishes FlowSwitch (resident app + Settings) into one folder, ready to copy or zip.

.DESCRIPTION
    Each project is published into its own staging folder and then merged. Both apps run from the
    same folder, so a file present in both outputs must be byte-identical: otherwise one app would
    silently run with the other's copy (this is how a mismatched Microsoft.Windows.SDK.NET.dll once
    broke the renderer). Any conflict fails the build, and the result is verified afterwards.

.EXAMPLE
    ./build/publish.ps1
    ./build/publish.ps1 -SelfContained -Runtime win-arm64 -Zip
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [switch] $SelfContained,
    [string] $Output = [IO.Path]::Combine($PSScriptRoot, '..', 'artifacts', 'FlowSwitch'),
    [switch] $Zip
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$sc = if ($SelfContained) { 'true' } else { 'false' }
$staging = Join-Path $root 'artifacts/.staging'

foreach ($dir in $Output, $staging) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir | Out-Null
}
$Output = (Resolve-Path $Output).Path

$projects = 'FlowSwitch', 'FlowSwitch.Settings'
foreach ($project in $projects) {
    Write-Host "Publishing $project ($Runtime, self-contained: $sc)" -ForegroundColor Cyan
    dotnet publish (Join-Path $root "src/$project/$project.csproj") -c Release -r $Runtime --self-contained $sc -o (Join-Path $staging $project) -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

# Known, intended overlap: a self-contained .NET runtime carries a tiny WindowsBase.dll facade
# (4.0.0.0), WPF carries the real WindowsBase.dll (10.0.0.0). FlowSwitch.exe never uses it and a
# higher version satisfies any reference, so the Settings (WPF) copy is the one to keep.
$settingsWins = @('WindowsBase.dll')
foreach ($name in $settingsWins) {
    $wpfCopy = Join-Path $staging "FlowSwitch.Settings/$name"
    if (Test-Path $wpfCopy) {
        Copy-Item $wpfCopy (Join-Path $Output $name)
        Remove-Item (Join-Path $staging "FlowSwitch/$name") -ErrorAction SilentlyContinue
    }
}

$conflicts = @()
foreach ($project in $projects) {
    $from = (Resolve-Path (Join-Path $staging $project)).Path
    foreach ($file in Get-ChildItem $from -Recurse -File) {
        $relative = $file.FullName.Substring($from.Length).TrimStart('\', '/')
        $target = Join-Path $Output $relative
        if (Test-Path $target) {
            if ($settingsWins -contains $relative) { continue }
            if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash $target).Hash) { $conflicts += $relative }
        }
        else {
            New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
            Copy-Item $file.FullName $target
        }
    }
}
if ($conflicts.Count -gt 0) {
    $conflicts | ForEach-Object { Write-Host "CONFLICT: $_ differs between FlowSwitch and FlowSwitch.Settings" -ForegroundColor Red }
    throw 'Publish failed: the two apps ship different versions of the same file.'
}
Remove-Item $staging -Recurse -Force

# Debug symbols are embedded in Release builds; drop stray .pdb files from dependencies.
Get-ChildItem $Output -Filter *.pdb | Remove-Item -Force

# ── Verification ──
$required = 'FlowSwitch.exe', 'FlowSwitch.dll', 'FlowSwitch.deps.json', 'FlowSwitch.runtimeconfig.json',
    'FlowSwitch.Settings.exe', 'FlowSwitch.Settings.dll', 'FlowSwitch.Settings.deps.json', 'FlowSwitch.Settings.runtimeconfig.json',
    'FlowSwitch.Core.dll', 'Microsoft.Windows.SDK.NET.dll', 'WinRT.Runtime.dll', 'SharpGen.Runtime.dll', 'SharpGen.Runtime.COM.dll',
    'Vortice.Direct3D11.dll', 'Vortice.DXGI.dll', 'Vortice.Direct2D1.dll', 'Vortice.DirectComposition.dll',
    'Vortice.D3DCompiler.dll', 'Vortice.DirectX.dll', 'Vortice.Mathematics.dll'
$missing = $required | Where-Object { -not (Test-Path (Join-Path $Output $_)) }
if ($missing) { throw "Publish verification failed, missing: $($missing -join ', ')" }

function Get-AssemblyVersions([string] $depsFile) {
    $map = @{}
    $deps = Get-Content $depsFile -Raw | ConvertFrom-Json
    foreach ($target in $deps.targets.PSObject.Properties) {
        foreach ($library in $target.Value.PSObject.Properties) {
            $runtime = $library.Value.runtime
            if ($null -eq $runtime) { continue }
            foreach ($asm in $runtime.PSObject.Properties) {
                if ($asm.Value.assemblyVersion) { $map[[IO.Path]::GetFileName($asm.Name)] = $asm.Value.assemblyVersion }
            }
        }
    }
    return $map
}
$hostVersions = Get-AssemblyVersions (Join-Path $Output 'FlowSwitch.deps.json')
$settingsVersions = Get-AssemblyVersions (Join-Path $Output 'FlowSwitch.Settings.deps.json')
foreach ($name in $hostVersions.Keys) {
    if ($settingsWins -contains $name) { continue }
    if ($settingsVersions.ContainsKey($name) -and $settingsVersions[$name] -ne $hostVersions[$name]) {
        throw "Publish verification failed: $name — FlowSwitch expects $($hostVersions[$name]), Settings expects $($settingsVersions[$name])."
    }
}
Write-Host "Publish verification passed." -ForegroundColor Green

if ($Zip) {
    $zipPath = "$Output-$Runtime.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zipPath
    Write-Host "Created $zipPath" -ForegroundColor Green
}

Write-Host "FlowSwitch published to $Output" -ForegroundColor Green
Write-Host "Start FlowSwitch.Settings.exe (or FlowSwitch.exe); FlowSwitch lives in the notification area."
