using System.Windows;
using DiamondDesktop.Data;

namespace DiamondDesktop;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();

        // Dev and CI convenience only. Real credentials never live in source — this reads the
        // machine's own environment, so nothing secret ships in the binary.
        UserBox.Text = Environment.GetEnvironmentVariable("SOLITAIRE_EMAIL") ?? "";
        PasswordBox.Password = Environment.GetEnvironmentVariable("SOLITAIRE_PASSWORD") ?? "";

        Loaded += async (_, _) =>
        {
            // Only the workspace this desk used last is initialised here. Waking every configured
            // project at startup would cost a round trip each for a session at most one of them can
            // hold — the rest are woken lazily, if the sign-in below actually reaches them.
            var failure = await Db.InitializeAsync();
            if (failure is not null) { Status.Text = failure; return; }

            // A session persisted from last run is still valid — skip straight in.
            if (Db.CurrentUser is not null) DialogResult = true;
            else if (UserBox.Text.Length == 0) UserBox.Focus();
        };
    }

    public bool SignedIn { get; private set; }

    /// <summary>
    /// Signs in against whichever configured project accepts these credentials.
    ///
    /// The two databases are separate installations of the same schema with separate user lists, and
    /// Gotrue answers "invalid credentials" identically for a wrong password and an unknown email —
    /// so there is no way to ask which project a person belongs to. It has to be tried.
    ///
    /// Db.Workspaces puts the last-used project first, so on any desk that has signed in before,
    /// the right one is normally the only one tried.
    /// </summary>
    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SignIn.IsEnabled = false;
        Status.Text = "";

        try
        {
            string email = UserBox.Text.Trim();
            string password = PasswordBox.Password;

            // Where we started. This window is also shown AFTER an idle timeout, over a MainWindow
            // that is still displaying one workspace's data -- so if nothing accepts the
            // credentials, Db has to be put back exactly where it was rather than left wherever
            // the probe finished, or the screen and the connection would disagree.
            var entry = Db.Active;

            var refused = new List<Workspace>();
            string? lastFailure = null;
            int lockedFor = 0;

            foreach (var workspace in Db.Workspaces)
            {
                Db.Use(workspace);

                // Unreachable is not "wrong password". Say so only if nothing else works, so one
                // project being down does not stop a user whose account is on the other.
                if (await Db.InitializeAsync() is { } notReady) { lastFailure = notReady; continue; }

                // Asked before the password goes anywhere. The count lives in Postgres (0025), not in
                // this process — counting here would reset on every restart, which makes "three
                // attempts" mean "three attempts, then close and reopen".
                if (await Repo.LoginLockedForAsync(email) is > 0 and var locked)
                {
                    // Locked HERE does not mean locked everywhere: the same address can exist in
                    // both projects. Remember it and keep looking.
                    lockedFor = Math.Max(lockedFor, locked);
                    continue;
                }

                if (await Db.SignInAsync(email, password) is { } failure)
                {
                    lastFailure = failure;

                    // Refused on the ACCOUNT, not on the password -- this address exists here and is
                    // switched off, which is how one address is kept to one database. Not a failed
                    // attempt: counting it would lock the address out of a project it is not trying
                    // to enter, every time it signs in to the one it does belong to.
                    if (failure != Db.Deactivated && failure != Db.NoProfile) refused.Add(workspace);
                    continue;
                }

                await Repo.ClearLoginFailuresAsync(email);
                SignedIn = true;
                DialogResult = true;
                return;
            }

            // Nobody accepted them. The failure is recorded in each project that was actually asked,
            // because we still cannot tell which one the account belongs to — and a lockout that
            // only counts in the project we happened to try first would not be a lockout at all.
            foreach (var workspace in refused)
            {
                Db.Use(workspace);
                lockedFor = Math.Max(lockedFor, await Repo.NoteLoginFailureAsync(email));
            }

            Db.Use(entry);

            Status.Text = lockedFor > 0
                ? LockedMessage(lockedFor)
                : lastFailure ?? "Wrong email or password.";
        }
        finally
        {
            SignIn.IsEnabled = true;
        }
    }

    /// Says how long, not just "locked" — an unbounded lock reads as a broken account and becomes
    /// a support call.
    private static string LockedMessage(int seconds)
    {
        int minutes = (seconds + 59) / 60;
        return $"Too many failed sign-ins. This account is locked for "
             + (minutes <= 1 ? "another minute." : $"another {minutes} minutes.");
    }
}
