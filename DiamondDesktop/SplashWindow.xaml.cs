using System.Windows;
using System.Windows.Media.Animation;

namespace DiamondDesktop;

/// <summary>
/// The window shown while the app restores a saved session. Presentation only — it starts nothing,
/// waits on nothing and decides nothing; App.OnStartup shows it and dismisses it.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    /// <summary>
    /// Fades out and then hides. Hidden rather than closed so the caller can dismiss it more than
    /// once — the sign-in window may appear, or may be skipped entirely when a session is restored,
    /// and both paths dismiss this.
    /// </summary>
    public void Dismiss()
    {
        if (!IsVisible) return;

        // Snapping away is what makes a splash feel like a stutter rather than a hand-off.
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(280));
        fade.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, fade);
    }
}
