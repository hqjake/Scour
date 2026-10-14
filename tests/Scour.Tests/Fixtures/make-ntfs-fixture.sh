#!/usr/bin/env bash
# Builds ntfs.img.gz + ntfs-expected.tsv used by MftScannerTests. Needs mkntfs and ntfs-3g (FUSE).
set -eu
out="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'fusermount -u "$work/mnt" 2>/dev/null || umount "$work/mnt" 2>/dev/null || true; rm -rf "$work"' EXIT

truncate -s 48M "$work/vol.img"
mkntfs -q -F -f -L Fixture "$work/vol.img"
mkdir "$work/mnt"
ntfs-3g "$work/vol.img" "$work/mnt"
cd "$work/mnt"

fill() { yes "$3" 2>/dev/null | head -c "$2" > "$1" || true; }   # compressible content of an exact size

mkdir -p Documents/Notes Games/Big/assets Photos "Música" Empty
fill Documents/report.docx 123456 report
fill Documents/Notes/todo.txt 300 todo          # small enough to stay resident in the MFT
: > Documents/Notes/empty.txt
fill "Documents/A very long file name with spaces.txt" 5000 long
fill Games/Big/data.bin 5242880 data
fill Games/Big/assets/a.pak 2097152 pak
fill "Música/canción ñ.mp3" 777777 mp3
for i in $(seq -w 1 400); do fill "Photos/img_$i.jpg" $(( 1000 + 10#$i * 7 )) "p$i"; done
truncate -s 10M sparse.vhd                     # sparse: 10 MB logical, nothing allocated
# Interleaved appends fragment both files into many runs.
for i in $(seq 1 60); do fill t1 65536 a; cat t1 >> frag1.bin; fill t2 65536 b; cat t2 >> frag2.bin; done

rm -f t1 t2
find . -mindepth 1 \( -type f -printf 'F\t%P\t%s\n' \) -o \( -type d -printf 'D\t%P\t0\n' \) | sort > "$out/ntfs-expected.tsv"
cd /
sync
fusermount -u "$work/mnt" 2>/dev/null || umount "$work/mnt"
gzip -9 -c "$work/vol.img" > "$out/ntfs.img.gz"
ls -la "$out"
