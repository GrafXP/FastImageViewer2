# Downloads the sample files listed in format-samples.txt into corpus\formats: every format the viewer
# opens, at the bit depths and in the variants that tend to break decoders. About 120 files, 270 MB,
# most of it the camera raws. Files already there are checked against their hash, not downloaded again.
# -Missing lists the entries without a hash yet, with the hash of what was downloaded.
param([switch]$Missing)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$target = Join-Path $repo 'corpus\formats'
$failed = 0
foreach ($line in Get-Content (Join-Path $PSScriptRoot 'format-samples.txt')) {
    if ($line -match '^\s*(#|$)') { continue }
    $path, $url, $sha256, $what = $line -split '\s+', 4
    $file = Join-Path $target $path
    if (-not (Test-Path $file)) {
        New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
        try {
            Invoke-WebRequest $url -OutFile $file -UseBasicParsing
        }
        catch {
            Write-Warning "$path`: $($_.Exception.Message)"
            $failed++
            continue
        }
    }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sha256 -eq '-') {
        if ($Missing) { "$path $hash" }
    }
    elseif ($hash -ne $sha256) {
        Write-Warning "$path`: SHA256 $hash does not match the listed $sha256"
        Remove-Item $file
        $failed++
    }
}
if ($failed) { throw "$failed sample(s) could not be fetched" }
$files = Get-ChildItem $target -Recurse -File
'{0} files, {1:N0} MB in {2}' -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $target
