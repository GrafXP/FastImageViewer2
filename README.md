# FastImageViewer2

An image viewer for Windows built for speed. Measured on the development machine: first pixels
of a 24 MP JPEG 100 ms after a cold start, the next image of a folder on the next screen refresh,
a folder of 95,000 images listed and sorted in 52 ms. Plan, design and all measurements:
[PLAN.md](PLAN.md).

## Build

Needs the .NET 10 SDK, MSVC Build Tools 2022 with the Windows SDK, CMake, NASM, Git and Python.

```powershell
native\build-libjpeg-turbo.ps1    # once: downloads and builds libjpeg-turbo 3.2.0
native\build-fivjpeg.ps1          # once: the cancellable JPEG decode shim
native\build-codecs.ps1           # once: downloads and builds the decoders for HEIF, AVIF, WebP, JPEG XL, raw, TIFF (some 6 minutes)
native\build-fivcodecs.ps1        # once: fivcodecs.dll, the shim that holds them
.\build.ps1                       # publishes publish\FastImageViewer2.exe (Native AOT)
dotnet test tests\Fiv.Tests       # unit tests
bench\tools\get-format-samples.ps1  # once, for the format tests: 128 sample files of every format, 258 MB, into corpus\formats
```

`publish\` then holds `FastImageViewer2.exe`, `cimgui.dll`, `ImGuiImpl.dll`, `fivcodecs.dll` and
`vcomp140.dll` (the OpenMP runtime the raw decoder uses); the five belong together.

## Use

```powershell
publish\FastImageViewer2.exe photo.jpg            # one image, and with it its folder
publish\FastImageViewer2.exe a.jpg b.jpg c.jpg    # two to sixteen images: compared
```

Opening one file also lists its folder, so next and previous walk through it. The folder is
watched: files added, removed or changed show up by themselves. Starting the viewer again while
it runs hands the files to the running window.

JPEG (also 12-bit, and lossless up to 16-bit), PNG, TIFF, BMP, GIF, ICO and JPEG XR are read
through Windows. HEIF and HEIC (also the 10-bit HIF files of cameras), AVIF, WebP, JPEG XL and
camera raw are read by decoders that come with the viewer, so they open on any machine, whether
or not the codec extensions from the Microsoft Store are installed: libheif with libde265 and
dav1d, libwebp, libjxl and LibRaw. TIFFs that Windows does not read or reads wrongly (10, 14, 24
and 32 bits, signed integers, 16- and 64-bit float, LogLuv) go to libtiff. More than 8 bits per
channel are kept: as 16-bit values (10 and 12 bits spread over the full range), or as float for
grey. A raw shows the JPEG its camera embedded at once, and its own pixels a second or two later.

Two to sixteen files opened together, dropped on the window, or selected in a panel are
compared: side by side, in a grid, one at a time in the same place, or two of
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

Images that do not lie on each other can be aligned: the second image of the pair is moved,
turned and scaled until it lies on the first. By hand, with `A`: drag it, turn it with a drag
and `Shift` held, nudge it with the arrow keys, or type the numbers. Automatically, with `Shift+A`, from the pictures themselves (a
24 MP pair in about a quarter of a second, to a few hundredths of a pixel); `Q` refines a pose
set by hand. Both can be told to go by a region only: a rectangle dragged on the image, for
when part of the picture has changed or moved on its own. Everything follows the alignment: the panes, the difference and the other
two-image views, the pixel readouts, the numbers and the saved files. The aligned image itself
can be saved, on the pixel grid of the image it was aligned to.

An image can be measured, with `U`: lines, rectangles and circles are drawn on it with the
mouse and listed in a panel. A line has its length, how far it goes across and down, and its
direction; the panel plots the values of the image along it, and the mouse over the plot reads
them at a point, which is marked on the image. A rectangle or a circle has its size and area,
and of the pixels inside it the mean, spread, smallest, largest and median value and a
histogram, for every channel. All of these are taken from the full-resolution pixels as the
file has them, not from the screen. The measurements stay where they are when the image
changes, so the same line or area can be looked at in every image of a folder; in a comparison
they are drawn in every pane, and the plot, the histogram and the tables show every image of
the set, each drawn with a line style of its own and each with a tick in the panel that
switches it off and on. Given a scale (what one pixel is long in a unit of yours, typed, or
worked out from a line of known length), sizes are shown in that unit as well. The list, the
values along a line, and the numbers and the histogram of an area can be saved as CSV. The
panel is a window like the folder panel: it can be moved, made any size, and docked at an edge.

An image can be seen as a 3D height map. The 3D button in the top right corner of the image,
or `3`, tilts the picture back and lets it rise into a surface whose height is its brightness;
again, and it settles flat into the 2D view. The surface carries every pixel of the image; its
heights come from a grid of up to 2048 values on the long side, made from the decoded pixels in
25 to 65 ms. What the display controls do to the brightness they do to the heights: black and
white set the lowest and the highest point, gamma shapes the slopes, `H` takes the height from
one channel. So the values of a 16-bit or float image (a depth map, terrain, a measured
surface) are its height, while a photograph becomes a relief of its brightness, which is not
its depth. A light from the image's upper left shades the slopes. The panel under the button
sets the height, the smoothing, how fine the mesh is, the light, the colours (the image's own,
or a colour map by height), upside down and wireframe. The camera turns about the surface with
a drag; going to the next image keeps it where it is, and the next image rises in its turn.

While comparing (`Shift+3` there, since `3` is the third image), every pane goes into 3D and one
camera sees them all: side by side and in the grid, each image is raised by its own values, on
the comparison's canvas with one height scale for all, so that heights can be compared as
they are seen; its alignment and placement are kept. A view that combines two images is drawn
flat over the whole canvas (at most 2048 pixels on the long side) and raised by what it shows:
the difference by where it is on the colour scale, so that the gain sets the height and a signed
difference stands about a middle plane; SSIM by the dissimilarity; split, blend and checkerboard
by brightness. Everything that changes these views in 2D (mode, gain, divider, opacity, cells,
colours) changes them in 3D at once.

| Input | Action |
|---|---|
| `Right` / `Space` / `PgDn`, `Left` / `Backspace` / `PgUp` | next, previous image |
| `Home`, `End` | first, last image |
| `S`, `Shift+S` | sort the folder by name, date, size, type in turn; reverse the order |
| `B`, `T`, `Tab` | folder panel (thumbnails or list, filter, subfolders), filmstrip, hide or show both |
| `Up` / `Down` | one row up or down in the thumbnail grid |
| Click, `Ctrl`+click, `Shift`+click, `Ctrl+A`, `Ctrl+I` | select in the panels; two to sixteen selected images are compared at once |
| `Ctrl+O`, `Ctrl+Shift+O` | open files, open a folder |
| `Ctrl+C`, `Ctrl+Shift+C` | copy the image, copy its path |
| `F2`, `Del` | rename, move to the Recycle Bin (asks first) |
| Right click | menu: recent files and folders, file actions, panels, labels, settings, file types |
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
| `U` | measure: lines, rectangles and circles on the image, and the panel with their numbers (below) |
| `3`, the 3D button | the image as a 3D height map, or back to flat (below); while comparing `Shift+3` |
| `F11` | fullscreen |
| `F12` | timing overlay: start-up, this image, navigation, cache, folder index |
| `Esc` | leave fullscreen, stop comparing, or quit |

Comparing:

| Input | Action |
|---|---|
| `C` | compare the selected images, or the image on show with the next one; again to stop |
| Drop 2 to 16 files | compare them; with `Ctrl` held, add what is dropped to the comparison |
| Select 2 to 16 images in a panel, or open them from Explorer | compare them: two side by side, more in the grid; with one left selected, it is shown on its own |
| Right click the selection in a panel | file actions for all the selected images (`Del`, `F2` and the image's own menu act on the active one) |
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

Aligning the second image of the pair to the first:

| Input | Action |
|---|---|
| `A` | align mode: a panel with the image's position, turn and size as numbers, how alike the two images are as they lie, reset and undo; drags and the keys below move the image instead of the view |
| `Shift+A` | align automatically: moved only, moved and turned, or moved, turned and scaled, as chosen in the panel. A result that does not make the two more alike is not taken over |
| `Q` | fine align: refine the pose the image has (set by hand to within about a twentieth of the image) |
| Drag, arrow keys | move the image; the keys by 1 pixel, with `Shift` by 0.1 |
| `Shift`+drag | turn the image about its centre, by the angle the mouse goes round it: grab it far from the centre to turn finely |
| `PgUp` / `PgDn`, `Shift`+wheel | turn it by 0.1 degrees; the keys with `Shift`, the wheel with `Ctrl` as well: by 0.01 |
| `Home` / `End`, `Ctrl`+wheel | make it smaller / larger by 0.1 % (the keys with `Shift`: 0.01 %) |
| "Region..." in the panel, then a drag | a rectangle for the automatic alignment to go by, instead of the whole of both images; "Whole image" takes it away |
| `Backspace` | back to where it was before any aligning |
| `Ctrl+E` | save the aligned image: PNG, or 32-bit float TIFF for float images; transparent where it has no pixel |
| `A` or `Esc` | leave align mode; the image stays where it was put |

Measuring, on one image or while comparing:

| Input | Action |
|---|---|
| `U` | the measurements and their panel, on or off; they are kept while they are off |
| `L`, `R`, `C`, then a drag | draw a line, a rectangle, a circle from its centre. While the measurements are on, these three keys choose a shape instead of what they do otherwise |
| `Shift`+drag | the line level, upright or diagonal; the rectangle a square |
| Drag a handle | change a measurement: an end of a line, a corner or a side of a rectangle, the outline of a circle |
| Drag a line, a rectangle's outline, a circle's centre | move it |
| Click a measurement, or its row in the panel | select it: the panel shows its numbers, and the values along the line or the histogram of the area |
| The numbers in the panel | type or step the position and size of the selected measurement |
| Mouse over the plot or the histogram | the last column of the table under it shows the values at that point of the line, which is marked on the image, or the number of pixels with that value |
| The ticks by the images' names (while comparing) | which images of the set the plot, the histogram and the tables show; the first one ticked is drawn solid and its histogram filled, the others dashed, dotted and so on |
| Drag the panel's title bar, its edges or corner | move it, or dock it where the markers show; resize it. What is in it follows its width |
| `H`, `Shift+H` | the channel the plot and the histogram show, as for the image |
| `Del` | delete the selected measurement (while the measurements are on, `Del` does not delete the file) |
| `Esc` | give up the shape chosen; again: put the measurements away |
| "Scale" in the panel | what one pixel is long, and in which unit; or the known length of the selected line, from which that is worked out |
| Buttons in the panel | save the list, the profile of the line, or the numbers and the histogram of the area, as CSV |

The 3D height map, of one image or of every pane of a comparison (measuring lays it flat):

| Input | Action |
|---|---|
| `3`, the 3D button | into 3D, or back to the flat image |
| `Shift+3` | the same while comparing |
| Drag, `Shift`+drag | turn the camera about the surface; move it over the ground |
| Mouse wheel, `+` / `-` | closer or further away, at the cursor / at the centre |
| Double click, `F`, "Starting view" | back to the view the 3D started from, the whole image filling the window |
| `D`, `H` | black, white, gamma and the channel: they make the heights as they make the brightness |
| `Right`, `Left` | next, previous image, in 3D; while comparing, of the set (`M`, `Up`, `Down`, `G` act as in 2D) |
| `Ctrl+C` | copy what the 3D view shows |
| `Esc` | back to the flat image |

A line is where the mouse puts it: zoomed out its ends lie on the centres of pixels, zoomed in
they follow the mouse to the fraction of a pixel the screen shows. Along it there is a sample
every pixel of length, blended from the four pixels around its point, so a line along a row or
a column through pixel centres gives the stored values exactly. A rectangle holds whole pixels;
a pixel belongs to a circle if its centre is inside.

The status bar shows position in the folder, file, size, zoom, and the coordinates and stored
values of the pixel under the cursor (16-bit and float values as they are in the file). The
name and size are also written into the top left corner of the image, on its own as in every
pane of a comparison; "Labels" in the menu and in the compare panel switches that off.

Formats: JPEG through libjpeg-turbo; PNG, TIFF, BMP, GIF, ICO and whatever else Windows has a
codec for (WebP, HEIC, AVIF, camera raw) through WIC.

Opening images from Explorer: the viewer is not installed, `publish\` can be copied anywhere.
"File types..." in the menu registers it with Windows for the endings ticked there, for the
current user: it then appears under "Open with" and among the default apps of the Windows
settings. Which program a double click starts remains the user's choice, which Windows 10 and
11 keep programs from changing: Windows asks the next time such a file is opened, and a button
in the dialog opens the settings page to choose at once. The dialog shows for every ending which
program opens it now. "Remove all" takes the viewer out of the registry and puts back what the
endings were set to before: do that before deleting the folder. After moving the folder, Apply
again.

Several images selected in Explorer and opened together are compared, two side by side and
more in the grid, in the order of their names. Explorer starts the viewer once for each file
of a selection, and each start hands its file to the viewer already running; files that arrive
within half a second of each other, and within a second and a half once there are two, are
taken as one selection.

Options: `--cache-mb <n>` and `--vram-mb <n>` set the budgets of the image cache (default 4096
and 2048); `--no-prefetch` loads only the image asked for; `--new-instance` does not hand over
to a running viewer; `--compare-mode <single|side|grid|split|blend|checker|diff|ssim>` starts a
comparison in that mode instead of the one used last. `--register <jpg,png,...|common|all>`
does what "File types..." does, for exactly those endings and without a window, and
`--unregister` takes it all back; the exit code is 0 if it was done.

## Measure

```powershell
dotnet build bench\Fiv.Bench -c Release
$bench = 'artifacts\bin\Fiv.Bench\release_win-x64\fivbench.exe'
& $bench corpus      # generate the test images into corpus\ (221 files, about 0.9 GB, half a minute)
& $bench open        # cold start of the viewer, stage by stage
& $bench reopen      # open with the viewer already running
& $bench navigate    # next/previous in a folder: single steps, and the arrow key held
& $bench thumbs      # making thumbnails, and reading them back from the cache
& $bench browse      # folder panel: first screen of thumbnails, new and seen before
& $bench compare     # compare modes: loading a set, zoom and pan in each mode, switching
& $bench metrics     # comparing on the CPU: error metrics, structural similarity, saving the views
& $bench align       # aligning two images: time, and how close to known moves, turns and scalings
& $bench measure     # measuring: the numbers of an area and the values along a line
& $bench surface     # the 3D height map: making the height grid of an image
& $bench decode      # decoder timings
& $bench formats     # every sample of corpus\formats: decoder, result, timings, what Windows alone makes of it (--dump <dir>: as PNG)
& $bench bakeoff     # the C# vs C++ comparison (bench\bakeoff\build.ps1 first)
```

The benchmarks open and close viewer windows on the desktop; they do not take the focus.
`bench\tools\window-shot.ps1` starts the viewer, sends it keys and mouse input, and saves
pictures of its window. Its `press`, `hover`, `clickw` and `dragc` steps reach the viewer
through test messages, so they work whatever the real mouse and keyboard are doing.

A crash writes to `%LOCALAPPDATA%\FastImageViewer2\crash.log`.
