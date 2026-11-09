namespace AresToys.Editor.Model;

/// <summary>HSV triple, all components in [0, 1]. Hue wraps at 1 → 0 (treat 1 and 0 as the same red).</summary>
public readonly record struct Hsv(double H, double S, double V)
{
    public static Hsv FromRgb(byte r, byte g, byte b)
    {
        var rf = r / 255.0;
        var gf = g / 255.0;
        var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var v = max;
        var delta = max - min;
        var s = max <= 0 ? 0 : delta / max;

        double h;
        if (delta <= 0) h = 0;
        else if (max == rf) h = ((gf - bf) / delta) % 6;
        else if (max == gf) h = (bf - rf) / delta + 2;
        else h = (rf - gf) / delta + 4;
        h /= 6.0;
        if (h < 0) h += 1;
        return new Hsv(h, s, v);
    }

    public (byte R, byte G, byte B) ToRgb()
    {
        var h6 = (H * 6.0) % 6.0;
        if (h6 < 0) h6 += 6;
        var c = V * S;
        var x = c * (1 - Math.Abs(h6 % 2 - 1));
        var m = V - c;
        double rf, gf, bf;
        switch ((int)Math.Floor(h6))
        {
            case 0: rf = c; gf = x; bf = 0; break;
            case 1: rf = x; gf = c; bf = 0; break;
            case 2: rf = 0; gf = c; bf = x; break;
            case 3: rf = 0; gf = x; bf = c; break;
            case 4: rf = x; gf = 0; bf = c; break;
            default: rf = c; gf = 0; bf = x; break;
        }
        return ((byte)Math.Round((rf + m) * 255),
                (byte)Math.Round((gf + m) * 255),
                (byte)Math.Round((bf + m) * 255));
    }
}
