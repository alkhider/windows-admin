namespace WinAdmin.Core.Infrastructure;

public static class AppPaths
{
    public static string ProgramData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinAdmin");

    public static string LocalData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinAdmin");

    public static string MachineStamp => Path.Combine(ProgramData, "machine.json");
    public static string WebView2UserData => Path.Combine(LocalData, "WebView2");
}
