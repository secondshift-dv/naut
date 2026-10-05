#!/usr/bin/env bash
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive TZ=UTC LC_ALL=C
cd /recipe
snapshot=$(python3 -c 'import json; print(json.load(open("ffmpeg-build-inputs.json"))["debianSnapshot"])' 2>/dev/null || echo 20261003T000000Z)
rm -f /etc/apt/sources.list.d/debian.sources
printf 'deb [check-valid-until=no] https://snapshot.debian.org/archive/debian/%s/ bookworm main\ndeb-src [check-valid-until=no] https://snapshot.debian.org/archive/debian/%s/ bookworm main\n' "$snapshot" "$snapshot" > /etc/apt/sources.list
apt-get -o Acquire::https::CaInfo=/bootstrap-ca.crt -o Acquire::Retries=3 update
apt-get -o Acquire::https::CaInfo=/bootstrap-ca.crt install -y --no-install-recommends ca-certificates python3 make gcc g++ gcc-mingw-w64-x86-64 g++-mingw-w64-x86-64 pkg-config nasm meson ninja-build dpkg-dev xz-utils bzip2 patch
python3 /recipe/ffmpeg-build.py prepare /build /out
export SOURCE_DATE_EPOCH=1790985600
export CC=x86_64-w64-mingw32-gcc-posix CXX=x86_64-w64-mingw32-g++-posix
export AR=x86_64-w64-mingw32-ar RANLIB=x86_64-w64-mingw32-ranlib
export PKG_CONFIG_LIBDIR=/opt/naut/lib/pkgconfig PKG_CONFIG_PATH=
export CFLAGS='-O2 -static-libgcc -fstack-protector-strong -ffile-prefix-map=/build=.'
export CXXFLAGS='-O2 -static-libgcc -static-libstdc++ -fstack-protector-strong -ffile-prefix-map=/build=.'
export LDFLAGS='-static -static-libgcc -static-libstdc++ -Wl,--no-insert-timestamp'
mkdir -p /opt/naut /out/evidence /out/licenses /out/source/debian
cd /build/openh264
make -j2 OS=mingw_nt ARCH=x86_64 PREFIX=/opt/naut INCLUDE_PREFIX=/opt/naut/include/wels LIBDIR_NAME=lib BUILDTYPE=Release DEBUGSYMBOLS=False CC="$CC" CXX="$CXX" AR="$AR" install-static
cd /build/zlib
./configure --static --prefix=/opt/naut
make -j2
make install
cat > /build/cross.meson <<'EOF'
[binaries]
c = 'x86_64-w64-mingw32-gcc-posix'
cpp = 'x86_64-w64-mingw32-g++-posix'
ar = 'x86_64-w64-mingw32-ar'
strip = 'x86_64-w64-mingw32-strip'
windres = 'x86_64-w64-mingw32-windres'
pkgconfig = 'pkg-config'
[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
EOF
meson setup /build/dav1d/build /build/dav1d --cross-file=/build/cross.meson --prefix=/opt/naut --libdir=lib --buildtype=release --default-library=static -Denable_tools=false -Denable_tests=false
ninja -C /build/dav1d/build -j2
ninja -C /build/dav1d/build install
cd /out/source/debian
apt-get source --download-only gcc-mingw-w64 gcc-12 mingw-w64
dpkg-query -W > /out/evidence/debian-packages.tsv
"$CC" -v > /out/evidence/compiler.txt 2>&1
for pkg in gcc-mingw-w64-x86-64-posix gcc-mingw-w64-base g++-mingw-w64-x86-64-posix mingw-w64-x86-64-dev mingw-w64-common gcc-12-base; do
    cp "/usr/share/doc/$pkg/copyright" "/out/licenses/Debian-$pkg-copyright.txt"
done
cd /build/ffmpeg
for pass in 1 2; do
    if [[ "$pass" == 2 ]]; then make distclean; fi
    ./configure --prefix=/build/prefix --enable-cross-compile --cross-prefix=x86_64-w64-mingw32- --cc="$CC" --cxx="$CXX" --ar="$AR" --ranlib="$RANLIB" --arch=x86_64 --target-os=mingw32 --pkg-config=pkg-config --pkg-config-flags=--static --disable-autodetect --enable-static --disable-shared --enable-version3 --disable-debug --disable-doc --disable-ffplay --disable-chromaprint --disable-gpl --disable-nonfree --enable-libopenh264 --enable-zlib --enable-libdav1d --extra-version=naut-330caae0c1 --extra-cflags='-I/opt/naut/include -ffile-prefix-map=/build=.' --extra-ldflags='-L/opt/naut/lib -static -static-libgcc -static-libstdc++ -fstack-protector-strong -Wl,--no-insert-timestamp' --extra-ldexeflags='-Wl,-Map,/build/link.map' || { cat ffbuild/config.log; exit 1; }
    make -j2 V=1 ffmpeg.exe > "/out/evidence/ffmpeg-build-$pass.log" 2>&1 || { tail -60 "/out/evidence/ffmpeg-build-$pass.log"; exit 1; }
    cp /build/link.map "/out/evidence/ffmpeg-$pass.map"
    make -j2 V=1 ffprobe.exe > "/out/evidence/ffprobe-build-$pass.log" 2>&1 || { tail -60 "/out/evidence/ffprobe-build-$pass.log"; exit 1; }
    cp /build/link.map "/out/evidence/ffprobe-$pass.map"
    mkdir -p "/out/pass-$pass"
    cp ffmpeg.exe ffprobe.exe "/out/pass-$pass/"
    cp ffbuild/config.mak "/out/evidence/config-$pass.mak"
    cp ffbuild/config.log "/out/evidence/config-$pass.log"
done
cmp /out/pass-1/ffmpeg.exe /out/pass-2/ffmpeg.exe
cmp /out/pass-1/ffprobe.exe /out/pass-2/ffprobe.exe
x86_64-w64-mingw32-objdump -p /out/pass-1/ffmpeg.exe > /out/evidence/ffmpeg-pe.txt
x86_64-w64-mingw32-objdump -p /out/pass-1/ffprobe.exe > /out/evidence/ffprobe-pe.txt
python3 /recipe/ffmpeg-build.py finish /build /out
