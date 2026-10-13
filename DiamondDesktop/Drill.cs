using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace DiamondDesktop;

/// <summary>
/// A figure you can open: <c>ui:Drill.On="True"</c> beside the handler that opens it.
///
/// WHY THIS EXISTS AT ALL
///
/// Every number this app prints came from rows, and for most of them the rows were unreachable. The
/// Stock page said 414.8400 ct had been rejected and offered no way to ask which parcels, off which
/// invoices, or what became of them -- finding one meant guessing a bucket and pressing Show
/// movements, which only works if you already know the answer. Measured across the app: 36 figures
/// on seven pages, ONE of which could be opened.
///
/// The fix is not thirty-five more click handlers. That is the shape of mistake this codebase has
/// made twice already -- PreviewMouseWheel="NoWheelChange" written onto eleven controls one at a
/// time, and the Dashboard's four never given it; a per-control rule is a rule the next control is
/// written without. So the rule lives in two places instead: HERE, which makes a figure openable
/// properly in one attribute, and in the probe suite, which walks every page and fails when a
/// figure has no way in and is not on the list of ones deliberately left closed.
///
/// WHAT IT SUPPLIES, so a tile does not have to remember five things
///
///   the pointer      a hand cursor, so it looks like what it is;
///   the keyboard     tab focus, and Enter or Space to open it -- a figure reachable only by mouse
///                    is hidden from anyone who does not use one, which is the same fault in a
///                    different costume;
///   the hit area     a transparent background, or the gaps between the label and the number are
///                    dead and the tile only opens where there happens to be ink;
///   the words        a tooltip and a screen-reader name, because a number that is quietly
///                    clickable is a number nobody clicks.
///
/// The handler stays on the tile: what "open" means differs -- some figures have their rows on the
/// same page and should narrow the list below, others have nowhere to show them and open a dialog.
/// That is a judgement per figure. Making it POSSIBLE, discoverable and reachable is not, and that
/// is all this does.
///
/// ON A FIGURE THAT GENUINELY CANNOT BE OPENED, do not set this and add it to the suite's list with
/// the reason. An explicit exception is a decision; a missing attribute is an oversight, and the
/// point of the pairing is to keep those two apart.
/// </summary>
public static class Drill
{
    public static readonly DependencyProperty OnProperty =
        DependencyProperty.RegisterAttached("On", typeof(bool), typeof(Drill),
            new PropertyMetadata(false, OnChanged));

    public static void SetOn(DependencyObject d, bool value) => d.SetValue(OnProperty, value);
    public static bool GetOn(DependencyObject d) => (bool)d.GetValue(OnProperty);

    /// <summary>
    /// What the tile says it will show, in the tooltip and to a screen reader.
    ///
    /// Optional, and worth setting: "Click to see what was rejected" tells the desk what they will
    /// get, where a bare hand cursor only says that something happens.
    /// </summary>
    public static readonly DependencyProperty HintProperty =
        DependencyProperty.RegisterAttached("Hint", typeof(string), typeof(Drill),
            new PropertyMetadata(""));

    public static void SetHint(DependencyObject d, string value) => d.SetValue(HintProperty, value);
    public static string GetHint(DependencyObject d) => (string)d.GetValue(HintProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement tile || !Equals(e.NewValue, true)) return;

        tile.Cursor = Cursors.Hand;

        // A panel with no background is transparent to hit testing, so the gaps between the label,
        // the figure and the caption would not open anything. Only when nothing has been set: a
        // tile that paints itself keeps its own colour.
        if (tile is System.Windows.Controls.Panel { Background: null } panel)
            panel.Background = System.Windows.Media.Brushes.Transparent;
        else if (tile is System.Windows.Controls.Control { Background: null } control)
            control.Background = System.Windows.Media.Brushes.Transparent;

        // THE KEYBOARD, which the first hand-written version of this forgot. A figure that opens
        // only under a mouse is still hidden -- from a keyboard user entirely, and from anyone
        // tabbing through the page looking for what they can do.
        tile.Focusable = true;
        if (tile.GetValue(KeyboardNavigation.TabNavigationProperty) is not null)
            KeyboardNavigation.SetIsTabStop(tile, true);

        tile.KeyDown += (_, k) =>
        {
            if (k.Key is not (Key.Enter or Key.Space)) return;

            // Raised as a click so the tile's own handler answers it, rather than every tile
            // needing a second handler that does the same thing for the keyboard.
            tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount,
                                                     MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = tile });
            k.Handled = true;
        };

        // THE HINT IS READ LATER, and that is not fussiness. Attached properties are applied in the
        // order they appear on the element, so On="True" runs while Hint is still empty -- the
        // tooltip came out blank and the suite caught it. By Loaded every attribute has been
        // parsed, whichever order they were written in.
        tile.Loaded += (_, _) =>
        {
            string hint = GetHint(tile);
            if (hint.Length == 0) return;

            tile.ToolTip ??= hint;
            if (string.IsNullOrEmpty(AutomationProperties.GetHelpText(tile)))
                AutomationProperties.SetHelpText(tile, hint);
        };
    }
}
