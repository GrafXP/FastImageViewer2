using Fiv.Indexing;
using Xunit;

namespace Fiv.Tests;

public class NaturalOrderTests
{
    static int Sign(string a, string b) => Math.Sign(NaturalOrder.Compare(a, b));

    [Theory]
    [InlineData("img2.jpg", "img10.jpg")]        // numbers by value, not digit by digit
    [InlineData("img9.jpg", "img10.jpg")]
    [InlineData("IMG_99.jpg", "img_100.jpg")]    // case does not matter
    [InlineData("a.jpg", "b.jpg")]
    [InlineData("a1b2.png", "a1b10.png")]        // several numbers in one name
    [InlineData("7.jpg", "007.jpg")]             // equal value: fewer leading zeros first
    [InlineData("scan 2.png", "scan 10.png")]
    [InlineData("img.jpg", "img1.jpg")]          // a prefix sorts before what extends it
    [InlineData("12345678901234567890.jpg", "123456789012345678901.jpg")] // longer than any integer type
    public void Orders(string first, string second)
    {
        Assert.Equal(-1, Sign(first, second));
        Assert.Equal(1, Sign(second, first));
    }

    [Fact]
    public void EqualNamesCompareEqual() => Assert.Equal(0, Sign("IMG_0001.JPG", "IMG_0001.JPG"));

    [Fact]
    public void NamesDifferingOnlyInCaseAreOrderedConsistently()
    {
        Assert.NotEqual(0, Sign("a.jpg", "A.jpg"));
        Assert.Equal(-Sign("a.jpg", "A.jpg"), Sign("A.jpg", "a.jpg"));
    }
}

public class ImageFormatsTests
{
    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("A.JPEG", true)]
    [InlineData("photo.final.PNG", true)]
    [InlineData("scan.tiff", true)]
    [InlineData("raw.CR3", true)]
    [InlineData("notes.txt", false)]
    [InlineData("IMG_1.xmp", false)]
    [InlineData("noextension", false)]
    [InlineData("trailingdot.", false)]
    [InlineData("x.jpgx", false)]
    public void RecognisesImagesByExtension(string name, bool image) => Assert.Equal(image, ImageFormats.IsImageFileName(name));

    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("a.JFIF", true)]
    [InlineData("a.png", false)]
    public void RecognisesJpegNames(string name, bool jpeg) => Assert.Equal(jpeg, ImageFormats.IsJpegFileName(name));
}

public sealed class FolderIndexTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("fivtest").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    void Create(string name, int size, DateTime modifiedUtc)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    static string[] Names(FolderIndex index) => Enumerable.Range(0, index.Count).Select(i => index.NameAt(i).ToString()).ToArray();

    static void AssertNames(FolderIndex index, params string[] expected) => Assert.Equal(expected, Names(index));

    void CreateSample()
    {
        var day = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        Create("img10.jpg", 300, day.AddDays(1));
        Create("img2.jpg", 100, day.AddDays(3));
        Create("IMG1.JPG", 200, day.AddDays(2));
        Create("b.png", 50, day);
        Create("notes.txt", 10, day);
        Directory.CreateDirectory(Path.Combine(_folder, "sub.jpg")); // a directory that looks like an image
    }

    [Fact]
    public void ListsOnlyImageFilesInNaturalOrder()
    {
        CreateSample();
        FolderIndex index = FolderIndex.Build(_folder);
        AssertNames(index, "b.png", "IMG1.JPG", "img2.jpg", "img10.jpg");
    }

    [Fact]
    public void TakesSizeAndTimeFromTheDirectory()
    {
        CreateSample();
        FolderIndex index = FolderIndex.Build(_folder);
        int position = index.Find("img2.jpg");
        Assert.Equal(100, index.SizeAt(position));
        Assert.Equal(new DateTime(2024, 5, 4, 12, 0, 0, DateTimeKind.Utc), DateTime.FromFileTimeUtc(index.ModifiedAt(position)));
        Assert.Equal(Path.Combine(_folder, "img2.jpg"), index.PathAt(position));
    }

    [Fact]
    public void FindIgnoresCaseAndReportsMissing()
    {
        CreateSample();
        FolderIndex index = FolderIndex.Build(_folder);
        Assert.Equal(1, index.Find("img1.jpg"));
        Assert.Equal(-1, index.Find("notes.txt"));
    }

    [Fact]
    public void SortsBySizeDateAndType()
    {
        CreateSample();
        FolderIndex index = FolderIndex.Build(_folder);
        AssertNames(index.Sorted(FolderSort.Size, false), "b.png", "img2.jpg", "IMG1.JPG", "img10.jpg");
        AssertNames(index.Sorted(FolderSort.Size, true), "img10.jpg", "IMG1.JPG", "img2.jpg", "b.png");
        AssertNames(index.Sorted(FolderSort.Modified, false), "b.png", "img10.jpg", "IMG1.JPG", "img2.jpg");
        AssertNames(index.Sorted(FolderSort.Type, false), "IMG1.JPG", "img2.jpg", "img10.jpg", "b.png");
        AssertNames(index, "b.png", "IMG1.JPG", "img2.jpg", "img10.jpg"); // the original view is untouched
    }

    [Fact]
    public void FilteringMakesAViewOverTheSameListing()
    {
        CreateSample();
        FolderIndex index = FolderIndex.Build(_folder);
        FolderIndex view = index.Where(name => name.Contains("img", StringComparison.OrdinalIgnoreCase));
        AssertNames(view, "IMG1.JPG", "img2.jpg", "img10.jpg");
        Assert.Same(index.Listing, view.Listing);
    }

    [Fact]
    public void SortsLargeFoldersTheSameAsSmallOnes()
    {
        // Enough files for the parallel sort path; compared with a plain sort of the same names.
        var names = new List<string>();
        for (int i = 0; i < 6000; i++)
        {
            string name = (i % 3) switch { 0 => $"IMG_{i}.jpg", 1 => $"dsc{i:D5}.JPG", _ => $"scan {i}.png" };
            names.Add(name);
            File.WriteAllBytes(Path.Combine(_folder, name), Array.Empty<byte>());
        }
        names.Sort((a, b) => NaturalOrder.Compare(a, b));
        Assert.Equal(names, Names(FolderIndex.Build(_folder)));
    }

    [Fact]
    public void MissingFolderIsAnIoError() =>
        Assert.Throws<IOException>(() => FolderIndex.Build(Path.Combine(_folder, "does not exist")));

    [Fact]
    public void EmptyFolderGivesAnEmptyIndex() => Assert.Equal(0, FolderIndex.Build(_folder).Count);
}
