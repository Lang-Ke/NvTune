namespace SimpleNvidiaUndervolt;

internal static class ControlExtensions
{
    public static T Add<T>(this T panel, params Control[] children) where T : Control
    {
        foreach (var c in children) panel.Controls.Add(c);
        return panel;
    }
}
