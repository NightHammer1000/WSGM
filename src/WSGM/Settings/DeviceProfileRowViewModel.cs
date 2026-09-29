using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Settings;

/// <summary>One authored device profile being edited.</summary>
/// <remarks>
/// Holds the curve in the SDK's own point type because that is what the editor control speaks;
/// conversion to the stored shape happens once, at save. Keeping two mutable representations in
/// step during a drag is exactly the kind of bookkeeping that goes wrong silently.
/// </remarks>
public sealed class DeviceProfileRowViewModel : INotifyPropertyChanged
{
    private string _name;
    private IReadOnlyList<CurvePoint> _curve;
    private int? _color;

    /// <summary>Creates a row from a stored profile.</summary>
    /// <param name="profile">The stored profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public DeviceProfileRowViewModel(DeviceAuthoredProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ProfileId = profile.ProfileId;
        CapabilityId = profile.CapabilityId;
        _name = profile.Name;
        _curve =
        [
            .. profile.Curve.Select(point => new CurvePoint(point.Input, point.Output)),
        ];
        _color = profile.Color;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stable identifier the overlay selects by. Never changes with a rename.</summary>
    public string ProfileId { get; }

    /// <summary>The capability this profile authors.</summary>
    public string CapabilityId { get; }

    /// <summary>Gets or sets what the user calls it.</summary>
    public string Name
    {
        get => _name;
        set
        {
            string bounded = (value ?? string.Empty).Trim();
            if (bounded.Length > DeviceAuthoredProfile.MaxNameLength)
            {
                bounded = bounded[..DeviceAuthoredProfile.MaxNameLength];
            }

            if (string.Equals(_name, bounded, StringComparison.Ordinal))
            {
                return;
            }

            _name = bounded;
            Raise(nameof(Name));
        }
    }

    /// <summary>Gets or sets the curve, as the editor control works with it.</summary>
    public IReadOnlyList<CurvePoint> Curve
    {
        get => _curve;
        set
        {
            _curve = value ?? [];
            Raise(nameof(Curve));
        }
    }

    /// <summary>Gets or sets the packed 24-bit colour of a lighting profile.</summary>
    /// <remarks>
    /// Null for a profile that is not a lighting one. A profile carries a curve or a colour, never
    /// both: the capability it authors decides which, and storing the unused half would let a
    /// capability change silently resurrect a value the user set for something else.
    /// </remarks>
    public int? Color
    {
        get => _color;
        set
        {
            // Masked to 24 bits on the way in. The picker hands back an alpha channel WSGM has no
            // use for, and a stored value carrying one reads as a wildly different colour when it
            // is later unpacked as RGB.
            int? bounded = value is { } packed ? packed & 0xFFFFFF : null;
            if (_color == bounded)
            {
                return;
            }

            _color = bounded;
            Raise(nameof(Color));
            Raise(nameof(PickerColor));
            Raise(nameof(ColorHex));
        }
    }

    /// <summary>The authored colour in the type consumed directly by Avalonia's picker.</summary>
    public Avalonia.Media.Color PickerColor
    {
        get => Avalonia.Media.Color.FromUInt32((uint)(0xFF000000 | (_color ?? 0)));
        set => Color = (value.R << 16) | (value.G << 8) | value.B;
    }

    /// <summary>The authored colour as an editable RGB string.</summary>
    public string ColorHex
    {
        get => $"#{(_color ?? 0):X6}";
        set
        {
            if (Avalonia.Media.Color.TryParse(value, out Avalonia.Media.Color color))
            {
                PickerColor = color;
            }
        }
    }

    /// <summary>Whether this profile authors a colour.</summary>
    /// <remarks>
    /// Decided by what the profile actually carries, not by what it lacks. "Has no curve" would
    /// class a half-built profile as a colour one and put a picker in front of a fan curve.
    /// </remarks>
    public bool IsColorProfile => _color is not null;

    /// <summary>Whether this profile authors a curve.</summary>
    public bool IsCurveProfile => _curve.Count > 0;

    /// <summary>Converts back to the stored shape.</summary>
    /// <returns>The profile to persist.</returns>
    /// <remarks>
    /// A rename keeps <see cref="ProfileId"/>, which is the entire reason the two are separate: an
    /// application override points at the id, and renaming a profile must not orphan it.
    /// </remarks>
    public DeviceAuthoredProfile ToStored() => new()
    {
        ProfileId = ProfileId,
        Name = string.IsNullOrWhiteSpace(_name) ? ProfileId : _name,
        CapabilityId = CapabilityId,
        Curve =
        [
            .. _curve.Select(point => new AuthoredCurvePoint
            {
                Input = point.Input,
                Output = point.Output,
            }),
        ],
        Color = _color,
    };

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
