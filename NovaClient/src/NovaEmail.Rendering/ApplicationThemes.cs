using System.Globalization;

namespace NovaEmail.Rendering;

public enum ApplicationThemeSelection
{
    System,
    Light,
    Dark,
    Ocean,
    Forest,
    Sunset,
    HolidayAutomatic,
    WinterLights,
    Valentine,
    Spring,
    Independence,
    Halloween,
}

public enum ApplicationThemeBase
{
    System,
    Light,
    Dark,
}

public readonly record struct ThemeColor(byte Red, byte Green, byte Blue)
{
    public static ThemeColor Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 7 || value[0] != '#')
            throw new FormatException("Theme colors must use #RRGGBB notation.");
        return new(
            byte.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public double ContrastRatio(ThemeColor other)
    {
        var first = RelativeLuminance();
        var second = other.RelativeLuminance();
        return (Math.Max(first, second) + 0.05d) / (Math.Min(first, second) + 0.05d);
    }

    private double RelativeLuminance() =>
        (0.2126d * Linearize(Red)) + (0.7152d * Linearize(Green)) + (0.0722d * Linearize(Blue));

    private static double Linearize(byte component)
    {
        var normalized = component / 255d;
        return normalized <= 0.04045d
            ? normalized / 12.92d
            : Math.Pow((normalized + 0.055d) / 1.055d, 2.4d);
    }
}

public sealed record ApplicationThemePalette(
    ThemeColor PageBackground,
    ThemeColor Surface,
    ThemeColor Banner,
    ThemeColor BannerText,
    ThemeColor BodyText);

public sealed record ApplicationThemeDefinition(
    ApplicationThemeSelection Selection,
    string Id,
    string DisplayName,
    ApplicationThemeBase BaseTheme,
    ApplicationThemePalette? Palette,
    bool IsAutomatic = false);

public sealed record ResolvedApplicationTheme(
    ApplicationThemeDefinition Requested,
    ApplicationThemeDefinition Effective,
    bool HighContrastOverride);

public static class ApplicationThemeCatalog
{
    private static readonly IReadOnlyList<ApplicationThemeDefinition> Definitions =
    [
        New(ApplicationThemeSelection.System, "system", "Use Windows setting", ApplicationThemeBase.System),
        New(ApplicationThemeSelection.Light, "light", "Light", ApplicationThemeBase.Light,
            Palette("#FFFFFF", "#F5F5F5", "#005FB8", "#FFFFFF", "#1B1B1B")),
        New(ApplicationThemeSelection.Dark, "dark", "Dark", ApplicationThemeBase.Dark,
            Palette("#202020", "#2B2B2B", "#60CDFF", "#000000", "#FFFFFF")),
        New(ApplicationThemeSelection.Ocean, "ocean", "Ocean", ApplicationThemeBase.Light,
            Palette("#F2F8FC", "#E4F0F7", "#005A9E", "#FFFFFF", "#17212B")),
        New(ApplicationThemeSelection.Forest, "forest", "Forest", ApplicationThemeBase.Light,
            Palette("#F3F8F4", "#E5F0E8", "#1F6F43", "#FFFFFF", "#17231B")),
        New(ApplicationThemeSelection.Sunset, "sunset", "Sunset", ApplicationThemeBase.Light,
            Palette("#FFF7F0", "#FCE9DA", "#8A3F00", "#FFFFFF", "#2A1C12")),
        New(ApplicationThemeSelection.HolidayAutomatic, "holiday-auto", "Holidays — automatic",
            ApplicationThemeBase.System, isAutomatic: true),
        New(ApplicationThemeSelection.WinterLights, "winter-lights", "Winter lights",
            ApplicationThemeBase.Dark,
            Palette("#101A2B", "#18263D", "#2B579A", "#FFFFFF", "#F5F8FC")),
        New(ApplicationThemeSelection.Valentine, "valentine", "Valentine",
            ApplicationThemeBase.Light,
            Palette("#FFF4F7", "#FBE4EB", "#8B1E3F", "#FFFFFF", "#2A1720")),
        New(ApplicationThemeSelection.Spring, "spring", "Spring",
            ApplicationThemeBase.Light,
            Palette("#F5FAF4", "#E6F2E4", "#245C3C", "#FFFFFF", "#17231B")),
        New(ApplicationThemeSelection.Independence, "independence", "Independence Day",
            ApplicationThemeBase.Light,
            Palette("#F4F7FC", "#E5EBF5", "#003F88", "#FFFFFF", "#151D2A")),
        New(ApplicationThemeSelection.Halloween, "halloween", "Halloween",
            ApplicationThemeBase.Dark,
            Palette("#1B1511", "#2B211A", "#7A3100", "#FFFFFF", "#FFF7F0")),
    ];

    public static IReadOnlyList<ApplicationThemeDefinition> All => Definitions;

    public static ApplicationThemeDefinition Get(ApplicationThemeSelection selection) =>
        Definitions.Single(definition => definition.Selection == selection);

    public static bool TryGetById(string? id, out ApplicationThemeDefinition definition)
    {
        definition = Definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.Ordinal))!;
        return definition is not null;
    }

    public static ResolvedApplicationTheme Resolve(
        ApplicationThemeSelection selection,
        DateOnly localDate,
        bool highContrast)
    {
        var requested = Get(selection);
        if (highContrast)
            return new(requested, Get(ApplicationThemeSelection.System), HighContrastOverride: true);
        if (selection != ApplicationThemeSelection.HolidayAutomatic)
            return new(requested, requested, HighContrastOverride: false);

        var holiday = ResolveHoliday(localDate);
        return new(requested, Get(holiday ?? ApplicationThemeSelection.System), HighContrastOverride: false);
    }

    public static ApplicationThemeSelection? ResolveHoliday(DateOnly localDate)
    {
        var month = localDate.Month;
        var day = localDate.Day;
        if (month == 2 && day is >= 10 and <= 14)
            return ApplicationThemeSelection.Valentine;
        if ((month == 3 && day >= 20) || month is 4 or 5)
            return ApplicationThemeSelection.Spring;
        if ((month == 6 && day >= 28) || (month == 7 && day <= 4))
            return ApplicationThemeSelection.Independence;
        if (month == 10 && day is >= 25 and <= 31)
            return ApplicationThemeSelection.Halloween;
        if (month == 12)
            return ApplicationThemeSelection.WinterLights;
        return null;
    }

    private static ApplicationThemeDefinition New(
        ApplicationThemeSelection selection,
        string id,
        string displayName,
        ApplicationThemeBase baseTheme,
        ApplicationThemePalette? palette = null,
        bool isAutomatic = false) =>
        new(selection, id, displayName, baseTheme, palette, isAutomatic);

    private static ApplicationThemePalette Palette(
        string pageBackground,
        string surface,
        string banner,
        string bannerText,
        string bodyText) =>
        new(ThemeColor.Parse(pageBackground), ThemeColor.Parse(surface), ThemeColor.Parse(banner),
            ThemeColor.Parse(bannerText), ThemeColor.Parse(bodyText));
}
