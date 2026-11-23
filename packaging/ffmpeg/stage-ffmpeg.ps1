<#
.SYNOPSIS
    Fetch, verify and unpack the vendored LGPL FFmpeg (P4.9). PowerShell twin of stage-ffmpeg.sh.

.DESCRIPTION
    Zip source, first match wins:
      1. $env:PIXELWIZARD_FFMPEG_ZIP - a zip file, or a directory holding the asset named in manifest.json.
      2. The GitHub release tagged manifest.releaseTag, from $env:PIXELWIZARD_FFMPEG_BASE_URL, else from
         this clone's "origin" remote. That release may not exist yet; the script then fails.
    The zip's SHA-256 must equal manifest.json assets.<Platform>.sha256 BEFORE anything is unpacked.

.PARAMETER Platform
    A key of manifest.json "assets" (default windows-x64).

.PARAMETER Dest
    Directory to create; receives the DLLs and licenses\. The app finds it as <exe dir>\ffmpeg.
#>
param(
    [string]$Platform = "windows-x64",
    [Parameter(Mandatory = $true)][string]$Dest
)
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Manifest  = Get-Content (Join-Path $ScriptDir "manifest.json") -Raw | ConvertFrom-Json
$Asset     = $Manifest.assets.$Platform
if (-not $Asset) { throw "stage-ffmpeg: no asset '$Platform' in manifest.json" }
$WantSha = $Asset.sha256.ToLowerInvariant()

$Work = Join-Path ([System.IO.Path]::GetTempPath()) ("pwc-ff-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $Work | Out-Null
try {
    if ($env:PIXELWIZARD_FFMPEG_ZIP) {
        $Zip = $env:PIXELWIZARD_FFMPEG_ZIP
        if (Test-Path $Zip -PathType Container) { $Zip = Join-Path $Zip $Asset.assetName }
        if (-not (Test-Path $Zip -PathType Leaf)) { throw "stage-ffmpeg: PIXELWIZARD_FFMPEG_ZIP does not point at $($Asset.assetName): $Zip" }
        Write-Host "stage-ffmpeg: using local zip $Zip"
    } else {
        $Base = $env:PIXELWIZARD_FFMPEG_BASE_URL
        if (-not $Base) {
            $remote = (& git -C $ScriptDir remote get-url origin 2>$null)
            $slug = ($remote -replace '^(git@github\.com:|https://github\.com/)', '') -replace '\.git$', ''
            if ($slug -match '^[^/]+/[^/]+$') { $Base = "https://github.com/$slug/releases/download/$($Manifest.releaseTag)" }
        }
        if (-not $Base) { throw "stage-ffmpeg: cannot work out the release URL; set PIXELWIZARD_FFMPEG_ZIP or PIXELWIZARD_FFMPEG_BASE_URL" }
        $Zip = Join-Path $Work $Asset.assetName
        Write-Host "stage-ffmpeg: downloading $Base/$($Asset.assetName)"
        try { Invoke-WebRequest -Uri "$Base/$($Asset.assetName)" -OutFile $Zip -UseBasicParsing }
        catch {
            throw ("stage-ffmpeg: ERROR: could not download $($Asset.assetName) from release '$($Manifest.releaseTag)'. The release may not be published yet. " +
                   "Pass the zip with PIXELWIZARD_FFMPEG_ZIP=<file or dir>, or build without FFmpeg (JPEG-only) with -NoFfmpeg. ($($_.Exception.Message))")
        }
    }

    $GotSha = (Get-FileHash -Algorithm SHA256 $Zip).Hash.ToLowerInvariant()
    if ($GotSha -ne $WantSha) {
        throw "stage-ffmpeg: ERROR: SHA-256 mismatch for $($Asset.assetName): expected $WantSha (manifest.json), actual $GotSha"
    }
    Write-Host "stage-ffmpeg: SHA-256 verified"

    if (Test-Path $Dest) { Remove-Item -Recurse -Force $Dest }
    New-Item -ItemType Directory -Force -Path $Dest | Out-Null
    Expand-Archive -Path $Zip -DestinationPath $Dest -Force

    foreach ($lib in $Asset.libraries) {
        if (-not (Test-Path (Join-Path $Dest $lib))) { throw "stage-ffmpeg: ERROR: $lib missing from $($Asset.assetName)" }
    }
    if (-not (Test-Path (Join-Path $Dest "licenses\COPYING.LGPLv2.1"))) { throw "stage-ffmpeg: ERROR: licenses\COPYING.LGPLv2.1 missing from $($Asset.assetName)" }
    Write-Host "stage-ffmpeg: staged $Platform into $Dest"
}
finally { Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue }
