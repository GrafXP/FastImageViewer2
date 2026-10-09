# Builds both bake-off programs:
#   cs\publish\bakeoff_cs.exe          C# Native AOT
#   cpp\obj\Release\bakeoff_cpp.exe    C++ (MSVC, /O2, LTCG, static CRT)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Resolve-Path (Join-Path $here '..\..')

# The Native AOT targets locate the linker through vswhere, which is not on PATH by default.
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

& (Join-Path $repo 'shaders\build.ps1')

dotnet publish (Join-Path $here 'cs\Bakeoff.csproj') -c Release -nologo -v q -o (Join-Path $here 'cs\publish')
if ($LASTEXITCODE) { throw 'C# publish failed' }

cmake -S (Join-Path $here 'cpp') -B (Join-Path $here 'cpp\obj') -G 'Visual Studio 17 2022' -A x64 | Out-Null
if ($LASTEXITCODE) { throw 'cmake configure failed' }
cmake --build (Join-Path $here 'cpp\obj') --config Release -- /nologo /v:q
if ($LASTEXITCODE) { throw 'C++ build failed' }

Get-Item (Join-Path $here 'cs\publish\bakeoff_cs.exe'), (Join-Path $here 'cpp\obj\Release\bakeoff_cpp.exe') |
    Select-Object Name, Length, LastWriteTime
