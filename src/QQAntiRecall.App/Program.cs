using Avalonia;
using System;

namespace QQAntiRecall.App;

/// <summary>
/// Hosts the classic desktop process and configures the shared Avalonia application builder.
/// </summary>
sealed class Program
{
    /// <summary>
    /// Starts the platform-specific desktop lifetime; no Avalonia API may run before this entry point.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the desktop lifetime.</param>
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// Builds the platform-detected application configuration shared by runtime and designer hosts.
    /// </summary>
    /// <returns>An unstarted Avalonia application builder.</returns>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
