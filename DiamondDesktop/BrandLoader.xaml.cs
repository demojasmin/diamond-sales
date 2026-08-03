using System.Windows;
using System.Windows.Controls;

namespace DiamondDesktop;

/// <summary>
/// The application's one loading mark: diamond, turning arcs, and "Loading" with three dots.
/// Presentation only — it starts nothing and knows nothing about what is being fetched.
/// </summary>
public partial class BrandLoader : UserControl
{
    public BrandLoader() => InitializeComponent();

    /// <summary>
    /// Whether the Solitaire Desk wordmark is shown beneath the mark. True on the startup splash,
    /// where the app is introducing itself; false on a page loader, where repeating the product
    /// name every time a grid refreshes is noise rather than branding.
    /// </summary>
    public static readonly DependencyProperty ShowWordmarkProperty =
        DependencyProperty.Register(nameof(ShowWordmark), typeof(bool), typeof(BrandLoader),
            new PropertyMetadata(true, OnShowWordmarkChanged));

    public bool ShowWordmark
    {
        get => (bool)GetValue(ShowWordmarkProperty);
        set => SetValue(ShowWordmarkProperty, value);
    }

    private static void OnShowWordmarkChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        => ((BrandLoader)o).Wordmark.Visibility =
            (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
}
