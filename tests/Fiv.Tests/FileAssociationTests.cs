using System.Text;
using Fiv.Indexing;
using Fiv.Shell;
using Microsoft.Win32;
using Xunit;

namespace Fiv.Tests;

/// <summary>
/// Against a key of the test's own that stands in for HKEY_CURRENT_USER\Software: nothing Windows
/// reads is written to.
/// </summary>
public sealed class FileAssociationTests : IDisposable
{
    const string Parent = @"Software\FastImageViewer2.Tests";
    const string Viewer = @"C:\Tools\Viewer\FastImageViewer2.exe";

    readonly string _name = Guid.NewGuid().ToString("N");
    readonly RegistryKey _software;

    public FileAssociationTests()
    {
        _software = Registry.CurrentUser.CreateSubKey($@"{Parent}\{_name}");
        _software.CreateSubKey("Classes").Dispose(); // as HKEY_CURRENT_USER\Software always has it
    }

    public void Dispose()
    {
        _software.Dispose();
        using RegistryKey parent = Registry.CurrentUser.CreateSubKey(Parent);
        parent.DeleteSubKeyTree(_name, throwOnMissingSubKey: false);
        bool last = parent.SubKeyCount == 0;
        parent.Dispose();
        if (last) Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
    }

    FileAssociations Associations() => new(Registry.CurrentUser.CreateSubKey($@"{Parent}\{_name}"));

    void Apply(string executable, params string[] extensions)
    {
        using FileAssociations associations = Associations();
        associations.Apply(extensions, executable);
    }

    string? DefaultOf(string extension)
    {
        using RegistryKey? key = _software.OpenSubKey($@"Classes\.{extension}");
        return key?.GetValue("") as string;
    }

    void SetDefault(string extension, string fileType, bool exists = true)
    {
        using RegistryKey key = _software.CreateSubKey($@"Classes\.{extension}");
        key.SetValue("", fileType);
        if (exists) _software.CreateSubKey($@"Classes\{fileType}\shell\open\command").Dispose();
    }

    /// <summary>Everything below the test's key, one line per key and value.</summary>
    string Dump()
    {
        var text = new StringBuilder();
        Walk(_software, "");
        return text.ToString();

        void Walk(RegistryKey key, string path)
        {
            text.Append('[').Append(path).Append("]\n");
            foreach (string name in key.GetValueNames().Order(StringComparer.Ordinal))
            {
                object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!;
                text.Append(path).Append('|').Append(name).Append('=').Append(key.GetValueKind(name)).Append(':')
                    .Append(value is byte[] bytes ? Convert.ToHexString(bytes) : value.ToString()).Append('\n');
            }
            foreach (string name in key.GetSubKeyNames().Order(StringComparer.Ordinal))
            {
                using RegistryKey child = key.OpenSubKey(name)!;
                Walk(child, path + '\\' + name);
            }
        }
    }

    [Fact]
    public void ListsExactlyTheEndingsTheViewerOpens()
    {
        string[] listed = Array.ConvertAll(FileAssociations.Types, type => type.Extension);
        Assert.Equal(ImageFormats.Extensions.Order(), listed.Order());
        Assert.Equal(listed.Length, listed.Distinct().Count());
    }

    [Fact]
    public void RegistersAnEnding()
    {
        Apply(Viewer, "jpg");

        using (RegistryKey command = _software.OpenSubKey(@"Classes\FastImageViewer2.jpg\shell\open\command")!)
            Assert.Equal($"\"{Viewer}\" \"%1\"", command.GetValue(""));
        using (RegistryKey fileType = _software.OpenSubKey(@"Classes\FastImageViewer2.jpg")!)
            Assert.Equal("JPEG image", fileType.GetValue(""));
        using (RegistryKey icon = _software.OpenSubKey(@"Classes\FastImageViewer2.jpg\DefaultIcon")!)
            Assert.Equal(@"%SystemRoot%\System32\imageres.dll,-72", icon.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        using (RegistryKey openWith = _software.OpenSubKey(@"Classes\.jpg\OpenWithProgids")!)
            Assert.Contains("FastImageViewer2.jpg", openWith.GetValueNames());
        Assert.Equal("FastImageViewer2.jpg", DefaultOf("jpg"));
        using (RegistryKey capabilities = _software.OpenSubKey(@"FastImageViewer2\Capabilities\FileAssociations")!)
            Assert.Equal("FastImageViewer2.jpg", capabilities.GetValue(".jpg"));
        using (RegistryKey applications = _software.OpenSubKey("RegisteredApplications")!)
            Assert.Equal(@"Software\FastImageViewer2\Capabilities", applications.GetValue("FastImageViewer2"));

        using FileAssociations associations = Associations();
        Assert.Equal(["jpg"], associations.Registered());
        Assert.Equal(Viewer, associations.Executable());
    }

    [Fact]
    public void NothingIsRegisteredAtFirst()
    {
        using FileAssociations associations = Associations();
        Assert.Empty(associations.Registered());
        Assert.Null(associations.Executable());
    }

    [Fact]
    public void TakingEverythingBackLeavesTheRegistryAsItWas()
    {
        // What a machine in use looks like: other programs listed for the endings, one of them the default of some.
        SetDefault("jpg", "Other.jpg");
        using (RegistryKey openWith = _software.CreateSubKey(@"Classes\.jpg\OpenWithProgids")) openWith.SetValue("AppXother", Array.Empty<byte>(), RegistryValueKind.None);
        using (RegistryKey openWith = _software.CreateSubKey(@"Classes\.png\OpenWithProgids")) openWith.SetValue("AppXother", Array.Empty<byte>(), RegistryValueKind.None);
        using (RegistryKey applications = _software.CreateSubKey("RegisteredApplications")) applications.SetValue("Browser", @"Software\Browser\Capabilities");
        string before = Dump();

        Apply(Viewer, FileAssociations.ParseList("all")!);
        Assert.NotEqual(before, Dump());
        Apply(Viewer);

        Assert.Equal(before, Dump());
    }

    [Fact]
    public void NothingIsLeftWhereNothingWasBefore()
    {
        string before = Dump();
        Apply(Viewer, FileAssociations.ParseList("common")!);
        Apply(Viewer);
        Assert.Equal(before, Dump());
    }

    [Fact]
    public void ThePreviousDefaultComesBack()
    {
        SetDefault("tif", "Other.tif");
        Apply(Viewer, "tif");
        Assert.Equal("FastImageViewer2.tif", DefaultOf("tif"));
        Apply(Viewer);
        Assert.Equal("Other.tif", DefaultOf("tif"));
    }

    [Fact]
    public void RegisteringAgainDoesNotForgetThePreviousDefault()
    {
        SetDefault("tif", "Other.tif");
        Apply(Viewer, "tif");
        Apply(Viewer, "tif", "png");
        Apply(Viewer);
        Assert.Equal("Other.tif", DefaultOf("tif"));
    }

    [Fact]
    public void APreviousDefaultThatIsGoneIsNotPutBack()
    {
        SetDefault("gif", "Uninstalled.gif", exists: false);
        Apply(Viewer, "gif");
        Apply(Viewer);
        Assert.Null(DefaultOf("gif"));
    }

    [Fact]
    public void ADefaultTakenOverSinceIsLeftAlone()
    {
        Apply(Viewer, "png");
        SetDefault("png", "Later.png");
        Apply(Viewer);
        Assert.Equal("Later.png", DefaultOf("png"));
        Assert.Null(_software.OpenSubKey(@"Classes\FastImageViewer2.png"));
    }

    [Fact]
    public void WhatTheShellNotedAboutTheViewerGoesToo()
    {
        const string fileExts = @"Microsoft\Windows\CurrentVersion\Explorer\FileExts";
        Apply(Viewer, "jpg", "dib");
        // As the shell does when it lists the programs for an ending: beside the others it knows, or in a list made for the occasion.
        using (RegistryKey key = _software.CreateSubKey($@"{fileExts}\.jpg\OpenWithProgids"))
        {
            key.SetValue("FastImageViewer2.jpg", Array.Empty<byte>(), RegistryValueKind.None);
            key.SetValue("AppXother", Array.Empty<byte>(), RegistryValueKind.None);
        }
        using (RegistryKey key = _software.CreateSubKey($@"{fileExts}\.dib\OpenWithProgids"))
            key.SetValue("FastImageViewer2.dib", Array.Empty<byte>(), RegistryValueKind.None);
        Apply(Viewer);

        using (RegistryKey others = _software.OpenSubKey($@"{fileExts}\.jpg\OpenWithProgids")!)
            Assert.Equal(["AppXother"], others.GetValueNames());
        using RegistryKey dib = _software.OpenSubKey($@"{fileExts}\.dib")!;
        Assert.Empty(dib.GetSubKeyNames());
    }

    [Fact]
    public void EndingsNoLongerWantedAreTakenBack()
    {
        SetDefault("png", "Other.png");
        Apply(Viewer, "jpg", "png");
        Apply(Viewer, "jpg");

        Assert.Equal("Other.png", DefaultOf("png"));
        Assert.Null(_software.OpenSubKey(@"Classes\FastImageViewer2.png"));
        Assert.Equal("FastImageViewer2.jpg", DefaultOf("jpg"));
        using FileAssociations associations = Associations();
        Assert.Equal(["jpg"], associations.Registered());
    }

    [Fact]
    public void AMovedViewerIsRegisteredForItsNewPlace()
    {
        const string moved = @"D:\Elsewhere\FastImageViewer2.exe";
        SetDefault("jpg", "Other.jpg");
        Apply(Viewer, "jpg");
        Apply(moved, "jpg");

        using (FileAssociations associations = Associations()) Assert.Equal(moved, associations.Executable());
        Apply(moved);
        Assert.Equal("Other.jpg", DefaultOf("jpg"));
    }

    [Theory]
    [InlineData("jpg,png", new[] { "jpg", "png" })]
    [InlineData(".JPG; .Tif  png", new[] { "jpg", "tif", "png" })]
    [InlineData("jpg,jpg", new[] { "jpg" })]
    [InlineData("none", new string[0])]
    public void ReadsAListOfEndings(string list, string[] expected) => Assert.Equal(expected, FileAssociations.ParseList(list));

    [Fact]
    public void ReadsTheNamedSets()
    {
        Assert.Equal(FileAssociations.Types.Length, FileAssociations.ParseList("all")!.Length);
        string[] common = FileAssociations.ParseList("Common")!;
        Assert.Contains("jpg", common);
        Assert.DoesNotContain("cr3", common);
    }

    [Theory]
    [InlineData("jpg,txt")]
    [InlineData("exe")]
    [InlineData("")]
    [InlineData(" , ")]
    public void RefusesAListWithAnythingElseInIt(string list) => Assert.Null(FileAssociations.ParseList(list));
}
