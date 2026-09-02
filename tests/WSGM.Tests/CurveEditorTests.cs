using Avalonia.Input;
using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests;

public sealed class CurveEditorTests
{
    private static CurveEditor Editor(params (int Input, int Output)[] points) =>
        new()
        {
            Points = [.. points.Select(point => new CurvePoint(point.Input, point.Output))],
        };

    [Fact]
    public void AddingAPointTargetsTheWidestGapBecauseThatIsWhereResolutionIsMissing()
    {
        CurveEditor editor = Editor((0, 0), (10, 10), (100, 100));

        editor.AddPointAtWidestGap();

        // The 10-100 gap, not the 0-10 one, and at its midpoint.
        Assert.Equal(4, editor.Points.Count);
        Assert.Equal(55, editor.Points[2].Input);
    }

    [Fact]
    public void AnAddedPointLandsOnTheCurveSoTheShapeDoesNotJump()
    {
        CurveEditor editor = Editor((0, 0), (100, 100));

        editor.AddPointAtWidestGap();

        Assert.Equal(new CurvePoint(50, 50), editor.Points[1]);
    }

    [Fact]
    public void AGapWithNoRoomBetweenItsPointsIsNotSplit()
    {
        // Inputs are integers and must stay strictly ascending, so a gap of one has no midpoint.
        CurveEditor editor = Editor((0, 0), (1, 100));

        editor.AddPointAtWidestGap();

        Assert.Equal(2, editor.Points.Count);
    }

    [Fact]
    public void AFullCurveDoesNotGrow()
    {
        // 63 tightly packed points plus a far endpoint: the widest gap still has a
        // midpoint, so the refusal below can only come from the point limit.
        CurveEditor editor = new()
        {
            Points = [.. Enumerable.Range(0, CurveEditing.MaximumPoints - 1)
                .Select(index => new CurvePoint(index, index)), new CurvePoint(100, 100)],
        };

        editor.AddPointAtWidestGap();

        Assert.Equal(CurveEditing.MaximumPoints, editor.Points.Count);
    }

    [Fact]
    public void RemovingTheSelectedPointRaisesTheChange()
    {
        CurveEditor editor = Editor((0, 0), (50, 50), (100, 100));
        editor.SelectedIndex = 1;
        IReadOnlyList<CurvePoint>? raised = null;
        editor.CurveChanged += curve => raised = curve;

        editor.RemoveSelectedPoint();

        Assert.NotNull(raised);
        Assert.Equal(2, editor.Points.Count);
    }

    [Fact]
    public void RemovingAnEndpointChangesNothingAndRaisesNothing()
    {
        // The endpoints define the curve's answer at the ends of the device's range.
        CurveEditor editor = Editor((0, 0), (50, 50), (100, 100));
        editor.SelectedIndex = 2;
        bool raised = false;
        editor.CurveChanged += _ => raised = true;

        editor.RemoveSelectedPoint();

        Assert.Equal(3, editor.Points.Count);
        Assert.False(raised);
    }

    [Fact]
    public void RemovingWithNothingSelectedIsNotAnError()
    {
        CurveEditor editor = Editor((0, 0), (50, 50), (100, 100));

        editor.RemoveSelectedPoint();

        Assert.Equal(3, editor.Points.Count);
    }

    [Fact]
    public void AnEmptyCurveIsLeftAloneRatherThanInvented()
    {
        CurveEditor editor = Editor();

        editor.AddPointAtWidestGap();

        Assert.Empty(editor.Points);
    }

    [Fact]
    public void GamepadDirectionsSelectPointsAndEditTheirOutput()
    {
        CurveEditor editor = Editor((0, 0), (50, 50), (100, 100));
        editor.SelectedIndex = 0;
        IReadOnlyList<CurvePoint>? changed = null;
        editor.CurveChanged += curve => changed = curve;

        editor.ApplyDirection(NavigationDirection.Right);
        editor.ApplyDirection(NavigationDirection.Up);

        Assert.Equal(1, editor.SelectedIndex);
        Assert.NotNull(changed);
        Assert.Equal(new CurvePoint(50, 51), editor.Points[1]);
    }

    [Fact]
    public void DirectionAtTheDeviceBoundDoesNotPublishAFalseEdit()
    {
        CurveEditor editor = Editor((0, 0), (100, 100));
        editor.SelectedIndex = 1;
        bool changed = false;
        editor.CurveChanged += _ => changed = true;

        editor.ApplyDirection(NavigationDirection.Up);

        Assert.False(changed);
        Assert.Equal(new CurvePoint(100, 100), editor.Points[1]);
    }
}
