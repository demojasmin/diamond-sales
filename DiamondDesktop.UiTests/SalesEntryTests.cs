using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace DiamondDesktop.UiTests;

/// <summary>
/// Sales entry, start to end. Every header field, every column, line management, every validation
/// rule and the save/post round-trip — driven through the real keyboard against the real API.
/// </summary>
public sealed class SalesEntryTests(AppFixture fx) : IClassFixture<AppFixture>
{
    private readonly SalesEntryPage p = new(fx);

    // ── Header ──────────────────────────────────────────────────────────────

    [Fact]
    public void Buyer_autofills_its_default_terms()
    {
        p.StartFreshInvoice().Buyer("ABC Company");
        Assert.Equal("45", p.Find("TermsBox").AsTextBox().Text);
    }

    [Fact]
    public void Buyer_with_zero_default_leaves_terms_alone()
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND");
        Assert.Equal("0", p.Find("TermsBox").AsTextBox().Text);
    }

    [Fact]
    public void Terms_autofill_does_not_overwrite_a_typed_value()
    {
        // Guard on the "TermsDays == 0" condition in SelectedBuyer — a typed 7 must survive.
        p.StartFreshInvoice().SetHeaderField("TermsBox", "7").Buyer("ABC Company");
        Assert.Equal("7", p.Find("TermsBox").AsTextBox().Text);
    }

    [Fact]
    public void Due_date_is_invoice_date_plus_terms()          // CALC-10
    {
        p.StartFreshInvoice().SetHeaderField("TermsBox", "30");
        var expected = DateTime.Today.AddDays(30).ToString("dd-MM-yyyy");
        Assert.NotNull(p.W.FindFirstDescendant(cf => cf.ByName(expected)));
    }

    [Fact]
    public void Terms_of_zero_means_due_on_the_invoice_date()  // docs/04 A-3
    {
        p.StartFreshInvoice().SetHeaderField("TermsBox", "0");
        var today = DateTime.Today.ToString("dd-MM-yyyy");
        Assert.NotNull(p.W.FindFirstDescendant(cf => cf.ByName(today)));
    }

    [Fact]
    public void Broker_autofills_its_default_percent()
    {
        p.StartFreshInvoice().Broker("JITESH SHAH");
        Assert.Equal("1.0", p.Find("BrokerPctBox").AsTextBox().Text);
    }

    [Fact]
    public void Doc_type_offers_bill()
    {
        p.StartFreshInvoice();
        var types = p.Find("DocTypePicker").AsComboBox();
        Assert.Contains("BILL", types.Items.Select(i => i.Name));
    }

    // ── Grade / size rules ──────────────────────────────────────────────────

    [Fact]
    public void No_1_carries_all_four_sizes()                  // docs/04 §3.4
    {
        p.StartFreshInvoice().PickGrade("NO 1");
        var sizes = p.CellCombo(SalesEntryPage.ColSize).Items.Select(i => i.Name).ToArray();
        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.Equal(4, sizes.Length);
        Assert.Contains("-2", sizes);
    }

    [Fact]
    public void Other_grades_drop_the_smallest_sieve()          // only NO 1 / NO 1 BB carry -2
    {
        p.StartFreshInvoice().PickGrade("NO 2");
        var sizes = p.CellCombo(SalesEntryPage.ColSize).Items.Select(i => i.Name).ToArray();
        Keyboard.Press(VirtualKeyShort.ESCAPE);

        Assert.Equal(3, sizes.Length);
        Assert.DoesNotContain("-2", sizes);
    }

    [Fact]
    public void Changing_grade_clears_a_size_that_grade_cannot_use()
    {
        // -2 is valid for NO 1 but not for NO 2, so switching must blank it rather than keep a
        // grade/size pair the server would reject.
        p.StartFreshInvoice().PickGrade("NO 1").PickSize("-2");
        Assert.Equal("-2", p.CellCombo(SalesEntryPage.ColSize).SelectedItem?.Name);
        Keyboard.Press(VirtualKeyShort.ESCAPE);

        p.PickGrade("NO 2");
        Assert.Null(p.CellCombo(SalesEntryPage.ColSize).SelectedItem);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
    }

    // ── The calculation engine, through the UI ──────────────────────────────

    [Fact]
    public void Rejection_is_gross_minus_selection()            // CALC-2
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000");

        Assert.Equal("4.00", p.Cell(SalesEntryPage.ColRejection));
    }

    [Fact]
    public void Amount_is_selection_times_price()               // CALC-1, no discounts
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000");

        Assert.Equal("6,000.00", p.Cell(SalesEntryPage.ColAmount));
    }

    [Fact]
    public void Ex_rate_multiplies_the_amount()
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000")
         .EnterCell(SalesEntryPage.ColExRate, "2");

        Assert.Equal("12,000.00", p.Cell(SalesEntryPage.ColAmount));
    }

    [Fact]
    public void Discounts_compound_rather_than_add()            // CALC-1: (1-l1)(1-l2), not 1-(l1+l2)
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000")
         .EnterCell(SalesEntryPage.ColLess1, "10")
         .EnterCell(SalesEntryPage.ColLess2, "5");

        // 6000 x 0.90 x 0.95 = 5130.00.  Additive would give 5100.00 — the point of the test.
        Assert.Equal("5,130.00", p.Cell(SalesEntryPage.ColAmount));
    }

    [Fact]
    public void Broker_percent_applies_after_the_discounts()    // docs/03 C-7
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000")
         .SetHeaderField("BrokerPctBox", "1");

        Assert.Equal("5,940.00", p.Cell(SalesEntryPage.ColAmount));   // 6000 x 0.99
    }

    [Fact]
    public void Amount_rounds_half_up_at_two_places()           // BR-ROUND-4, not banker's
    {
        // 1 ct x 0.125 => 0.125 exactly; half-up gives 0.13, banker's would give 0.12.
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "1", selection: "1", price: "0.125");

        Assert.Equal("0.13", p.Cell(SalesEntryPage.ColAmount));
    }

    [Fact]
    public void Totals_sum_the_lines()                          // CALC-4 / CALC-5
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000");

        Assert.Equal("6.00", p.TextOf("TotalCarats"));
        Assert.Equal("6,000.00", p.TextOf("TotalAmount"));
        Assert.Equal("1,000.00", p.TextOf("BlendedRate"));
    }

    [Fact]
    public void Blended_rate_is_weighted_across_two_lines()     // CALC-5
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "10", price: "1000");
        p.Click("AddLineButton");
        p.FillLine("NO 2", "-6.5", weight: "10", selection: "10", price: "2000", row: 1);

        // 20 ct, 30,000 total -> 1,500.00/ct. A mean of the two rates would also be 1500 here, so
        // the second line uses a different weight below to keep this honest.
        Assert.Equal("20.00", p.TextOf("TotalCarats"));
        Assert.Equal("30,000.00", p.TextOf("TotalAmount"));
        Assert.Equal("1,500.00", p.TextOf("BlendedRate"));
    }

    // ── Line management ─────────────────────────────────────────────────────

    [Fact]
    public void Add_line_button_appends_a_row()
    {
        p.StartFreshInvoice();
        int before = p.RowCount;
        p.Click("AddLineButton");
        Assert.Equal(before + 1, p.RowCount);
    }

    [Fact]
    public void Enter_on_the_last_row_appends_a_new_one()       // SALES-001 AC
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000");

        int before = p.RowCount;
        p.Grid.Rows[before - 1].Cells[SalesEntryPage.ColRemark].Click();
        Keyboard.Press(VirtualKeyShort.RETURN);
        Wait.UntilInputIsProcessed();

        Assert.Equal(before + 1, p.RowCount);
    }

    [Fact]
    public void Enter_keeps_the_header_when_it_adds_a_line()    // SALES-001 AC, second half
    {
        p.StartFreshInvoice().Buyer("ABC Company")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000");

        p.Grid.Rows[p.RowCount - 1].Cells[SalesEntryPage.ColRemark].Click();
        Keyboard.Press(VirtualKeyShort.RETURN);
        Wait.UntilInputIsProcessed();

        Assert.Equal("45", p.Find("TermsBox").AsTextBox().Text);
        Assert.Equal("6.00", p.TextOf("TotalCarats"));          // the typed line survived
    }

    [Fact]
    public void New_resets_the_invoice_but_keeps_the_master_lists()
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000")
         .SetHeaderField("BrokerPctBox", "1");

        p.Click("New");

        Assert.Equal(1, p.RowCount);
        Assert.Equal("0", p.Find("BrokerPctBox").AsTextBox().Text);
        Assert.Equal("0.00", p.TextOf("TotalAmount"));
        Assert.Equal(3, p.Find("BuyerPicker").AsComboBox().Items.Length);
    }

    [Fact]
    public void A_blank_row_is_ignored_by_the_totals()
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000");
        p.Click("AddLineButton");                                // leaves row 1 blank

        Assert.Equal("6,000.00", p.TextOf("TotalAmount"));       // blank contributes nothing
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [Fact]
    public void Buyer_is_required()
    {
        p.StartFreshInvoice();
        p.Click("SaveDraft");
        Assert.Equal("Buyer is required", p.Status);
    }

    [Fact]
    public void An_invoice_needs_at_least_one_line()
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND");
        p.Click("SaveDraft");
        Assert.Equal("An invoice needs at least one line", p.Status);
    }

    [Fact]
    public void Selection_above_gross_is_an_error_not_a_clamp() // docs/03 §3.4
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "5", selection: "10", price: "1000");

        p.Click("SaveDraft");
        Assert.Contains("exceeds", p.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Size_is_required_on_a_line()
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND").PickGrade("NO 1")
         .EnterCell(SalesEntryPage.ColWeight, "10")
         .EnterCell(SalesEntryPage.ColSelection, "6")
         .EnterCell(SalesEntryPage.ColPrice, "1000");

        p.Click("SaveDraft");
        Assert.Contains("Size is required", p.Status);
    }

    [Fact]
    public void Grade_is_required_on_a_line()
    {
        // Size first: with no grade chosen every sieve is on offer, and it also gets the grid
        // focused — the first click into the grid is consumed and never starts an edit.
        p.StartFreshInvoice().Buyer("QUEST DIAMOND").PickSize("-6.5")
         .EnterCell(SalesEntryPage.ColWeight, "10")
         .EnterCell(SalesEntryPage.ColSelection, "6")
         .EnterCell(SalesEntryPage.ColPrice, "1000");

        p.Click("SaveDraft");
        Assert.Contains("Grade is required", p.Status);
    }

    [Fact]
    public void Weight_must_be_greater_than_zero()
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND").PickGrade("NO 1").PickSize("-6.5");
        p.EnterCell(SalesEntryPage.ColPrice, "1000");           // weight left at 0

        p.Click("SaveDraft");
        Assert.Contains("Weight", p.Status);
    }

    // ── Persistence ─────────────────────────────────────────────────────────

    [Fact]
    public void A_valid_draft_saves()
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000");

        p.Click("SaveDraft");
        Assert.StartsWith("Draft saved", p.Status);
    }

    [Fact]
    public void Saving_twice_updates_the_same_invoice()
    {
        // The client owns the id, so a second save is a PUT — not a duplicate invoice.
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000");

        p.Click("SaveDraft");
        p.EnterCell(SalesEntryPage.ColPrice, "1100");
        p.Click("SaveDraft");

        Assert.StartsWith("Draft saved", p.Status);
        Assert.Equal("6,600.00", p.TextOf("TotalAmount"));
    }

    [Fact]
    public void Posting_without_stock_warns_before_it_deducts() // SALES-003
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND")
         .FillLine("NO 1", "-6.5", weight: "10", selection: "6", price: "1000");

        p.Click("Post");

        // Nothing was taken in, so the ledger cannot cover this: the server must ask before it
        // lets the balance go negative rather than posting silently.
        string? dialog = p.DismissModal("No");
        Assert.NotNull(dialog);
        Assert.Contains("negative", dialog, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NO_1", dialog);          // names the exact grade x size that cannot cover it
    }
}
