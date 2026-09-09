# build.ps1 — publish the tray application and package it as an MSI.
#
#   installer\build.ps1              → installer\out\Agience-0.1.0-x64.msi
#   installer\build.ps1 -Configuration Debug
#
# ⛔ SELF-CONTAINED, SO THE PACKAGE CARRIES NO PREREQUISITE. A framework-dependent build would need
# the .NET 8 Desktop runtime on the target machine — a second thing to install before the thing you
# installed will start, and a failure mode that presents as a dialog about a missing DLL. This is
# already an application whose FIRST RUN asks the person to install Python; asking for two runtimes
# is not an installer anybody finishes.
#
# ⚠ TRIMMING STAYS OFF. WinForms reflects over designer-generated types, and a trimmed tray
# application fails at the first menu — at run time, on somebody else's machine, with a
# MissingMethodException naming a type nobody wrote.
#
# ⛔ THE FLAGS ARE HERE AND NOT IN THE .csproj. Pinning a RuntimeIdentifier in the project forces
# every referencing project — the test project first — to pin the same one, and a mismatch fails
# restore with a message about package graphs rather than about what is actually wrong.
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    # ⚠ Kept in step with the Version in Agience.Manager.csproj and the Package/@Version in
    # Agience.wxs. Three copies is two too many; this parameter is what a release bumps, and the
    # check below refuses when they have drifted.
    [string]$Version = '0.1.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = Split-Path $here -Parent
$publish = Join-Path $here "obj\publish\$Runtime"
$out = Join-Path $here 'out'
$msi = Join-Path $out "Agience-$Version-$($Runtime -replace '^win-', '').msi"

function Step($text) { Write-Host "  [build]  $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "  [ok]     $text" -ForegroundColor Green }
function Fail($text) { Write-Host "  [error]  $text" -ForegroundColor Red; exit 1 }

# ── the version has to agree in three places ─────────────────────────────────────────────────────
# ⛔ CHECKED RATHER THAN TRUSTED. A binary whose file properties say 0.1.0 inside an MSI that
# registers 0.2.0 is a support call nobody can resolve over the phone, and nothing else notices.
$wxs = Get-Content (Join-Path $here 'Agience.wxs') -Raw
if ($wxs -notmatch "Version=`"$([regex]::Escape($Version))\.0`"") {
    Fail "Agience.wxs does not carry Version=`"$Version.0`". Bump it, or pass -Version to match."
}
$csproj = Get-Content (Join-Path $root 'src\Agience.Manager\Agience.Manager.csproj') -Raw
if ($csproj -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    Fail "Agience.Manager.csproj does not carry <Version>$Version</Version>."
}
Ok "version $Version agrees across the project and the package"

# ── the icon ─────────────────────────────────────────────────────────────────────────────────────
# Generated rather than committed, so it cannot drift from the green the tray paints for Healthy.
Step 'drawing agience.ico'
& (Join-Path $here 'make-icon.ps1') | Out-Null
Ok 'agience.ico'

# ── publish ──────────────────────────────────────────────────────────────────────────────────────
Step "publishing $Configuration/$Runtime (self-contained, single file)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

dotnet publish (Join-Path $root 'src\Agience.Manager') `
    -c $Configuration `
    -r $Runtime `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $publish

if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed' }

$exe = Join-Path $publish 'Agience.exe'
if (-not (Test-Path $exe)) { Fail "publish produced no $exe" }

# ⚠ ASSERTED, BECAUSE SINGLE-FILE IS A REQUEST RATHER THAN A GUARANTEE. A publish that quietly
# emitted loose assemblies still produces a working exe HERE and an MSI that installs one file and
# is missing every dependency THERE.
$loose = Get-ChildItem $publish -File | Where-Object { $_.Name -ne 'Agience.exe' }
if ($loose) {
    Fail ("publish is not single-file; it also emitted: " + ($loose.Name -join ', '))
}
Ok ("Agience.exe  {0:N1} MB" -f ((Get-Item $exe).Length / 1MB))

# ── package ──────────────────────────────────────────────────────────────────────────────────────
Step 'building the MSI'
New-Item -ItemType Directory -Path $out -Force | Out-Null

# ⛔ RUN FROM THE MANAGER DIRECTORY, NOT FROM installer\. `dotnet wix` resolves the tool manifest by
# walking up from the working directory, but it looks for its EXTENSIONS in `.wix\extensions` of the
# working directory ALONE — no parent search. Run from anywhere else and the build fails with
# "The extension 'WixToolset.UI.wixext' could not be found", naming a package that is installed.
#
# `-b installer` then tells the compiler where the source files the .wxs names relatively —
# agience.ico, license.rtf — actually live.
Push-Location $root
try {
    dotnet wix build (Join-Path 'installer' 'Agience.wxs') `
        -arch x64 `
        -b installer `
        -d "PublishDir=$publish" `
        -ext WixToolset.UI.wixext `
        -ext WixToolset.Util.wixext `
        -o $msi
    if ($LASTEXITCODE -ne 0) { Fail 'wix build failed' }
}
finally {
    Pop-Location
}

Ok ("$msi  {0:N1} MB" -f ((Get-Item $msi).Length / 1MB))
Write-Host ''
Write-Host '  Install:    msiexec /i "' -NoNewline; Write-Host $msi -NoNewline; Write-Host '"'
Write-Host '  Uninstall:  msiexec /x {9C1B2F60-3F2E-4C4B-9F1A-1D5E2A6B7C80}'
Write-Host '  Verify:     "%ProgramFiles%\Agience\Agience.exe" --check'
Write-Host ''
Write-Host '  Installing is per-machine, so msiexec asks for administrator. A double-click on the'
Write-Host '  .msi gets the UAC prompt; an unelevated /qn fails with 1925 and rolls back to 1603.'
Write-Host ''
