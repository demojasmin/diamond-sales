using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using DiamondDesktop.Data;

namespace DiamondDesktop;

public partial class App : Application
{
    /// <summary>
    /// Faults already shown, keyed on type and message, so a repeating one is reported once.
    ///
    /// A binding or layout fault fires on every measure pass. Reporting each put a modal in front
    /// of a modal in front of a modal, and the window underneath could not be reached until every
    /// one had been dismissed — which is what "it shows so many times" was.
    /// </summary>
    private readonly HashSet<string> _reported = [];

    /// How many repeats were swallowed. Not shown anywhere yet; it is here so the count exists
    /// when there is somewhere honest to put it.
    private int _repeats;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UseThreeLetterDayNames();

        // Without this, any unhandled exception closes the app with no window, no message and no
        // log — from the user's side it just vanishes, which is indistinguishable from the sign-in
        // having failed. Say what broke instead.
        //
        // AND KEEP GOING. This used to Shutdown() on every one, which turned a transient fault in
        // a control — a stale collection view, a binding evaluated a frame after its source went —
        // into the loss of a half-typed invoice. The UI thread is not corrupt after a
        // NullReferenceException inside a WPF control; the frame it happened on is abandoned and
        // the next one draws fine. Closing the app was the harshest possible answer to a fault the
        // desk could not have caused and cannot avoid.
        //
        // ONCE PER FAULT, not once per occurrence. A layout or binding fault repeats on every
        // measure pass, and the old handler put a modal up for each — a stack of identical boxes
        // that had to be clicked through before the window could be reached at all. The same
        // exception type and message is reported once and then only counted.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;                      // the app stays up; see above

            string fault = $"{args.Exception.GetType().Name}: {args.Exception.Message}";
            if (!_reported.Add(fault))
            {
                _repeats++;
                return;                               // seen it; do not stack another box
            }

            MessageBox.Show(
                $"{fault}\n\n" +
                $"{args.Exception.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}\n\n" +
                "The app is still running and nothing on screen has been lost. If this keeps "
                + "happening, note what you were doing and restart when it suits you.",
                "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        // WPF quits when the last window closes — and during login the login dialog IS the last
        // window. Closing it would shut the app down before MainWindow exists, so hold shutdown
        // open across the handover, then hand ownership to MainWindow.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // The splash covers the one unavoidable wait at startup: LoginWindow restores and refreshes
        // a saved session in its Loaded handler, which is a network round trip. Presentation only —
        // the sign-in sequence below is unchanged, and nothing waits on the splash.
        var splash = new SplashWindow();
        splash.Show();

        try
        {
            // Sign in first: nothing in this app happens un-attributed (AUTH-001).
            // Db.CurrentUser, not login.SignedIn — a session restored from disk skips the sign-in click.
            var login = new LoginWindow();

            // Two ways out, and the splash has to clear on both: the form appears and asks for a
            // password, or a restored session closes the window before it ever renders.
            login.ContentRendered += (_, _) => splash.Dismiss();

            if (login.ShowDialog() != true || Db.CurrentUser is null) { Shutdown(); return; }

            var main = new MainWindow();
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
        }
        finally
        {
            // Close, not Dismiss: by here the app either has its main window or is shutting down,
            // and a splash left alive would hold the process open under OnExplicitShutdown.
            splash.Close();
        }
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
