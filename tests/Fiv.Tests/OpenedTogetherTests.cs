using Fiv.Comparing;
using Xunit;

namespace Fiv.Tests;

public class OpenedTogetherTests
{
    const string A = @"C:\p\a.jpg", B = @"C:\p\b.jpg", C = @"C:\p\c.jpg";

    [Fact]
    public void AFileOnItsOwnIsOnItsOwn()
    {
        var together = new OpenedTogether();
        Assert.True(together.Add(A, 1000));
        Assert.Equal([A], together.Files);
    }

    [Fact]
    public void FilesThatFollowCloselyJoin()
    {
        var together = new OpenedTogether();
        together.Add(A, 1000);
        Assert.True(together.Add(B, 1030));
        Assert.True(together.Add(C, 1060));
        Assert.Equal([A, B, C], together.Files);
    }

    [Fact]
    public void AFileOpenedLaterStartsOver()
    {
        var together = new OpenedTogether();
        together.Add(A, 1000);
        Assert.True(together.Add(B, 1000 + OpenedTogether.FirstMs));
        Assert.Equal([B], together.Files);
    }

    [Fact]
    public void OnceItIsASelectionTheRestMayTakeLonger()
    {
        var together = new OpenedTogether();
        together.Add(A, 1000);
        together.Add(B, 1020);
        // Longer than a second file may take, but they are known to be a selection by now.
        Assert.True(together.Add(C, 1020 + OpenedTogether.MoreMs - 1));
        Assert.Equal(3, together.Files.Count);

        Assert.True(together.Add(A, 1019 + OpenedTogether.MoreMs + OpenedTogether.MoreMs));
        Assert.Equal([A], together.Files);
    }

    [Fact]
    public void TheFilesAreInTheOrderOfTheirNamesHoweverTheyArrive()
    {
        var together = new OpenedTogether();
        together.Add(@"C:\p\img10.jpg", 0);
        together.Add(@"C:\p\IMG2.jpg", 10);
        together.Add(@"C:\q\img1.jpg", 20);
        together.Add(@"C:\p\img1.jpg", 30);
        Assert.Equal([@"C:\p\img1.jpg", @"C:\q\img1.jpg", @"C:\p\IMG2.jpg", @"C:\p\img10.jpg"], together.Files);
    }

    [Fact]
    public void AFileOpenedTwiceIsThereOnce()
    {
        var together = new OpenedTogether();
        together.Add(A, 0);
        Assert.False(together.Add(@"C:\P\A.JPG", 10));
        Assert.Equal([A], together.Files);

        // Later, it is opened anew like any other file.
        Assert.True(together.Add(A, 10_000));
        Assert.Equal([A], together.Files);
    }

    [Fact]
    public void AfterClearTheNextFileIsOnItsOwn()
    {
        var together = new OpenedTogether();
        together.Add(A, 0);
        together.Clear();
        Assert.True(together.Add(B, 10));
        Assert.Equal([B], together.Files);
    }
}
