using System.Collections.Generic;

namespace SolitaireDesk.Design;

// ============================================================
// DESIGN-TIME SAMPLE DATA ONLY.
// Pre-formatted strings so the styled grids render realistically
// in the designer. Replace with real view-model bindings.
// ============================================================

public class InvoiceRow
{
    public string No { get; set; } = "";
    public string Date { get; set; } = "";
    public string Buyer { get; set; } = "";
    public string Broker { get; set; } = "";
    public string Lines { get; set; } = "";
    public string Carats { get; set; } = "";
    public string Amount { get; set; } = "";
    public string Received { get; set; } = "";
    public string Outstanding { get; set; } = "";
    public string Status { get; set; } = "Posted";
}

public class ReceivableRow
{
    public string No { get; set; } = "";
    public string Buyer { get; set; } = "";
    public string Date { get; set; } = "";
    public string Due { get; set; } = "";
    public string Age { get; set; } = "";
    public string Amount { get; set; } = "";
    public string Received { get; set; } = "";
    public string Outstanding { get; set; } = "";
    public string Chip { get; set; } = "Due";
}

public class GradeRow
{
    public string Index { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Aliases { get; set; } = "";
    public string Stock { get; set; } = "";
    public bool Active { get; set; } = true;
}

public class BuyerRow
{
    public string Name { get; set; } = "";
    public string Terms { get; set; } = "";
    public string Limit { get; set; } = "";
    public string Outstanding { get; set; } = "";
    public string Exposure { get; set; } = "";
    public string Invoices { get; set; } = "";
}

public class AuditRow
{
    public string When { get; set; } = "";
    public string User { get; set; } = "";
    public string Entity { get; set; } = "";
    public string Action { get; set; } = "";
    public string Change { get; set; } = "";
}

public class LineRow
{
    public string N { get; set; } = "";
    public string Size { get; set; } = "";
    public string Grade { get; set; } = "";
    public string Weight { get; set; } = "";
    public string Selection { get; set; } = "";
    public string Reject { get; set; } = "";
    public string Price { get; set; } = "";
    public string ExRate { get; set; } = "1";
    public string Less1 { get; set; } = "0";
    public string Less2 { get; set; } = "0";
    public string Amount { get; set; } = "";
    public string Remark { get; set; } = "";
}

public static class SampleData
{
    public static List<InvoiceRow> Invoices { get; } = new()
    {
        new() { No="SB/26-27/157", Date="24 Jul 26", Buyer="Quest Diamond",    Broker="Rajesh Mehta",  Lines="3", Carats="149.74", Amount="₹67,24,427", Received="₹40,00,000", Outstanding="₹27,24,427", Status="Posted" },
        new() { No="SB/26-27/156", Date="23 Jul 26", Buyer="Z K Enterprise",   Broker="Bhavesh Shah",  Lines="2", Carats="112.89", Amount="₹59,23,450", Received="₹59,23,450", Outstanding="₹0",         Status="Posted" },
        new() { No="SB/26-27/155", Date="22 Jul 26", Buyer="Meridian Diam",    Broker="Nilesh Patel",  Lines="4", Carats="204.10", Amount="₹92,80,120", Received="₹50,00,000", Outstanding="₹42,80,120", Status="Posted" },
        new() { No="SB/26-27/154", Date="21 Jul 26", Buyer="Radiance Gems",    Broker="Dinesh Zaveri", Lines="1", Carats="77.99",  Amount="₹27,79,638", Received="—",          Outstanding="—",          Status="Draft" },
        new() { No="SB/26-27/153", Date="19 Jul 26", Buyer="Blue Nile Traders",Broker="Amit Kothari",  Lines="2", Carats="58.40",  Amount="₹18,42,600", Received="₹18,42,600", Outstanding="₹0",         Status="Posted" },
        new() { No="SB/26-27/152", Date="18 Jul 26", Buyer="Kiran Exports",    Broker="Rajesh Mehta",  Lines="3", Carats="131.06", Amount="₹51,90,300", Received="₹20,00,000", Outstanding="₹31,90,300", Status="Posted" },
        new() { No="SB/26-27/151", Date="16 Jul 26", Buyer="Shanti Gems",      Broker="Bhavesh Shah",  Lines="1", Carats="12.65",  Amount="₹4,50,858",  Received="—",          Outstanding="—",          Status="Cancelled" },
        new() { No="SB/26-27/150", Date="15 Jul 26", Buyer="Aurum Diamonds",   Broker="Nilesh Patel",  Lines="2", Carats="88.20",  Amount="₹33,17,400", Received="₹33,17,400", Outstanding="₹0",         Status="Posted" },
        new() { No="SB/26-27/149", Date="14 Jul 26", Buyer="Quest Diamond",    Broker="Dinesh Zaveri", Lines="2", Carats="96.11",  Amount="₹41,08,900", Received="₹15,00,000", Outstanding="₹26,08,900", Status="Posted" },
        new() { No="SB/26-27/148", Date="12 Jul 26", Buyer="Z K Enterprise",   Broker="Amit Kothari",  Lines="3", Carats="160.33", Amount="₹72,45,110", Received="₹72,45,110", Outstanding="₹0",         Status="Posted" },
    };

    public static List<InvoiceRow> Recent { get; } = Invoices.GetRange(0, 8);

    public static List<ReceivableRow> Receivables { get; } = new()
    {
        new() { No="SB/26-27/149", Buyer="Quest Diamond",    Date="14 Jul 26", Due="28 Aug 26", Age="97d", Amount="₹41,08,900", Received="₹15,00,000", Outstanding="₹26,08,900", Chip="Overdue" },
        new() { No="SB/26-27/152", Buyer="Kiran Exports",    Date="18 Jul 26", Due="16 Oct 26", Age="72d", Amount="₹51,90,300", Received="₹20,00,000", Outstanding="₹31,90,300", Chip="Overdue" },
        new() { No="SB/26-27/155", Buyer="Meridian Diam",    Date="22 Jul 26", Due="19 Nov 26", Age="41d", Amount="₹92,80,120", Received="₹50,00,000", Outstanding="₹42,80,120", Chip="Overdue" },
        new() { No="SB/26-27/157", Buyer="Quest Diamond",    Date="24 Jul 26", Due="07 Sep 26", Age="12d", Amount="₹67,24,427", Received="₹40,00,000", Outstanding="₹27,24,427", Chip="Overdue" },
        new() { No="SB/26-27/144", Buyer="Blue Nile Traders",Date="09 Jul 26", Due="23 Aug 26", Age="8d left", Amount="₹22,15,000", Received="₹16,00,000", Outstanding="₹6,15,000",  Chip="Due" },
        new() { No="SB/26-27/141", Buyer="Shanti Gems",      Date="05 Jul 26", Due="19 Sep 26", Age="21d left", Amount="₹9,80,400", Received="₹3,80,400", Outstanding="₹6,00,000",  Chip="Due" },
    };

    public static List<GradeRow> Grades { get; } = new()
    {
        new() { Index="1",  Code="NO 1",    Name="No. 1 Clean",         Aliases="1",       Stock="1,842.60", Active=true },
        new() { Index="2",  Code="NO 1 BB", Name="No. 1 Bottom Black",  Aliases="1BB, 1 BB", Stock="1,204.30", Active=true },
        new() { Index="3",  Code="NO 2",    Name="No. 2 Clean",         Aliases="2",       Stock="988.10",   Active=true },
        new() { Index="4",  Code="NO II",   Name="No. II Spotted",      Aliases="2I",      Stock="1,377.90", Active=true },
        new() { Index="5",  Code="NO DX",   Name="No. DX Deluxe",       Aliases="DX",      Stock="642.00",   Active=true },
        new() { Index="6",  Code="EX 1",    Name="Extra No. 1",         Aliases="X1",      Stock="731.40",   Active=true },
        new() { Index="7",  Code="TOP-COL", Name="Top Colour",          Aliases="TC",      Stock="410.20",   Active=true },
        new() { Index="8",  Code="GH",      Name="Ghost",               Aliases="",        Stock="0.00",     Active=false },
        new() { Index="9",  Code="LB 2",    Name="Light Brown 2",       Aliases="LB2",     Stock="58.40",    Active=false },
        new() { Index="10", Code="EXTRA",   Name="Extra Assort",        Aliases="EX",      Stock="126.70",   Active=true },
    };

    public static List<BuyerRow> Buyers { get; } = new()
    {
        new() { Name="Quest Diamond",     Terms="45 days",          Limit="₹1,50,00,000", Outstanding="₹53,33,327", Exposure="36%", Invoices="7" },
        new() { Name="Z K Enterprise",    Terms="90 days",          Limit="₹2,50,00,000", Outstanding="₹0",         Exposure="0%",  Invoices="6" },
        new() { Name="Meridian Diam",     Terms="120 days",         Limit="₹4,00,00,000", Outstanding="₹42,80,120", Exposure="78%", Invoices="5" },
        new() { Name="Radiance Gems",     Terms="Cash / immediate", Limit="₹80,00,000",   Outstanding="₹0",         Exposure="0%",  Invoices="4" },
        new() { Name="Blue Nile Traders", Terms="45 days",          Limit="₹1,20,00,000", Outstanding="₹6,15,000",  Exposure="55%", Invoices="5" },
        new() { Name="Kiran Exports",     Terms="90 days",          Limit="₹3,00,00,000", Outstanding="₹31,90,300", Exposure="58%", Invoices="6" },
    };

    public static List<AuditRow> Audit { get; } = new()
    {
        new() { When="24 Jul 26 18:07", User="h.parekh", Entity="SB/26-27/155", Action="Post",   Change="Status Draft → Posted · ₹92,80,120" },
        new() { When="24 Jul 26 17:44", User="h.parekh", Entity="Receipt",      Action="Create", Change="SB/26-27/141 · ₹6,00,000 RTGS" },
        new() { When="23 Jul 26 11:22", User="n.desai",  Entity="Grade LB 2",   Action="Update", Change="Active true → false" },
        new() { When="23 Jul 26 10:05", User="h.parekh", Entity="SB/26-27/139", Action="Update", Change="Line 2 Less 1 % 0 → 2.5" },
        new() { When="22 Jul 26 16:30", User="s.raval",  Entity="Stock",        Action="Intake", Change="NO 1 · −6.5 · 420.00 ct @ ₹56,400" },
        new() { When="22 Jul 26 09:48", User="n.desai",  Entity="Buyer Meridian", Action="Update", Change="Credit limit ₹3,00,00,000 → ₹4,00,00,000" },
        new() { When="21 Jul 26 15:12", User="h.parekh", Entity="SB/26-27/151", Action="Cancel", Change="Status Posted → Cancelled" },
        new() { When="21 Jul 26 12:03", User="s.raval",  Entity="Stock",        Action="Convert", Change="NO 2 → NO DX · +6.5 · 64.00 ct" },
    };

    public static List<LineRow> EntryLines { get; } = new()
    {
        new() { N="1", Size="+11",  Grade="NO II", Weight="232.86", Selection="149.74", Reject="83.12", Price="47,251", ExRate="1", Less1="4", Less2="0", Amount="₹67,24,427", Remark="Culet repair" },
        new() { N="2", Size="+6.5", Grade="NO II", Weight="93.81",  Selection="77.99",  Reject="15.82", Price="37,501", ExRate="1", Less1="4", Less2="0", Amount="₹27,79,638", Remark="" },
        new() { N="3", Size="−6.5", Grade="NO II", Weight="14.18",  Selection="12.65",  Reject="1.53",  Price="37,501", ExRate="1", Less1="4", Less2="0", Amount="₹4,50,858",  Remark="" },
    };
}
