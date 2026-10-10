# Starts the viewer, optionally sends input, saves screenshots of its window, closes it.
#   -Steps: list of actions run in order:
#       "wait:500"            sleep ms
#       "shot:name"           screenshot to <OutDir>\name.jpg
#       "key:0x70"            WM_KEYDOWN with that virtual key
#       "keyup:0x70"          WM_KEYUP with that virtual key (for keys that act differently when held)
#       "press:0x4D[:sc]"     a key the viewer acts on, with Shift ("s") and Ctrl ("c") as stated and not as the real keyboard has them
#       "release:0x4D"        that key released
#       "wheel:120:800:450[:sc]"  WM_MOUSEWHEEL delta at client x,y, with Shift ("s") and Ctrl ("c") if stated
#       "drag:x0:y0:x1:y1"    left-button drag in client coordinates
#       "dragc:x0:y0:x1:y1[:s]"  the same, but working wherever the real mouse is (see "hover"); with "s": Shift held
#       "move:x:y"            mouse move in client coordinates
#       "hover:x:y"           tell the viewer the mouse rests there (a mouse move is followed by "mouse left" when the real mouse is elsewhere)
#       "dbl:x:y"             double click
#       "rclick:x:y"          right click on the image, in client coordinates
#       "ps:<command>"        run a PowerShell command (e.g. change files the viewer is watching)
#       "clickw:x:y[:sc]"     left click at a position in window pixels, as in an unscaled screenshot; in a panel, with Shift ("s") and Ctrl ("c") if stated
#       "type:text"           type characters into the focused text field
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
            'clickw' {
                # a left click at a position given in pixels of the window as the screenshots show it (before -Scale).
                # Through WM_APP_TEST_MOUSE (0: move, 1: button down, 2: up), so that it works wherever the real mouse is.
                $r = New-Object W+RECT; [void][W]::GetWindowRect($h, [ref]$r)
                $o = New-Object W+POINT; $o.X = 0; $o.Y = 0; [void][W]::ClientToScreen($h, [ref]$o)
                $cx = [int]$a[1] - ($o.X - $r.L); $cy = [int]$a[2] - ($o.Y - $r.T)
                $down = 1; if ($a.Count -gt 3 -and $a[3] -match 's') { $down += 4 }; if ($a.Count -gt 3 -and $a[3] -match 'c') { $down += 8 }   # the button goes down with Shift, Ctrl held
                [void][W]::PostMessageW($h, 0x8016, [IntPtr]::Zero, [W]::L($cx, $cy)); Start-Sleep -Milliseconds 80
                [void][W]::PostMessageW($h, 0x8016, [IntPtr]$down, [W]::L($cx, $cy)); Start-Sleep -Milliseconds 80
                [void][W]::PostMessageW($h, 0x8016, [IntPtr]2, [W]::L($cx, $cy)); Start-Sleep -Milliseconds 80
            }
            'dragc' {
                # a left-button drag in client coordinates, through WM_APP_TEST_MOUSE, in a few steps
                $x0 = [int]$a[1]; $y0 = [int]$a[2]; $x1 = [int]$a[3]; $y1 = [int]$a[4]
                $down = 1; if ($a.Count -gt 5 -and $a[5] -match 's') { $down = 5 }   # 4 added: the button goes down with Shift held
                [void][W]::PostMessageW($h, 0x8016, [IntPtr]::Zero, [W]::L($x0, $y0)); Start-Sleep -Milliseconds 80
                [void][W]::PostMessageW($h, 0x8016, [IntPtr]$down, [W]::L($x0, $y0)); Start-Sleep -Milliseconds 80
                foreach ($i in 1..4) {
                    [void][W]::PostMessageW($h, 0x8016, [IntPtr]::Zero, [W]::L([int]($x0 + ($x1 - $x0) * $i / 4), [int]($y0 + ($y1 - $y0) * $i / 4))); Start-Sleep -Milliseconds 40
                }
                [void][W]::PostMessageW($h, 0x8016, [IntPtr]2, [W]::L($x1, $y1)); Start-Sleep -Milliseconds 80
            }
            'type' {
                # characters as WM_CHAR, for text fields
                foreach ($ch in $step.Substring(5).ToCharArray()) { [void][W]::PostMessageW($h, 0x0102, [IntPtr][int]$ch, [IntPtr]::Zero); Start-Sleep -Milliseconds 30 }
            }
            'ps' { Invoke-Expression ($step.Substring(3)) | Out-Null }
            'key' { [void][W]::PostMessageW($h, 0x0100, [IntPtr][int]$a[1], [IntPtr]::Zero) }
            'keyup' { [void][W]::PostMessageW($h, 0x0101, [IntPtr][int]$a[1], [IntPtr]::Zero) }
            'press' {
                # WM_APP_TEST_KEY: a key with its modifiers stated ("s" Shift, "c" Ctrl), whatever the real keyboard is doing
                $mods = if ($a.Count -gt 2) { $a[2] } else { '' }
                $flags = 0; if ($mods -match 's') { $flags = $flags -bor 1 }; if ($mods -match 'c') { $flags = $flags -bor 2 }
                [void][W]::PostMessageW($h, 0x8017, [IntPtr][int]$a[1], [IntPtr]$flags)
            }
            'release' { [void][W]::PostMessageW($h, 0x8017, [IntPtr][int]$a[1], [IntPtr]4) }
            'move' { [void][W]::PostMessageW($h, 0x0200, [IntPtr]::Zero, [W]::L([int]$a[1], [int]$a[2])) }
            'hover' { [void][W]::PostMessageW($h, 0x8016, [IntPtr]::Zero, [W]::L([int]$a[1], [int]$a[2])) }   # WM_APP_TEST_MOUSE
            'dbl' { [void][W]::PostMessageW($h, 0x0203, [IntPtr]1, [W]::L([int]$a[1], [int]$a[2])) }
            'rclick' {
                # a right click on the image, in client coordinates: opens its menu there
                [void][W]::PostMessageW($h, 0x8016, [IntPtr]::Zero, [W]::L([int]$a[1], [int]$a[2])); Start-Sleep -Milliseconds 80
                [void][W]::PostMessageW($h, 0x0205, [IntPtr]::Zero, [W]::L([int]$a[1], [int]$a[2])); Start-Sleep -Milliseconds 80
            }
            'wheel' {
                $pt = New-Object W+POINT; $pt.X = [int]$a[2]; $pt.Y = [int]$a[3]; [void][W]::ClientToScreen($h, [ref]$pt)
                # the low word of wParam says which keys are held: MK_SHIFT 4, MK_CONTROL 8
                $mods = if ($a.Count -gt 4) { $a[4] } else { '' }
                $keys = 0; if ($mods -match 's') { $keys = $keys -bor 4 }; if ($mods -match 'c') { $keys = $keys -bor 8 }
                [void][W]::PostMessageW($h, 0x020A, [IntPtr]((([int]$a[1]) -shl 16) -bor $keys), [W]::L($pt.X, $pt.Y))
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
