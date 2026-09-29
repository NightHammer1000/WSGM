using System.Collections.Generic;
using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Input;

namespace WSGM.Overlay;

/// <summary>
/// Maps a canonical physical sample onto the glyph controls the preview draws.
/// </summary>
/// <remarks>
/// The one place the two vocabularies meet. <see cref="CanonicalButtons"/> is what a plugin reports
/// pressing; <see cref="GlyphControlId"/> is what a glyph profile draws. They are deliberately
/// separate — a device can report a control it has no artwork for, and a profile can carry artwork
/// for a control the plugin never reports — so the map is explicit rather than derived from names.
/// <para>
/// That separation is exactly what the input test exists to check. A control that lights up when the
/// wrong button is pressed is a mapping defect in the plugin, and this is where it becomes visible.
/// </para>
/// </remarks>
internal static class GlyphInputTestMap
{
    /// <summary>Buttons paired with the glyph they light, in canonical order.</summary>
    private static readonly (CanonicalButtons Button, GlyphControlId Control)[] Buttons =
    [
        (CanonicalButtons.A, GlyphControlId.FaceSouth),
        (CanonicalButtons.B, GlyphControlId.FaceEast),
        (CanonicalButtons.X, GlyphControlId.FaceWest),
        (CanonicalButtons.Y, GlyphControlId.FaceNorth),
        (CanonicalButtons.DPadUp, GlyphControlId.DpadUp),
        (CanonicalButtons.DPadDown, GlyphControlId.DpadDown),
        (CanonicalButtons.DPadLeft, GlyphControlId.DpadLeft),
        (CanonicalButtons.DPadRight, GlyphControlId.DpadRight),
        (CanonicalButtons.LeftShoulder, GlyphControlId.LeftShoulder),
        (CanonicalButtons.RightShoulder, GlyphControlId.RightShoulder),
        (CanonicalButtons.LeftStick, GlyphControlId.LeftStick),
        (CanonicalButtons.RightStick, GlyphControlId.RightStick),
        (CanonicalButtons.LeftStickTouch, GlyphControlId.LeftStickTouch),
        (CanonicalButtons.RightStickTouch, GlyphControlId.RightStickTouch),
        (CanonicalButtons.Guide, GlyphControlId.Guide),
        (CanonicalButtons.View, GlyphControlId.View),
        (CanonicalButtons.Menu, GlyphControlId.Menu),
        (CanonicalButtons.QuickAccess, GlyphControlId.QuickAccess),

        // Rear paddles 1 and 2 are the upper pair the Claw prints M1 and M2; 3 and 4 are the second
        // pair a Steam Deck has and the Claw does not. A profile that declares the second pair
        // absent simply has no tile for them, which is the case the preview is meant to show.
        (CanonicalButtons.RearPaddle1, GlyphControlId.RearM1),
        (CanonicalButtons.RearPaddle2, GlyphControlId.RearM2),
        (CanonicalButtons.RearPaddle3, GlyphControlId.RearLeft2),
        (CanonicalButtons.RearPaddle4, GlyphControlId.RearRight2),

        // A trackpad lights on either touch or click: the tile stands for the pad, and a user
        // checking it wants to see it react to being touched, not only to being pressed through.
        (CanonicalButtons.LeftPadClick, GlyphControlId.LeftTrackpad),
        (CanonicalButtons.RightPadClick, GlyphControlId.RightTrackpad),
        (CanonicalButtons.LeftPadTouch, GlyphControlId.LeftTrackpad),
        (CanonicalButtons.RightPadTouch, GlyphControlId.RightTrackpad),
    ];

    /// <summary>How far a trigger must travel before it counts as pressed.</summary>
    /// <remarks>
    /// Triggers are analogue and rest slightly off zero on real hardware, so a bare non-zero test
    /// would light them permanently and make the test useless for everything beside them.
    /// </remarks>
    private const float TriggerThreshold = 0.2f;

    /// <summary>The glyph controls a sample is currently pressing.</summary>
    /// <param name="sample">The physical sample.</param>
    /// <returns>The set of lit controls.</returns>
    internal static HashSet<GlyphControlId> Pressed(CanonicalControllerSample sample)
    {
        HashSet<GlyphControlId> pressed = [];
        foreach ((CanonicalButtons button, GlyphControlId control) in Buttons)
        {
            if ((sample.Buttons & button) != 0)
            {
                pressed.Add(control);
            }
        }

        if (sample.LeftTrigger > TriggerThreshold)
        {
            pressed.Add(GlyphControlId.LeftTrigger);
        }

        if (sample.RightTrigger > TriggerThreshold)
        {
            pressed.Add(GlyphControlId.RightTrigger);
        }

        return pressed;
    }
}
