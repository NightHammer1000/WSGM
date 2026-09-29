using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Controls;

/// <summary>
/// Edits a device curve — a fan curve, an RGB response — by dragging its points.
/// </summary>
/// <remarks>
/// Presentation only. Every edit goes through <see cref="CurveEditing"/>, which is what guarantees
/// the curve stays inside the contract the device router validates; this control owns hit testing,
/// rendering, and input, and decides nothing about what a valid curve is.
/// <para>
/// Usable by touch, mouse, keyboard, and gamepad, because it appears on a handheld's Settings page:
/// a selected point moves with the arrow keys or the left stick, and the two commit affordances
/// (add, remove) are reachable without a right mouse button that the device does not have.
/// </para>
/// </remarks>
internal sealed class CurveEditor : Control
{
    /// <summary>How close a pointer must be to a point to grab it, in device-independent pixels.</summary>
    /// <remarks>
    /// Sized for a fingertip rather than a cursor. Too small and the control is unusable on the
    /// panel it was built for; too large and adjacent points cannot be told apart.
    /// </remarks>
    private const double GrabRadius = 22;

    /// <summary>Radius of a drawn point handle.</summary>
    private const double HandleRadius = 7;

    /// <summary>Padding around the plot so handles at the bounds are not clipped.</summary>
    private const double Inset = HandleRadius + 3;

    /// <summary>The curve being edited.</summary>
    public static readonly StyledProperty<IReadOnlyList<CurvePoint>> PointsProperty =
        AvaloniaProperty.Register<CurveEditor, IReadOnlyList<CurvePoint>>(
            nameof(Points),
            defaultValue: []);

    /// <summary>The percent-over-percent plane every authored curve is edited within.</summary>
    private static readonly CurveBounds EditBounds = new(0, 100, 0, 100);

    /// <summary>Index of the selected point, or -1 when none is selected.</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<CurveEditor, int>(nameof(SelectedIndex), defaultValue: -1);

    private int _dragIndex = -1;

    static CurveEditor()
    {
        AffectsRender<CurveEditor>(PointsProperty, SelectedIndexProperty);
        FocusableProperty.OverrideDefaultValue<CurveEditor>(true);
    }

    /// <summary>Raised when an edit produced a new curve.</summary>
    /// <remarks>
    /// The control does not persist anything. The owner decides whether an edit is written to a
    /// profile, held until a Save, or discarded, because that is policy and this is a control.
    /// </remarks>
    internal event Action<IReadOnlyList<CurvePoint>>? CurveChanged;

    /// <summary>The curve being edited.</summary>
    public IReadOnlyList<CurvePoint> Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>Index of the selected point, or -1 when none is selected.</summary>
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>Adds a point at the midpoint of the widest gap.</summary>
    /// <remarks>
    /// Deliberately not "at the selection": the reason to add a point is that the curve needs more
    /// resolution somewhere, and the widest gap is where that is true. It also gives the keyboard
    /// and gamepad paths an add that needs no pointer position.
    /// </remarks>
    internal void AddPointAtWidestGap()
    {
        IReadOnlyList<CurvePoint> points = Points;
        if (points.Count == 0)
        {
            LogEditRefused("add", "the curve has no points to split");
            return;
        }

        if (points.Count >= CurveEditing.MaximumPoints)
        {
            LogEditRefused("add", $"the {CurveEditing.MaximumPoints}-point limit is already reached");
            return;
        }

        int widest = 0;
        int at = -1;
        for (int index = 1; index < points.Count; index++)
        {
            int gap = points[index].Input - points[index - 1].Input;
            if (gap > widest)
            {
                widest = gap;
                at = index;
            }
        }

        // A gap of one has no midpoint to insert into: inputs are integers and must stay strictly
        // ascending, so there is no room between them.
        if (at < 0 || widest < 2)
        {
            LogEditRefused("add", $"the widest input gap is {widest}, leaving no integer midpoint");
            return;
        }

        int input = points[at - 1].Input + (widest / 2);
        TryCommit(
            CurveEditing.Add(points, input, CurveEditing.Evaluate(points, input), EditBounds),
            "add");
    }

    /// <summary>Removes the selected point.</summary>
    internal void RemoveSelectedPoint()
    {
        IReadOnlyList<CurvePoint> updated = CurveEditing.Remove(Points, SelectedIndex);
        if (ReferenceEquals(updated, Points))
        {
            LogEditRefused(
                "remove",
                $"selection {SelectedIndex} is absent, an endpoint, or part of the two-point minimum");
            return;
        }

        SelectedIndex = Math.Min(SelectedIndex, updated.Count - 1);
        TryCommit(updated, "remove");
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect plot = PlotRect();
        CurveBounds bounds = EditBounds;
        if (plot.Width <= 0 || plot.Height <= 0 || !bounds.IsUsable)
        {
            return;
        }

        IBrush grid = Resolve("HcControlBorderBrush", Brushes.DimGray);
        IBrush accent = Resolve("HcAccentBrush", Brushes.Orange);
        IBrush surface = Resolve("HcControlBrush", Brushes.Black);
        IBrush handleFill = Resolve("HcBackgroundBrush", Brushes.Black);

        context.FillRectangle(surface, plot);

        // Quarters, not a dense grid: this is read at arm's length on a handheld, and the lines are
        // there to judge a curve's shape against, not to measure it.
        Pen gridPen = new(grid, 1);
        for (int step = 1; step < 4; step++)
        {
            double x = plot.X + (plot.Width * step / 4);
            double y = plot.Y + (plot.Height * step / 4);
            context.DrawLine(gridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
            context.DrawLine(gridPen, new Point(plot.X, y), new Point(plot.Right, y));
        }

        context.DrawRectangle(new Pen(grid, 1), plot);

        IReadOnlyList<CurvePoint> points = Points;
        if (points.Count == 0)
        {
            return;
        }

        Pen curvePen = new(accent, 2);
        for (int index = 1; index < points.Count; index++)
        {
            context.DrawLine(
                curvePen,
                ToScreen(points[index - 1], plot, bounds),
                ToScreen(points[index], plot, bounds));
        }

        for (int index = 0; index < points.Count; index++)
        {
            Point centre = ToScreen(points[index], plot, bounds);
            bool selected = index == SelectedIndex;
            context.DrawEllipse(
                selected ? accent : handleFill,
                new Pen(accent, selected ? 3 : 2),
                centre,
                HandleRadius,
                HandleRadius);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        Point position = e.GetPosition(this);
        int hit = HitTest(position);
        if (hit >= 0)
        {
            SelectedIndex = hit;
            _dragIndex = hit;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // A press on empty space adds a point there and immediately begins dragging it, so placing
        // a point and positioning it are one gesture rather than two.
        if (!TryToCurve(position, out int input, out int output))
        {
            LogEditRefused(
                "pointer-add",
                $"position {position} is outside the usable plot or the device bounds are invalid");
            return;
        }

        IReadOnlyList<CurvePoint> updated = CurveEditing.Add(Points, input, output, EditBounds);
        if (ReferenceEquals(updated, Points))
        {
            LogEditRefused(
                "pointer-add",
                $"the {CurveEditing.MaximumPoints}-point limit is already reached");
            return;
        }

        if (!TryCommit(updated, "pointer-add"))
        {
            return;
        }
        int added = IndexOfInput(updated, EditBounds.ClampInput(input));
        SelectedIndex = added;
        _dragIndex = added;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragIndex < 0 || !TryToCurve(e.GetPosition(this), out int input, out int output))
        {
            return;
        }

        IReadOnlyList<CurvePoint> updated = CurveEditing.Move(
            Points,
            _dragIndex,
            input,
            output,
            EditBounds);
        TryCommit(updated, "pointer-move");
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragIndex < 0)
        {
            return;
        }

        _dragIndex = -1;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Left and right move the selection between points, up and down change its output, and
    /// Shift+left/right changes an interior point's input. Gamepad navigation mirrors the
    /// unmodified arrows so either of the controller's input paths produces the same action.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Delete:
                RemoveSelectedPoint();
                e.Handled = true;
                return;
            case Key.Insert:
                AddPointAtWidestGap();
                e.Handled = true;
                return;
        }

        if (ApplyDirectionalKey(
            e.Key,
            e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            "keyboard"))
        {
            e.Handled = true;
        }
    }

    /// <summary>Applies one gamepad direction to the focused editor.</summary>
    /// <param name="direction">The physical direction reported by controller navigation.</param>
    /// <remarks>
    /// This mirrors the unmodified keyboard arrows: left and right choose a point, while up and
    /// down change its output. Steam's desktop-layout arrow and SDL's copy of the same press must
    /// produce identical edits so the cross-source suppression in <c>GamepadNavigation</c> cannot
    /// make the result depend on which event arrived first.
    /// </remarks>
    internal void ApplyDirection(NavigationDirection direction)
    {
        Key key = direction switch
        {
            NavigationDirection.Left => Key.Left,
            NavigationDirection.Right => Key.Right,
            NavigationDirection.Up => Key.Up,
            NavigationDirection.Down => Key.Down,
            _ => Key.None,
        };
        if (key == Key.None)
        {
            LogEditRefused("gamepad", $"direction {direction} has no curve-edit action");
            return;
        }

        ApplyDirectionalKey(key, moving: false, "gamepad");
    }

    private bool ApplyDirectionalKey(Key key, bool moving, string inputSource)
    {
        if (key is not (Key.Left or Key.Right or Key.Up or Key.Down))
        {
            return false;
        }

        IReadOnlyList<CurvePoint> points = Points;
        if (points.Count == 0)
        {
            LogEditRefused(inputSource, "the curve has no points");
            return true;
        }

        if (!moving && key is Key.Left or Key.Right)
        {
            int nextIndex = key == Key.Left
                ? Math.Max(0, SelectedIndex - 1)
                : SelectedIndex < 0
                    ? 0
                    : Math.Min(points.Count - 1, SelectedIndex + 1);
            if (nextIndex == SelectedIndex)
            {
                LogEditRefused(
                    $"{inputSource}-select",
                    $"selection {SelectedIndex} is already at the {key.ToString().ToLowerInvariant()} edge");
                return true;
            }

            SelectedIndex = nextIndex;
            return true;
        }

        if (SelectedIndex < 0 || SelectedIndex >= points.Count)
        {
            LogEditRefused(
                $"{inputSource}-move",
                $"selection {SelectedIndex} is outside the curve");
            return true;
        }

        (int inputStep, int outputStep) = key switch
        {
            Key.Left when moving => (-1, 0),
            Key.Right when moving => (1, 0),
            Key.Up => (0, 1),
            Key.Down => (0, -1),
            _ => (0, 0),
        };
        if (inputStep == 0 && outputStep == 0)
        {
            return true;
        }

        CurvePoint point = points[SelectedIndex];
        TryCommit(
            CurveEditing.Move(
                points,
                SelectedIndex,
                point.Input + inputStep,
                point.Output + outputStep,
                EditBounds),
            $"{inputSource}-move");
        return true;
    }

    private bool TryCommit(IReadOnlyList<CurvePoint> updated, string operation)
    {
        if (ReferenceEquals(updated, Points) || updated.SequenceEqual(Points))
        {
            LogEditRefused(operation, "the requested point is already at its permitted bound");
            return false;
        }

        Points = updated;
        InvalidateVisual();
        CurveChanged?.Invoke(updated);
        return true;
    }

    private void LogEditRefused(string operation, string reason) =>
        Log.Change(
            "curve-editor.refusal",
            $"Curve edit refused: operation={operation}, reason={reason}, "
                + $"points={Points.Count}, selected={SelectedIndex}.");

    private static int IndexOfInput(IReadOnlyList<CurvePoint> points, int input)
    {
        for (int index = 0; index < points.Count; index++)
        {
            if (points[index].Input == input)
            {
                return index;
            }
        }

        return -1;
    }

    private int HitTest(Point position)
    {
        Rect plot = PlotRect();
        CurveBounds bounds = EditBounds;
        if (!bounds.IsUsable)
        {
            return -1;
        }

        IReadOnlyList<CurvePoint> points = Points;
        int closest = -1;
        double closestDistance = GrabRadius;
        for (int index = 0; index < points.Count; index++)
        {
            Point centre = ToScreen(points[index], plot, bounds);
            double distance = Math.Sqrt(
                ((centre.X - position.X) * (centre.X - position.X))
                + ((centre.Y - position.Y) * (centre.Y - position.Y)));
            if (distance <= closestDistance)
            {
                closestDistance = distance;
                closest = index;
            }
        }

        return closest;
    }

    private bool TryToCurve(Point position, out int input, out int output)
    {
        input = 0;
        output = 0;
        Rect plot = PlotRect();
        CurveBounds bounds = EditBounds;
        if (plot.Width <= 0 || plot.Height <= 0 || !bounds.IsUsable)
        {
            return false;
        }

        double inputSpan = bounds.InputMaximum - bounds.InputMinimum;
        double outputSpan = bounds.OutputMaximum - bounds.OutputMinimum;
        input = bounds.ClampInput(bounds.InputMinimum
            + (int)Math.Round((position.X - plot.X) / plot.Width * inputSpan));

        // Screen Y grows downward and an output grows upward, so this inverts. Getting it wrong
        // gives an editor that works and draws every curve upside down.
        output = bounds.ClampOutput(bounds.OutputMinimum
            + (int)Math.Round((plot.Bottom - position.Y) / plot.Height * outputSpan));
        return true;
    }

    private static Point ToScreen(CurvePoint point, Rect plot, CurveBounds bounds)
    {
        double inputSpan = bounds.InputMaximum - bounds.InputMinimum;
        double outputSpan = bounds.OutputMaximum - bounds.OutputMinimum;
        double x = plot.X + ((point.Input - bounds.InputMinimum) / inputSpan * plot.Width);
        double y = plot.Bottom - ((point.Output - bounds.OutputMinimum) / outputSpan * plot.Height);
        return new Point(x, y);
    }

    /// <remarks>
    /// Guarded rather than deflated blindly: a control laid out smaller than its own inset would
    /// otherwise produce an inverted rect, and every screen-space conversion built on it would
    /// place points outside the control.
    /// </remarks>
    private Rect PlotRect()
    {
        Size size = Bounds.Size;
        return size.Width <= Inset * 2 || size.Height <= Inset * 2
            ? default
            : new Rect(size).Deflate(Inset);
    }

    private IBrush Resolve(string key, IBrush fallback) =>
        this.TryFindResource(key, out object? value) && value is IBrush brush ? brush : fallback;
}
