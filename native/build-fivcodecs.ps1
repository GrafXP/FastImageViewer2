# Builds the decoder shim (native\fivcodecs\fivcodecs.cpp) as native\fivcodecs\bin\fivcodecs.dll,
# with libheif, libde265, dav1d, libwebp, libjxl and LibRaw linked in, and puts the OpenMP
# runtime LibRaw uses (vcomp140.dll, redistributable, from the Build Tools) next to it.
# Run native\build-libjpeg-turbo.ps1 and native\build-codecs.ps1 first.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root '_build\fivcodecs'
if (-not (Test-Path (Join-Path $root 'codecs\lib\heif.lib'))) { throw 'run native\build-codecs.ps1 first' }

cmake -S (Join-Path $root 'fivcodecs') -B $build -G 'Visual Studio 17 2022' -A x64 "-DCMAKE_INSTALL_PREFIX=$(Join-Path $root 'fivcodecs')" | Out-Null
if ($LASTEXITCODE) { throw 'cmake configure failed' }
cmake --build $build --config Release -- /nologo /v:m
if ($LASTEXITCODE) { throw 'build failed' }
cmake --install $build --config Release | Out-Null
if ($LASTEXITCODE) { throw 'install failed' }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$openmp = Get-ChildItem (Join-Path $vs 'VC\Redist\MSVC\*\x64\Microsoft.VC*.OpenMP\vcomp140.dll') | Sort-Object FullName | Select-Object -Last 1
if (-not $openmp) { throw 'vcomp140.dll not found in the Visual C++ redistributable folder of the Build Tools' }
Copy-Item $openmp.FullName (Join-Path $root 'fivcodecs\bin') -Force
Get-ChildItem (Join-Path $root 'fivcodecs\bin') | Select-Object Name, Length
