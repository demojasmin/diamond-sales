using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace DiamondDesktop.UiTests;

/// <summary>
/// Launches the real app once for the whole test class and signs in.
/// Signs into Supabase with a real account — DiamondApi and its owner/owner login are retired
/// (docs/12 §1). Nothing needs to be running locally; the project must be reachable.
/// </summary>
public sealed class AppFixture : IDisposable
{
    public UIA3Automation Automation { get; } = new();
    public Application App { get; }
    public Window Window { get; }

    private static string ExePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiamondDesktop", "DiamondDesktop.csproj")))
            dir = dir.Parent;

        if (dir is null) throw new InvalidOperationException("Could not locate the repo root");
        return Path.Combine(dir.FullName, "DiamondDesktop", "bin", "Debug", "net10.0-windows", "DiamondDesktop.exe");
    }

    private static string Email =>
        Environment.GetEnvironmentVariable("SOLITAIRE_EMAIL") ?? "demojasmin89@gmail.com";
    private static string Password =>
        Environment.GetEnvironmentVariable("SOLITAIRE_PASSWORD") ?? "Priya@Hexa@123";

    public AppFixture()
    {
        // Application.Launch(string) does NOT carry this process's environment to the child, so the
        // app never sees SOLITAIRE_EMAIL/PASSWORD and sits on the login screen forever. Building the
        // ProcessStartInfo ourselves (UseShellExecute = false) is what makes Environment take effect.
        var start = new System.Diagnostics.ProcessStartInfo(ExePath()) { UseShellExecute = false };
        start.Environment["SOLITAIRE_EMAIL"] = Email;
        start.Environment["SOLITAIRE_PASSWORD"] = Password;

        App = Application.Launch(start);

        // Credentials are TYPED, not passed through the environment: Application.Launch does not carry
        // this process's environment into the child, so SOLITAIRE_EMAIL/PASSWORD never reach the app and
        // sign-in silently does nothing. Typing also exercises the real login path.
        //
        // Two possible openings depending on whether a DPAPI session survived the last run: the login
        // dialog, or straight into the shell because the token is still good.
        bool typed = false;
        var shell = Retry.WhileNull(() =>
        {
            var windows = App.GetAllTopLevelWindows(Automation);
            var main = windows.FirstOrDefault(w => w.Title.Contains("Solitaire Desk"));
            if (main is not null) return main;

            var login = windows.FirstOrDefault(w => w.Title.Contains("Sign in"));
            if (login is null || typed) return null;

            var email = login.FindFirstDescendant(cf => cf.ByAutomationId("UserBox"))?.AsTextBox();
            var button = login.FindFirstDescendant(cf => cf.ByAutomationId("SignIn"))?.AsButton();
            if (email is null || button is null) return null;

            login.SetForeground();
            email.Text = Email;

            // A PasswordBox deliberately refuses ValuePattern, so the only way in is the keyboard.
            login.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox"))?.Focus();
            Keyboard.Type(Password);
            Wait.UntilInputIsProcessed();

            typed = true;
            button.Invoke();
            return null;
        }, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1)).Result;

        if (shell is null)
        {
            // Report what the app actually said. "Never appeared" on its own sends you hunting for the
            // wrong thing — the login screen is usually holding the real answer on screen.
            var windows = App.GetAllTopLevelWindows(Automation);
            string diagnosis = string.Join(" | ", windows.Select(w =>
                $"[{w.Title}] status='{w.FindFirstDescendant(cf => cf.ByAutomationId("Status"))?.Name}' "
                + $"email='{w.FindFirstDescendant(cf => cf.ByAutomationId("UserBox"))?.AsTextBox().Text}'"));

            throw new InvalidOperationException(
                $"Main window never appeared. Windows seen: {(diagnosis.Length == 0 ? "none" : diagnosis)}");
        }

        Window = shell;

        Window.SetForeground();
    }

    public void Dispose()
    {
        App.Close();
        Automation.Dispose();
    }
}

/// <summary>Everything the Sales entry screen can do, expressed once so the tests stay readable.</summary>
public sealed class SalesEntryPage(AppFixture fx)
{
    public Window W => fx.Window;

    // Column indices, in display order. Named so a column reorder breaks in one place.
    public const int ColSize = 0, ColGrade = 1, ColWeight = 2, ColSelection = 3, ColRejection = 4,
                     ColPrice = 5, ColExRate = 6, ColLess1 = 7, ColLess2 = 8, ColAmount = 9, ColRemark = 10;

    /// Retries: save/post round-trip the API, and an empty TextBlock is pruned from the automation
    /// tree entirely — so "not written yet" and "does not exist" look identical without a wait.
    public AutomationElement Find(string automationId) =>
        Retry.WhileNull(() => W.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
                        TimeSpan.FromSeconds(5)).Result
        ?? throw new InvalidOperationException($"'{automationId}' not found — is it missing an x:Name?");

    public string TextOf(string automationId) => Find(automationId).Name;

    /// Save and post write the status from an async continuation, so reading it straight after the
    /// click races the API round-trip and usually sees the empty string.
    public string Status =>
        Retry.WhileEmpty(() => Find("Status").Name, TimeSpan.FromSeconds(10)).Result ?? "";
    public Grid Grid => Find("Grid").AsGrid();
    public int RowCount => Grid.Rows.Length;

    public string Cell(int column, int row = 0) => Grid.Rows[row].Cells[column].Name;

    public void SelectTab(string header) =>
        W.FindFirstDescendant(cf => cf.ByControlType(ControlType.TabItem).And(cf.ByName(header)))!
         .AsTabItem().Select();

    /// One app instance is shared across the class, so a stray broker % or leftover line from an
    /// earlier test silently changes every amount downstream. Always start clean.
    public SalesEntryPage StartFreshInvoice()
    {
        SelectTab("Sales entry");
        Find("New").AsButton().Invoke();
        Wait.UntilInputIsProcessed();
        return this;
    }

    public SalesEntryPage Buyer(string name)
    {
        Find("BuyerPicker").AsComboBox().Select(name);
        Wait.UntilInputIsProcessed();
        return this;
    }

    public SalesEntryPage Broker(string name)
    {
        Find("BrokerPicker").AsComboBox().Select(name);
        Wait.UntilInputIsProcessed();
        return this;
    }

    public SalesEntryPage SetHeaderField(string automationId, string value)
    {
        var box = Find(automationId).AsTextBox();
        box.Text = value;
        Keyboard.Press(VirtualKeyShort.TAB);
        Wait.UntilInputIsProcessed();
        return this;
    }

    /// Types into a cell the way a user does — the text columns bind on LostFocus, so Tab commits.
    /// F2 is not optional: the first click into the grid only takes focus, it does not begin an
    /// edit, so without it the very first value typed on a screen is silently dropped.
    public SalesEntryPage EnterCell(int column, string value, int row = 0)
    {
        var cell = Grid.Rows[row].Cells[column];
        cell.Click();
        Wait.UntilInputIsProcessed();

        Keyboard.Type(value);
        Keyboard.Press(VirtualKeyShort.TAB);
        Wait.UntilInputIsProcessed();
        return this;
    }

    /// Size and Grade are template columns holding ComboBoxes; the cell's first click opens them.
    public ComboBox CellCombo(int column, int row = 0)
    {
        var cell = Grid.Rows[row].Cells[column];
        cell.Click();
        Wait.UntilInputIsProcessed();
        return cell.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox))!.AsComboBox();
    }

    public SalesEntryPage PickGrade(string grade, int row = 0)
    {
        CellCombo(ColGrade, row).Select(grade);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Wait.UntilInputIsProcessed();
        return this;
    }

    public SalesEntryPage PickSize(string size, int row = 0)
    {
        CellCombo(ColSize, row).Select(size);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Wait.UntilInputIsProcessed();
        return this;
    }

    public SalesEntryPage FillLine(string grade, string size, string weight, string selection, string price,
                                   int row = 0)
    {
        PickGrade(grade, row);
        PickSize(size, row);
        EnterCell(ColWeight, weight, row);
        EnterCell(ColSelection, selection, row);
        EnterCell(ColPrice, price, row);
        return this;
    }

    public void Click(string automationId)
    {
        Find(automationId).AsButton().Invoke();
        Wait.UntilInputIsProcessed();
    }

    /// Post can raise a modal (negative stock, or the posted confirmation). Answer and return its text.
    public string? DismissModal(string button)
    {
        var modal = Retry.WhileNull(() => W.ModalWindows.FirstOrDefault(), TimeSpan.FromSeconds(5)).Result;
        if (modal is null) return null;

        string text = string.Join(" ", modal.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                                            .Select(t => t.Name));
        modal.FindFirstDescendant(cf => cf.ByName(button))?.AsButton().Invoke();
        Wait.UntilInputIsProcessed();
        return text;
    }
}
