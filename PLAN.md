# FastImageViewer2 — Plan

Status: **M0, M1 and M2 done and measured; M3, M4 and M5 built and measured, each with untested items** (see section 9); **M6 is next.** What each milestone left open is listed
under it in section 9. Written 2026-10-09. Language decided: C# / .NET 10 Native AOT (section 3.1).
Measured numbers: section 2.1 (M0), 2.2 (M1), 2.3 (M2), 2.4 (M4, comparing) and 2.5 (M5, difference and metrics). Build and keys: `README.md`.

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
| Other formats | **Windows Imaging Component (WIC)** | built in; PNG, TIFF incl. 16-bit and float, BMP, GIF, ICO, WebP, plus HEIC/AVIF/RAW when the Windows codecs are installed |
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
    Fiv.Core/      folder index, decoders, caches, scheduler, metrics, registration (no UI, no GPU)
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

## 8. Feature inventory of FastImageViewer → where it lands

| Old feature | In FastImageViewer2 |
|---|---|
| Open files / folder, drag and drop, command line, single instance | M1 |
| Formats JPG, PNG, TIFF, BMP, GIF, WebP, ICO; 16-bit and float PNG/TIFF with raw values | M1 (HEIC/AVIF/RAW additionally, when Windows codecs are installed) |
| Zoom, pan, fit, 100 %, interpolation toggle, fullscreen, keep zoom/pan across images | M1 |
| Pixel coordinates and values under the cursor, status bar | M1 |
| Folder panel: thumbnails; name, size, dimensions, modified columns; natural sort; auto-update | M2 (index, navigation), M3 (thumbnails, panel) |
| Name / size / dimension / date filters; select all / invert | M3 |
| File operations: show in Explorer, copy path / name / image, rename, delete, move, copy to, export list | M3 (basic set), rest M8 |
| Tabs / open-files list, recent files, settings | M3 |
| Layouts single / 2 columns / 2×2, synced zoom/pan, synced anchor point, filename overlay | M4 |
| Overlay: per-image opacity; blend modes Normal, Multiply, Screen, Overlay, Difference, False-Color Diff; swap A/B; colour bar | M4 (opacity), M5 (difference, false colour) |
| False-colour schemes Jet, Thermal, Rainbow, Magenta-Cyan, Red-Blue | M5, plus Viridis, Turbo, Coolwarm |
| Overlay transform: translate / rotate / scale by slider, number, drag; step sizes 0.1 / 1 / 10 | M6 |
| Auto-align and fine-align (image registration) | M6, new algorithm |
| Low-resolution "preview mode" for overlays | not needed — full resolution is real-time |
| Quality metrics (sharpness, contrast, edge %) as overlay and sortable columns; "analyze all" | M8 |
| Line / circle / rectangle measurements, measurement panel, calibration | M8 |
| Line profile with CSV export | M8, extended to overlay the profiles of A and B |
| Crosshair with 8 styles | M4 (linked cursor), styles M8 |
| Image information / EXIF, high-bit-depth info | M8 |
| Save with annotations, save viewport, screen capture | M5 (export view / diff), rest M8 |
| Rotate / flip / crop, filters, false-colour LUT, per-image undo history, overwrite original | M8, on request |
| Image analysis (histogram, statistics, FFT), segmentation, 3D surface plot | not planned (difference histogram is in M5) |
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

**M7 — Compare IV: folders and automation**
- Pair two folders by file name (or by order): table of pairs with metrics, sorted worst-first,
  step through pairs; missing and extra files flagged.
- Reference mode: pin one image and step through a folder against it.
- Batch metrics across all cores; report export.
- `Fiv.Cli`: `fiv compare A B --metric psnr,ssim --diff out.png --json` for scripts and agents.

**M8 — Carry-over features**
- Picked from section 8 as needed: measurements, line profile A/B, quality-metric columns,
  image information, crosshair styles, calibration, remaining file operations.

## 10. Risks

| Risk | Mitigation |
|---|---|
| A binding (ImGui, D3D11, WIC) does not work under Native AOT | Found in the M0 bake-off before anything is built on it; fallback is C++ with the identical stack |
| Full-resolution JPEG decode is bound by single-thread libjpeg-turbo speed (my estimate: 100–150 MP/s here) | Two-stage loading hides it; it cannot be removed |
| PNG and TIFF have no reduced-size decode | Faster native PNG decoder if measured worthwhile; cached thumbnail shown at once |
| Cold start dominated by GPU driver load during device creation | Decode runs in parallel with it; optional stay-resident mode |
| First thumbnail pass on HDD or network is I/O-bound | Embedded EXIF thumbnails first; persistent local cache makes it a one-time cost |
| ImGui: non-native look, weaker text input and accessibility; render-on-demand needs discipline | Accepted for speed; Avalonia is the alternative app layer |
| WebP / HEIC / AVIF depend on installed Windows codec extensions | Detect and report; bundle libwebp if needed |
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
