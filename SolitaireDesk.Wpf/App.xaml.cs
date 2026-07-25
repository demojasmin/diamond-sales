using System.Windows;

namespace SolitaireDesk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Ensure the theme service knows the initial (Light) theme merged in App.xaml.
        Services.ThemeManager.Initialize(this);
    }
}
