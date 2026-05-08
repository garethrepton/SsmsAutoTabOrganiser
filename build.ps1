[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',
    [switch]$Clean,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found. Install VS Build Tools 2022." }

# vswhere can also report MSBuild that ships with SSMS / other VS-based products.
# Restrict to a real VS product (BuildTools, Community, Pro, Enterprise).
$vsProducts = @(
    'Microsoft.VisualStudio.Product.BuildTools',
    'Microsoft.VisualStudio.Product.Community',
    'Microsoft.VisualStudio.Product.Professional',
    'Microsoft.VisualStudio.Product.Enterprise'
)

$msbuild = $null
foreach ($p in $vsProducts) {
    $candidate = & $vswhere -all -prerelease -products $p -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    if ($candidate) { $msbuild = $candidate; break }
}
if (-not $msbuild) {
    throw "MSBuild not found in a VS Build Tools / Community / Pro / Enterprise install. Install VS Build Tools 2022 with the 'Visual Studio extension development' workload."
}

Write-Host "MSBuild: $msbuild"

$sln = Join-Path $PSScriptRoot 'AutoTabOrganiser.sln'

$targets = if ($Clean) { 'Clean;Restore;Rebuild' } else { 'Restore;Build' }
& $msbuild $sln /t:$targets /p:Configuration=$Configuration /v:m /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

$vsix = Join-Path $PSScriptRoot "src\AutoTabOrganiser\bin\$Configuration\net472\AutoTabOrganiser.vsix"
Write-Host ""
Write-Host "VSIX: $vsix"

if ($Install) {
    $ssmsRoot = "${env:ProgramFiles}\Microsoft SQL Server Management Studio"
    $candidates = @()
    if (Test-Path $ssmsRoot) {
        $candidates += Get-ChildItem -Path $ssmsRoot -Directory | Where-Object { $_.Name -match '^\d+$' } | Sort-Object { [int]$_.Name } -Descending
    }
    Get-ChildItem -Path "${env:ProgramFiles}\Microsoft SQL Server Management Studio *" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | ForEach-Object { $candidates += $_ }

    $installer = $null
    foreach ($d in $candidates) {
        $maybe1 = Join-Path $d.FullName 'Common7\IDE\VSIXInstaller.exe'
        $maybe2 = Join-Path $d.FullName 'Release\Common7\IDE\VSIXInstaller.exe'
        if (Test-Path $maybe1) { $installer = $maybe1; break }
        if (Test-Path $maybe2) { $installer = $maybe2; break }
    }
    if (-not $installer) { throw "VSIXInstaller.exe not found under '$ssmsRoot'." }

    Write-Host "Installer: $installer"
    & $installer /quiet $vsix
    if ($LASTEXITCODE -ne 0) { throw "VSIX install failed (exit $LASTEXITCODE)" }
    Write-Host "Installed into SSMS."
}
