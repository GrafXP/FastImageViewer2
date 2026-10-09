using System.Collections.Concurrent;
using Fiv.Imaging;
using Fiv.Indexing;
using Fiv.Render;
using Fiv.Scheduling;
using Fiv.Thumbnails;

namespace Fiv.App;

/// <summary>
/// One place thumbnails are shown at one size (the grid, the filmstrip): its atlas and the
/// requests under way for it.
/// </summary>
internal sealed class ThumbnailSurface(int cellSize)
{
    public ThumbnailAtlas? Atlas;
    public int CellSize = cellSize;
    public readonly Dictionary<ulong, (WorkItem Job, int Frame)> Requests = [];
    public readonly HashSet<ulong> Failed = [];
    public int Frame;
}

/// <summary>
/// Supplies the thumbnails the panels draw. A panel asks for each thumbnail it wants every frame;
/// what is not in the atlas yet is loaded on low-priority workers (from the folder's pack, or made
/// and added to it), most urgent first, and uploaded on the UI thread a few per frame. Requests a
/// panel stops asking for are cancelled, so scrolling far never leaves a queue behind.
/// UI thread only, except Run.
/// </summary>
internal sealed unsafe class ThumbnailService(Action wake, string cacheRoot, int workers)
{
    readonly record struct Result(ThumbnailSurface Surface, ulong Key, DecodedImage? Image, bool Provisional, int CellSize, bool Failed);

    // Below normal priority: thumbnails never take a core from the image being opened.
    readonly WorkScheduler _workers = new(workers, "fiv thumb", ThreadPriority.BelowNormal);
    readonly ConcurrentQueue<Result> _results = new();
    ThumbnailPack? _pack;
    ulong _folderHash;
    long _lastFlush;

    public ThumbnailPack? Pack => _pack;

    public string CacheRoot => cacheRoot;

    /// <summary>Switches to the pack of another folder (opened by the caller, off the UI thread). The previous pack is closed.</summary>
    public void SetPack(ThumbnailPack? pack)
    {
        if (pack == _pack) return;
        _pack?.Dispose();
        _pack = pack;
        _folderHash = pack == null ? 0 : ThumbnailPack.Hash(pack.Folder);
        _probes.Clear();
    }

    readonly Dictionary<int, WorkItem> _probes = [];
    FolderListing? _probeListing;

    /// <summary>
    /// Asks for the pixel dimensions of a file to be read from its header. They appear in the
    /// listing when known. For lists that show dimensions without showing thumbnails.
    /// </summary>
    public void RequestDimensions(FolderIndex folder, int position)
    {
        FolderListing listing = folder.Listing;
        if (_probeListing != listing)
        {
            foreach (WorkItem old in _probes.Values) _workers.Cancel(old);
            _probes.Clear();
            _probeListing = listing;
        }
        int listingIndex = folder.ListingIndexAt(position);
        if (_probes.ContainsKey(listingIndex)) return;
        if (_probes.Count > 512)
        {
            // Forget the ones that are done; the listing has their result.
            foreach (int done in _probes.Keys.Where(index => listing.TryGetDimensions(index, out _, out _)).ToList()) _probes.Remove(done);
            if (_probes.Count > 512) return;
        }
        string path = folder.PathAt(position);
        long size = folder.SizeAt(position), modified = folder.ModifiedAt(position);
        ThumbnailPack? pack = _pack;
        _probes[listingIndex] = _workers.Enqueue(3000, job =>
        {
            if (ImageProbe.TryReadSize(path, out int width, out int height, out _))
            {
                listing.SetDimensions(listingIndex, width, height);
                // Kept in the pack, so that the folder lists with dimensions next time. (Rotation is not known from a header probe.)
                if (pack != null && !pack.TryGet(Path.GetFileName(path.AsSpan()), size, modified, out _))
                    pack.Add(Path.GetFileName(path.AsSpan()), size, modified, width, height, Orientation.Normal, 0, 0, default);
            }
            else
            {
                listing.SetDimensions(listingIndex, -1, -1); // looked at and not readable: do not ask again
            }
            wake();
        });
    }

    /// <summary>Identifies a file version: the same file after a change gets a new thumbnail.</summary>
    ulong Key(FolderIndex folder, int position) =>
        ThumbnailPack.Hash(folder.NameAt(position)) ^ _folderHash * 0x9E3779B97F4A7C15 ^
        (ulong)folder.SizeAt(position) * 0xC2B2AE3D27D4EB4F ^ (ulong)folder.ModifiedAt(position) * 0x165667B19E3779F9;

    public void BeginFrame(ThumbnailSurface surface)
    {
        surface.Frame++;
        surface.Atlas?.BeginFrame();
    }

    /// <summary>
    /// The thumbnail of a file if it is in the atlas. If not, or if only a provisional one is,
    /// it is requested; <paramref name="priority"/> orders the requests of a frame (lower first).
    /// Returns false while there is nothing to draw.
    /// </summary>
    public bool TryGet(ThumbnailSurface surface, FolderIndex folder, int position, int priority, out AtlasSlot slot, out bool failed)
    {
        slot = default;
        failed = false;
        if (surface.Atlas == null || _pack == null) return false;
        ulong key = Key(folder, position);
        bool have = surface.Atlas.TryGet(key, out slot);
        if (have && !slot.Provisional) return true;
        if (surface.Failed.Contains(key))
        {
            failed = true;
            return have;
        }

        if (surface.Requests.TryGetValue(key, out var request))
        {
            surface.Requests[key] = (request.Job, surface.Frame);
            _workers.SetPriority(request.Job, priority);
        }
        else
        {
            ThumbnailPack pack = _pack;
            string path = folder.PathAt(position);
            long size = folder.SizeAt(position), modified = folder.ModifiedAt(position);
            int listingIndex = folder.ListingIndexAt(position);
            FolderListing listing = folder.Listing;
            int cell = surface.CellSize;
            WorkItem job = _workers.Enqueue(priority, item => Run(item, surface, pack, listing, listingIndex, path, size, modified, key, cell));
            surface.Requests[key] = (job, surface.Frame);
        }
        return have;
    }

    /// <summary>Ends a frame: requests that were not repeated in it or in the frame before are cancelled.</summary>
    public void EndFrame(ThumbnailSurface surface)
    {
        // What was not asked for in this frame or the last is no longer wanted.
        List<ulong>? stale = null;
        foreach (var (key, request) in surface.Requests)
            if (request.Frame < surface.Frame - 1) (stale ??= []).Add(key);
        if (stale == null) return;
        foreach (ulong key in stale)
        {
            _workers.Cancel(surface.Requests[key].Job);
            surface.Requests.Remove(key);
        }
    }

    /// <summary>
    /// Moves finished thumbnails into their atlases, at most <paramref name="budget"/> per call.
    /// Returns true if anything arrived, i.e. if the panels should be redrawn.
    /// </summary>
    public bool Upload(int budget = 96)
    {
        bool any = false;
        while (budget-- > 0 && _results.TryDequeue(out Result result))
        {
            ThumbnailSurface surface = result.Surface;
            if (!result.Provisional) surface.Requests.Remove(result.Key);
            if (result.Failed) surface.Failed.Add(result.Key);
            if (result.Image != null)
            {
                if (result.CellSize == surface.CellSize && surface.Atlas != null) surface.Atlas.Put(result.Key, result.Image, result.Provisional);
                result.Image.Dispose();
            }
            any = true;
        }
        if (!_results.IsEmpty) wake(); // more next frame
        long now = Environment.TickCount64;
        if (any && now - _lastFlush > 3000)
        {
            _lastFlush = now;
            _pack?.Flush();
        }
        return any;
    }

    /// <summary>Changes the size thumbnails are shown at on a surface; they are all made again from the pack.</summary>
    public void SetCellSize(ThumbnailSurface surface, int cellSize, GraphicsDevice gpu)
    {
        if (surface.Atlas != null && surface.CellSize == cellSize) return;
        foreach (var request in surface.Requests.Values) _workers.Cancel(request.Job);
        surface.Requests.Clear();
        surface.CellSize = cellSize;
        if (surface.Atlas == null) surface.Atlas = new ThumbnailAtlas(gpu, cellSize);
        else surface.Atlas.Reset(cellSize);
    }

    public void Dispose()
    {
        _pack?.Dispose();
        _pack = null;
    }

    // ---------------------------------------------------------------- worker threads

    void Run(WorkItem job, ThumbnailSurface surface, ThumbnailPack pack, FolderListing listing, int listingIndex,
        string path, long size, long modified, ulong key, int cell)
    {
        try
        {
            if (job.IsCancelled) return;
            DecodedImage? image = ThumbnailLoader.Load(pack, path, Path.GetFileName(path.AsSpan()), size, modified, cell, job.CancelFlag,
                embedded =>
                {
                    _results.Enqueue(new Result(surface, key, embedded, Provisional: true, cell, Failed: false));
                    wake();
                },
                out PackEntry entry, out _);
            if (image == null) return; // cancelled
            if (entry.Width > 0) listing.SetDimensions(listingIndex, entry.Orientation.SwapsAxes() ? entry.Height : entry.Width,
                entry.Orientation.SwapsAxes() ? entry.Width : entry.Height);
            _results.Enqueue(new Result(surface, key, image, Provisional: false, cell, Failed: false));
        }
        catch (Exception)
        {
            // Unreadable or not an image after all: remembered, so that it is not tried on every frame.
            _results.Enqueue(new Result(surface, key, null, Provisional: false, cell, Failed: true));
        }
        wake();
    }
}
