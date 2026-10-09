# Publishes the viewer as a Native AOT executable: publish\FastImageViewer2.exe
# First time on a machine: run native\build-libjpeg-turbo.ps1 once.
param([switch]$Run, [string[]]$Arguments = @())
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

# The Native AOT targets locate the linker through vswhere, which is not on PATH by default.
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

dotnet publish (Join-Path $repo 'src\Fiv.App\Fiv.App.csproj') -c Release -nologo -v q -o (Join-Path $repo 'publish')
if ($LASTEXITCODE) { throw 'publish failed' }
$exe = Join-Path $repo 'publish\FastImageViewer2.exe'
Get-Item $exe | Select-Object Name, Length, LastWriteTime
if ($Run) { & $exe @Arguments }
