<#
.SYNOPSIS
    Publishes FlowSwitch (resident app + Settings) into one folder, ready to copy or zip.

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

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output | Out-Null

foreach ($project in 'src/FlowSwitch/FlowSwitch.csproj', 'src/FlowSwitch.Settings/FlowSwitch.Settings.csproj') {
    Write-Host "Publishing $project ($Runtime, self-contained: $sc)" -ForegroundColor Cyan
    dotnet publish (Join-Path $root $project) -c Release -r $Runtime --self-contained $sc -o $Output -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

# Debug symbols are embedded in Release builds; drop stray .pdb files from dependencies.
Get-ChildItem $Output -Filter *.pdb | Remove-Item -Force

if ($Zip) {
    $zipPath = "$Output-$Runtime.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zipPath
    Write-Host "Created $zipPath" -ForegroundColor Green
}

Write-Host "FlowSwitch published to $Output" -ForegroundColor Green
Write-Host "Run FlowSwitch.exe to start; it lives in the notification area."
