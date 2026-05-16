#Requires -Version 5.0
<#
.SYNOPSIS
    Build and package a release zip for SE Sprite LCD Layout Tool.

.DESCRIPTION
    1. Rebuilds the project in Release configuration.
    2. Stages every file from bin\Release into a clean folder, EXCLUDING the
       SE/Torch redistributable DLLs that ship from the user's local game
       install (setup.ps1 copies those at first run).
    3. Copies setup.ps1 alongside the staged files.
    4. Zips the staging folder into release\SESpriteLCDLayoutTool-v<version>.zip.

    Run from anywhere - paths are resolved relative to this script.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER SkipBuild
    Skip the msbuild step (use the existing bin\<Configuration> output).

.EXAMPLE
    .\make-release.ps1
    .\make-release.ps1 -SkipBuild
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root      = Split-Path -Parent $MyInvocation.MyCommand.Definition
$projDir   = Join-Path $root "SESpriteLCDLayoutTool"
$projFile  = Join-Path $projDir "SESpriteLCDLayoutTool.csproj"
$binDir    = Join-Path $projDir "bin\$Configuration"
$releaseDir = Join-Path $root "release"
$setupPs1  = Join-Path $root "setup.ps1"

if (-not (Test-Path $projFile)) { Write-Error "Project not found: $projFile"; exit 1 }
if (-not (Test-Path $setupPs1)) { Write-Error "setup.ps1 not found at: $setupPs1"; exit 1 }

# ── 1. Build ──────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host ""
    Write-Host " [1/4] Building $Configuration..." -ForegroundColor Cyan

    $msbuild = $null
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    }
    if (-not $msbuild) { $msbuild = "msbuild" }

    & $msbuild $projFile /t:Restore,Rebuild /p:Configuration=$Configuration /p:Platform="AnyCPU" /nologo /v:minimal
    if ($LASTEXITCODE -ne 0) { Write-Error "Build failed (exit code $LASTEXITCODE)"; exit 1 }
} else {
    Write-Host " [1/4] Skipping build (--SkipBuild)" -ForegroundColor Yellow
}

if (-not (Test-Path $binDir)) { Write-Error "Build output not found: $binDir"; exit 1 }

# ── 2. Discover version ───────────────────────────────────────────────────
$exe = Join-Path $binDir "SESpriteLCDLayoutTool.exe"
if (-not (Test-Path $exe)) { Write-Error "Exe not found in build output: $exe"; exit 1 }
$version = (Get-Item $exe).VersionInfo.FileVersion
if (-not $version) { $version = "0.0.0" }
# Trim trailing .0 if four-part (4.5.0.0 -> 4.5.0)
$version = $version -replace '\.0$',''
Write-Host " [2/4] Detected version: v$version" -ForegroundColor Green

# ── 3. Stage files (excluding SE/Torch redistributables) ──────────────────
$excludeDlls = @(
    # Space Engineers ModSDK / game binaries - shipped by setup.ps1 from user's
    # local SE install. Redistributing them in our release zip is both wasteful
    # and a licensing grey area.
    "Sandbox.Common.dll","Sandbox.Game.dll","Sandbox.Game.XmlSerializers.dll",
    "SpaceEngineers.Game.dll","SpaceEngineers.ObjectBuilders.dll",
    "SpaceEngineers.ObjectBuilders.XmlSerializers.dll",
    "VRage.dll","VRage.Game.dll","VRage.Game.XmlSerializers.dll",
    "VRage.Library.dll","VRage.Math.dll","VRage.Math.XmlSerializers.dll",
    "VRage.XmlSerializers.dll","VRage.Mod.Io.dll","VRage.Input.dll",
    "VRage.Render.dll","VRage.Render11.dll","VRage.NativeWrapper.dll",
    "VRage.Network.dll","VRage.UserInterface.dll",
    "Sandbox.Graphics.dll","Sandbox.RenderDirect.dll",
    "HavokWrapper.dll","RecastDetourWrapper.dll",
    "SharpDX.dll","SharpDX.D3DCompiler.dll","SharpDX.Direct3D11.dll","SharpDX.DXGI.dll",
    # Torch - shipped by setup.ps1 from user's Torch install.
    "Torch.dll","Torch.API.dll","NLog.dll"
)

$stageDir = Join-Path $releaseDir "stage-v$version"
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir | Out-Null

Write-Host " [3/4] Staging files into $stageDir" -ForegroundColor Cyan
$copied = 0
$skipped = 0
Get-ChildItem $binDir -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($binDir.Length).TrimStart('\','/')
    $name = $_.Name

    # Skip excluded DLLs (case-insensitive match on filename only - they sit at bin root)
    if ($excludeDlls -contains $name) { $skipped++; return }

    # Skip pdb/xmldoc to keep zip small - comment these out if you want symbols shipped
    if ($_.Extension -eq ".pdb") { $skipped++; return }
    # Keep .xml docs for SE assemblies that ARE present (intellisense). For others, skip.
    # Simpler rule: skip all .xml at root to mirror the historical zip.
    if ($_.Extension -eq ".xml" -and -not $rel.Contains('\')) { $skipped++; return }

    $dest = Join-Path $stageDir $rel
    $destDir = Split-Path -Parent $dest
    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir | Out-Null }
    Copy-Item $_.FullName $dest -Force
    $copied++
}

# Copy setup.ps1 alongside the exe
Copy-Item $setupPs1 (Join-Path $stageDir "setup.ps1") -Force
$copied++

# Copy THIRD-PARTY-NOTICES.txt (license disclosures for bundled OSS DLLs)
$notices = Join-Path $root "THIRD-PARTY-NOTICES.txt"
if (Test-Path $notices) {
    Copy-Item $notices (Join-Path $stageDir "THIRD-PARTY-NOTICES.txt") -Force
    $copied++
}

# Copy LICENSE / README if present at repo root
foreach ($extra in @("LICENSE", "LICENSE.txt", "LICENSE.md", "README.md")) {
    $p = Join-Path $root $extra
    if (Test-Path $p) {
        Copy-Item $p (Join-Path $stageDir $extra) -Force
        $copied++
    }
}

Write-Host "       Copied  : $copied file(s)" -ForegroundColor Green
Write-Host "       Skipped : $skipped (SE/Torch/pdb/xml)" -ForegroundColor DarkGray

# ── 4. Zip ────────────────────────────────────────────────────────────────
$zipName = "SESpriteLCDLayoutTool-v$version.zip"
$zipPath = Join-Path $releaseDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host " [4/4] Creating $zipName" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zipSize = "{0:N1} MB" -f ((Get-Item $zipPath).Length / 1MB)
Write-Host ""
Write-Host " ------------------------------------------------" -ForegroundColor Green
Write-Host "  Release ready: $zipPath ($zipSize)" -ForegroundColor Green
Write-Host " ------------------------------------------------" -ForegroundColor Green
Write-Host ""
Write-Host " Contents preview:" -ForegroundColor Cyan
Get-ChildItem $stageDir | Select-Object Name, @{n='KB';e={[int]($_.Length/1KB)}} | Format-Table -AutoSize
