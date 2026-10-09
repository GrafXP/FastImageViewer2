# Downloads and builds libjpeg-turbo as a static library (x64, static CRT, SIMD via NASM).
# Output: native\libjpeg-turbo\{include,lib,bin}
# Needs: MSVC Build Tools 2022, CMake, NASM on PATH.
param(
    [string]$Version = '3.2.0',
    [string]$Sha256 = '980DD81F425082AA6D7C9E47FEF27554CE7A9FFC8E2F6E863B97D263C5C50858',
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root "_src\libjpeg-turbo-$Version"
$build = Join-Path $root "_build\libjpeg-turbo-$Version"
$prefix = Join-Path $root 'libjpeg-turbo'
$tgz = Join-Path $root "_src\libjpeg-turbo-$Version.tar.gz"

New-Item -ItemType Directory -Force (Join-Path $root '_src'), $build | Out-Null
if (-not (Test-Path $tgz)) {
    Invoke-WebRequest "https://github.com/libjpeg-turbo/libjpeg-turbo/archive/refs/tags/$Version.tar.gz" -OutFile $tgz
}
$hash = (Get-FileHash $tgz -Algorithm SHA256).Hash
if ($hash -ne $Sha256) { throw "libjpeg-turbo-$Version.tar.gz: SHA256 $hash does not match the pinned $Sha256" }
if (-not (Test-Path $src)) { tar -xzf $tgz -C (Join-Path $root '_src') }

# WITH_CRT_DLL=OFF -> /MT, which is what a Native AOT exe links against.
cmake -S $src -B $build -G 'Visual Studio 17 2022' -A x64 `
    -DENABLE_SHARED=ON -DENABLE_STATIC=ON -DWITH_CRT_DLL=OFF `
    -DREQUIRE_SIMD=ON -DWITH_TOOLS=ON "-DWITH_TESTS=$(if ($Test) { 'ON' } else { 'OFF' })" `
    "-DCMAKE_INSTALL_PREFIX=$prefix"
if ($LASTEXITCODE) { throw 'cmake configure failed' }
cmake --build $build --config Release --parallel
if ($LASTEXITCODE) { throw 'cmake build failed' }
if ($Test) {
    ctest --test-dir $build -C Release --output-on-failure -j $env:NUMBER_OF_PROCESSORS
    if ($LASTEXITCODE) { throw 'libjpeg-turbo regression tests failed' }
}
cmake --install $build --config Release
if ($LASTEXITCODE) { throw 'cmake install failed' }
