using System;

namespace WSGM.Input;

/// <summary>
/// Where WSGM's own navigation gets its button presses from.
/// </summary>
/// <remarks>
/// One event, because that is the entire coupling every navigation surface has had to
/// <see cref="GamepadService"/>. Making it an interface is what lets the managed canonical stream
/// stand in for SDL without any surface knowing which one it is talking to.
/// </remarks>
public interface IUiButtonSource
{
    /// <summary>Raised on the press edge of each button, on the UI thread.</summary>
    event Action<GamepadButtons>? ButtonPressed;
}
