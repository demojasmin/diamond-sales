using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace DiamondDesktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UseThreeLetterDayNames();

        // WPF quits when the last window closes — and during login the login dialog IS the last
        // window. Closing it would shut the app down before MainWindow exists, so hold shutdown
        // open across the handover, then hand ownership to MainWindow.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Sign in first: nothing in this app happens un-attributed (AUTH-001).
        var login = new LoginWindow();
        if (login.ShowDialog() != true || login.Api is null) { Shutdown(); return; }

        var main = new MainWindow(login.Api);
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    /// <summary>
    /// WPF's Calendar takes its column headings from the culture's ShortestDayNames — one letter,
    /// so Tuesday and Thursday both read "T". Widening them to three letters is the only way to
    /// get Sun/Mon/Tue without retemplating CalendarItem wholesale.
    /// </summary>
    private static void UseThreeLetterDayNames()
    {
        var culture = (CultureInfo)CultureInfo.CurrentCulture.Clone();
        culture.DateTimeFormat.ShortestDayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;

        // WPF resolves dates through the element Language, not the thread culture.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }
}
