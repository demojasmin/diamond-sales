using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace DiamondCalc.Tests;

/// <summary>
/// Builds the two import dialogs for real — App resources loaded, XAML parsed, layout run — and
/// reports what came out. Nothing is shown on screen.
///
/// Worth having because the failure it catches has already happened once here: XAML that compiles
/// cleanly can still throw at load on a StaticResource that is not in scope, and the first sign of
/// it was the whole window vanishing.
/// </summary>
public static class DialogProbe
{
    public static List<(string Name, bool Ok, string? Detail)> Run()
    {
        var results = new List<(string, bool, string?)>();
        var thread = new Thread(() =>
        {
            try
            {
                // The dialogs resolve UiFont, RadiusCard, AppButton and friends through the
                // application's merged dictionaries, so those must exist before one is created.
                var app = new Application();
                foreach (string part in new[]
                         {
                             "Themes/LightTheme.xaml", "Themes/Typography.xaml",
                             "Styles/Buttons.xaml", "Styles/Inputs.xaml",
                             "Styles/Widgets.xaml", "Styles/DataGridStyles.xaml",
                             "Styles/AppOverrides.xaml", "Styles/Components.xaml",
                         })
                {
                    try
                    {
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        {
                            Source = new Uri($"pack://application:,,,/DiamondDesktop;component/{part}"),
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(($"dialogs · resource dictionary {part} loads", false,
                            ex.Message));
                    }
                }

                var confirm = Make("Replace imported sales data", warning: true);
                results.Add(("dialogs · the confirmation dialog loads", confirm is not null, null));
                results.Add(("dialogs · its counts are laid out in a grid, not a paragraph",
                    Descendants(confirm!).OfType<Grid>().Any(g => g.RowDefinitions.Count >= 4),
                    "expected at least 4 fact rows"));
                results.Add(("dialogs · it offers two buttons",
                    Descendants(confirm!).OfType<Button>()
                        .Count(b => b.Visibility == Visibility.Visible) == 2,
                    $"{Descendants(confirm!).OfType<Button>().Count(b => b.Visibility == Visibility.Visible)} visible"));

                var info = Make("Import complete", warning: false);
                results.Add(("dialogs · the completion dialog loads", info is not null, null));
                results.Add(("dialogs · it offers exactly one button",
                    Descendants(info!).OfType<Button>()
                        .Count(b => b.Visibility == Visibility.Visible) == 1,
                    $"{Descendants(info!).OfType<Button>().Count(b => b.Visibility == Visibility.Visible)} visible"));

                // The progress dialog: it must refuse to close, and its bar must switch between
                // indeterminate and a real percentage as the importer reports counts.
                var progressType = typeof(DiamondDesktop.AppProgressDialog);
                var ctor = progressType.GetConstructor(
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                    Type.EmptyTypes)!;
                var progress = (Window)ctor.Invoke(null);
                progress.Measure(new Size(600, 600));

                var report = progressType.GetMethod("Report",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var bar = (System.Windows.Controls.ProgressBar)progressType
                    .GetField("Bar", System.Reflection.BindingFlags.NonPublic
                                     | System.Reflection.BindingFlags.Instance)!.GetValue(progress)!;

                report.Invoke(progress, [new DiamondDesktop.Data.ImportProgress("Reading…")]);
                results.Add(("progress · a step with nothing to count shows an indeterminate bar",
                    bar.IsIndeterminate, null));

                report.Invoke(progress,
                    [new DiamondDesktop.Data.ImportProgress("Importing…", 717, 1434)]);
                results.Add(("progress · a countable step fills the bar to the right percentage",
                    !bar.IsIndeterminate && Math.Abs(bar.Value - 50) < 0.2, $"value {bar.Value:F1}"));

                var closing = new CancelEventArgs();
                progressType.GetMethod("Window_Closing",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(progress, [progress, closing]);
                results.Add(("progress · the dialog refuses to be closed mid-import",
                    closing.Cancel, null));
            }
            catch (Exception ex)
            {
                var real = ex is System.Reflection.TargetInvocationException { InnerException: { } i }
                    ? i : ex;
                results.Add(("dialogs · load without throwing", false,
                    $"{real.GetType().Name}: {real.Message}"));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));
        return results;
    }

    /// Reaches the private Build through the public entry points' own shape: constructing the
    /// window and populating it is what is under test, so it goes through reflection rather than a
    /// test-only overload that would not be the code users hit.
    // No owner: WPF refuses to own a window that has never been shown, and centring on a
    // parent is not what is under test here.
    private static Window? Make(string title, bool warning)
    {
        var type = typeof(DiamondDesktop.AppDialog);
        var build = type.GetMethod("Build",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        (string, string)[] facts =
        [
            ("Invoices", "1,434"), ("Lines", "1,437"),
            ("Receipts", "1,055"), ("Dates", "01 Aug 2024 — 31 Jul 2026"),
        ];

        var dialog = (Window)build.Invoke(null, [
            null,
            warning ? DiamondDesktop.AppDialog.Tone.Warning : DiamondDesktop.AppDialog.Tone.Info,
            title,
            title,
            "Sale File Sample.xlsx",
            facts,
            warning ? "This will DELETE the 1,366 previously imported invoice(s)." : null,
            "Worth knowing",
            new[] { "3 Sr. number(s) became separate invoices." },
            warning ? "Import now" : "Done",
            warning ? "Cancel" : null,
            null,
        ])!;

        dialog.Measure(new Size(800, 800));
        dialog.Arrange(new Rect(0, 0, 560, 600));
        return dialog;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        if (n == 0 && root is ContentControl { Content: DependencyObject inner })
        {
            yield return inner;
            foreach (var d in Descendants(inner)) yield return d;
            yield break;
        }
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
