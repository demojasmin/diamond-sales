using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiamondCalc;
using Microsoft.EntityFrameworkCore;

namespace DiamondApi;

public static class Audit
{
    /// AUD-001. Append-only: this is the only method that ever touches the table.
    public static void Write(DiamondDb db, string entityType, Guid id, string action,
                             object? before = null, object? after = null, Guid? userId = null)
        => db.Audit.Add(new AuditEntry
        {
            EntityType = entityType,
            EntityId = id,
            Action = action,
            Before = before is null ? null : JsonSerializer.Serialize(before),
            After = after is null ? null : JsonSerializer.Serialize(after),
            UserId = userId,
        });
}

public sealed record StockRow(Guid GradeId, string GradeCode, Guid SizeId, string SizeCode,
                              decimal BalanceCt, decimal AvgPricePerCt, decimal Value);

public static class Stock
{
    /// CALC-7. Balance is SUM of signed weights and is stored nowhere (docs/03 §3.6).
    public static decimal Balance(DiamondDb db, Guid gradeId, Guid sizeId)
        => Calc.Balance(db.Movements.Where(m => m.GradeId == gradeId && m.SizeId == sizeId)
                          .Select(m => m.WeightCt).AsEnumerable());

    /// INV-003. Every grade × size that has ever moved, valued with CALC-6.
    public static List<StockRow> Position(DiamondDb db)
    {
        var grades = db.Grades.ToDictionary(g => g.GradeId);
        var sizes = db.Sizes.ToDictionary(s => s.SizeId);

        return db.Movements.AsEnumerable()
            .GroupBy(m => (m.GradeId, m.SizeId))
            .Select(g =>
            {
                decimal balance = Calc.Balance(g.Select(m => m.WeightCt));

                // Cost basis comes from ACQUISITIONS only — intake and material converted in.
                // Not from ADJUST: a cancellation reversal re-adds carats at the *sale* price, and a
                // physical-count correction has no price at all. Letting either re-price stock would
                // quietly inflate inventory value and destroy the margin figure (W7/W11).
                decimal avg = Calc.WeightedAvgPrice(
                    g.Where(m => m.MovementType is MovementTypes.Intake or MovementTypes.ConvertIn)
                     .Select(m => (m.WeightCt, m.PricePerCt)));
                return new StockRow(g.Key.GradeId, grades[g.Key.GradeId].Code,
                                    g.Key.SizeId, sizes[g.Key.SizeId].Code,
                                    balance, avg, Calc.RoundMoney(balance * avg));
            })
            .OrderBy(r => grades[r.GradeId].SortOrder).ThenBy(r => sizes[r.SizeId].SortOrder)
            .ToList();
    }

    public static bool SizeAllowed(DiamondDb db, Guid gradeId, Guid sizeId)
        => db.GradeSizes.Any(gs => gs.GradeId == gradeId && gs.SizeId == sizeId);
}

public sealed record PostResult(bool Ok, string? ErrorCode, string? Message,
                                List<string> Warnings, string? OverrideToken, SalesInvoice? Invoice);

public static class Invoices
{
    public static decimal Outstanding(DiamondDb db, Guid invoiceId)
        => Calc.Outstanding(db.Lines.Where(l => l.InvoiceId == invoiceId).Select(l => l.Amount).AsEnumerable(),
                            db.Receipts.Where(r => r.InvoiceId == invoiceId).Select(r => r.Amount).AsEnumerable());

    public static decimal Total(DiamondDb db, Guid invoiceId)
        => Calc.InvoiceTotal(db.Lines.Where(l => l.InvoiceId == invoiceId).Select(l => l.Amount).AsEnumerable());

    /// CALC-1/2 recomputed server-side on every write. The client's arithmetic is never trusted.
    public static string? Recalculate(DiamondDb db, SalesInvoice invoice, List<SalesLine> lines)
    {
        foreach (var line in lines)
        {
            if (!Stock.SizeAllowed(db, line.GradeId, line.SizeId))
                return "SIZE_NOT_VALID_FOR_GRADE";
            if (line.SelectionCt > line.GrossWeightCt)
                return "SELECTION_EXCEEDS_GROSS";

            line.RejectionCt = Calc.Rejection(line.GrossWeightCt, line.SelectionCt);
            line.Amount = Calc.LineAmount(line.SelectionCt, line.PricePerCt, line.ExRate,
                                          line.Less1Pct, line.Less2Pct, invoice.BrokerPct);
        }
        return null;
    }

    /// SALES-003 — docs/07 §4.4. The call this whole project exists for.
    public static PostResult Post(DiamondDb db, Guid invoiceId, AppUser actor, string? overrideToken)
    {
        var invoice = db.Invoices.FirstOrDefault(i => i.InvoiceId == invoiceId);
        if (invoice is null) return Fail("NOT_FOUND", "Unknown invoice");
        if (invoice.Status == InvoiceStatus.Posted)
            return new(true, null, "Already posted", [], null, invoice);       // idempotent replay
        if (invoice.Status == InvoiceStatus.Cancelled) return Fail("INVOICE_CANCELLED", "Invoice is cancelled");
        if (actor.Role == Roles.Sales && invoice.CreatedBy != actor.UserId)
            return Fail("FORBIDDEN", "Sales staff may only post their own invoices");

        var lines = db.Lines.Where(l => l.InvoiceId == invoiceId).OrderBy(l => l.LineNo).ToList();
        if (lines.Count == 0) return Fail("NO_LINES", "An invoice needs at least one line");

        if (Recalculate(db, invoice, lines) is { } error)
            return Fail(error, "Invoice does not validate");

        // Negative-stock policy (Q10, default WARN). A balance that was ALREADY negative is reported,
        // never hidden — the workbook ships negative balances (docs/04 B-2).
        string policy = Settings.Get(db, "negative_stock_policy") ?? "WARN";
        var warnings = new List<string>();
        foreach (var group in lines.GroupBy(l => (l.GradeId, l.SizeId)))
        {
            decimal before = Stock.Balance(db, group.Key.GradeId, group.Key.SizeId);
            decimal after = before - group.Sum(l => l.SelectionCt);
            if (after >= 0) continue;

            string code = db.Grades.First(g => g.GradeId == group.Key.GradeId).Code;
            string size = db.Sizes.First(s => s.SizeId == group.Key.SizeId).Code;
            warnings.Add(before < 0
                ? $"{code} {size}: balance was already negative ({before:N4} ct) and goes to {after:N4} ct"
                : $"{code} {size}: balance goes negative ({before:N4} → {after:N4} ct)");
        }

        if (warnings.Count > 0)
        {
            if (policy == "BLOCK")
                return new(false, "NEGATIVE_STOCK", "Posting blocked by negative-stock policy", warnings, null, null);

            string expected = OverrideTokenFor(invoice);
            if (policy == "WARN" && overrideToken != expected)
                return new(false, "NEGATIVE_STOCK", "Confirm to post over a negative balance", warnings, expected, null);
        }

        invoice.InvoiceNo ??= NextInvoiceNo(db);
        invoice.Status = InvoiceStatus.Posted;
        invoice.PostedAt = DateTime.UtcNow;
        invoice.PostedBy = actor.UserId;
        invoice.Version++;

        bool autoReject = Settings.Bool(db, "auto_reject_on_post", false);
        foreach (var line in lines)
        {
            if (line.SelectionCt > 0)
                db.Movements.Add(new StockMovement
                {
                    MovementDate = invoice.InvoiceDate,
                    GradeId = line.GradeId,
                    SizeId = line.SizeId,
                    MovementType = MovementTypes.Sale,
                    WeightCt = -line.SelectionCt,                      // the sign is the rule
                    PricePerCt = line.PricePerCt,
                    RefType = "INVOICE",
                    RefId = invoice.InvoiceId,
                    CreatedBy = actor.UserId,
                });

            if (autoReject && line.RejectionCt > 0)
                db.Movements.Add(new StockMovement
                {
                    MovementDate = invoice.InvoiceDate,
                    GradeId = line.GradeId,
                    SizeId = line.SizeId,
                    MovementType = MovementTypes.Rejection,
                    WeightCt = -line.RejectionCt,
                    PricePerCt = line.PricePerCt,
                    RefType = "INVOICE",
                    RefId = invoice.InvoiceId,
                    CreatedBy = actor.UserId,
                });
        }

        Audit.Write(db, "SalesInvoice", invoice.InvoiceId, "POST",
                    after: new { invoice.InvoiceNo, Total = Total(db, invoice.InvoiceId) }, userId: actor.UserId);
        db.SaveChanges();
        return new(true, null, null, warnings, null, invoice);
    }

    /// SALES-004. Compensating movements, never a delete — INV-6 proves the reversal is complete.
    public static PostResult Cancel(DiamondDb db, Guid invoiceId, AppUser actor, string reason)
    {
        var invoice = db.Invoices.FirstOrDefault(i => i.InvoiceId == invoiceId);
        if (invoice is null) return Fail("NOT_FOUND", "Unknown invoice");
        if (invoice.Status == InvoiceStatus.Cancelled) return new(true, null, "Already cancelled", [], null, invoice);
        if (string.IsNullOrWhiteSpace(reason)) return Fail("REASON_REQUIRED", "Cancellation needs a reason");

        var warnings = new List<string>();
        decimal received = db.Receipts.Where(r => r.InvoiceId == invoiceId).Sum(r => r.Amount);
        if (received > 0) warnings.Add($"{received:N2} has been received against this invoice");

        foreach (var movement in db.Movements.Where(m => m.RefType == "INVOICE" && m.RefId == invoiceId).ToList())
            db.Movements.Add(new StockMovement
            {
                MovementDate = DateOnly.FromDateTime(DateTime.UtcNow),
                GradeId = movement.GradeId,
                SizeId = movement.SizeId,
                MovementType = MovementTypes.Adjust,
                WeightCt = -movement.WeightCt,                          // returns the stock
                PricePerCt = movement.PricePerCt,
                RefType = "INVOICE",
                RefId = invoiceId,
                Reason = $"Reversal of cancelled invoice: {reason}",
                CreatedBy = actor.UserId,
            });

        invoice.Status = InvoiceStatus.Cancelled;
        invoice.CancelledAt = DateTime.UtcNow;
        invoice.CancelledBy = actor.UserId;
        invoice.CancelReason = reason;

        Audit.Write(db, "SalesInvoice", invoiceId, "CANCEL", after: new { reason }, userId: actor.UserId);
        db.SaveChanges();
        return new(true, null, null, warnings, null, invoice);
    }

    /// PAY-001 + PAY-003. A hand-rounded payment must not leave a phantom receivable (docs/04 §2.4).
    public static (Receipt Receipt, bool Settled, List<string> Warnings) AddReceipt(
        DiamondDb db, Guid invoiceId, DateOnly date, decimal amount, string method, AppUser actor)
    {
        var warnings = new List<string>();
        decimal before = Outstanding(db, invoiceId);
        if (amount > before) warnings.Add($"Receipt exceeds the outstanding {before:N2} — treat as advance/credit");

        var receipt = new Receipt
        {
            InvoiceId = invoiceId, ReceiptDate = date, Amount = amount,
            Method = method, CreatedBy = actor.UserId,
        };
        db.Receipts.Add(receipt);
        db.SaveChanges();

        decimal residue = Outstanding(db, invoiceId);
        decimal threshold = Settings.Dec(db, "settlement_write_off_threshold", 1.00m);
        bool settled = false;

        if (residue != 0 && Calc.IsSettled(residue, threshold))
        {
            db.Receipts.Add(new Receipt
            {
                InvoiceId = invoiceId, ReceiptDate = date, Amount = residue,   // signed: closes it exactly
                Method = "WRITE_OFF", CreatedBy = actor.UserId, IsWriteOff = true,
            });
            warnings.Add($"Residue of {residue:N2} written off as a rounding adjustment");
            settled = true;
        }

        Audit.Write(db, "Receipt", receipt.ReceiptId, "CREATE", after: new { amount, settled }, userId: actor.UserId);
        db.SaveChanges();
        return (receipt, settled, warnings);
    }

    public static string NextInvoiceNo(DiamondDb db)
    {
        int next = db.Invoices.Count(i => i.InvoiceNo != null && !i.InvoiceNo.StartsWith("MIG-")) + 1;
        return $"INV-{next:D5}";
    }

    /// Deterministic, so a client can echo it back without the server storing state.
    public static string OverrideTokenFor(SalesInvoice invoice)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{invoice.InvoiceId}:{invoice.Version}")))[..16];

    private static PostResult Fail(string code, string message) => new(false, code, message, [], null, null);
}

public static class Inventory
{
    /// INV-004. A conversion is two rows sharing one ref_id; conservation is INV-1, checked here.
    public static string? Convert(DiamondDb db, Guid fromGrade, Guid fromSize, Guid toGrade, Guid toSize,
                                  decimal weightCt, decimal pricePerCt, AppUser actor)
    {
        if (weightCt <= 0) return "WEIGHT_MUST_BE_POSITIVE";
        if (!Stock.SizeAllowed(db, fromGrade, fromSize) || !Stock.SizeAllowed(db, toGrade, toSize))
            return "SIZE_NOT_VALID_FOR_GRADE";

        var refId = Guid.CreateVersion7();
        db.Movements.Add(new StockMovement
        {
            MovementDate = DateOnly.FromDateTime(DateTime.UtcNow), GradeId = fromGrade, SizeId = fromSize,
            MovementType = MovementTypes.ConvertOut, WeightCt = -weightCt, PricePerCt = pricePerCt,
            RefType = "CONVERSION", RefId = refId,
            CounterpartyGradeId = toGrade, CounterpartySizeId = toSize, CreatedBy = actor.UserId,
        });
        db.Movements.Add(new StockMovement
        {
            MovementDate = DateOnly.FromDateTime(DateTime.UtcNow), GradeId = toGrade, SizeId = toSize,
            MovementType = MovementTypes.ConvertIn, WeightCt = weightCt, PricePerCt = pricePerCt,
            RefType = "CONVERSION", RefId = refId,
            CounterpartyGradeId = fromGrade, CounterpartySizeId = fromSize, CreatedBy = actor.UserId,
        });

        Audit.Write(db, "Conversion", refId, "CREATE", after: new { fromGrade, toGrade, weightCt }, userId: actor.UserId);
        db.SaveChanges();
        return null;
    }

    /// INV-005/006. Dispositions must sum to the rejection — the workbook's comments do not, which is the point.
    public static string? Reject(DiamondDb db, Guid gradeId, Guid sizeId, decimal weightCt, decimal pricePerCt,
                                 string? reason, List<(decimal Weight, string Outcome, Guid? ToGrade, string? Note)> dispositions,
                                 AppUser actor)
    {
        if (weightCt <= 0) return "WEIGHT_MUST_BE_POSITIVE";
        if (!Stock.SizeAllowed(db, gradeId, sizeId)) return "SIZE_NOT_VALID_FOR_GRADE";

        if (dispositions.Count > 0)
        {
            if (dispositions.Sum(d => d.Weight) != weightCt) return "DISPOSITIONS_DO_NOT_SUM";
            if (dispositions.Any(d => d.Outcome == "REGRADE" && d.ToGrade is null)) return "REGRADE_REQUIRES_GRADE";
        }

        var movement = new StockMovement
        {
            MovementDate = DateOnly.FromDateTime(DateTime.UtcNow), GradeId = gradeId, SizeId = sizeId,
            MovementType = MovementTypes.Rejection, WeightCt = -weightCt, PricePerCt = pricePerCt,
            RefType = "REJECTION", RefId = Guid.CreateVersion7(), Reason = reason, CreatedBy = actor.UserId,
        };
        db.Movements.Add(movement);

        foreach (var (weight, outcome, toGrade, note) in dispositions)
            db.Dispositions.Add(new RejectionDisposition
            {
                MovementId = movement.MovementId, WeightCt = weight,
                Outcome = outcome, ToGradeId = toGrade, Note = note,
            });

        Audit.Write(db, "StockMovement", movement.MovementId, "CREATE",
                    after: new { weightCt, dispositions = dispositions.Count }, userId: actor.UserId);
        db.SaveChanges();
        return null;
    }
}

/// OPS-001. They can copy a workbook to a pen drive today; anything less is a regression.
public static class Backup
{
    /// `VACUUM INTO` writes a consistent copy even while the database is in use — no file locking,
    /// no half-written page. One statement beats a backup framework.
    public static (bool Ok, string Detail) Create(DiamondDb db, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"diamond-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
            db.Database.ExecuteSqlRaw($"VACUUM INTO '{path.Replace("'", "''")}'");

            var info = new FileInfo(path);
            return (true, $"{path} ({info.Length / 1024} KB)");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);          // a silent backup is not a backup
        }
    }

    public static List<object> List(string folder)
        => !Directory.Exists(folder) ? []
         : new DirectoryInfo(folder).GetFiles("diamond-*.db")
             .OrderByDescending(f => f.CreationTimeUtc)
             .Select(f => (object)new { file = f.Name, sizeKb = f.Length / 1024, createdUtc = f.CreationTimeUtc })
             .ToList();
}

/// docs/03 §3.9. The invariants that replaced CALC-8, runnable on demand and nightly.
public static class Invariants
{
    public static List<string> CheckAll(DiamondDb db)
    {
        var failures = new List<string>();

        foreach (var conversion in db.Movements.Where(m => m.RefType == "CONVERSION").AsEnumerable()
                                     .GroupBy(m => m.RefId))
            if (Calc.Balance(conversion.Select(m => m.WeightCt)) != 0)
                failures.Add($"INV-1: conversion {conversion.Key} does not conserve weight");

        foreach (var movement in db.Movements.AsEnumerable())
            if (!MovementTypes.SignIsValid(movement.MovementType, movement.WeightCt))
                failures.Add($"INV-2: {movement.MovementType} movement {movement.MovementId} has the wrong sign");

        foreach (var invoice in db.Invoices.Where(i => i.Status == InvoiceStatus.Posted).AsEnumerable())
        {
            int lines = db.Lines.Count(l => l.InvoiceId == invoice.InvoiceId && l.SelectionCt > 0);
            int sales = db.Movements.Count(m => m.RefId == invoice.InvoiceId && m.MovementType == MovementTypes.Sale);
            if (lines != sales)
                failures.Add($"INV-3: invoice {invoice.InvoiceNo} has {lines} sold line(s) but {sales} SALE movement(s)");
        }

        foreach (var line in db.Lines.AsEnumerable())
            if (line.SelectionCt > line.GrossWeightCt)
                failures.Add($"INV-4: line {line.LineId} sells more than the parcel weighs");

        // INV-5, refined: docs/03 says a SALE movement must belong to a POSTED invoice, but movements
        // are append-only — a cancellation leaves the original SALE in place beside its reversal.
        // So the real rule is "never a DRAFT", and INV-6 proves the cancelled ones netted to zero.
        foreach (var movement in db.Movements.Where(m => m.MovementType == MovementTypes.Sale).AsEnumerable())
            if (!db.Invoices.Any(i => i.InvoiceId == movement.RefId && i.Status != InvoiceStatus.Draft))
                failures.Add($"INV-5: SALE movement {movement.MovementId} has no posted or cancelled invoice");

        foreach (var invoice in db.Invoices.Where(i => i.Status == InvoiceStatus.Cancelled).AsEnumerable())
        {
            var movements = db.Movements.Where(m => m.RefId == invoice.InvoiceId).Select(m => m.WeightCt).AsEnumerable();
            if (Calc.Balance(movements) != 0)
                failures.Add($"INV-6: cancelled invoice {invoice.InvoiceId} did not return its stock");
        }

        return failures;
    }
}
