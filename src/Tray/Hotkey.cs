using System.Windows.Forms;

namespace HtmlWallpaper.Tray;

/// <summary>A parsed global hotkey (modifiers + virtual key) with string round-tripping.</summary>
internal sealed class Hotkey
{
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }
    public Keys Key { get; set; }

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    public uint Modifiers
    {
        get
        {
            uint m = MOD_NOREPEAT;
            if (Alt) m |= MOD_ALT;
            if (Ctrl) m |= MOD_CONTROL;
            if (Shift) m |= MOD_SHIFT;
            if (Win) m |= MOD_WIN;
            return m;
        }
    }

    public uint Vk => (uint)Key;

    public bool HasModifier => Ctrl || Alt || Shift || Win;

    public static Hotkey Default => new() { Ctrl = true, Alt = true, Key = Keys.C };

    public static Hotkey Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Default;
        var hk = new Hotkey();
        string[] parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string raw in parts)
        {
            string p = raw;
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": hk.Ctrl = true; break;
                case "alt": hk.Alt = true; break;
                case "shift": hk.Shift = true; break;
                case "win": case "windows": hk.Win = true; break;
                default:
                    if (p.Length == 1 && char.IsDigit(p[0])) p = "D" + p;
                    if (Enum.TryParse<Keys>(p, ignoreCase: true, out Keys k)) hk.Key = k;
                    break;
            }
        }
        if (hk.Key == Keys.None) return Default;
        return hk;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyLabel(Key));
        return string.Join("+", parts);
    }

    private static string KeyLabel(Keys k)
    {
        string s = k.ToString();
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s.Substring(1); // D1 -> 1
        if (s.StartsWith("NumPad")) return "Num" + s.Substring(6);
        return s;
    }
}
