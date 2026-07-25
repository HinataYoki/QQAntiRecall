using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using QQAntiRecall.App.Services;
using QQAntiRecall.App.ViewModels;
using QQAntiRecall.App.Views;
using QQAntiRecall.Core;

namespace QQAntiRecall.App;

/// <summary>
/// Loads application resources and composes the desktop anti-recall workflow.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Loads the compiled application XAML before any view is created.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Creates the desktop main window and injects the core patch and user-interaction services.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(
                    new AntiRecallService(),
                    new AvaloniaUserInteractionService()),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
