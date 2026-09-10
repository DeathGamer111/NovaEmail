using NovaEmail.Rendering;

namespace NovaEmail.Rendering.Tests;

public sealed class ApplicationThemeTests
{
    [Fact]
    public void StableIdsAreUniqueAndEveryDecorativePaletteMeetsAaTextContrast()
    {
        Assert.Equal(ApplicationThemeCatalog.All.Count,
            ApplicationThemeCatalog.All.Select(theme => theme.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var theme in ApplicationThemeCatalog.All.Where(theme => theme.Palette is not null))
        {
            var palette = Assert.IsType<ApplicationThemePalette>(theme.Palette);
            Assert.True(palette.BannerText.ContrastRatio(palette.Banner) >= 4.5d,
                $"{theme.Id} banner text does not meet WCAG AA contrast.");
            Assert.True(palette.BodyText.ContrastRatio(palette.PageBackground) >= 4.5d,
                $"{theme.Id} body text does not meet WCAG AA contrast.");
            Assert.True(palette.BodyText.ContrastRatio(palette.Surface) >= 4.5d,
                $"{theme.Id} surface text does not meet WCAG AA contrast.");
        }
    }

    [Theory]
    [InlineData(2026, 2, 10, ApplicationThemeSelection.Valentine)]
    [InlineData(2026, 2, 14, ApplicationThemeSelection.Valentine)]
    [InlineData(2026, 3, 20, ApplicationThemeSelection.Spring)]
    [InlineData(2026, 5, 31, ApplicationThemeSelection.Spring)]
    [InlineData(2026, 6, 28, ApplicationThemeSelection.Independence)]
    [InlineData(2026, 7, 4, ApplicationThemeSelection.Independence)]
    [InlineData(2026, 10, 25, ApplicationThemeSelection.Halloween)]
    [InlineData(2026, 10, 31, ApplicationThemeSelection.Halloween)]
    [InlineData(2026, 12, 1, ApplicationThemeSelection.WinterLights)]
    [InlineData(2026, 12, 31, ApplicationThemeSelection.WinterLights)]
    public void HolidayAutomaticUsesDocumentedInclusiveLocalDateWindows(
        int year,
        int month,
        int day,
        ApplicationThemeSelection expected)
    {
        var resolved = ApplicationThemeCatalog.Resolve(
            ApplicationThemeSelection.HolidayAutomatic, new DateOnly(year, month, day),
            highContrast: false);

        Assert.Equal(ApplicationThemeSelection.HolidayAutomatic, resolved.Requested.Selection);
        Assert.Equal(expected, resolved.Effective.Selection);
        Assert.False(resolved.HighContrastOverride);
    }

    [Theory]
    [InlineData(2026, 2, 9)]
    [InlineData(2026, 2, 15)]
    [InlineData(2026, 6, 1)]
    [InlineData(2026, 7, 5)]
    [InlineData(2026, 10, 24)]
    [InlineData(2026, 11, 1)]
    public void HolidayAutomaticFallsBackToWindowsOutsideHolidayWindows(int year, int month, int day)
    {
        var resolved = ApplicationThemeCatalog.Resolve(
            ApplicationThemeSelection.HolidayAutomatic, new DateOnly(year, month, day),
            highContrast: false);

        Assert.Equal(ApplicationThemeSelection.System, resolved.Effective.Selection);
    }

    [Fact]
    public void HighContrastOverridesEveryDecorativeSelectionWithoutChangingPreference()
    {
        foreach (var selection in ApplicationThemeCatalog.All.Select(theme => theme.Selection))
        {
            var resolved = ApplicationThemeCatalog.Resolve(
                selection, new DateOnly(2026, 10, 31), highContrast: true);

            Assert.Equal(selection, resolved.Requested.Selection);
            Assert.Equal(ApplicationThemeSelection.System, resolved.Effective.Selection);
            Assert.True(resolved.HighContrastOverride);
        }
    }

    [Fact]
    public void PreferenceRoundTripsThroughBoundedAtomicLocalFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "settings", "theme.json");
        var store = new ThemePreferenceStore(path);

        var absent = store.Load();
        Assert.Equal(ApplicationThemeSelection.System, absent.Selection);
        Assert.False(absent.UsedFallback);

        store.Save(ApplicationThemeSelection.Forest);
        var loaded = store.Load();

        Assert.Equal(ApplicationThemeSelection.Forest, loaded.Selection);
        Assert.False(loaded.UsedFallback);
        Assert.Null(loaded.Diagnostic);
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(path)!), file =>
            file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":999,\"selection\":\"dark\"}")]
    [InlineData("{\"schemaVersion\":1,\"selection\":\"unknown\"}")]
    [InlineData("not json")]
    public void InvalidPreferenceFailsClosedToWindowsWithoutOverwritingEvidence(string content)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "theme.json");
        File.WriteAllText(path, content);
        var store = new ThemePreferenceStore(path);

        var loaded = store.Load();

        Assert.Equal(ApplicationThemeSelection.System, loaded.Selection);
        Assert.True(loaded.UsedFallback);
        Assert.NotNull(loaded.Diagnostic);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void OversizedPreferenceFailsClosedWithoutParsingOrMutation()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "theme.json");
        var content = new string('x', ThemePreferenceStore.MaximumSettingsBytes + 1);
        File.WriteAllText(path, content);

        var loaded = new ThemePreferenceStore(path).Load();

        Assert.Equal(ApplicationThemeSelection.System, loaded.Selection);
        Assert.True(loaded.UsedFallback);
        Assert.Equal(content.Length, new FileInfo(path).Length);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"novaemail-theme-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
