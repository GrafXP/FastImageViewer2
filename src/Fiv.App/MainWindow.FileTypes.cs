using System.Numerics;
using System.Text.Unicode;
using Fiv.Shell;
using Hexa.NET.ImGui;

namespace Fiv.App;

// The File types dialog: registering the viewer with Windows for the endings ticked, and taking
// that back. The registry and the shell are asked on a worker; the dialog shows what came back.
internal sealed unsafe partial class MainWindow
{
    bool _showFileTypes, _fileTypesBusy;
    FileTypes.State? _fileTypes;
    string? _fileTypesError;
    readonly bool[] _fileTypeWanted = new bool[FileAssociations.Types.Length];

    void ToggleFileTypes()
    {
        _showFileTypes = !_showFileTypes;
        if (!_showFileTypes) return;
        _fileTypes = null;
        _fileTypesError = null;
        LoadFileTypes(apply: null, tick: true);
    }

    /// <summary>
    /// Reads how things stand, after registering exactly the endings of <paramref name="apply"/>
    /// if it is given. <paramref name="tick"/>: set the boxes from what is found, as when the
    /// dialog opens; otherwise they stay as the user has them.
    /// </summary>
    void LoadFileTypes(string[]? apply, bool tick)
    {
        if (_fileTypesBusy) return;
        _fileTypesBusy = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string? error = null;
            FileTypes.State? state = null;
            try
            {
                if (apply != null) error = FileTypes.Apply(apply);
                state = FileTypes.Read();
            }
            catch (Exception e)
            {
                CrashLog.Write(e);
                error = e.Message;
            }
            RunOnUi(() =>
            {
                _fileTypesBusy = false;
                if (state != null) _fileTypes = state;
                if (apply != null || error != null) _fileTypesError = error;
                if (tick && state != null)
                {
                    // Nothing registered yet: the common ones are the suggestion.
                    bool any = Array.IndexOf(state.Registered, true) >= 0;
                    for (int i = 0; i < _fileTypeWanted.Length; i++) _fileTypeWanted[i] = any ? state.Registered[i] : FileAssociations.Types[i].Common;
                }
                if (apply != null && error == null)
                    Notice(apply.Length == 0 ? "The file types are as they were before." : apply.Length == 1 ? "Registered for 1 file type." : $"Registered for {apply.Length} file types.");
            });
        });
    }

    void DrawFileTypes()
    {
        float scale = (float)_dpiScale;
        FileType[] types = FileAssociations.Types;
        FileTypes.State? state = _fileTypes;
        ImGui.SetNextWindowPos(new Vector2(ViewX + ViewWidth / 2f, ViewY + ViewHeight / 2f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (ImGui.Begin("File types"u8, ref _showFileTypes, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            int n;
            ImGui.TextDisabled("Register FastImageViewer2 with Windows for"u8);
            ImGui.BeginDisabled(state == null || _fileTypesBusy);
            if (ImGui.BeginTable("types"u8, 2, ImGuiTableFlags.SizingFixedFit))
            {
                string? format = null;
                for (int i = 0; i < types.Length; i++)
                {
                    FileType type = types[i];
                    if (type.Format != format)
                    {
                        format = type.Format;
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.AlignTextToFramePadding();
                        Utf8Of(format);
                        fixed (byte* text = _utf8) ImGui.TextUnformatted(text);
                        ImGui.TableSetColumnIndex(1);
                    }
                    else
                    {
                        ImGui.SameLine();
                    }
                    // Green: Windows opens these with this viewer already.
                    bool opens = state != null && state.Opens[i];
                    if (opens) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.9f, 0.55f, 1));
                    if (Utf8.TryWrite(_utf8, $".{type.Extension}\0", out _))
                    {
                        fixed (byte* label = _utf8) ImGui.Checkbox(label, ref _fileTypeWanted[i]);
                    }
                    if (opens) ImGui.PopStyleColor();
                    if (state != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && ImGui.BeginTooltip())
                    {
                        string by = state.OpenedBy[i];
                        if (opens ? Utf8.TryWrite(_utf8, $"Windows opens .{type.Extension} files with this viewer", out n)
                            : by.Length > 0 ? Utf8.TryWrite(_utf8, $"Windows opens .{type.Extension} files with {by}", out n)
                            : Utf8.TryWrite(_utf8, $"Windows has no program for .{type.Extension} files", out n)) Put(n);
                        ImGui.EndTooltip();
                    }
                }
                ImGui.EndTable();
            }
            if (ImGui.Button("Common"u8))
                for (int i = 0; i < types.Length; i++) _fileTypeWanted[i] = types[i].Common;
            ImGui.SameLine();
            if (ImGui.Button("All"u8)) Array.Fill(_fileTypeWanted, true);
            ImGui.SameLine();
            if (ImGui.Button("None"u8)) Array.Fill(_fileTypeWanted, false);
            ImGui.EndDisabled();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            if (state == null)
            {
                ImGui.TextDisabled(_fileTypesError == null ? "Reading what is registered..."u8 : "What is registered could not be read."u8);
            }
            else
            {
                int registered = 0, opened = 0, waiting = 0;
                bool differs = false;
                for (int i = 0; i < types.Length; i++)
                {
                    if (state.Registered[i]) registered++;
                    if (state.Opens[i]) opened++;
                    if (state.Registered[i] && !state.Opens[i]) waiting++;
                    differs |= _fileTypeWanted[i] != state.Registered[i];
                }

                ImGui.BeginDisabled(_fileTypesBusy);
                ImGui.BeginDisabled(!differs && !(registered > 0 && state.Moved));
                if (ImGui.Button("Apply"u8))
                {
                    var wanted = new List<string>();
                    for (int i = 0; i < types.Length; i++)
                        if (_fileTypeWanted[i]) wanted.Add(types[i].Extension);
                    LoadFileTypes([.. wanted], tick: false);
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(registered == 0);
                if (ImGui.Button("Remove all"u8)) LoadFileTypes([], tick: false);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Takes the viewer out of the registry and puts back what the file types were set to before"u8);
                ImGui.EndDisabled();
                ImGui.EndDisabled();

                ImGui.PushTextWrapPos(560 * scale);
                if (registered == 0) ImGui.TextDisabled("Not registered for any file type."u8);
                else if (Utf8.TryWrite(_utf8, $"Registered for {registered} file {(registered == 1 ? "type" : "types")}. Windows opens {opened} with this viewer.", out n)) Put(n);
                if (registered > 0 && state.Moved && Utf8.TryWrite(_utf8, $"They start another copy of the viewer: {state.Executable}. Apply makes it this one.\0", out _))
                {
                    fixed (byte* text = _utf8) TextIn(new Vector4(1, 0.75f, 0.3f, 1), text);
                }
                if (waiting > 0)
                {
                    ImGui.TextDisabled("Which program opens a file type is for you to say in Windows, not for a program to set: Windows asks the next time you open such a file, or choose there right away."u8);
                    ImGui.PopTextWrapPos();
                    if (ImGui.Button("Windows default apps..."u8)) ThreadPool.QueueUserWorkItem(_ => FileTypes.OpenWindowsSettings());
                }
                else
                {
                    ImGui.PopTextWrapPos();
                }
            }
            if (_fileTypesError != null)
            {
                Utf8Of(_fileTypesError);
                fixed (byte* text = _utf8) TextIn(new Vector4(1, 0.5f, 0.4f, 1), text);
            }
        }
        ImGui.End();
    }
}
