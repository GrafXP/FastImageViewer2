# FastImageViewer2

An image viewer for Windows built for speed: first pixels of a 24 MP JPEG 93 ms after a cold
start, measured on the development machine. Plan, design and all measurements: [PLAN.md](PLAN.md).

## Build

Needs the .NET 10 SDK, MSVC Build Tools 2022 with the Windows SDK, CMake and NASM.

```powershell
native\build-libjpeg-turbo.ps1    # once: downloads and builds libjpeg-turbo 3.2.0
.\build.ps1                       # publishes publish\FastImageViewer2.exe (Native AOT)
```

`publish\` then holds `FastImageViewer2.exe`, `cimgui.dll` and `ImGuiImpl.dll`; the three belong together.

## Use

```powershell
publish\FastImageViewer2.exe photo.jpg            # or several files
```

Starting it again while it runs hands the files to the running window.

| Input | Action |
|---|---|
| Mouse wheel, `+` / `-` | zoom at the cursor / at the centre |
| Left drag | pan |
| Double click | 100 % at the cursor, or back to fit |
| `F` or `0` | fit to window |
| `1` | 100 % |
| `Right` / `Space` / `PgDn`, `Left` / `Backspace` / `PgUp` | next, previous file |
| `Home`, `End` | first, last file |
| `I` | exact pixels or smooth when zoomed in |
| `L` | keep zoom and position when changing image |
| `D` | display panel: black point, white point, gamma |
| `F11` | fullscreen |
| `F12` | timing overlay |
| `Esc` | leave fullscreen, or quit |

The status bar shows file, size, zoom, and the coordinates and stored values of the pixel under
the cursor (16-bit and float values as they are in the file).

Formats: JPEG through libjpeg-turbo; PNG, TIFF, BMP, GIF, ICO and whatever else Windows has a
codec for (WebP, HEIC, AVIF, camera raw) through WIC.

## Measure

```powershell
dotnet build bench\Fiv.Bench -c Release
$bench = 'artifacts\bin\Fiv.Bench\release_win-x64\fivbench.exe'
& $bench corpus      # generate the test images into corpus\ (about 1 GB, 2 minutes)
& $bench open        # cold start of the viewer, stage by stage
& $bench reopen      # open with the viewer already running
& $bench decode      # decoder timings
& $bench bakeoff     # the C# vs C++ comparison (bench\bakeoff\build.ps1 first)
```

A crash writes to `%LOCALAPPDATA%\FastImageViewer2\crash.log`.
