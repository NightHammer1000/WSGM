using WSGM.Device.Sdk.Glyphs;
using WSGM.Device.Sdk.Input;
using WSGM.Overlay;

namespace WSGM.Tests;

/// <summary>
/// The one place the canonical button vocabulary meets the glyph one.
/// </summary>
/// <remarks>
/// These are separate vocabularies on purpose: a device can report a control it has no artwork for,
/// and a profile can carry artwork for a control the plugin never reports. Getting the map wrong is
/// exactly the defect the input test exists to reveal, so the map itself needs pinning.
/// </remarks>
public sealed class GlyphInputTestMapTests
{
    [Fact]
    public void NothingHeldLightsNothing()
    {
        Assert.Empty(GlyphInputTestMap.Pressed(Sample(CanonicalButtons.None)));
    }

    [Theory]
    [InlineData(CanonicalButtons.A, GlyphControlId.FaceSouth)]
    [InlineData(CanonicalButtons.B, GlyphControlId.FaceEast)]
    [InlineData(CanonicalButtons.X, GlyphControlId.FaceWest)]
    [InlineData(CanonicalButtons.Y, GlyphControlId.FaceNorth)]
    public void FaceButtonsLightTheirPositionRatherThanTheirLetter(
        CanonicalButtons button,
        GlyphControlId expected)
    {
        // The canonical names are Xbox letters and the glyph ids are positions, because a profile
        // draws whatever letter the hardware prints there. Confusing the two would light the wrong
        // glyph on any device that is not laid out like an Xbox pad.
        Assert.Equal([expected], GlyphInputTestMap.Pressed(Sample(button)));
    }

    [Theory]
    [InlineData(CanonicalButtons.RearPaddle1, GlyphControlId.RearM1)]
    [InlineData(CanonicalButtons.RearPaddle2, GlyphControlId.RearM2)]
    [InlineData(CanonicalButtons.RearPaddle3, GlyphControlId.RearLeft2)]
    [InlineData(CanonicalButtons.RearPaddle4, GlyphControlId.RearRight2)]
    public void AllFourRearControlsAreDistinct(CanonicalButtons button, GlyphControlId expected)
    {
        // The whole reason the canonical model defines four: a Steam Deck has two pairs, the Claw
        // has one, and a profile that declares the second pair absent simply has no tile for it.
        Assert.Equal([expected], GlyphInputTestMap.Pressed(Sample(button)));
    }

    [Theory]
    [InlineData(CanonicalButtons.LeftPadTouch)]
    [InlineData(CanonicalButtons.LeftPadClick)]
    public void ATrackpadLightsOnTouchAsWellAsOnClick(CanonicalButtons button)
    {
        // The tile stands for the pad. Someone checking it wants to see it react to being touched,
        // not only to being pressed through.
        Assert.Equal([GlyphControlId.LeftTrackpad], GlyphInputTestMap.Pressed(Sample(button)));
    }

    [Fact]
    public void StickTouchAndStickClickAreDifferentGlyphs()
    {
        Assert.Equal(
            [GlyphControlId.LeftStick],
            GlyphInputTestMap.Pressed(Sample(CanonicalButtons.LeftStick)));
        Assert.Equal(
            [GlyphControlId.LeftStickTouch],
            GlyphInputTestMap.Pressed(Sample(CanonicalButtons.LeftStickTouch)));
    }

    [Fact]
    public void ARestingTriggerDoesNotCountAsHeld()
    {
        // Triggers are analogue and sit slightly off zero on real hardware. A bare non-zero test
        // would light them permanently, which makes the test useless for everything beside them.
        Assert.Empty(GlyphInputTestMap.Pressed(Sample(CanonicalButtons.None, leftTrigger: 0.05f)));
    }

    [Fact]
    public void APulledTriggerLightsItsGlyph()
    {
        Assert.Equal(
            [GlyphControlId.RightTrigger],
            GlyphInputTestMap.Pressed(Sample(CanonicalButtons.None, rightTrigger: 0.9f)));
    }

    [Fact]
    public void HoldingSeveralControlsLightsAllOfThem()
    {
        HashSet<GlyphControlId> pressed = GlyphInputTestMap.Pressed(Sample(
            CanonicalButtons.A | CanonicalButtons.RightShoulder | CanonicalButtons.QuickAccess,
            leftTrigger: 1f));

        Assert.Equal(
            new HashSet<GlyphControlId>
            {
                GlyphControlId.FaceSouth,
                GlyphControlId.RightShoulder,
                GlyphControlId.QuickAccess,
                GlyphControlId.LeftTrigger,
            },
            pressed);
    }

    private static CanonicalControllerSample Sample(
        CanonicalButtons buttons,
        float leftTrigger = 0,
        float rightTrigger = 0) =>
        new()
        {
            Sequence = 1,
            CycleGeneration = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            Buttons = buttons,
            LeftTrigger = leftTrigger,
            RightTrigger = rightTrigger,
        };
}
