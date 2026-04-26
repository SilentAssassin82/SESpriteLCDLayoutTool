#Requires -Version 5.0
<#
.SYNOPSIS
    First-time setup for SE Sprite LCD Layout Tool.
    Copies required DLLs from your local Space Engineers / Torch installations.

.DESCRIPTION
    SE Sprite LCD Layout Tool depends on DLLs that ship with Space Engineers
    and (optionally) Torch.  This script locates them automatically and copies
    them next to the exe.  Run it once after extracting the release zip.

.NOTES
    Must be run from the folder containing SESpriteLCDLayoutTool.exe.
    Does NOT require elevated (admin) privileges.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$exePath   = Join-Path $scriptDir "SESpriteLCDLayoutTool.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Run this script from the same folder as SESpriteLCDLayoutTool.exe"
    exit 1
}

Write-Host ""
Write-Host " â•”â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•—"
Write-Host " â•‘  SE Sprite LCD Layout Tool â€” First Setup   â•‘"
Write-Host " â•šâ•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•"
Write-Host ""

# â”€â”€ Locate Space Engineers ModSDK / game â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function Find-SEBinDir {
    # 1. ModSDK (preferred â€” has the profile DLLs we need)
    $sdkCandidates = @(
        "C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineersModSDK\Bin64_Profile",
        "D:\SteamLibrary\steamapps\common\SpaceEngineersModSDK\Bin64_Profile",
        "E:\SteamLibrary\steamapps\common\SpaceEngineersModSDK\Bin64_Profile",
        "F:\SteamLibrary\steamapps\common\SpaceEngineersModSDK\Bin64_Profile",
        "G:\SteamLibrary\steamapps\common\SpaceEngineersModSDK\Bin64_Profile"
    )
    foreach ($p in $sdkCandidates) {
        if (Test-Path (Join-Path $p "Sandbox.Common.dll")) { return $p }
    }

    # 2. Fall back to Steam libraryfolders.vdf to find any Steam library
    $steamRoot = "C:\Program Files (x86)\Steam"
    $vdf = Join-Path $steamRoot "steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        $paths = Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' |
                 ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\','\\' }
        foreach ($lib in $paths) {
            $sdk = Join-Path $lib "steamapps\common\SpaceEngineersModSDK\Bin64_Profile"
            if (Test-Path (Join-Path $sdk "Sandbox.Common.dll")) { return $sdk }
            $game = Join-Path $lib "steamapps\common\SpaceEngineers\Bin64"
            if (Test-Path (Join-Path $game "Sandbox.Common.dll")) { return $game }
        }
    }
    return $null
}

function Find-TorchDir {
    $candidates = @(
        "C:\Torch",
        "D:\Torch",
        "C:\DedicatedServer\Torch",
        "D:\DedicatedServer\Torch",
        (Join-Path $env:LOCALAPPDATA "Torch")
    )
    foreach ($p in $candidates) {
        if (Test-Path (Join-Path $p "Torch.dll")) { return $p }
    }
    return $null
}

# â”€â”€ DLLs required from SE ModSDK / game â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
$seDlls = @(
    "Sandbox.Common.dll",
    "Sandbox.Game.dll",
    "Sandbox.Game.XmlSerializers.dll",
    "SpaceEngineers.Game.dll",
    "SpaceEngineers.ObjectBuilders.dll",
    "SpaceEngineers.ObjectBuilders.XmlSerializers.dll",
    "VRage.dll",
    "VRage.Game.dll",
    "VRage.Game.XmlSerializers.dll",
    "VRage.Library.dll",
    "VRage.Math.dll",
    "VRage.Math.XmlSerializers.dll",
    "VRage.Mod.Io.dll",
    "VRage.Input.dll",
    "VRage.Render.dll",
    "VRage.Render11.dll",
    "VRage.NativeWrapper.dll",
    "VRage.Network.dll",
    "VRage.UserInterface.dll",
    "Sandbox.Graphics.dll",
    "Sandbox.RenderDirect.dll",
    "HavokWrapper.dll",
    "RecastDetourWrapper.dll",
    "SharpDX.dll",
    "SharpDX.D3DCompiler.dll",
    "SharpDX.Direct3D11.dll",
    "SharpDX.DXGI.dll",
    "0Harmony.dll"
)

# â”€â”€ DLLs required from Torch â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
$torchDlls = @(
    "Torch.dll",
    "Torch.API.dll",
    "NLog.dll"
)

# â”€â”€ Locate SE â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
Write-Host " [1/3] Locating Space Engineers..."
$seDir = Find-SEBinDir
if (-not $seDir) {
    Write-Host ""
    Write-Host " âœ— Could not find Space Engineers ModSDK or game installation." -ForegroundColor Red
    Write-Host "   Please enter the full path to your SE Bin64_Profile folder:"
    Write-Host "   (e.g. D:\Steam\steamapps\common\SpaceEngineersModSDK\Bin64_Profile)"
    Write-Host ""
    $seDir = Read-Host "   SE path"
    if (-not (Test-Path (Join-Path $seDir "Sandbox.Common.dll"))) {
        Write-Error "Sandbox.Common.dll not found at '$seDir' â€” aborting."
        exit 1
    }
}
Write-Host "   Found: $seDir" -ForegroundColor Green

# â”€â”€ Locate Torch (optional) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
Write-Host ""
Write-Host " [2/3] Locating Torch (optional)..."
$torchDir = Find-TorchDir
if (-not $torchDir) {
    Write-Host "   Not found â€” Torch features will be unavailable." -ForegroundColor Yellow
    Write-Host "   If you have Torch installed, enter its path (or press Enter to skip):"
    $input = Read-Host "   Torch path"
    if ($input -and (Test-Path (Join-Path $input "Torch.dll"))) {
        $torchDir = $input
        Write-Host "   Found: $torchDir" -ForegroundColor Green
    }
} else {
    Write-Host "   Found: $torchDir" -ForegroundColor Green
}

# â”€â”€ Copy DLLs â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
Write-Host ""
Write-Host " [3/3] Copying dependencies..."
$copied  = 0
$skipped = 0
$missing = @()

foreach ($dll in $seDlls) {
    $src  = Join-Path $seDir $dll
    $dest = Join-Path $scriptDir $dll
    if (Test-Path $dest) { $skipped++; continue }
    if (Test-Path $src)  { Copy-Item $src $dest; $copied++ }
    else                 { $missing += "SE: $dll" }
}

# Also copy XML docs if present (non-fatal if absent)
foreach ($xml in (Get-ChildItem $seDir -Filter "*.xml" -ErrorAction SilentlyContinue)) {
    $dest = Join-Path $scriptDir $xml.Name
    if (-not (Test-Path $dest)) { Copy-Item $xml.FullName $dest -ErrorAction SilentlyContinue }
}

if ($torchDir) {
    foreach ($dll in $torchDlls) {
        $src  = Join-Path $torchDir $dll
        $dest = Join-Path $scriptDir $dll
        if (Test-Path $dest) { $skipped++; continue }
        if (Test-Path $src)  { Copy-Item $src $dest; $copied++ }
        else                 { $missing += "Torch: $dll" }
    }
}

Write-Host ""
Write-Host "   Copied : $copied dll(s)" -ForegroundColor Green
if ($skipped -gt 0) {
    Write-Host "   Skipped: $skipped (already present)" -ForegroundColor Cyan
}
if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "   The following optional DLLs were not found:" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host "     - $_" -ForegroundColor Yellow }
    Write-Host "   The tool will still run but some features may be unavailable."
}

# â”€â”€ Done â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
Write-Host ""
Write-Host " â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€"
Write-Host "  Setup complete!  Run SESpriteLCDLayoutTool.exe"
Write-Host " â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€"
Write-Host ""
