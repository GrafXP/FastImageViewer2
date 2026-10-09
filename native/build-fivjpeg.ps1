# Builds the cancellable JPEG decode shim (native\fivjpeg\fivjpeg.c):
#   native\fivjpeg\bin\fivjpeg-static.lib   linked into the Native AOT executables
#   native\fivjpeg\bin\fivjpeg.dll          loaded by JIT-run projects (tests, benchmarks)
# Run native\build-libjpeg-turbo.ps1 first.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root '_build\fivjpeg'
if (-not (Test-Path (Join-Path $root 'libjpeg-turbo\lib\turbojpeg-static.lib'))) { throw 'run native\build-libjpeg-turbo.ps1 first' }

cmake -S (Join-Path $root 'fivjpeg') -B $build -G 'Visual Studio 17 2022' -A x64 "-DCMAKE_INSTALL_PREFIX=$(Join-Path $root 'fivjpeg')" | Out-Null
if ($LASTEXITCODE) { throw 'cmake configure failed' }
cmake --build $build --config Release -- /nologo /v:m
if ($LASTEXITCODE) { throw 'build failed' }
cmake --install $build --config Release | Out-Null
if ($LASTEXITCODE) { throw 'install failed' }
Get-ChildItem (Join-Path $root 'fivjpeg\bin') | Select-Object Name, Length
