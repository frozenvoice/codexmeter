namespace CodexMeter.Codex;

public static class FlyoutZoom
{
    public const int DefaultPercent = 100;
    public const int MinPercent = 80;
    public const int MaxPercent = 150;
    public const int StepPercent = 10;

    public static int Normalize(int percent) =>
        ((Math.Clamp(percent, MinPercent, MaxPercent) + StepPercent / 2) / StepPercent) * StepPercent;

    public static int Adjust(int percent, bool increase) =>
        Math.Clamp(Normalize(percent) + (increase ? StepPercent : -StepPercent), MinPercent, MaxPercent);
}
