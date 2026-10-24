<#
.SYNOPSIS
    Builds Mendrain release packages with Velopack: an installer, a portable ZIP and the update feed.

.DESCRIPTION
    1. Runs the core tests when the (private, not published) tests folder is present, unless -SkipTests.
    2. Publishes a self-contained Release build for win-x64 to artifacts\publish\Mendrain.
    3. Removes debug symbols and adds the notice files.
    4. Takes this version's notes from CHANGELOG.md (shown in the in-app update notification).
    5. Optionally (-PreviousFromGitHub) downloads the latest published release's package so Velopack can build
       a small delta update.
    6. Packs with Velopack (vpk, pinned in dotnet-tools.json) into artifacts\releases:
       the update feed (releases.win.json, *.nupkg) plus Setup.exe and the portable ZIP.
    7. Copies the installer and portable ZIP to artifacts\Mendrain-Setup-x64.exe and
       artifacts\Mendrain-Portable-x64.zip, and writes artifacts\SHA256SUMS.txt.
    Any failing step stops the script with an error.

    The pack ID is "MendrainApp", so the app installs to %LOCALAPPDATA%\MendrainApp. Never change it to
    "Mendrain": uninstalling deletes the whole install folder, and %LOCALAPPDATA%\Mendrain holds user data.

.EXAMPLE
    pwsh tools/package.ps1
    pwsh tools/package.ps1 -Version 1.0.0 -PreviousFromGitHub
#>
param(
    # x.y.z (or x.y.z-preview.n); defaults to <Version> in src/Mendrain/Mendrain.csproj.
    [string]$Version,
    [switch]$SkipTests,
    # Download the latest published GitHub release first, to generate a delta package (used by CI).
    [switch]$PreviousFromGitHub,
    # Test builds only: let MENDRAIN_UPDATE_FEED point the updater at a local folder. Never use for releases.
    [switch]$TestFeed
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$packId = "MendrainApp"
$repoUrl = "https://github.com/esteriavaelis/mendrain"

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

if (-not $Version) {
    [xml]$proj = Get-Content (Join-Path $root "src/Mendrain/Mendrain.csproj")
    $Version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Version '$Version' must look like 1.2.3 or 1.2.3-preview.1." }
$numericVersion = ($Version -split '-')[0]

$artifacts = Join-Path $root "artifacts"
$publishDir = Join-Path $artifacts "publish/Mendrain"
$releasesDir = Join-Path $artifacts "releases"
$portableZip = Join-Path $artifacts "Mendrain-Portable-x64.zip"
$installerExe = Join-Path $artifacts "Mendrain-Setup-x64.exe"
$notesFile = Join-Path $artifacts "release-notes.md"

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
foreach ($old in @($publishDir, $releasesDir, $portableZip, $installerExe, $notesFile)) {
    if (Test-Path $old) { Remove-Item $old -Recurse -Force }
}
$extra = @()
if ($TestFeed) {
    Write-Warning "Test build: the update source can be overridden with MENDRAIN_UPDATE_FEED. Do not publish it."
    $extra += "-p:UpdateFeedOverride=true"
}
Invoke-Checked "Publish" {
    dotnet publish src/Mendrain/Mendrain.csproj -c Release -r win-x64 --self-contained true `
        -p:Version=$numericVersion -p:InformationalVersion=$Version @extra -o $publishDir --nologo
}
foreach ($required in @("Mendrain.exe", "Mendrain.pri", "Microsoft.ui.xaml.dll", "SharpCompress.dll", "Velopack.dll")) {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "Publish output is missing $required." }
}

# Debug symbols are development-only.
Get-ChildItem $publishDir -Recurse -Filter *.pdb | Remove-Item -Force
foreach ($notice in @("THIRD-PARTY-NOTICES.md", "LICENSE")) {
    if (Test-Path (Join-Path $root $notice)) { Copy-Item (Join-Path $root $notice) $publishDir }
}

Write-Host "== Release notes"
# The "## [x.y.z]" section of CHANGELOG.md, or "## [Unreleased]" when there is none yet.
# Read as UTF-8 explicitly (Windows PowerShell 5.1 would otherwise use the ANSI code page).
$changelog = [System.IO.File]::ReadAllLines((Join-Path $root "CHANGELOG.md"), [System.Text.Encoding]::UTF8)
$start = [Array]::FindIndex($changelog, [Predicate[string]]{ param($l) $l -match "^## \[$([regex]::Escape($Version))\]" })
if ($start -lt 0) { $start = [Array]::FindIndex($changelog, [Predicate[string]]{ param($l) $l -match '^## \[Unreleased\]' }) }
$notes = @()
if ($start -ge 0) {
    for ($i = $start + 1; $i -lt $changelog.Count -and $changelog[$i] -notmatch '^## '; $i++) { $notes += $changelog[$i] }
}
New-Item -ItemType Directory -Force $artifacts | Out-Null
[System.IO.File]::WriteAllText($notesFile, ($notes -join "`n").Trim() + "`n", (New-Object System.Text.UTF8Encoding $false))

Write-Host "== Velopack"
Invoke-Checked "Restoring vpk" { dotnet tool restore }
New-Item -ItemType Directory -Force $releasesDir | Out-Null
if ($PreviousFromGitHub) {
    # The first Velopack release has no previous package (0.1.0 used Inno Setup); then no delta is built.
    # In CI, GITHUB_TOKEN avoids API rate limits. It is only used here, never built into the app.
    $token = @()
    if ($env:GITHUB_TOKEN) { $token = @("--token", $env:GITHUB_TOKEN) }
    dotnet vpk download github --repoUrl $repoUrl -o $releasesDir @token
    if ($LASTEXITCODE -ne 0) { Write-Warning "No previous Velopack release found; building a full package only." }
}
Invoke-Checked "vpk pack" {
    dotnet vpk pack --packId $packId --packVersion $Version --packDir $publishDir --mainExe Mendrain.exe --runtime win-x64 `
        --packTitle Mendrain --packAuthors "Esteria Vaelis" --icon (Join-Path $root "src/Mendrain/Assets/Mendrain.ico") `
        --releaseNotes $notesFile --shortcuts StartMenuRoot -o $releasesDir
}
$setup = Join-Path $releasesDir "$packId-win-Setup.exe"
$portable = Join-Path $releasesDir "$packId-win-Portable.zip"
foreach ($file in @($setup, $portable, (Join-Path $releasesDir "releases.win.json"))) {
    if (-not (Test-Path $file)) { throw "Velopack did not create $file." }
}
Copy-Item $setup $installerExe
Copy-Item $portable $portableZip

Write-Host "== Checksums"
$outputs = @($installerExe, $portableZip) + (Get-ChildItem $releasesDir -Filter "*-$Version-*.nupkg" | ForEach-Object FullName)
$outputs | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf)
} | Set-Content (Join-Path $artifacts "SHA256SUMS.txt") -Encoding ascii

Write-Host ""
foreach ($o in $outputs) { Write-Host ("{0,-40} {1,8:N1} MB" -f (Split-Path $o -Leaf), ((Get-Item $o).Length / 1MB)) }
