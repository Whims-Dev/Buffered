<#
  Runs, against a given ffmpeg folder, a copy of every kind of ffmpeg/ffprobe command Buffered issues, on a short test clip made by ffmpeg itself.
  Used by the "ffmpeg" workflow to check a slimmed-down build before it is published. Any failing command fails the script.
  Usage:  powershell -File scripts\ffmpeg-smoke.ps1 -Dir C:\path\to\ffmpeg\bin
#>
param([Parameter(Mandatory)][string]$Dir)

$ErrorActionPreference = 'Stop'
$ff = Join-Path $Dir 'ffmpeg.exe'
$fp = Join-Path $Dir 'ffprobe.exe'
$w = Join-Path ([IO.Path]::GetTempPath()) ('ffsmoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $w | Out-Null
$failed = @()

function Step([string]$name, [string]$exe, [string[]]$arguments, [string]$expect) {
    $out = & $exe @arguments 2>&1 | Out-String
    $ok = ($LASTEXITCODE -eq 0)
    if ($ok -and $expect) { $ok = Test-Path $expect -PathType Leaf; if ($ok) { $ok = (Get-Item $expect).Length -gt 0 } }
    if ($ok) { Write-Host "ok     $name" } else { Write-Host "FAILED $name"; Write-Host $out; $script:failed += $name }
}

$src = "$w\test.mp4"
# A test clip like the recorder's: 720p60 H.264 with a keyframe every 2 seconds, and AAC sound.
Step 'make a test clip' $ff @('-hide_banner','-loglevel','error','-y','-f','lavfi','-i','testsrc2=size=1280x720:rate=60','-f','lavfi','-i','sine=frequency=440:sample_rate=48000','-t','6','-c:v','libx264','-preset','ultrafast','-g','120','-pix_fmt','yuv420p','-c:a','aac','-b:a','160k','-movflags','+faststart',$src) $src

# The library: a thumbnail, durations, audio streams.
Step 'thumbnail (jpg)' $ff @('-hide_banner','-loglevel','error','-y','-ss','1','-i',$src,'-frames:v','1','-vf','scale=480:-2','-q:v','4',"$w\thumb.jpg") "$w\thumb.jpg"
Step 'ffprobe: duration' $fp @('-v','error','-show_entries','format=duration','-of','csv=p=0',$src) $null
Step 'ffprobe: audio streams' $fp @('-v','error','-select_streams','a','-show_entries','stream=index','-of','csv=p=0',$src) $null
Step 'ffprobe: first audio packet' $fp @('-v','error','-select_streams','a:0','-show_entries','packet=pts_time','-read_intervals','%+#1','-of','csv=p=0',$src) $null
Step 'ffprobe: frame rate' $fp @('-v','error','-select_streams','v:0','-show_entries','stream=avg_frame_rate','-of','csv=p=0',$src) $null
Step 'ffprobe: keyframes' $fp @('-v','error','-select_streams','v:0','-show_entries','packet=pts_time,flags','-of','csv=p=0',$src) $null

# The editor: waveform, filmstrip, a copy with frequent keyframes, a corrected copy.
Step 'waveform (raw sound)' $ff @('-hide_banner','-loglevel','error','-i',$src,'-map','0:a:0','-vn','-ac','1','-ar','4000','-f','s16le',"$w\peaks.raw") "$w\peaks.raw"
Step 'filmstrip' $ff @('-hide_banner','-loglevel','error','-y','-skip_frame','nokey','-i',$src,'-an','-vf','fps=4,tpad=stop=6:stop_mode=clone,scale=-2:68,tile=24x1','-frames:v','1','-q:v','5',"$w\strip.jpg") "$w\strip.jpg"
Step 'scrub copy (x264, copy sound)' $ff @('-hide_banner','-loglevel','error','-y','-i',$src,'-map','0:v:0','-map','0:a:0?','-c:v','libx264','-preset','ultrafast','-crf','20','-g','6','-keyint_min','6','-sc_threshold','0','-pix_fmt','yuv420p','-threads','2','-c:a','copy','-fps_mode','passthrough','-movflags','+faststart',"$w\scrub.mp4") "$w\scrub.mp4"
Step 'corrected copy (aac re-encode)' $ff @('-hide_banner','-loglevel','error','-y','-i',$src,'-map','0:v','-map','0:a:0','-c:v','copy','-c:a','aac','-b:a','160k','-af','atrim=start_sample=1024,asetpts=N/SR/TB','-use_editlist','0','-movflags','+faststart',"$w\fixed.mp4") "$w\fixed.mp4"

# Saving a clip: raw video and raw sound in, mixed and separate tracks out, a companion file.
Step 'raw video out' $ff @('-hide_banner','-loglevel','error','-y','-i',$src,'-an','-c:v','copy','-f','h264',"$w\raw.h264") "$w\raw.h264"
Step 'raw sound out' $ff @('-hide_banner','-loglevel','error','-y','-i',$src,'-vn','-ac','2','-ar','48000','-f','s16le',"$w\a.pcm") "$w\a.pcm"
$graph = '[1:a]volume=3.5dB[mx0];[2:a]volume=-6dB,asplit=2[mx1][st1];[mx0]asplit=2[mx0a][st0];[mx0a]aformat=channel_layouts=stereo[ms0];[mx1]aformat=channel_layouts=stereo[ms1];[ms0][ms1]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95,atrim=start_sample=1024,asetpts=N/SR/TB[mix];[st0]atrim=start_sample=1024,asetpts=N/SR/TB[spt0]'
Step 'save: video, mixed sound, companion file' $ff @('-hide_banner','-loglevel','error','-y','-f','h264','-framerate','60','-i',"$w\raw.h264",'-f','s16le','-ar','48000','-ac','2','-i',"$w\a.pcm",'-f','s16le','-ar','48000','-ac','2','-i',"$w\a.pcm",'-filter_complex',$graph,'-map','0:v','-map','[mix]','-c:v','copy','-c:a','aac','-b:a','160k','-movflags','+faststart',"$w\saved.mp4",'-map','[st0]','-map','[st1]','-vn','-c:a','aac','-b:a','128k','-metadata:s:a:0','title=Game','-f','matroska',"$w\stems.mka") "$w\stems.mka"

# The editor's trim and new volumes.
Step 'trim: copy' $ff @('-hide_banner','-loglevel','error','-y','-noaccurate_seek','-ss','1','-i',$src,'-t','3','-map','0','-map_metadata','0','-c:v','copy','-avoid_negative_ts','make_zero','-c:a','copy','-use_editlist','0','-movflags','+faststart',"$w\trim-copy.mp4") "$w\trim-copy.mp4"
Step 'trim: precise (x264)' $ff @('-hide_banner','-loglevel','error','-y','-ss','1','-i',$src,'-t','3','-map','0','-map_metadata','0','-c:v','libx264','-preset','veryfast','-crf','18','-g','120','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709','-c:a','aac','-b:a','160k','-af','atrim=start_sample=1024,asetpts=N/SR/TB','-use_editlist','0','-movflags','+faststart',"$w\trim-x264.mp4") "$w\trim-x264.mp4"
Step 'new volumes from the companion file' $ff @('-hide_banner','-loglevel','error','-y','-i',$src,'-i',"$w\stems.mka",'-filter_complex','[1:a:0]volume=-3dB,asplit=2[m0][s0];[1:a:1]volume=2dB,asplit=2[m1][s1];[m0]aformat=channel_layouts=stereo[ms0];[m1]aformat=channel_layouts=stereo[ms1];[ms0][ms1]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95,atrim=start_sample=1024,asetpts=N/SR/TB[mix]','-map','0:v','-map','[mix]','-c:v','copy','-c:a','aac','-b:a','160k','-use_editlist','0','-movflags','+faststart',"$w\balanced.mp4") "$w\balanced.mp4"

# Sharing: a smaller copy, with progress on a pipe.
Step 'share: smaller copy (x264, progress on a pipe)' $ff @('-hide_banner','-loglevel','error','-y','-progress','pipe:1','-nostats','-ss','0','-i',$src,'-t','4','-map','0:v:0','-map','0:a:0?','-vf',"scale=-2:'min(ih,360)',fps=30",'-c:v','libx264','-preset','veryfast','-b:v','800k','-maxrate','800k','-bufsize','1600k','-g','120','-pix_fmt','yuv420p','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709','-c:a','aac','-b:a','96k','-ac','2','-movflags','+faststart',"$w\share.mp4") "$w\share.mp4"

# The GPU encoder has to be in the build (it can only run on a PC with an NVIDIA card).
$enc = & $ff -hide_banner -encoders 2>&1 | Out-String
if ($enc -match 'h264_nvenc') { Write-Host 'ok     h264_nvenc is in the build' } else { Write-Host 'FAILED h264_nvenc is missing'; $failed += 'h264_nvenc' }

Remove-Item $w -Recurse -Force -ErrorAction SilentlyContinue
if ($failed.Count -gt 0) { throw "These checks failed: $($failed -join ', ')" }
Write-Host 'All checks passed.'
