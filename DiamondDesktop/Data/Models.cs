using Newtonsoft.Json;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace DiamondDesktop.Data;

// Writable tables. created_at/updated_at are deliberately unmapped everywhere: Postgres owns them,
// and a mapped DateTime would be sent as 0001-01-01 on insert. Read them from the views instead.

[Table("profiles")]
public class Profile : BaseModel
{
    [PrimaryKey("id", true)] public Guid Id { get; set; }
    [Column("full_name")] public string? FullName { get; set; }
    [Column("role")] public string Role { get; set; } = "";
    [Column("active")] public bool Active { get; set; } = true;
}

[Table("grade")]
public class Grade : BaseModel
{
    // 'aliases' is intentionally unmapped - its Postgres type (text vs text[]) is unverified and a
    // wrong guess breaks every read of this table. Map it once the DDL is confirmed.
    [PrimaryKey("grade_id", false)] public long GradeId { get; set; }
    [Column("code")] public string Code { get; set; } = "";
    [Column("display_name")] public string? DisplayName { get; set; }
    [Column("sort_order")] public int SortOrder { get; set; }
    [Column("active")] public bool Active { get; set; } = true;
}

[Table("size_bucket")]
public class SizeBucket : BaseModel
{
    [PrimaryKey("size_id", false)] public long SizeId { get; set; }
    [Column("code")] public string Code { get; set; } = "";
    [Column("lower_mm")] public decimal? LowerMm { get; set; }
    [Column("upper_mm")] public decimal? UpperMm { get; set; }
    [Column("sort_order")] public int SortOrder { get; set; }
}

[Table("buyer")]
public class Buyer : BaseModel
{
    [PrimaryKey("buyer_id", false)] public long BuyerId { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("default_terms_days")] public int DefaultTermsDays { get; set; }
    [Column("credit_limit")] public decimal? CreditLimit { get; set; }
    [Column("active")] public bool Active { get; set; } = true;
}

[Table("broker")]
public class Broker : BaseModel
{
    [PrimaryKey("broker_id", false)] public long BrokerId { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("default_broker_pct")] public decimal DefaultBrokerPct { get; set; }
    [Column("active")] public bool Active { get; set; } = true;
}

[Table("currency")]
public class Currency : BaseModel
{
    [PrimaryKey("currency_id", false)] public long CurrencyId { get; set; }
    [Column("code")] public string Code { get; set; } = "";
    [Column("latest_rate_to_base")] public decimal LatestRateToBase { get; set; } = 1m;
}

[Table("price_list")]
public class PriceList : BaseModel
{
    [PrimaryKey("price_id", false)] public long PriceId { get; set; }
    [Column("grade_id")] public long GradeId { get; set; }
    [Column("size_id")] public long SizeId { get; set; }
    [Column("context")] public string Context { get; set; } = PriceContext.SALE;
    [Column("price_per_ct")] public decimal PricePerCt { get; set; }
    [Column("effective_from")] public DateOnly EffectiveFrom { get; set; }
    [Column("effective_to")] public DateOnly? EffectiveTo { get; set; }
}

[Table("sales_invoice")]
public class SalesInvoice : BaseModel
{
    [PrimaryKey("invoice_id", false)] public long InvoiceId { get; set; }
    // NULL until post_invoice() assigns it, and CHECK (status <> 'POSTED' or invoice_no is not null)
    // once it has. Never written by the client: a read-modify-write that started before someone else
    // posted would otherwise PATCH the number back to NULL and un-post the invoice.
    [Column("invoice_no", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? InvoiceNo { get; set; }
    [Column("invoice_date")] public DateOnly InvoiceDate { get; set; }
    [Column("buyer_id")] public long BuyerId { get; set; }
    [Column("broker_id")] public long? BrokerId { get; set; }
    [Column("broker_pct")] public decimal BrokerPct { get; set; }
    [Column("terms_days")] public int TermsDays { get; set; }
    [Column("doc_type")] public string DocType { get; set; } = "BILL";
    [Column("currency_id")] public long CurrencyId { get; set; }
    // Set once, at insert. Only post_invoice()/cancel_invoice() may move it after that.
    [Column("status", ignoreOnUpdate: true)] public string Status { get; set; } = InvoiceStatus.DRAFT;
    [Column("created_by")] public Guid? CreatedBy { get; set; }
    [Column("updated_by")] public Guid? UpdatedBy { get; set; }
    [Column("client_ref")] public Guid? ClientRef { get; set; }
}

[Table("sales_line")]
public class SalesLine : BaseModel
{
    // rejection_ct is GENERATED. Mapping it here makes every insert fail. Read it from VSalesLine.
    [PrimaryKey("line_id", false)] public long LineId { get; set; }
    [Column("invoice_id")] public long InvoiceId { get; set; }
    [Column("grade_id")] public long GradeId { get; set; }
    [Column("size_id")] public long SizeId { get; set; }
    [Column("gross_weight_ct")] public decimal GrossWeightCt { get; set; }
    [Column("selection_ct")] public decimal SelectionCt { get; set; }
    [Column("price_per_ct")] public decimal PricePerCt { get; set; }
    [Column("ex_rate")] public decimal ExRate { get; set; } = 1m;
    [Column("less1_pct")] public decimal Less1Pct { get; set; }
    [Column("less2_pct")] public decimal Less2Pct { get; set; }
    [Column("remark")] public string? Remark { get; set; }
}

[Table("receipt")]
public class Receipt : BaseModel
{
    [PrimaryKey("receipt_id", false)] public long ReceiptId { get; set; }
    [Column("invoice_id")] public long InvoiceId { get; set; }
    [Column("receipt_date")] public DateOnly ReceiptDate { get; set; }
    // Cash actually received - an input, unlike the computed amounts on the invoice views.
    [Column("amount")] public decimal Amount { get; set; }
    [Column("method")] public string? Method { get; set; }
    [Column("reference")] public string? Reference { get; set; }
    [Column("created_by")] public Guid? CreatedBy { get; set; }
    [Column("client_ref")] public Guid? ClientRef { get; set; }
}

[Table("rough_intake")]
public class RoughIntake : BaseModel
{
    [PrimaryKey("intake_id", false)] public long IntakeId { get; set; }
    [Column("intake_date")] public DateOnly IntakeDate { get; set; }
    [Column("grade_id")] public long GradeId { get; set; }
    [Column("size_id")] public long SizeId { get; set; }
    [Column("weight_ct")] public decimal WeightCt { get; set; }
    [Column("price_per_ct")] public decimal PricePerCt { get; set; }
    [Column("created_by")] public Guid? CreatedBy { get; set; }
}

/// <summary>Append only: never UPDATE or DELETE. Corrections are offsetting ADJUST rows.</summary>
[Table("stock_movement")]
public class StockMovement : BaseModel
{
    [PrimaryKey("movement_id", false)] public long MovementId { get; set; }
    [Column("movement_date")] public DateOnly MovementDate { get; set; }
    [Column("grade_id")] public long GradeId { get; set; }
    [Column("size_id")] public long SizeId { get; set; }
    [Column("movement_type")] public string MovementType { get; set; } = "";
    // Negative only when MovementType == Movement.ADJUST.
    [Column("weight_ct")] public decimal WeightCt { get; set; }
    [Column("price_per_ct")] public decimal? PricePerCt { get; set; }
    [Column("ref_type")] public string? RefType { get; set; }
    [Column("ref_id")] public long? RefId { get; set; }
    [Column("counterparty_grade_id")] public long? CounterpartyGradeId { get; set; }
    // Mandatory when MovementType == Movement.ADJUST.
    [Column("reason")] public string? Reason { get; set; }
    [Column("created_by")] public Guid? CreatedBy { get; set; }
    [Column("client_ref")] public Guid? ClientRef { get; set; }
}

[Table("app_config")]
public class AppConfig : BaseModel
{
    [PrimaryKey("key", true)] public string Key { get; set; } = "";
    [Column("value")] public string? Value { get; set; }
    [Column("description")] public string? Description { get; set; }
}

/// <summary>Read only.</summary>
[Table("audit_log")]
public class AuditLog : BaseModel
{
    [Column("audit_id")] public long AuditId { get; set; }
    // Not TableName: that is BaseModel's own, and shadowing it makes an audit row read back as
    // "audit_log" through any BaseModel-typed reference. The column mapping is unaffected.
    [Column("table_name")] public string AuditedTable { get; set; } = "";
    [Column("record_id")] public long? RecordId { get; set; }
    [Column("action")] public string Action { get; set; } = "";
    [Column("changed_by")] public Guid? ChangedBy { get; set; }
    [Column("changed_at")] public DateTime ChangedAt { get; set; }
    [Column("old_values")] public Dictionary<string, object>? OldValues { get; set; }
    [Column("new_values")] public Dictionary<string, object>? NewValues { get; set; }
}

// Views. Read only - no PrimaryKey, never inserted or updated. Every computed number lives here.

[Table("v_sales_line")]
public class VSalesLine : BaseModel
{
    [Column("line_id")] public long LineId { get; set; }
    [Column("invoice_id")] public long InvoiceId { get; set; }
    [Column("invoice_no")] public string? InvoiceNo { get; set; }
    [Column("invoice_date")] public DateOnly InvoiceDate { get; set; }
    [Column("status")] public string Status { get; set; } = "";
    [Column("buyer_id")] public long BuyerId { get; set; }
    [Column("created_by")] public Guid? CreatedBy { get; set; }
    [Column("grade_id")] public long GradeId { get; set; }
    [Column("grade_code")] public string GradeCode { get; set; } = "";
    [Column("size_id")] public long SizeId { get; set; }
    [Column("size_code")] public string SizeCode { get; set; } = "";
    [Column("gross_weight_ct")] public decimal GrossWeightCt { get; set; }
    [Column("selection_ct")] public decimal SelectionCt { get; set; }
    [Column("rejection_ct")] public decimal RejectionCt { get; set; }
    [Column("price_per_ct")] public decimal PricePerCt { get; set; }
    [Column("ex_rate")] public decimal ExRate { get; set; }
    [Column("less1_pct")] public decimal Less1Pct { get; set; }
    [Column("less2_pct")] public decimal Less2Pct { get; set; }
    [Column("broker_pct")] public decimal BrokerPct { get; set; }
    [Column("amount")] public decimal Amount { get; set; }
    [Column("amount_pre_broker")] public decimal AmountPreBroker { get; set; }
    [Column("remark")] public string? Remark { get; set; }
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}

[Table("v_invoice")]
public class VInvoice : BaseModel
{
    [Column("invoice_id")] public long InvoiceId { get; set; }
    [Column("invoice_no")] public string? InvoiceNo { get; set; }
    [Column("invoice_date")] public DateOnly InvoiceDate { get; set; }
    [Column("buyer_id")] public long BuyerId { get; set; }
    [Column("buyer_name")] public string BuyerName { get; set; } = "";
    [Column("credit_limit")] public decimal? CreditLimit { get; set; }
    [Column("broker_id")] public long? BrokerId { get; set; }
    [Column("broker_name")] public string? BrokerName { get; set; }
    [Column("broker_pct")] public decimal BrokerPct { get; set; }
    [Column("terms_days")] public int TermsDays { get; set; }
    [Column("doc_type")] public string DocType { get; set; } = "";
    [Column("status")] public string Status { get; set; } = "";
    [Column("created_by")] public Guid? CreatedBy { get; set; }
    [Column("salesperson")] public string? Salesperson { get; set; }
    [Column("amount_total")] public decimal AmountTotal { get; set; }
    [Column("carats_sold")] public decimal CaratsSold { get; set; }
    [Column("received")] public decimal Received { get; set; }
    [Column("outstanding")] public decimal Outstanding { get; set; }
    [Column("blended_rate")] public decimal? BlendedRate { get; set; }
    [Column("broker_payable")] public decimal BrokerPayable { get; set; }
    [Column("due_date")] public DateOnly DueDate { get; set; }
    [Column("is_overdue")] public bool IsOverdue { get; set; }
    [Column("days_overdue")] public int DaysOverdue { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}

[Table("v_stock_position")]
public class VStockPosition : BaseModel
{
    [Column("grade_id")] public long GradeId { get; set; }
    [Column("grade_code")] public string GradeCode { get; set; } = "";
    [Column("grade_name")] public string? GradeName { get; set; }
    [Column("size_id")] public long SizeId { get; set; }
    [Column("size_code")] public string SizeCode { get; set; } = "";
    [Column("balance_ct")] public decimal BalanceCt { get; set; }
    [Column("avg_cost")] public decimal? AvgCost { get; set; }
    [Column("stock_value")] public decimal StockValue { get; set; }
    [Column("oldest_intake")] public DateOnly? OldestIntake { get; set; }
    [Column("age_days")] public int? AgeDays { get; set; }
}

[Table("v_stock_movement")]
public class VStockMovement : BaseModel
{
    [Column("movement_id")] public long MovementId { get; set; }
    [Column("movement_date")] public DateOnly MovementDate { get; set; }
    [Column("grade_code")] public string GradeCode { get; set; } = "";
    [Column("size_code")] public string SizeCode { get; set; } = "";
    [Column("movement_type")] public string MovementType { get; set; } = "";
    [Column("weight_ct")] public decimal WeightCt { get; set; }
    [Column("price_per_ct")] public decimal? PricePerCt { get; set; }
    [Column("ref_type")] public string? RefType { get; set; }
    [Column("ref_id")] public long? RefId { get; set; }
    [Column("counterparty_grade_code")] public string? CounterpartyGradeCode { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("updated_at")] public DateTime UpdatedAt { get; set; }
}

[Table("v_receivables_ageing")]
public class VReceivablesAgeing : BaseModel
{
    [Column("invoice_id")] public long InvoiceId { get; set; }
    [Column("invoice_no")] public string? InvoiceNo { get; set; }
    [Column("buyer_id")] public long BuyerId { get; set; }
    [Column("buyer_name")] public string BuyerName { get; set; } = "";
    [Column("outstanding")] public decimal Outstanding { get; set; }
    [Column("due_date")] public DateOnly DueDate { get; set; }
    [Column("days_overdue")] public int DaysOverdue { get; set; }
    [Column("is_overdue")] public bool IsOverdue { get; set; }
    [Column("age_bucket")] public string AgeBucket { get; set; } = "";
}

[Table("v_reconciliation")]
public class VReconciliation : BaseModel
{
    [Column("grade_code")] public string GradeCode { get; set; } = "";
    [Column("size_code")] public string SizeCode { get; set; } = "";
    [Column("moved_out_ct")] public decimal MovedOutCt { get; set; }
    [Column("sold_on_invoices_ct")] public decimal SoldOnInvoicesCt { get; set; }
    [Column("diff_ct")] public decimal DiffCt { get; set; }
    [Column("reconciles")] public bool Reconciles { get; set; }
}

/// The one shape that is neither a table nor a view: dashboard_summary() returns a row set, so it
/// arrives as plain JSON from Rpc rather than through a BaseModel. Rpc&lt;T&gt; hydrates through
/// JSON.NET, so these must be Newtonsoft attributes — System.Text.Json ones are invisible to it and
/// every field would silently read back as zero.
/// A date range with no invoices makes each sum() NULL, which is zero here, not a crash.
[JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
public sealed class DashboardSummary
{
    [JsonProperty("sales_amount")]      public decimal SalesAmount { get; set; }
    [JsonProperty("carats_sold")]       public decimal CaratsSold { get; set; }
    [JsonProperty("blended_rate")]      public decimal BlendedRate { get; set; }
    [JsonProperty("invoice_count")]     public long InvoiceCount { get; set; }
    [JsonProperty("outstanding_total")] public decimal OutstandingTotal { get; set; }
    [JsonProperty("overdue_total")]     public decimal OverdueTotal { get; set; }
    [JsonProperty("overdue_count")]     public long OverdueCount { get; set; }
    [JsonProperty("stock_value")]       public decimal StockValue { get; set; }
    [JsonProperty("stock_carats")]      public decimal StockCarats { get; set; }
}

// Check constraint values. Wrong case = rejected insert, so never hand-type these strings.

public static class Movement
{
    public const string INTAKE = "INTAKE";
    public const string CONVERT_IN = "CONVERT_IN";
    public const string CONVERT_OUT = "CONVERT_OUT";
    public const string REJECTION = "REJECTION";
    public const string SALE = "SALE";
    public const string ADJUST = "ADJUST";
}

public static class InvoiceStatus
{
    public const string DRAFT = "DRAFT";
    public const string POSTED = "POSTED";
    public const string CANCELLED = "CANCELLED";
}

public static class PriceContext
{
    public const string STOCK = "STOCK";
    public const string REJECTION = "REJECTION";
    public const string SALE = "SALE";
}
