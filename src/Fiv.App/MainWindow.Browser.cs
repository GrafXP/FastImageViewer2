using System.Numerics;
using System.Text.Unicode;
using Fiv.Indexing;
using Fiv.Render;
using Hexa.NET.ImGui;

namespace Fiv.App;

// The folder panel (thumbnail grid or details list, filter, sort, subfolders) and the filmstrip.
// Both draw only the items in view, however many the folder has, and ask the thumbnail service
// each frame for what they show and for what is about to scroll in.
internal sealed unsafe partial class MainWindow
{
    enum BrowserMode { Grid, List }

    ThumbnailService? _thumbs;
    readonly ThumbnailSurface _gridSurface = new(128), _stripSurface = new(96);
    bool _showBrowser, _showFilmstrip;
    BrowserMode _browserMode = BrowserMode.Grid;
    int _thumbSize = 128;
    int _gridColumns = 1;
    bool _scrollBrowserToCurrent = true, _scrollStripToCurrent = true, _syncTableSort = true;
    readonly byte[] _filter = new byte[128];
    string _filterText = "";
    FolderIndex? _folderAll; // the folder as read; _folder is the filtered view the panels and next/previous use

    // Selection, as indices into the folder listing: the same files stay selected when the view is sorted or filtered.
    readonly HashSet<int> _selection = [];
    FolderListing? _selectionListing;
    int _selectionAnchor = -1;

    const uint ColorSelected = 0xFF805A2A, ColorHover = 0x40FFFFFF, ColorCurrent = 0xFFFFB060, ColorPlaceholder = 0xFF2C2A2A,
        ColorFailed = 0xFF30305A, ColorText = 0xFFE0E0E0, ColorTextDim = 0xFF909090;

    bool IsSelected(FolderIndex folder, int position) =>
        _selectionListing == folder.Listing && _selection.Contains(folder.ListingIndexAt(position));

    /// <summary>A click on an item in a panel: plain selects and shows it, Ctrl adds or removes it, Shift selects the range from the last one.</summary>
    void ClickItem(FolderIndex folder, int position, bool control, bool shift)
    {
        if (_selectionListing != folder.Listing)
        {
            _selection.Clear();
            _selectionListing = folder.Listing;
            _selectionAnchor = -1;
        }
        int item = folder.ListingIndexAt(position);
        if (shift && _selectionAnchor >= 0 && _selectionAnchor < folder.Count)
        {
            if (!control) _selection.Clear();
            for (int i = Math.Min(_selectionAnchor, position); i <= Math.Max(_selectionAnchor, position); i++) _selection.Add(folder.ListingIndexAt(i));
            Navigate(position);
        }
        else if (control)
        {
            if (!_selection.Remove(item)) _selection.Add(item);
            _selectionAnchor = position;
        }
        else
        {
            _selection.Clear();
            _selection.Add(item);
            _selectionAnchor = position;
            Navigate(position);
        }
    }

    /// <summary>The selection follows the image on show unless several are selected.</summary>
    void SelectCurrent()
    {
        if (_folder == null || _fileIndex < 0 || _fileIndex >= _folder.Count) return;
        if (_selectionListing == _folder.Listing && _selection.Count > 1) return;
        _selectionListing = _folder.Listing;
        _selection.Clear();
        _selection.Add(_folder.ListingIndexAt(_fileIndex));
        _selectionAnchor = _fileIndex;
    }

    void SelectAll(bool invert)
    {
        if (_folder == null) return;
        if (_selectionListing != _folder.Listing) _selection.Clear();
        _selectionListing = _folder.Listing;
        for (int i = 0; i < _folder.Count; i++)
        {
            int item = _folder.ListingIndexAt(i);
            if (!invert) _selection.Add(item);
            else if (!_selection.Remove(item)) _selection.Add(item);
        }
    }

    /// <summary>Writes text as null-terminated UTF-8 into the shared buffer at <paramref name="offset"/>; returns its length in bytes.</summary>
    int Utf8Of(ReadOnlySpan<char> text, int offset = 0)
    {
        Utf8.FromUtf16(text, _utf8.AsSpan(offset, _utf8.Length - offset - 1), out _, out int written);
        _utf8[offset + written] = 0;
        return written;
    }

    void EnsureThumbnails()
    {
        if (_thumbs != null || _gpu == null) return;
        _thumbs = new ThumbnailService(() => Post(WM_APP_THUMBS), _options.ThumbsDir,
            Math.Max(2, _pipeline.DecodeWorkers - 2));
        if (_pendingPack != null)
        {
            _thumbs.SetPack(_pendingPack);
            _pendingPack = null;
        }
    }

    // ---------------------------------------------------------------- folder panel

    void DrawBrowser()
    {
        float scale = (float)_dpiScale;
        FolderIndex? folder = _folder;

        // Where we are, and the way out and in.
        string? directory = _folderAll?.Directory ?? (_files.Count == 1 ? Path.GetDirectoryName(_files[0]) : null);
        if (ImGui.Button("Up"u8) && directory != null && Path.GetDirectoryName(directory) is { Length: > 0 } parent) OpenFolder(parent);
        ImGui.SameLine();
        int subfolders = _folderAll?.Listing.Directories.Count ?? 0;
        ImGui.BeginDisabled(subfolders == 0);
        if (Utf8.TryWrite(_utf8, $"Folders ({subfolders})\0", out _))
        {
            fixed (byte* label = _utf8)
                if (ImGui.Button(label)) ImGui.OpenPopup("folders"u8);
        }
        ImGui.EndDisabled();
        if (ImGui.BeginPopup("folders"u8))
        {
            if (_folderAll != null)
            {
                foreach (string name in _folderAll.Listing.Directories)
                {
                    Utf8Of(name);
                    fixed (byte* label = _utf8)
                        if (ImGui.Selectable(label)) OpenFolder(Path.Combine(_folderAll.Directory, name));
                }
            }
            ImGui.EndPopup();
        }
        ImGui.SameLine();
        if (directory != null)
        {
            Utf8Of(directory);
            fixed (byte* text = _utf8) ImGui.TextDisabled(text);
        }
        else
        {
            ImGui.TextDisabled(FileCount > 1 ? "Opened files"u8 : "No folder"u8);
        }

        // Filter, view, size.
        ImGui.SetNextItemWidth(170 * scale);
        fixed (byte* buffer = _filter)
        {
            if (ImGui.InputTextWithHint("##filter"u8, "filter by name"u8, buffer, (nuint)_filter.Length))
            {
                _filterText = System.Text.Encoding.UTF8.GetString(_filter, 0, Array.IndexOf(_filter, (byte)0) is >= 0 and int end ? end : _filter.Length);
                ApplyView();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button(_browserMode == BrowserMode.Grid ? "List"u8 : "Grid"u8))
        {
            _browserMode = _browserMode == BrowserMode.Grid ? BrowserMode.List : BrowserMode.Grid;
            _scrollBrowserToCurrent = true;
            _syncTableSort = true;
        }
        if (_browserMode == BrowserMode.Grid)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120 * scale);
            int step = _thumbSize / 16;
            if (ImGui.SliderInt("##size"u8, ref step, 4, 16, "")) _thumbSize = step * 16; // whole steps: thumbnails are stored at exactly the size shown
        }
        if (folder != null)
        {
            ImGui.SameLine();
            int n = _filterText.Length > 0 && _folderAll != null
                ? (Utf8.TryWrite(_utf8, $"{folder.Count} of {_folderAll.Count}\0", out int a) ? a : 0)
                : (Utf8.TryWrite(_utf8, $"{folder.Count} images\0", out int b) ? b : 0);
            if (n > 0)
            {
                fixed (byte* text = _utf8) ImGui.TextDisabled(text);
            }
        }

        if (folder == null || folder.Count == 0)
        {
            ImGui.TextDisabled(_filterText.Length > 0 ? "Nothing matches the filter."u8 : FileCount > 1 ? "Several files were opened; there is no folder to list."u8 : "No images."u8);
            return;
        }
        EnsureThumbnails();
        if (_browserMode == BrowserMode.Grid) DrawGrid(folder);
        else DrawList(folder);
    }

    void DrawGrid(FolderIndex folder)
    {
        float scale = (float)_dpiScale;
        if (_thumbs == null || _gpu == null) return;
        _thumbs.SetCellSize(_gridSurface, _thumbSize, _gpu);
        int thumb = _thumbSize;
        float pad = MathF.Round(6 * scale), label = ImGui.GetTextLineHeight() + MathF.Round(4 * scale);

        if (!ImGui.BeginChild(ImGui.GetID("grid"u8), ImGui.GetContentRegionAvail()))
        {
            ImGui.EndChild();
            return;
        }
        float width = ImGui.GetContentRegionAvail().X;
        float cellWidth = thumb + 2 * pad, cellHeight = thumb + label + 2 * pad;
        int columns = Math.Max(1, (int)(width / cellWidth));
        cellWidth = MathF.Floor(width / columns);
        int count = folder.Count, rows = (count + columns - 1) / columns;
        _gridColumns = columns;

        float viewHeight = ImGui.GetWindowHeight(), scrollY = ImGui.GetScrollY();
        if (_scrollBrowserToCurrent && _fileIndex >= 0)
        {
            _scrollBrowserToCurrent = false;
            float top = _fileIndex / columns * cellHeight;
            if (top < scrollY) ImGui.SetScrollY(scrollY = top);
            else if (top + cellHeight > scrollY + viewHeight) ImGui.SetScrollY(scrollY = top + cellHeight - viewHeight);
        }
        Vector2 origin = ImGui.GetCursorScreenPos();
        int firstRow = Math.Max(0, (int)(scrollY / cellHeight)), lastRow = Math.Min(rows - 1, (int)((scrollY + viewHeight) / cellHeight));
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        bool hovered = ImGui.IsWindowHovered();
        Vector2 mouse = ImGui.GetMousePos();
        ImGuiIOPtr io = ImGui.GetIO();

        _thumbs.BeginFrame(_gridSurface);
        int order = 0;
        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int position = row * columns + column;
                if (position >= count) break;
                var p0 = new Vector2(MathF.Floor(origin.X + column * cellWidth), MathF.Floor(origin.Y + row * cellHeight));
                var p1 = p0 + new Vector2(cellWidth, cellHeight);
                bool over = hovered && mouse.X >= p0.X && mouse.X < p1.X && mouse.Y >= p0.Y && mouse.Y < p1.Y;
                if (IsSelected(folder, position)) draw.AddRectFilled(p0 + Vector2.One, p1 - Vector2.One, ColorSelected, 4);
                else if (over) draw.AddRectFilled(p0 + Vector2.One, p1 - Vector2.One, ColorHover, 4);

                var box = new Vector2(MathF.Floor(p0.X + (cellWidth - thumb) / 2), p0.Y + pad);
                DrawThumbnail(draw, _gridSurface, folder, position, 1000 + order++, box, thumb);
                if (position == _shownIndex) draw.AddRect(p0 + Vector2.One, p1 - Vector2.One, ColorCurrent, 4);

                // The name, centred if it fits, cut off at the cell's edge if not.
                Utf8Of(folder.NameAt(position));
                fixed (byte* text = _utf8)
                {
                    float textWidth = ImGui.CalcTextSize(text).X, room = cellWidth - 2 * pad;
                    var at = new Vector2(MathF.Floor(p0.X + pad + Math.Max(0, (room - textWidth) / 2)), p0.Y + pad + thumb + MathF.Round(2 * scale));
                    draw.PushClipRect(new Vector2(p0.X + pad, at.Y), new Vector2(p1.X - pad, p1.Y), true);
                    draw.AddText(at, ColorText, text);
                    draw.PopClipRect();
                }

                if (over)
                {
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) ClickItem(folder, position, io.KeyCtrl, io.KeyShift);
                    else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) OpenItemMenu(folder, position);
                }
            }
        }

        // About to scroll in: two screens ahead and one behind, after everything visible.
        int visibleRows = lastRow - firstRow + 1;
        for (int row = lastRow + 1; row <= Math.Min(rows - 1, lastRow + 2 * visibleRows); row++)
            for (int column = 0; column < columns && row * columns + column < count; column++)
                _thumbs.TryGet(_gridSurface, folder, row * columns + column, 5000 + order++, out _, out _);
        for (int row = firstRow - 1; row >= Math.Max(0, firstRow - visibleRows); row--)
            for (int column = 0; column < columns && row * columns + column < count; column++)
                _thumbs.TryGet(_gridSurface, folder, row * columns + column, 5000 + order++, out _, out _);
        _thumbs.EndFrame(_gridSurface);

        ImGui.Dummy(new Vector2(1, rows * cellHeight)); // gives the scroll area its full height
        DrawItemMenu();
        ImGui.EndChild();
    }

    /// <summary>Draws a file's thumbnail centred in a square of <paramref name="size"/> pixels at <paramref name="box"/>, or a placeholder while it loads.</summary>
    void DrawThumbnail(ImDrawListPtr draw, ThumbnailSurface surface, FolderIndex folder, int position, int priority, Vector2 box, int size)
    {
        if (_thumbs!.TryGet(surface, folder, position, priority, out AtlasSlot slot, out bool failed))
        {
            var at = new Vector2(MathF.Floor(box.X + (size - slot.Width) / 2f), MathF.Floor(box.Y + (size - slot.Height) / 2f));
            draw.AddImage(new ImTextureRef(null, new ImTextureID(slot.Texture)), at, at + new Vector2(slot.Width, slot.Height), slot.Uv0, slot.Uv1);
        }
        else
        {
            draw.AddRectFilled(box, box + new Vector2(size, size), failed ? ColorFailed : ColorPlaceholder, 2);
        }
    }

    static readonly FolderSort[] ListColumns = [FolderSort.Name, FolderSort.Size, FolderSort.Dimensions, FolderSort.Modified, FolderSort.Type];

    void DrawList(FolderIndex folder)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("files"u8, 5, flags, ImGui.GetContentRegionAvail())) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name"u8, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Size"u8, ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Dimensions"u8, ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Modified"u8, ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Type"u8);
        ImGui.TableHeadersRow();

        // The order is the folder's order: a click on a header changes it for the whole viewer.
        if (_syncTableSort)
        {
            _syncTableSort = false;
            ImGuiP.TableSetColumnSortDirection(Array.IndexOf(ListColumns, _sort), _sortDescending ? ImGuiSortDirection.Descending : ImGuiSortDirection.Ascending, false);
        }
        ImGuiTableSortSpecsPtr specs = ImGui.TableGetSortSpecs();
        if (!specs.IsNull && specs.SpecsDirty)
        {
            specs.SpecsDirty = false;
            if (specs.SpecsCount > 0)
            {
                ImGuiTableColumnSortSpecs spec = specs.Specs[0];
                FolderSort sort = ListColumns[Math.Clamp((int)spec.ColumnIndex, 0, ListColumns.Length - 1)];
                bool descending = spec.SortDirection == ImGuiSortDirection.Descending;
                if (sort != _sort || descending != _sortDescending) SetSort(sort, descending);
            }
        }

        ImGuiIOPtr io = ImGui.GetIO();
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        if (_scrollBrowserToCurrent && _fileIndex >= 0)
        {
            _scrollBrowserToCurrent = false;
            float top = _fileIndex * rowHeight, scrollY = ImGui.GetScrollY(), viewHeight = ImGui.GetWindowHeight() - 2 * rowHeight;
            if (top < scrollY) ImGui.SetScrollY(top);
            else if (top + rowHeight > scrollY + viewHeight) ImGui.SetScrollY(top + rowHeight - viewHeight);
        }

        ImGuiListClipperPtr clipper = ImGui.ImGuiListClipper();
        clipper.Begin(folder.Count, rowHeight);
        while (clipper.Step())
        {
            for (int position = clipper.DisplayStart; position < clipper.DisplayEnd; position++)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.PushID(position);
                ReadOnlySpan<char> name = folder.NameAt(position);
                Utf8Of(name);
                bool selected = IsSelected(folder, position) || position == _shownIndex;
                fixed (byte* text = _utf8)
                {
                    if (ImGui.Selectable(text, selected, ImGuiSelectableFlags.SpanAllColumns)) ClickItem(folder, position, io.KeyCtrl, io.KeyShift);
                }
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) OpenItemMenu(folder, position);

                int n;
                ImGui.TableSetColumnIndex(1);
                long size = folder.SizeAt(position);
                if (size >= 1 << 20 ? Utf8.TryWrite(_utf8, $"{size / 1048576.0:0.00} MB", out n) : Utf8.TryWrite(_utf8, $"{size / 1024.0:0.0} KB", out n)) Put(n);

                ImGui.TableSetColumnIndex(2);
                if (folder.Listing.TryGetDimensions(folder.ListingIndexAt(position), out int width, out int height))
                {
                    if (width > 0 && Utf8.TryWrite(_utf8, $"{width} x {height}", out n)) Put(n);
                }
                else
                {
                    _thumbs?.RequestDimensions(folder, position);
                }

                ImGui.TableSetColumnIndex(3);
                if (Utf8.TryWrite(_utf8, $"{DateTime.FromFileTime(folder.ModifiedAt(position)):yyyy-MM-dd HH:mm}", out n)) Put(n);

                ImGui.TableSetColumnIndex(4);
                int dot = name.LastIndexOf('.');
                if (dot >= 0)
                {
                    n = Utf8Of(name[(dot + 1)..]);
                    for (int i = 0; i < n; i++) if (_utf8[i] is >= (byte)'a' and <= (byte)'z') _utf8[i] -= 32;
                    Put(n);
                }
                ImGui.PopID();
            }
        }
        clipper.End();
        clipper.Destroy();
        DrawItemMenu();
        ImGui.EndTable();
    }

    // ---------------------------------------------------------------- filmstrip

    void DrawFilmstrip()
    {
        FolderIndex? folder = _folder;
        if (folder == null || folder.Count == 0 || _gpu == null)
        {
            ImGui.TextDisabled("No folder."u8);
            return;
        }
        EnsureThumbnails();
        if (_thumbs == null) return;
        float scale = (float)_dpiScale;
        Vector2 available = ImGui.GetContentRegionAvail();
        float pad = MathF.Round(4 * scale);
        // As tall as the panel allows, in whole steps of 16 pixels.
        int thumb = Math.Clamp((int)(available.Y - ImGui.GetStyle().ScrollbarSize - 2 * pad) / 16 * 16, 48, 256);
        _thumbs.SetCellSize(_stripSurface, thumb, _gpu);

        if (!ImGui.BeginChild(ImGui.GetID("strip"u8), available, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }
        float itemWidth = thumb + 2 * pad, viewWidth = ImGui.GetWindowWidth(), scrollX = ImGui.GetScrollX();
        int count = folder.Count;
        ImGuiIOPtr io = ImGui.GetIO();
        bool hovered = ImGui.IsWindowHovered();
        if (hovered && io.MouseWheel != 0) ImGui.SetScrollX(scrollX = Math.Max(0, scrollX - io.MouseWheel * itemWidth * 3)); // the wheel moves along the strip
        if (_scrollStripToCurrent && _fileIndex >= 0)
        {
            _scrollStripToCurrent = false;
            ImGui.SetScrollX(scrollX = Math.Max(0, _fileIndex * itemWidth - (viewWidth - itemWidth) / 2));
        }
        Vector2 origin = ImGui.GetCursorScreenPos();
        int first = Math.Max(0, (int)(scrollX / itemWidth)), last = Math.Min(count - 1, (int)((scrollX + viewWidth) / itemWidth));
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Vector2 mouse = ImGui.GetMousePos();

        _thumbs.BeginFrame(_stripSurface);
        int order = 0;
        for (int position = first; position <= last; position++)
        {
            var p0 = new Vector2(MathF.Floor(origin.X + position * itemWidth), MathF.Floor(origin.Y));
            var p1 = p0 + new Vector2(itemWidth, thumb + 2 * pad);
            bool over = hovered && mouse.X >= p0.X && mouse.X < p1.X && mouse.Y >= p0.Y && mouse.Y < p1.Y;
            if (IsSelected(folder, position)) draw.AddRectFilled(p0, p1, ColorSelected, 3);
            else if (over) draw.AddRectFilled(p0, p1, ColorHover, 3);
            DrawThumbnail(draw, _stripSurface, folder, position, 2000 + order++, p0 + new Vector2(pad, pad), thumb);
            if (position == _shownIndex) draw.AddRect(p0, p1, ColorCurrent, 3);
            if (over)
            {
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) ClickItem(folder, position, io.KeyCtrl, io.KeyShift);
                else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) OpenItemMenu(folder, position);
            }
        }
        int visible = last - first + 1;
        for (int position = last + 1; position <= Math.Min(count - 1, last + visible); position++)
            _thumbs.TryGet(_stripSurface, folder, position, 6000 + order++, out _, out _);
        for (int position = first - 1; position >= Math.Max(0, first - visible); position--)
            _thumbs.TryGet(_stripSurface, folder, position, 6000 + order++, out _, out _);
        _thumbs.EndFrame(_stripSurface);

        ImGui.Dummy(new Vector2(count * itemWidth, thumb + 2 * pad));
        DrawItemMenu();
        ImGui.EndChild();
    }

    // ---------------------------------------------------------------- item menu

    (bool Browser, bool Filmstrip) _panelsBeforeHide;
    string? _menuPath;
    bool _menuRequested;

    /// <summary>A right click on an item: it becomes the selection unless it is part of one, and its menu opens.</summary>
    void OpenItemMenu(FolderIndex folder, int position)
    {
        if (!IsSelected(folder, position))
        {
            _selectionListing = folder.Listing;
            _selection.Clear();
            _selection.Add(folder.ListingIndexAt(position));
            _selectionAnchor = position;
        }
        _menuPath = folder.PathAt(position);
        _menuRequested = true;
    }

    /// <summary>Draws the item menu. Called by each panel at its own level, where the popup was asked for.</summary>
    void DrawItemMenu()
    {
        if (_menuRequested)
        {
            _menuRequested = false;
            ImGui.OpenPopup("item"u8);
        }
        if (!ImGui.BeginPopup("item"u8)) return;
        if (_menuPath is { } path)
        {
            if (ImGui.MenuItem("Show in Explorer"u8)) FileActions.ShowInExplorer(path);
            if (ImGui.MenuItem("Copy path"u8)) ImGui.SetClipboardText(path);
            if (ImGui.MenuItem("Copy name"u8)) ImGui.SetClipboardText(Path.GetFileName(path));
        }
        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- views of the folder

    /// <summary>Applies the filter to the folder as read and makes the result what the panels and next/previous use.</summary>
    void ApplyView()
    {
        if (_folderAll == null) return;
        string filter = _filterText;
        FolderIndex view = filter.Length == 0 ? _folderAll : _folderAll.Where(name => name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        AdoptFolder(view);
        _scrollBrowserToCurrent = _scrollStripToCurrent = true;
    }
}
