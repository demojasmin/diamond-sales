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
                             "Styles/AppOverrides.xaml", "Styles/Components.xaml", "Styles/Dashboard.xaml",
                             "Styles/Audit.xaml",
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

                // The printed bill. It is the one artefact a buyer physically receives and it had
                // no coverage at all: the document was built inside a method that opens a
                // PrintDialog first, so nothing could be checked without a printer attached.
                results.AddRange(BillChecks());
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

    /// <summary>
    /// Builds a real bill and reads the text back. Every figure here reaches a customer, so the
    /// checks are about what must appear rather than about layout: the invoice number, the buyer,
    /// each line, the totals — and that a missing cost basis prints as words rather than as a zero,
    /// which would read as a 100% margin on a parcel whose purchase price was never recorded.
    /// </summary>
    private static List<(string, bool, string?)> BillChecks()
    {
        var invoice = new DiamondDesktop.Data.VInvoice
        {
            InvoiceNo = "INV-2026-00004", InvoiceDate = new DateOnly(2026, 8, 5),
            BuyerName = "QUEST DIAMOND", DocType = "BILL", TermsDays = 45,
            DueDate = new DateOnly(2026, 9, 19), BrokerName = "JITESH SHAH", BrokerPct = 1m,
            AmountTotal = 139864.73m, CaratsSold = 2.30m, Received = 50000m,
            Outstanding = 89864.73m, BlendedRate = 60810.75m, BrokerPayable = 1412.78m,
        };

        var lines = new List<DiamondDesktop.Data.VSalesLine>
        {
            new()
            {
                GradeCode = "NO II", SizeCode = "-6.5", GrossWeightCt = 2.30m, SelectionCt = 2.30m,
                RejectionCt = 0m, PricePerCt = 63000m, ExRate = 1m, Less1Pct = 2.5m, Less2Pct = 0m,
                Amount = 139864.73m, Remark = "sorted parcel",
            },
        };

        var doc = DiamondDesktop.Reports.BuildInvoice(invoice, lines, "Solitaire Desk");
        string text = new System.Windows.Documents.TextRange(
            doc.ContentStart, doc.ContentEnd).Text;

        return
        [
            ("bill · names the company and the invoice",
             text.Contains("Solitaire Desk") && text.Contains("INV-2026-00004"), null),
            ("bill · names the buyer and the due date",
             text.Contains("QUEST DIAMOND") && text.Contains("19-09-2026"), null),
            ("bill · prints the line, its grade and its size",
             text.Contains("NO II") && text.Contains("-6.5"), null),
            ("bill · prints the per-line remark, which no screen shows",
             text.Contains("sorted parcel"), null),
            ("bill · the amount matches the invoice total",
             text.Contains("139,864.73"), null),
            ("bill · outstanding is on it, not just the total",
             text.Contains("89,864.73"), null),
            ("bill · an unknown cost prints as words, never as a zero margin",
             text.Contains("Cost not available") && !text.Contains("Margin\t0.00"), null),
            ("bill · says the broker cut is already deducted",
             text.Contains("already deducted"), null),
        ];
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
