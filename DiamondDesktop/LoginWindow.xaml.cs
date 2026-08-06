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
            var failure = await Db.InitializeAsync();
            if (failure is not null) { Status.Text = failure; return; }

            // A session persisted from last run is still valid — skip straight in.
            if (Db.CurrentUser is not null) DialogResult = true;
            else if (UserBox.Text.Length == 0) UserBox.Focus();
        };
    }

    public bool SignedIn { get; private set; }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SignIn.IsEnabled = false;
        Status.Text = "";

        try
        {
            string email = UserBox.Text.Trim();

            // Asked before the password goes anywhere. The count lives in Postgres (0025), not in
            // this process — counting here would reset on every restart, which makes "three
            // attempts" mean "three attempts, then close and reopen".
            if (await Repo.LoginLockedForAsync(email) is > 0 and var locked)
            {
                Status.Text = LockedMessage(locked);
                return;
            }

            string? failure = await Db.SignInAsync(email, PasswordBox.Password);
            if (failure is not null)
            {
                int nowLocked = await Repo.NoteLoginFailureAsync(email);
                Status.Text = nowLocked > 0 ? LockedMessage(nowLocked) : failure;
                return;
            }

            await Repo.ClearLoginFailuresAsync(email);
            SignedIn = true;
            DialogResult = true;
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
