using System.Numerics;
using System.Text;
using System.Text.Unicode;
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
        ("D", "display panel: black, white, gamma"),
        ("F11 / F12", "fullscreen / timing overlay"),
        ("Right click", "menu"),
        ("Esc", "close a dialog, leave fullscreen, or quit"),
    ];

    // ---------------------------------------------------------------- what the actions work on

    /// <summary>The files an action applies to: the selection if several are selected, otherwise the image on show.</summary>
    List<string> ActionPaths()
    {
        var paths = new List<string>();
        if (_folder != null && _selectionListing == _folder.Listing && _selection.Count > 1)
        {
            for (int position = 0; position < _folder.Count; position++)
                if (_selection.Contains(_folder.ListingIndexAt(position))) paths.Add(_folder.PathAt(position));
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

    void OpenWithDialog(bool folder)
    {
        List<string> picked = FileActions.Pick(_hwnd, folder);
        if (picked.Count == 0) return;
        if (folder) OpenFolder(picked[0]);
        else Open(picked);
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
        if (_shown != null && string.Equals(_shown.Path, _renamePath, StringComparison.OrdinalIgnoreCase)) _followName = newName.Trim();
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
        DrawFileItems();
        ImGui.Separator();
        if (ImGui.MenuItem("Folder panel"u8, "B"u8, _showBrowser)) _showBrowser = !_showBrowser;
        if (ImGui.MenuItem("Filmstrip"u8, "T"u8, _showFilmstrip)) _showFilmstrip = !_showFilmstrip;
        if (ImGui.MenuItem("Display panel"u8, "D"u8, _showDisplayPanel)) _showDisplayPanel = !_showDisplayPanel;
        if (ImGui.MenuItem("Timing overlay"u8, "F12"u8, _showPerf)) _showPerf = !_showPerf;
        if (ImGui.MenuItem("Fullscreen"u8, "F11"u8, _fullscreen)) _deferred = ToggleFullscreen;
        ImGui.Separator();
        if (ImGui.MenuItem("Keys"u8, "F1"u8, _showKeys)) _showKeys = !_showKeys;
        if (ImGui.MenuItem("Settings..."u8, default(ReadOnlySpan<byte>), _showSettings)) _showSettings = !_showSettings;
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

    /// <summary>The entries that act on files, shared by the image's menu and the panels' item menu.</summary>
    void DrawFileItems()
    {
        bool any = _shown != null || _menuPath != null;
        ImGui.BeginDisabled(!any);
        if (ImGui.MenuItem("Show in Explorer"u8) && ActionPaths() is [string first, ..]) FileActions.ShowInExplorer(first);
        if (ImGui.MenuItem("Copy image"u8, "Ctrl+C"u8)) _deferred = CopyImage;
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
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth / 2f, ViewY + ViewHeight - 16 * (float)_dpiScale), ImGuiCond.Always, new Vector2(0.5f, 1));
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
        _settings.Save(Path.Combine(_options.ProfileDir, "settings.json"));
    }
}
