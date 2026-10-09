# FastImageViewer2

An image viewer for Windows built for speed. Measured on the development machine: first pixels
of a 24 MP JPEG 100 ms after a cold start, the next image of a folder on the next screen refresh,
a folder of 95,000 images listed and sorted in 52 ms. Plan, design and all measurements:
[PLAN.md](PLAN.md).

## Build

Needs the .NET 10 SDK, MSVC Build Tools 2022 with the Windows SDK, CMake and NASM.

```powershell
native\build-libjpeg-turbo.ps1    # once: downloads and builds libjpeg-turbo 3.2.0
native\build-fivjpeg.ps1          # once: the cancellable JPEG decode shim
.\build.ps1                       # publishes publish\FastImageViewer2.exe (Native AOT)
dotnet test tests\Fiv.Tests       # unit tests
```

`publish\` then holds `FastImageViewer2.exe`, `cimgui.dll` and `ImGuiImpl.dll`; the three belong together.

## Use

```powershell
publish\FastImageViewer2.exe photo.jpg            # or several files
```

Opening one file also lists its folder, so next and previous walk through it. The folder is
watched: files added, removed or changed show up by themselves. Starting the viewer again while
it runs hands the files to the running window.

| Input | Action |
|---|---|
| `Right` / `Space` / `PgDn`, `Left` / `Backspace` / `PgUp` | next, previous image |
| `Home`, `End` | first, last image |
| `S`, `Shift+S` | sort the folder by name, date, size, type in turn; reverse the order |
| Mouse wheel, `+` / `-` | zoom at the cursor / at the centre |
| Left drag | pan |
| Double click | 100 % at the cursor, or back to fit |
| `F` or `0` | fit to window |
| `1` | 100 % |
| `I` | exact pixels or smooth when zoomed in |
| `L` | keep zoom and position when changing image |
| `D` | display panel: black point, white point, gamma |
| `F11` | fullscreen |
| `F12` | timing overlay: start-up, this image, navigation, cache, folder index |
| `Esc` | leave fullscreen, or quit |

The status bar shows position in the folder, file, size, zoom, and the coordinates and stored
values of the pixel under the cursor (16-bit and float values as they are in the file).

Formats: JPEG through libjpeg-turbo; PNG, TIFF, BMP, GIF, ICO and whatever else Windows has a
codec for (WebP, HEIC, AVIF, camera raw) through WIC.

Options: `--cache-mb <n>` and `--vram-mb <n>` set the budgets of the image cache (default 4096
and 2048); `--no-prefetch` loads only the image asked for; `--new-instance` does not hand over
to a running viewer.

## Measure

```powershell
dotnet build bench\Fiv.Bench -c Release
$bench = 'artifacts\bin\Fiv.Bench\release_win-x64\fivbench.exe'
& $bench corpus      # generate the test images into corpus\ (about 1 GB, 2 minutes)
& $bench open        # cold start of the viewer, stage by stage
& $bench reopen      # open with the viewer already running
& $bench navigate    # next/previous in a folder: single steps, and the arrow key held
& $bench index       # folder read and sort for 1k / 10k / 100k files, header probes
& $bench decode      # decoder timings
& $bench bakeoff     # the C# vs C++ comparison (bench\bakeoff\build.ps1 first)
```

The benchmarks open and close viewer windows on the desktop; they do not take the focus.
`bench\tools\window-shot.ps1` starts the viewer, sends it keys and mouse input, and saves
pictures of its window.

A crash writes to `%LOCALAPPDATA%\FastImageViewer2\crash.log`.
