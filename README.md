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
publish\FastImageViewer2.exe photo.jpg            # one image, and with it its folder
publish\FastImageViewer2.exe a.jpg b.jpg c.jpg    # two to sixteen images: compared
```

Opening one file also lists its folder, so next and previous walk through it. The folder is
watched: files added, removed or changed show up by themselves. Starting the viewer again while
it runs hands the files to the running window.

Two to sixteen files opened together, dropped on the window, or selected in a panel and sent
off with `C`, are compared: side by side, in a grid, one at a time in the same place, or two of
them in one pane, split by a divider, blended, or as a checkerboard. Zoom, position and the
display controls are the same for all of them, and each pane shows the position and values of
its own pixel under the cursor.

What two images differ by is shown as a picture and as numbers. The difference view colours
every pixel by the difference (absolute, signed, squared, or relative to the first image) with a
gain and a colour bar in the units of the files, or marks the pixels that differ by more than a
threshold; the SSIM view is a heat map of structural similarity. The metrics panel gives MAE,
MSE, PSNR, the largest error, the share of pixels over the threshold and SSIM for every channel,
with a histogram of the differences, for the whole image or a region dragged on it. The numbers
are computed from the full-resolution pixels as the files have them, not from the screen. Zoomed
in far enough, the values of both images and their difference are written into each pixel. The
view at full resolution, the difference values and the numbers can be saved.

| Input | Action |
|---|---|
| `Right` / `Space` / `PgDn`, `Left` / `Backspace` / `PgUp` | next, previous image |
| `Home`, `End` | first, last image |
| `S`, `Shift+S` | sort the folder by name, date, size, type in turn; reverse the order |
| `B`, `T`, `Tab` | folder panel (thumbnails or list, filter, subfolders), filmstrip, hide or show both |
| `Up` / `Down` | one row up or down in the thumbnail grid |
| Click, `Ctrl`+click, `Shift`+click, `Ctrl+A`, `Ctrl+I` | select in the panels |
| `Ctrl+O`, `Ctrl+Shift+O` | open files, open a folder |
| `Ctrl+C`, `Ctrl+Shift+C` | copy the image, copy its path |
| `F2`, `Del` | rename, move to the Recycle Bin (asks first) |
| Right click | menu: recent files and folders, file actions, panels, settings |
| `F1` | list of keys |
| Mouse wheel, `+` / `-` | zoom at the cursor / at the centre |
| Left drag | pan |
| Double click | 100 % at the cursor, or back to fit |
| `F` or `0` | fit to window |
| `1` or `Z` | 100 % (while comparing, `1` is the first image and only `Z` is 100 %) |
| `I` | exact pixels or smooth when zoomed in |
| `L` | keep zoom and position when changing image |
| `D` | display panel: black point, white point, gamma, exposure, channel |
| `H`, `Shift+H` | show colour, red, green, blue, alpha, luma in turn |
| `V` | write the pixel values into the pixels once they are large enough on screen (zoom goes to 25600 %) |
| `F11` | fullscreen |
| `F12` | timing overlay: start-up, this image, navigation, cache, folder index |
| `Esc` | leave fullscreen, stop comparing, or quit |

Comparing:

| Input | Action |
|---|---|
| `C` | compare the selected images, or the image on show with the next one; again to stop |
| Drop 2 to 16 files | compare them; with `Ctrl` held, add what is dropped to the comparison |
| `M`, `Shift+M` | mode: single, side by side, grid, split, blend, checkerboard, difference, SSIM |
| `1` … `9` | go to that image; held down, show it only until the key is released |
| `Left` / `Right`, `Home` / `End` | previous / next, first / last image of the set |
| `X` | swap the two images of the split, blend and checkerboard |
| `K` | blink between the last two images |
| `Up` / `Down` | move the divider, change the opacity, the cell size, the blink rate or the gain (`Shift`: finer) |
| `L` | one zoom and position for all panes, or one each |
| `P` | compare panel: all of the above, how different sizes are matched, labels |
| Click a pane, drag the divider | make its image the active one; move the divider |
| Right click | menu: mode, remove the image from the comparison, copy and save, stop |
| `E`, `Shift+E` | difference view; in it: absolute, signed, squared, relative. `H` chooses the channel (colour: the one that differs most) |
| `G`, `Shift+G` | colours of the difference and the similarity: grey, Viridis, Turbo, Jet, thermal, rainbow, cool-warm, red-blue, magenta-cyan |
| `W` | mark the pixels that differ by more than the threshold, on the dimmed first image |
| `,` / `.` | threshold down / up |
| `N` | metrics panel |
| `R`, `Shift+R` | drag a rectangle for the numbers to be taken over (also `Shift`+drag); the whole image again |
| `Ctrl+C` | copy what the panes show (one image of the set: through the menu) |
| `Ctrl+S` | save the split, blend, checkerboard, difference or SSIM view at full resolution (PNG) |
| `Ctrl+D` | save the difference itself, second image less first, as 32-bit float TIFF |
| `Ctrl+Shift+S` | save the numbers as CSV or JSON |

The status bar shows position in the folder, file, size, zoom, and the coordinates and stored
values of the pixel under the cursor (16-bit and float values as they are in the file).

Formats: JPEG through libjpeg-turbo; PNG, TIFF, BMP, GIF, ICO and whatever else Windows has a
codec for (WebP, HEIC, AVIF, camera raw) through WIC.

Options: `--cache-mb <n>` and `--vram-mb <n>` set the budgets of the image cache (default 4096
and 2048); `--no-prefetch` loads only the image asked for; `--new-instance` does not hand over
to a running viewer; `--compare-mode <single|side|grid|split|blend|checker|diff|ssim>` starts a
comparison in that mode instead of the one used last.

## Measure

```powershell
dotnet build bench\Fiv.Bench -c Release
$bench = 'artifacts\bin\Fiv.Bench\release_win-x64\fivbench.exe'
& $bench corpus      # generate the test images into corpus\ (220 files, about 0.9 GB, half a minute)
& $bench open        # cold start of the viewer, stage by stage
& $bench reopen      # open with the viewer already running
& $bench navigate    # next/previous in a folder: single steps, and the arrow key held
& $bench thumbs      # making thumbnails, and reading them back from the cache
& $bench browse      # folder panel: first screen of thumbnails, new and seen before
& $bench compare     # compare modes: loading a set, zoom and pan in each mode, switching
& $bench metrics     # comparing on the CPU: error metrics, structural similarity, saving the views
& $bench decode      # decoder timings
& $bench bakeoff     # the C# vs C++ comparison (bench\bakeoff\build.ps1 first)
```

The benchmarks open and close viewer windows on the desktop; they do not take the focus.
`bench\tools\window-shot.ps1` starts the viewer, sends it keys and mouse input, and saves
pictures of its window. Its `press`, `hover`, `clickw` and `dragc` steps reach the viewer
through test messages, so they work whatever the real mouse and keyboard are doing.

A crash writes to `%LOCALAPPDATA%\FastImageViewer2\crash.log`.
