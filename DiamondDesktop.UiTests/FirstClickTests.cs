namespace DiamondDesktop.UiTests;

/// <summary>
/// Isolates one question: does the very first click into the grid on a fresh screen lose the value
/// typed after it? Every other test touches the grid at least once before it types, so none of them
/// exercise this. Its own class so it gets its own app instance and the grid is genuinely untouched.
/// </summary>
public sealed class FirstClickTests(AppFixture fx) : IClassFixture<AppFixture>
{
    private readonly SalesEntryPage p = new(fx);

    [Fact]
    public void The_first_value_typed_into_a_fresh_grid_is_kept()
    {
        p.StartFreshInvoice().Buyer("QUEST DIAMOND");

        // No PickGrade/PickSize first — this click IS the first interaction with the grid.
        p.EnterCell(SalesEntryPage.ColWeight, "10");

        Assert.Equal("10.00", p.Cell(SalesEntryPage.ColWeight));
    }
}
