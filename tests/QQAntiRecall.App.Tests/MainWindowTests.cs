using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using QQAntiRecall.App.ViewModels;
using QQAntiRecall.App.Views;
using QQAntiRecall.Core;

namespace QQAntiRecall.App.Tests;

public sealed class MainWindowTests
{
    /// <summary>
    /// Verifies the full production XAML renders and exposes the primary workflow controls.
    /// </summary>
    [AvaloniaFact]
    public void MainWindow_RendersPrimaryWorkflow()
    {
        var window = CreateWindow(960, 720, CreatePopulatedViewModel());
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("QQ 防撤回", window.Title);
            Assert.NotNull(window.FindControl<Border>("StatusRail"));
            Assert.NotNull(window.FindControl<TextBox>("InstallPathBox"));
            Assert.NotNull(window.FindControl<Button>("BrowseButton"));
            Assert.NotNull(window.FindControl<Button>("ScanButton"));
            Assert.NotNull(window.FindControl<Button>("InstallButton"));
            Assert.NotNull(window.FindControl<Button>("RestoreButton"));
            Assert.NotNull(window.FindControl<Button>("CloseQqInstallAndRestartButton"));
            Assert.NotNull(window.FindControl<Button>("OpenBackupDirectoryButton"));
            Assert.NotNull(window.FindControl<Button>("CleanupBackupsButton"));
            Button restoreButton = window.FindControl<Button>("RestoreButton")!;
            Assert.Equal(HorizontalAlignment.Center, restoreButton.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, restoreButton.VerticalContentAlignment);

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            using var png = new MemoryStream();
            frame.Save(png, PngBitmapEncoderOptions.Default);
            Assert.True(png.Length > 1_000, "Rendered frame should contain non-empty UI pixels.");

            var screenshotPath = Environment.GetEnvironmentVariable("QQAR_SCREENSHOT_PATH");
            if (!string.IsNullOrWhiteSpace(screenshotPath))
            {
                var directory = Path.GetDirectoryName(screenshotPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                frame.Save(screenshotPath, PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Verifies compact spacing activates at the supported minimum width without hiding commands.
    /// </summary>
    [AvaloniaFact]
    public void MainWindow_AtCompactWidth_KeepsActionsVisible()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var window = CreateWindow(760, 620);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("compact", window.Classes);
            Assert.True(window.FindControl<Button>("InstallButton")!.IsVisible);
            Assert.True(window.FindControl<Button>("RestoreButton")!.IsVisible);
            Assert.True(window.FindControl<Button>("OpenBackupDirectoryButton")!.IsVisible);
            Assert.True(window.FindControl<Button>("CleanupBackupsButton")!.IsVisible);
            Assert.True(window.FindControl<TextBox>("InstallPathBox")!.Bounds.Width > 0);
            Assert.True(window.FindControl<Border>("StatusRail")!.Bounds.Width > 0);

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var screenshotPath = Environment.GetEnvironmentVariable("QQAR_COMPACT_SCREENSHOT_PATH");
            if (!string.IsNullOrWhiteSpace(screenshotPath))
            {
                var directory = Path.GetDirectoryName(screenshotPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                frame.Save(screenshotPath, PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
    }

    /// <summary>
    /// Verifies a detected QQ process swaps the standard install action for the close-install-restart action.
    /// </summary>
    [AvaloniaFact]
    public void MainWindow_WhenQqIsRunning_ShowsAutomatedRestartAction()
    {
        MainViewModel viewModel = new()
        {
            IsQqRunning = true,
            QqProcessStatus = "正在运行",
            StatusTitle = "QQ 正在运行",
            StatusDetail = "目标版本已验证，可关闭 QQ 后安装补丁并重新启动。",
            IsStatusWarning = true,
        };
        var window = CreateWindow(960, 720, viewModel);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.False(window.FindControl<Button>("InstallButton")!.IsVisible);
            Assert.True(window.FindControl<Button>("CloseQqInstallAndRestartButton")!.IsVisible);

            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var screenshotPath = Environment.GetEnvironmentVariable("QQAR_RUNNING_SCREENSHOT_PATH");
            if (!string.IsNullOrWhiteSpace(screenshotPath))
            {
                var directory = Path.GetDirectoryName(screenshotPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                frame.Save(screenshotPath, PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Creates an installed design snapshot used to verify version-row density and metadata rendering.
    /// </summary>
    /// <returns>A populated ViewModel that performs no filesystem or process access.</returns>
    private static MainViewModel CreatePopulatedViewModel()
    {
        const string installRoot = @"D:\Tencent\QQNT";
        MainViewModel viewModel = new()
        {
            InstallPath = installRoot,
            StatusTitle = "防撤回已启用",
            StatusDetail = "防撤回已安装，备份编号：20260726T051500Z。",
            IsStatusSuccess = true,
            QqProcessStatus = "未运行",
            VersionStatus = "已发现 1 个版本",
            BackupStatus = "备份可用",
        };
        viewModel.Targets.Add(new TargetItemViewModel(new TargetScanResult(
            "9.9.33-51728",
            Path.Combine(installRoot, "versions", "9.9.33-51728", "resources", "app", "wrapper.node"),
            TargetPatchState.Installed,
            "41B4298F80A0874B9B1A63F05F59B7DBB1E6F1B127C77C30436F5B42EACD0D1A",
            [
                new PatchSignatureStatus("私聊撤回", 0, 1),
                new PatchSignatureStatus("群聊撤回", 0, 1),
                new PatchSignatureStatus("撤回提示", 0, 1),
            ],
            "三个补丁签名均唯一匹配。")));
        return viewModel;
    }

    /// <summary>
    /// Creates a design-state window at a deterministic client size without touching QQ files.
    /// </summary>
    /// <param name="width">Requested client width in device-independent pixels.</param>
    /// <param name="height">Requested client height in device-independent pixels.</param>
    /// <param name="viewModel">Optional preconfigured design state.</param>
    /// <returns>A main window ready for headless rendering.</returns>
    private static MainWindow CreateWindow(
        double width,
        double height,
        MainViewModel? viewModel = null) =>
        new()
        {
            Width = width,
            Height = height,
            DataContext = viewModel ?? new MainViewModel(),
        };
}
