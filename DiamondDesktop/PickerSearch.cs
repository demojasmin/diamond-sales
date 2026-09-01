using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Linq;

namespace DiamondDesktop;

/// <summary>
/// Type-to-search on any ComboBox: <c>ui:PickerSearch.On="True"</c>.
///
/// WHY AN ATTACHED BEHAVIOUR AND NOT WHAT SALES ENTRY DOES. The Sales entry grid narrows its Size
/// and Grade lists through per-line properties on SaleLine, which works because every row has its
/// own view model to hang a filter on. The other seventeen pickers -- intake, conversion, rejection,
/// adjustment, the ledger, the stock filters, the price grid, the dashboard -- are filled from
/// code-behind and have no view model between them and the catalogue. Giving each one a pair of
/// filter properties would mean seventeen near-identical rewrites. This carries the same behaviour
/// to any ComboBox with one attribute.
///
/// THE CRUX IS THE VIEW. WPF hands every ItemsControl bound to the same collection the SAME default
/// view, so filtering it in one picker would narrow every other picker in the app at once -- the
/// stock filter would silently reach into the intake form. Each control gets its own
/// CollectionViewSource here, which is the only way to keep the filtering local.
///
/// A SEARCH MISS IS NOT A CLEAR, the same rule SaleLine enforces. An editable ComboBox writes
/// SelectedItem = null the moment its text stops matching an item, so typing one character over a
/// chosen grade would empty a filter or, worse, an intake form. The last real selection is put back
/// when the control is left.
/// </summary>
public static class PickerSearch
{
    public static readonly DependencyProperty OnProperty =
        DependencyProperty.RegisterAttached("On", typeof(bool), typeof(PickerSearch),
            new PropertyMetadata(false, OnChanged));

    public static void SetOn(DependencyObject d, bool value) => d.SetValue(OnProperty, value);
    public static bool GetOn(DependencyObject d) => (bool)d.GetValue(OnProperty);

    /// The view this behaviour created for a control, so a later ItemsSource assignment can be
    /// told apart from its own wrapping and not wrapped twice.
    private static readonly DependencyProperty OwnedViewProperty =
        DependencyProperty.RegisterAttached("OwnedView", typeof(ICollectionView), typeof(PickerSearch),
            new PropertyMetadata(null));

    /// <summary>
    /// THE CollectionViewSource ITSELF, and this is the fix for the crash.
    ///
    /// It used to read <c>new CollectionViewSource { Source = … }.View</c> — the view was kept and
    /// the CollectionViewSource that made it was dropped on the floor. A CollectionViewSource owns
    /// the view it hands out and holds the plumbing that keeps it attached to its source; once the
    /// CVS is collected the ListCollectionView is left with nothing behind it, and the next touch
    /// of Count, IsEmpty or the enumerator throws NullReferenceException from inside WPF.
    ///
    /// That is why it was intermittent, why it landed on a different accessor each time
    /// (InternalCount, then IsEmpty), and why it then repeated on every measure pass: nothing the
    /// desk did caused it, a garbage collection did.
    ///
    /// Held in an attached property, so the CVS lives exactly as long as the control that uses it.
    /// </summary>
    private static readonly DependencyProperty OwnedSourceProperty =
        DependencyProperty.RegisterAttached("OwnedSource", typeof(CollectionViewSource), typeof(PickerSearch),
            new PropertyMetadata(null));

    /// <summary>
    /// The view, but ONLY while it is still what the control is showing.
    ///
    /// The second half of the crash. Every handler below used to reach for OwnedView directly, and
    /// the drawer re-points its pickers at the entry's lists each time it opens — so LostFocus,
    /// the Enter key and the filter were all still refreshing a view the control had stopped using
    /// and whose collection had since been cleared and refilled.
    ///
    /// One accessor, so a stale view can never be touched from anywhere.
    /// </summary>
    private static ICollectionView? View(ComboBox cb) =>
        cb.GetValue(OwnedViewProperty) is ICollectionView v && ReferenceEquals(cb.ItemsSource, v)
            ? v : null;

    /// Set while a picker holds focus that has not been typed into yet. The first character then
    /// replaces what is in the box, which is the job the focus highlight used to do.
    private static readonly DependencyProperty UntouchedProperty =
        DependencyProperty.RegisterAttached("Untouched", typeof(bool), typeof(PickerSearch),
            new PropertyMetadata(false));

    /// What was chosen before the current burst of typing, so a search miss can be undone.
    private static readonly DependencyProperty LastPickProperty =
        DependencyProperty.RegisterAttached("LastPick", typeof(object), typeof(PickerSearch),
            new PropertyMetadata(null));

    /// <summary>
    /// Forgets what this picker was last showing: the remembered selection, the typed text and any
    /// filter left on its view.
    ///
    /// WHY IT HAS TO EXIST. LastPick is attached to the CONTROL, not to whatever the control is
    /// editing — which is right while one form is being filled in, and wrong the moment the same
    /// control is re-pointed at a different record. The Deal Details drawer does exactly that: one
    /// Buyer picker, re-used for every line.
    ///
    /// So opening the drawer on a line WITH a buyer and then on a line WITHOUT one left the box
    /// still reading the first line's buyer — and worse, LostFocus would put it back as a real
    /// selection, because SelectedItem was null and LastPick still held it. A buyer the desk never
    /// chose, written onto a line, silently. That is a wrong invoice, not a cosmetic slip.
    ///
    /// Called when the drawer re-points, so every open starts on exactly the line's own deal.
    /// </summary>
    public static void Reset(ComboBox cb)
    {
        cb.SetValue(LastPickProperty, null);

        if (View(cb) is { } view && view.Filter is not null)
        {
            view.Filter = null;
            view.Refresh();
        }

        // The TEXT, last. An editable ComboBox keeps whatever was typed into it even after its
        // SelectedItem is set elsewhere, so without this the box goes on showing the previous
        // line's buyer while the binding underneath says otherwise.
        cb.Text = "";
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox cb || !Equals(e.NewValue, true)) return;

        cb.IsEditable = true;
        // OFF: WPF's own type-ahead rewrites the text box to the first prefix match, which fights
        // the filtering below and makes a mid-word edit jump. TextPath is still needed though --
        // the two settings do different jobs, and without it an editable combo showing items
        // through an ItemTemplate writes the item's ToString() into the box.
        cb.IsTextSearchEnabled = false;
        cb.StaysOpenOnEdit = true;
        // TextPath is NOT set here. It depends on what the control is filled with, and at attach
        // time it is usually filled with nothing at all -- see PathFor, called once the items are
        // known.

        // LAZY, and this is not an optimisation. Replacing ItemsSource makes WPF raise
        // SelectionChanged, and these pickers hang real work off that handler -- the dashboard's
        // grade filter reloads the dashboard from the server. Wrapping at attach time therefore
        // fired a network call for every picker on the screen the moment the window opened, which
        // is how it was caught: a probe that never touches the dashboard began failing on a
        // dashboard_summary call, and the refusal message it was asserting had been overwritten by
        // the error from that call.
        //
        // Carrying the selection across the wrap was not enough on its own -- the event is raised
        // whether or not the value ends up the same -- so the wrap waits until the control is
        // actually being used. By then the person is in that picker deliberately, the list is
        // loaded, and nothing else on the screen is mid-load.
        cb.DropDownOpened += (_, _) => Wrap(cb);

        // ── ARRIVING IN A PICKER DOES NOT HIGHLIGHT IT ────────────────────────
        //
        // An editable ComboBox selects ALL of its text when it takes focus, so tabbing into Buyer,
        // or the Deal Details drawer putting the caret there on purpose, painted the whole value in
        // a blue block. It reads as the app having chosen something, which on this screen is
        // exactly the thing the desk has been burned by.
        //
        // The select-all was doing a job, though: it is what makes the next keystroke REPLACE the
        // value rather than append to it. Take it away on its own and typing "jite" over a chosen
        // broker gives "JITESH SHAHjite", which matches nothing. So the job moves to the flag
        // below and the highlight goes.
        cb.GotKeyboardFocus += (_, _) =>
        {
            Deselect(cb);
            cb.SetValue(UntouchedProperty, true);
        };
        cb.LostKeyboardFocus += (_, _) => cb.SetValue(UntouchedProperty, false);

        cb.PreviewTextInput += (_, _) =>
        {
            // THE FIRST CHARACTER AFTER ARRIVING STARTS A NEW SEARCH, which is what the highlight
            // used to do. Only the first: everything after it is ordinary typing.
            //
            // Safe here in a way it would not be in a free-text box, because a picker's content is
            // always some item's name -- there is nothing in it worth editing in the middle. You
            // either keep what is there or look for another one.
            if (cb.GetValue(UntouchedProperty) is true)
            {
                cb.SetValue(UntouchedProperty, false);
                if (cb.Template?.FindName("PART_EditableTextBox", cb) is TextBox fresh) fresh.Clear();
            }

            Wrap(cb);
        };

        // The PATH, unlike the wrap, cannot wait. It decides what an editable box DISPLAYS, and the
        // app sets these selections in code -- restoring a stock filter, defaulting to "All grades"
        // -- long before anyone interacts. Deferred, the box showed the item's TYPE NAME until it
        // was first clicked. One reflection lookup, touching no property WPF raises events on, so
        // unlike the wrap it is safe the moment the items arrive.
        PathFor(cb);
        DependencyPropertyDescriptor
            .FromProperty(ItemsControl.ItemsSourceProperty, typeof(ComboBox))
            .AddValueChanged(cb, (_, _) => PathFor(cb));

        DependencyPropertyDescriptor
            .FromProperty(ComboBox.TextProperty, typeof(ComboBox))
            .AddValueChanged(cb, (_, _) => Narrow(cb));

        // ENTER TAKES THE TOP MATCH. Typing "dx" narrows the list to one row and then, without
        // this, Enter did nothing: WPF only commits text that matches an item EXACTLY, so a
        // half-typed code left the box holding text with no selection behind it, and the revert on
        // the way out threw the typing away. Type, Enter, done -- the same three keystrokes the
        // Sales entry grid takes.
        //
        // Only while the list is open, and only when it actually offers something. With the list
        // shut, Enter belongs to whatever the screen does with it.
        cb.PreviewKeyDown += (_, k) =>
        {
            if (k.Key != System.Windows.Input.Key.Enter || !cb.IsDropDownOpen) return;
            if (View(cb) is not { } view) return;

            object? top = view.Cast<object>().FirstOrDefault();
            if (top is null) return;

            cb.SelectedItem = top;
            cb.IsDropDownOpen = false;
            k.Handled = true;
        };

        // ONCE THE LIST IS GONE, THE BOX SHOWS WHAT IS CHOSEN.
        //
        // The other half of "I searched, I picked it, and the box still says what I typed".
        // SelectionChanged is what rewrites the text, and choosing the row that is ALREADY the
        // selection raises nothing at all -- so the box was left holding the search term. That is
        // not a corner case on this screen: the Deal Details drawer opens on the line's own broker,
        // and "jite" is how somebody gets back to JITESH SHAH.
        //
        // Only when something IS chosen. A search that found nothing keeps its text, so the person
        // can go on editing it rather than having it snatched back.
        //
        // ON THE PROPERTY, not on DropDownClosed. That event is raised off the popup's own close,
        // and the popup does not always get that far -- measured: a picker whose list is dismissed
        // by choosing a row leaves IsDropDownOpen false with DropDownClosed never raised. The
        // property is the fact; the event is a report of it.
        DependencyPropertyDescriptor
            .FromProperty(ComboBox.IsDropDownOpenProperty, typeof(ComboBox))
            .AddValueChanged(cb, (_, _) => { if (!cb.IsDropDownOpen) ShowSelection(cb); });

        cb.SelectionChanged += (_, args) =>
        {
            // Remember only a REAL pick. WPF's deselect-on-miss arrives with nothing added, and
            // recording that would throw away the very thing being protected.
            if (args.AddedItems.Count > 0) cb.SetValue(LastPickProperty, args.AddedItems[0]);

            // NO BLUE BLOCK ON A BOX NOBODY IS IN.
            //
            // Giving an editable ComboBox a value makes WPF write the text and SELECT ALL OF IT --
            // and every picker in this app is filled from code, so they all ended up sitting on a
            // full selection with nothing focused. Open the Deal Details drawer and Buyer and
            // Broker were both highlighted at once; the Stock filters were highlighted before
            // anybody had touched the page. It reads as the app having chosen something.
            //
            // ONLY WHILE UNFOCUSED. Selecting the text when the box IS focused is what makes the
            // next keystroke replace the old value instead of appending to it -- take that away and
            // typing "jite" over a chosen broker gives "JITESH SHAHjite" and finds nothing.
            //
            // At Input priority, because WPF's own SelectAll lands after this handler returns.
            if (!cb.IsKeyboardFocusWithin)
                cb.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    new Action(() => { if (!cb.IsKeyboardFocusWithin) Deselect(cb); }));
        };

        cb.LostFocus += (_, _) =>
        {
            // Leaving puts back what is actually chosen and discards whatever was typed to find it,
            // so the box never sits reading one thing while the form holds another.
            if (cb.SelectedItem is null && cb.GetValue(LastPickProperty) is { } last)
                cb.SelectedItem = last;

            // View(cb), not the raw attached property: the drawer re-points its pickers at the
            // entry's lists each time it opens, so this used to clear the filter on a view the
            // control had stopped using and whose collection had since been replaced.
            if (View(cb) is { } view)
            {
                view.Filter = null;
                view.Refresh();
            }
        };
    }

    /// <summary>Gives the control its own view of whatever it is bound to.</summary>
    private static void Wrap(ComboBox cb)
    {
        if (cb.ItemsSource is null) return;
        if (ReferenceEquals(cb.ItemsSource, cb.GetValue(OwnedViewProperty))) return;   // already ours

        // NEVER OVER A BINDING. Assigning ItemsSource writes a LOCAL VALUE, and a local value
        // replaces a binding permanently -- so wrapping a bound picker left it holding a view over
        // whatever collection was there at that moment, while the binding's source went on being
        // cleared and refilled underneath. Refreshing a view whose source has been emptied throws
        // NullReferenceException out of ListCollectionView.InternalCount, and it kept throwing.
        //
        // Refused rather than worked around: a picker that quietly stops searching is a small
        // loss, and a picker that quietly stops tracking its own data is a wrong invoice. The
        // three drawer pickers this caught now fill their lists from code instead, which is what
        // the other seventeen already did.
        if (BindingOperations.GetBindingExpressionBase(cb, ItemsControl.ItemsSourceProperty) is not null)
            return;

        // THE SELECTION IS CARRIED ACROSS. Replacing ItemsSource makes WPF drop the selection and
        // raise SelectionChanged with nothing added -- and these pickers hang real work off that
        // handler. The dashboard's grade filter reloaded the whole dashboard the instant this
        // behaviour attached, which is how it was found: a probe that had never touched the
        // dashboard started failing on a dashboard_summary call.
        //
        // Index as well as item: the filter pickers select "All grades" by index before their list
        // is a view, and an index survives a re-wrap where the item reference may not.
        object? keepItem = cb.SelectedItem;
        int keepIndex = cb.SelectedIndex;

        // ONE CollectionViewSource PER CONTROL, KEPT ALIVE, and re-pointed rather than rebuilt.
        //
        // This used to be `new CollectionViewSource { Source = ... }.View` -- the view was kept and
        // the CVS that made it was dropped. A CVS owns the view it hands out; once it is collected
        // the ListCollectionView has nothing behind it and the next read of Count, IsEmpty or its
        // enumerator throws NullReferenceException from inside WPF. Intermittent, on a different
        // accessor each time, and then on every measure pass. Held in an attached property it
        // lives exactly as long as the control.
        if (cb.GetValue(OwnedSourceProperty) is not CollectionViewSource cvs)
        {
            cvs = new CollectionViewSource();
            cb.SetValue(OwnedSourceProperty, cvs);
        }

        // Setting Source rebuilds the view against the new list, which is the supported way to
        // re-point one. Creating a second CVS would leave the first one's view live and filtered
        // over a collection nobody updates -- the same stranding, one layer down.
        cvs.Source = cb.ItemsSource;
        var view = cvs.View;
        if (view is null) return;                 // nothing usable; leave the control as it is

        // THE VIEW MUST NOT CHOOSE FOR THE PERSON.
        //
        // A Selector whose ItemsSource is a CollectionView synchronises SelectedItem with that
        // view's CURRENT ITEM by default -- and applying a filter, then Refresh(), moves currency to
        // the first row that survives it. So the moment "+" narrowed the list, the picker silently
        // adopted "+6.5" as its selection; PutBack then wrote the typed "+" back over the box, and
        // the control was left claiming a value nobody had picked.
        //
        // Clicking that first row was then a no-op -- it was already SelectedItem, so no
        // SelectionChanged, so nothing rewrote the box, and the size stayed showing "+". Any OTHER
        // row worked, which is what made it look random.
        //
        // Off, so the only thing that moves the selection is somebody choosing.
        cb.IsSynchronizedWithCurrentItem = false;

        cb.SetValue(OwnedViewProperty, view);
        cb.ItemsSource = view;

        if (keepItem is not null) cb.SelectedItem = keepItem;
        else if (keepIndex >= 0 && keepIndex < view.Cast<object>().Count()) cb.SelectedIndex = keepIndex;
    }

    /// <summary>
    /// Works out which property an item's text lives on, from the items themselves.
    ///
    /// It was hardcoded to "ShortName", which is right for a list of Grade or SizeBucket and wrong
    /// for half the pickers in the app: the stock filters are filled with plain STRINGS -- "All
    /// grades", then the codes -- and a string has no ShortName, so the path resolved to nothing
    /// and an editable box rendered EMPTY. The filter applied, the table narrowed, and the control
    /// that caused it looked untouched.
    ///
    /// Left alone if the control already names a path, or displays through DisplayMemberPath. An
    /// empty path is the right answer for strings: WPF falls back to ToString().
    /// </summary>
    private static void PathFor(ComboBox cb)
    {
        if (cb.ItemsSource is null) return;
        if (!string.IsNullOrEmpty(cb.DisplayMemberPath)) return;
        if (!string.IsNullOrEmpty(TextSearch.GetTextPath(cb))) return;

        // This runs from an ItemsSource value-changed hook, which WPF raises DURING the swap -- so
        // it can land on a view that is between sources. One reflection lookup is not worth a
        // crash, and the path is set again the next time the items change.
        object? first;
        try { first = cb.ItemsSource.Cast<object>().FirstOrDefault(); }
        catch (Exception) { return; }

        if (first is not null && first.GetType().GetProperty("ShortName") is not null)
            TextSearch.SetTextPath(cb, "ShortName");
    }

    /// <summary>
    /// Narrows the control's own view to the rows whose text CONTAINS what was typed.
    ///
    /// Contains rather than StartsWith: the sheet writes "NO 1 BB" and the desk says "BB". Spaces
    /// are dropped from both sides so "no1" finds "NO 1". A search matching nothing shows
    /// everything -- an empty drop-down under a typo reads as a broken picker, and the text is
    /// about to be reverted anyway.
    /// </summary>
    /// <summary>
    /// One narrowing at a time, per control.
    ///
    /// EVERYTHING BELOW WRITES Text AS A SIDE EFFECT, and every one of those writes raises the hook
    /// that called this. Applying the filter moves the view's current item, an editable ComboBox
    /// follows its view's currency, and following it rewrites the box -- so a search that matched
    /// one row re-entered here with the text now reading that row's NAME, took the "the box shows
    /// what is chosen, this is not a search" branch, and CLEARED the filter it had just applied.
    /// Typing "qu" left the full list on screen.
    ///
    /// The nested call has nothing to add: the outer one is still mid-flight and finishes by
    /// putting the typed text back. Every real keystroke arrives outside it and narrows normally.
    /// </summary>
    private static void Narrow(ComboBox cb)
    {
        if (cb.GetValue(NarrowingProperty) is true) return;

        cb.SetValue(NarrowingProperty, true);
        try { NarrowOnce(cb); }
        finally { cb.SetValue(NarrowingProperty, false); }
    }

    private static readonly DependencyProperty NarrowingProperty =
        DependencyProperty.RegisterAttached("Narrowing", typeof(bool), typeof(PickerSearch),
            new PropertyMetadata(false));

    private static void NarrowOnce(ComboBox cb)
    {
        // CAPTURED BEFORE ANYTHING ELSE, and this is the whole bug.
        //
        // A picker that ALREADY HAD A SELECTION could not be searched. Wrapping re-applies the
        // selection and Refresh() re-resolves it, and an editable ComboBox answers both by
        // rewriting its own text box to the selected item's name -- so typing "ji" over a broker
        // that was already chosen put "JITESH SHAH" straight back, and by the time the filter was
        // computed the search term had been erased. Buyer, with nothing selected yet, worked
        // perfectly, which is exactly why it read as "the broker search is broken".
        string raw = cb.Text ?? "";

        // Typing is one of the two things that earns the wrap; see the comment where these are
        // hooked. Doing it here as well means the very first keystroke narrows rather than being
        // the one that only sets things up.
        Wrap(cb);
        if (View(cb) is not { } view) { PutBack(cb, raw); return; }

        string typed = raw.Replace(" ", "").Trim();

        // The box showing what is already chosen is not a search. Narrowing on it would leave the
        // drop-down offering the one item already picked, so opening it to change your mind showed
        // a list of one.
        if (typed.Length == 0 || Text(cb, cb.SelectedItem).Replace(" ", "") == typed)
        {
            if (view.Filter is not null) { view.Filter = null; view.Refresh(); }
            PutBack(cb, raw);
            return;
        }

        bool Hit(object? o) =>
            Text(cb, o).Replace(" ", "").Contains(typed, StringComparison.OrdinalIgnoreCase);

        // Counted before it is applied: a filter that hides every row is worse than no filter.
        bool any = false;
        foreach (object? o in (IEnumerable)view) if (Hit(o)) { any = true; break; }

        view.Filter = any ? o => Hit(o) : null;
        view.Refresh();
        PutBack(cb, raw);
        cb.IsDropDownOpen = true;

        // THE CARET, LAST, and this is what ate the first character.
        //
        // Opening the drop-down makes a ComboBox SELECT ALL of its edit box -- the drop-down is
        // being offered as the way to choose, so it highlights what is there to be replaced. That
        // is right when it opens by itself; here it opens because somebody is TYPING into the box,
        // and the next keystroke landed on a full selection and replaced everything before it.
        // Typing "abc" gave "bc": 'a' opened the list, 'b' replaced the highlighted 'a', and 'c'
        // appended to what was left. PutBack above can move the caret for the same reason.
        //
        // Only while the box actually has the keyboard, so a picker filled from code is untouched.
        Caret(cb);
    }

    /// <summary>Puts the caret after the last character typed, with nothing selected.</summary>
    private static void Caret(ComboBox cb)
    {
        if (cb.IsKeyboardFocusWithin) Deselect(cb);
    }

    /// <summary>Caret at the end, nothing highlighted.</summary>
    private static void Deselect(ComboBox cb)
    {
        if (cb.Template?.FindName("PART_EditableTextBox", cb) is not TextBox box) return;

        box.SelectionStart = box.Text.Length;
        box.SelectionLength = 0;
    }

    /// <summary>
    /// Once the list is shut, the box reads what is chosen rather than what was typed to find it.
    /// </summary>
    private static void ShowSelection(ComboBox cb)
    {
        if (cb.SelectedItem is not { } chosen) return;

        string shown = Text(cb, chosen);
        if (!string.Equals(cb.Text, shown, StringComparison.Ordinal)) cb.Text = shown;
    }

    /// <summary>
    /// Puts the half-typed search term back in the box after something re-resolved the selection.
    ///
    /// Safe to call from inside Narrow: the Narrowing flag is still set, so the hook this raises
    /// returns without doing anything.
    /// </summary>
    private static void PutBack(ComboBox cb, string typed)
    {
        if (!string.Equals(cb.Text, typed, StringComparison.Ordinal)) cb.Text = typed;
    }

    /// An item's text, read through the same TextPath the ComboBox displays it by, so searching and
    /// showing can never disagree.
    private static string Text(ComboBox cb, object? item)
    {
        if (item is null) return "";

        string path = TextSearch.GetTextPath(cb);
        if (string.IsNullOrEmpty(path)) return item.ToString() ?? "";

        return item.GetType().GetProperty(path)?.GetValue(item)?.ToString() ?? item.ToString() ?? "";
    }
}
