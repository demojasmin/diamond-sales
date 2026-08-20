using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

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
    /// The Width set on DocTypePicker in MainWindow.xaml. Here so the check below has something
    /// to hold the measurement against; the two are kept in step by hand and by that check.
    private const double TypePickerWidth = 150;

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

                // ── the slim scrollbar, on both axes ───────────────────────────────
                // Width was set on the style unconditionally, so a HORIZONTAL bar inherited
                // it and was pinned to eleven pixels wide: laid out, working, and impossible
                // to see or grab. Every page that opts into this style and scrolls sideways
                // had the same invisible bar, and it read as "the ScrollViewer is ignoring
                // HorizontalScrollBarVisibility" for as long as nobody looked at the style.
                //
                // Checked against the resolved Style rather than the file, so a setter
                // arriving from a BasedOn parent later is caught too.
                if (app.TryFindResource("SlimScrollBar") is Style slim)
                {
                    bool widthAlways = slim.Setters.OfType<Setter>()
                        .Any(x => x.Property == FrameworkElement.WidthProperty);

                    results.Add(("scrollbar · the slim style pins no width across both axes",
                        !widthAlways,
                        "a horizontal bar inherits it and renders 11px wide"));

                    // Built for real on each axis: a Style trigger is only worth anything if
                    // it actually fires, and Orientation is set by the ScrollViewer template
                    // rather than by whoever wrote the markup.
                    foreach (var (axis, orientation) in new[]
                             {
                                 ("vertical", Orientation.Vertical),
                                 ("horizontal", Orientation.Horizontal),
                             })
                    {
                        var bar0 = new System.Windows.Controls.Primitives.ScrollBar
                        {
                            Style = slim, Orientation = orientation,
                            Minimum = 0, Maximum = 100, ViewportSize = 20, Value = 0,
                        };
                        bar0.Measure(new Size(400, 400));
                        bar0.Arrange(new Rect(0, 0, 400, 400));
                        bar0.UpdateLayout();

                        // The bar must be slim ACROSS itself and free to stretch ALONG itself.
                        double across = orientation == Orientation.Vertical
                            ? bar0.ActualWidth : bar0.ActualHeight;
                        double along = orientation == Orientation.Vertical
                            ? bar0.ActualHeight : bar0.ActualWidth;

                        results.Add(($"scrollbar · a {axis} bar is 11px across", across == 11d,
                            $"{across}px"));
                        results.Add(($"scrollbar · a {axis} bar stretches along its length",
                            along > 100d, $"{along}px"));
                    }
                }
                else
                {
                    results.Add(("scrollbar · the slim style is in the dictionaries", false,
                        "SlimScrollBar did not resolve"));
                }

                // ── the document-type picker is wide enough for its longest entry ──
                // "BILL" fitted an 80px box; "WITHOUT BILL" did not, and the closed box showed
                // "WITH(". A ComboBox does not widen itself for an item it is not currently
                // showing, so the width has to be set for the LONGEST one and checked here --
                // otherwise a fifth type added later clips in exactly the same way, and only on
                // the invoices that use it.
                //
                // Measured through the app's own AppComboBox style, which MainWindow applies to
                // every ComboBox implicitly, so this is the real chrome and not an estimate.
                if (app.TryFindResource("AppComboBox") is Style combo)
                {
                    double widest = 0;
                    string worst = "";

                    foreach (string docType in DiamondDesktop.Catalogue.DocTypes)
                    {
                        var box = new ComboBox
                        {
                            Style = combo,
                            ItemsSource = DiamondDesktop.Catalogue.DocTypes,
                            SelectedItem = docType,
                        };
                        box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        if (box.DesiredSize.Width > widest)
                        {
                            widest = box.DesiredSize.Width;
                            worst = docType;
                        }
                    }

                    // Mirrors Width on DocTypePicker in MainWindow.xaml. If this fails, the
                    // detail names the figure to raise it to.
                    const double declared = TypePickerWidth;

                    results.Add(("doctype · the Type picker fits its longest entry",
                        widest <= declared,
                        $"\"{worst}\" needs {widest:F0}px, the picker is {declared:F0}px"));
                }
                else
                {
                    results.Add(("doctype · the AppComboBox style is in the dictionaries", false,
                        "AppComboBox did not resolve"));
                }

                // ── the date pickers all pop the same calendar ─────────────────────
                // Sales entry and the Dashboard were reported as showing two different calendars.
                // Both carry Style="{StaticResource AppDatePicker}", and that style sets
                // CalendarStyle -- which is load-bearing: DatePicker binds its Calendar's Style to
                // that property, a binding is a local value, and a local value suppresses implicit
                // style lookup. So if one picker ever loses the style, its popup silently falls
                // back to the stock Windows calendar and nothing else says so.
                //
                // Compared as OBJECTS, not by name: this is the one property that decides it.
                if (app.TryFindResource("AppDatePicker") is Style datePicker)
                {
                    // Built the way each page declares it, differences and all: the Dashboard sets
                    // an explicit Height, Sales entry does not.
                    var salesEntry = new DatePicker { Style = datePicker, Width = 150 };
                    var dashboard = new DatePicker { Style = datePicker, Width = 120, Height = 34 };

                    foreach (var picker in new[] { salesEntry, dashboard })
                    {
                        picker.Measure(new Size(400, 400));
                        picker.Arrange(new Rect(0, 0, 400, 400));
                        picker.UpdateLayout();
                    }

                    results.Add(("calendar · both pages pop the same calendar style",
                        salesEntry.CalendarStyle is not null
                        && ReferenceEquals(salesEntry.CalendarStyle, dashboard.CalendarStyle),
                        salesEntry.CalendarStyle is null
                            ? "CalendarStyle is null - the popup would fall back to the stock calendar"
                            : $"sales {salesEntry.CalendarStyle?.GetHashCode()}, "
                              + $"dash {dashboard.CalendarStyle?.GetHashCode()}"));

                    results.Add(("calendar · and the same picker chrome around it",
                        ReferenceEquals(salesEntry.Template, dashboard.Template), null));

                    // The chrome the reported difference was about: rounded corners and the popup
                    // border come off AppCalendar, so it has to be the style that is resolving.
                    results.Add(("calendar · which is AppCalendar, not the stock one",
                        ReferenceEquals(salesEntry.CalendarStyle, app.TryFindResource("AppCalendar")),
                        "CalendarStyle did not resolve to AppCalendar"));

                    // Height is the one thing the two declarations genuinely differ on, and it is
                    // the FIELD not the popup. Pinned so it stays a deliberate difference.
                    // The property is one thing; the Calendar the popup actually builds is another,
                    // and it is the one on screen. DatePicker binds its Calendar's Style to
                    // CalendarStyle -- a binding, so a local value, so implicit lookup can never
                    // reach it. Pulled out of the template and compared as the objects they are.
                    Calendar? CalendarInside(DatePicker picker)
                    {
                        picker.ApplyTemplate();
                        return picker.Template?.FindName("PART_Popup", picker) is Popup pop
                            ? pop.Child as Calendar
                            : null;
                    }

                    var salesCal = CalendarInside(salesEntry);
                    var dashCal = CalendarInside(dashboard);

                    results.Add(("calendar · each picker really builds a calendar in its popup",
                        salesCal is not null && dashCal is not null,
                        $"sales {(salesCal is null ? "none" : "ok")}, dash {(dashCal is null ? "none" : "ok")}"));

                    if (salesCal is not null && dashCal is not null)
                    {
                        // Measured so the style is applied and the template expanded, exactly as
                        // it would be the moment the popup opens.
                        foreach (var cal in new[] { salesCal, dashCal })
                        {
                            cal.Measure(new Size(400, 400));
                            cal.Arrange(new Rect(0, 0, 400, 400));
                            cal.UpdateLayout();
                        }

                        results.Add(("calendar · and both popups use the very same style object",
                            salesCal.Style is not null && ReferenceEquals(salesCal.Style, dashCal.Style),
                            salesCal.Style is null
                                ? "the popup fell back to the STOCK calendar"
                                : "styles differ between the two pages"));

                        results.Add(("calendar · the same template, so the same chrome",
                            ReferenceEquals(salesCal.Template, dashCal.Template), null));

                        // Corner radius and the popup border were what the difference was reported
                        // as. Same size at the same constraint means the same layout produced them.
                        results.Add(("calendar · and they lay out to the same size",
                            Math.Abs(salesCal.ActualWidth - dashCal.ActualWidth) < 0.5
                            && Math.Abs(salesCal.ActualHeight - dashCal.ActualHeight) < 0.5,
                            $"sales {salesCal.ActualWidth:F0}x{salesCal.ActualHeight:F0}, "
                            + $"dash {dashCal.ActualWidth:F0}x{dashCal.ActualHeight:F0}"));
                    }

                    // ── how a day is marked ────────────────────────────────────────
                    // One implicit CalendarDayButton style serves both pages, so the marks cannot
                    // differ BY PAGE -- only by state. That was the whole confusion: Sales entry
                    // opens with its invoice date selected (a fill) and the Dashboard opens with
                    // nothing selected (today's ring), and the ring used to be green while the
                    // fill was blue, which read as two different calendars.
                    //
                    // Read off the template's own triggers, so this is the rule that ships rather
                    // than a control asked to pretend: IsToday and IsSelected are read-only
                    // properties a test cannot set.
                    if (app.TryFindResource(typeof(CalendarDayButton)) is Style day
                        && day.Setters.OfType<Setter>()
                              .FirstOrDefault(x => x.Property == Control.TemplateProperty)?.Value
                           is ControlTemplate dayTemplate)
                    {
                        // The KEY, not a resolved Brush: these setters carry a DynamicResource,
                        // so the value is the markup extension and the brush does not exist until
                        // the theme is applied. The key is also the thing worth pinning -- it is
                        // the intent, and it survives a theme swap.
                        object? KeyFrom(DependencyProperty state, DependencyProperty want) =>
                            (dayTemplate.Triggers.OfType<Trigger>()
                                .FirstOrDefault(x => x.Property == state && Equals(x.Value, true))
                                ?.Setters.OfType<Setter>()
                                .FirstOrDefault(y => y.Property == want)
                                ?.Value as DynamicResourceExtension)?.ResourceKey;

                        const string accent = "AccentBrush";

                        var selectedFill = KeyFrom(CalendarDayButton.IsSelectedProperty,
                                                   Border.BackgroundProperty);
                        var todayRing = KeyFrom(CalendarDayButton.IsTodayProperty,
                                                Border.BorderBrushProperty);

                        results.Add(("calendar · a selected day is filled with the accent",
                            Equals(selectedFill, accent), $"{selectedFill}"));

                        // The one that was wrong: SuccessBrush is this app's "that worked" green
                        // and belongs nowhere on a calendar.
                        results.Add(("calendar · and today is ringed in the same accent, not green",
                            Equals(todayRing, accent), $"{todayRing}"));

                        // Today is filled too -- an outline beside a fill read as two different
                        // calendars -- but with the SOFT accent, because filling it with the solid
                        // one made today indistinguishable from the selection and a calendar with
                        // the 21st chosen showed two blue squares.
                        var todayFill = KeyFrom(CalendarDayButton.IsTodayProperty,
                                                Border.BackgroundProperty);

                        results.Add(("calendar · today is filled, so it never reads as a bare outline",
                            Equals(todayFill, "AccentSoftBrush"), $"{todayFill}"));

                        results.Add(("calendar · but not with the selected day's fill, or there are two",
                            !Equals(todayFill, selectedFill),
                            $"today {todayFill}, selected {selectedFill}"));

                        // On a tint the number stays TextBrush. OnAccent is white, and white on
                        // #E7F0FF is not a number.
                        results.Add(("calendar · and its number is left readable on that tint",
                            KeyFrom(CalendarDayButton.IsTodayProperty, Control.ForegroundProperty) is null,
                            $"{KeyFrom(CalendarDayButton.IsTodayProperty, Control.ForegroundProperty)}"));

                        // A day the range refuses must LOOK refused. The Dashboard blacks out
                        // everything before its From date on the To calendar, and with no trigger
                        // for it those days rendered identically to selectable ones -- same
                        // colour, same hand cursor -- so clicking them did nothing and the
                        // calendar read as broken.
                        var blackedOut = dayTemplate.Triggers.OfType<Trigger>()
                            .FirstOrDefault(x => x.Property == CalendarDayButton.IsBlackedOutProperty
                                                 && Equals(x.Value, true));

                        results.Add(("calendar · a blacked-out day is marked at all",
                            blackedOut is not null,
                            "no IsBlackedOut trigger: it looks exactly like a day you can pick"));

                        if (blackedOut is not null)
                        {
                            var setters = blackedOut.Setters.OfType<Setter>().ToList();

                            // Opacity, not Foreground: the weekend colour is a STYLE trigger and
                            // outranks anything the template sets, so a blacked-out Saturday would
                            // otherwise stay bright blue.
                            results.Add(("calendar · dimmed by opacity, which the weekend colour cannot beat",
                                setters.FirstOrDefault(x => x.Property == UIElement.OpacityProperty)
                                    ?.Value is double dim && dim < 0.6,
                                $"{setters.FirstOrDefault(x => x.Property == UIElement.OpacityProperty)?.Value}"));

                            // And it must not keep today's or a selection's fill.
                            var strippedFill = setters
                                .FirstOrDefault(x => x.Property == Border.BackgroundProperty)?.Value;

                            results.Add(("calendar · and stripped of any fill it would otherwise carry",
                                Equals(strippedFill?.ToString(), "#00FFFFFF"), $"{strippedFill}"));
                        }

                        // The hand cursor lives on the style, so only the style can take it back.
                        results.Add(("calendar · and the pointer stops inviting the click",
                            day.Triggers.OfType<Trigger>()
                                .Any(x => x.Property == CalendarDayButton.IsBlackedOutProperty
                                          && Equals(x.Value, true)
                                          && x.Setters.OfType<Setter>()
                                              .Any(y => y.Property == FrameworkElement.CursorProperty)),
                            "a hand cursor over a day that refuses is the complaint in one gesture"));

                        // IsSelected must stand on its own rather than leaning on IsToday, or a
                        // day that is selected but not today loses its fill.
                        results.Add(("calendar · a selected day is filled whether or not it is today",
                            Equals(selectedFill, accent)
                            && Equals(KeyFrom(CalendarDayButton.IsSelectedProperty,
                                              Control.ForegroundProperty), "OnAccentBrush"),
                            $"{selectedFill}"));
                    }
                    else
                    {
                        results.Add(("calendar · the day-button style is in the dictionaries", false,
                            "CalendarDayButton did not resolve"));
                    }

                    results.Add(("calendar · only the field height differs between the two",
                        Math.Abs(salesEntry.ActualHeight - 32) < 0.5
                        && Math.Abs(dashboard.ActualHeight - 34) < 0.5,
                        $"sales {salesEntry.ActualHeight:F0}px, dash {dashboard.ActualHeight:F0}px"));
                }
                else
                {
                    results.Add(("calendar · the AppDatePicker style is in the dictionaries", false,
                        "AppDatePicker did not resolve"));
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
