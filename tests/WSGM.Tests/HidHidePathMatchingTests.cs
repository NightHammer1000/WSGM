using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>
/// WSGM has to recognise its own HidHide entries in the notation HidHide stores them in.
/// </summary>
/// <remarks>
/// HidHide keeps application entries as NT device paths — <c>\Device\HarddiskVolume3\…</c> — while
/// WSGM knows its executables by drive letter. A plain string compare therefore never matched, so
/// WSGM added a second entry for a path that was already present: the allowlist grew on every
/// activation, and cleanup, which matches what it wrote, would leave the other notation behind.
/// Device-observed on the reference Claw, 2026-08-29.
/// </remarks>
public sealed class HidHidePathMatchingTests
{
    private const string DosPath = @"C:\Program Files\WSGM\WSGM.exe";
    private const string DevicePath =
        @"\Device\HarddiskVolume3\Program Files\WSGM\WSGM.exe";

    [Fact]
    public void AnEntryStoredAsADevicePathIsRecognisedFromItsDriveLetterForm()
    {
        // The exact case that produced the duplicate.
        Assert.True(HidHideOwnedDeltaManager.Contains([DevicePath], DosPath));
    }

    [Fact]
    public void AndTheOtherWayRound()
    {
        Assert.True(HidHideOwnedDeltaManager.Contains([DosPath], DevicePath));
    }

    [Fact]
    public void AnExactMatchStillMatches()
    {
        Assert.True(HidHideOwnedDeltaManager.Contains([DosPath], DosPath));
        Assert.True(HidHideOwnedDeltaManager.Contains([DevicePath], DevicePath));
    }

    [Fact]
    public void TheVolumeNumberIsNotWhatIdentifiesTheFile()
    {
        // Volume numbering is assigned by Windows and is not stable across machines or boots, so it
        // must not be part of the comparison.
        Assert.True(HidHideOwnedDeltaManager.Contains(
            [@"\Device\HarddiskVolume7\Program Files\WSGM\WSGM.exe"],
            DosPath));
    }

    [Fact]
    public void ADifferentProgramIsNotMatched()
    {
        Assert.False(HidHideOwnedDeltaManager.Contains(
            [@"\Device\HarddiskVolume3\Program Files\Handheld Companion\HandheldCompanion.exe"],
            DosPath));
    }

    [Fact]
    public void ADifferentPathToASameNamedProgramIsNotMatched()
    {
        // Only the volume prefix is ignored. Everything that identifies the file still has to agree.
        Assert.False(HidHideOwnedDeltaManager.Contains(
            [@"C:\Other\WSGM\WSGM.exe"],
            DosPath));
    }

    [Fact]
    public void DeviceInstancePathsAreLeftAlone()
    {
        // The device list never had this problem: instance paths carry no volume prefix, so they
        // must pass through untouched and keep comparing exactly.
        const string instance = @"HID\VID_0DB0&PID_1902&MI_00&COL01\7&3222ED46&0&0000";

        Assert.Equal(instance, HidHideOwnedDeltaManager.NormalizePath(instance));
        Assert.True(HidHideOwnedDeltaManager.Contains([instance], instance));
        Assert.False(HidHideOwnedDeltaManager.Contains(
            [@"HID\VID_0DB0&PID_1901&IG_00\8&1717EFAA&0&0000"],
            instance));
    }

    [Fact]
    public void AUncPathKeepsItsServerAndShare()
    {
        // There is no volume to strip, and the server and share are part of what identifies it.
        const string unc = @"\\build\tools\WSGM.exe";

        Assert.Equal(unc, HidHideOwnedDeltaManager.NormalizePath(unc));
        Assert.False(HidHideOwnedDeltaManager.Contains([unc], DosPath));
    }

    [Fact]
    public void EmptyEntriesMatchNothing()
    {
        Assert.False(HidHideOwnedDeltaManager.Contains([], DosPath));
        Assert.Equal(string.Empty, HidHideOwnedDeltaManager.NormalizePath("   "));
    }
}
