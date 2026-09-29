using Avalonia.Media;

namespace WSGM.Controls;

/// <summary>Application vector icons as cached <see cref="StreamGeometry"/> instances.
/// Each geometry is authored on a 24x24 viewbox as stroke-style path data (render with
/// a ~2 px stroke and no fill, e.g. via <c>Path</c> or <c>CardButton.IconGeometry</c>)
/// and parsed exactly once from its path string — plain string parsing with no external
/// asset decoder (same approach as <see cref="WSGM.Overlay.GlyphIcon"/>).</summary>
public static class Icons
{
    /// <summary>Play triangle (start / resume, Big Picture home action).</summary>
    public static StreamGeometry Play { get; } =
        StreamGeometry.Parse("M 8,4.5 L 19.5,12 L 8,19.5 Z");

    /// <summary>Desktop monitor with stand (switch to desktop mode).</summary>
    public static StreamGeometry Monitor { get; } =
        StreamGeometry.Parse("M 3,4.5 L 21,4.5 L 21,16.5 L 3,16.5 Z M 12,16.5 L 12,20 M 8,20 L 16,20");

    /// <summary>Two diagonal arrows pointing outward (exit Big Picture / leave fullscreen).</summary>
    public static StreamGeometry ExitFullscreen { get; } =
        StreamGeometry.Parse("M 14,10 L 21,3 M 15,3 L 21,3 L 21,9 M 10,14 L 3,21 M 9,21 L 3,21 L 3,15");

    /// <summary>Close cross (X) — close Steam, dismiss surfaces.</summary>
    public static StreamGeometry Close { get; } =
        StreamGeometry.Parse("M 5.5,5.5 L 18.5,18.5 M 18.5,5.5 L 5.5,18.5");

    /// <summary>Gear (settings): inner ring plus eight radial teeth.</summary>
    public static StreamGeometry Gear { get; } =
        StreamGeometry.Parse(
            "M 12,8 A 4,4 0 1 0 12,16 A 4,4 0 1 0 12,8 "
                + "M 12,3 L 12,5.7 M 12,18.3 L 12,21 M 3,12 L 5.7,12 M 18.3,12 L 21,12 "
                + "M 5.6,5.6 L 7.5,7.5 M 16.5,16.5 L 18.4,18.4 M 16.5,7.5 L 18.4,5.6 M 5.6,18.4 L 7.5,16.5");

    /// <summary>Four-square grid (Task Manager).</summary>
    public static StreamGeometry Grid4 { get; } =
        StreamGeometry.Parse(
            "M 4,4 L 10,4 L 10,10 L 4,10 Z M 14,4 L 20,4 L 20,10 L 14,10 Z "
                + "M 4,14 L 10,14 L 10,20 L 4,20 Z M 14,14 L 20,14 L 20,20 L 14,20 Z");

    /// <summary>Three bulleted lines (a list of entries).</summary>
    public static StreamGeometry ListLines { get; } =
        StreamGeometry.Parse(
            "M 3,6 L 5,6 M 8,6 L 21,6 M 3,12 L 5,12 M 8,12 L 21,12 "
                + "M 3,18 L 5,18 M 8,18 L 21,18");

    /// <summary>Up and down arrows side by side (reorder a list).</summary>
    public static StreamGeometry Reorder { get; } =
        StreamGeometry.Parse(
            "M 8,20 L 8,4 M 4.5,7.5 L 8,4 L 11.5,7.5 M 16,4 L 16,20 M 12.5,16.5 L 16,20 L 19.5,16.5");

    /// <summary>Straight arrow pointing up (move a list entry up).</summary>
    public static StreamGeometry ArrowUp { get; } =
        StreamGeometry.Parse("M 12,20 L 12,4 M 5.5,10.5 L 12,4 L 18.5,10.5");

    /// <summary>Straight arrow pointing down (move a list entry down).</summary>
    public static StreamGeometry ArrowDown { get; } =
        StreamGeometry.Parse("M 12,4 L 12,20 M 5.5,13.5 L 12,20 L 18.5,13.5");

    /// <summary>Two stacked documents (copy-to-clipboard commands).</summary>
    public static StreamGeometry CopyDoc { get; } =
        StreamGeometry.Parse("M 5,8 L 15,8 L 15,21 L 5,21 Z M 9,8 L 9,4 L 19,4 L 19,17 L 15,17");

    /// <summary>Circle with a diagonal slash (blocked / Steam Input block command).</summary>
    public static StreamGeometry BlockedCircle { get; } =
        StreamGeometry.Parse("M 12,4 A 8,8 0 1 0 12,20 A 8,8 0 1 0 12,4 M 6.3,6.3 L 17.7,17.7");

    /// <summary>Crescent moon (sleep).</summary>
    public static StreamGeometry Moon { get; } =
        StreamGeometry.Parse("M 21,12.8 A 9,9 0 1 1 11.2,3 A 7,7 0 0 0 21,12.8 Z");

    /// <summary>Six-point snowflake (hibernate). Deliberately not the power symbol:
    /// hibernate sits next to Shut down in the Power tab, and sharing that glyph made
    /// the two rows read as the same action.</summary>
    public static StreamGeometry Snowflake { get; } =
        StreamGeometry.Parse(
            "M 12,3 L 12,21 M 4.2,7.5 L 19.8,16.5 M 4.2,16.5 L 19.8,7.5 "
                + "M 9.5,5.5 L 12,3 L 14.5,5.5 M 9.5,18.5 L 12,21 L 14.5,18.5");

    /// <summary>Steaming coffee mug (keep awake).</summary>
    public static StreamGeometry Mug { get; } =
        StreamGeometry.Parse(
            "M 4,8 L 16,8 L 16,17 A 3,3 0 0 1 13,20 L 7,20 A 3,3 0 0 1 4,17 Z "
                + "M 16,10.5 L 17.5,10.5 A 2.5,2.5 0 0 1 17.5,15.5 L 16,15.5 "
                + "M 7.5,5 L 7.5,3 M 12.5,5 L 12.5,3");

    /// <summary>Circular arrow with an arrowhead (restart).</summary>
    public static StreamGeometry Restart { get; } =
        StreamGeometry.Parse("M 12,4 A 8,8 0 1 0 20,12 M 17.4,14.6 L 20,12 L 22.6,14.6");

    /// <summary>Power symbol: broken circle with a vertical bar (shut down).</summary>
    public static StreamGeometry Power { get; } =
        StreamGeometry.Parse("M 12,2.5 L 12,11 M 7.5,5.6 A 8,8 0 1 0 16.5,5.6");

    /// <summary>Wrench (tools tab).</summary>
    public static StreamGeometry Wrench { get; } =
        StreamGeometry.Parse(
            "M 17.7,3.3 L 17.1,6.9 L 20.7,6.3 A 5,5 0 0 1 14,12.6 L 6.7,19.9 "
                + "A 1.8,1.8 0 0 1 4.1,17.3 L 11.4,10 A 5,5 0 0 1 17.7,3.3 Z");

    /// <summary>Bluetooth rune.</summary>
    public static StreamGeometry Bluetooth { get; } =
        StreamGeometry.Parse("M 6.5,6.5 L 17.5,17.5 L 12,22 L 12,2 L 17.5,6.5 L 6.5,17.5");

    /// <summary>Wi-Fi fan: three arcs above a small dot.</summary>
    public static StreamGeometry WiFi { get; } =
        StreamGeometry.Parse(
            "M 8.8,16.8 A 4.5,4.5 0 0 1 15.2,16.8 "
                + "M 6.3,14.3 A 8,8 0 0 1 17.7,14.3 "
                + "M 3.9,11.9 A 11.5,11.5 0 0 1 20.1,11.9 "
                + "M 11.3,20.1 A 0.7,0.7 0 1 0 12.7,20.1 A 0.7,0.7 0 1 0 11.3,20.1");

    /// <summary>SD/memory card: rounded body with a clipped top-right corner and
    /// three contact pins.</summary>
    public static StreamGeometry SdCard { get; } =
        StreamGeometry.Parse(
            "M 6,3 L 15,3 L 19,7 L 19,21 L 6,21 Z "
                + "M 9,3 L 9,6 M 11.5,3 L 11.5,6 M 14,3 L 14,6");

    /// <summary>Plus in a rounded square (add a library folder).</summary>
    public static StreamGeometry FolderPlus { get; } =
        StreamGeometry.Parse(
            "M 3,6 L 9,6 L 11,8.5 L 21,8.5 L 21,19 L 3,19 Z "
                + "M 12,11.5 L 12,16 M 9.75,13.75 L 14.25,13.75");

    /// <summary>Eject symbol: triangle over a base bar (safe removal).</summary>
    public static StreamGeometry Eject { get; } =
        StreamGeometry.Parse("M 12,5.5 L 18.5,13 L 5.5,13 Z M 5.5,17 L 18.5,17");

    /// <summary>Horizontal battery: body, terminal nub and three charge bars.</summary>
    public static StreamGeometry Battery { get; } =
        StreamGeometry.Parse(
            "M 2.5,8 L 18.5,8 L 18.5,16 L 2.5,16 Z M 21,10.5 L 21,13.5 "
                + "M 5.5,10.5 L 5.5,13.5 M 8.5,10.5 L 8.5,13.5 M 11.5,10.5 L 11.5,13.5");

    /// <summary>Speaker cone with two sound-wave arcs.</summary>
    public static StreamGeometry Volume { get; } =
        StreamGeometry.Parse(
            "M 3,10 L 7,10 L 12,6 L 12,18 L 7,14 L 3,14 Z "
                + "M 15,9 A 4,4 0 0 1 15,15 M 17.5,6.5 A 7.5,7.5 0 0 1 17.5,17.5");

    /// <summary>Window with a right-hand side panel (quick access).</summary>
    public static StreamGeometry Panel { get; } =
        StreamGeometry.Parse(
            "M 3,5 L 21,5 L 21,19 L 3,19 Z M 15,5 L 15,19 M 17,8.5 L 19,8.5 M 17,11.5 L 19,11.5");

    /// <summary>Push pin marking a row that is present on the Quick access root.</summary>
    public static StreamGeometry Pin { get; } =
        StreamGeometry.Parse(
            "M 8,3 L 16,3 M 9,3 L 9,9 L 6,12 L 18,12 L 15,9 L 15,3 M 12,12 L 12,21");

    /// <summary>Painter's palette with four paint wells (appearance).</summary>
    public static StreamGeometry Palette { get; } =
        StreamGeometry.Parse(
            "M 12,3 A 9,9 0 1 0 12,21 C 13.2,21 14,20.2 14,19 C 14,18.4 13.7,18 13.4,17.6 "
                + "C 13.1,17.2 12.9,16.8 12.9,16.3 C 12.9,15.2 13.8,14.3 14.9,14.3 L 17.5,14.3 "
                + "A 3.5,3.5 0 0 0 21,10.8 C 20.4,6.3 16.6,3 12,3 Z "
                + "M 6.3,9.5 A 1.2,1.2 0 1 0 8.7,9.5 A 1.2,1.2 0 1 0 6.3,9.5 "
                + "M 9.3,6.3 A 1.2,1.2 0 1 0 11.7,6.3 A 1.2,1.2 0 1 0 9.3,6.3 "
                + "M 13.3,6.3 A 1.2,1.2 0 1 0 15.7,6.3 A 1.2,1.2 0 1 0 13.3,6.3 "
                + "M 16.3,9.8 A 1.2,1.2 0 1 0 18.7,9.8 A 1.2,1.2 0 1 0 16.3,9.8");

    /// <summary>Rocket with fins, porthole and exhaust flame (startup apps).</summary>
    public static StreamGeometry Rocket { get; } =
        StreamGeometry.Parse(
            "M 12,2.5 C 15.5,4.5 17,8 17,11.5 L 17,14 L 7,14 L 7,11.5 C 7,8 8.5,4.5 12,2.5 Z "
                + "M 7,12.5 L 4.5,17 L 7.5,15.8 M 17,12.5 L 19.5,17 L 16.5,15.8 "
                + "M 10,9.5 A 2,2 0 1 0 14,9.5 A 2,2 0 1 0 10,9.5 "
                + "M 10.5,16.5 L 12,21 L 13.5,16.5");

    /// <summary>Gamepad silhouette with D-pad and face buttons (Steam / game mode).</summary>
    public static StreamGeometry SteamLike { get; } =
        StreamGeometry.Parse(
            "M 6.5,8 L 17.5,8 A 4.5,4.5 0 0 1 21.9,13.4 L 21,17.2 A 2.6,2.6 0 0 1 16.6,18.4 "
                + "L 14.8,16 L 9.2,16 L 7.4,18.4 A 2.6,2.6 0 0 1 3,17.2 L 2.1,13.4 "
                + "A 4.5,4.5 0 0 1 6.5,8 Z "
                + "M 8,10.2 L 8,13.8 M 6.2,12 L 9.8,12 "
                + "M 14.8,10.6 A 1,1 0 1 0 16.8,10.6 A 1,1 0 1 0 14.8,10.6 "
                + "M 17,13 A 1,1 0 1 0 19,13 A 1,1 0 1 0 17,13");
}
