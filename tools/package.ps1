<#
.SYNOPSIS
    Builds Mendrain release packages: a portable ZIP and (when Inno Setup is available) an installer.

.DESCRIPTION
    1. Runs the core tests when the (private, not published) tests folder is present, unless -SkipTests.
    2. Publishes a self-contained Release build for win-x64 to artifacts\publish\Mendrain.
    3. Removes debug symbols and adds the notice files.
    4. Zips it to artifacts\Mendrain-Portable-x64.zip.
    5. Compiles installer\Mendrain.iss with Inno Setup 6 to artifacts\Mendrain-Setup-x64.exe.
    6. Writes artifacts\SHA256SUMS.txt.
    Any failing step stops the script with an error.

.EXAMPLE
    pwsh tools/package.ps1
    pwsh tools/package.ps1 -Version 1.0.0 -RequireInstaller
#>
param(
    # x.y.z; defaults to <Version> in src/Mendrain/Mendrain.csproj.
    [string]$Version,
    [switch]$SkipTests,
    # Fail instead of warning when Inno Setup is not installed (used by CI).
    [switch]$RequireInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

if (-not $Version) {
    [xml]$proj = Get-Content (Join-Path $root "src/Mendrain/Mendrain.csproj")
    $Version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' must look like 1.2.3." }

$artifacts = Join-Path $root "artifacts"
$publishDir = Join-Path $artifacts "publish/Mendrain"
$portableZip = Join-Path $artifacts "Mendrain-Portable-x64.zip"
$installerExe = Join-Path $artifacts "Mendrain-Setup-x64.exe"

Write-Host "== Mendrain $Version"

$testProject = Join-Path $root "tests/Mendrain.Core.Tests/Mendrain.Core.Tests.csproj"
if ($SkipTests) {
    Write-Host "== Tests skipped (-SkipTests)"
} elseif (Test-Path $testProject) {
    Write-Host "== Tests"
    Invoke-Checked "Tests" { dotnet test $testProject -c Release --nologo }
} else {
    Write-Host "== Tests not present in this checkout; skipping"
}

Write-Host "== Publish (self-contained, win-x64)"
foreach ($old in @($publishDir, $portableZip, $installerExe)) {
    if (Test-Path $old) { Remove-Item $old -Recurse -Force }
}
Invoke-Checked "Publish" {
    dotnet publish src/Mendrain/Mendrain.csproj -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -o $publishDir --nologo
}
foreach ($required in @("Mendrain.exe", "Mendrain.pri", "Microsoft.ui.xaml.dll", "SharpCompress.dll")) {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "Publish output is missing $required." }
}

# Debug symbols are development-only.
Get-ChildItem $publishDir -Recurse -Filter *.pdb | Remove-Item -Force
foreach ($notice in @("THIRD-PARTY-NOTICES.md", "LICENSE")) {
    if (Test-Path (Join-Path $root $notice)) { Copy-Item (Join-Path $root $notice) $publishDir }
}

Write-Host "== Portable ZIP"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# Entries go under one "Mendrain/" folder, with "/" separators on every PowerShell version
# (Windows PowerShell's ZipFile.CreateFromDirectory writes "\", which some unzip tools reject).
$publishFull = (Get-Item $publishDir).FullName
$zip = [System.IO.Compression.ZipFile]::Open($portableZip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $publishFull -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($publishFull.Length).TrimStart('\', '/').Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, "Mendrain/$relative",
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $zip.Dispose()
}

Write-Host "== Installer"
$iscc = @(
    $env:ISCC_PATH,
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($iscc) {
    Invoke-Checked "Inno Setup" {
        & $iscc /Qp "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$artifacts" (Join-Path $root "installer/Mendrain.iss")
    }
    if (-not (Test-Path $installerExe)) { throw "Inno Setup finished but $installerExe was not created." }
} elseif ($RequireInstaller) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install it, e.g. 'winget install JRSoftware.InnoSetup', or set ISCC_PATH."
} else {
    Write-Warning "Inno Setup 6 not found: the installer was NOT built. Install it with 'winget install JRSoftware.InnoSetup' and rerun."
}

Write-Host "== Checksums"
$outputs = @($portableZip, $installerExe) | Where-Object { Test-Path $_ }
$outputs | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf)
} | Set-Content (Join-Path $artifacts "SHA256SUMS.txt") -Encoding ascii

Write-Host ""
foreach ($o in $outputs) { Write-Host ("{0,-28} {1,8:N1} MB" -f (Split-Path $o -Leaf), ((Get-Item $o).Length / 1MB)) }
