# FastImageViewer2 — Plan

Status: **M0 closed; M1 working and measured, with a few items open** (listed under M1 in
section 9). Written 2026-10-09. Language decided: C# / .NET 10 Native AOT (section 3.1).
Measured numbers are in sections 2.1 (M0) and 2.2 (the viewer itself). Build and keys: `README.md`.

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
  native/          libjpeg-turbo static library (later: optional decoders)
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
- **Upload.** Images become tiled textures (tiles ≤ 8192 px) so files beyond the 16384 px D3D11
  limit work from day one. Mipmaps are generated on the GPU for clean down-scaling.
- **Prefetch and cache.** The next and previous images (two ahead in the direction of travel) are
  decoded in the background. Decoded images live in a true least-recently-used cache with a byte
  budget (default 4 GB RAM, 2 GB VRAM, configurable).
- **Rapid navigation.** While a key is held, each image is shown from whatever is ready
  (thumbnail or preview); stale decodes are cancelled between scanlines.
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
- **Change tracking:** a debounced file-system watcher updates the index incrementally.

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
- Test corpus: generated folders of 1k / 10k / 100k files; JPEG at 12 / 24 / 50 MP; PNG 8- and
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

**M3 — Thumbnails and browser**
- Thumbnail pipeline, cache pack files, GPU atlas.
- Browser panel: virtualised grid and details list, column sort, type-to-filter, multi-select,
  thumbnail size, filmstrip.
- Basic file operations, settings, recent items, key map, docking layout, fullscreen.
- Exit: thumbnail budgets met; scrolling a 100k folder stays at refresh rate while thumbnails
  stream in.

**M4 — Compare I: views and switching**
- Compare set of 2…N images from selection, drag-in or command line.
- Layouts 1-up, 2-up, grid; linked zoom/pan with toggle; linked cursor with per-image readout.
- Flicker: keys 1–9 switch image in place, hold-to-peek, auto-blink at a set rate.
- Split / wipe with draggable divider; opacity blend; checkerboard interleave.
- Display controls applied identically to all images: exposure, gamma, window/level, channel
  isolation (R, G, B, A, luma).
- Different sizes: pixel-aligned (top-left or centre) or scaled to fit.

**M5 — Compare II: difference and metrics**
- Difference modes: absolute, signed (diverging colormap), squared, relative; gain; per channel
  or luma; threshold mask that highlights pixels differing by more than T.
- Colormaps and colour bar in real units; swap A/B.
- Metrics panel for the whole image or a region: MAE, MSE, PSNR, max error, % pixels over
  threshold, SSIM, per channel; difference histogram; SSIM heat-map view.
- Pixel inspector: per-pixel values of A, B and Δ drawn in the cells at high zoom.
- Export: difference / composite at full resolution, view to clipboard, metrics to CSV / JSON.

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
