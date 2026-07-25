using Avalonia.Controls;

namespace QQAntiRecall.App.Views;

public partial class MainWindow : Window
{
    private const double CompactBreakpoint = 820;

    /// <summary>
    /// Initializes the window and keeps its responsive class synchronized with the client width.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        UpdateResponsiveClass(Width);
    }

    /// <summary>
    /// Applies responsive styling after the window client area changes size.
    /// </summary>
    /// <param name="sender">Window that raised the size-change event.</param>
    /// <param name="e">Event data containing the new client size.</param>
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveClass(e.NewSize.Width);
    }

    /// <summary>
    /// Marks widths below the compact breakpoint for tighter page spacing.
    /// </summary>
    /// <param name="width">The current client width in device-independent pixels.</param>
    private void UpdateResponsiveClass(double width)
    {
        Classes.Set("compact", width < CompactBreakpoint);
    }
}
