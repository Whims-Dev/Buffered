<#
  Builds a self-contained copy of Buffered (no .NET install needed) into artifacts\Buffered and zips it.
  Usage:  powershell -File scripts\publish.ps1 [-FfmpegDir C:\path\to\ffmpeg\bin]
  If -FfmpegDir is given, ffmpeg.exe and ffprobe.exe (and the DLLs of a shared build) are copied into a tools folder so the zip works out of the box.
  Check the ffmpeg build's license before redistributing it.
#>
param([string]$FfmpegDir)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts\Buffered'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

foreach ($proj in 'Buffered.Recorder', 'Buffered.UI') {
    dotnet publish (Join-Path $root "src\$proj") -c Release -r win-x64 --self-contained true -o $out
    if ($LASTEXITCODE -ne 0) { throw "Publishing $proj failed." }
}

if ($FfmpegDir) {
    # ffmpeg goes in a "tools" folder (Buffered looks there). A shared build also brings its DLLs; a static one is just the two programs.
    $tools = Join-Path $out 'tools'
    New-Item -ItemType Directory -Force $tools | Out-Null
    foreach ($f in 'ffmpeg.exe', 'ffprobe.exe') { Copy-Item (Join-Path $FfmpegDir $f) $tools }
    Get-ChildItem $FfmpegDir -Filter *.dll -ErrorAction SilentlyContinue | Copy-Item -Destination $tools
    # ffmpeg builds are GPL: ship the license text and say where the source is.
    $lic = Join-Path (Split-Path $FfmpegDir -Parent) 'LICENSE.txt'
    if (Test-Path $lic) { Copy-Item $lic (Join-Path $out 'ffmpeg-LICENSE.txt') }
    @"
Buffered is MIT licensed (see the project's LICENSE).

The tools folder contains ffmpeg and ffprobe (with the DLLs they need), separate programs from the FFmpeg project (https://ffmpeg.org),
built by BtbN (https://github.com/BtbN/FFmpeg-Builds) under the GNU GPL. Their license is in ffmpeg-LICENSE.txt and
their source code is available from those two sites. Buffered only runs them as separate programs.
"@ | Set-Content (Join-Path $out 'THIRD-PARTY-NOTICES.txt') -Encoding utf8
}

$zip = Join-Path $root 'artifacts\Buffered-win-x64.zip'
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Host "Done: $zip"
