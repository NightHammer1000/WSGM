using System.Collections.Generic;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using WSGM.Controls;

namespace WSGM.Overlay;

/// <summary>A bounded shared-performance snapshot for one overlay projection.</summary>
internal sealed record PerformanceOverlaySnapshot(
    bool Visible,
    string Status,
    IReadOnlyList<DescriptorRow> Rows,
    IReadOnlyList<DescriptorRow> ProfileRows);

/// <summary>Semantic presentation state shared by descriptor-driven overlay rows.</summary>
internal enum DescriptorStatus
{
    None,
    Available,
    Warning,
    Faulted,
    Stale,
    ExternallyOwned,
    Unsupported,
    Progress,
}

/// <summary>Immutable, presentation-only content for a descriptor-driven overlay row.</summary>
internal sealed record DescriptorRow(
    string Id,
    string Title,
    string Description,
    string TrailingText,
    bool CanInvoke,
    DescriptorStatus Status = DescriptorStatus.None);

/// <summary>
/// Renders a closed semantic row descriptor with the shared card appearance and status vocabulary.
/// </summary>
internal sealed class DescriptorStatusRow : CardButton
{
    internal void Apply(DescriptorRow descriptor)
    {
        Tag = descriptor.Id;
        Title = descriptor.Title;
        Description = descriptor.Description;
        TrailingText = descriptor.TrailingText;
        IsEnabled = descriptor.CanInvoke;
        IconGeometry = Icons.Gear;
        StatusBrush = StatusBrushFor(descriptor.Status);
        AutomationProperties.SetName(this, descriptor.Title);
        AutomationProperties.SetHelpText(this, descriptor.Description);
    }

    private IBrush? StatusBrushFor(DescriptorStatus status)
    {
        string? resource = status switch
        {
            DescriptorStatus.Available => "HcSuccessBrush",
            DescriptorStatus.Warning or DescriptorStatus.Stale => "HcWarningBrush",
            DescriptorStatus.Faulted => "HcDangerBrush",
            DescriptorStatus.ExternallyOwned or DescriptorStatus.Unsupported => "HcTextMutedBrush",
            DescriptorStatus.Progress => "HcWarningBrush",
            _ => null,
        };
        return resource is null ? null : this.FindResource(resource) as IBrush;
    }
}
