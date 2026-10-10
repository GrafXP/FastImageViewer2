using System.Numerics;
using System.Text;
using System.Text.Unicode;
using Fiv.Comparing;
using Fiv.Imaging;
using Fiv.Indexing;
using Fiv.Thumbnails;
using Hexa.NET.ImGui;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Fiv.App;

// Menus, dialogs and what they do: opening, recent items, file operations, the key list, settings.
internal sealed unsafe partial class MainWindow
{
    enum Dialog { None, Rename, Delete }

    readonly Settings _settings;
    Dialog _dialog;
    bool _dialogOpening;
    readonly byte[] _renameBuffer = new byte[780];
    string? _renamePath, _dialogError, _followName, _notice;
    long _noticeUntil;
    List<string> _deletePaths = [];
    bool _showKeys, _showSettings, _viewMenuRequested, _resetLayout;

    /// <summary>The keys, in one place: what F1 shows.</summary>
    static readonly (string Keys, string Action)[] KeyMap =
    [
        ("Right, Space, PgDn", "next image"),
        ("Left, Backspace, PgUp", "previous image"),
        ("Home / End", "first / last image"),
        ("Up / Down", "one row up / down in the grid"),
        ("Mouse wheel, + / -", "zoom at the cursor / at the centre"),
        ("Left drag", "pan"),
        ("Double click", "100 % at the cursor, or back to fit"),
        ("F or 0 / 1", "fit to window / 100 %"),
        ("B / T / Tab", "folder panel / filmstrip / all panels"),
        ("S / Shift+S", "sort by name, date, size, type / reverse"),
        ("Ctrl+A / Ctrl+I", "select all / invert the selection"),
        ("Ctrl+O / Ctrl+Shift+O", "open files / open a folder"),
        ("Ctrl+C / Ctrl+Shift+C", "copy the image / its path"),
        ("F2 / Del", "rename / move to the Recycle Bin"),
        ("I", "exact pixels or smooth when zoomed in"),
        ("L", "keep zoom and position when changing image"),
        ("D", "display panel: black, white, gamma, exposure, channel"),
        ("H / Shift+H", "show colour, red, green, blue, alpha, luma in turn"),
        ("Z", "100 %"),
        ("V", "write the pixel values into the pixels at high zoom"),
        ("F11 / F12", "fullscreen / timing overlay"),
        ("Right click", "menu"),
        ("Esc", "close a dialog, leave fullscreen, stop comparing, or quit"),
        ("", ""),
        ("U", "measure: lines, rectangles and circles on the image, and a panel with their numbers; again to put them away"),
        ("While measuring", ""),
        ("L / R / C", "then a drag: a line / a rectangle / a circle (from its centre)"),
        ("Shift+drag", "the line level, upright or diagonal; the rectangle a square"),
        ("Drag a handle / the outline", "change a measurement / move it (a circle: by its centre)"),
        ("Click a measurement", "select it: a line shows the values along it, an area its numbers and histogram"),
        ("Del / Esc", "delete the selected measurement / give up the shape chosen, then put the measurements away"),
        ("H", "the channel the plot and the histogram show, as for the image"),
        ("Ticks in the panel", "while comparing: which images of the set the plot, the histogram and the tables show"),
        ("", ""),
        ("C", "compare the selected images, or this one with the next; again to stop"),
        ("Drop 2 to 16 files", "compare them; with Ctrl held, add them to the comparison"),
        ("While comparing", ""),
        ("M / Shift+M", "mode: single, side by side, grid, split, blend, checkerboard, difference, SSIM"),
        ("1 ... 9", "go to that image; held down, only until released"),
        ("Left / Right, Home / End", "previous / next, first / last image of the set"),
        ("X / K", "swap the two images / blink between them"),
        ("Up / Down", "divider, opacity, cell size, blink rate or gain (with Shift: finer); while aligning, see below"),
        ("L", "link zoom and position of the panes, or not"),
        ("P", "compare panel"),
        ("", ""),
        ("E / Shift+E", "difference view; in it: absolute, signed, squared, relative"),
        ("G / Shift+G", "colours of the difference and of the similarity"),
        ("W", "mark the pixels that differ by more than the threshold"),
        (", / .", "threshold down / up"),
        ("N", "metrics panel: MAE, MSE, PSNR, largest error, share over the threshold, SSIM, histogram"),
        ("R / Shift+R", "drag a region for the numbers (also Shift+drag) / the whole image again"),
        ("Ctrl+C", "copy what the panes show"),
        ("Ctrl+S", "save the two-image view at full resolution (PNG)"),
        ("Ctrl+D", "save the difference values (32-bit float TIFF)"),
        ("Ctrl+Shift+S", "save the numbers (CSV or JSON)"),
        ("", ""),
        ("A", "align: a panel, and drags and the keys below move the second image instead of the view"),
        ("Shift+A", "align the second image to the first automatically"),
        ("Q", "fine align: refine the pose the image has"),
        ("While aligning", ""),
        ("Drag, arrows", "move the image; arrows by 1 pixel, with Shift by 0.1"),
        ("Shift+drag", "turn it about its centre, as far as the mouse goes round it"),
        ("PgUp / PgDn, Shift+wheel", "turn it by 0.1 degrees; the keys with Shift, the wheel with Ctrl as well: by 0.01"),
        ("Home / End, Ctrl+wheel", "make it smaller / larger by 0.1 %, the keys with Shift by 0.01 %"),
        ("Region... in the panel", "then a drag: a rectangle for the automatic alignment to go by"),
        ("Backspace", "back to where it was before any aligning"),
        ("Ctrl+E", "save the aligned image (PNG, or TIFF for float)"),
    ];

    // ---------------------------------------------------------------- what the actions work on

    /// <summary>The files an action applies to: the selection if several are selected, otherwise the image on show.</summary>
    List<string> ActionPaths()
    {
        var paths = new List<string>();
        FolderIndex? selected = _folder != null && _selectionListing == _folder.Listing && _selection.Count > 1 ? _folder : null;
        if (Comparing && (selected == null || _menuPath == null))
        {
            // While comparing, an action means one image: the active one, or the one whose menu is open. Only from the
            // menu of the selection in a panel does it mean all that is selected: several selected images are compared
            // by themselves, and would otherwise be out of reach as a group.
            paths.Add(_menuPath ?? _compare[_pick.Active].Path);
        }
        else if (selected != null)
        {
            for (int position = 0; position < selected.Count; position++)
                if (_selection.Contains(selected.ListingIndexAt(position))) paths.Add(selected.PathAt(position));
        }
        else if (_menuPath != null)
        {
            paths.Add(_menuPath); // the item whose menu is open
        }
        else if (_shown != null)
        {
            paths.Add(_shown.Path);
        }
        return paths;
    }

    void Notice(string text)
    {
        _notice = text;
        _noticeUntil = Environment.TickCount64 + 2500;
        PInvoke.SetTimer(_hwnd, NoticeTimerId, 2600, null);
        Invalidate();
    }

    bool _pickerOpen;
    readonly System.Collections.Concurrent.ConcurrentQueue<(bool Folder, List<string> Paths)> _picked = new();

    /// <summary>Opens the system's dialog; what is chosen arrives later through <see cref="OnPicked"/>.</summary>
    void OpenWithDialog(bool folder)
    {
        if (_pickerOpen) return;
        _pickerOpen = true;
        FileActions.PickAsync(_hwnd, folder, paths =>
        {
            _picked.Enqueue((folder, paths));
            Post(WM_APP_PICKED);
        });
    }

    void OnPicked()
    {
        while (_picked.TryDequeue(out var picked))
        {
            _pickerOpen = false;
            if (picked.Paths.Count == 0) continue; // cancelled
            if (picked.Folder) OpenFolder(picked.Paths[0]);
            else Open(picked.Paths);
            BringToFront();
        }
        Invalidate();
    }

    void BeginRename()
    {
        if (ActionPaths() is not [string path]) return; // one file at a time
        _renamePath = path;
        Array.Clear(_renameBuffer);
        Encoding.UTF8.GetBytes(Path.GetFileName(path), _renameBuffer.AsSpan(0, _renameBuffer.Length - 1));
        _dialogError = null;
        _dialog = Dialog.Rename;
        _dialogOpening = true;
    }

    void CommitRename()
    {
        if (_renamePath == null) return;
        string newName = Encoding.UTF8.GetString(_renameBuffer, 0, Math.Max(0, Array.IndexOf(_renameBuffer, (byte)0)));
        _dialogError = FileActions.Rename(_renamePath, newName);
        if (_dialogError != null) return;
        // Stay on the file under its new name when the folder is read again.
        if (Comparing) RenameInCompare(_renamePath, Path.Combine(Path.GetDirectoryName(_renamePath) ?? "", newName.Trim()));
        else if (_shown != null && string.Equals(_shown.Path, _renamePath, StringComparison.OrdinalIgnoreCase)) _followName = newName.Trim();
        _dialog = Dialog.None;
        RefreshFolder();
    }

    void BeginDelete()
    {
        _deletePaths = ActionPaths();
        if (_deletePaths.Count == 0) return;
        _dialogError = null;
        _dialog = Dialog.Delete;
        _dialogOpening = true;
    }

    void CommitDelete()
    {
        bool ok = FileActions.Recycle(_hwnd, _deletePaths);
        _dialog = Dialog.None;
        if (!ok) Notice("Not everything could be moved to the Recycle Bin.");
        // A deleted image leaves the comparison at once, also where no folder listing would notice it gone.
        foreach (string path in _deletePaths)
        {
            int compared = _compare.FindIndex(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            if (compared >= 0 && !File.Exists(path)) RemoveFromCompare(compared);
        }
        _selection.Clear();
        RefreshFolder(); // the watcher would notice too; this is sooner
    }

    void CopyImage()
    {
        if (_shown == null) return;
        DecodedImage? source = _shown.Full ?? _shown.Preview;
        if (source == null)
        {
            Notice("The image is not loaded yet.");
            return;
        }
        // The clipboard gets what is on screen: upright, and for 16-bit and float images with the current brightness window.
        using DecodedImage bgra = source.Layout == PixelLayout.Bgra8
            ? Resampler.Upright(source, _shown.Orientation)
            : Upright(Resampler.ToBgra(source, source.Width, source.Height, _display.WindowLow, _display.WindowHigh), _shown.Orientation);
        Notice(FileActions.CopyToClipboard(_hwnd, bgra)
            ? source == _shown.Full ? "Image copied." : "Preview copied (the full image was still loading)."
            : "The image could not be copied.");

        static DecodedImage Upright(DecodedImage image, Orientation orientation)
        {
            using (image) return Resampler.Upright(image, orientation);
        }
    }

    /// <summary>Copies what the image area shows, as it is on screen and without what is drawn over it. Done in the next frame, where the picture is.</summary>
    void CopyView()
    {
        if (_gpu == null) return;
        _captureView = true;
        Invalidate();
    }

    void CopyPath()
    {
        List<string> paths = ActionPaths();
        if (paths.Count == 0) return;
        if (FileActions.CopyText(_hwnd, string.Join("\r\n", paths))) Notice(paths.Count == 1 ? "Path copied." : $"{paths.Count} paths copied.");
    }

    // ---------------------------------------------------------------- menus

    /// <summary>The menu of a right click on the image.</summary>
    void DrawViewMenu()
    {
        if (_viewMenuRequested)
        {
            _viewMenuRequested = false;
            _menuPath = null;
            ImGui.OpenPopup("view"u8);
        }
        if (!ImGui.BeginPopup("view"u8)) return;
        if (ImGui.MenuItem("Open files..."u8, "Ctrl+O"u8)) _deferred = () => OpenWithDialog(folder: false);
        if (ImGui.MenuItem("Open folder..."u8, "Ctrl+Shift+O"u8)) _deferred = () => OpenWithDialog(folder: true);
        DrawRecentMenu("Recent files"u8, _settings.RecentFiles, folder: false);
        DrawRecentMenu("Recent folders"u8, _settings.RecentFolders, folder: true);
        ImGui.Separator();
        DrawCompareItems();
        ImGui.Separator();
        DrawFileItems();
        ImGui.Separator();
        if (ImGui.MenuItem("Folder panel"u8, "B"u8, _showBrowser)) _showBrowser = !_showBrowser;
        if (ImGui.MenuItem("Filmstrip"u8, "T"u8, _showFilmstrip)) _showFilmstrip = !_showFilmstrip;
        if (ImGui.MenuItem("Display panel"u8, "D"u8, _showDisplayPanel)) _showDisplayPanel = !_showDisplayPanel;
        if (ImGui.MenuItem("Pixel values at high zoom"u8, "V"u8, _pixelValues)) _pixelValues = !_pixelValues;
        if (ImGui.MenuItem("Measure: lines, rectangles, circles"u8, "U"u8, _measure)) SetMeasure(!_measure);
        if (ImGui.MenuItem("Labels"u8, default(ReadOnlySpan<byte>), _paneLabels)) _paneLabels = !_paneLabels;
        if (ImGui.MenuItem("Timing overlay"u8, "F12"u8, _showPerf)) _showPerf = !_showPerf;
        if (ImGui.MenuItem("Fullscreen"u8, "F11"u8, _fullscreen)) _deferred = ToggleFullscreen;
        ImGui.Separator();
        if (ImGui.MenuItem("Keys"u8, "F1"u8, _showKeys)) _showKeys = !_showKeys;
        if (ImGui.MenuItem("Settings..."u8, default(ReadOnlySpan<byte>), _showSettings)) _showSettings = !_showSettings;
        if (ImGui.MenuItem("File types..."u8, default(ReadOnlySpan<byte>), _showFileTypes)) ToggleFileTypes();
        ImGui.EndPopup();
    }

    Action? _deferred; // run after the frame: things that must not happen in the middle of drawing (dialogs, window changes)

    void DrawRecentMenu(ReadOnlySpan<byte> label, List<string> items, bool folder)
    {
        if (!ImGui.BeginMenu(label, items.Count > 0)) return;
        foreach (string item in items)
        {
            Utf8Of(item);
            fixed (byte* text = _utf8)
            {
                if (!ImGui.MenuItem(text)) continue;
                string chosen = item;
                _deferred = folder ? () => OpenFolder(chosen) : () => Open([chosen]);
            }
        }
        ImGui.EndMenu();
    }

    /// <summary>The entries of the image's menu that start, change and end a comparison.</summary>
    void DrawCompareItems()
    {
        if (!Comparing)
        {
            bool several = _folder != null && _selectionListing == _folder.Listing && _selection.Count > 1;
            if (ImGui.MenuItem(several ? "Compare the selected images"u8 : "Compare with the next image"u8, "C"u8)) _deferred = CompareSelected;
            return;
        }
        if (ImGui.BeginMenu("Compare mode"u8))
        {
            for (CompareMode mode = CompareMode.Single; mode <= CompareModeExtensions.Last; mode++)
            {
                if (ImGui.MenuItem(ModeLabel(mode), mode == CompareMode.Single ? "M"u8 : mode == CompareMode.Difference ? "E"u8 : default, mode == _compareMode))
                {
                    CompareMode chosen = mode;
                    _deferred = () => SetCompareMode(chosen);
                }
            }
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem("Swap the two images"u8, "X"u8, _pick.Swapped)) _pick.Swapped = !_pick.Swapped;
        if (ImGui.MenuItem("Blink"u8, "K"u8, _blink)) _deferred = ToggleBlink;
        if (ImGui.MenuItem("Link zoom and position"u8, "L"u8, _linked)) SetLinked(!_linked);
        if (ImGui.MenuItem("Compare panel"u8, "P"u8, _showComparePanel)) _showComparePanel = !_showComparePanel;
        if (ImGui.MenuItem("Metrics panel"u8, "N"u8, _showMetrics)) _showMetrics = !_showMetrics;
        if (ImGui.MenuItem("Region for the numbers: drag a rectangle"u8, "R"u8, _regionArmed)) _regionArmed = !_regionArmed;
        if (_region != null && ImGui.MenuItem("Numbers of the whole image"u8, "Shift+R"u8)) _region = null;
        if (ImGui.BeginMenu("Align"u8))
        {
            if (ImGui.MenuItem("Align panel: move the second image by hand"u8, "A"u8, _alignMode)) SetAlignMode(!_alignMode);
            if (ImGui.MenuItem("Align automatically"u8, "Shift+A"u8, false, !_alignRunning)) _deferred = () => AutoAlign(fine: false);
            if (ImGui.MenuItem("Fine align from the pose it has"u8, "Q"u8, false, !_alignRunning)) _deferred = () => AutoAlign(fine: true);
            if (ImGui.MenuItem("Region for the alignment to go by: drag a rectangle"u8, default(ReadOnlySpan<byte>), _alignRegionArmed)) _alignRegionArmed = !_alignRegionArmed;
            if (_alignRegion != null && ImGui.MenuItem("Align by the whole image again"u8)) _alignRegion = null;
            if (ImGui.MenuItem("Back to where it was"u8, _alignMode ? "Backspace"u8 : default)) ResetPose();
            if (ImGui.MenuItem("Save the aligned image..."u8, "Ctrl+E"u8)) _deferred = SaveAligned;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Copy and save"u8))
        {
            if (ImGui.MenuItem("Copy what the panes show"u8, "Ctrl+C"u8)) _deferred = CopyView;
            if (ImGui.MenuItem("Save the view at full resolution..."u8, "Ctrl+S"u8, false, _compareMode.IsOverlay())) _deferred = SaveView;
            if (ImGui.MenuItem("Save the difference values (float TIFF)..."u8, "Ctrl+D"u8)) _deferred = SaveDifferenceValues;
            if (ImGui.MenuItem("Save the numbers (CSV, JSON)..."u8, "Ctrl+Shift+S"u8)) _deferred = SaveMetrics;
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem("Remove this image from the comparison"u8))
        {
            int active = _pick.Active;
            _deferred = () => RemoveFromCompare(active);
        }
        if (ImGui.MenuItem("Stop comparing"u8, "C"u8)) _deferred = () => StopCompare();
    }

    /// <summary>The entries that act on files, shared by the image's menu and the panels' item menu.</summary>
    void DrawFileItems()
    {
        bool any = _shown != null || _menuPath != null;
        ImGui.BeginDisabled(!any);
        if (ImGui.MenuItem("Show in Explorer"u8) && ActionPaths() is [string first, ..]) FileActions.ShowInExplorer(first);
        if (ImGui.MenuItem("Copy image"u8, Comparing ? default : "Ctrl+C"u8)) _deferred = CopyImage; // while comparing, Ctrl+C copies the view
        if (ImGui.MenuItem("Copy path"u8, "Ctrl+Shift+C"u8)) CopyPath();
        if (ImGui.MenuItem("Rename..."u8, "F2"u8)) BeginRename();
        if (ImGui.MenuItem("Move to Recycle Bin..."u8, "Del"u8)) BeginDelete();
        ImGui.EndDisabled();
    }

    // ---------------------------------------------------------------- dialogs

    void DrawDialogs()
    {
        if (_dialogOpening)
        {
            _dialogOpening = false;
            ImGui.OpenPopup(_dialog == Dialog.Rename ? "Rename"u8 : "Move to Recycle Bin"u8);
        }
        var centre = new Vector2(_clientWidth / 2f, _clientHeight / 2f);

        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (ImGui.BeginPopupModal("Rename"u8, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (_dialog != Dialog.Rename) ImGui.CloseCurrentPopup();
            ImGui.SetNextItemWidth(420 * (float)_dpiScale);
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            bool enter;
            fixed (byte* buffer = _renameBuffer)
                enter = ImGui.InputText("##name"u8, buffer, (nuint)_renameBuffer.Length, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
            if (_dialogError != null)
            {
                Utf8Of(_dialogError);
                fixed (byte* text = _utf8) TextIn(new Vector4(1, 0.5f, 0.4f, 1), text);
            }
            if (ImGui.Button("Rename"u8) || enter) CommitRename();
            ImGui.SameLine();
            if (ImGui.Button("Cancel"u8) || ImGui.IsKeyPressed(ImGuiKey.Escape)) _dialog = Dialog.None;
            ImGui.EndPopup();
        }

        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (ImGui.BeginPopupModal("Move to Recycle Bin"u8, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (_dialog != Dialog.Delete) ImGui.CloseCurrentPopup();
            int n;
            if (_deletePaths.Count == 1
                    ? Utf8.TryWrite(_utf8, $"Move '{Path.GetFileName(_deletePaths[0].AsSpan())}' to the Recycle Bin?", out n)
                    : Utf8.TryWrite(_utf8, $"Move {_deletePaths.Count} files to the Recycle Bin?", out n)) Put(n);
            for (int i = 0; i < Math.Min(_deletePaths.Count, 8) && _deletePaths.Count > 1; i++)
            {
                n = Utf8Of(Path.GetFileName(_deletePaths[i].AsSpan()));
                fixed (byte* text = _utf8) TextDim(text);
            }
            if (_deletePaths.Count > 8 && Utf8.TryWrite(_utf8, $"and {_deletePaths.Count - 8} more\0", out _))
            {
                fixed (byte* text = _utf8) TextDim(text);
            }
            ImGui.Spacing();
            // While a dialog is up the keyboard belongs to ImGui, so Enter and Esc are read here.
            if (ImGui.Button("Move to Recycle Bin"u8) || ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) _deferred = CommitDelete;
            ImGui.SameLine();
            if (ImGui.Button("Cancel"u8) || ImGui.IsKeyPressed(ImGuiKey.Escape)) _dialog = Dialog.None;
            ImGui.EndPopup();
        }
    }

    /// <summary>A short message at the bottom of the image: what an action did.</summary>
    void DrawNotice()
    {
        if (_notice == null || Environment.TickCount64 > _noticeUntil) return;
        Utf8Of(_notice);
        float above = Comparing && _showComparePanel ? _comparePanelHeight : 0; // the compare panel has that place
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth / 2f, ViewY + ViewHeight - 16 * (float)_dpiScale - above), ImGuiCond.Always, new Vector2(0.5f, 1));
        ImGui.SetNextWindowBgAlpha(0.75f);
        if (ImGui.Begin("notice"u8, PinnedWindow | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs))
        {
            fixed (byte* text = _utf8) ImGui.TextUnformatted(text);
        }
        ImGui.End();
    }

    void DrawKeys()
    {
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth / 2f, ViewY + ViewHeight / 2f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (ImGui.Begin("Keys"u8, ref _showKeys, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            if (ImGui.BeginTable("keys"u8, 2, ImGuiTableFlags.SizingFixedFit))
            {
                foreach ((string keys, string action) in KeyMap)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    Utf8Of(keys);
                    fixed (byte* text = _utf8) ImGui.TextUnformatted(text);
                    ImGui.TableSetColumnIndex(1);
                    Utf8Of(action);
                    fixed (byte* text = _utf8) TextDim(text);
                }
                ImGui.EndTable();
            }
        }
        ImGui.End();
    }

    void DrawSettings()
    {
        float scale = (float)_dpiScale;
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth / 2f, ViewY + ViewHeight / 2f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (ImGui.Begin("Settings"u8, ref _showSettings, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.TextDisabled("Decoded images kept in memory"u8);
            ImGui.PushItemWidth(260 * scale);
            if (ImGui.SliderInt("RAM, MB"u8, ref _settings.CacheMb, 256, 16384)) _pipeline.RamBudget = (long)_settings.CacheMb << 20;
            if (ImGui.SliderInt("video memory, MB"u8, ref _settings.VramMb, 256, 8192)) _pipeline.VramBudget = (long)_settings.VramMb << 20;
            ImGui.Spacing();
            ImGui.TextDisabled("Thumbnails kept on disk"u8);
            ImGui.SliderInt("at most, MB"u8, ref _settings.ThumbnailCacheMb, 64, 16384);
            ImGui.PopItemWidth();
            if (ImGui.Button("Delete the thumbnails of all other folders"u8))
            {
                string root = _options.ThumbsDir;
                ThreadPool.QueueUserWorkItem(_ => ThumbnailPack.Trim(root, 0)); // the pack in use is locked and stays
                Notice("Thumbnail cache cleared.");
            }
            ImGui.Spacing();
            ImGui.TextDisabled("View"u8);
            ImGui.Checkbox("Exact pixels when zoomed in (I)"u8, ref _display.Sharp);
            ImGui.Checkbox("Keep zoom and position across images (L)"u8, ref _keepView);
            if (ImGui.Button("Reset the panel layout"u8)) _resetLayout = true;
            ImGui.Spacing();
            Utf8Of(_options.ProfileDir);
            fixed (byte* text = _utf8) TextDim(text);
        }
        ImGui.End();
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Collects the state worth keeping and writes it. Called when the window closes.</summary>
    void SaveSettings()
    {
        WINDOWPLACEMENT placement = new() { length = (uint)sizeof(WINDOWPLACEMENT) };
        if (_fullscreen) placement = _savedPlacement;
        else PInvoke.GetWindowPlacement(_hwnd, &placement);
        var normal = placement.rcNormalPosition;
        if (normal.right - normal.left >= 200 && normal.bottom - normal.top >= 150)
        {
            _settings.WindowX = normal.left;
            _settings.WindowY = normal.top;
            _settings.WindowWidth = normal.right - normal.left;
            _settings.WindowHeight = normal.bottom - normal.top;
            _settings.Maximized = placement.showCmd == SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED;
        }
        if (_viewFromLayout && _clientWidth > 0 && _clientHeight > 0 && !_fullscreen)
        {
            _settings.ViewLeft = (double)_viewX / _clientWidth;
            _settings.ViewTop = (double)_viewY / _clientHeight;
            _settings.ViewRight = (double)(_viewX + _viewWidth) / _clientWidth;
            _settings.ViewBottom = (double)(_viewY + _viewHeight) / _clientHeight;
        }
        _settings.ShowBrowser = _showBrowser;
        _settings.ShowFilmstrip = _showFilmstrip;
        _settings.BrowserList = _browserMode == BrowserMode.List;
        _settings.ThumbnailSize = _thumbSize;
        _settings.Sort = _sort;
        _settings.SortDescending = _sortDescending;
        _settings.KeepView = _keepView;
        _settings.ExactPixels = _display.Sharp;
        if (_options.CompareMode == null) _settings.CompareMode = _compareMode; // a mode forced from the command line is not the user's choice
        _settings.CompareSizes = _sizeMatch;
        _settings.CompareLinked = _linked;
        _settings.CompareCursor = _linkedCursor;
        _settings.CompareLabels = _paneLabels;
        _settings.CompareSplitRows = _splitRows;
        _settings.CompareBlinkMs = _blinkMs;
        _settings.CompareCheckerSize = _checkerSize;
        _settings.DifferenceKind = _differenceKind;
        _settings.DifferenceMap = _differenceMap;
        _settings.SignedMap = _signedMap;
        _settings.DifferenceThreshold = _threshold;
        _settings.DifferenceMask = _mask;
        _settings.ShowMetrics = _showMetrics;
        _settings.PixelValues = _pixelValues;
        _settings.AlignModel = _alignModel;
        _settings.MeasurePixelSize = _pixelSize;
        _settings.MeasureUnit = _unit.Length <= 16 ? _unit : "mm";
        _settings.HistogramLog = _histogramLog;
        _settings.Save(Path.Combine(_options.ProfileDir, "settings.json"));
    }
}
