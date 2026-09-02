using WindowsDeviceControl;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class AudioManagerTests
{
    [Theory]
    [InlineData(-20, 0)]
    [InlineData(0, 0)]
    [InlineData(48.4, 48)]
    [InlineData(48.6, 49)]
    [InlineData(100, 100)]
    [InlineData(150, 100)]
    public void SliderValuesAreRoundedAndBoundedForCoreAudio(double value, int expected)
        => Assert.Equal(expected, AudioManager.NormalizeVolume(value));

    [Fact]
    public void NonFiniteSliderValuesFailClosedToZero()
    {
        Assert.Equal(0, AudioManager.NormalizeVolume(double.NaN));
        Assert.Equal(0, AudioManager.NormalizeVolume(double.PositiveInfinity));
    }

    [Fact]
    public void InvalidEndpointFlowsFailWithoutCallingCom()
    {
        // The enum keeps a caller from passing this by accident, but a cast still can,
        // and the value reaches a COM call that would fault on it.
        var result = CoreAudio.ListEndpoints((CoreAudio.AudioDirection)(-1), out var endpoints);

        Assert.Equal(unchecked((int)0x80070057), result);
        Assert.Empty(endpoints);
    }

    [Fact]
    public void EndpointRowsOnlyNotifyWhenTheirFriendlyNameChanges()
    {
        var endpoint = new AudioEndpointEntry("id", "Speakers");
        var changes = new List<string>();
        endpoint.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");

        endpoint.Name = "Speakers";
        endpoint.Name = "Headset";

        Assert.Equal([nameof(AudioEndpointEntry.Name)], changes);
    }

    [Fact]
    public void EndpointRefreshesKeepSurvivingRowsAndUpdateThemInPlace()
    {
        var entries = new System.Collections.ObjectModel.ObservableCollection<AudioEndpointEntry>
        {
            new("stay", "Old name"),
            new("gone", "Disconnected headset"),
        };
        var survivor = entries[0];

        AudioManager.Reconcile(
            entries,
            [
                new CoreAudio.AudioEndpoint("stay", "New name", true),
                new CoreAudio.AudioEndpoint("new", "Dock speakers", false),
            ]);

        Assert.Equal(2, entries.Count);
        Assert.Same(survivor, entries[0]);
        Assert.Equal("New name", survivor.Name);
        Assert.Equal("Dock speakers", entries[1].Name);
    }

    [Fact]
    public void RapidEndpointSelectionsFinishWithTheLatestChoice()
    {
        // The revision bookkeeping behind rapid default-endpoint changes: a stale
        // selection may neither publish UI state nor settle the flow — only the
        // newest revision does. (The writes themselves are serialized by the
        // per-flow gate in AudioManager.ApplyEndpointSelection.)
        var tracker = default(AudioManager.EndpointSelectionTracker);
        Assert.False(tracker.Pending);

        var first = tracker.Begin();
        Assert.True(tracker.Pending);
        Assert.True(tracker.IsCurrent(first));

        var second = tracker.Begin();
        Assert.False(tracker.IsCurrent(first));
        Assert.True(tracker.IsCurrent(second));

        tracker.Complete(first);
        Assert.True(tracker.Pending);

        tracker.Complete(second);
        Assert.False(tracker.Pending);
        Assert.True(tracker.IsCurrent(second));
    }
}
