# Starts the viewer, optionally sends input, saves screenshots of its window, closes it.
#   -Steps: list of actions run in order:
#       "wait:500"            sleep ms
#       "shot:name"           screenshot to <OutDir>\name.jpg
#       "key:0x70"            WM_KEYDOWN with that virtual key
#       "wheel:120:800:450"   WM_MOUSEWHEEL delta at client x,y
#       "drag:x0:y0:x1:y1"    left-button drag in client coordinates
#       "move:x:y"            mouse move in client coordinates
#       "dbl:x:y"             double click
#       "ps:<command>"        run a PowerShell command (e.g. change files the viewer is watching)
param([string]$Exe, [string[]]$AppArgs, [string[]]$Steps, [string]$OutDir, [int]$Scale = 2)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class W {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern bool SetProcessDPIAware();
    [DllImport("user32")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    public static IntPtr Find(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }
    public static IntPtr L(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }
}
'@
[void][W]::SetProcessDPIAware()
New-Item -ItemType Directory -Force $OutDir | Out-Null
$p = Start-Process -FilePath $Exe -ArgumentList $AppArgs -PassThru
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 200 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 25; $h = [W]::Find([uint32]$p.Id) }
if ($h -eq [IntPtr]::Zero) { if (-not $p.HasExited) { $p.Kill() }; throw "no window appeared (exit code $($p.ExitCode))" }
try {
    foreach ($step in $Steps) {
        $a = $step -split ':'
        switch ($a[0]) {
            'wait' { Start-Sleep -Milliseconds ([int]$a[1]) }
            'ps' { Invoke-Expression ($step.Substring(3)) | Out-Null }
            'key' { [void][W]::PostMessageW($h, 0x0100, [IntPtr][int]$a[1], [IntPtr]::Zero) }
            'move' { [void][W]::PostMessageW($h, 0x0200, [IntPtr]::Zero, [W]::L([int]$a[1], [int]$a[2])) }
            'dbl' { [void][W]::PostMessageW($h, 0x0203, [IntPtr]1, [W]::L([int]$a[1], [int]$a[2])) }
            'wheel' {
                $pt = New-Object W+POINT; $pt.X = [int]$a[2]; $pt.Y = [int]$a[3]; [void][W]::ClientToScreen($h, [ref]$pt)
                [void][W]::PostMessageW($h, 0x020A, [IntPtr]([int]$a[1] -shl 16), [W]::L($pt.X, $pt.Y))
            }
            'drag' {
                [void][W]::PostMessageW($h, 0x0201, [IntPtr]1, [W]::L([int]$a[1], [int]$a[2]))
                [void][W]::PostMessageW($h, 0x0200, [IntPtr]1, [W]::L([int]$a[3], [int]$a[4]))
                [void][W]::PostMessageW($h, 0x0202, [IntPtr]::Zero, [W]::L([int]$a[3], [int]$a[4]))
            }
            'shot' {
                $r = New-Object W+RECT; [void][W]::GetWindowRect($h, [ref]$r)
                $w = $r.R - $r.L; $ht = $r.B - $r.T
                $bmp = New-Object System.Drawing.Bitmap $w, $ht
                $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); [void][W]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()   # the window's own content, even when covered
                $small = New-Object System.Drawing.Bitmap ([int]($w / $Scale)), ([int]($ht / $Scale))
                $g = [System.Drawing.Graphics]::FromImage($small); $g.InterpolationMode = 'HighQualityBicubic'
                $g.DrawImage($bmp, 0, 0, $small.Width, $small.Height); $g.Dispose()
                $file = Join-Path $OutDir "$($a[1]).jpg"
                $small.Save($file, [System.Drawing.Imaging.ImageFormat]::Jpeg); $bmp.Dispose(); $small.Dispose()
                "saved $file ($w x $ht window)"
            }
        }
    }
}
finally {
    [void][W]::PostMessageW($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $p.WaitForExit(3000)) { $p.Kill(); "had to kill the process" }
}
