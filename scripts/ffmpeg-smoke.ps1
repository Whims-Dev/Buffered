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
    if ($ok) { Write-Host "ok     $name" }
    else {
        Write-Host "FAILED $name"; Write-Host $out
        # run it again with detail, so the reason is in the log
        $detail = @($arguments | ForEach-Object { if ($_ -eq 'error') { 'verbose' } else { $_ } })
        Write-Host '--- again, in detail:'
        (& $exe @detail 2>&1 | Out-String).Split("`n") | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
        $script:failed += $name
    }
}

Write-Host '--- what this build can do:'
& $ff -hide_banner -protocols 2>&1 | Out-String | Write-Host
& $ff -hide_banner -formats 2>&1 | Out-String | Select-String -Pattern ' mp4| matroska| image2| s16le| h264' | ForEach-Object { Write-Host $_.Line }
& $ff -hide_banner -version 2>&1 | Select-Object -First 3 | ForEach-Object { Write-Host $_ }
Write-Host '---'

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
$graph = '[1:a]volume=3.5dB[mx0];[2:a]volume=-6dB,asplit=2[mx1][st1];[mx0]asplit=2[mx0a][st0];[mx0a]aformat=channel_layouts=stereo[ms0];[mx1]aformat=channel_layouts=stereo[ms1];[ms0][ms1]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95,atrim=start_sample=1024,asetpts=N/SR/TB[mix]'
Step 'save: video, mixed sound, companion file' $ff @('-hide_banner','-loglevel','error','-y','-f','h264','-framerate','60','-i',"$w\raw.h264",'-f','s16le','-ar','48000','-ac','2','-i',"$w\a.pcm",'-f','s16le','-ar','48000','-ac','2','-i',"$w\a.pcm",'-filter_complex',$graph,'-map','0:v','-map','[mix]','-c:v','copy','-c:a','aac','-b:a','160k','-movflags','+faststart',"$w\saved.mp4",'-map','[st0]','-map','[st1]','-vn','-c:a','aac','-b:a','128k','-metadata:s:a:0','title=Game','-f','matroska',"$w\stems.mka") "$w\stems.mka"

# The editor's trim and new volumes.
Step 'trim: copy' $ff @('-hide_banner','-loglevel','error','-y','-noaccurate_seek','-ss','1','-i',$src,'-t','3','-map','0','-map_metadata','0','-c:v','copy','-avoid_negative_ts','make_zero','-c:a','copy','-use_editlist','0','-movflags','+faststart',"$w\trim-copy.mp4") "$w\trim-copy.mp4"
Step 'trim: precise (x264)' $ff @('-hide_banner','-loglevel','error','-y','-ss','1','-i',$src,'-t','3','-map','0','-map_metadata','0','-c:v','libx264','-preset','veryfast','-crf','18','-g','120','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709','-c:a','aac','-b:a','160k','-af','atrim=start_sample=1024,asetpts=N/SR/TB','-use_editlist','0','-movflags','+faststart',"$w\trim-x264.mp4") "$w\trim-x264.mp4"
Step 'new volumes from the companion file' $ff @('-hide_banner','-loglevel','error','-y','-i',$src,'-i',"$w\stems.mka",'-filter_complex','[1:a:0]volume=-3dB,asplit=2[m0][s0];[1:a:1]volume=2dB,asplit=2[m1][s1];[m0]aformat=channel_layouts=stereo[ms0];[m1]aformat=channel_layouts=stereo[ms1];[ms0][ms1]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95,atrim=start_sample=1024,asetpts=N/SR/TB[mix];[s0]anullsink;[s1]anullsink','-map','0:v','-map','[mix]','-c:v','copy','-c:a','aac','-b:a','160k','-use_editlist','0','-movflags','+faststart',"$w\balanced.mp4") "$w\balanced.mp4"

# Sharing: a smaller copy, with progress on a pipe.
Step 'share: smaller copy (x264, progress on a pipe)' $ff @('-hide_banner','-loglevel','error','-y','-progress','pipe:1','-nostats','-ss','0','-i',$src,'-t','4','-map','0:v:0','-map','0:a:0?','-vf',"scale=-2:'min(ih,360)',fps=30",'-c:v','libx264','-preset','veryfast','-b:v','800k','-maxrate','800k','-bufsize','1600k','-g','120','-pix_fmt','yuv420p','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709','-c:a','aac','-b:a','96k','-ac','2','-movflags','+faststart',"$w\share.mp4") "$w\share.mp4"

# The advanced editor's export: a picture laid over a black frame with a fade and opacity, a picture file looped, and sound delayed, faded and mixed.
$exportGraph = "[1:v]setpts=(PTS-STARTPTS)/1,fps=30,scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2:black,format=yuva420p,fade=t=in:st=0:d=0.5:alpha=1,colorchannelmixer=aa=0.8,setpts=PTS+0.5/TB[v0];[0:v][v0]overlay=eof_action=pass:format=auto[o1];[2:v]format=rgba,fade=t=out:st=1:d=0.5:alpha=1,setpts=PTS-STARTPTS+1/TB[v1];[o1][v1]overlay=x=10:y=10:eof_action=pass:format=auto,format=yuv420p[vout];[3:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,atempo=1.25,volume=0.5,afade=t=in:st=0:d=0.5,adelay=500|500[a0];[a0]alimiter=limit=0.95,apad=whole_dur=3[aout]"
Step 'export: overlay, fades, loop a picture, mix sound' $ff @('-hide_banner','-loglevel','error','-y','-f','lavfi','-i','color=c=black:s=320x180:r=30:d=3','-ss','0','-t','2','-i',$src,'-loop','1','-framerate','30','-t','2','-i',"$w\strip.jpg",'-f','s16le','-ar','48000','-ac','2','-ss','0','-t','2','-i',"$w\a.pcm",'-filter_complex',$exportGraph,'-map','[vout]','-map','[aout]','-c:v','libx264','-preset','veryfast','-crf','23','-pix_fmt','yuv420p','-c:a','aac','-t','3','-movflags','+faststart',"$w\export.mp4") "$w\export.mp4"

# Animated export: values that change over time (volume by an expression, opacity by commands sent to a filter as the frames go by, a picture moved and scaled by expressions).
$animGraph = "[1:v]setpts=PTS-STARTPTS,fps=30,scale=320:180:force_original_aspect_ratio=decrease,scale=w='trunc(iw*if(lt(t,1),0.5,1.2)/2)*2':h='trunc(ih*if(lt(t,1),0.5,1.2)/2)*2':eval=frame,format=yuva420p,sendcmd=c='0 colorchannelmixer@opv0 aa 0.3;1 colorchannelmixer@opv0 aa 0.9;',colorchannelmixer@opv0=aa=0.3,setpts=PTS+0.2/TB[v0];[0:v][v0]overlay=x='if(lt(t,1),50,150)-overlay_w/2':y=90-overlay_h/2:eof_action=pass:format=auto,format=yuv420p[vout];[2:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=volume='if(lt(t,1),0.2,1.4)':eval=frame,alimiter=limit=0.95,apad=whole_dur=3[aout]"
Step 'export: animated position, zoom, opacity and volume' $ff @('-hide_banner','-loglevel','error','-y','-f','lavfi','-i','color=c=black:s=320x180:r=30:d=3','-ss','0','-t','2','-i',$src,'-f','s16le','-ar','48000','-ac','2','-ss','0','-t','2','-i',"$w\a.pcm",'-filter_complex',$animGraph,'-map','[vout]','-map','[aout]','-c:v','libx264','-preset','veryfast','-crf','23','-pix_fmt','yuv420p','-c:a','aac','-t','3','-movflags','+faststart',"$w\anim.mp4") "$w\anim.mp4"

# Pictures with transparency (text drawn by the editor, pictures and GIF frames are PNG files), and the filters for turning, cropping and cross-dissolving.
$png = Join-Path $PSScriptRoot 'smoke.png'
Step 'png: read a picture with transparency' $ff @('-hide_banner','-loglevel','error','-y','-loop','1','-framerate','30','-t','1','-i',$png,'-vf','format=rgba,scale=64:-2','-c:v','libx264','-pix_fmt','yuv420p',"$w\png.mp4") "$w\png.mp4"
$moveGraph = "[0:v]trim=duration=2,setpts=PTS-STARTPTS,scale=320:180,format=yuva420p,rotate=a=0.3:c=none:ow=rotw(0.3):oh=roth(0.3),crop=iw*0.8:ih*0.8,hflip,vflip[a];[1:v]trim=duration=2,setpts=PTS-STARTPTS,scale=320:180,format=yuva420p,fps=30[b];[a]scale=320:180,fps=30[a2];[a2][b]xfade=transition=fade:duration=1:offset=1,format=yuv420p[vout]"
Step 'rotate, crop, flip and cross-dissolve' $ff @('-hide_banner','-loglevel','error','-y','-i',$src,'-i',$src,'-filter_complex',$moveGraph,'-map','[vout]','-c:v','libx264','-pix_fmt','yuv420p','-t','3',"$w\move.mp4") "$w\move.mp4"
Step 'loudness normalize' $ff @('-hide_banner','-loglevel','error','-y','-i',$src,'-vn','-af','loudnorm=I=-14:TP=-1.5:LRA=11,aresample=48000','-t','3','-f','s16le',"$w\loud.pcm") "$w\loud.pcm"

# The GPU encoder has to be in the build (it can only run on a PC with an NVIDIA card).
$enc = & $ff -hide_banner -encoders 2>&1 | Out-String
if ($enc -match 'h264_nvenc') { Write-Host 'ok     h264_nvenc is in the build' } else { Write-Host 'FAILED h264_nvenc is missing'; $failed += 'h264_nvenc' }

Remove-Item $w -Recurse -Force -ErrorAction SilentlyContinue
if ($failed.Count -gt 0) { throw "These checks failed: $($failed -join ', ')" }
Write-Host 'All checks passed.'
