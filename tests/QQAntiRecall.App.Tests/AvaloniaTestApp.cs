using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(QQAntiRecall.App.Tests.AvaloniaTestApp))]

namespace QQAntiRecall.App.Tests;

/// <summary>
/// Builds the production Avalonia application with an in-memory renderer for UI tests.
/// </summary>
public static class AvaloniaTestApp
{
    /// <summary>
    /// Creates the headless application while loading the real theme, resources, and views.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
}
