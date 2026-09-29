using WSGM.Core;

namespace WSGM.Tests;

// The exit event's name and its DACL are a cross-version contract: during an
// upgrade the object is created by the OLD build and the new installer only opens
// it by name (installer\WSGM.iss StopRunningInstances, OpenEventW with
// EVENT_MODIFY_STATE). These tests pin the descriptor text without touching the
// real token, any kernel object, or %LOCALAPPDATA%.
public sealed class UpdateExitWatcherTests
{
    private const string UserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string MediumLabel = "S:(ML;;NW;;;ME)";

    [Fact]
    public void BuildEventSddl_UserSid_GrantsThatSidAndAdministratorsModifyState()
        => Assert.Equal(
            "D:(A;;0x00100002;;;S-1-5-21-1111111111-2222222222-3333333333-1001)"
                + "(A;;0x00100002;;;BA)S:(ML;;NW;;;ME)",
            UpdateExitWatcher.BuildEventSddl(UserSid));

    [Fact]
    public void BuildEventSddl_NullUserSid_FallsBackToTheEveryoneGrant()
        => Assert.Equal(
            "D:(A;;0x00100002;;;WD)(A;;0x00100002;;;BA)S:(ML;;NW;;;ME)",
            UpdateExitWatcher.BuildEventSddl(null));

    [Fact]
    public void BuildEventSddl_EitherUserSid_KeepsTheMediumNoWriteUpLabel()
    {
        Assert.EndsWith(MediumLabel, UpdateExitWatcher.BuildEventSddl(UserSid));
        Assert.EndsWith(MediumLabel, UpdateExitWatcher.BuildEventSddl(null));
    }

    [Fact]
    public void BuildEventSddl_EitherUserSid_GrantsExactlyTwoAcesWithTheInstallerMask()
    {
        // 0x00100002 is EVENT_MODIFY_STATE | SYNCHRONIZE — what the installer opens
        // the event with, and what the unelevated settings instance needs for its
        // stale-signal ResetEvent. Two ACEs, no third, no other mask.
        string[] variants =
        [
            UpdateExitWatcher.BuildEventSddl(UserSid),
            UpdateExitWatcher.BuildEventSddl(null),
        ];
        foreach (var sddl in variants)
        {
            Assert.Equal(2, sddl.Split("(A;;").Length - 1);
            Assert.Equal(2, sddl.Split("0x00100002").Length - 1);
        }
    }

    [Fact]
    public void EventName_Always_MatchesTheNameTheInstallerOpens()
        => Assert.Equal(@"Local\WSGM.ExitForUpdate", UpdateExitWatcher.EventName);

    [Fact]
    public void UninstallEventName_Always_MatchesTheNameTheInstallerOpens()
        => Assert.Equal(@"Local\WSGM.ExitForUninstall", UpdateExitWatcher.UninstallEventName);

    [Fact]
    public void HandoffEventNameFor_Update_UsesOneCompletionChannel()
        => Assert.Equal(
            @"Local\WSGM.ExitForUpdate.Completed",
            UpdateExitWatcher.HandoffEventNameFor(ApplicationShutdownReason.Update));

    [Fact]
    public void HandoffEventNameFor_Uninstall_UsesOneSeparateCompletionChannel()
        => Assert.Equal(
            @"Local\WSGM.ExitForUninstall.Completed",
            UpdateExitWatcher.HandoffEventNameFor(ApplicationShutdownReason.Uninstall));

    [Fact]
    public void HandoffEventNameFor_NonInstallerExit_HasNoCrossProcessChannel()
    {
        Assert.Null(UpdateExitWatcher.HandoffEventNameFor(ApplicationShutdownReason.Normal));
        Assert.Null(UpdateExitWatcher.HandoffEventNameFor(ApplicationShutdownReason.SessionEnd));
    }
}
