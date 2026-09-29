using WSGM.Core;

namespace WSGM.Tests;

/// <summary>Replays a recorded frametime trace through the controller.</summary>
/// <remarks>
/// The regression harness for this feature. A reported oscillation or a limit that walked to maximum
/// is reproduced by recording the trace and replaying it here, with no device involved; the
/// controller is only allowed to grow more sophisticated when a recorded trace defeats the simple
/// one. It lives in the test project because nothing in the application replays a trace.
/// </remarks>
internal static class AutoTdpReplay
{
    /// <summary>Runs a trace and returns every decision in order.</summary>
    internal static IReadOnlyList<AutoTdpDecision> Run(
        AutoTdpController controller,
        AutoTdpLimits limits,
        IEnumerable<AutoTdpSample> trace)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(trace);
        List<AutoTdpDecision> decisions = [];
        foreach (AutoTdpSample sample in trace)
        {
            decisions.Add(controller.Evaluate(sample, limits));
        }

        return decisions;
    }

    /// <summary>Builds a run of identical windows.</summary>
    internal static IEnumerable<AutoTdpSample> Run(
        int count,
        double frametimeMs,
        double targetFrametimeMs,
        string contextKey,
        bool capped = false)
    {
        for (int index = 0; index < count; index++)
        {
            yield return new AutoTdpSample(frametimeMs, targetFrametimeMs, capped, contextKey);
        }
    }
}
