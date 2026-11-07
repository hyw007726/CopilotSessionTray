namespace CopilotSessionTray.Core.Tests.Services;

internal static class ColorMath
{
    public static double Contrast(string first, string second)
    {
        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static double Luminance(string hex)
    {
        var (red, green, blue) = Parse(hex);
        return 0.2126 * Linear(red) + 0.7152 * Linear(green) + 0.0722 * Linear(blue);
    }

    public static (double Hue, double Saturation) HueAndSaturation(string hex)
    {
        var (red, green, blue) = Parse(hex);
        double r = red / 255.0, g = green / 255.0, b = blue / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        if (delta == 0)
        {
            return (0, 0);
        }

        var hue = max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return (hue < 0 ? hue + 360 : hue, delta / max);
    }

    public static bool IsRed(string hex)
    {
        var (hue, saturation) = HueAndSaturation(hex);
        return saturation >= 0.5 && (hue <= 12 || hue >= 348);
    }

    public static double HueDistance(string first, string second)
    {
        var apart = Math.Abs(HueAndSaturation(first).Hue - HueAndSaturation(second).Hue);
        return apart > 180 ? 360 - apart : apart;
    }

    public static double Difference(string first, string second)
    {
        var (l1, a1, b1) = Lab(first);
        var (l2, a2, b2) = Lab(second);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    private static (double L, double A, double B) Lab(string hex)
    {
        var (red, green, blue) = Parse(hex);
        double r = Linear(red), g = Linear(green), b = Linear(blue);
        var x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        double fx = LabCurve(x), fy = LabCurve(y), fz = LabCurve(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static double LabCurve(double value) => value > 0.008856 ? Math.Cbrt(value) : 7.787 * value + 16.0 / 116;

    private static double Linear(int channel)
    {
        var value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static (int Red, int Green, int Blue) Parse(string hex) =>
        (Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));
}
