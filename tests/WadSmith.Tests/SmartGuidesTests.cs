using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>Where a corner goes while drawing: lined up with corners nearby, sides on whole 45°, Shift's 15°.</summary>
public sealed class SmartGuidesTests
{
    private static (MapPoint At, IReadOnlyList<Guide> Guides) Place(MapPoint raw, MapPoint snapped, IReadOnlyList<MapPoint> corners, MapPoint? previous = null, bool fifteen = false, bool onCorner = false) =>
        SmartGuides.Place(raw, snapped, onCorner, corners, previous, 4, 16, fifteen);

    [Fact]
    public void ACornerOffTheGridLinesUpWithACornerAboveItAndTheGuideJoinsThem()
    {
        // A remixed map's corner at x 100 (off the 16 grid): the pointer at 98 would snap to 96.
        var (at, guides) = Place(new MapPoint(98, 50), new MapPoint(96, 48), [new MapPoint(100, 300)]);
        Assert.Equal(new MapPoint(100, 48), at);
        Assert.Equal(new Guide(new MapPoint(100, 300), at), Assert.Single(guides));
    }

    [Fact]
    public void LinedUpBothWaysItTakesBoth()
    {
        var (at, guides) = Place(new MapPoint(101, 203), new MapPoint(96, 208), [new MapPoint(100, 0), new MapPoint(-50, 205)]);
        Assert.Equal(new MapPoint(100, 205), at);
        Assert.Equal(2, guides.Count);
    }

    [Fact]
    public void NothingNearMeansTheGridAndNoGuides()
    {
        var (at, guides) = Place(new MapPoint(50, 50), new MapPoint(48, 48), [new MapPoint(200, 200)]);
        Assert.Equal(new MapPoint(48, 48), at);
        Assert.Empty(guides);
    }

    [Fact]
    public void ACornerOfTheMapStaysPut() =>
        Assert.Equal((new MapPoint(7, 9), 0), (Place(new MapPoint(8, 8), new MapPoint(7, 9), [new MapPoint(10, 300)], onCorner: true) is var r ? (r.At, r.Guides.Count) : default));

    [Fact]
    public void ASideNearlyAtFortyFiveDegreesSettlesOnItInWholeGridSteps()
    {
        // From (0, 0) toward (99, 101): within 4 of the 45° line, 99 / 16 ≈ 6 steps.
        var (at, guides) = Place(new MapPoint(99, 101), new MapPoint(96, 96), [], new MapPoint(0, 0));
        Assert.Equal(new MapPoint(96, 96), at);
        Assert.Equal(new Guide(new MapPoint(0, 0), at), Assert.Single(guides));

        // Straight across: the other way stays on the grid.
        var (across, _) = Place(new MapPoint(203, 2), new MapPoint(208, 0), [], new MapPoint(0, 0));
        Assert.Equal(new MapPoint(208, 0), across);
    }

    [Fact]
    public void ASideAtFortyFiveMeetsTheCornerItLinesUpWith()
    {
        // The diagonal from (0, 0) and a corner straight above at x 100: they meet at (100, 100).
        var (at, guides) = Place(new MapPoint(99, 101), new MapPoint(96, 96), [new MapPoint(100, 400)], new MapPoint(0, 0));
        Assert.Equal(new MapPoint(100, 100), at);
        Assert.Equal(2, guides.Count);
    }

    [Fact]
    public void ShiftHoldsTheSideToFifteenDegreeSteps()
    {
        // 31° from the corner before becomes 30°, as long as the pointer is away.
        var raw = new MapPoint(100 * Math.Cos(31 * Math.PI / 180), 100 * Math.Sin(31 * Math.PI / 180));
        var (at, guides) = Place(raw, new MapPoint(80, 48), [new MapPoint(87, 900)], new MapPoint(0, 0), fifteen: true);
        Assert.Equal(new MapPoint(87, 50), at);
        Assert.Single(guides);
    }
}
