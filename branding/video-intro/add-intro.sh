#!/bin/zsh
# Put the SentinelCore logo intro in front of a video.
#   add-intro.sh <video> [captions.srt]
# Writes <video>-with-intro.mp4 (and a shifted .srt) next to the input. The video is
# re-encoded to the intro's format (1280x720, 29.97 fps, H.264 + AAC 48k stereo), so any source works.
set -e
H=${0:a:h}
IN=${1:a}
SRT=${2:+${2:a}}
OUT=${IN:r}-with-intro.mp4
TMP=$(mktemp -d)
INTRO_LEN=7.2072  # 216 frames at 29.97 fps

ffmpeg -v error -stats -y -i $IN -map 0:v:0 -map 0:a:0 \
  -vf "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=30000/1001,format=yuv420p" \
  -c:v libx264 -preset medium -crf 18 -c:a aac -b:a 192k -ar 48000 -ac 2 $TMP/body.mp4
printf "file '%s'\nfile '%s'\n" $H/sentinelcore-intro.mp4 $TMP/body.mp4 > $TMP/concat.txt
ffmpeg -v error -y -f concat -safe 0 -i $TMP/concat.txt -c copy -movflags +faststart $TMP/joined.mp4

if [[ -n $SRT ]]; then
  ffmpeg -v error -y -itsoffset $INTRO_LEN -i $SRT -c:s srt ${OUT:r}.srt
  ffmpeg -v error -y -i $TMP/joined.mp4 -i ${OUT:r}.srt -map 0 -map 1 -c copy -c:s mov_text \
    -metadata:s:s:0 language=eng -disposition:s:0 0 -movflags +faststart $OUT
else
  mv $TMP/joined.mp4 $OUT
fi
rm -rf $TMP
echo "wrote $OUT"
