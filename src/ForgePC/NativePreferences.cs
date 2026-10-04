using System.Globalization;
using System.IO;

namespace ForgePC;

public enum NativeParameter { PointerValue, UiValue, AnimationStructure }
public record NativePreference(string Id, uint Get, uint Set, NativeParameter Parameter, bool Numeric = false, int Minimum = 0, int Maximum = 1);

public static class NativePreferences
{
    public const string Documentation = "https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow";
    public static readonly NativePreference[] Specifications =
    [
        new("animations", 0x1042, 0x1043, NativeParameter.PointerValue),
        new("menus", 0x1002, 0x1003, NativeParameter.PointerValue),
        new("minimize-animation", 0x0048, 0x0049, NativeParameter.AnimationStructure),
        new("combo-animation", 0x1004, 0x1005, NativeParameter.PointerValue),
        new("list-smooth-scroll", 0x1006, 0x1007, NativeParameter.PointerValue),
        new("tooltip-animation", 0x1016, 0x1017, NativeParameter.PointerValue),
        new("selection-fade", 0x1014, 0x1015, NativeParameter.PointerValue),
        new("cursor-shadow", 0x101A, 0x101B, NativeParameter.PointerValue),
        new("menu-shadow", 0x1024, 0x1025, NativeParameter.PointerValue),
        new("drag-contents", 0x0026, 0x0025, NativeParameter.UiValue),
        new("menu-delay", 0x006A, 0x006B, NativeParameter.UiValue, true, 0, 60000),
        new("mouse-speed", 0x0070, 0x0071, NativeParameter.PointerValue, true, 1, 20),
        new("keyboard-delay", 0x0016, 0x0017, NativeParameter.UiValue, true, 0, 3),
        new("keyboard-repeat", 0x000A, 0x000B, NativeParameter.UiValue, true, 0, 31)
    ];
    public static NativePreference Get(string id) => Specifications.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("Unknown native preference.");
    public static int ParseNumber(NativePreference preference, string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ||
            number < preference.Minimum || number > preference.Maximum || number.ToString(CultureInfo.InvariantCulture) != value)
            throw new InvalidDataException("Preference value is outside its supported range.");
        return number;
    }
    public static string Format(string id, string value)
    {
        if (id == "menu-delay") return value + " ms";
        if (id == "mouse-speed") return value + " / 20";
        if (id == "keyboard-repeat") return value + " / 31";
        if (id == "keyboard-delay" && int.TryParse(value, out int level)) return "~" + ((level + 1) * 250) + " ms";
        return value;
    }
    public static IEnumerable<ValueOption> Choices(string id, string current)
    {
        var preference = Get(id);
        if (!preference.Numeric) return [new("On", "On"), new("Off", "Off")];
        IEnumerable<int> choices = id == "menu-delay" ? new[] { 0, 100, 200, 400, 500, 1000 } : Enumerable.Range(preference.Minimum, preference.Maximum - preference.Minimum + 1);
        if (int.TryParse(current, out int original) && original >= preference.Minimum && original <= preference.Maximum) choices = choices.Append(original);
        return choices.Distinct().Order().Select(number => new ValueOption(number.ToString(CultureInfo.InvariantCulture), Format(id, number.ToString(CultureInfo.InvariantCulture))));
    }
    private static OperationDefinition Preference(string id, string title, string category, string purpose, string tradeoff) =>
        new(id, 1, title, category, purpose, tradeoff, Documentation);
    public static readonly OperationDefinition[] Additional =
    [
        Preference("minimize-animation", "Minimize and restore animation", "Windows", "Control the motion when Windows minimizes or restores a window.", "A desktop motion preference. It does not change game frame rendering."),
        Preference("combo-animation", "Dropdown animation", "Windows", "Choose whether supported dropdown controls slide open.", "Some modern apps draw their own controls and ignore this preference."),
        Preference("list-smooth-scroll", "List scrolling effects", "Windows", "Choose smooth scrolling in native Windows list boxes.", "This affects supported list controls, not game frame pacing or browser scrolling."),
        Preference("tooltip-animation", "Tooltip animation", "Windows", "Control the appearance animation of supported tooltips.", "Disabling motion changes how hints appear. It does not reduce network or input latency."),
        Preference("selection-fade", "Menu selection fade", "Windows", "Choose whether a selected menu item briefly fades after closing.", "A visual preference; effects depend on the app and Windows theme."),
        Preference("cursor-shadow", "Pointer shadow", "Windows", "Show or hide the shadow around the mouse pointer.", "The shadow can make the pointer easier to see. Kept out of presets."),
        Preference("menu-shadow", "Supported menu shadows", "Windows", "Control drop shadows for windows using the documented drop-shadow class style.", "This is not a global switch for all modern window shadows."),
        Preference("drag-contents", "Window contents while dragging", "Windows", "Choose full contents or an outline while dragging supported windows.", "Outline dragging shows less detail. App behavior can differ."),
        Preference("menu-delay", "Submenu opening delay", "Windows", "Set how long Windows waits before opening a hovered submenu.", "Very short delays can open menus accidentally. This does not change game input latency."),
        Preference("mouse-speed", "Windows pointer speed", "Windows", "Adjust the documented desktop pointer-speed scale from 1 to 20.", "Raw-input games may ignore this setting. Choose for comfort; it is not a polling-rate tweak."),
        Preference("keyboard-delay", "Keyboard repeat delay", "Windows", "Choose the delay before a held key starts repeating.", "Actual timing depends on hardware. This changes typing behavior, not the first keypress latency."),
        Preference("keyboard-repeat", "Keyboard repeat rate", "Windows", "Adjust the repeat-rate scale for a held key.", "A faster repeat can cause unwanted repeated text. It is not a keyboard polling-rate control.")
    ];
}
