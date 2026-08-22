namespace Tweaker.App.Presentation;

public static class UiMotion
{
    public static TimeSpan Duration(bool reduceMotion) =>
        reduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(160);

    public static double Offset(bool reduceMotion) => reduceMotion ? 0 : 6;
}
