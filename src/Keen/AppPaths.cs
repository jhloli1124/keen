namespace Keen;

internal static class AppPaths
{
    private static readonly string AppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Keen"
    );

    public static string Vault => Path.Combine(AppData, "vault");
    public static string LogDir => Path.Combine(AppData, "logs");
    public static string ConfigFile => Path.Combine(AppData, "config.json");

    public static void Ensure()
    {
        Directory.CreateDirectory(AppData);
        Directory.CreateDirectory(LogDir);
    }
}
