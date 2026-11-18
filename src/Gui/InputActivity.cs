namespace Zenith.Gui;

/// <summary>Input edges that must reach windows even without pointer movement.
/// Releases and hover changes preserve animations; new presses interrupt them.</summary>
internal readonly record struct InputActivity(bool LeftPressed, bool LeftReleased, bool RightPressed,
    bool MiddlePressed, int Scroll, int Keys)
{
    public bool InterruptsMotion => LeftPressed || RightPressed || MiddlePressed || Scroll != 0 || Keys > 0;
    public bool RoutesWithoutMotion => InterruptsMotion || LeftReleased;
}
