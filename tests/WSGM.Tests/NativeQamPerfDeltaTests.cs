using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class NativeQamPerfDeltaTests
{
    private static JsonElement Payload(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void APerAppFrameLimitChangeIsRecognized()
    {
        bool read = NativeQamPerfDeltaReader.TryRead(
            Payload("""{"delta":{"gameid":42,"settings_delta":{"per_app":{"fps_limit":60}}}}"""),
            out NativeQamPerfDelta delta,
            out string? error);

        Assert.True(read);
        Assert.Null(error);
        Assert.Equal((uint)42, delta.SteamAppId);
        Assert.Equal(
            new NativeQamPerfChange(NativeQamPerfSetting.FrameLimit, 60),
            Assert.Single(delta.Recognized));
    }

    [Fact]
    public void NullFieldsAreNotChanges()
    {
        // toObject() emits the whole message, not only what the setter touched. Treating an unset
        // field as a change would make one slider write every other control's value back on every
        // drag.
        NativeQamPerfDeltaReader.TryRead(
            Payload(
                """
                {"delta":{"settings_delta":{"per_app":{"fps_limit":60,"is_vrr_enabled":null,
                "display_refresh_manual_hz":null}}}}
                """),
            out NativeQamPerfDelta delta,
            out _);

        Assert.Equal(
            new NativeQamPerfChange(NativeQamPerfSetting.FrameLimit, 60),
            Assert.Single(delta.Recognized));
    }

    [Fact]
    public void BooleansBecomeFlags()
    {
        NativeQamPerfDeltaReader.TryRead(
            Payload(
                """
                {"delta":{"settings_delta":{"per_app":{"is_vrr_enabled":true,
                "is_fps_limit_enabled":false}}}}
                """),
            out NativeQamPerfDelta delta,
            out _);

        Assert.Contains(
            delta.Recognized,
            change => change.Kind is NativeQamPerfSetting.VariableRefreshRate && change.AsFlag);
        Assert.Contains(
            delta.Recognized,
            change => change.Kind is NativeQamPerfSetting.FrameLimitEnabled && !change.AsFlag);
    }

    [Fact]
    public void GlobalAndPerAppSettingsBothArrive()
    {
        NativeQamPerfDeltaReader.TryRead(
            Payload(
                """
                {"delta":{"settings_delta":{"global":{"perf_overlay_level":3},
                "per_app":{"fps_limit":30}}}}
                """),
            out NativeQamPerfDelta delta,
            out _);

        Assert.Equal(2, delta.Recognized.Count);
    }

    [Fact]
    public void AnUnbackedFieldIsReportedRatherThanSilentlyDropped()
    {
        // A control that appears to work and does nothing is worse than one that is not there, so
        // an unsupported field has to reach the log.
        NativeQamPerfDeltaReader.TryRead(
            Payload("""{"delta":{"settings_delta":{"per_app":{"cpu_governor":2}}}}"""),
            out NativeQamPerfDelta delta,
            out _);

        Assert.Empty(delta.Recognized);
        Assert.Equal("cpu_governor", Assert.Single(delta.Unsupported));
    }

    [Fact]
    public void ResetToDefaultIsCarried()
    {
        NativeQamPerfDeltaReader.TryRead(
            Payload("""{"delta":{"reset_to_default":true}}"""),
            out NativeQamPerfDelta delta,
            out _);

        Assert.True(delta.ResetToDefault);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"0\"")]
    [InlineData("769")]
    [InlineData("\"769\"")]
    [InlineData("\"18374686479671623680\"")]
    public void AGameIdThatIsNotAnAppIdTargetsTheGlobalProfile(string gameId)
    {
        // gameid is 64-bit and the client emits it as a number or a string by magnitude. A full
        // game id is not an AppID and must not be truncated into one, and 769 — the Steam client's
        // own pseudo-app — is how every store setter addresses the global profile.
        NativeQamPerfDeltaReader.TryRead(
            Payload($$$"""{"delta":{"gameid":{{{gameId}}}}}"""),
            out NativeQamPerfDelta delta,
            out _);

        Assert.Null(delta.SteamAppId);
    }

    [Fact]
    public void AGameIdSentAsAStringStillResolves()
    {
        NativeQamPerfDeltaReader.TryRead(
            Payload("""{"delta":{"gameid":"570"}}"""),
            out NativeQamPerfDelta delta,
            out _);

        Assert.Equal((uint)570, delta.SteamAppId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"delta":7}""")]
    [InlineData("[]")]
    public void APayloadWithoutADeltaObjectIsRefusedWithAReason(string json)
    {
        bool read = NativeQamPerfDeltaReader.TryRead(
            Payload(json),
            out _,
            out string? error);

        Assert.False(read);
        Assert.NotNull(error);
    }
}
