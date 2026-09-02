namespace Patchboard.Models;

/// <summary>
/// Modifier flags matching the Win32 RegisterHotKey fsModifiers parameter.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// A global key combination. <see cref="VirtualKey"/> is a Win32 virtual-key code,
/// not a WPF <c>Key</c>, because that is what RegisterHotKey takes.
/// </summary>
public sealed class Hotkey
{
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.None;

    public uint VirtualKey { get; set; }

    public bool IsSet => VirtualKey != 0;

    /// <summary>Human readable form for the button corner, e.g. "Ctrl+Shift+F5".</summary>
    public override string ToString()
    {
        if (!IsSet) return "";
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyNames.Describe(VirtualKey));
        return string.Join("+", parts);
    }
}
