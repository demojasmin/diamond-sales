using System.Windows;
using System.Windows.Controls;
using SolitaireDesk.Services;
using SolitaireDesk.Views;

namespace SolitaireDesk;

public partial class MainWindow : Window
{
    // Views are created lazily and reused. This is navigation ONLY — no business logic.
    private DashboardView? _dashboard;
    private SalesListView? _salesList;
    private InventoryView? _inventory;
    private ReceivablesView? _receivables;
    private MastersView? _masters;
    private SettingsView? _settings;

    public MainWindow()
    {
        InitializeComponent();
        NavDashboard.IsChecked = true; // default screen
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (ContentHost is null || sender is not RadioButton rb) return;

        switch (rb.Tag as string)
        {
            case "Dashboard":
                Show(_dashboard ??= new DashboardView(), "Dashboard", "Trading position at a glance");
                break;
            case "Sales":
                Show(_salesList ??= new SalesListView(), "Sales", "Invoices · last 90 days");
                break;
            case "Inventory":
                Show(_inventory ??= new InventoryView(), "Inventory", "Grade × sieve-size stock position");
                break;
            case "Receivables":
                Show(_receivables ??= new ReceivablesView(), "Receivables", "Ageing and collections");
                break;
            case "Masters":
                Show(_masters ??= new MastersView(), "Masters", "Grades, buyers, brokers, price list");
                break;
            case "Settings":
                Show(_settings ??= new SettingsView(), "Settings", "Appearance, company, audit");
                break;
        }
    }

    private void Show(UserControl view, string title, string sub)
    {
        ContentHost.Content = view;
        ScreenTitle.Text = title;
        ScreenSub.Text = sub;
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e) => ThemeManager.Toggle();
}
