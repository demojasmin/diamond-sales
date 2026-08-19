using Microsoft.EntityFrameworkCore;

namespace DiamondApi;

// Schema per docs/03-domain-model.md §2. One class per table, plain FK guids, no navigation soup.
// Ids are uuid v7 (time-ordered) minted by whoever creates the row — client or server (docs/03 §2.1).

public class AppUser
{
    public Guid UserId { get; set; } = Guid.CreateVersion7();
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Sales;        // SALES | MANAGER | OWNER
    public bool Active { get; set; } = true;
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class Roles
{
    public const string Sales = "SALES", Manager = "MANAGER", Owner = "OWNER";

    /// docs/03 §4 — the capability map is code, not a permissions table (correction C-6).
    public static bool AtLeastManager(string role) => role is Manager or Owner;
    public static bool IsOwner(string role) => role == Owner;
}

public class Session
{
    public string Token { get; set; } = "";
    public Guid UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class Grade
{
    public Guid GradeId { get; set; } = Guid.CreateVersion7();
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Active { get; set; } = true;
}

public class GradeAlias
{
    public string Alias { get; set; } = "";
    public Guid GradeId { get; set; }
}

public class SizeBucket
{
    public Guid SizeId { get; set; } = Guid.CreateVersion7();
    public string Code { get; set; } = "";
    public decimal? LowerMm { get; set; }                  // unknown until Q6
    public decimal? UpperMm { get; set; }
    public int SortOrder { get; set; }
}

public class SizeAlias
{
    public string Alias { get; set; } = "";
    public Guid SizeId { get; set; }
}

/// docs/04 §3.4 — sizes are per grade. Only NO 1 and NO 1 BB carry the -2 bucket.
public class GradeSize
{
    public Guid GradeId { get; set; }
    public Guid SizeId { get; set; }
}

public class Buyer
{
    public Guid BuyerId { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public int DefaultTermsDays { get; set; }
    public decimal? CreditLimit { get; set; }
    public bool Active { get; set; } = true;
}

public class Broker
{
    public Guid BrokerId { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public decimal DefaultBrokerPct { get; set; }
    public bool Active { get; set; } = true;
}

public class PriceListEntry
{
    public Guid PriceId { get; set; } = Guid.CreateVersion7();
    public Guid GradeId { get; set; }
    public Guid SizeId { get; set; }
    public string Context { get; set; } = "SALE";          // STOCK | REJECTION | SALE
    public decimal PricePerCt { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }             // null = current
}

public class SalesInvoice
{
    public Guid InvoiceId { get; set; } = Guid.CreateVersion7();
    public string? InvoiceNo { get; set; }                 // server-assigned AT POST (docs/03 §2.3)
    public DateOnly InvoiceDate { get; set; }
    public Guid BuyerId { get; set; }
    public Guid? BrokerId { get; set; }
    public decimal BrokerPct { get; set; }
    public int TermsDays { get; set; }
    public string DocType { get; set; } = "BILL";
    public string Status { get; set; } = InvoiceStatus.Draft;
    public int Version { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? PostedBy { get; set; }
    public DateTime? PostedAt { get; set; }
    public Guid? CancelledBy { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
}

public static class InvoiceStatus
{
    public const string Draft = "DRAFT", Posted = "POSTED", Cancelled = "CANCELLED";
}

public class SalesLine
{
    public Guid LineId { get; set; } = Guid.CreateVersion7();
    public Guid InvoiceId { get; set; }
    public int LineNo { get; set; }
    public Guid GradeId { get; set; }
    public Guid SizeId { get; set; }
    public decimal GrossWeightCt { get; set; }
    public decimal SelectionCt { get; set; }
    public decimal RejectionCt { get; set; }               // CALC-2, computed on write
    public decimal PricePerCt { get; set; }
    public decimal ExRate { get; set; } = 1m;
    public decimal Less1Pct { get; set; }
    public decimal Less2Pct { get; set; }
    public decimal Amount { get; set; }                    // CALC-1, the rounding boundary
    public string? Remark { get; set; }
}

public class Receipt
{
    public Guid ReceiptId { get; set; } = Guid.CreateVersion7();
    public Guid InvoiceId { get; set; }
    public DateOnly ReceiptDate { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "CASH";
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsWriteOff { get; set; }                   // PAY-003 rounding adjustment
}

public class ChangeRequest
{
    public Guid RequestId { get; set; } = Guid.CreateVersion7();
    public Guid InvoiceId { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public string Proposed { get; set; } = "{}";
    public string Status { get; set; } = "OPEN";           // OPEN | APPROVED | REJECTED
    public Guid? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
}

public class StockMovement
{
    public Guid MovementId { get; set; } = Guid.CreateVersion7();
    public DateOnly MovementDate { get; set; }
    public Guid GradeId { get; set; }
    public Guid SizeId { get; set; }
    public string MovementType { get; set; } = MovementTypes.Intake;
    public decimal WeightCt { get; set; }                  // SIGNED — docs/03 correction C-5
    public decimal PricePerCt { get; set; }
    public string RefType { get; set; } = "INTAKE";
    public Guid RefId { get; set; }
    public Guid? CounterpartyGradeId { get; set; }
    public Guid? CounterpartySizeId { get; set; }
    public string? Reason { get; set; }                    // mandatory for ADJUST
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class MovementTypes
{
    public const string Intake = "INTAKE", ConvertIn = "CONVERT_IN", ConvertOut = "CONVERT_OUT",
                        Rejection = "REJECTION", Sale = "SALE", Adjust = "ADJUST";

    /// The sign is the rule. Enforced on every write so a balance is only ever SUM(weight_ct).
    public static bool SignIsValid(string type, decimal weight) => type switch
    {
        Intake or ConvertIn => weight > 0,
        ConvertOut or Rejection or Sale => weight < 0,
        Adjust => weight != 0,
        _ => false,
    };
}

/// docs/04 §2.5 — a rejection is a parent quantity with child destinations. DQ-13.
public class RejectionDisposition
{
    public Guid DispositionId { get; set; } = Guid.CreateVersion7();
    public Guid MovementId { get; set; }
    public decimal WeightCt { get; set; }
    public string Outcome { get; set; } = "RESELECT";      // RESELECT|REPAIR|REGRADE|CULET|OTHER
    public Guid? ToGradeId { get; set; }                   // required when REGRADE
    public string? Note { get; set; }
}

public class AuditEntry
{
    public Guid AuditId { get; set; } = Guid.CreateVersion7();
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Action { get; set; } = "";               // CREATE|UPDATE|POST|CANCEL|DELETE|LOGIN_FAIL
    public string? Before { get; set; }
    public string? After { get; set; }
    public Guid? UserId { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public Guid? UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// docs/07 §3 — a timeout is not evidence that a write failed, so replays must be free.
public class IdempotencyRecord
{
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Response { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DiamondDb(DbContextOptions<DiamondDb> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Grade> Grades => Set<Grade>();
    public DbSet<GradeAlias> GradeAliases => Set<GradeAlias>();
    public DbSet<SizeBucket> Sizes => Set<SizeBucket>();
    public DbSet<SizeAlias> SizeAliases => Set<SizeAlias>();
    public DbSet<GradeSize> GradeSizes => Set<GradeSize>();
    public DbSet<Buyer> Buyers => Set<Buyer>();
    public DbSet<Broker> Brokers => Set<Broker>();
    public DbSet<PriceListEntry> Prices => Set<PriceListEntry>();
    public DbSet<SalesInvoice> Invoices => Set<SalesInvoice>();
    public DbSet<SalesLine> Lines => Set<SalesLine>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<ChangeRequest> ChangeRequests => Set<ChangeRequest>();
    public DbSet<StockMovement> Movements => Set<StockMovement>();
    public DbSet<RejectionDisposition> Dispositions => Set<RejectionDisposition>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<IdempotencyRecord> Idempotency => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Keys are named as the docs name them (user_id, invoice_id …), not as EF's convention
        // guesses (AppUserId, SalesInvoiceId), so the schema reads like docs/03 §2.
        b.Entity<AppUser>().HasKey(x => x.UserId);
        b.Entity<SizeBucket>().HasKey(x => x.SizeId);
        b.Entity<PriceListEntry>().HasKey(x => x.PriceId);
        b.Entity<SalesInvoice>().HasKey(x => x.InvoiceId);
        b.Entity<SalesLine>().HasKey(x => x.LineId);
        b.Entity<ChangeRequest>().HasKey(x => x.RequestId);
        b.Entity<StockMovement>().HasKey(x => x.MovementId);
        b.Entity<RejectionDisposition>().HasKey(x => x.DispositionId);
        b.Entity<AuditEntry>().HasKey(x => x.AuditId);

        b.Entity<AppUser>().HasIndex(u => u.Username).IsUnique();
        b.Entity<Session>().HasKey(s => s.Token);
        b.Entity<Grade>().HasIndex(g => g.Code).IsUnique();
        b.Entity<GradeAlias>().HasKey(a => a.Alias);
        b.Entity<SizeBucket>().HasIndex(s => s.Code).IsUnique();
        b.Entity<SizeAlias>().HasKey(a => a.Alias);
        b.Entity<GradeSize>().HasKey(gs => new { gs.GradeId, gs.SizeId });
        b.Entity<Buyer>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Broker>().HasIndex(x => x.Name).IsUnique();
        b.Entity<SalesInvoice>().HasIndex(i => i.InvoiceNo).IsUnique();
        b.Entity<SalesLine>().HasIndex(l => l.InvoiceId);
        b.Entity<StockMovement>().HasIndex(m => new { m.GradeId, m.SizeId });
        b.Entity<StockMovement>().HasIndex(m => new { m.RefType, m.RefId });
        b.Entity<AuditEntry>().HasIndex(a => new { a.EntityType, a.EntityId });
        b.Entity<AppSetting>().HasKey(s => s.Key);
        b.Entity<IdempotencyRecord>().HasKey(i => i.Key);

        // ponytail: money/carats are `decimal` and SQLite stores them as TEXT, so no SQL-side SUM or
        // ORDER BY on them. Every aggregate here runs in memory through DiamondCalc, which is correct
        // at ~10k movements a year. On PostgreSQL the same code works and the limitation disappears.
    }
}
