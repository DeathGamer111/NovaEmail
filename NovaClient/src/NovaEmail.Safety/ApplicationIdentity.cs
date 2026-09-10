namespace NovaEmail.Safety;

public static class ApplicationIdentity
{
    public const string ProductName = "Nova Email";
    public const string PackageIdentityName = "NovaEmail.Desktop";
    public const string RegistryRoot = @"Software\NovaEmail";
    public const string DataDirectoryName = "NovaEmail";

    public static string LocalDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        DataDirectoryName);

    public static string FixtureRoot => Path.Combine(LocalDataRoot, "Fixtures");
    public static string RunRoot => Path.Combine(LocalDataRoot, "Runs");
    public static string BrowserProfileRoot => Path.Combine(LocalDataRoot, "BrowserProfile");
    public static string LogRoot => Path.Combine(LocalDataRoot, "Logs");
    public static string SettingsRoot => Path.Combine(LocalDataRoot, "Settings");
    public static string TestConfigurationRoot => Path.Combine(LocalDataRoot, "TestConfiguration");
}
