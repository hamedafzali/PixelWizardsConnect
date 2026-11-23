<#
.SYNOPSIS
    Build a self-contained, single-file Windows publish of PixelWizard Connect.

.DESCRIPTION
    Publishes the Avalonia client for win-x64 (self-contained, single-file) and
    copies the result to dist\PixelWizardConnect-win-x64\. The exe icon is wired
    via <ApplicationIcon> in the csproj (Assets\app.ico), so no extra step here.

.PARAMETER OutDir
    Output directory for the published folder. Default: <repo>\dist

.PARAMETER NoFfmpeg
    Build without the vendored FFmpeg (the app then runs JPEG-only). By default FFmpeg is required
    and staged as <exe dir>\ffmpeg, where the app looks for it. The zip comes from
    $env:PIXELWIZARD_FFMPEG_ZIP (file or dir), else the release named in packaging\ffmpeg\manifest.json;
    its SHA-256 is verified before unpacking. installer.iss ships the folder with the exe.

.EXAMPLE
    .\packaging\windows\build-windows.ps1
    .\packaging\windows\build-windows.ps1 -OutDir C:\out
#>
param(
    [string]$OutDir,
    [switch]$NoFfmpeg
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = (Resolve-Path (Join-Path $ScriptDir "..\..")).Path

if (-not $OutDir) { $OutDir = Join-Path $RepoRoot "dist" }

$Project   = Join-Path $RepoRoot "avalonia\PixelWizard.AvaloniaClient"
$Staging   = Join-Path ([System.IO.Path]::GetTempPath()) ("pwc-win-" + [System.Guid]::NewGuid().ToString("N"))
$DestDir   = Join-Path $OutDir "PixelWizardConnect-win-x64"

# Fail before the slow publish if FFmpeg cannot be obtained or does not verify.
$FfmpegStage = $null
if (-not $NoFfmpeg) {
    $FfmpegStage = Join-Path ([System.IO.Path]::GetTempPath()) ("pwc-ffmpeg-" + [System.Guid]::NewGuid().ToString("N"))
    & (Join-Path $RepoRoot "packaging\ffmpeg\stage-ffmpeg.ps1") -Platform windows-x64 -Dest $FfmpegStage
} else {
    Write-Warning "Building without FFmpeg; the app will use JPEG only."
}

Write-Host "Publishing win-x64 (self-contained, single-file)..."
dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $Staging

Write-Host "Copying to $DestDir ..."
if (Test-Path $DestDir) { Remove-Item -Recurse -Force $DestDir }
New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
Copy-Item -Recurse -Force (Join-Path $Staging "*") $DestDir

Remove-Item -Recurse -Force $Staging

# Vendored FFmpeg: DLLs (replaceable, LGPL) plus licenses\, next to the exe.
if ($FfmpegStage) {
    Copy-Item -Recurse -Force $FfmpegStage (Join-Path $DestDir "ffmpeg")
    Remove-Item -Recurse -Force $FfmpegStage
}

Write-Host "Built: $DestDir"

# ──────────────────────────────────────────────────────────────────────────────
# TODO: CODE SIGNING (required for distribution; needs a Windows code-signing
#       certificate). Not done here.
#
#   signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
#       /a "$DestDir\PixelWizard.AvaloniaClient.exe"
# ──────────────────────────────────────────────────────────────────────────────

# ──────────────────────────────────────────────────────────────────────────────
# OPTIONAL: build a Windows installer with Inno Setup after this script runs.
#   See packaging\windows\installer.iss. Compile it on Windows with:
#       iscc packaging\windows\installer.iss
#   (Install Inno Setup from https://jrsoftware.org/isinfo.php to get iscc.)
# ──────────────────────────────────────────────────────────────────────────────
