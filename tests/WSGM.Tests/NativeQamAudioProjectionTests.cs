using WSGM.Shell;

namespace WSGM.Tests;

public sealed class NativeQamAudioProjectionTests
{
    [Fact]
    public void Project_NoEndpoints_ReportsUnavailableWithAReason()
    {
        NativeQamAudioState state = AudioManagerNativeQamAudioService.Project(Manager());

        Assert.False(state.Available);
        Assert.NotEqual(string.Empty, state.StatusText);
        Assert.Empty(state.Devices);
    }

    [Fact]
    public void Project_OutputAndInputEndpoints_CarryTheirOwnDirections()
    {
        AudioManager audio = Manager();
        audio.OutputEndpoints.Add(Endpoint("speakers", "Speakers"));
        audio.InputEndpoints.Add(Endpoint("mic", "Microphone"));

        NativeQamAudioState state = AudioManagerNativeQamAudioService.Project(audio);

        Assert.True(state.Available);
        NativeQamAudioDevice speakers = Assert.Single(state.Devices, d => d.Id == "speakers");
        NativeQamAudioDevice mic = Assert.Single(state.Devices, d => d.Id == "mic");
        Assert.True(speakers.HasOutput);
        Assert.False(speakers.HasInput);
        Assert.True(mic.HasInput);
        Assert.False(mic.HasOutput);
    }

    [Fact]
    public void Project_EndpointPresentInBothDirections_IsReportedOnceCarryingBoth()
    {
        // A headset shows up under render and capture with the same id. Steam's model is one entry
        // with a direction test, so listing it twice would put the same hardware in the picker
        // under two identities.
        AudioManager audio = Manager();
        audio.OutputEndpoints.Add(Endpoint("headset", "Headset"));
        audio.InputEndpoints.Add(Endpoint("headset", "Headset"));

        NativeQamAudioState state = AudioManagerNativeQamAudioService.Project(audio);

        NativeQamAudioDevice headset = Assert.Single(state.Devices);
        Assert.True(headset.HasOutput);
        Assert.True(headset.HasInput);
    }

    [Fact]
    public void Project_SelectedEndpoints_BecomeTheActiveIds()
    {
        AudioManager audio = Manager();
        AudioEndpointEntry speakers = Endpoint("speakers", "Speakers");
        AudioEndpointEntry mic = Endpoint("mic", "Microphone");
        audio.OutputEndpoints.Add(speakers);
        audio.InputEndpoints.Add(mic);
        audio.SelectedOutput = speakers;
        audio.SelectedInput = mic;

        NativeQamAudioState state = AudioManagerNativeQamAudioService.Project(audio);

        Assert.Equal("speakers", state.ActiveOutputDeviceId);
        Assert.Equal("mic", state.ActiveInputDeviceId);
    }

    [Fact]
    public void Project_NothingSelected_ReportsEmptyRatherThanGuessing()
    {
        AudioManager audio = Manager();
        audio.OutputEndpoints.Add(Endpoint("speakers", "Speakers"));

        NativeQamAudioState state = AudioManagerNativeQamAudioService.Project(audio);

        Assert.Equal(string.Empty, state.ActiveOutputDeviceId);
    }

    private static AudioManager Manager() => new();

    private static AudioEndpointEntry Endpoint(string id, string name) => new(id, name);
}
