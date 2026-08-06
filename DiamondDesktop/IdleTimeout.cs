using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DiamondDesktop.Data;

namespace DiamondDesktop;

/// <summary>
/// Ends the session after app_config.session_timeout_min minutes with no keyboard or mouse
/// activity.
///
/// The setting existed for months and did nothing. The only code reading it was
/// DiamondApi/Auth.cs, which stamps an expiry on its own session rows — but this desktop does not
/// talk to that service. It signs in to Supabase, whose JWT lifetime is a project-level Auth
/// setting and which refreshes itself in the background, so an app left open at a shared desk
/// stayed signed in indefinitely no matter what the Settings page said.
///
/// Idle means no INPUT, not no network: a long import or a slow report must not sign the user out
/// while they watch it run, and PreProcessInput fires for the mouse moving over the window during
/// either.
/// </summary>
public sealed class IdleTimeout
{
    private readonly Window _owner;
    private readonly DispatcherTimer _timer;
    private DateTime _lastInput = DateTime.UtcNow;
    private bool _expired;

    /// Checked four times a minute. The alternative — a one-shot timer restarted on every
    /// keystroke — rebuilds a timer thousands of times an hour to answer the same question.
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    public IdleTimeout(Window owner)
    {
        _owner = owner;

        // Application-wide, so activity in any dialog counts too. A modal import dialog is still
        // the user being present.
        InputManager.Current.PreProcessInput += (_, _) => _lastInput = DateTime.UtcNow;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Tick };
        _timer.Tick += OnTick;
        _timer.Start();

        // Re-read when the Settings page saves, so a changed timeout takes effect without a
        // restart — the whole complaint about these settings was that they did nothing.
        Policy.Changed += () => _lastInput = DateTime.UtcNow;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_expired) return;
        if (DateTime.UtcNow - _lastInput < TimeSpan.FromMinutes(Policy.SessionTimeoutMin)) return;

        _expired = true;
        _timer.Stop();

        await Db.SignOutAsync();

        MessageBox.Show(_owner,
            $"Signed out after {Policy.SessionTimeoutMin} minute(s) without activity.\n\n"
            + "Start Solitaire Desk again to sign back in.",
            "Session timed out", MessageBoxButton.OK, MessageBoxImage.Information);

        Application.Current.Shutdown();
    }
}
