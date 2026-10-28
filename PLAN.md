# FastImageViewer2 — Plan

Status: **M0, M1 and M2 done and measured; M3, M4, M5 and M6 built and measured, each with untested items** (see section 9); **M7 is put off, at the user's wish; of M8 the measurements are built and measured (M8.1), with untested items.** What each milestone left open is listed
under it in section 9. Written 2026-10-09. Language decided: C# / .NET 10 Native AOT (section 3.1).
Measured numbers: section 2.1 (M0), 2.2 (M1), 2.3 (M2), 2.4 (M4, comparing), 2.5 (M5, difference and metrics), 2.6 (M6, aligning) and 2.7 (M8.1, measuring). Build and keys: `README.md`.

FastImageViewer2 is a new image viewer for Windows. It takes its feature ideas from
FastImageViewer (`../FastImageViewer`) but shares no code and no technology with it.

## 1. Goals, in priority order

1. **Open an image ultra fast** — from double-click in Explorer to pixels on screen.
2. **Index a folder of images ultra fast** — list, sort, navigate and thumbnail tens of
   thousands of files without ever blocking the UI.
3. **Image comparison** — the main feature area once 1 and 2 are met (side-by-side, flicker,
   split, difference, metrics, alignment, folder-vs-folder).

Everything else the old app did (measurement, analysis, scripting, OCR, …) is out of scope
until those three are done. Section 8 lists each old feature and where it lands.

## 2. Performance budgets

These are **targets, not measurements**. The decode figures behind them are my estimates for
this machine (i7-8700K 6C/12T with AVX2, GTX 1070 Ti 8 GB, 32 GB RAM, NVMe + SATA SSD + HDDs).
Milestone M0 builds the harness that measures them, measures the old app on the same files as
the baseline to beat, and then this table is replaced with real numbers.

| Scenario | Target |
|---|---|
| Open, viewer already running: 24 MP JPEG, first pixels (fit-to-window preview) | ≤ 100 ms |
| Same, full resolution ready for 100 % zoom | ≤ 300 ms |
| Open, cold start (no instance running), first pixels | ≤ 200 ms |
| Next / previous image, already prefetched | ≤ 1 frame (16 ms) |
| Holding the arrow key through a folder of 12 MP JPEGs | every image shown, ≥ 20 images/s |
| Folder of 10,000 files: listed, sorted, scrollable (local SSD) | ≤ 50 ms |
| Folder of 100,000 files: same | ≤ 500 ms |
| First screen of thumbnails, folder seen before (cached) | ≤ 100 ms |
| First screen of thumbnails, new folder of camera JPEGs | ≤ 300 ms |
| Thumbnail generation, uncached camera JPEGs | ≥ 300 images/s (disk permitting) |
| Pan / zoom, any image size, any compare mode | display refresh rate, < 2 ms CPU per frame |
| Idle | 0 % CPU and GPU (render only on change) |

Formats without a cheap low-resolution decode (PNG, TIFF) cannot hit the 100 ms preview target
for large files on a first visit; they show the cached thumbnail immediately and the full
image when decoding finishes.

### 2.1 Measured in M0

This machine, 1600×900 window, synthetic photo-like corpus (`fivbench corpus`; JPEGs at quality 95,
about 2 bits/pixel, which is phone-camera territory — DSLR "fine" files are 3–4 bits/pixel and
will decode slower). Files were in the OS cache; nothing here is a disk-cold number. Reproduce
with `fivbench bakeoff` and `fivbench decode`.

**Cold start, 24 MP JPEG, fresh process each time** (50 runs each, median, p10–p90 in brackets):

| Stage, ms | C# Native AOT | C++ |
|---|---|---|
| process spawn → `main()` | 13.3 (12.2–14.9) | 10.4 (9.8–12.5) |
| create window | 7.0 | 6.8 |
| **create D3D11 device** | **188.9 (183–199)** | **193.2 (187–205)** |
| swap chain, shaders, sampler | 4.1 | 4.3 |
| read file + decode at 1/4 (parallel thread) | 73.0 | 75.6 |
| show window, upload, draw, `Present` | 18.4 | 18.3 |
| **total, spawn → `Present` returned** | **232.3 (226–244)** | **234.3 (226–249)** |

**Warm path in a running process:** read + decode at 1/4: 67 ms; upload + draw + `Present`: 1.3 ms.

**Decode, single thread, ms (source megapixels per second):**

| File | 1/1 | 1/2 | 1/4 | 1/8 | WIC 1/1 |
|---|---|---|---|---|---|
| JPEG 12 MP 4:2:0, 3.2 MB | 50 (242) | 37 | 33 | 26 | 54 |
| JPEG 24 MP 4:2:0, 5.9 MB | 99 (243) | 72 | 63 | 48 | 102 |
| JPEG 24 MP 4:2:2, 6.6 MB | 115 (209) | 83 | 73 | 55 | 116 |
| JPEG 24 MP 4:4:4, 8.4 MB | 143 (168) | 114 | 102 | 78 | 150 |
| JPEG 24 MP progressive, 5.5 MB | 294 (82) | 263 | 260 | 252 | 542 |
| JPEG 50 MP 4:2:0, 13.2 MB | 201 (250) | 153 | 135 | 104 | 216 |
| JPEG 20000×4000, 16.5 MB | 299 (267) | 220 | 194 | 150 | 315 |
| PNG 8-bit RGB 24 MP, 30.7 MB | | | | | 274 (88) |
| PNG 16-bit gray 24 MP, 37.2 MB | | | | | 380 (63) |
| TIFF 16-bit gray 24 MP, uncompressed | | | | | 13 |
| TIFF float gray 24 MP, uncompressed | | | | | 28 |

libjpeg-turbo's fast-upsample and fast-DCT options gain 7 % and 12 % at full size and nothing at 1/4.

**Reduced-size decode throughput, 12 MP JPEGs from memory, images per second:**

| Threads | 1/8 (500×375) | 1/4 (1000×750) | 1/1 |
|---|---|---|---|
| 1 | 40 | 31 | 20 |
| 6 | 174 | 121 | 64 |
| 12 | 274 | 175 | 96 |

**What this changes:**

- **Language makes no measurable difference.** C# costs 3 ms more before `main()` and nothing after.
- **Device creation is 80 % of a cold start** and puts the D3D-first path 30 ms over the 200 ms
  budget in either language. M1 therefore creates the device on its own thread and paints the
  first frame with GDI as soon as the preview is decoded (expected near 100 ms, to be measured);
  Direct3D takes the window over when it is ready.
- **A reduced-size JPEG decode is 1.5–2× faster than a full one, not "several times"** as section 5
  assumed: entropy decoding does not shrink with the output. The preview still arrives a third
  sooner, so two-stage loading stays, with the full decode started in parallel rather than after.
- **The warm-open budget (≤ 100 ms) holds** for 24 MP 4:2:0 (68 ms in process, plus the
  forwarding process, not yet measured end to end) and is tight for 4:4:4 and 50 MP files.
- **Progressive JPEGs are 3× slower** and gain nothing from reduced-size decoding.
- **PNG through WIC is the slowest common case** (274 ms for 24 MP). This is the measured case
  for trying a faster native PNG decoder in M1.
- **Thumbnails by decoding alone reach 274/s, not 300/s**; the target needs the embedded EXIF
  thumbnails that real camera files carry and the synthetic corpus does not.

### 2.2 Measured in M1: the viewer itself

`publish\FastImageViewer2.exe`, same machine and corpus, window opened at its default size on the
2560×1440 monitor (viewport about 2160×1180, so larger than M0's 1600×900). The machine was in
use during the runs, which widens the spread. Reproduce with `fivbench open` and `fivbench reopen`.

**Cold start, 24 MP JPEG** (30 fresh processes, ms after process creation, median and p10–p90):

| Stage reached | ms |
|---|---|
| `main()` | 13.1 (11.2–16.0) |
| window created (hidden) | 20.7 |
| preview decoded (1/4 size, on its own thread) | 87.8 |
| **first pixels, painted with GDI** | **93.1 (87.1–103.3)** |
| full image decoded (third thread) | 131.3 |
| Direct3D device ready (fourth thread) | 207.3 |
| **full-resolution image on screen** | **275.0 (265.4–291.2)** |

**Warm open** (viewer running; a second process hands the file over; 12 opens per image, ms after
that second process was created):

| Image | Handed over | First frame | Full image on screen |
|---|---|---|---|
| JPEG 12 MP | 23 | 76 (62–90) | 105 (93–124) |
| JPEG 24 MP 4:2:0 | 22 | 98 (85–111) | 158 (141–167) |
| JPEG 24 MP 4:2:2 | 22 | 106 (99–123) | 166 (156–185) |
| JPEG 50 MP | 18 | 176 (164–188) | 299 (283–318) |
| PNG 8-bit 24 MP | 23 | 352 (344–367) | 352 |

**Zoom and pan** (2 s of scripted wheel and drag input, an event every 4 ms, on a 144 Hz monitor):
50 MP JPEG, the 20000×4000 JPEG (three textures) and the 24 MP float TIFF each rendered every
frame at the refresh interval (mean 6.9 ms between frames, longest 11.7 ms) using 0.12 ms of CPU
per frame (longest 1.4 ms).

**Against the budgets of section 2:**

| Budget | Result |
|---|---|
| Cold start, first pixels ≤ 200 ms | **93 ms** — met |
| Warm open 24 MP, first pixels ≤ 100 ms | **98 ms** — met at the median, not at p90 (111 ms) |
| Warm open 24 MP, full resolution ≤ 300 ms | **158 ms** — met |
| Pan / zoom at refresh rate, < 2 ms CPU per frame | met (0.12 ms) |
| Idle 0 % CPU and GPU | by design (frames are rendered only on change); not measured |

Where the warm-open time goes for 24 MP: about 15–20 ms to start the second process and hand
over, about 65 ms to decode the preview, the rest upload and waiting for the next refresh. The
decode is bound by libjpeg-turbo's entropy decoder, so the remaining levers are embedded camera
previews (for real camera files) and a smaller hand-over process.

### 2.3 Measured in M2: navigation, index, and the viewer on its new pipeline

Same machine and corpus; the machine was in use. Reproduce with `fivbench navigate`, `open` and
`reopen`. Times end when `Present` returns; the frame is on screen at the next refresh.
The folder index and header probe numbers below cannot be re-run: they came from `fivbench index`
on generated folders of 1,000, 10,000 and 100,000 files, and both were removed on 2026-10-10
(111,000 files in the project were more than the backup could take).

**Navigation**, folder of 40 different 12 MP JPEGs (about 3 MB each):

| | Result |
|---|---|
| One step to the next or previous image, 0.6 s between steps (20 steps) | key to frame: median 1.3 ms, p90 1.9 ms, longest 4.2 ms |
| Right arrow held, 30 key events per second (39 steps) | 39 of 39 images shown in 1.30 s = 30 images/s; key to frame: median 1.2 ms, longest 1.6 ms |

With ten large JPEGs instead (12 to 80 MP, one progressive), single steps are the same (median
1.2 ms, longest 8 ms); with the key held, 8 of 9 were shown and one took 83 ms — previews of
50 MP and progressive files take longer to decode than a key repeat lasts.

**Folder index** (directory metadata in the OS cache, median of 7):

| Folder | Read | Natural sort | Total | Budget | .NET `GetFiles` + `FileInfo` | Sort with Explorer's `StrCmpLogicalW` |
|---|---|---|---|---|---|---|
| 950 images | 0.6 ms | 1.5 ms | 2.1 ms | | 10 ms | 2 ms |
| 9,500 images | 5.3 ms | 18.1 ms | 23.8 ms | ≤ 50 ms | 74 ms | 20 ms |
| 95,000 images | 37.0 ms | 14.4 ms | 51.7 ms | ≤ 500 ms | 2,241 ms | 297 ms |

The 9,500 row ranged from 9 to 24 ms over three runs; at that size the cost of starting the
parallel sort varies. Through the SMB redirector (the same folders as
`\\localhost\I$\…`), reading 95,000 entries took 51 ms against 32 ms locally in the same run. That
is the protocol without a network; a real share adds its latency once per directory buffer, and
that has not been measured.

**Header probes** (pixel size from the first 4 KB of a file): they agree with the decoders on
all 15 corpus files. A first pass over files not opened before ran at 540–700 files per second
on one thread, about 1.5 ms per file, nearly all of it in opening the file (the on-access virus
scan, most likely; not verified); a second pass over the same files, on all cores, ran 16 to
200 times faster. Dimensions
for a file list therefore have to be read lazily, visible rows first, and kept — which is what
the thumbnail cache of M3 is for. This also confirms the index design: it never opens a file.

**Open, after the move to the decode pool** (compare section 2.2):

| | M1 | Now |
|---|---|---|
| Cold start 24 MP, first pixels (GDI) | 93 ms | 100 ms (92–112) |
| Cold start, first Direct3D frame (preview) | 275 ms | 255 ms |
| Cold start, full resolution | 275 ms | 298 ms (291–309) |
| Warm open 24 MP, first frame | 98 ms | 94 ms (86–102) |
| Warm open 24 MP, full resolution | 158 ms | 160 ms |
| Warm open 12 MP, first frame / full | 76 / 105 ms | 70 / 109 ms |
| Warm open 50 MP, first frame / full | 176 / 299 ms | 182 / 325 ms |

The cold-start figures moved with the machine's state between sessions (device creation alone
ranged from 207 to 224 ms); nothing in them points at the new pipeline. Idle, showing an image:
31 ms of CPU time in 10 s.

**Against the budgets of section 2:**

| Budget | Result |
|---|---|
| Next / previous, already prefetched: ≤ 1 frame (16 ms) | **1.3 ms** to `Present`, plus the wait for the refresh — met |
| Arrow key held through 12 MP JPEGs: every image shown, ≥ 20 images/s | **39 of 39 at 30 images/s** — met |
| Folder of 10,000 listed and sorted ≤ 50 ms | **24 ms** (9–24) — met |
| Folder of 100,000 ≤ 500 ms | **52 ms** — met |
| Cold start first pixels ≤ 200 ms; warm ≤ 100 ms; full ≤ 300 ms | 100 ms; 94 ms; 160 ms — met (warm p90 is 102 ms) |
| Idle 0 % CPU | 0.3 % of one core — met |

### 2.4 Measured in M4: comparing

Same machine and corpus (`corpus\compare`: one 24 MP scene in four versions); window of 2160×1185
pixels on the 144 Hz monitor; the machine was in use. Reproduce with `fivbench compare`.

"All in full" counts from just before the viewer's process is created to the first frame that
has every image on show at full resolution (in the single view that is one image). Zoom and pan:
2 s of scripted wheel and drag input, an event every 4 ms.

| Mode | Images | All in full on screen | Frames in 2 s | Between frames, mean / longest | CPU per frame, mean / longest |
|---|---|---|---|---|---|
| one image, not comparing | 1 of 24 MP | 290 ms | 294 | 6.94 / 11.4 ms | 0.12 / 0.54 ms |
| single | 2 of 24 MP | 273 ms | 294 | 6.94 / 11.4 ms | 0.14 / 0.50 ms |
| side by side | 2 of 24 MP | 312 ms | 294 | 6.93 / 11.5 ms | 0.16 / 0.44 ms |
| grid | 3 of 24 MP, 1 of 6 MP | 350 ms | 295 | 6.93 / 11.4 ms | 0.16 / 0.53 ms |
| grid | 16 of 12 MP | 488 ms | 295 | 6.94 / 11.3 ms | 0.21 / 0.42 ms |
| split | 2 of 24 MP | 302 ms | 294 | 6.93 / 11.3 ms | 0.14 / 0.42 ms |
| blend | 2 of 24 MP | 302 ms | 294 | 6.94 / 11.3 ms | 0.14 / 0.62 ms |
| checkerboard | 2 of 24 MP | 306 ms | 294 | 6.95 / 11.3 ms | 0.13 / 0.59 ms |

Switching between two 24 MP images in the single view (20 switches, 300 ms apart): key to
`Present` returned, median 1.6 ms, p90 2.7 ms, longest 3.5 ms. Idle with a blend of two 24 MP
images on screen: 16 ms of CPU time in 10 s. With sixteen 12 MP images in the set, the cache
held 732 MB of decoded pixels and 977 MB of textures.

**Against the budgets of section 2:**

| Budget | Result |
|---|---|
| Pan / zoom, any image size, any compare mode: refresh rate, < 2 ms CPU per frame | every refresh got a frame in every mode; **0.13–0.21 ms** of CPU per frame, longest 0.62 ms (1.4 ms in an earlier run) — met |
| Next / previous, already loaded: ≤ 1 frame | switching between compared images: **1.6 ms** to `Present`, plus the wait for the refresh — met |
| Idle 0 % CPU | 0.16 % of one core while comparing — met |

A set costs little more to load than one image, because its images are decoded side by side on
the pool: two 24 MP images are on screen in full 12–22 ms after the time one takes, sixteen
12 MP images after half a second. Nothing here re-measures the open and navigation budgets of
2.2 and 2.3 beyond a regression run (cold start 96 ms to first pixels, 274 ms to the full image;
39 of 39 images at 30 per second with the arrow key held): unchanged.

### 2.5 Measured in M5: difference and metrics

Same machine, corpus and window as 2.4; the machine was in use. Reproduce with `fivbench compare`
(the viewer) and `fivbench metrics` (the computation alone).

**On screen.** The pair is `scene_1_original.jpg` and `scene_3_changed.jpg`, 24 MP each. The rows
of 2.4 were measured again in the same run: every refresh got a frame as before, at 0.15 to
0.26 ms of CPU per frame (0.12 to 0.21 in 2.4); the times to load came out 0 to 60 ms longer
than in 2.4, and differed by as much between four runs on a machine that was in use.

| Mode | All in full on screen | Frames in 2 s | Between frames, mean / longest | CPU per frame, mean / longest |
|---|---|---|---|---|
| difference | 323 ms | 295 | 6.95 / 11.4 ms | 0.19 / 0.54 ms |
| difference, zoomed in, values written into the pixels | 312 ms | 295 | 6.92 / 11.3 ms | 0.81 / 2.48 ms |
| structural similarity (heat map) | 359 ms | 295 | 6.95 / 11.3 ms | 0.17 / 0.58 ms |

The similarity map is computed after the two images are there: it was on screen 1,029 ms after
the start of the process, 670 ms after the images. Until then the pane shows the first image.
Idle in the difference view with the metrics panel open: no CPU time in five of six samples of
5 s, 16 ms in the sixth. Working set 290 MB in the difference view, 391 MB in the similarity
view (the map is 96 MB of float values; its texture with mip levels is 128 MB of video memory,
computed, not measured).

**The numbers**, for two images of 24 MP on all 12 logical cores (median of 5, `fivbench metrics`):

| Pair | Error metrics and histogram | With structural similarity | With its map as well |
|---|---|---|---|
| 8-bit colour JPEGs | 180 ms | 691 ms | 708 ms |
| 16-bit grey PNG against float TIFF | 87 ms | 220 ms | 236 ms |

In the viewer the same work took 190 to 240 ms for the error numbers and 490 to 550 ms more for
the similarity, over seven runs; the error numbers are shown as soon as they are there, the
similarity when it follows. A region of 1,282 x 940 pixels:
13 ms and 38 ms. A half-size image scaled to lie over the original (blended, two passes for the
histogram): 583 ms and 629 ms.

**Saving** the 24 MP pair at full resolution (rendering on all cores, then writing the file; one run):

| File | Render | Write | Size |
|---|---|---|---|
| difference in colours, PNG | 157 ms | 223 ms | 1.1 MB |
| blend, PNG | 392 ms | 912 ms | 48.8 MB |
| difference values, 32-bit float TIFF | 303 ms | 35 ms | 96.0 MB |

**Against the budgets of section 2:**

| Budget | Result |
|---|---|
| Pan / zoom, any compare mode: refresh rate, < 2 ms CPU per frame | every refresh got a frame in both new modes; **0.17–0.19 ms** of CPU per frame. With the values written into the pixels: every refresh, 0.81 ms on average, **2.5 ms in the longest frame — over the budget there** |
| Idle 0 % CPU | none measurable — met |

Regression run of the earlier budgets: cold start 95 ms to first pixels, 278 ms to the full image;
39 of 39 images at 30 per second with the arrow key held; switching between compared images
1.3 ms to `Present`: unchanged.

### 2.6 Measured in M6: aligning

Same machine and corpus; the machine was in use. Reproduce with `fivbench align` (the
registration alone) and `fivbench compare` (the viewer).

**The registration**, on all 12 logical cores, median of 7. The reference is
`scene_1_original.jpg`, 24 MP. The moved images of the first six rows were made from it in
memory, by blending four pixels, so that the pose that lays them back on it is known exactly;
`scene_5_moved.jpg` was made the same way and then compressed. "Off by" is how far the pose
found puts the corners of the image from where the true pose puts them.

| Moved image | Reduced sizes of both images | Aligning | Pose found | Off by | Alike before → after |
|---|---|---|---|---|---|
| moved by 37 and -21 pixels | 22 ms | 199 ms | x 37, y -21 | 0.000 px | 0.8660 → 0.9986 |
| the same, only moving allowed | 21 ms | 154 ms | x 37, y -21 | 0.000 px | 0.8660 → 0.9986 |
| moved by 12.3 and -7.6 pixels | 21 ms | 190 ms | x 12.299, y -7.599 | 0.001 px | 0.9575 → 0.9992 |
| moved, turned by 0.31° | 22 ms | 202 ms | x -18.398, y 9.7, 0.31° | 0.002 px | 0.9364 → 0.9992 |
| moved, turned by -2.5°, 2 % larger | 21 ms | 164 ms | x 64.199, y 31.501, -2.5°, 102 % | 0.002 px | 0.6376 → 0.9991 |
| moved, turned by 25°, 10 % smaller | 22 ms | 135 ms | x -119.999, y 80.001, 25°, 90 % | 0.001 px | 0.1146 → 0.9995 |
| `scene_5_moved.jpg`: x 23.6, y -14.2, 0.75°, 100.4 % | 22 ms | 194 ms | x 23.599, y -14.2, 0.75°, 100.4 % | 0.003 px | 0.8750 → 0.9991 |
| the same, refined from x 20, y -10, 0.6° | 23 ms | 157 ms | x 23.599, y -14.2, 0.75°, 100.4 % | 0.003 px | 0.9601 → 0.9991 |
| the same, by a region of 1500 x 1000 pixels in the middle | 32 ms | 168 ms | x 23.598, y -14.2, 0.7499°, 100.4005 % | 0.008 px in the region | 0.9291 → 0.9997 there |
| the same, refined by a region of 400 x 300 pixels, moving and turning only | 27 ms | 25 ms | x 23.582, y -14.25, 0.7486° | 0.007 px in the region | 0.7778 → 0.9935 there |
| `scene_2_quality50.jpg`: nothing to align | 22 ms | 191 ms | nothing | 0.000 px | 0.9999 → 0.9999 |
| `scene_4_half.jpg`, scaled to fit: nothing to align | 13 ms | 188 ms | x -0.075, y -0.067 | 0.101 px | 1.0000 → 1.0000 |

The turn came out right to the fourth decimal of a degree in every row but the two that went
by a region. Those two are from a later run, in which the other rows took 170 to 250 ms; "off
by" is there taken at the corners of the region, which is where the alignment looked. Outside
it the pose is only as good as the region is large: the turn found from 400 x 300 pixels is
off by 0.0014°, which is 0.09 pixels at the far corner of the image. Two other runs on the
same day gave the same poses and 130 to 275 ms, depending on what else the machine was doing. How alike two 24 MP images are at a given pose, which is what the align
panel shows after every change: 1.3 ms, longest 2.1 ms. The Fourier transform under all of
it, on one core: 1.0 ms for 256 x 256 samples, 4.8 ms for 512 x 512.

Two rows need a word. The recompressed image is found 0.04 and 0.06 pixels from "nothing" and
then taken to be nothing by the rule for whole pixels (section 9, M6); the half-size image is
found 0.1 pixels off and left so. Both are properties of this corpus, not of the method, as far
as could be told: every patch of the image measures about the same small shift (mean 0.034 and
0.047 pixels over 35 patches, spread 0.04 to 0.07), with the files' own luma as with Rec. 709's;
the image that differs from the original by brightness and one square, at the same quality,
measures 0.0000; and an exact picture with noise, compressed at quality 95 and at 50 by the
same encoder, comes out within 0.03 pixels of nothing. The corpus scene builds its fine detail on grids of 2,
4 and 8 pixels that start where JPEG's blocks start, which is the likely reason coarser
quantisation moves that detail a little: an explanation, not something that was proved.

**In the viewer**, from `Shift+A` to the image in its new place, with the reduced sizes made on
the way: 174 to 266 ms over eight runs on 24 MP pairs (8-bit JPEGs, and a 16-bit PNG against a
float TIFF); `Q`, with the reduced sizes already there: 130 and 165 ms. The numbers of the
aligned pair (23.9 of 24 million pixels, one image blended): 577 ms for the errors and 618 ms
for the structural similarity with its map, against 284 and 496 ms for the same pair not
aligned, measured in the same run.

**Nothing else got slower.** The M5 build and this one were run back to back, because the
machine was busier than when 2.4 and 2.5 were measured and both builds came out slower than
those sections say (sets loaded in 340 to 770 ms, a few of the 295 refreshes missed in some
modes). Between the two builds there is no difference beyond the scatter of the runs:

| | M5 build | this build |
|---|---|---|
| Zoom and pan, the eight modes of 2.4: CPU per frame, mean | 0.19–0.38 ms | 0.18–0.31 ms |
| Difference view / with values written into the pixels | 0.25 / 1.21 ms | 0.22 / 0.91 ms |
| The same, longest frame | 0.56 / 5.1 ms | 3.6 / 4.5 ms |
| Similarity view | 0.25 ms | 0.20 ms |
| Switching between compared images, median / p90 | 1.1 / 1.5 ms | 1.0 / 1.1 ms |
| Error numbers of a 24 MP pair (`fivbench metrics`; two runs, five runs) | 179, 185 ms | 180–207 ms |
| With structural similarity | 754, 766 ms | 743–792 ms |

The values written into the pixels stay over the 2 ms budget in their longest frame, as in 2.5.

**Against the exit criteria of M6:** see section 9. The synthetic tests are unit tests on
exact pictures of 640 x 480 (blobs of all sizes, defined at every point, so that nothing is
resampled to make the moved picture): ten poses, corners off by 0.000 to 0.015 pixels, turn by
0 to 0.0012°.

### 2.7 Measured in M8.1: measuring

Same machine and corpus; the machine was in use. Reproduce with `fivbench measure` (the numbers
alone); the viewer's figures are from its `--perf-log` in runs driven by `window-shot.ps1`.

**The numbers**, on all 12 logical cores, median of 7, of 24 MP images (6000 x 4000):

| Image | Area: the whole image | Area: a circle 4000 across, 12.6 million pixels | Area: 1000 x 1000 | Line from corner to corner, 7211 samples |
|---|---|---|---|---|
| JPEG, 8-bit colour | 126 ms | 70 ms | 5.7 ms | 0.82 ms |
| PNG, 16-bit grey | 35 ms | 18 ms | 2.4 ms | 0.36 ms |
| TIFF, float grey | 61 ms | 34 ms | 3.0 ms | 0.36 ms |
| the JPEG turned by 0.5°, so that its pixels are blended | 237 ms | 133 ms | 10.8 ms | 0.51 ms |

An area is gone through once, with sums, smallest and largest value and a histogram for every
channel (five for colour: red, green, blue, alpha, luma); float values twice, because the
range has to be known before the histogram's bins can be laid out. It runs on a worker. The
values along a line are taken on the UI thread when the line or the image changes.

**In the viewer**, window of 1880 x 1000, no folder panel. The numbers of an area follow a
drag: each run was 2 to 7 ms for areas of 0.1 to 1.6 million pixels of one image, 18 ms for
1.2 million pixels of a pair, 40 ms for 1.1 million of a pair of which one image is aligned
and blended. CPU per frame while the view is panned and zoomed with the measure panel open,
some 340 frames each:

| What the panel shows | Mean | Longest frame |
|---|---|---|
| nothing: no measurements, panel closed | 0.14 ms | 0.9 ms |
| the histogram of an area, 16-bit image | 0.26 ms | 2.7 ms |
| the histograms of an area in a pair of 8-bit colour images | 0.45 ms | 2.0 ms |
| the values along a line of 286 pixels, one colour image | 0.50 ms | 1.9 ms |
| the values along a line of 3757 pixels in a pair: six curves | 0.89 ms | 4.5 ms |
| the values along a line of 3830 pixels in a grid of three: nine curves | 0.92 ms | 3.7 ms |
| the histograms of an area in a grid of three: nine of them | 0.46 ms | 2.1 ms |

The row of the pair was 1.11 ms (longest 2.9) before the plot drew one stroke per column and
curve instead of two. The numbers of 4.4 million pixels of each of three images: 78 ms for the
three. The mean is within the 2 ms of section 2 everywhere; the longest frame is over
it in three of the rows, as with the values written into the pixels (2.5). Nothing is drawn
while nothing changes: a run of some fifteen seconds with the panel open, in which three
shapes were drawn, came to 110 frames.

### 2.8 Measured: the bundled decoders

Same machine (12 logical cores). Reproduce with `bench\tools\get-format-samples.ps1` (128 files
from conformance suites, test sets of the libraries and raw.pixls.us) and `fivbench formats`,
which decodes each as the viewer does, times it, and tries the Windows codecs alone on it.
Full decode, and for a raw the JPEG embedded in it, decoded for a 1920 x 1080 view:

| File | Size | Full decode | Preview |
|---|---|---|---|
| HEIC from a phone, a grid of tiles | 4032 x 3024 | 129 ms | none (thumbnail: the one stored in the file, 10 ms) |
| HIF from a camera, 10-bit 4:2:2 | 4240 x 2832 | 428 ms | none |
| HIF from a camera, 10-bit, one coded picture | 4672 x 7008 | 2.3 s | none |
| AVIF 10-bit | 1204 x 800 | 33 ms | none |
| AVIF, a grid of 5 x 4 | 6400 x 2880 | 227 ms | none |
| JPEG XL, lossy, progressive | 4064 x 2704 | 168 ms | none |
| WebP, lossy | 550 x 368 | 3 ms | none |
| Canon CR3 | 4319 x 2879 | 672 ms | 1620 x 1080, 7 ms |
| Nikon NEF, lossless compressed | 5600 x 3728 | 1.28 s | 1392 x 928, 37 ms |
| Sony ARW | 4692 x 3132 | 0.78 s | 1152 x 768, 44 ms |
| Fujifilm RAF, X-Trans | 6264 x 4176 | 2.10 s | 1104 x 736, 50 ms |
| Olympus ORF | 5240 x 3912 | 1.48 s | 1600 x 1200, 17 ms |

Raws are demosaiced by LibRaw with AHD (X-Trans: Markesteijn 1-pass) on all cores through
OpenMP. On one core, as first built, the RAF took 15.6 s and the NEF 2.9 s; 3-pass X-Trans on
all cores takes 3.3 s. The 33 MP HIF is one HEVC picture without tiles or wavefronts: libde265
decodes it on one thread whatever it is given. In the viewer, the RAF's embedded JPEG was on
screen 294 ms after the start and its own pixels at 2.24 s.

**What the Windows codecs make of the same files** on this machine, which has the HEIF, AV1,
WebP, raw and JPEG XL extensions installed: HEIC and AVIF open, except 12-bit HEIF, a gray
10-bit HEIF and a HEIF that is only an image sequence; a 10-bit AVIF with HDR metadata comes out
over-saturated. JPEG XL does not open at all ("component initialization has failed"), though
its decoder is registered. Raws open as the camera's JPEG in 8 bits, a DJI DNG as its 960 x 720
preview. TIFF: 10-, 14-, 24-bit, 64-bit float, LogLuv and uncompressed YCbCr fail; 16-bit
float is read as integers and 32-bit integers as noise, both without an error. 12-bit and
16-bit lossless JPEG fail.

### 2.9 Measured: the 3D height map

Same machine and corpus. Reproduce with `fivbench surface` (the height grid alone); the
viewer's figures are from its `--perf-log` in runs driven by `window-shot.ps1`, window of 1880 x
1000, no panels.

**The height grid**, on all 12 logical cores, median of 15: the image reduced to 2048 x 1365
values by averaging, then smoothed (three box blurs of the radius given, along the rows and down
the columns), then its mip levels. This is what passes between the click and the start of the
turn, with the upload of 11 MB to the GPU after it.

| Image | Smoothing 0 | 2 | 8 | 20 | Mip levels |
|---|---|---|---|---|---|
| 24 MP JPEG, 8-bit colour | 33 ms | 45 ms | 44 ms | 44 ms | 2.5 ms |
| 24 MP PNG, 16-bit grey | 20 ms | 37 ms | 38 ms | 38 ms | 1.4 ms |
| 24 MP TIFF, float grey | 22 ms | 38 ms | 38 ms | 39 ms | 1.3 ms |
| 50 MP JPEG, 8-bit colour | 42 ms | 59 ms | 60 ms | 60 ms | 1.4 ms |

The smoothing costs the same at any radius (running sums), some 17 ms, most of it the two
passes that turn the grid over so that the columns are blurred as rows.

**In the viewer**: the 24 MP JPEG turned into 3D and back with the finest mesh (2048 cells on
the long side, 5.6 million triangles): 216 frames, one every 7.0 ms on average (the screen's
refresh), the longest interval 29.4 ms, which is the first frame of the 3D view, where the
renderer is made. CPU per frame 0.14 ms on average and 1.2 ms at most, as in 2D: the surface
costs the GPU, not the CPU.

**While comparing**, the same turn in and out, 24 MP images: a grid of four (three of 6000 x 4000,
one of 3000 x 2000), each its own surface, 227 frames, one every 6.97 ms, the longest interval
16.7 ms; the difference of a pair, drawn flat into its canvas of 2048 x 1365 and raised in every
frame, 214 frames, one every 7.03 ms, the longest 19.5 ms. CPU per frame 0.19 and 0.22 ms on
average, 1.0 and 4.4 ms at most (the first frame of the difference, which makes the canvas).

## 3. Technology

### 3.1 Language: C#, C++, Rust or Go?

Short answer: **the language changes user-visible speed by a few milliseconds; the design in
sections 5–7 changes it by orders of magnitude.** In every language the heavy work is done by
the same things — libjpeg-turbo's hand-written SIMD, Windows' directory syscalls, and the GPU.

| | C# / .NET 10 Native AOT | C++ (MSVC) | Rust | Go |
|---|---|---|---|---|
| Startup overhead (estimate) | 5–15 ms | 1–3 ms | 1–3 ms | 3–10 ms |
| JPEG decode | libjpeg-turbo via P/Invoke — same machine code | libjpeg-turbo, direct | `turbojpeg` crate (same code) or pure-Rust `zune-jpeg` (comparable speed, but no reduced-size decode as far as I know) | built-in decoder is several times slower; fast only through cgo |
| Rendering | Direct3D 11 through generated bindings | Direct3D 11, native | `wgpu` (portable, slower to initialise) or D3D11 via the `windows` crate | weak: OpenGL through cgo, immature GPU bindings |
| UI kits | Dear ImGui bindings; Avalonia | Dear ImGui first-party with official Win32 + D3D11 backends; Qt | egui (mature, immediate-mode), Slint, iced | Fyne, Gio, ImGui wrapper — fewer, and weaker with 100k-item lists |
| SIMD for metrics / resize | explicit AVX2 intrinsics; almost no auto-vectorisation | intrinsics and auto-vectorisation | same as C++, plus ready-made crates (`fast_image_resize`, `rustfft`, `rayon`) | no stable intrinsics; assembly or cgo |
| Safety net | memory-safe outside `unsafe`; GC, kept away from pixel data | none; needs sanitizers and tests | memory and thread safety checked at compile time | memory-safe; GC |
| Integration risk for this app | bindings must work under Native AOT (checked in M0) | lowest: every dependency is native C/C++ | cold-start time of wgpu/egui unproven against our budget | high |
| Installed here | SDK 10.0.203 ✔ | MSVC Build Tools 2022 ✔ | no | no |
| Same language as your other projects | yes | no | no | no |

- **Go: no.** It is the only one of the four that would be slower, and it has the weakest GPU,
  SIMD and UI story.
- **Rust: a good choice, but not a faster one.** Its imaging libraries are the best of the four
  and it removes whole classes of threading bugs. The cost is a new language and toolchain, and
  its usual GUI/GPU stack (egui + wgpu) is not known to start as fast as raw D3D11.
- **C++: the most direct choice.** The stack below is native C/C++ at every layer, so C++ needs
  no bindings at all and is what the fastest existing viewers use. The cost is no safety net
  around a heavily multi-threaded decode pipeline, slower builds, and a codebase outside your
  C# projects.
- **C# / .NET 10 Native AOT: recommended.** It should land within roughly 10 ms of C++ on every
  budget above, in the language of all your other projects.

**Decided 2026-10-09: C# / .NET 10 Native AOT.** The M0 bake-off ran the same program (create
window, create D3D11 device, decode one JPEG on a second thread, present it, exit) in C# Native
AOT and in C++, 50 fresh-process runs each: 232 ms against 234 ms from process spawn to first
present, with C# 3 ms slower to reach `main()` and equal everywhere else (table in section 2.1).
The two programs are kept in `bench/bakeoff` and can be re-run with `fivbench bakeoff`.

### 3.2 Stack (same in C# and C++)

| Layer | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (current LTS), C# 14, x64, published with **Native AOT** | single native exe, no JIT, millisecond startup |
| Window | raw Win32 window and message loop (CsWin32 source-generated P/Invoke) | nothing between us and the OS; no framework startup cost |
| Rendering | **Direct3D 11**, DXGI flip-model swap chain, HLSL shaders compiled at build time | zoom, pan and every compare mode run on the GPU; D3D12 would add complexity, not speed |
| UI | **Dear ImGui** (docking), drawn in the same D3D11 pass as the image | fastest startup and frame time of any option; virtualised lists of 100k rows are trivial; well suited to dense tool UIs |
| JPEG | **libjpeg-turbo 3**, statically linked | fastest CPU JPEG decoder; can decode straight to 1/2, 1/4, 1/8 size |
| Other formats | **Windows Imaging Component (WIC)** | built in; PNG, TIFF incl. 16-bit and float, BMP, GIF, ICO, JPEG XR |
| HEIF/HEIC, AVIF, WebP, JPEG XL, camera raw; TIFFs WIC gets wrong | **libheif** (libde265, dav1d), **libwebp**, **libjxl**, **LibRaw**, **libtiff**, in `fivcodecs.dll` | the Windows codecs for these are Store extensions, on some machines and not on others (added after M8.1, section 9) |
| Faster PNG / WebP | add native decoders (libspng or wuffs, libwebp) **only if** the M0 benchmark shows WIC is the bottleneck | no dependency without a measured win |
| CPU maths | AVX2 intrinsics (`Vector256`), `TensorPrimitives` | metrics, min/max, downscale |
| Threading | fixed pool of decode workers fed by priority queues | visible-first ordering and cancellation |
| Storage | own thumbnail pack files (memory-mapped) in `%LOCALAPPDATA%`; settings as JSON (source-generated serializer) | no database dependency |
| Tests / bench | xUnit, BenchmarkDotNet, own end-to-end timing harness | |

UI alternative, if the custom-drawn technical look of ImGui is not wanted: Avalonia with a GPU
viewport. It gives conventional controls and XAML, at an estimated 150–300 ms extra startup.
Only the app layer would differ.

### 3.3 Toolchain on this machine

Present: .NET SDK 10.0.203, MSVC Build Tools 2022 (required for Native AOT linking and for C++),
Windows SDK 10.0.26100 with `fxc`/`dxc` shader compilers, CMake 3.29, NASM 2.16 (the last two
build libjpeg-turbo with SIMD). Missing: libjpeg-turbo itself (build from source in M0), vcpkg,
Rust, Go.

## 4. Architecture

### 4.1 Rules

1. **The UI thread never does I/O or decoding.**
2. **Show the best available picture immediately, then refine it in place:** cached thumbnail →
   reduced-size decode → full resolution.
3. **One copy of the pixels.** Decode straight into a native buffer in the format the GPU takes.
   16-bit and float data stay 16-bit and float; brightness window and gamma are applied in the
   shader, so there is no CPU conversion pass.
4. **Everything is cancellable.** Work for an image the user has already left is dropped.
5. **No per-frame and no per-file heap allocation.** Pixel memory is pooled native memory; the
   folder index is a few flat arrays.
6. **Every stage is timed.** A built-in perf overlay (F12) shows read, decode, upload and present
   times, and enumerate / sort / thumbnails-per-second for the index.

### 4.2 Layout

```
FastImageViewer2/
  PLAN.md
  FastImageViewer2.slnx
  src/
    Fiv.Interop/   Win32, Direct3D and WIC bindings generated by CsWin32, shared by the others
    Fiv.Core/      folder index, decoders, caches, scheduler, metrics, registration, measuring (no UI, no GPU)
    Fiv.Render/    D3D11 device, tiled textures, thumbnail atlas, viewport + compare renderer
    Fiv.App/       Win32 window, single instance, ImGui panels, input, settings -> FastImageViewer2.exe
    Fiv.Cli/       headless compare for scripts (M7)
  shaders/         *.hlsl, compiled to bytecode at build
  native/          libjpeg-turbo build script; fivjpeg, the cancellable JPEG decode shim (C)
  tests/Fiv.Tests/
  bench/Fiv.Bench/ decoder and index benchmarks, time-to-first-pixel harness, test-corpus generator
```

## 5. Design: fast open

```
process start
 ├─ instance already running? ── send path to its window (WM_COPYDATA) ── exit
 └─ otherwise, in parallel:
      thread A: create window, D3D11 device, swap chain, UI
      thread B: read file ─ probe header ─ decode preview
    join ─ upload ─ present ─ decode full resolution ─ swap in ─ prefetch neighbours
```

- **Single instance.** A second launch only forwards its arguments and exits, so with the viewer
  running the cost of "open" is process spawn plus decode. Optional setting: stay resident in the
  tray after closing, so every open takes the warm path.
- **Two-stage JPEG decode.** Fit-to-window on a 1440p or 4K screen needs far fewer pixels than a
  24–50 MP file holds. libjpeg-turbo decodes directly to the smallest of 1/1, 1/2, 1/4, 1/8 that
  still covers the viewport — measured 1.5–2× faster than a full decode — while the
  full-resolution decode runs on a second thread at the same time.
- **First frame without the GPU.** Creating the D3D11 device takes about 190 ms here, longer than
  decoding the preview. The device is created on its own thread; if the preview is ready first it
  is painted with GDI, and Direct3D takes the window over when it is up.
- **No conversions.** Decoders write BGRA8, R8, R16 or RGBA16/float directly; EXIF orientation is
  a transform in the shader, not a pixel shuffle.
- **Upload.** Images become tiled textures (tiles ≤ 4096 px, overlapping by 64) so files beyond
  the 16384 px D3D11 limit work from day one, and so that no single texture creation holds the
  device lock for long. The mip levels are computed on the decode thread and the textures are
  created complete on an upload thread; the UI thread never copies pixels.
- **Decode pool.** Up to eight workers take the most urgent job first: the preview of the image
  the user is at, then its full image, then the neighbours. Work that is no longer wanted is
  cancelled, a JPEG decode within a few rows (through a small C shim over libjpeg's scanline
  API, `native/fivjpeg`).
- **Prefetch and cache.** Standing still, the neighbours wait for the target: first the target
  alone, then previews of the next two images in the direction of travel and of the previous
  one, then the full images of the two direct neighbours. Everything decoded stays in a
  least-recently-used cache with byte budgets (4 GB RAM, 2 GB VRAM; `--cache-mb`, `--vram-mb`).
- **Rapid navigation.** While a key is held, only previews are decoded, for the target and five
  neighbours, four of them ahead. The picture on screen changes when the next one can be drawn
  and never goes back, so every image decoded in time is seen and nothing flashes empty.
  Full-resolution work resumes 150 ms after the last step.
- **Pixel readout** comes from the CPU copy of the image — a constant-time lookup returning the
  true 8/16-bit/float values.

## 6. Design: fast index

- **Enumeration** reads the directory in large buffers and takes name, size and timestamps from
  the directory entries themselves — no per-file open or stat. On a network share that is one
  round trip per few hundred files instead of one per file.
- **Index layout:** flat arrays (name arena + offsets, sizes, times, dimensions, flags) and an
  integer permutation for the sort order. No object per file; 100,000 files cost a few MB.
- **Sorting:** natural order ("img2" before "img10") with precomputed keys, parallel above ~50k
  entries; by size / date / type / dimensions on demand. Filtering produces an index view, never
  a copy.
- **Opening a file indexes its folder** in the background and positions on that file, so
  next/previous works immediately.
- **Dimensions** come from small header parsers (JPEG, PNG, TIFF, BMP, GIF, WebP) that read only
  the first kilobytes, run lazily and visible-rows-first.
- **Thumbnails**, cheapest source first:
  1. the thumbnail cache;
  2. the thumbnail embedded in the JPEG's EXIF block (no image decode at all);
  3. a 1/8-size JPEG decode;
  4. full decode plus SIMD down-scale (PNG, TIFF, …).

  Work is ordered visible rows → rows about to scroll in → the rest, across all cores, and
  re-ordered when the user scrolls.
- **Thumbnail cache:** one pack file per folder under `%LOCALAPPDATA%\FastImageViewer2\thumbs`,
  holding 256 px JPEG thumbnails plus each file's dimensions, validated by size and modified
  time. Re-opening a folder is one sequential read. Total cache size is capped.
- **On the GPU** thumbnails live in a texture atlas, so a full grid is a handful of draw calls.
- **Change tracking:** a file-system watcher notices changes; 400 ms after the first one the
  folder is read again in the background and the index replaced. (Re-reading costs 50 ms for
  100,000 files, so applying changes one by one was not worth its complexity.) A file whose size
  or time changed is reloaded; if the current file is deleted, the one that takes its place is shown.

## 7. Design: rendering and compare pipeline

Each image is drawn with its own transform (pan, zoom, alignment offset / rotation / scale) into
a screen-sized intermediate target; a final pass combines the intermediates according to the
compare mode and applies the shared display controls and colormap. Tiling and alignment are
therefore independent of the compare maths, and adding a compare mode means adding a few lines
of shader. Every mode runs at full resolution in real time.

What is on screen is for looking; **numbers (metrics, statistics) are always computed on the CPU
from the full-resolution native pixel values**, never from the zoomed display.

Sampling: exact pixels when zoomed in (with optional pixel grid and per-pixel value labels),
mipmapped filtering when zoomed out, optional bicubic.

**As built in M4:**

- **The canvas.** The images of a compare set share an area as wide as the widest and as tall as
  the tallest of them. Zoom and position are kept in canvas coordinates, so one view state moves
  every pane alike, and the canvas point under the cursor in one pane is the linked cursor's
  place in the others. Each image has a placement on the canvas (top-left, centred, or scaled to
  fit); the alignment transform of M6 goes in the same place.
- **Panes** (single, side by side, grid) draw each image straight into the back buffer, clipped
  to its pane, with the same shader as a single image.
- **Two images in one pane** (split, blend, checkerboard) go through the intermediates: each
  image is drawn under its own transform into a pane-sized target of 32-bit float per channel
  that holds the stored sample values, alpha below zero meaning "no pixel here"; `combine.hlsl`
  reads both and decides per screen pixel. The targets exist only while such a mode is in use
  (2 × 16 bytes per pixel of the view: about 90 MB at 2160×1185).
- **One set of display controls** (`display.hlsli`: window, gamma, exposure, channel) is included
  by both shaders, so every pane and every mode shows the same values the same way.

**As built in M5:**

- **Difference and similarity are two more modes of the combine pass.** For the difference,
  `combine.hlsl` takes the second image less the first from the two float targets, per screen
  pixel: of one channel, of luma, or, for colour, of the channel that differs most, so that a
  change in one channel is never averaged away. Kind (absolute, signed, squared, relative) and
  gain turn it into a position on a colour map, which is a texture of 256 entries. The gain is
  relative to the span between the display's black and white: at gain 1 a difference of the
  whole range reaches the end of the map.
- **One definition, two users.** `Fiv.Comparing` (`DifferenceMath`, `Colormaps`, `CompareExport`)
  holds the same formulas and the same colour tables on the CPU. The saved files are computed
  there, from the full-resolution pixels, and come out in the colours that were on screen.
- **The numbers never come from the screen.** `CompareMetrics` reads both images row by row in
  canvas coordinates (`CompareSource`: any layout, any orientation; exactly one stored pixel per
  canvas pixel where the placement moves whole pixels, the four nearest blended where it
  scales), in bands on all cores, on a worker. Differences are in the units of the files when
  both have the same depth, on a scale of 0..1 when they differ. For 8- and 16-bit pairs the
  histogram counts every whole difference, so "share over the threshold" is exact and follows
  the threshold without recomputing. Structural similarity is the authors' own: 11 x 11 Gaussian
  window, mean over where it fits, worked in tiles and in double precision (in 32-bit float a
  flat area near black or white was wrong in the fourth decimal place).
- **The similarity map** is a float image like any other: mip levels, a texture, drawn into the
  first intermediate target under the canvas transform, and coloured by the combine pass.
- **On screen, a zoomed-out difference is the difference of averaged pixels.** Each image is
  filtered down to the screen before the two are subtracted, so differences of single pixels
  fade when many pixels share one screen pixel. At 100 % and above it is exact.

**As built in M6:**

- **A pose per image.** Every image of a set can be moved, turned and scaled about its centre
  (`AlignPose`). The pose is the last factor of the image's transform to the canvas, after
  orientation and placement, and nothing downstream had to learn about alignment: the panes,
  the two float targets of the combine pass, the linked cursor, the numbers and the saved files
  all go through the canvas. On screen an aligned image costs what any image costs.
- **Whole pixels stay whole.** A pose that only moves, by whole pixels, leaves a transform that
  takes pixels to pixels, and such a pair is compared exactly, as before. Any other pose blends
  the four nearest pixels, on screen (at 100 % and below) and in the numbers alike.
- **A turned image does not cover a rectangle.** `CompareSource` gives, row by row, the run of
  canvas pixels whose centres the image covers: one run, because an image is a convex shape.
  The error numbers count the pixels both images cover. Structural similarity takes its mean
  over the positions whose whole window both cover (what both cover is convex too, so a window
  is inside if its top and its bottom row are) and its map says "the same" elsewhere; the
  saved views are empty where one image is not.
- **Automatic alignment works on brightness, from coarse to fine** (`Registration`). Each image
  gets its brightness at a half, a quarter, an eighth and so on of its size (`LumaPyramid`),
  which whatever follows samples at the step it works at.
  A first estimate comes from the two pictures as a whole, at 512 samples across: the
  magnitudes of their Fourier spectra do not depend on where the pictures lie, and laid out by
  angle and by the logarithm of the radius a turn and a scaling become two shifts, which phase
  correlation finds; with those undone, a second phase correlation gives the move. Each
  picture is faded out towards its own edges, so that its outline does not pass for content.
  The spectra cannot tell a turn from the same turn plus half a turn, so the move is worked
  out for both, and for the image as it is: the clearest peak wins.
  The estimate is then refined at a sixteenth, an eighth, ... of the size up to full size: at
  each size the shift that is left is measured in up to 6 x 6 patches of up to 256 samples,
  and the move, turn and scaling that explains those shifts best (least squares, clear peaks
  counting for more, patches that disagree left out) is taken off, until nothing is left at
  that size. Patches of the last step are 256 pixels of the full image.
- **By a region, only what is inside it counts.** The alignment can be given a rectangle of
  the canvas: the first estimate is then made from what the two images show in it (faded out
  towards the rectangle's edges, the same fade for both, in place of each image's own), the
  patches of the refinement lie inside it, and "how alike" is taken over it. The pose found is
  still one pose for the whole image. What a region can make up for is about a quarter of its
  own size; what it says about turn and scaling goes with its size too.
- **Phase correlation with a Gaussian weight over frequency.** The weight leaves out the finest
  detail (noise, compression, what resampling does) and makes the peak a Gaussian, whose place
  between samples follows exactly from three of them. The patches' samples lie on whole canvas
  pixels, so that a reference that lies pixel for pixel on the canvas is read, not
  interpolated: interpolating both images at different fractions was worth up to 0.02 pixels
  of error.
- **The Fourier transform is the project's own** (`Fft2D`): radix 2, along the rows as in the
  textbook, down the columns with whole rows as elements, which the vector types do eight
  values at a time. 512 x 512 samples in 5 ms on one core.
- **A result is taken over only if it does not make the two images less alike.** How alike is
  the correlation of their brightness where they overlap, at about 640 samples across
  (`Registration.Score`): cheap enough to follow every change of the pose in the align panel,
  blind to brightness and contrast, and to anything below a pixel or so. The work runs on a
  worker with the full-resolution pixels; the reduced sizes are kept per image while it is in
  the set (32 MB for 24 MP).

**As built after the 3D height map: every image at once.**

- **The pair gave way to the images shown and a reference** (`CompareSelection`). Every image
  of the set is in the views unless taken out, and one is the reference; the views take the
  images shown beginning with it and going on round the set. With two images and the first as
  the reference this is the pair as it was, and making the other the reference is the old swap.
- **Split, blend and checkerboard take any number of images through one target**, a pass each:
  a pass draws its image's strip or its cells and discards the rest, or for the blend is laid
  over what the passes before it drew by its opacity, which the blend unit applies through the
  blend factor (to the 3D canvas's heights as well, where 32-bit float targets blend). The video
  memory is that of one float target whatever the number of images; the difference and the
  similarity, which read two, make the second one only when they are used. The checkerboard's
  cells go round the images so that no two neighbours, also across a corner from four images
  on, show the same one (`CompareGeometry.CheckerLayer`, the shader and the saved file alike).
- **Difference and SSIM are a pane for each image against the reference**, laid out as the grid
  is. The numbers and the similarity maps are kept per pair and made one pair at a time (each
  uses all cores), the active image's first; those of pairs no longer shown are let go of.
- **The overlay is the blend's passes with other opacities.** Each image has an opacity of its
  own, and the overlay is meant to be the same in any order: each image counts by its opacity
  (sum of a_k c_k over the sum of a_k), and together they cover what is behind them as layers of
  those opacities would, 1 - (1 - a_1)(1 - a_2)... Drawn as layer over layer, the pass of image k
  needs the opacity a_k P / (S - P A_k), where S is the sum of the opacities, P what they cover
  and A_k the sum of those above it (`OverlayMix.PassOpacities`); the first pass lies over the
  backdrop by its own opacity, which the shader does, the others are the blend unit's. Exact
  where every image has a pixel; the saved file is worked out pixel by pixel from the images
  there, with what they cover as its alpha.
- **Aligning moves the ticked images onto the reference**: drags and keys change all of their
  poses as one change, which one undo takes back; the automatic alignment goes through them
  one after the other, each taken over or not on its own, as for one image before.

## 8. Feature inventory of FastImageViewer → where it lands

| Old feature | In FastImageViewer2 |
|---|---|
| Open files / folder, drag and drop, command line, single instance | M1 |
| Formats JPG, PNG, TIFF, BMP, GIF, WebP, ICO; 16-bit and float PNG/TIFF with raw values | M1; HEIC/AVIF/RAW at first only where the Windows codecs were installed, then with decoders of its own (after M8.1), with JPEG XL and 12-bit JPEG |
| Zoom, pan, fit, 100 %, interpolation toggle, fullscreen, keep zoom/pan across images | M1 |
| Pixel coordinates and values under the cursor, status bar | M1 |
| Folder panel: thumbnails; name, size, dimensions, modified columns; natural sort; auto-update | M2 (index, navigation), M3 (thumbnails, panel) |
| Name / size / dimension / date filters; select all / invert | M3 |
| File operations: show in Explorer, copy path / name / image, rename, delete, move, copy to, export list | M3 (basic set), rest M8 |
| Tabs / open-files list, recent files, settings | M3 |
| Layouts single / 2 columns / 2×2, synced zoom/pan, synced anchor point, filename overlay | M4 |
| Overlay: per-image opacity; blend modes Normal, Multiply, Screen, Overlay, Difference, False-Color Diff; swap A/B; colour bar | M4 (opacity), M5 (difference, false colour) |
| False-colour schemes Jet, Thermal, Rainbow, Magenta-Cyan, Red-Blue | M5, plus Viridis, Turbo, Coolwarm |
| Overlay transform: translate / rotate / scale by slider, number, drag; step sizes 0.1 / 1 / 10 | M6: numbers with step buttons, drag, keys and wheel, in steps of 1 and 0.1; no sliders |
| Auto-align and fine-align (image registration) | M6, new algorithm (section 7) |
| Low-resolution "preview mode" for overlays | not needed — full resolution is real-time |
| Quality metrics (sharpness, contrast, edge %) as overlay and sortable columns; "analyze all" | M8 |
| Line / circle / rectangle measurements, measurement panel, calibration | M8.1: all three, the panel, and the scale of lengths; the calibration of values (a colour scale read as numbers) is not carried over |
| Line profile with CSV export | M8.1, with the profiles of A and B in one plot |
| Crosshair with 8 styles | M4 (linked cursor), styles M8 |
| Image information / EXIF, high-bit-depth info | M8 |
| Save with annotations, save viewport, screen capture | M5 (export view / diff), rest M8 |
| Rotate / flip / crop, filters, false-colour LUT, per-image undo history, overwrite original | M8, on request |
| Image analysis (histogram, statistics, FFT), segmentation, 3D surface plot | histogram and statistics of a rectangle or a circle: M8.1 (the difference histogram is in M5); the 3D surface plot as the 3D height map (after M8.1, section 9); the rest not planned |
| OCR / barcode reader, Lua scripting with editor and debugger | not planned; M7's command line covers automation of comparisons |

`USER_GUIDE.md` also describes angle measurement, batch processing, a pixel inspector, histogram
equalisation and colour-space conversion. I did not find these in the old app's menus, so I have
treated them as unconfirmed; the pixel inspector exists in M5 regardless.

## 9. Milestones

Each milestone ends with its exit criteria measured and written back into this file.

**M0 — Bake-off, skeleton, measurement**
- `git init`; solution skeleton; build libjpeg-turbo static library.
- Language bake-off described in 3.1 (C# Native AOT vs C++). Decide and record.
- Test corpus: generated folders of 1k / 10k / 100k files (removed again on 2026-10-10, see
  section 2.3); JPEG at 12 / 24 / 50 MP; PNG 8- and
  16-bit; TIFF 16-bit and float; WebP; one image wider than 16384 px. Plus a real folder of yours.
- Time-to-first-pixel harness; decoder benchmarks (libjpeg-turbo at each scale, WIC per format,
  candidate PNG decoders); enumeration and sort benchmarks.
- Baseline: time the old FastImageViewer on the same corpus.
- Exit: language and UI toolkit fixed; section 2 rewritten with measured numbers.

*Closed 2026-10-09.* Done: repository, libjpeg-turbo 3.2.0 built (331 of its 331 regression tests
pass), bake-off, corpus generator, decoder benchmarks, language decision. **Not done, moved:**
enumeration and sort benchmarks (to M2, with the index), Dear ImGui under Native AOT (to M1, with
the first UI), time-to-first-pixel harness that watches the screen and the old-app baseline (to
M1's exit measurement), WebP in the corpus (Windows has no WebP encoder), a folder of real camera
files (needed from you: the synthetic corpus has no embedded thumbnails or previews).

**M1 — Ultra-fast open**
- Window, device, swap chain, render-on-demand loop, perf overlay.
- JPEG and WIC decoders, two-stage loading, EXIF orientation, tiled textures with mipmaps.
- Viewport: cursor-anchored zoom, pan, fit, 100 %, sampling modes, transparency checkerboard,
  brightness window / gamma for 16-bit and float.
- Parallel startup, single instance with forwarding, drag and drop, multiple files on the
  command line, pixel readout, status bar.
- Exit: open budgets met; 50 MP JPEG and the >16k image pan and zoom at refresh rate.

*State 2026-10-09: exit criteria measured and met (section 2.2).* Built and checked on screen:
parallel start-up with the GDI first frame and Direct3D takeover, two-stage JPEG loading, WIC for
the other formats, EXIF orientation (also in the GDI frame), tiled textures with CPU-built mip
chains, cursor-anchored zoom, pan, fit, 100 %, exact-pixel and smooth sampling, brightness window
and gamma with auto range for 16-bit and float, pixel readout, status bar, F12 timing overlay,
fullscreen, several files on the command line with next/previous, single instance with
hand-over, error display. Dear ImGui runs under Native AOT through Hexa.NET.ImGui; its two
native DLLs sit next to the exe, so the viewer is three files, not one.
**Written but not tested:** drag and drop, the transparency checkerboard (no image with alpha in
the corpus), moving the window between monitors of different DPI. **Not done:** the faster PNG
decoder (PNG is the slowest open, 352 ms for 24 MP), an Open dialog, recovery from a lost
graphics device, the screen-watching time-to-first-pixel harness and the old-app baseline (the
numbers above are the viewer's own timestamps, taken when `Present` returns).

Found while building it, for M2:

- A full-resolution upload holds Direct3D's device lock for 50–90 ms; anything else that creates
  a texture meanwhile waits. During fast navigation that would stall the next preview. Upload in
  smaller tiles, and do not start an upload for an image that is no longer current.
- A superseded decode runs to its end (cancellation is between stages only). Cancelling between
  scanlines needs libjpeg's scanline API instead of the one-call TurboJPEG API.

**M2 — Ultra-fast index and navigation**
- Folder index, natural sort, header probes, file-system watcher.
- Next / previous / first / last, prefetch, decoded-image cache, cancellation.
- Exit: index budgets met; held-arrow-key budget met.

*Closed 2026-10-09: exit criteria measured and met (section 2.3).* Built: the folder index
(directory read without opening files, natural sort, sorting by date, size and type, filtered
views over the same listing), the decode pool with priorities and cancellation, the cancellable
JPEG decoder, tiled and cancellable uploads on their own thread, the image cache with RAM and
VRAM budgets, staged prefetch, in-order display while a key is held, the folder watcher, header
probes for six formats, and 85 unit tests (`dotnet test`), which found one real bug on their
first run (the PNG signature in the header probe).
Checked in the running viewer: stepping and holding the key through 40 images; a mixed folder
of 15 files up to 80 MP, 16-bit and float; files added, the current file deleted, a file replaced
while shown; sorting by date.
**Left open:**

- Header probes are written, tested and timed, but nothing shows dimensions yet and sorting by
  dimensions does not exist; both need the file list of M3, where the probes will be run lazily.
- Filtered views have no user interface yet (M3: type-to-filter).
- The cache evicts whole images. Dropping only the textures of an image whose pixels are still
  in RAM would use video memory better.
- Not measured: a real network share, a disk-cold folder (after a reboot), HDD.
- `Present` blocks the UI thread until the previous frame is shown. Rendering only when the swap
  chain is ready for a frame (a waitable swap chain) would remove that and cut input latency by
  up to one refresh; worth doing before the compare modes, where smooth interaction matters most.
- Stepping through very large or progressive JPEGs with the key held skips or delays some of
  them (section 2.3). Real camera files carry embedded previews that would avoid the decode.

**M3 — Thumbnails and browser**
- Thumbnail pipeline, cache pack files, GPU atlas.
- Browser panel: virtualised grid and details list, column sort, type-to-filter, multi-select,
  thumbnail size, filmstrip.
- Basic file operations, settings, recent items, key map, docking layout, fullscreen.
- Exit: thumbnail budgets met; scrolling a 100k folder stays at refresh rate while thumbnails
  stream in.

*State 2026-10-10: built and measured; three of four exit budgets met, some items untested.*
Reproduce with `fivbench browse` and `fivbench thumbs`. The scrolling row and the 95,000-image
listing time cannot be re-run: that part of `fivbench browse` was removed on 2026-10-10 with the
generated 100k folder (see section 2.3).

| Budget | Result |
|---|---|
| First screen of thumbnails, folder seen before ≤ 100 ms | **4–18 ms** (21 thumbnails of 128 px, from the pack on disk) — met |
| First screen, new folder of camera JPEGs ≤ 300 ms | **231–260 ms** for 21 thumbnails of 12 MP JPEGs — met at this panel size; a screen with more thumbnails takes proportionally longer |
| Thumbnail generation ≥ 300 images/s | **213/s** from 12 MP JPEGs on 10 threads — not met; 7,148/s when the camera's embedded thumbnail is shown first as a stand-in |
| Scrolling a 100k folder at refresh rate while thumbnails stream in | **6.95 ms between frames (144 Hz), longest 11.8 ms**, 0.18 ms CPU per frame, 366 thumbnails made on the way — met |

Also measured: a folder of 95,000 images is listed 52 ms after it is asked for; a pack of 2,000
thumbnails opens in 0.7 ms and is 27.5 MB; with the folder panel open, cold start (first pixels
99 ms, full 286 ms) and navigation (39 of 39 images at 30/s, 1.2 ms per step) are unchanged.

Built: thumbnails from the cheapest source (pack, embedded EXIF thumbnail, reduced JPEG decode,
WIC), one pack per folder under the profile's `thumbs` with a size cap, a GPU atlas at display
size, low-priority thumbnail workers whose requests are cancelled when scrolled away; ImGui
docking with the image in the centre; folder panel with virtualised grid and sortable details
list, filter by name, thumbnail size, subfolders and up; filmstrip; selection (click, Ctrl,
Shift, Ctrl+A, Ctrl+I); opening folders; right-click menu with recent files and folders; Open
dialogs; rename, move to Recycle Bin, copy image / path / name, show in Explorer; settings
(window position, panels, sort, cache sizes) in `settings.json`; key list (F1); `--profile`.
Checked in the running viewer: grid, list, filter, filmstrip, mixed formats and rotated
thumbnails, first-run layout, rename, delete, saved settings. Found and fixed on the way: a
crash when switching folders, text with a per-cent sign being read as a format string, Enter
not confirming the delete dialog.
**Written but not tested:** Open dialogs, copy image to clipboard, the Settings window, recent
menus, restoring a maximised window, drag and drop, sorting by dimensions, multi-select with
Shift, the list of several opened files. **Not done:** unit tests for `Settings`; filters other
than by name; reading settings costs about 4 ms of every cold start (12.3 → 16.5 ms) and should
be made cheaper or later; opening a 95,000-image folder has one frame of about 10 ms; sorting
by dimensions only uses the dimensions known so far; key bindings are fixed, not configurable;
tabs for several open folders.

**M4 — Compare I: views and switching**
- Compare set of 2…N images from selection, drag-in or command line.
- Layouts 1-up, 2-up, grid; linked zoom/pan with toggle; linked cursor with per-image readout.
- Flicker: keys 1–9 switch image in place, hold-to-peek, auto-blink at a set rate.
- Split / wipe with draggable divider; opacity blend; checkerboard interleave.
- Display controls applied identically to all images: exposure, gamma, window/level, channel
  isolation (R, G, B, A, luma).
- Different sizes: pixel-aligned (top-left or centre) or scaled to fit.

*State 2026-10-10: built and measured (section 2.4); every item above is in, some paths untested.*
The plan gave M4 no exit criteria of its own; the budgets it touches (zoom and pan in any
compare mode, switching, idle) are met.

Built: a compare set of 2 to 16 images, started from the selection in the panels (`C`), from the
image on show and the next one (`C` with nothing selected), or from files opened together
(command line, hand-over from a second process, drop); images added (item menu, drop with Ctrl)
and removed (image menu); files deleted on disk leave the set, and the last one left is shown on
its own. Six modes (`M`, or the compare panel): single, side by side, grid (columns and rows
chosen to show the images largest), split with a divider that is dragged or moved with Up/Down
(left|right or top|bottom), blend with an opacity, checkerboard with a cell size. Linked zoom and
position, or one view per pane (`L`); the linked cursor, marked in the other panes, with name,
size, position and stored values per image in each pane's corner. Flicker: `1`–`9` and the arrow
keys switch in place, a number key held for more than 250 ms shows its image only until it is
released, `K` blinks between the last two images (or through all) at 50 ms to 5 s. Display
controls shared by all images: black and white point, gamma, exposure in stops, and one channel
(red, green, blue, alpha, luma; `H`); 16-bit and float sets open with the window at the smallest
and largest value of the whole set. Different sizes: top-left, centred, or scaled to fit. The
compare settings are kept in `settings.json`; `--compare-mode` starts in a given mode.
Under it: the canvas and the two-target combine pass of section 7; 51 new unit tests (pane
layout, size matching, selection and peek logic, and the settings file, which had none; 167 in
all); `fivbench compare`; `corpus\compare`; test messages that give the viewer mouse and key
input with stated modifiers (posted Windows messages turned out to depend on where the real
mouse is and which keys are held on the machine).

Checked in the running viewer, from pictures of its window: all six modes with two images; grids
of 3, 4 and 16; linked zoom; the linked cursor and its readouts (a half-size image reads x 1708
where the original reads x 3416 when scaled to fit); panes unlinked; `1`–`9`, hold-to-peek, arrow
keys; blinking; the divider dragged and moved by keys, left|right and top|bottom; swap; the three
ways of matching sizes; an image with transparency (which also covers the checkerboard left
untested in M1) and its alpha channel; red and luma; a 16-bit PNG next to a float TIFF with their
common window; starting from a folder with `C` and with Ctrl+A, `C` (the first 16 of 40) and
leaving with Esc; three files handed over by a second process; removing images through the menu
down to one; deleting compared files on disk; the saved settings.
Found and fixed on the way: with Ctrl held the 100 % key zoomed about the wrong centre while
comparing; the window title did not follow the size of the set.

**Changed from before, to be confirmed:**

- Two to sixteen files opened together are now compared, in the mode used last (side by side at
  first). Before, they were a list to walk through; more than sixteen still are.
- While comparing, `1`–`9` are the images, so `1` is 100 % only outside a comparison; `Z` is
  100 % always.
- The display panel moved to the bottom right corner: the top corners name the compared images.
- Different sizes default to top-left, pixel for pixel; the two images shown by the modes that
  take two are the last two gone to, in the order of the set (`X` swaps).

**Written but not tested:** a real drop of files (it shares its code with the hand-over that was
tested) and a drop with Ctrl; selecting with Ctrl+click before `C` (the tests selected with
Ctrl+A); "Compare the selected images" and "Add to the comparison" in the panels' item menu;
renaming a compared file; a compared file changing on disk; the mouse cursor's shape over the
divider; the green and blue channels and the exposure slider; blinking through all images; what
is shown when the two float targets cannot be created; windows on a display of another DPI.

**Not done:**

- The waitable swap chain (left open in M2, suggested before the compare modes): every refresh
  gets a frame as it is, but input still waits up to one refresh longer than it has to.
- A comparison given on the command line skips the GDI first frame: its window shows its first
  picture when Direct3D is up, about 230 ms after the start instead of 95 ms.
- Images 10 to 16 of a set have no key; they are reached with the arrow keys or by clicking their pane.
- Window and level as such: the window is set by its black and white points.
- The compare panel is a fixed overlay at the bottom of the view (`P` hides it), not a dockable panel.
- A pane shows "Loading" for a moment when its file is rewritten; a single image keeps the old picture until the new one is there.
- All images of a set are held in full, whatever the cache budgets say: sixteen of 24 MP are 1.5 GB of RAM and 2 GB of video memory.
- Pinning one image as the reference while stepping through others (M7).

**M5 — Compare II: difference and metrics**
- Difference modes: absolute, signed (diverging colormap), squared, relative; gain; per channel
  or luma; threshold mask that highlights pixels differing by more than T.
- Colormaps and colour bar in real units; swap A/B.
- Metrics panel for the whole image or a region: MAE, MSE, PSNR, max error, % pixels over
  threshold, SSIM, per channel; difference histogram; SSIM heat-map view.
- Pixel inspector: per-pixel values of A, B and Δ drawn in the cells at high zoom.
- Export: difference / composite at full resolution, view to clipboard, metrics to CSV / JSON.

*State 2026-10-10: built and measured (section 2.5); every item above is in, some paths untested.*
The plan gave M5 no exit criteria of its own. Of the budgets it touches, zoom and pan at the
refresh rate and idle are met; the 2 ms of CPU per frame are exceeded in the longest frame
(2.5 ms) while values are written into the pixels.

Built: two more compare modes, **difference** (`E`: absolute, signed, squared, relative to the
first image; gain with Up/Down or set from the largest difference; the channel of `H`, colour
meaning the channel that differs most; nine colour maps, `G`, with a choice of its own for the
signed kind; a colour bar in the units of the files; `W` marks the pixels over a threshold on
the dimmed first image) and **SSIM** (a heat map of structural similarity, shown as 1 less it,
with gain). The difference and the similarity under the cursor are in the pane's readout. A
**metrics panel** (`N`): MAE, MSE, PSNR, largest error, share of pixels over the threshold and
SSIM for red, green, blue, alpha where an image is not opaque, luma, and all; a histogram of the
differences; for the whole area both images cover or for a **region** dragged on the image (`R`,
or Shift+drag). **Pixel values** (`V`): the values of an image, or of two images and their
difference, written into the pixels once these are large enough, with a grid between them; also
for one image outside a comparison. **Saving**: the split, blend, checkerboard, difference or
SSIM view at the full resolution of the canvas as PNG (`Ctrl+S`), the difference itself as
32-bit float TIFF (`Ctrl+D`), the numbers as CSV or JSON (`Ctrl+Shift+S`), and what the panes
show to the clipboard (`Ctrl+C`). The settings of the difference view are kept in
`settings.json`; `--compare-mode diff` and `ssim`.
Under it: what section 7 lists for M5; a PNG and a float-TIFF writer; 70 new unit tests (colour
maps, the difference definitions, reading rows through placement and orientation, every metric
against numbers worked out by hand, structural similarity against a plain implementation of its
definition, the saved pictures pixel by pixel, the writers through the Windows decoders, CSV and
JSON; 237 in all); `fivbench metrics`, and three more rows in `fivbench compare`.

Checked in the running viewer, from pictures of its window: the difference view in all four
kinds with five of the colour maps, gain by key and by "Auto", the colour bar and its labels;
the mask at two thresholds and the threshold keys; the similarity heat map with its readout;
the metrics panel on a pair that differs everywhere, one that differs by compression, an
identical pair of a 16-bit PNG and a float TIFF (infinite PSNR, units "normalised"), and a
half-size image scaled to fit; its numbers against `fivbench metrics`; a region dragged after
`R` and the whole image again; a set of three with the pair changed by key; pixel values of a
pair in the difference and split views (all nine numbers at 96 screen pixels per image pixel,
the differences alone at 67, nothing at 39), of one image of a set, and of a single 16-bit
image; saving, all through `--export-dir`: the SSIM view (6000 x 4000, looked at), the split
view (two of its pixels read back) and the blend view (written), the float TIFF (its size), CSV
and JSON (read), and the copied view (looked at); the menu.
Found on the way: structural similarity in 32-bit float was off by up to 3 in the fourth decimal
place on flat areas (now double precision); a "stutter" of 60 to 80 ms with pixel values on was
the benchmark counting the frame in which the notice of `V` times out, not the viewer (the
benchmark now waits for it; `--perf-log` records frames that come late, with where the time went).

**Changed from before, to be confirmed:**

- `Ctrl+C` while comparing copies what the panes show. One image of the set is copied through
  the menu ("Copy image").
- Zoom goes to 25600 % instead of 6400 %: a pixel has to be about 95 screen pixels wide to hold
  the three values of two 8-bit images and their differences, about 135 for 16-bit images, and
  half as much again on a display scaled to 150 %.
- `M` goes through eight modes; `E` goes straight to the difference.

**Written but not tested:** the Save dialog itself (every save was tested with `--export-dir`,
which skips it) and choosing JSON in it; the real clipboard (in test runs the copied view goes
to a file, so that the clipboard of whoever is working is left alone); Shift+drag for a region;
the entries under "Copy and save" in the menu; saving the difference view and the checkerboard
from the viewer (both are unit-tested and the difference was rendered by `fivbench metrics`);
moving or docking the metrics panel; pixel values of rotated, float, 16-bit colour and
transparent images; the alpha row of the metrics in the viewer; "Auto" before the numbers are
there; changing the pair while its numbers are being computed; what is shown when the numbers
or the map cannot be computed (out of memory); a difference of more than 4 GB, which TIFF
cannot hold; a display of another DPI.

**Not done:**

- Zoomed out, the difference view shows the difference of averaged pixels (section 7). The
  numbers and the saved files do not have this limit.
- For float pairs, pairs of different depth and scaled pairs the histogram has 4,095 bins
  between the largest negative and positive difference: the share over the threshold is good
  to a bin, not exact.
- PSNR of float data takes the span of the values met in the region as its peak. There is no
  setting for another one.
- The numbers are of the two images on show. All pairs of a set, and one image against a folder,
  are M7.
- One region, a rectangle, not kept between runs.
- Pixel values are written in the UI's text size only: an 8-bit colour pair shows all nine
  numbers from about 95 screen pixels per image pixel, its differences alone from about 50.
- Side by side and grid are not saved as pictures, and the colour bar is in neither the saved
  nor the copied view.
- The UI font has no Greek: the readout says "diff", not Δ.
- While the similarity view is in use its map stays in memory (for 24 MP: 96 MB, and 128 MB of
  video memory) until the pair changes or the comparison ends.

**M6 — Compare III: alignment**
- Manual: drag, arrow-key nudge (1 px / 0.1 px), rotate, scale, numeric entry, reset.
- Automatic: translation by phase correlation with sub-pixel peak fit; rotation and scale by
  log-polar correlation; refinement on an image pyramid. "Fine align" refines from the current
  manual pose.
- Before / after similarity score; export the aligned image.
- Exit: synthetic tests recover known transforms within 0.1 px and 0.05°; a 24 MP pair aligns in
  under 1 s (target).

*State 2026-10-10: built and measured (section 2.6); both exit criteria met; some paths untested.*

| Exit criterion | Result |
|---|---|
| Synthetic tests recover known transforms within 0.1 px and 0.05° | ten known moves, turns (up to 58°) and scalings (0.9 to 1.12) of an exact picture of 640 x 480: the corners of the image off by at most **0.015 px**, the turn by at most **0.0012°**; the same with noise, less contrast and more brightness: 0.08 px. The unit tests assert the limits — met |
| A 24 MP pair aligns in under 1 s | **0.14–0.20 s** (up to 0.28 s with the machine busy), and 0.02 s once for the reduced sizes of both images; in the viewer 0.13–0.27 s from the key to the image in place — met |

Built: a **pose** for every image of a set: moved by x and y, turned, scaled, about its centre;
shown in the image's label ("moved x 23.6 y -14.2 0.75° 100.4 %"). What is aligned is the
second image of the pair on show, to the first. **By hand**, in align mode (`A`): a panel with
the four numbers to type or step; a drag moves the image instead of the view (by whole pixels
when zoomed out, by screen pixels when zoomed in), and a drag with Shift turns it about its
centre, by the angle the mouse goes round that centre, so that what was grabbed stays under the
mouse and grabbing further out turns more finely; the arrow keys move it by a pixel, `PgUp` /
`PgDn` turn it by 0.1°, `Home` / `End` scale it by 0.1 %, each a tenth of that with Shift;
`Shift`+wheel turns, `Ctrl`+wheel scales; `Backspace` resets, a button undoes the last change
(a drag, a run of nudges, an automatic alignment). **Automatically** (`Shift+A`): the image
may be moved, moved and turned, or moved, turned and scaled, as chosen; **fine align** (`Q`)
refines the pose the image has. Both go by everything the two images show, or by a **region**:
a rectangle dragged on the image after "Region..." in the panel, drawn in green and named; it
is the alignment's own, apart from the region of the
numbers, so that one part of a picture can be aligned by and another measured. **How alike**
the two are as they lie (in the region, if there is one) is in the panel after every change,
and before and after in what an automatic alignment reports; a result that does not make them
more alike is not taken over. **The aligned image** is saved on the first
image's pixels (`Ctrl+E`; on the canvas's, if the first image is itself scaled or turned): PNG
with the image's own depth (8 or 16 bits, grey or colour), 32-bit float TIFF for float,
transparent where it has no pixel. The choice of what may be
changed is kept in `settings.json`.
Under it: what section 7 lists for M6; 16-bit colour in the PNG writer; 88 new unit tests (the
pose, the Fourier transform against its definition, phase correlation, the reduced sizes, the
registration on exact pictures of known pose in five layouts, and by a region on a picture
whose two halves moved differently, coverage and numbers of moved and turned images, the saved
aligned image; 325 in all); `fivbench align`; `scene_5_moved.jpg` in `corpus\compare`;
`window-shot.ps1` turns the wheel with Ctrl and Shift and drags with Shift.

Checked in the running viewer, from pictures of its window: the moved JPEG in the difference
view before and after `Shift+A` (the pose in its label, and in the log to the third decimal
what the corpus was made with); the panel; a drag at 100 %; the arrow keys, `PgDn` and `End`
with and without Shift, the wheel with Ctrl, Shift and both; fine align from the pose set that
way; reset; the similarity view and the metrics panel of the aligned pair (23,856,807 pixels
of 6000 x 4000) and its numbers as CSV; the aligned image as PNG (looked at: transparent
border) and, of a float image, as TIFF; a pair nudged by 3 and 2 pixels going back to exactly
nothing; a recompressed pair ("nothing to improve"); side by side, where the two readouts
under the linked cursor agree, and split; a 16-bit PNG against a float TIFF; a set of three,
the third aligned to the first, and the pair swapped; pixel values at 14,000 %; a region drawn
after the panel's button, `Shift+A` and `Q` going by it (the pose within 0.005 pixels and
0.0001° of the one found from the whole image, the report and the score naming the region),
"Whole image" taking it away again, and the region of the numbers next to it, each in its
colour; a drag with Shift 600 screen pixels to the right of the image's centre, 60 pixels down
(turned by 5.70°, clockwise) and half of it back (2.86°); the wheel with Shift, with Ctrl and
with both, as they are now; a plain drag at 29 % moving the image by whole pixels (83 and -48).
Found and fixed on the way: the metrics of a pair of which one image is blended showed a row
for alpha with a difference of 0.00005, because four weights that should add up to one gave an
opaque pixel an alpha of 254.99998 (in M5's scaled-to-fit pairs too); the corpus's
recompressed image came out 0.07 pixels off "nothing to align" (below).

**Changed from before, to be confirmed:**

- In align mode the arrow keys, `PgUp` / `PgDn`, `Home` / `End` and `Backspace` are the image's,
  not the set's and not the mode's (divider, opacity, gain). `1`–`9` still switch images.
- `A`, `Shift+A` and `Q` are new keys while comparing; `Ctrl+E` saves the aligned image.
- In align mode a drag with Shift turns the image; it does not draw the region of the numbers,
  as it does outside align mode. That region is still drawn after `R` or its button in the
  metrics panel, and the alignment's own after "Region..." in the align panel. (For a few
  hours a drag with Shift drew the alignment's region; turning the image got the gesture.)
- Shift is the turn and Ctrl the size on the wheel too: `Shift`+wheel turns, `Ctrl`+wheel
  scales, the other way round from the first version of this milestone.
- What the automatic alignment finds within 0.08 pixels (at the corners of the image) of a
  move by whole pixels with no turn and no scaling is taken to be that move: it is the one pose
  under which a pair is compared pixel for pixel, and 0.08 is inside the tenth of a pixel the
  method is held to. A turn or scaling that moves the corners by less than 0.03 pixels is
  taken to be none.
- An automatic alignment is taken over unless it makes the two images less alike (by more than
  0.0005 of the score). It replaces what was set by hand; "undo" brings that back.
- The numbers of scaled-to-fit pairs can differ from M5's in the last digits: four pixels are
  now blended by going from one to the next, not by four weights.

**Written but not tested:** typing into the panel's fields and their step buttons; "undo"; the
three choices of what may be changed, in the viewer (each is unit-tested, "moved only" also
timed); the entries under "Align" in the menu; a region with the model that only moves, in
the viewer; the Save dialog; an aligned image of 16-bit
colour and one with transparency of its own; a drag above 100 %; turning with the mouse close
to the image's centre, and in side by side; the mouse cursor's shape; aligning in the grid and
with unlinked panes; `Shift+A` again while one
is running; an image leaving the set, or being moved, while its alignment is worked out; a
smaller image placed centred, in the viewer (unit-tested); running out of memory; a display
of another DPI.

**Not done:**

- Only the second image of the pair is moved, one pair at a time. Aligning every image of a
  set to one, and two folders pair by pair, is M7.
- Moving, turning and one scale for both directions: no shear, no perspective, no lens distortion.
- It goes by brightness alone, and needs the two pictures to overlap for the most part. Fine
  align finds what is off by up to about a twentieth of the image. Two pictures with little in
  common, or with little texture, get "no alignment found".
- The region is one rectangle, not kept between runs: no several regions, no shapes, no parts
  left out of a region. A small region does not make the alignment keep to moving only: with
  turn and scaling allowed, what it finds for them is right in the region and less so far from it.
- Blending four pixels softens: the similarity map of a turned pair shows it as a faint weave.
  No bicubic or better, neither on screen nor in the saved aligned image.
- The pixel readouts and the values written into the pixels are of the nearest stored pixel of
  a moved image, not of the blend the numbers are taken of.
- The score is taken at reduced size: it does not see an improvement of a fraction of a pixel.
  The metrics panel does.
- Poses are not kept between runs, and the saved numbers (CSV, JSON) do not say at what pose
  they were taken. One step of undo.
- A grey image saved aligned is 0 where it has no pixel: the PNG writer has no grey with alpha.
- Not measured: real photographs. The corpus is one synthetic scene.

**M7 — Compare IV: folders and automation**
- Pair two folders by file name (or by order): table of pairs with metrics, sorted worst-first,
  step through pairs; missing and extra files flagged.
- Reference mode: pin one image and step through a folder against it.
- Batch metrics across all cores; report export.
- `Fiv.Cli`: `fiv compare A B --metric psnr,ssim --diff out.png --json` for scripts and agents.

*Put off on 2026-10-10, at the user's wish: M8 was begun instead, with the measurements.*

**M8 — Carry-over features**
- Picked from section 8 as needed: measurements, line profile A/B, quality-metric columns,
  image information, crosshair styles, calibration, remaining file operations.

**M8.1 — Measuring: lines, rectangles, circles, the values along a line, the histogram of an area**

*State 2026-10-10: built and measured (section 2.7); some paths untested. Asked for as "the
whole measurement (line, rectangle, circle) stuff with line profile, area histogram"; no exit
criteria were set beforehand.*

Built: with `U`, **measurements** on the image and a panel that lists them. `L`, `R` or `C`
and a drag draw a **line**, a **rectangle** or a **circle** (from its centre); with Shift the
line is level, upright or diagonal and the rectangle a square. A measurement is changed by its
handles (the ends of a line, the corners and sides of a rectangle, anywhere on a circle), moved
by a drag on it (a rectangle by its outline, a circle by its centre: the inside of either is
still the view's to pan), selected by a click on it or on its row, and deleted with `Del`. The
selected one's position and size can be typed and stepped in the panel. Each is drawn with its
number and its size; the panel gives length, the distance across and down and the direction
of a line, and size, area and perimeter of a rectangle or a circle.
**The values along a line**: a plot of red, green and blue (or the value of a grey image, or
the one channel chosen with `H`), with mean, spread, smallest and largest of each curve; the
mouse over the plot reads the values at that point of the line, which is marked on the image.
**The pixels inside a rectangle or a circle**: how many, and for every channel mean, spread,
smallest, largest and median value, with a histogram (counts on a logarithmic scale if
wanted) that the mouse reads as well. **In a comparison** the measurements are drawn in
every pane, and the plot, the histogram and the tables show every image of the set that is
loaded: each has a tick in the panel that switches it off and on, a line style (the first one
ticked solid and its histogram filled, then dashed, dotted, dash-dot, long dashes, sparse
dots) and a colour for where the channel has none (a grey value, luma, alpha); the values are
in the units the images can be read in together (as stored if all have one depth, else from
0 to 1). What the mouse is at in the plot or the histogram is in the last column of the table
under it. **The panel** is a window like the folder panel: moved, sized and docked by the
user and kept so in `layout.ini`; its rows break and its plots shrink with its width, and it
scrolls when it is too small for what it shows.
**The measurements stay where they are** when the image changes and when a comparison begins
or ends: they are on the pixel grid on show, not on an image. **A scale**: what one pixel is
long in a unit, typed, or worked out from the selected line and its known length; lengths and
areas are then given in that unit as well, in the labels, the list and the panel; it is kept
in `settings.json`. **Saved as CSV**: the list, the values along the selected line, and the
numbers and the histogram of the selected area.

How the values are taken: from the full-resolution pixels as the file has them, through the
same sources the numbers of a comparison use (`CompareSource`), so orientation, placement and
alignment are followed. Along a line there is a sample every pixel of length, the first at its
start and the last at its end, each blended from the four stored pixels around its point; a
line along a row or a column through the centres of its pixels gives the stored values
exactly. Zoomed out, the ends of a line are put on the centres of the pixels under the mouse;
zoomed in, where the mouse is, to the fraction of a pixel the screen shows. A rectangle's
sides are on pixel edges, so it holds whole pixels; a pixel belongs to a circle if its centre
is inside. 8- and 16-bit values are counted a bin each, float values in 1024 bins between the
smallest and the largest. The values along a line are taken at once; the numbers of an area
on a worker, one run at a time, so that they follow a drag as fast as the area can be gone
through.

Under it: `Fiv.Measuring` in `Fiv.Core` (the shapes with their geometry, hit test and changes
by a drag; the line profile; the numbers of an area; the units several images share; the CSV
texts); `CompareSource.Sample` for the image at any point of the canvas;
`MainWindow.Measure.cs`; the panel layout is now also written when the viewer closes
(`Ui.SaveLayout`), not only by ImGui's own timer, which needs a frame five seconds after a
change and so missed what was docked just before closing; 68 new unit tests (the
shapes' numbers, the rows of a circle against the test for each pixel, what is hit where,
drawing and dragging with and without Shift and at the edge of the grid, profiles along rows
and columns against the stored values and of slanted lines over a slope, of turned and moved
images, where there is no pixel; the numbers of areas against the same worked out the slow
way, for grey, colour, 16-bit, float with values that are not numbers, a turned image, a large
area on many threads; the units of images of one depth and of several; the CSV texts, for one
image and for several numbered as on screen; 418 in all); `fivbench measure`.

Checked in the running viewer, from pictures of its window: on a 24 MP JPEG a line (ends on
pixel centres at 23 %: 1159.5, 1282.5 to 3720.5, 2563.5, length 2863.51, angle -26.57°) with
its plot, the corpus's magenta square in it as red 255, green 0, blue 254 under the mouse and
the mark on the image there; a rectangle (1110 x 853: 946,830 pixels, three histograms, the
table) and a circle (radius 256: 205,861 pixels against an area of 205,887); a level line and
a square with Shift; an end of the line dragged and the line moved; a corner and a side of the
rectangle dragged; the circle's outline and centre dragged, its radius and centre typed, a
step button; a row of the list clicked; `H` (red only, in the image and in the plot); `Del`,
`Del` with nothing selected, `Esc` and `U` again (the measurements were kept); the next image
of the folder with the line still there and the plot of the new image; the mouse over the
histogram, and the logarithmic scale; the four CSV files (through `--export-dir`); at 892 % a
line with its ends at fractions of a pixel; the scale typed (0.5 mm: 62.7 px = 31.35 mm) and
set from the line (100 mm: 1 pixel is 1.59496 mm, the label "100 mm", a column in the list); a
16-bit PNG (histogram from 10178 to 50915) and a float TIFF (0.2377 to 0.7769); a JPEG turned
by its orientation tag, the first and last row of its saved profile against the status bar's
values of the pixels at the ends of the line (the same); side by side, where the line and the
rectangle are in both panes and the second image's means are 6.0 above the first's in every
channel, as the corpus made it; the difference view of a pair aligned with `Shift+A`, with the
note that one image is blended and the same means for both; a grid of three with a circle in
every pane; the list of keys. After the two things reported by the user (below): a grid of
three with a line (nine curves, solid, dashed and dotted, nine rows in the table) and with a
rectangle (three histograms, the first filled; twelve rows), the second image switched off
by its tick and gone from plot and table, the third then dashed, and the profile saved so
(columns `red_1` ... and `red_3` ..., none of the second); the values at the mouse in
the last column of both tables; the panel made narrow and tall by its corner (rows break, the
images' names go onto two lines, no bar for scrolling sideways), docked at the right edge by
a drag of its title bar onto the marker there (the image area gives way, the grid is laid
out again), and docked again after the viewer was closed at once and started again.
Found and fixed on the way: a square drawn with Shift was 854 x 853 pixels, because its sides
went onto the pixel edges after it had been made square.
Reported by the user after the first version, and fixed: in a grid of more than two images
the panel showed only the two of the pair (it now shows all, with a tick each); the panel
could not be docked or resized (it was a window that sizes itself to what it shows; it is now
a window like the folder panel). On the way: long remarks did not wrap in a narrow panel and
kept it scrolling sideways; a panel docked just before closing was not remembered.

**Changed from before, to be confirmed:**

- `U` is a new key. While the measurements are on show, `L`, `R` and `C` choose a shape and do
  not do what they do otherwise (keep or link zoom and position; the region of the numbers;
  compare or stop comparing), `Del` deletes the selected measurement and never the file, and
  `Esc` gives up the shape chosen and then puts the measurements away, before it leaves align
  mode, fullscreen or a comparison. With Shift or Ctrl the keys are as before (`Shift+R`,
  `Ctrl+C`). `U` again gives them all back.
- While the measurements are on show, a drag that begins within six screen pixels of one
  changes or moves it instead of panning; also in align mode, where it would move the image,
  and with Shift, where it would draw the region of the numbers. With a shape chosen, the next
  drag anywhere on the image draws it.
- Measurements do not belong to an image: they stay on the pixel grid when the image changes.
  On an image the file's orientation tag turns, their positions are those of the upright
  picture; the status bar's are of the stored pixels, as before.
- `settings.json` has three new entries: `measurePixelSize`, `measureUnit`, `histogramLog`.
- The panel layout (`layout.ini`) is written when the viewer closes, for all panels: until now
  a panel moved or docked in the last five seconds before closing was where it had been
  before, the next time.
- In a comparison the values are those of every image of the set that is ticked, in any mode,
  not only of the pair on show; a saved profile has its columns numbered as the images are on
  screen (`red_1`, `red_3`).

**Written but not tested:** the Save dialog (the saves were made with `--export-dir`); the
entry in the menu; typing a unit; the position fields of a line and of a rectangle (a
circle's were typed); measuring with a zoom and position per pane, and in the single, split,
blend, checkerboard and similarity views; measuring while align mode is on; a drag that leaves
the pane or the window; measurements left where a smaller image has no pixels (unit-tested: no
value, no pixel); a pair of different depths in the viewer (the scaling is unit-tested); an
image that is not opaque, and alpha and luma chosen with `H`; the note on samples that are off
the image; a panel so small that it scrolls; the panel docked anywhere but at the right edge,
taken out of its dock again, or sharing a dock with the folder panel; more than three images
ticked, and images of different depths among them; the folder panel and the filmstrip open
(every run was without them); the mouse cursor's shapes; a double click on a measurement; many
measurements; a display of another DPI; running out of memory.

**Not done:**

- No angle between two lines, no polygon, ellipse or freehand shape; a circle is drawn from its
  centre, not through three points.
- A line has no width: its profile is not averaged over the pixels beside it. Its samples are
  always blended from four pixels, also where the readouts elsewhere give the nearest one.
- The images' curves are laid over each other; no difference between two of them is plotted.
  There are six line styles: the seventh image ticked is drawn like the first. Red, green and
  blue have the same colours in every image, so with many images ticked the plot is crowded:
  `H` leaves one curve an image, each in its image's colour. Nine curves cost 0.9 ms a frame;
  sixteen images of three channels each were not measured.
- Docked onto the middle of the window, the panel covers the image, as the folder panel does
  when it is docked there.
- Measurements are not kept between runs, not saved into or with an image (no "save with
  annotations"), and there is no undo: what is deleted is gone.
- One scale for all images, not read from the resolution a file states. The old app's
  calibration of values (a colour scale read as temperatures) is not carried over.
- The plot cannot be zoomed; its range is that of the values on show. The histogram of an
  8-bit image always runs from 0 to 255, of others over the values met.
- The numbers of an area of a whole 24 MP colour image take 126 ms: sums and bins for each of
  five channels, pixel by pixel. They could be had from the histograms for all but luma.
- The median of float values is as fine as the 1024 bins; the spread is that of the pixels
  themselves (divided by their number).
- Not measured: real photographs.

**Formats — decoders of its own**

*State 2026-10-24: built, measured (section 2.8), tested on 128 sample files. Asked for after a
HEIC would not open: "find images for all supported formats online, download them and test
the opening correctly; also high bit images", then "add native loaders for these formats".*

Built: **fivcodecs.dll**, a C++ shim over libheif 1.23.6 (with libde265 1.1.3 for HEVC and
dav1d 1.5.4 for AV1), libwebp 1.6.0, libjxl 0.12.0, LibRaw 0.22.2 and libtiff 4.7.2, all
static, /MT, from pinned sources (`native\build-codecs.ps1`, `native\build-fivcodecs.ps1`).
One call per image: the decoder works out size and layout, asks the C# side for the memory
through a callback, and writes the pixels into it, so there is no second copy. It is a DLL in
the Native AOT build too, loaded with the first image that needs it; PNG, GIF and BMP never
load it. Next to it goes `vcomp140.dll`, the OpenMP runtime, for LibRaw.

- **Which decoder**: by the first bytes, not the ending. HEIF and AVIF by their brands, CR3 too
  (it is ISO media); WebP, JPEG XL; raws by their own signatures, and a TIFF only if its first
  directory has a camera's Make or a DNG version and LibRaw takes it. TIFFs go to WIC, except
  those WIC fails on or gets wrong (10, 14, 24, 32 bits, signed, 16- and 64-bit float, LogLuv,
  uncompressed YCbCr), which go to libtiff. A file the bundled decoder rejects gets WIC, and a
  TIFF WIC rejects gets libtiff.
- **Depth**: more than 8 bits per channel stay: RGBA16 or Gray16, 10 and 12 bits spread over 16
  as WIC does it (the low bits repeat the high ones); float grey as GrayFloat. 12-bit JPEG and
  lossless JPEG up to 16 bits through libjpeg-turbo's own calls for them, in full only (no
  preview for those).
- **HEIF**: the primary image with the file's rotation, mirroring and crop applied; a file that
  is only an image sequence shows its first picture. Thumbnails come from the thumbnail the
  file holds. All cores on a grid's tiles, or on the codec's threads for one picture.
- **Raw**: decoded as the sensor has it, turned by the orientation the file states, as a JPEG
  is. That lets the JPEG the camera embedded (the largest in the file) stand in until the raw
  is decoded, drawn at the raw's size: the file name decides that a preview stage is planned,
  as for JPEG. Thumbnails come from that JPEG too. 16-bit sRGB from camera white balance, AHD,
  Markesteijn 1-pass for X-Trans; Foveon X3F through LibRaw's x3f code.
- **JPEG XL**: the first frame, upright; lossy (XYB) files converted to sRGB; premultiplied
  alpha divided out. **WebP**: animated ones show their first frame; thumbnails scaled while
  decoding.
- The endings `hif`, `jxl`, `crw`, `nrw`, `srf`, `sr2`, `rwl`, `pef`, `srw`, `3fr`, `iiq`, `erf`,
  `mrw`, `x3f`, `dcr`, `kdc`, `mos` and `mef` are new in the folder lists and in "File types...".
- Tests: 210 new, most of them on the sample files and skipped without them: every sample
  opens with the decoder it should and makes a thumbnail; sizes and layouts of 31 of them; the
  10-, 12- and 14-bit samples spread exactly; a TIFF at every depth from 8 to 32 bits is the same
  picture; float TIFFs of 16, 32 and 64 bits give the same values; the 12-bit JPEG is the
  picture of its 16-bit PNG; AVIF rotation, mirroring and crop against the references; a raw's
  preview is its shape and stands at its size; a decode cancelled before it starts gives nothing.
  Found on the way by them: a raw decode cancelled while LibRaw opened the file came back as an
  error, and the TIFF path rounded 10- and 14-bit samples where the other decoders repeat bits.

**Changed from before, to be confirmed:**

- HEIF, AVIF and JPEG XL are now among the "Common" endings of the File types dialog.
- Raws show the camera's JPEG first, then LibRaw's decode, which is flatter (no tone curve) and
  in a DJI DNG darker than the camera's rendering: the picture changes when it lands.
- `LoadedImage.JpegName` became `PreviewName` (JPEG or raw by name).

**Written but not tested:** HEIF, AVIF, JPEG XL and TIFFs through libtiff in the viewer itself
(only the decodes, the thumbnails and two images in the viewer: a phone HEIC and a RAF);
comparing, aligning and measuring with images of these formats; cancelling a decode midway
(only before it starts); a machine without the Windows codec extensions; files of more than
2 GB; running out of memory.

**Not done:**

- HDR: PQ and HLG files (AVIF, HEIF, JPEG XL) show their code values, flat and dim; no tone
  mapping (WIC maps them to SDR). Gain maps (Ultra HDR, Apple) are not applied: the SDR picture
  shows.
- Float colour (JPEG XL float, float RGB TIFF, half-float JPEG XR) is clipped to 0..1 in RGBA16:
  there is no float colour layout. Float grey keeps its values.
- No colour management, as before: wide-gamut files show as if sRGB; CMYK only where a decoder
  turns it into RGB itself.
- The file list shows no dimensions for HEIF, AVIF, JPEG XL and raws (it reads them from file
  headers for JPEG, PNG, GIF, BMP, TIFF and WebP only).
- No preview stage for HEIF: a 33 MP camera HIF is blank for 2.3 s. Its stored thumbnail could
  stand in.
- Cameras newer than LibRaw 0.22.2 (the Sony A7 V in the samples) do not open; a BMP with
  alpha bitfields (Windows CE) does not either.
- libheif, libde265 and LibRaw are LGPL (LibRaw also CDDL), linked statically into
  `fivcodecs.dll`: whoever ships the viewer has to let users replace that DLL with their own
  build, which the shim's source and the build scripts allow.

**3D height map**

*State 2026-10-24: built, measured (section 2.9), looked at in the viewer. Asked for as "click
a button, and the image should just turn in 3d from image to 3d heightmap", then "can we do this
for all other view like multi view or comparison views?". Height from brightness was chosen
after a look at what it would take; depth from photographs through a neural network (Depth
Anything V2 and the like) was left for later.*

Built: the **3D button** in the view's upper right corner, `3`, or "3D height map" in the menu
turns the image on show into a surface whose height is its brightness. It **turns**: the first
frame is the 2D view exactly (a camera straight down at the point the view is centred on, as
far off as the zoom says), and over 0.7 s the camera tilts back to 50 degrees, turns by 20 and
moves out a little while the surface rises from flat. Back to 2D the same way in reverse,
whatever the camera has done meanwhile, and the 2D view takes over where it was. From a fitted
2D view the 3D view ends with the whole image filling the window (small images enlarged, which
the 2D fit does not do); from a zoomed-in one it keeps that part.

- **Drawn** from the image's own textures, tile by tile: a grid of vertices over each tile, a
  row of cells per instance as a triangle strip without a vertex buffer, cut at the tile's
  edges, so that neighbouring tiles meet at vertices of the same height. The colour is every
  pixel of the image, with exact pixels where it is magnified (decided per screen pixel) and
  anisotropic filtering where it is seen at a slant. Depth buffer reversed (1 near, 0 at
  infinity), 32-bit float, the renderer's own, made when first needed.
- **Heights** (Fiv.Surface.HeightField): the decoded pixels averaged to at most 2048 values on
  the long side, in the stored orientation and in the units the GPU samples, smoothed if asked,
  with mip levels; made on a worker and made again when the image, its full version, the
  channel (`H`) or the smoothing changes. Black, white, gamma and exposure act on them in the
  shader, as on the colours: white is the highest point. Float samples that are not finite are
  left out; a cell with none lies at the lowest value.
- **Light** from the image's upper left at 45 degrees, from the slope worked out per screen pixel
  from the heights at the detail the screen shows: level ground keeps its colour exactly, so the
  first frame of the turn is the 2D picture.
- **Camera** (Fiv.Surface.SurfaceCamera): a drag turns it about the point looked at, `Shift`+drag
  moves that point over the ground, the wheel brings it closer at the cursor (the ground under
  the cursor stays there), double click and `F` go back to the starting view. A new image of the
  same size keeps the camera: a folder can be walked through in 3D, each image rising in turn.
- **Options** in a panel under the button: height (a share of the long side), smoothing, mesh of
  256 to 2048 cells, light, colours (the image's own or a colour map by height), upside down,
  wireframe; kept in settings.json.
- **Comparing** (`Shift+3`, as `3` is the third image; the button and the menu as on one image):
  every pane goes into 3D, one camera for all. The ground is the comparison's canvas, with one
  height scale for all, so that heights can be compared as seen. A pane of one image (single,
  side by side, grid) is that image's surface, placed and aligned on the canvas as in 2D; each
  image has its height grid (1024 values on the long side when more than four are compared),
  kept while it is in the set, and the mesh is no finer than the pane has pixels. A pane that
  combines two (split, blend, checkerboard, difference, SSIM) is drawn by the 2D combining pass,
  over the whole canvas at up to 2048 texels, into a texture of colours and one of heights at
  once (SurfaceCanvas: the pass writes a second target), in every frame, so that the mode, the
  gain, the divider, the opacity and the colours act as in 2D; that canvas is then raised. Its
  height is what the view shows: the difference's place on the colour scale (a signed one
  stands about a middle plane), the dissimilarity, or the brightness of split, blend and
  checkerboard; smoothed by reading it from a coarser mip level. In 3D the divider, the regions,
  the linked cursor and the pixel readouts are not drawn; the 3D panel pushes the second image's
  name down. Measuring ends the 3D view.
- `Ctrl+C` copies what the 3D view shows.
- Fiv.Render: SurfaceRenderer, SurfaceCanvas, ColormapTexture (taken out of CompareRenderer),
  CompareRenderer.DrawFlat, GpuImage.Wrap; shaders\surface.hlsl; combine.hlsl writes the height
  as a second target. The light follows the ground's slope also where the ground is scaled (a
  comparison's "scaled to fit", an alignment), through the inverse transpose of the image's place.
- fivbench surface.
- Tests: 21 new (649 in all): the grid's values for every layout and channel, its reduction,
  the size it lies over, float gaps, smoothing (a flat image stays flat, a spike spreads evenly
  and keeps its sum, and reaches no further than it should), cancelling; the camera straight
  down projecting the ground as the 2D view does, reversed depth, turning within limits, the
  ground following a drag and staying under the cursor while zooming, the turn between two
  cameras; the options in settings.json.

Looked at in the viewer with `window-shot.ps1`: the turn in and out on a 24 MP JPEG (several
tiles, no seam), a 12 MP JPEG turned by its EXIF orientation, the next image rising, `Esc`,
turning, moving and zooming, double click, `H` (red), "upside down" clicked in the panel,
`Ctrl+C` to a file, a float TIFF in Turbo by height, a 16-bit PNG with alpha as wireframe.
Comparing: side by side in and out with turning and `Esc`; the difference of a JPEG and its
quality-50 copy with the gain raised by `Up`, and of an edited copy in Turbo; split with the
divider moved by `Up`; checkerboard; blend; a grid of four with an image of half the size; SSIM;
single, going to the second image in 3D. Split, blend and checkerboard in 2D after the change to
the combining shader, with an image of half the size: as before.

**Changed from before, to be confirmed:**

- `3` on one image is the 3D view; while comparing it is still the third image, and `Shift+3`
  (until now the third image too) is the 3D view.
- The 3D button is always in the view's upper right corner when one image is shown, and while
  comparing; in a pane that combines two images, the second image's name is under it.
- The combining shader writes a second output, the height, which only the 3D view binds; split,
  blend and checkerboard now pick between and blend the two images through one `lerp`.
- A double click and `F` in a comparison in 3D go to the starting view instead of zooming the panes.
- `Shade` in display.hlsli is now `ShadeOver` (over any colour) and `Shade` (over the
  checkerboard), with the same result.

**Written but not tested:** the menu item; the slider and mesh controls dragged by the mouse
(each was set through settings.json instead); a window resized or made fullscreen in 3D; the
software rasteriser (no GPU); a failure while the heights are made; an image whose decoded
pixels are not in memory. While comparing: panes that are not linked (the turn starts from the
active image's view for all of them, so the others jump); an aligned or "scaled to fit"
image in 3D; more than four images (the smaller height grids); aligning by the keys in 3D; a
graphics card without mip levels made for float textures (asked for, then left without them).

**Not done:**

- Depth from photographs: brightness is not depth, so a photograph becomes a relief of its
  brightness. A depth estimation network (Depth Anything V2 Small is Apache-2.0, about 25
  million parameters) through ONNX Runtime or Windows ML could be a second source of heights.
- No sides or base: from low down the surface is a sheet, not a block.
- No pixel readout or measuring on the surface; the readout under the cursor is off in 3D.
- A combined view in 3D has at most 2048 texels of colour on the long side, also when zoomed in
  close; a pane of one image has every pixel.
- The divider and the regions cannot be dragged in 3D (the keys still move the divider).
- No export of the surface as a mesh (STL, OBJ) or of the heights as an image.
- Transparent pixels show the background colour, not the checkerboard.

**Every image at once: the compare modes, their controls and aligning; circles through three points**

*State 2026-10-28: built, unit-tested, looked at in the viewer with four images of the compare
corpus. Asked for as "the compare mode needs to work not only for two images but for n, and we
need controls for all compare modes for all selected images, also the align mode has to work for
all images (we can select which ones to move)" and "the measurement circle drawing needs 3 point
mode". Design in section 7.*

- Then asked for: "all images in one overlay with selectable transparency per image", the
  blend with its reference kept as it is. The overlay mode: every image in the views with an
  opacity of its own, 100 % to begin with, in a column of the compare panel and on its label;
  `Up` / `Down` change the active image's. Looked at with four images, all at 100 % and one at 30 %.
- The compare panel lists the images: in the views or not (`Ctrl+1` ... `9`), the reference
  (`X` steps through the images shown), and in the blend each one's opacity (an equal mix until
  one is set). Side by side is one row or one column of all the images shown, the grid as many
  rows and columns as show them largest; split has a divider between every two strips, each
  dragged on its own (`Up` / `Down` move the one after the active image's strip).
- The metrics panel has a row for every image against the reference, of the channel shown, and
  the clicked one in full; `Ctrl+Shift+S` saves the numbers of all of them in one CSV table or a
  JSON array. `Ctrl+S` saves the split, blend or checkerboard of all, or the difference or SSIM
  of the active image; `Ctrl+D` the active image's difference values.
- The align panel lists every image but the reference: a tick for whether it moves, what it has
  been moved by, how alike it is to the reference as it lies (worked out one image at a time
  while the panel is open), and what its last automatic alignment said; the numbers below are
  the clicked image's. Changing the reference stops an automatic alignment under way.
- Measuring: `Shift+C`, then three clicks, is the circle through them (`MeasureShape.CircleThrough`);
  a drag before letting go moves the point, a click on a point already there is dropped, three on
  a line are refused. Its centre may lie off the picture, and stays there when the circle is moved
  or its radius typed. Two quick clicks are two points, not a double click.
- Looked at in the viewer, four 24 MP images: every mode, the reference stepped with `X`, an
  image taken out with `Ctrl+3`, the metrics panel, the automatic alignment of three images (the
  moved one found at x 23.6, y -14.2, 0.75°, 100.4 %, alike 0.9991; the other two left as they
  were), split and difference in 3D, the circle through three points with its numbers. Two
  images: split, swap, blend and difference as before.

**Written but not tested:** dragging the dividers and the opacity sliders with the mouse; the
images menu in the right-click menu; `Shift+X`; saving any view of more than two images from the
viewer (the file export itself is unit-tested); the align panel's ticks, "Tick all / none", "Stop"
and its number fields with several images; a drag-turn of several images; the reference changed
during an automatic alignment; a 3-point circle in a comparison's panes; the frame times of the
stack modes with many images (each image is a pass of its own: 16 images at 4K is 32 full-pane
passes a frame).

**Not done:**

- The pixel values written into the pixels of a stack of three or more show the reference's only.
- The SSIM maps of many images are large: one float per canvas pixel each, on the CPU and the GPU
  (some 200 MB for a 24 MP pair); they are dropped when their image leaves the views.
- No per-image opacity for the split or the checkerboard, and no way to reorder the stack other
  than by the reference.

## 10. Risks

| Risk | Mitigation |
|---|---|
| A binding (ImGui, D3D11, WIC) does not work under Native AOT | Found in the M0 bake-off before anything is built on it; fallback is C++ with the identical stack |
| Full-resolution JPEG decode is bound by single-thread libjpeg-turbo speed (my estimate: 100–150 MP/s here) | Two-stage loading hides it; it cannot be removed |
| PNG and TIFF have no reduced-size decode | Faster native PNG decoder if measured worthwhile; cached thumbnail shown at once |
| Cold start dominated by GPU driver load during device creation | Decode runs in parallel with it; optional stay-resident mode |
| First thumbnail pass on HDD or network is I/O-bound | Embedded EXIF thumbnails first; persistent local cache makes it a one-time cost |
| ImGui: non-native look, weaker text input and accessibility; render-on-demand needs discipline | Accepted for speed; Avalonia is the alternative app layer |
| WebP / HEIC / AVIF depend on installed Windows codec extensions | Happened (a HEIC did not open): decoders of its own for these and for raw and JPEG XL, WIC as the fallback (section 9, after M8.1) |
| Antivirus scanning of a new unsigned exe adds launch latency | Outside the app; sign the exe or add an exclusion |

Not in scope: colour management (ICC profiles). Pixels are shown as sRGB; for comparison the raw
values are what matters. Revisit after M5 if needed.

## 11. Decisions to confirm before M0

Defaults are what I will assume unless told otherwise.

1. **Language** — decided: C# / .NET 10 Native AOT (bake-off result in section 3.1).
2. **UI toolkit** — in use: Dear ImGui (Hexa.NET.ImGui 2.2.9 with its Win32 and Direct3D 11
   backends), assumed from the default; say so if you want Avalonia instead.
3. **Formats that matter most** — default: JPEG first, then 8/16-bit PNG and TIFF. This decides
   which extra native decoders are worth adding.
4. **Where the images live** — default: assume both local disks and network shares.
5. **Must-have carry-over features** — default: none before M7.
