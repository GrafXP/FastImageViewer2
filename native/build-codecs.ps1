# Downloads and builds, as static libraries (x64, static CRT), the decoders for the formats the
# viewer does not leave to the Windows codecs, which are installed on some machines and not on others:
#   libheif + libde265 + dav1d   HEIF/HEIC (HEVC) and AVIF (AV1)
#   libwebp                      WebP, animated ones included
#   libjxl                       JPEG XL
#   LibRaw                       camera raw
#   libtiff + zlib               the TIFFs the Windows codec does not read (10-, 14-, 24-bit samples,
#                                64-bit float, LogLuv, uncompressed YCbCr) or reads wrongly (16-bit
#                                float, 32-bit and signed integers)
# Output: native\codecs\{include,lib}. Then native\build-fivcodecs.ps1 builds the shim the viewer calls.
# Needs: MSVC Build Tools 2022, CMake, NASM, Git and Python on PATH; run native\build-libjpeg-turbo.ps1 first.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sources = Join-Path $root '_src'
$builds = Join-Path $root '_build'
$prefix = Join-Path $root 'codecs'
$turbo = Join-Path $root 'libjpeg-turbo'
if (-not (Test-Path (Join-Path $turbo 'lib\jpeg-static.lib'))) { throw 'run native\build-libjpeg-turbo.ps1 first' }
New-Item -ItemType Directory -Force $sources, $builds, $prefix | Out-Null
$env:CL = '/MP'   # MSBuild runs one compiler per project; this makes each compile its files in parallel
# Only the libraries built here: not whatever a toolchain on PATH brings along (Strawberry Perl has a pkg-config and libraries).
$env:PKG_CONFIG_LIBDIR = Join-Path $prefix 'lib\pkgconfig'
$env:PKG_CONFIG_PATH = $env:PKG_CONFIG_LIBDIR

# A release tarball, checked against its pinned hash and unpacked into _src. Returns the source tree.
function Get-Source([string]$Name, [string]$Url, [string]$Sha256) {
    $tgz = Join-Path $sources "$Name.tar.gz"
    if (-not (Test-Path $tgz)) { Invoke-WebRequest $Url -OutFile $tgz }
    $hash = (Get-FileHash $tgz -Algorithm SHA256).Hash
    if ($hash -ne $Sha256) { throw "$Name.tar.gz: SHA256 $hash does not match the pinned $Sha256" }
    $tree = Join-Path $sources $Name
    if (-not (Test-Path $tree)) { tar -xzf $tgz -C $sources; if ($LASTEXITCODE) { throw "unpacking $Name failed" } }
    $tree
}

function Invoke-CMake([string]$Name, [string]$Source, [string[]]$Options) {
    $build = Join-Path $builds $Name
    # CMP0091 lets CMAKE_MSVC_RUNTIME_LIBRARY choose /MT also in projects that ask for an older CMake.
    cmake -S $Source -B $build -G 'Visual Studio 17 2022' -A x64 -Wno-dev `
        -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded `
        -DBUILD_SHARED_LIBS=OFF "-DCMAKE_INSTALL_PREFIX=$prefix" "-DCMAKE_PREFIX_PATH=$prefix" `
        -DCMAKE_FIND_USE_SYSTEM_ENVIRONMENT_PATH=OFF -DCMAKE_FIND_USE_CMAKE_SYSTEM_PATH=OFF @Options | Out-Null
    if ($LASTEXITCODE) { throw "$Name`: cmake configure failed" }
    cmake --build $build --config Release --parallel -- /nologo /v:m
    if ($LASTEXITCODE) { throw "$Name`: build failed" }
    cmake --install $build --config Release | Out-Null
    if ($LASTEXITCODE) { throw "$Name`: install failed" }
}

# ---------------------------------------------------------------- libde265 (HEVC)
$de265 = Get-Source 'libde265-1.1.3' 'https://github.com/strukturag/libde265/archive/refs/tags/v1.1.3.tar.gz' `
    '189BAA08FD6D2DD34DB099DE411BD6B2E6BD5EB88E81236A3D0FA3826B9715C4'
Invoke-CMake 'libde265' $de265 @('-DENABLE_SDL=OFF', '-DENABLE_DECODER=OFF', '-DENABLE_ENCODER=OFF')

# ---------------------------------------------------------------- dav1d (AV1), built with Meson
$dav1d = Get-Source 'dav1d-1.5.4' 'https://code.videolan.org/videolan/dav1d/-/archive/1.5.4/dav1d-1.5.4.tar.gz' `
    'A1D5B63D2D38EC9BD03ACF643CAA51FA22EDD1E89C5A109C4807717216BBEC07'
$venv = Join-Path $builds 'meson-venv'
if (-not (Test-Path (Join-Path $venv 'Scripts\meson.exe'))) {
    python -m venv $venv
    & (Join-Path $venv 'Scripts\python.exe') -m pip install --quiet --disable-pip-version-check meson==1.12.1 ninja==1.13.2
    if ($LASTEXITCODE) { throw 'installing Meson failed' }
}
$dav1dBuild = Join-Path $builds 'dav1d'
$env:PATH = (Join-Path $venv 'Scripts') + ';' + $env:PATH
$env:CC = 'cl'   # not the gcc some toolchains put on PATH
if (-not (Test-Path (Join-Path $dav1dBuild 'build.ninja'))) {
    meson setup $dav1dBuild $dav1d --vsenv --buildtype=release --default-library=static -Db_vscrt=mt `
        -Denable_tools=false -Denable_tests=false -Denable_examples=false -Denable_docs=false `
        "--prefix=$prefix" --libdir=lib | Out-Null
    if ($LASTEXITCODE) { throw 'dav1d: meson setup failed' }
}
meson install -C $dav1dBuild --quiet
if ($LASTEXITCODE) { throw 'dav1d: build failed' }
Remove-Item Env:CC
# Meson names a static library libdav1d.a even with MSVC.
Copy-Item (Join-Path $prefix 'lib\libdav1d.a') (Join-Path $prefix 'lib\dav1d.lib') -Force

# ---------------------------------------------------------------- libheif (HEIF container, HEIC and AVIF through the two above)
$heif = Get-Source 'libheif-1.23.6' 'https://github.com/strukturag/libheif/archive/refs/tags/v1.23.6.tar.gz' `
    'C9A00B0452F5C16D72EF71E5E40C0E90FBE2AD37148D27A1B2F9BB21546F5F03'
$off = 'X265', 'KVAZAAR', 'UVG266', 'VVDEC', 'VVENC', 'X264', 'OpenH264_DECODER', 'AOM_DECODER', 'AOM_ENCODER', 'SvtEnc',
    'RAV1E', 'JPEG_DECODER', 'JPEG_ENCODER', 'OpenJPEG_ENCODER', 'OpenJPEG_DECODER', 'FFMPEG_DECODER', 'OPENJPH_ENCODER',
    'LIBSHARPYUV', 'EXAMPLES', 'GDK_PIXBUF', 'HEADER_COMPRESSION' | ForEach-Object { "-DWITH_$_=OFF" }
Invoke-CMake 'libheif' $heif (@('-DWITH_LIBDE265=ON', '-DWITH_LIBDE265_PLUGIN=OFF', '-DWITH_DAV1D=ON', '-DWITH_DAV1D_PLUGIN=OFF',
    '-DENABLE_PLUGIN_LOADING=OFF', '-DBUILD_TESTING=OFF', '-DBUILD_DOCUMENTATION=OFF',
    '-DCMAKE_CXX_FLAGS=/DWIN32 /D_WINDOWS /EHsc /GR /DLIBDE265_STATIC_BUILD') + $off)

# ---------------------------------------------------------------- libwebp
$webp = Get-Source 'libwebp-1.6.0' 'https://github.com/webmproject/libwebp/archive/refs/tags/v1.6.0.tar.gz' `
    '93A852C2B3EFAFEE3723EFD4636DE855B46F9FE1EFDDD607E1F42F60FC8F2136'
Invoke-CMake 'libwebp' $webp ('ANIM_UTILS', 'CWEBP', 'DWEBP', 'GIF2WEBP', 'IMG2WEBP', 'VWEBP', 'WEBPINFO', 'WEBPMUX', 'EXTRAS',
    'LIBWEBPMUX' | ForEach-Object { "-DWEBP_BUILD_$_=OFF" })

# ---------------------------------------------------------------- libjxl, from Git: its dependencies are submodules
$jxlCommit = 'a7a9c787341cf703dede03c2009fa460cae5e5df'   # v0.12.0
$jxl = Join-Path $sources 'libjxl-0.12.0'
if (-not (Test-Path $jxl)) {
    git -c advice.detachedHead=false clone --quiet --depth 1 --branch v0.12.0 https://github.com/libjxl/libjxl.git $jxl
    if ($LASTEXITCODE) { throw 'libjxl: clone failed' }
    git -C $jxl submodule update --init --depth 1 --quiet third_party/highway third_party/brotli third_party/skcms
    if ($LASTEXITCODE) { throw 'libjxl: submodules failed' }
}
$head = git -C $jxl rev-parse HEAD
if ($head -ne $jxlCommit) { throw "libjxl: checked out $head, not the pinned $jxlCommit" }
Invoke-CMake 'libjxl' $jxl @('-DBUILD_TESTING=OFF', '-DJPEGXL_ENABLE_TOOLS=OFF', '-DJPEGXL_ENABLE_JPEGLI=OFF',
    '-DJPEGXL_ENABLE_DOXYGEN=OFF', '-DJPEGXL_ENABLE_MANPAGES=OFF', '-DJPEGXL_ENABLE_BENCHMARK=OFF',
    '-DJPEGXL_ENABLE_EXAMPLES=OFF', '-DJPEGXL_ENABLE_JNI=OFF', '-DJPEGXL_ENABLE_SJPEG=OFF', '-DJPEGXL_ENABLE_OPENEXR=OFF',
    '-DJPEGXL_ENABLE_TRANSCODE_JPEG=OFF', '-DJPEGXL_ENABLE_SKCMS=ON', '-DJPEGXL_BUNDLE_LIBPNG=OFF')

# ---------------------------------------------------------------- LibRaw
$raw = Get-Source 'LibRaw-0.22.2' 'https://github.com/LibRaw/LibRaw/archive/refs/tags/0.22.2.tar.gz' `
    '627928088300ECDE6CA91FFD202E189203F04AD61AD12F0FE9DC57B9A7A0FB3C'
Invoke-CMake 'libraw' (Join-Path $root 'fivcodecs\libraw') @("-DLIBRAW_SOURCE=$raw", "-DLIBJPEG_DIR=$turbo")

# ---------------------------------------------------------------- zlib and libtiff
$zlib = Get-Source 'zlib-1.3.2' 'https://github.com/madler/zlib/releases/download/v1.3.2/zlib-1.3.2.tar.gz' `
    'BB329A0A2CD0274D05519D61C667C062E06990D72E125EE2DFA8DE64F0119D16'
Invoke-CMake 'zlib' $zlib @('-DZLIB_BUILD_SHARED=OFF', '-DZLIB_BUILD_TESTING=OFF')
# The static library's name (zs.lib) is not one CMake's FindZLIB looks for, and libjpeg-turbo's own
# CMake package would bring its DLL: both are handed to libtiff by path.
# Forward slashes: they end up in the test projects libtiff's configure compiles, where a backslash escapes.
function ConvertTo-CMakePath([string]$Path) { $Path -replace '\\', '/' }
$zlibLib = (Get-ChildItem (Join-Path $prefix 'lib') -Filter 'z*.lib' | Select-Object -First 1).FullName
$tiff = Get-Source 'tiff-4.7.2' 'https://download.osgeo.org/libtiff/tiff-4.7.2.tar.gz' `
    '672BD7D10AEE4606171AFB864F3570B83340F6A33E2C186DC0512F7145FFDF6A'
Invoke-CMake 'libtiff' $tiff @('-Dtiff-tools=OFF', '-Dtiff-tests=OFF', '-Dtiff-contrib=OFF', '-Dtiff-docs=OFF', '-Dtiff-cxx=OFF',
    '-Dzlib=ON', "-DZLIB_LIBRARY=$(ConvertTo-CMakePath $zlibLib)", "-DZLIB_INCLUDE_DIR=$(ConvertTo-CMakePath (Join-Path $prefix 'include'))",
    '-Djpeg=ON', '-Djpeg-prefer-standard=ON', "-DJPEG_INCLUDE_DIR=$(ConvertTo-CMakePath (Join-Path $turbo 'include'))",
    "-DJPEG_LIBRARY=$(ConvertTo-CMakePath (Join-Path $turbo 'lib\jpeg-static.lib'))",
    '-Dlzma=OFF', '-Dzstd=OFF', '-Dwebp=OFF', '-Dlerc=OFF', '-Djbig=OFF', '-Dlibdeflate=OFF')

Get-ChildItem (Join-Path $prefix 'lib') -Filter *.lib | Select-Object Name, Length
