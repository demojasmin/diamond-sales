using System.Windows;

namespace DiamondDesktop;

public partial class LoginWindow : Window
{
    public LoginWindow() => InitializeComponent();

    public Api? Api { get; private set; }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SignIn.IsEnabled = false;
        Status.Text = "";

        try
        {
            var api = new Api(ServerBox.Text.Trim());
            string? error = await api.LoginAsync(UserBox.Text.Trim(), PasswordBox.Password);
            if (error is not null) { Status.Text = error; return; }

            await api.LoadCatalogueAsync();
            Api = api;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Status.Text = $"Cannot reach the server: {ex.Message}";   // is DiamondApi running?
        }
        finally
        {
            SignIn.IsEnabled = true;
        }
    }
}
