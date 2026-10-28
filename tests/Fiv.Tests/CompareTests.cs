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
    [InlineData(CompareMode.Overlay, true, false)]
    [InlineData(CompareMode.Checker, true, false)]
    [InlineData(CompareMode.Difference, true, false)]
    [InlineData(CompareMode.Ssim, true, false)]
    public void ModesAreClassified(CompareMode mode, bool overlay, bool panes)
    {
        Assert.Equal(overlay, mode.IsOverlay());
        Assert.Equal(panes, mode.HasPanes());
        Assert.Equal(mode is CompareMode.Split or CompareMode.Blend or CompareMode.Overlay or CompareMode.Checker, mode.IsStack());
        Assert.Equal(mode is CompareMode.Difference or CompareMode.Ssim, mode.IsPairwise());
    }

    [Theory]
    [InlineData(2, 2000, 1000, 6000, 4000, 2, 1)]  // two as before: next to each other in a wide view
    [InlineData(2, 1000, 2000, 6000, 4000, 1, 2)]  // above each other in a tall one
    [InlineData(4, 3000, 1000, 6000, 4000, 4, 1)]  // side by side is one row, never a grid
    [InlineData(4, 1000, 3000, 6000, 4000, 1, 4)]  // or one column, if that shows them larger
    [InlineData(1, 2000, 1000, 6000, 4000, 1, 1)]
    public void SideBySideIsOneRowOrOneColumn(int count, int areaWidth, int areaHeight, int canvasWidth, int canvasHeight, int columns, int rows) =>
        Assert.Equal((columns, rows), CompareGeometry.BestRow(count, areaWidth, areaHeight, canvasWidth, canvasHeight, 2));

    [Fact]
    public void TwoImagesTakeTurnsOnTheCheckerboardAsBlackAndWhiteSquaresDo()
    {
        for (int y = -3; y < 3; y++)
        for (int x = -3; x < 3; x++)
            Assert.Equal(((x + y) % 2 + 2) % 2, CompareGeometry.CheckerLayer(x, y, 2));
        Assert.Equal(0, CompareGeometry.CheckerLayer(5, 7, 1));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(16)]
    public void OnTheCheckerboardOfSeveralImagesEveryImageHasCellsAndNoTwoNeighboursAreTheSame(int layers)
    {
        var seen = new HashSet<int>();
        for (int y = -8; y < 8; y++)
        {
            for (int x = -8; x < 8; x++)
            {
                int layer = CompareGeometry.CheckerLayer(x, y, layers);
                Assert.InRange(layer, 0, layers - 1);
                seen.Add(layer);
                Assert.NotEqual(layer, CompareGeometry.CheckerLayer(x + 1, y, layers));
                Assert.NotEqual(layer, CompareGeometry.CheckerLayer(x, y + 1, layers));
                // Across a corner too, once there are four images or more.
                if (layers >= 4) Assert.NotEqual(layer, CompareGeometry.CheckerLayer(x + 1, y + 1, layers));
                if (layers >= 4) Assert.NotEqual(layer, CompareGeometry.CheckerLayer(x + 1, y - 1, layers));
            }
        }
        Assert.Equal(layers, seen.Count);
    }
}

public class OverlayMixTests
{
    /// <summary>The passes drawn as the GPU draws them: the first over the background, each after it over what is there.</summary>
    static double Layered(double background, double[] colors, float[] passes)
    {
        double result = background;
        for (int k = 0; k < colors.Length; k++) result += (colors[k] - result) * passes[k];
        return result;
    }

    /// <summary>The overlay as it is meant: each image by its opacity, covering what layers of those opacities would.</summary>
    static double Meant(double background, double[] colors, float[] opacities)
    {
        double sum = 0, weight = 0, through = 1;
        for (int k = 0; k < colors.Length; k++)
        {
            sum += opacities[k] * colors[k];
            weight += opacities[k];
            through *= 1 - opacities[k];
        }
        return weight > 0 ? background * through + sum / weight * (1 - through) : background;
    }

    [Theory]
    [InlineData(new[] { 1f, 1f, 1f, 1f })]
    [InlineData(new[] { 0.5f })]
    [InlineData(new[] { 0.3f, 0.9f, 0.6f })]
    [InlineData(new[] { 0f, 0.4f, 0f, 1f, 0.2f })]
    [InlineData(new[] { 0f, 0f })]
    [InlineData(new[] { 1f, 0.25f, 1f, 0.75f, 0.5f, 1f, 0.1f, 0.9f })]
    public void LayerOverLayerWithThePassOpacitiesIsTheOverlay(float[] opacities)
    {
        float[] passes = new float[opacities.Length];
        OverlayMix.PassOpacities(opacities, passes);
        Assert.All(passes, pass => Assert.InRange(pass, 0, 1));
        var random = new Random(opacities.Length);
        for (int trial = 0; trial < 20; trial++)
        {
            double background = random.NextDouble();
            double[] colors = [.. Enumerable.Range(0, opacities.Length).Select(_ => random.NextDouble())];
            Assert.Equal(Meant(background, colors, opacities), Layered(background, colors, passes), 5);
            // In any order: what the overlay is does not depend on it.
            int[] shuffled = [.. Enumerable.Range(0, opacities.Length).OrderBy(_ => random.Next())];
            float[] reordered = [.. shuffled.Select(i => opacities[i])];
            float[] reorderedPasses = new float[opacities.Length];
            OverlayMix.PassOpacities(reordered, reorderedPasses);
            Assert.Equal(Meant(background, colors, opacities), Layered(background, [.. shuffled.Select(i => colors[i])], reorderedPasses), 5);
        }
    }

    [Fact]
    public void AllOpaqueIsAnEqualMixAndOneAloneIsItsOwnTransparency()
    {
        float[] passes = new float[4];
        OverlayMix.PassOpacities([1, 1, 1, 1], passes);
        Assert.Equal(1.0, Layered(0, [4, 0, 0, 0], passes), 6); // a quarter of each
        Assert.Equal(1.0, Layered(0, [0, 0, 0, 4], passes), 6);
        OverlayMix.PassOpacities([0, 0.5f, 0, 0], passes);
        Assert.Equal(0.6, Layered(0.2, [9, 1, 9, 9], passes), 6); // half of it over the background, the others not there
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

    static int[] Order(CompareSelection selection)
    {
        int[] order = new int[selection.Count];
        return order[..selection.Order(order)];
    }

    [Fact]
    public void StartsAtTheFirstImageComparedWithTheSecond()
    {
        CompareSelection selection = Of(3);
        Assert.Equal((0, 1), (selection.Active, selection.Other));
        Assert.Equal(0, selection.Reference);
        Assert.Equal([0, 1, 2], Order(selection));
    }

    [Fact]
    public void StartingAtALaterImageComparesItWithTheFirst()
    {
        CompareSelection selection = Of(4, active: 2);
        Assert.Equal((2, 0), (selection.Active, selection.Other));
        Assert.Equal(0, selection.Reference); // the reference is the first image, whichever one the set starts at
    }

    [Fact]
    public void EveryImageIsShownAndAllButTheReferenceMove()
    {
        CompareSelection selection = Of(4);
        Assert.Equal(4, selection.ShownCount);
        Assert.True(Enumerable.Range(0, 4).All(selection.IsShown));
        Assert.Equal([false, true, true, true], Enumerable.Range(0, 4).Select(selection.Moves));
    }

    [Fact]
    public void TheViewsTakeTheShownImagesBeginningWithTheReference()
    {
        CompareSelection selection = Of(5);
        Assert.True(selection.SetShown(1, false));
        Assert.True(selection.SetReference(3));
        Assert.Equal([3, 4, 0, 2], Order(selection)); // on through the set and round its end, without the one taken out
        Assert.False(selection.IsShown(1));
    }

    [Fact]
    public void TheReferenceAndTheLastImageStayShown()
    {
        CompareSelection selection = Of(3);
        Assert.False(selection.SetShown(0, false)); // the reference
        Assert.True(selection.SetShown(1, false));
        Assert.True(selection.SetShown(2, false));
        Assert.Equal(1, selection.ShownCount);
        Assert.True(selection.SetReference(2)); // a reference taken out comes back in
        Assert.True(selection.IsShown(2));
        Assert.True(selection.SetShown(0, false));
        Assert.False(selection.SetShown(2, false)); // the last one left, and the reference
        Assert.Equal([2], Order(selection));
    }

    [Fact]
    public void SteppingTheReferenceGoesThroughTheShownImages()
    {
        CompareSelection selection = Of(4);
        selection.SetShown(2, false);
        Assert.True(selection.StepReference(1));
        Assert.Equal(1, selection.Reference);
        Assert.True(selection.StepReference(1));
        Assert.Equal(3, selection.Reference); // past the one taken out
        Assert.True(selection.StepReference(1));
        Assert.Equal(0, selection.Reference); // round the end
        Assert.True(selection.StepReference(-1));
        Assert.Equal(3, selection.Reference);
    }

    [Fact]
    public void WithTwoImagesMakingTheOtherTheReferenceSwapsThem()
    {
        CompareSelection selection = Of(2);
        Assert.Equal([0, 1], Order(selection));
        Assert.True(selection.Moves(1));
        Assert.True(selection.StepReference(1));
        Assert.Equal([1, 0], Order(selection));
        Assert.Equal((true, false), (selection.Moves(0), selection.Moves(1))); // and what is aligned is the other one
        Assert.True(selection.StepReference(1));
        Assert.Equal([0, 1], Order(selection));
    }

    [Fact]
    public void WithOnlyTheReferenceShownThereIsNoOtherToStepTo()
    {
        CompareSelection selection = Of(3);
        selection.SetShown(1, false);
        selection.SetShown(2, false);
        Assert.False(selection.StepReference(1));
        Assert.Equal(0, selection.Reference);
    }

    [Fact]
    public void TheReferenceIsNeverMoved()
    {
        CompareSelection selection = Of(3);
        Assert.False(selection.SetMoves(0, true));
        Assert.False(selection.Moves(0));
        Assert.True(selection.SetMoves(2, false));
        Assert.False(selection.Moves(2));
        selection.SetReference(1);
        Assert.Equal((true, false, false), (selection.Moves(0), selection.Moves(1), selection.Moves(2))); // its tick is kept for when it is not the reference
        selection.SetReference(0);
        Assert.True(selection.Moves(1));
    }

    [Fact]
    public void RemovingAnImageClosesTheGapInWhatIsShownAndMoved()
    {
        CompareSelection selection = Of(5);
        selection.SetShown(3, false);
        selection.SetMoves(4, false);
        selection.SetReference(2);
        selection.Removed(1);
        Assert.Equal(4, selection.Count);
        Assert.Equal(1, selection.Reference); // it moved up with the images after the removed one
        Assert.Equal([true, true, false, true], Enumerable.Range(0, 4).Select(selection.IsShown));
        Assert.Equal([true, false, true, false], Enumerable.Range(0, 4).Select(selection.Moves));
    }

    [Fact]
    public void RemovingTheReferenceMakesItsSuccessorTheReferenceAndShowsIt()
    {
        CompareSelection selection = Of(4);
        selection.SetShown(2, false);
        selection.SetReference(1);
        selection.Removed(1);
        Assert.Equal(1, selection.Reference); // what was image 2 is image 1 now
        Assert.True(selection.IsShown(1));
        selection.Removed(2);
        selection.Removed(1);
        Assert.Equal((1, 0), (selection.Count, selection.Reference));
    }

    [Fact]
    public void AnAddedImageIsShownAndMoved()
    {
        CompareSelection selection = Of(2);
        selection.Added();
        Assert.True(selection.IsShown(2));
        Assert.True(selection.Moves(2));
        Assert.Equal([0, 1, 2], Order(selection));
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
            Assert.Equal([0, 1], Order(selection));
        }
        Assert.Equal(1, selection.Active);
        Assert.Equal(0, selection.Reference);
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
