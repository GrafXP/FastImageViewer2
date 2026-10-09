using System.Collections.Concurrent;
using System.Diagnostics;
using Fiv.Imaging;
using Fiv.Indexing;
using Fiv.Render;
using Fiv.Scheduling;
using Windows.Win32.Graphics.Direct3D11;

namespace Fiv.App;

/// <summary>The decoded pixels an upload reads. It holds its own references, so the cache may drop the image meanwhile.</summary>
internal sealed class UploadTicket(DecodedImage image, MipChain? mips)
{
    public WorkItem Job = null!;
    public DecodedImage Image { get; } = image;
    public MipChain? Mips { get; } = mips;

    public void Release()
    {
        Image.Dispose();
        Mips?.Dispose();
    }
}

/// <summary>
/// Decodes, uploads and caches images. The window says which images it wants and how urgently
/// (<see cref="Request"/>); the pipeline runs decodes on a pool of workers and texture uploads on
/// one more thread, always the most urgent first, stops work that is no longer wanted, and keeps
/// finished images until the memory budgets are exceeded, least recently used first.
///
/// Everything here runs on the UI thread except the Run* methods, which report back through a
/// queue that <see cref="Drain"/> empties.
/// </summary>
internal sealed unsafe class ImagePipeline(Action wake, long ramBudget, long vramBudget)
{
    /// <summary>
    /// One wanted image. Rank 0 is the one to show, higher ranks are prefetched in that order.
    /// <see cref="Full"/> asks for the full-resolution image as well as the preview.
    /// </summary>
    public readonly record struct Want(string Path, long Size, long Modified, int Rank, bool Full);

    enum Kind { Preview, Full, PreviewUpload, FullUpload }

    sealed class Completion(LoadedImage entry, WorkItem job, Kind kind)
    {
        public readonly LoadedImage Entry = entry;
        public readonly WorkItem Job = job;
        public readonly Kind Kind = kind;
        public bool Cancelled;
        public string? Error;
        public ImageInfo Info;
        public long FileSize, ReadAt, DoneAt;
        public double ReadMs, DecodeMs, MipMs, UploadMs;
        public DecodedImage? Image;
        public MipChain? Mips;
        public (float Min, float Max) Range = (0, 1);
        public GpuImage? Gpu;
    }

    // Two cores are left for the UI thread and the uploader.
    readonly WorkScheduler _decoders = new(Math.Clamp(Environment.ProcessorCount - 2, 2, 8), "fiv decode");
    readonly WorkScheduler _uploader = new(1, "fiv upload");
    readonly Dictionary<string, LoadedImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentQueue<Completion> _completions = new();
    readonly List<LoadedImage> _wanted = [];
    readonly List<LoadedImage> _scratch = [];
    nint _device;
    int _viewWidth = 1, _viewHeight = 1;
    long _clock;

    /// <summary>The image on screen. It is never evicted; if its file changes it lives on outside the cache until replaced.</summary>
    public LoadedImage? Pinned;

    /// <summary>How many bytes of decoded pixels and of textures may stay cached beyond what is wanted right now.</summary>
    public long RamBudget = ramBudget, VramBudget = vramBudget;

    public int Count => _cache.Count;
    public long CpuBytes { get; private set; }
    public long GpuBytes { get; private set; }
    public int DecodeWorkers => _decoders.MaxWorkers;

    public LoadedImage? Find(string path) => _cache.GetValueOrDefault(path);

    /// <summary>Textures can be created from now on.</summary>
    public void SetDevice(GraphicsDevice gpu)
    {
        _device = (nint)gpu.Device;
        foreach (LoadedImage entry in _wanted) Schedule(entry);
    }

    /// <summary>
    /// Replaces the set of wanted images and returns the entry of rank 0. Work for images that
    /// are no longer wanted is cancelled; what they already have stays cached.
    /// </summary>
    public LoadedImage? Request(ReadOnlySpan<Want> wants, int viewWidth, int viewHeight)
    {
        _viewWidth = viewWidth;
        _viewHeight = viewHeight;
        _scratch.Clear();
        _scratch.AddRange(_wanted);
        foreach (LoadedImage entry in _wanted)
        {
            entry.Rank = int.MaxValue;
            entry.WantFull = false;
        }
        _wanted.Clear();

        LoadedImage? first = null;
        foreach (ref readonly Want want in wants)
        {
            LoadedImage entry = GetOrAdd(want);
            if (entry.Rank != int.MaxValue) continue; // the same file twice in a short list: the better rank stands
            entry.Rank = want.Rank;
            entry.WantFull = want.Full;
            entry.LastUsed = ++_clock;
            _wanted.Add(entry);
            if (want.Rank == 0) first = entry;
        }
        foreach (LoadedImage entry in _scratch)
            if (entry.Rank == int.MaxValue && !entry.Disposed) CancelJobs(entry);
        foreach (LoadedImage entry in _wanted) Schedule(entry);
        Trim();
        return first;
    }

    LoadedImage GetOrAdd(in Want want)
    {
        if (_cache.TryGetValue(want.Path, out LoadedImage? entry))
        {
            if (entry.ListedSize == 0 && entry.ListedModified == 0)
            {
                entry.ListedSize = want.Size;
                entry.ListedModified = want.Modified;
            }
            bool changedOnDisk = want.Size != 0 && (want.Size != entry.ListedSize || want.Modified != entry.ListedModified);
            if (!changedOnDisk) return entry;
            Evict(entry);
        }
        entry = new LoadedImage(want.Path, want.Size, want.Modified, ImageFormats.IsJpegFileName(want.Path));
        _cache[want.Path] = entry;
        return entry;
    }

    /// <summary>Starts, re-prioritises or stops the work of a wanted image so that it matches its rank.</summary>
    void Schedule(LoadedImage e)
    {
        bool previewPath = e.JpegName && !e.HasNoPreview;
        bool havePicture = e.Full != null || e.GpuFull != null;

        if (previewPath && !e.PreviewResolved && !havePicture)
        {
            int priority = e.Rank * 2;
            if (e.PreviewJob == null)
            {
                int viewWidth = _viewWidth, viewHeight = _viewHeight;
                e.PreviewJob = _decoders.Enqueue(priority, job => RunPreview(e, job, viewWidth, viewHeight));
            }
            else
            {
                _decoders.SetPriority(e.PreviewJob, priority);
            }
        }

        // Without a preview the full image is the only picture, so it is as urgent as a preview.
        bool needFull = e.WantFull || !previewPath;
        int fullPriority = previewPath ? 100 + e.Rank : e.Rank * 2 + 1;
        if (!e.FullResolved)
        {
            if (!needFull)
            {
                if (e.FullJob != null) _decoders.Cancel(e.FullJob);
                e.FullJob = null;
            }
            else if (e.FullJob == null)
            {
                e.FullJob = _decoders.Enqueue(fullPriority, job => RunFull(e, job));
            }
            else
            {
                _decoders.SetPriority(e.FullJob, fullPriority);
            }
        }

        if (_device == 0) return;
        if (e.Preview != null && e.GpuPreview == null && e.GpuFull == null)
        {
            if (e.PreviewUpload == null) e.PreviewUpload = EnqueueUpload(e, e.Preview, null, e.Rank * 2, full: false);
            else _uploader.SetPriority(e.PreviewUpload.Job, e.Rank * 2);
        }
        if (e.Full != null && e.GpuFull == null && !e.FullUploadFailed)
        {
            if (!needFull)
            {
                CancelUpload(ref e.FullUpload);
            }
            else if (e.FullUpload == null)
            {
                e.FullUpload = EnqueueUpload(e, e.Full, e.Mips, fullPriority, full: true);
            }
            else
            {
                _uploader.SetPriority(e.FullUpload.Job, fullPriority);
            }
        }
    }

    UploadTicket EnqueueUpload(LoadedImage entry, DecodedImage image, MipChain? mips, int priority, bool full)
    {
        image.AddReference();
        mips?.AddReference();
        var ticket = new UploadTicket(image, mips);
        ticket.Job = _uploader.Enqueue(priority, job => RunUpload(entry, ticket, job, full));
        return ticket;
    }

    void CancelUpload(ref UploadTicket? ticket)
    {
        if (ticket == null) return;
        if (_uploader.Cancel(ticket.Job)) ticket.Release(); // it will never run, so nobody else releases
        ticket = null;
    }

    void CancelJobs(LoadedImage e)
    {
        if (e.PreviewJob != null) _decoders.Cancel(e.PreviewJob);
        if (e.FullJob != null) _decoders.Cancel(e.FullJob);
        e.PreviewJob = e.FullJob = null;
        CancelUpload(ref e.PreviewUpload);
        CancelUpload(ref e.FullUpload);
        e.ReleaseFile();
    }

    void Evict(LoadedImage e)
    {
        CancelJobs(e);
        _cache.Remove(e.Path);
        e.Rank = int.MaxValue;
        _wanted.Remove(e);
        if (e == Pinned) e.Orphaned = true; // still on screen; the window disposes it when it shows something else
        else e.Dispose();
    }

    /// <summary>Evicts unwanted images, least recently used first, until both budgets are met.</summary>
    void Trim()
    {
        long cpu = 0, gpu = 0;
        foreach (LoadedImage e in _cache.Values)
        {
            cpu += e.CpuBytes;
            gpu += e.GpuBytes;
        }
        if (cpu > RamBudget || gpu > VramBudget)
        {
            _scratch.Clear();
            foreach (LoadedImage e in _cache.Values)
                if (e.Rank == int.MaxValue && e != Pinned) _scratch.Add(e);
            _scratch.Sort(static (a, b) => a.LastUsed.CompareTo(b.LastUsed));
            foreach (LoadedImage e in _scratch)
            {
                if (cpu <= RamBudget && gpu <= VramBudget) break;
                cpu -= e.CpuBytes;
                gpu -= e.GpuBytes;
                Evict(e);
            }
        }
        CpuBytes = cpu;
        GpuBytes = gpu;
    }

    /// <summary>
    /// Takes in what the workers finished. Returns true if any image changed;
    /// <paramref name="watchedChanged"/> says whether <paramref name="watched"/> was one of them. UI thread.
    /// </summary>
    public bool Drain(LoadedImage? watched, out bool watchedChanged)
    {
        bool changed = false;
        watchedChanged = false;
        while (_completions.TryDequeue(out Completion? c))
        {
            LoadedImage e = c.Entry;
            switch (c.Kind)
            {
                case Kind.Preview when e.PreviewJob == c.Job: e.PreviewJob = null; break;
                case Kind.Full when e.FullJob == c.Job: e.FullJob = null; break;
                case Kind.PreviewUpload when e.PreviewUpload?.Job == c.Job: e.PreviewUpload = null; break;
                case Kind.FullUpload when e.FullUpload?.Job == c.Job: e.FullUpload = null; break;
            }
            if (e.Disposed || c.Cancelled)
            {
                c.Image?.Dispose();
                c.Mips?.Dispose();
                c.Gpu?.Dispose();
                continue;
            }
            changed = true;
            watchedChanged |= e == watched;
            switch (c.Kind)
            {
                case Kind.Preview:
                    Describe(e, c);
                    e.PreviewResolved = true;
                    if (c.Error != null || c.Image == null)
                    {
                        e.HasNoPreview = true; // not a JPEG after all, too small to reduce, or unreadable: the full decode decides
                    }
                    else if (e.Full != null || e.GpuFull != null)
                    {
                        c.Image.Dispose(); // the full image got there first
                    }
                    else
                    {
                        e.Preview = c.Image;
                        e.PreviewDecodeMs = c.DecodeMs;
                        e.PreviewDecodedAt = c.DoneAt;
                        e.PreviewWidth = c.Image.Width;
                        e.PreviewHeight = c.Image.Height;
                    }
                    break;

                case Kind.Full:
                    e.FullResolved = true;
                    if (c.Error != null || c.Image == null)
                    {
                        e.Error = c.Error ?? "The image could not be decoded.";
                        break;
                    }
                    Describe(e, c);
                    e.Width = c.Image.SourceWidth;
                    e.Height = c.Image.SourceHeight;
                    e.Full = c.Image;
                    e.Mips = c.Mips;
                    e.ValueRange = c.Range;
                    e.FullDecodeMs = c.DecodeMs;
                    e.MipMs = c.MipMs;
                    e.FullDecodedAt = c.DoneAt;
                    break;

                case Kind.PreviewUpload:
                    if (c.Gpu == null) break; // the full upload will report a real problem
                    if (e.GpuPreview != null || e.GpuFull != null) c.Gpu.Dispose();
                    else e.GpuPreview = c.Gpu;
                    break;

                case Kind.FullUpload:
                    if (c.Gpu == null)
                    {
                        e.FullUploadFailed = true;
                        if (e.GpuPreview == null) e.Error = "Not enough video memory for this image: " + c.Error;
                        break;
                    }
                    e.GpuFull = c.Gpu;
                    e.UploadMs = c.UploadMs;
                    CancelUpload(ref e.PreviewUpload);
                    e.GpuPreview?.Dispose();
                    e.GpuPreview = null;
                    e.Preview?.Dispose();
                    e.Preview = null;
                    e.Mips?.Dispose(); // the GPU has its own copy now
                    e.Mips = null;
                    break;
            }
            if (e.PreviewJob == null && e.FullJob == null) e.ReleaseFile();
        }
        if (changed)
        {
            foreach (LoadedImage entry in _wanted) Schedule(entry);
            Trim();
        }
        return changed;
    }

    static void Describe(LoadedImage e, Completion c)
    {
        if (c.Info.Width > 0)
        {
            e.Width = c.Info.Width;
            e.Height = c.Info.Height;
            e.Orientation = c.Info.Orientation;
        }
        if (c.FileSize > 0) e.FileSize = c.FileSize;
        if (c.ReadAt != 0 && e.FileReadAt == 0)
        {
            e.FileReadAt = c.ReadAt;
            e.ReadMs = c.ReadMs;
        }
    }

    // ---------------------------------------------------------------- worker threads

    void Report(Completion c)
    {
        c.DoneAt = Stopwatch.GetTimestamp();
        _completions.Enqueue(c);
        wake();
    }

    void RunPreview(LoadedImage entry, WorkItem job, int viewWidth, int viewHeight)
    {
        var c = new Completion(entry, job, Kind.Preview);
        try
        {
            if (job.IsCancelled)
            {
                c.Cancelled = true;
                return;
            }
            FileBytes file = entry.AcquireFile(out c.ReadMs, out c.ReadAt);
            try
            {
                c.FileSize = (long)file.Length;
                c.Info = ImageDecoding.Probe(file);
                if (ImageDecoding.PreviewDenominator(c.Info, viewWidth, viewHeight) > 1)
                {
                    long started = Stopwatch.GetTimestamp();
                    c.Image = ImageDecoding.DecodePreview(file, c.Info, viewWidth, viewHeight, job.CancelFlag);
                    c.DecodeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    c.Cancelled = c.Image == null;
                }
            }
            finally
            {
                file.Release();
            }
        }
        catch (Exception e)
        {
            c.Error = e.Message;
        }
        finally
        {
            Report(c);
        }
    }

    void RunFull(LoadedImage entry, WorkItem job)
    {
        var c = new Completion(entry, job, Kind.Full);
        try
        {
            if (job.IsCancelled)
            {
                c.Cancelled = true;
                return;
            }
            FileBytes file = entry.AcquireFile(out c.ReadMs, out c.ReadAt);
            try
            {
                c.FileSize = (long)file.Length;
                c.Info = ImageDecoding.Probe(file);
                long started = Stopwatch.GetTimestamp();
                DecodedImage? image = ImageDecoding.DecodeFull(file, c.Info, job.CancelFlag);
                c.DecodeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (image == null || job.IsCancelled)
                {
                    image?.Dispose();
                    c.Cancelled = true;
                    return;
                }
                started = Stopwatch.GetTimestamp();
                c.Image = image;
                c.Mips = new MipChain(image);
                c.Range = ImageStatistics.ValueRange(image);
                c.MipMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            finally
            {
                file.Release();
            }
        }
        catch (Exception e)
        {
            c.Error = e.Message;
        }
        finally
        {
            Report(c);
        }
    }

    void RunUpload(LoadedImage entry, UploadTicket ticket, WorkItem job, bool full)
    {
        var c = new Completion(entry, job, full ? Kind.FullUpload : Kind.PreviewUpload);
        try
        {
            if (job.IsCancelled)
            {
                c.Cancelled = true;
                return;
            }
            long started = Stopwatch.GetTimestamp();
            c.Gpu = GpuImage.Create((ID3D11Device*)_device, ticket.Image, ticket.Mips, job.CancelFlag);
            c.UploadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            c.Cancelled = c.Gpu == null;
        }
        catch (Exception e)
        {
            c.Error = e.Message;
        }
        finally
        {
            ticket.Release();
            Report(c);
        }
    }
}
