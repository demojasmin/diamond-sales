using System.ComponentModel;
using System.Windows;
using DiamondDesktop.Data;

namespace DiamondDesktop;

/// <summary>
/// Modal "this is running" window for work that must not be interrupted half way.
///
/// The import deletes a dataset and then rebuilds it. Leaving the app usable during that would let
/// someone start a second import, or open Invoices and read a table that is momentarily empty. So
/// the owner window is disabled for the duration and this cannot be closed — there is no close
/// button, and Alt+F4 is refused.
///
/// It reports progress; it does not run anything. The caller owns the work and closes this when the
/// work ends, whether that end was success or failure.
/// </summary>
public partial class AppProgressDialog : Window
{
    private bool _mayClose;
    private Window? _owner;

    private AppProgressDialog() => InitializeComponent();

    /// <summary>Shows the dialog and disables the owner. Call <see cref="Finish"/> when done.</summary>
    public static AppProgressDialog Start(Window owner, string headline)
    {
        var dialog = new AppProgressDialog { Owner = owner, _owner = owner };
        dialog.Headline.Text = headline;

        // Disabling the owner is what actually stops every button, menu and grid on the page —
        // more reliable than remembering to disable each control, and it cannot drift as the
        // window grows new ones.
        owner.IsEnabled = false;
        dialog.Show();
        return dialog;
    }

    /// <summary>An <see cref="IProgress{T}"/> that drives this dialog on the UI thread.</summary>
    public IProgress<ImportProgress> Progress => new Progress<ImportProgress>(Report);

    private void Report(ImportProgress p)
    {
        Detail.Text = p.Message;

        if (p.Total > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = Math.Clamp(p.Done * 100.0 / p.Total, 0, 100);
            Counter.Text = $"{p.Done:N0} of {p.Total:N0}";
            Counter.Visibility = Visibility.Visible;
        }
        else
        {
            // A step with nothing to count — reading the catalogue, checking buyers. A bar sitting
            // at zero reads as stuck; an indeterminate one reads as working.
            Bar.IsIndeterminate = true;
            Counter.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Re-enables the owner and closes. Safe to call more than once.</summary>
    public void Finish()
    {
        if (_owner is not null)
        {
            _owner.IsEnabled = true;
            // Without this the owner is enabled but not focused, and the next dialog can open
            // behind the main window.
            _owner.Activate();
        }
        _mayClose = true;
        Close();
    }

    private void Window_Closing(object sender, CancelEventArgs e) => e.Cancel = !_mayClose;
}
