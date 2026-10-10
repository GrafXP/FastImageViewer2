using System.Diagnostics;
using Fiv.Shell;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Fiv.App;

/// <summary>
/// Registering the viewer for file types, as the File types dialog and the --register switch do
/// it: the registry through <see cref="FileAssociations"/>, and what only the shell can tell or
/// do. Not on the UI thread: the shell takes its time over some of the answers.
/// </summary>
internal static unsafe class FileTypes
{
    /// <summary>How things stand. The arrays go by <see cref="FileAssociations.Types"/>.</summary>
    /// <param name="Registered">The endings the viewer is registered for.</param>
    /// <param name="Opens">The endings Windows opens with this viewer.</param>
    /// <param name="OpenedBy">The program Windows opens each ending with; empty if it has none.</param>
    /// <param name="Executable">The viewer the endings are registered for; null if none is registered.</param>
    /// <param name="Moved">That viewer is not this one: a copy somewhere else, or this one before it was moved.</param>
    public sealed record State(bool[] Registered, bool[] Opens, string[] OpenedBy, string? Executable, bool Moved);

    /// <summary>
    /// Makes the endings registered for this executable exactly these (none: takes the viewer out
    /// of the registry) and has the shell read the registrations again. Returns null, or why it failed.
    /// </summary>
    public static string? Apply(IReadOnlyCollection<string> extensions)
    {
        try
        {
            using FileAssociations associations = FileAssociations.ForCurrentUser();
            associations.Apply(extensions, Environment.ProcessPath!);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return e.Message;
        }
        PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null);
        return null;
    }

    public static State Read()
    {
        FileType[] types = FileAssociations.Types;
        var registered = new bool[types.Length];
        var opens = new bool[types.Length];
        var openedBy = new string[types.Length];
        string self = Environment.ProcessPath!;
        string? executable;
        using (FileAssociations associations = FileAssociations.ForCurrentUser())
        {
            List<string> extensions = associations.Registered();
            for (int i = 0; i < types.Length; i++) registered[i] = extensions.Contains(types[i].Extension);
            executable = associations.Executable();
        }
        for (int i = 0; i < types.Length; i++)
        {
            string dotted = "." + types[i].Extension;
            // What a double click starts: the user's choice in Windows if there is one, else the registered default.
            opens[i] = SamePath(Query(ASSOCSTR.ASSOCSTR_EXECUTABLE, dotted), self);
            openedBy[i] = Query(ASSOCSTR.ASSOCSTR_FRIENDLYAPPNAME, dotted);
        }
        return new State(registered, opens, openedBy, executable, executable != null && !SamePath(executable, self));
    }

    /// <summary>
    /// Opens the Windows settings at the default apps, on Windows 11 at the page of the viewer:
    /// the one place where the program a file type opens with can be changed.
    /// </summary>
    public static void OpenWindowsSettings()
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + FileAssociations.AppName) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    static string Query(ASSOCSTR what, string dotted)
    {
        const int capacity = 1024;
        char* buffer = stackalloc char[capacity];
        uint length = capacity;
        fixed (char* name = dotted)
        {
            // ASSOCF_INIT_IGNOREUNKNOWN: an ending without a program has none, not the "pick an app" window.
            return PInvoke.AssocQueryString(ASSOCF.ASSOCF_INIT_IGNOREUNKNOWN, what, name, null, buffer, &length).Value == 0 ? new string(buffer) : "";
        }
    }

    static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}
