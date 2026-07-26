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
            string? failure = await Db.SignInAsync(UserBox.Text.Trim(), PasswordBox.Password);
            if (failure is not null) { Status.Text = failure; return; }

            SignedIn = true;
            DialogResult = true;
        }
        finally
        {
            SignIn.IsEnabled = true;
        }
    }
}
