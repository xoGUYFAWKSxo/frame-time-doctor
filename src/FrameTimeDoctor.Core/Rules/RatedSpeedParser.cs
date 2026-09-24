using System.Text.RegularExpressions;

namespace FrameTimeDoctor.Core.Rules;

/// <summary>
/// Windows only reports the JEDEC speed of a memory module, not its EXPO/XMP rating, so the rated
/// speed is decoded from the vendor part number. Returns null for unrecognised part numbers rather
/// than guessing — a wrong rated speed would produce a false "EXPO is off" finding.
/// </summary>
public static class RatedSpeedParser
{
    static readonly (Regex Pattern, int Multiplier)[] Patterns =
    {
        (new(@"^KF[45](\d{2})C", RegexOptions.IgnoreCase), 100),                // Kingston Fury: KF560C32 = 6000
        (new(@"GX[45]M\d[A-Z](\d{4})", RegexOptions.IgnoreCase), 1),            // Corsair: CMK32GX5M2B6000C36
        (new(@"^F[45]-(\d{4})", RegexOptions.IgnoreCase), 1),                   // G.Skill: F5-6000J3038F16G
        (new(@"(\d{4})HC\d{2}", RegexOptions.IgnoreCase), 1),                   // TeamGroup: FF3D532G6000HC38A
        (new(@"^C[TP](?:\d+K)?\d+G(\d{2})C\d{2}", RegexOptions.IgnoreCase), 100), // Crucial: CP16G60C36U5B, CT16G48C40U5
        (new(@"^PV[A-Z]?5\d+G(\d{3})C", RegexOptions.IgnoreCase), 10),          // Patriot Viper: PVV532G600C36K
        (new(@"^AX5U(\d{4})C", RegexOptions.IgnoreCase), 1),                    // ADATA XPG: AX5U6000C3016G
    };

    public static int? Parse(string? partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber)) return null;
        var pn = partNumber.Trim();
        foreach (var (pattern, multiplier) in Patterns)
        {
            var m = pattern.Match(pn);
            if (!m.Success) continue;
            var speed = int.Parse(m.Groups[1].Value) * multiplier;
            if (speed is >= 2133 and <= 10000) return speed;
        }
        return null;
    }
}
