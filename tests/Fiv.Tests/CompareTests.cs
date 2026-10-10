using Fiv.Comparing;
using Fiv.Imaging;
using Xunit;

namespace Fiv.Tests;

public class CompareGeometryTests
{
    [Theory]
    [InlineData(2, 2000, 1000, 6000, 4000, 2, 1)]   // landscape images in a wide view: next to each other
    [InlineData(2, 1000, 2000, 6000, 4000, 1, 2)]   // in a tall view: above each other
    [InlineData(2, 2000, 1000, 4000, 6000, 2, 1)]   // portrait images
    [InlineData(2, 2000, 1000, 20000, 4000, 1, 2)]  // panoramas are stacked even in a wide view
    [InlineData(4, 2000, 1200, 6000, 4000, 2, 2)]
    [InlineData(3, 2000, 1200, 6000, 4000, 2, 2)]   // three: two rows show them larger than one row of three
    [InlineData(3, 3000, 700, 6000, 4000, 3, 1)]
    [InlineData(9, 2000, 1200, 6000, 4000, 3, 3)]
    [InlineData(1, 2000, 1200, 6000, 4000, 1, 1)]
    public void GridShowsTheImagesAsLargeAsPossible(int count, int areaWidth, int areaHeight, int canvasWidth, int canvasHeight, int columns, int rows) =>
        Assert.Equal((columns, rows), CompareGeometry.BestGrid(count, areaWidth, areaHeight, canvasWidth, canvasHeight, 2));

    [Fact]
    public void GridWorksBeforeAnyImageSizeIsKnown() =>
        Assert.Equal((2, 1), CompareGeometry.BestGrid(2, 2000, 1000, 0, 0, 2));

    [Fact]
    public void GridHoldsEveryPane()
    {
        for (int count = 1; count <= 16; count++)
        {
            (int columns, int rows) = CompareGeometry.BestGrid(count, 1900, 1100, 6000, 4000, 2);
            Assert.True(columns * rows >= count, $"{count} panes do not fit in {columns} x {rows}");
            Assert.True(columns * (rows - 1) < count, $"{columns} x {rows} has an empty row for {count} panes");
        }
    }

    [Fact]
    public void PanesAreEqualInSizeInsideTheAreaAndApart()
    {
        var area = new PaneRect(100, 50, 1001, 701); // odd sizes: pixels are left over
        const int gap = 3;
        var panes = Enumerable.Range(0, 6).Select(i => CompareGeometry.Pane(i, 3, 2, area, gap)).ToArray();
        Assert.All(panes, pane => Assert.Equal((panes[0].Width, panes[0].Height), (pane.Width, pane.Height)));
        Assert.All(panes, pane => Assert.True(pane.X >= area.X && pane.Y >= area.Y && pane.Right <= area.Right && pane.Bottom <= area.Bottom));
        Assert.Equal(panes[0].Right + gap, panes[1].X);
        Assert.Equal(panes[0].Bottom + gap, panes[3].Y);
        Assert.Equal(panes[0].X, panes[3].X);
        // What is left over is split between the two sides.
        Assert.InRange((panes[0].X - area.X) - (area.Right - panes[2].Right), -1, 1);
    }

    [Fact]
    public void PaneContainsItsPixelsOnly()
    {
        var pane = new PaneRect(10, 20, 30, 40);
        Assert.True(pane.Contains(10, 20));
        Assert.True(pane.Contains(39, 59));
        Assert.False(pane.Contains(40, 59));
        Assert.False(pane.Contains(39, 60));
        Assert.False(pane.Contains(9, 20));
    }

    [Fact]
    public void TopLeftPlacementLeavesPixelsWhereTheyAre() =>
        Assert.Equal(Affine2.Identity, CompareGeometry.Placement(SizeMatch.TopLeft, 3000, 2000, 6000, 4000));

    [Fact]
    public void CentredPlacementMovesByWholePixels()
    {
        Affine2 placement = CompareGeometry.Placement(SizeMatch.Centre, 3001, 2000, 6000, 4001);
        Assert.Equal((1, 1), (placement.A, placement.D));
        Assert.Equal((1499, 1000), (placement.Tx, placement.Ty)); // (6000 - 3001) / 2 = 1499.5, (4001 - 2000) / 2 = 1000.5: both down to whole pixels
    }

    [Fact]
    public void ScaledPlacementPutsAHalfSizeCopyOverTheOriginal()
    {
        Affine2 placement = CompareGeometry.Placement(SizeMatch.ScaleToFit, 3000, 2000, 6000, 4000);
        Assert.Equal((0.0, 0.0), placement.Apply(0, 0));
        Assert.Equal((6000.0, 4000.0), placement.Apply(3000, 2000));
    }

    [Fact]
    public void ScaledPlacementKeepsTheShapeAndCentres()
    {
        // A square image on a 3:2 canvas: as tall as the canvas, in the middle of its width.
        Affine2 placement = CompareGeometry.Placement(SizeMatch.ScaleToFit, 1000, 1000, 6000, 4000);
        Assert.Equal((1000.0, 0.0), placement.Apply(0, 0));
        Assert.Equal((5000.0, 4000.0), placement.Apply(1000, 1000));
    }

    [Fact]
    public void PlacementOfAnImageWithoutSizeIsHarmless() =>
        Assert.Equal(Affine2.Identity, CompareGeometry.Placement(SizeMatch.ScaleToFit, 0, 0, 6000, 4000));

    [Theory]
    [InlineData(CompareMode.Single, false, false)]
    [InlineData(CompareMode.SideBySide, false, true)]
    [InlineData(CompareMode.Grid, false, true)]
    [InlineData(CompareMode.Split, true, false)]
    [InlineData(CompareMode.Blend, true, false)]
    [InlineData(CompareMode.Checker, true, false)]
    [InlineData(CompareMode.Difference, true, false)]
    [InlineData(CompareMode.Ssim, true, false)]
    public void ModesAreClassified(CompareMode mode, bool overlay, bool panes)
    {
        Assert.Equal(overlay, mode.IsOverlay());
        Assert.Equal(panes, mode.HasPanes());
    }
}

public class CompareSelectionTests
{
    static CompareSelection Of(int count, int active = 0)
    {
        var selection = new CompareSelection();
        selection.Reset(count, active);
        return selection;
    }

    [Fact]
    public void StartsAtTheFirstImageComparedWithTheSecond()
    {
        CompareSelection selection = Of(3);
        Assert.Equal((0, 1), (selection.Active, selection.Other));
        Assert.Equal((0, 1), selection.Pair);
    }

    [Fact]
    public void StartingAtALaterImageComparesItWithTheFirst()
    {
        CompareSelection selection = Of(4, active: 2);
        Assert.Equal((2, 0), (selection.Active, selection.Other));
        Assert.Equal((0, 2), selection.Pair);
    }

    [Fact]
    public void TheImageLeftBecomesTheOneComparedWith()
    {
        CompareSelection selection = Of(4);
        Assert.True(selection.Select(3));
        Assert.Equal((3, 0), (selection.Active, selection.Other));
        Assert.True(selection.Select(2));
        Assert.Equal((2, 3), (selection.Active, selection.Other));
    }

    [Fact]
    public void SelectingTheActiveImageOrOneOutsideTheSetChangesNothing()
    {
        CompareSelection selection = Of(3);
        Assert.False(selection.Select(0));
        Assert.False(selection.Select(3));
        Assert.False(selection.Select(-1));
        Assert.Equal((0, 1), (selection.Active, selection.Other));
    }

    [Fact]
    public void FlickingBetweenTwoImagesNeverMakesThemTradePlaces()
    {
        CompareSelection selection = Of(2);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(selection.Toggle());
            Assert.Equal((0, 1), selection.Pair);
        }
        Assert.Equal(1, selection.Active);
    }

    [Fact]
    public void SwappingReversesThePair()
    {
        CompareSelection selection = Of(3);
        selection.Select(2);
        Assert.Equal((0, 2), selection.Pair);
        selection.Swapped = true;
        Assert.Equal((2, 0), selection.Pair);
    }

    [Fact]
    public void SteppingGoesAroundTheEnds()
    {
        CompareSelection selection = Of(3);
        Assert.True(selection.Step(-1));
        Assert.Equal(2, selection.Active);
        Assert.True(selection.Step(1));
        Assert.Equal(0, selection.Active);
        Assert.Equal(2, selection.Other);
    }

    [Fact]
    public void ASetOfOneHasNowhereToGo()
    {
        CompareSelection selection = Of(1);
        Assert.False(selection.Step(1));
        Assert.False(selection.Toggle());
        Assert.Equal((0, 0), (selection.Active, selection.Other));
    }

    [Fact]
    public void AShortPressSwitches()
    {
        CompareSelection selection = Of(3);
        Assert.True(selection.Press(key: 51, index: 2, timestamp: 1000));
        Assert.False(selection.Release(key: 51, timestamp: 1100, holdTicks: 250));
        Assert.Equal((2, 0), (selection.Active, selection.Other));
    }

    [Fact]
    public void AHeldKeyPeeksAndReturns()
    {
        CompareSelection selection = Of(3);
        selection.Select(1); // at 1, compared with 0
        Assert.True(selection.Press(key: 51, index: 2, timestamp: 1000));
        Assert.Equal(2, selection.Active);
        Assert.True(selection.Release(key: 51, timestamp: 1300, holdTicks: 250));
        Assert.Equal((1, 0), (selection.Active, selection.Other));
    }

    [Fact]
    public void HoldingTheKeyOfTheActiveImageDoesNothing()
    {
        CompareSelection selection = Of(3);
        Assert.False(selection.Press(key: 49, index: 0, timestamp: 1000));
        Assert.False(selection.Release(key: 49, timestamp: 2000, holdTicks: 250));
        Assert.Equal((0, 1), (selection.Active, selection.Other));
    }

    [Fact]
    public void ASecondKeyWhileOneIsHeldReturnsToWhereTheFirstStarted()
    {
        CompareSelection selection = Of(4);
        selection.Press(key: 50, index: 1, timestamp: 1000);
        selection.Press(key: 52, index: 3, timestamp: 1100);
        Assert.False(selection.Release(key: 50, timestamp: 1500, holdTicks: 250)); // the first key no longer counts
        Assert.Equal(3, selection.Active);
        Assert.True(selection.Release(key: 52, timestamp: 1600, holdTicks: 250));
        Assert.Equal((0, 1), (selection.Active, selection.Other));
    }

    [Fact]
    public void AForgottenPressIsASwitch()
    {
        // The window lost the keyboard while the key was down: its release never arrives.
        CompareSelection selection = Of(3);
        selection.Press(key: 50, index: 1, timestamp: 1000);
        selection.ForgetPress();
        Assert.False(selection.Release(key: 50, timestamp: 9000, holdTicks: 250));
        Assert.Equal(1, selection.Active);
        // The next held key returns to where it itself started, not to before the forgotten one.
        selection.Press(key: 51, index: 2, timestamp: 10000);
        Assert.True(selection.Release(key: 51, timestamp: 11000, holdTicks: 250));
        Assert.Equal((1, 0), (selection.Active, selection.Other));
    }

    [Fact]
    public void ReleasingAKeyThatWasNotPressedDoesNothing()
    {
        CompareSelection selection = Of(3);
        Assert.False(selection.Release(key: 50, timestamp: 5000, holdTicks: 250));
        Assert.Equal((0, 1), (selection.Active, selection.Other));
    }

    [Fact]
    public void AddingToASetKeepsTheSelection()
    {
        CompareSelection selection = Of(2);
        selection.Select(1);
        selection.Added();
        Assert.Equal(3, selection.Count);
        Assert.Equal((1, 0), (selection.Active, selection.Other));
        Assert.True(selection.Select(2));
    }

    [Theory]
    [InlineData(4, 2, 0, 0, 1, 0)]  // an image before both: they move up
    [InlineData(4, 2, 0, 3, 2, 0)]  // an image after both: nothing moves
    [InlineData(4, 2, 0, 2, 2, 0)]  // the active one: its successor takes its place
    [InlineData(4, 3, 0, 3, 2, 0)]  // the active one at the end: the new last one
    [InlineData(3, 1, 0, 0, 0, 1)]  // the one compared with, before the active one
    [InlineData(3, 0, 1, 1, 0, 1)]  // the one compared with, after the active one
    public void RemovingAnImageKeepsTwoDifferentImagesSelected(int count, int active, int other, int removed, int expectedActive, int expectedOther)
    {
        CompareSelection selection = Of(count, other);
        if (active != other) selection.Select(active);
        Assert.Equal((active, other), (selection.Active, selection.Other));
        selection.Removed(removed);
        Assert.Equal(count - 1, selection.Count);
        Assert.Equal((expectedActive, expectedOther), (selection.Active, selection.Other));
        Assert.NotEqual(selection.Active, selection.Other);
    }

    [Fact]
    public void RemovingDownToOneImageLeavesItSelected()
    {
        CompareSelection selection = Of(2);
        selection.Removed(0);
        Assert.Equal((1, 0, 0), (selection.Count, selection.Active, selection.Other));
        selection.Removed(0);
        Assert.Equal(0, selection.Count);
    }
}

public class SettingsTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "fiv_settings_" + Guid.NewGuid().ToString("N"));

    string File => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void AMissingFileGivesTheDefaults()
    {
        Settings settings = Settings.Load(File);
        Assert.Equal(CompareMode.SideBySide, settings.CompareMode);
        Assert.Equal(SizeMatch.TopLeft, settings.CompareSizes);
        Assert.True(settings.CompareLinked && settings.CompareCursor && settings.CompareLabels);
        Assert.True(settings.ShowBrowser);
        Assert.Equal(4096, settings.CacheMb);
    }

    [Fact]
    public void WhatIsSavedIsLoadedAgain()
    {
        var saved = new Settings
        {
            WindowX = -1200, WindowY = 40, WindowWidth = 1600, WindowHeight = 900, Maximized = true,
            ViewLeft = 0.25, ViewTop = 0, ViewRight = 1, ViewBottom = 0.8,
            ShowBrowser = false, ShowFilmstrip = true, BrowserList = true, ThumbnailSize = 192,
            Sort = Fiv.Indexing.FolderSort.Modified, SortDescending = true, KeepView = true, ExactPixels = false,
            CacheMb = 1024, VramMb = 512, ThumbnailCacheMb = 100,
            CompareMode = CompareMode.Blend, CompareSizes = SizeMatch.ScaleToFit, CompareLinked = false, CompareCursor = false,
            CompareLabels = false, CompareSplitRows = true, CompareBlinkMs = 250, CompareCheckerSize = 32,
        };
        saved.RecentFiles.AddRange([@"C:\a\1 %.jpg", @"\\server\share\ü.png"]);
        saved.RecentFolders.Add(@"D:\photos");
        saved.Save(File);

        Settings loaded = Settings.Load(File);
        Assert.Equal((-1200, 40, 1600, 900, true), (loaded.WindowX, loaded.WindowY, loaded.WindowWidth, loaded.WindowHeight, loaded.Maximized));
        Assert.Equal((0.25, 0.0, 1.0, 0.8), (loaded.ViewLeft, loaded.ViewTop, loaded.ViewRight, loaded.ViewBottom));
        Assert.Equal((false, true, true, 192), (loaded.ShowBrowser, loaded.ShowFilmstrip, loaded.BrowserList, loaded.ThumbnailSize));
        Assert.Equal((Fiv.Indexing.FolderSort.Modified, true, true, false), (loaded.Sort, loaded.SortDescending, loaded.KeepView, loaded.ExactPixels));
        Assert.Equal((1024, 512, 100), (loaded.CacheMb, loaded.VramMb, loaded.ThumbnailCacheMb));
        Assert.Equal((CompareMode.Blend, SizeMatch.ScaleToFit), (loaded.CompareMode, loaded.CompareSizes));
        Assert.Equal((false, false, false, true), (loaded.CompareLinked, loaded.CompareCursor, loaded.CompareLabels, loaded.CompareSplitRows));
        Assert.Equal((250, 32), (loaded.CompareBlinkMs, loaded.CompareCheckerSize));
        Assert.Equal(saved.RecentFiles, loaded.RecentFiles);
        Assert.Equal(saved.RecentFolders, loaded.RecentFolders);
    }

    [Fact]
    public void ValuesOutOfRangeAndUnknownNamesAreTamed()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """
            {
              "compareMode": "Sideways", "compareSizes": "7", "compareBlinkMs": 1, "compareCheckerSize": 100000,
              "thumbnailSize": 1000, "viewLeft": 0.9, "viewRight": 0.1,
              "somethingNewer": { "nested": [1, 2, 3] },
              "cacheMb": 2048
            }
            """);
        Settings loaded = Settings.Load(File);
        Assert.Equal(CompareMode.SideBySide, loaded.CompareMode);
        Assert.Equal(SizeMatch.TopLeft, loaded.CompareSizes);
        Assert.Equal(Settings.MinBlinkMs, loaded.CompareBlinkMs);
        Assert.Equal(Settings.MaxCheckerSize, loaded.CompareCheckerSize);
        Assert.Equal(256, loaded.ThumbnailSize);
        Assert.Equal((0.0, 1.0), (loaded.ViewLeft, loaded.ViewRight)); // an impossible rectangle: the whole client area
        Assert.Equal(2048, loaded.CacheMb);                             // what follows an unknown entry is still read
    }

    [Fact]
    public void ADamagedFileKeepsWhatCouldBeRead()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "cacheMb": 1234, "vramMb": "lots", "showFilmstrip": true }""");
        Settings loaded = Settings.Load(File);
        Assert.Equal(1234, loaded.CacheMb);
        Assert.Equal(2048, loaded.VramMb);
    }

    [Fact]
    public void RecentListsPutTheNewestFirstWithoutDuplicates()
    {
        var list = new List<string> { @"C:\a.jpg", @"C:\b.jpg" };
        Settings.Remember(list, @"c:\B.JPG");
        Assert.Equal(new[] { @"c:\B.JPG", @"C:\a.jpg" }, list);
        for (int i = 0; i < 30; i++) Settings.Remember(list, $@"C:\{i}.jpg");
        Assert.Equal(Settings.MaxRecent, list.Count);
        Assert.Equal(@"C:\29.jpg", list[0]);
    }
}
