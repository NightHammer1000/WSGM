using System;
using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Controls;

/// <summary>The bounds a curve is edited within. The editor authors every curve
/// against the 0..100 percent-over-percent plane the device contract defines.</summary>
/// <param name="InputMinimum">Lowest input value, for a fan curve a temperature in °C.</param>
/// <param name="InputMaximum">Highest input value.</param>
/// <param name="OutputMinimum">Lowest output value, for a fan curve a duty percentage.</param>
/// <param name="OutputMaximum">Highest output value.</param>
internal readonly record struct CurveBounds(
    int InputMinimum,
    int InputMaximum,
    int OutputMinimum,
    int OutputMaximum)
{
    /// <summary>Whether the bounds describe a usable editing surface.</summary>
    internal bool IsUsable => InputMaximum > InputMinimum && OutputMaximum > OutputMinimum;

    /// <summary>Clamps an input to the bounds.</summary>
    /// <param name="value">The candidate input.</param>
    /// <returns>The value, held inside the bounds.</returns>
    internal int ClampInput(int value) => Math.Clamp(value, InputMinimum, InputMaximum);

    /// <summary>Clamps an output to the bounds.</summary>
    /// <param name="value">The candidate output.</param>
    /// <returns>The value, held inside the bounds.</returns>
    internal int ClampOutput(int value) => Math.Clamp(value, OutputMinimum, OutputMaximum);
}

/// <summary>
/// The editing operations behind the curve editor, kept pure so they can be tested without a UI.
/// </summary>
/// <remarks>
/// Every operation returns a curve that satisfies the same contract the device router validates
/// against: between 1 and 64 points, inputs strictly ascending, everything inside the bounds. The
/// editor can therefore never build a curve that is refused on apply, which is the failure this
/// separation exists to prevent — a drag that produces an invalid curve has to be impossible, not
/// merely reported.
/// </remarks>
internal static class CurveEditing
{
    /// <summary>The most points a curve may carry, matching the device router's own limit.</summary>
    internal const int MaximumPoints = 64;

    /// <summary>How close two inputs may be before a move is refused, in input units.</summary>
    /// <remarks>
    /// One, because inputs must be strictly ascending and are integers. A drag that would collide
    /// stops against its neighbour rather than reordering the curve underneath the user's finger.
    /// </remarks>
    private const int MinimumInputGap = 1;

    /// <summary>Moves one point, without letting it pass its neighbours.</summary>
    /// <param name="points">The current curve.</param>
    /// <param name="index">Index of the point being dragged.</param>
    /// <param name="input">Requested input.</param>
    /// <param name="output">Requested output.</param>
    /// <param name="bounds">The editing bounds.</param>
    /// <returns>The curve with the point moved, or the original if the index is out of range.</returns>
    /// <remarks>
    /// The moved point is held between its neighbours rather than being allowed to swap with them.
    /// Reordering mid-drag would make the point under the finger a different point, which reads as
    /// the curve snapping away — and the endpoints keep their inputs, because a fan curve that no
    /// longer spans its whole temperature range has an undefined answer at the ends.
    /// </remarks>
    internal static IReadOnlyList<CurvePoint> Move(
        IReadOnlyList<CurvePoint> points,
        int index,
        int input,
        int output,
        CurveBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (index < 0 || index >= points.Count)
        {
            return points;
        }

        bool firstPoint = index == 0;
        bool lastPoint = index == points.Count - 1;
        int lowerLimit = firstPoint
            ? bounds.InputMinimum
            : points[index - 1].Input + MinimumInputGap;
        int upperLimit = lastPoint
            ? bounds.InputMaximum
            : points[index + 1].Input - MinimumInputGap;

        int resolvedInput = firstPoint
            ? bounds.InputMinimum
            : lastPoint
                ? bounds.InputMaximum
                : Math.Clamp(bounds.ClampInput(input), lowerLimit, upperLimit);

        List<CurvePoint> moved = [.. points];
        moved[index] = new CurvePoint(resolvedInput, bounds.ClampOutput(output));
        return moved;
    }

    /// <summary>Inserts a point at an input, or moves the existing one there.</summary>
    /// <param name="points">The current curve.</param>
    /// <param name="input">Where to add the point.</param>
    /// <param name="output">The output at that input.</param>
    /// <param name="bounds">The editing bounds.</param>
    /// <returns>The curve with the point present.</returns>
    /// <remarks>
    /// Adding at an input a point already occupies moves that point instead of creating a duplicate,
    /// because duplicate inputs are exactly what the device contract forbids and a double-tap on an
    /// existing point should not be able to break the curve.
    /// </remarks>
    internal static IReadOnlyList<CurvePoint> Add(
        IReadOnlyList<CurvePoint> points,
        int input,
        int output,
        CurveBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(points);
        int clampedInput = bounds.ClampInput(input);
        int clampedOutput = bounds.ClampOutput(output);

        for (int index = 0; index < points.Count; index++)
        {
            if (points[index].Input == clampedInput)
            {
                List<CurvePoint> replaced = [.. points];
                replaced[index] = new CurvePoint(clampedInput, clampedOutput);
                return replaced;
            }
        }

        if (points.Count >= MaximumPoints)
        {
            // Refused rather than silently dropping someone else's point to make room.
            return points;
        }

        List<CurvePoint> added = [.. points, new CurvePoint(clampedInput, clampedOutput)];
        added.Sort(static (left, right) => left.Input.CompareTo(right.Input));
        return added;
    }

    /// <summary>Removes a point, keeping the curve valid.</summary>
    /// <param name="points">The current curve.</param>
    /// <param name="index">Index of the point to remove.</param>
    /// <returns>The curve without that point, or unchanged when it cannot be removed.</returns>
    /// <remarks>
    /// The two endpoints stay. They define the curve's answer at the ends of the device's range, and
    /// removing one leaves the value there undefined; a curve of two points is the floor.
    /// </remarks>
    internal static IReadOnlyList<CurvePoint> Remove(IReadOnlyList<CurvePoint> points, int index)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (index <= 0 || index >= points.Count - 1 || points.Count <= 2)
        {
            return points;
        }

        List<CurvePoint> removed = [.. points];
        removed.RemoveAt(index);
        return removed;
    }

    /// <summary>Reads the curve's output at an input, interpolating between points.</summary>
    /// <param name="points">The curve.</param>
    /// <param name="input">The input to evaluate at.</param>
    /// <returns>The interpolated output, or zero for an empty curve.</returns>
    /// <remarks>
    /// Linear, and clamped flat outside the curve's own range. This is the editor's own preview, not
    /// the device's interpolation — a plugin is free to interpolate differently, and where that
    /// matters the readout must come from the device rather than from here.
    /// </remarks>
    internal static int Evaluate(IReadOnlyList<CurvePoint> points, int input)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return 0;
        }

        if (input <= points[0].Input)
        {
            return points[0].Output;
        }

        if (input >= points[^1].Input)
        {
            return points[^1].Output;
        }

        for (int index = 1; index < points.Count; index++)
        {
            CurvePoint upper = points[index];
            if (input > upper.Input)
            {
                continue;
            }

            CurvePoint lower = points[index - 1];
            int span = upper.Input - lower.Input;
            if (span <= 0)
            {
                return upper.Output;
            }

            // Rounded, not truncated: a duty cycle that reads one below the point the user placed
            // looks like the editor lost the edit.
            return lower.Output
                + (int)Math.Round((double)(upper.Output - lower.Output) * (input - lower.Input) / span);
        }

        return points[^1].Output;
    }
}
