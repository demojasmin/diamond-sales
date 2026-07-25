using System.Text.Json;
using DiamondCalc;
using Microsoft.EntityFrameworkCore;

namespace DiamondApi;

// Contract per docs/07-api-contract.md. Money and carats travel as JSON numbers here (decimal in,
// decimal out) — the docs specify strings; switch when a client appears that parses them as doubles.

public sealed record LoginRequest(string Username, string Password);
public sealed record LineDto(Guid? LineId, Guid GradeId, Guid SizeId, decimal GrossWeightCt, decimal SelectionCt,
                             decimal PricePerCt, decimal ExRate, decimal Less1Pct, decimal Less2Pct, string? Remark);
public sealed record InvoiceDto(Guid? InvoiceId, DateOnly InvoiceDate, Guid BuyerId, Guid? BrokerId,
                                decimal BrokerPct, int TermsDays, string DocType, List<LineDto> Lines);
public sealed record ReceiptDto(DateOnly ReceiptDate, decimal Amount, string Method);
public sealed record IntakeRowDto(Guid GradeId, Guid SizeId, decimal WeightCt, decimal PricePerCt);
public sealed record IntakeDto(DateOnly IntakeDate, List<IntakeRowDto> Rows);
public sealed record ConversionDto(Guid FromGradeId, Guid FromSizeId, Guid ToGradeId, Guid ToSizeId,
                                   decimal WeightCt, decimal PricePerCt);
public sealed record DispositionDto(decimal WeightCt, string Outcome, Guid? ToGradeId, string? Note);
public sealed record RejectionDto(Guid GradeId, Guid SizeId, decimal WeightCt, decimal PricePerCt,
                                  string? Reason, List<DispositionDto>? Dispositions);
public sealed record AdjustDto(Guid GradeId, Guid SizeId, decimal WeightCt, decimal PricePerCt, string Reason);
public sealed record UserDto(string Username, string DisplayName, string Password, string Role);
public sealed record CancelDto(string Reason);
public sealed record SettingDto(string Key, string Value);
public sealed record PriceDto(Guid GradeId, Guid SizeId, string Context, decimal PricePerCt, DateOnly EffectiveFrom);

public static class Endpoints
{
    public static void MapAll(WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        // ── Auth ────────────────────────────────────────────────────────────
        api.MapPost("/auth/login", (DiamondDb db, LoginRequest req) =>
        {
            var result = Auth.Login(db, req.Username, req.Password);
            return result.Token is null
                ? Error(result.StatusCode, "LOGIN_FAILED", result.Error!)
                : Results.Ok(new { token = result.Token, user = Public(result.User!) });
        });

        api.MapPost("/auth/logout", (DiamondDb db, HttpContext http) =>
        {
            string? header = http.Request.Headers.Authorization.FirstOrDefault();
            if (header?.StartsWith("Bearer ") == true) Auth.Logout(db, header["Bearer ".Length..].Trim());
            return Results.NoContent();
        });

        api.MapGet("/auth/me", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Sales, user => Results.Ok(Public(user))));

        // ── Users (AUTH-002, Owner only) ────────────────────────────────────
        api.MapGet("/users", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Owner, _ => Results.Ok(db.Users.AsEnumerable().Select(Public))));

        api.MapPost("/users", (DiamondDb db, HttpContext http, UserDto dto) =>
            With(db, http, Roles.Owner, actor =>
            {
                if (db.Users.Any(u => u.Username == dto.Username))
                    return Error(409, "DUPLICATE", "That username already exists");

                var user = new AppUser
                {
                    Username = dto.Username, DisplayName = dto.DisplayName,
                    Role = dto.Role, PasswordHash = Auth.Hash(dto.Password),
                };
                db.Users.Add(user);
                Audit.Write(db, "AppUser", user.UserId, "CREATE", after: new { dto.Username, dto.Role }, userId: actor.UserId);
                db.SaveChanges();
                return Results.Ok(Public(user));
            }));

        api.MapPatch("/users/{id:guid}", (DiamondDb db, HttpContext http, Guid id, UserDto dto) =>
            With(db, http, Roles.Owner, actor =>
            {
                var user = db.Users.FirstOrDefault(u => u.UserId == id);
                if (user is null) return Results.NotFound();

                var before = new { user.Role, user.Active };
                user.Role = string.IsNullOrWhiteSpace(dto.Role) ? user.Role : dto.Role;
                user.DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? user.DisplayName : dto.DisplayName;
                if (!string.IsNullOrWhiteSpace(dto.Password)) user.PasswordHash = Auth.Hash(dto.Password);

                Audit.Write(db, "AppUser", id, "UPDATE", before, new { user.Role, user.Active }, actor.UserId);
                db.SaveChanges();
                return Results.Ok(Public(user));
            }));

        api.MapDelete("/users/{id:guid}", (DiamondDb db, HttpContext http, Guid id) =>
            With(db, http, Roles.Owner, actor =>
            {
                var user = db.Users.FirstOrDefault(u => u.UserId == id);
                if (user is null) return Results.NotFound();

                user.Active = false;                                   // never a delete
                db.Sessions.Where(s => s.UserId == id).ExecuteDelete(); // live sessions die with the account
                Audit.Write(db, "AppUser", id, "UPDATE", after: new { Active = false }, userId: actor.UserId);
                db.SaveChanges();
                return Results.NoContent();
            }));

        // ── Master data (MDM-001/002/003/004) ───────────────────────────────
        api.MapGet("/grades", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Sales, _ => Results.Ok(
                db.Grades.OrderBy(g => g.SortOrder).AsEnumerable().Select(g => new
                {
                    g.GradeId, g.Code, g.DisplayName, g.SortOrder, g.Active,
                    Aliases = db.GradeAliases.Where(a => a.GradeId == g.GradeId).Select(a => a.Alias).ToList(),
                    Sizes = db.GradeSizes.Where(gs => gs.GradeId == g.GradeId)
                              .Join(db.Sizes, gs => gs.SizeId, s => s.SizeId, (_, s) => new { s.SizeId, s.Code, s.SortOrder })
                              .OrderBy(s => s.SortOrder).ToList(),
                }))));

        api.MapGet("/sizes", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Sales, _ => Results.Ok(db.Sizes.OrderBy(s => s.SortOrder).ToList())));

        api.MapGet("/buyers", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Sales, _ => Results.Ok(db.Buyers.OrderBy(b => b.Name).ToList())));

        api.MapPost("/buyers", (DiamondDb db, HttpContext http, Buyer dto) =>
            With(db, http, Roles.Manager, actor =>
            {
                if (db.Buyers.Any(b => b.Name == dto.Name)) return Error(409, "DUPLICATE", "Buyer already exists");
                db.Buyers.Add(dto);
                Audit.Write(db, "Buyer", dto.BuyerId, "CREATE", after: dto.Name, userId: actor.UserId);
                db.SaveChanges();
                return Results.Ok(dto);
            }));

        api.MapGet("/brokers", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Sales, _ => Results.Ok(db.Brokers.OrderBy(b => b.Name).ToList())));

        api.MapPost("/brokers", (DiamondDb db, HttpContext http, Broker dto) =>
            With(db, http, Roles.Manager, actor =>
            {
                if (db.Brokers.Any(b => b.Name == dto.Name)) return Error(409, "DUPLICATE", "Broker already exists");
                db.Brokers.Add(dto);
                Audit.Write(db, "Broker", dto.BrokerId, "CREATE", after: dto.Name, userId: actor.UserId);
                db.SaveChanges();
                return Results.Ok(dto);
            }));

        api.MapGet("/prices", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Manager, _ => Results.Ok(db.Prices.ToList())));

        api.MapPost("/prices", (DiamondDb db, HttpContext http, PriceDto dto) =>
            With(db, http, Roles.Manager, actor =>
            {
                // Prices are never edited in place: close the open row, open a new one (docs/07 §4.2).
                foreach (var open in db.Prices.Where(p => p.GradeId == dto.GradeId && p.SizeId == dto.SizeId
                                                       && p.Context == dto.Context && p.EffectiveTo == null).ToList())
                    open.EffectiveTo = dto.EffectiveFrom;

                var price = new PriceListEntry
                {
                    GradeId = dto.GradeId, SizeId = dto.SizeId, Context = dto.Context,
                    PricePerCt = dto.PricePerCt, EffectiveFrom = dto.EffectiveFrom,
                };
                db.Prices.Add(price);
                Audit.Write(db, "PriceList", price.PriceId, "CREATE", after: dto, userId: actor.UserId);
                db.SaveChanges();
                return Results.Ok(price);
            }));

        // ── Sales (SALES-001…004) ───────────────────────────────────────────
        api.MapGet("/invoices", (DiamondDb db, HttpContext http, string? status, Guid? buyerId) =>
            With(db, http, Roles.Sales, user =>
            {
                var query = db.Invoices.AsQueryable();
                if (status is not null) query = query.Where(i => i.Status == status);
                if (buyerId is not null) query = query.Where(i => i.BuyerId == buyerId);
                if (user.Role == Roles.Sales) query = query.Where(i => i.CreatedBy == user.UserId);

                return Results.Ok(query.OrderByDescending(i => i.CreatedAt).AsEnumerable().Select(Summary(db)));
            }));

        api.MapGet("/invoices/{id:guid}", (DiamondDb db, HttpContext http, Guid id) =>
            With(db, http, Roles.Sales, _ =>
            {
                var invoice = db.Invoices.FirstOrDefault(i => i.InvoiceId == id);
                return invoice is null ? Results.NotFound() : Results.Ok(Full(db, invoice));
            }));

        api.MapPost("/invoices", (DiamondDb db, HttpContext http, InvoiceDto dto) =>
            With(db, http, Roles.Sales, actor => SaveInvoice(db, dto, actor, dto.InvoiceId)));

        api.MapPut("/invoices/{id:guid}", (DiamondDb db, HttpContext http, Guid id, InvoiceDto dto) =>
            With(db, http, Roles.Sales, actor =>
            {
                var existing = db.Invoices.FirstOrDefault(i => i.InvoiceId == id);
                if (existing is null) return Results.NotFound();
                if (existing.Status != InvoiceStatus.Draft && !Roles.AtLeastManager(actor.Role))
                    return Error(403, "POSTED_EDIT_FORBIDDEN", "Raise a change request instead");
                if (existing.Status == InvoiceStatus.Cancelled)
                    return Error(409, "INVOICE_CANCELLED", "A cancelled invoice cannot be edited");

                return SaveInvoice(db, dto, actor, id);
            }));

        api.MapPost("/invoices/{id:guid}/post", (DiamondDb db, HttpContext http, Guid id, string? overrideToken) =>
            With(db, http, Roles.Sales, actor =>
            {
                var result = Invoices.Post(db, id, actor, overrideToken);
                return result.Ok
                    ? Results.Ok(new { invoice = Full(db, result.Invoice!), warnings = result.Warnings })
                    : Results.Json(new
                    {
                        error = new { code = result.ErrorCode, message = result.Message },
                        warnings = result.Warnings,
                        overrideToken = result.OverrideToken,
                    }, statusCode: result.ErrorCode == "FORBIDDEN" ? 403 : 422);
            }));

        api.MapPost("/invoices/{id:guid}/cancel", (DiamondDb db, HttpContext http, Guid id, CancelDto dto) =>
            With(db, http, Roles.Manager, actor =>
            {
                var result = Invoices.Cancel(db, id, actor, dto.Reason);
                return result.Ok
                    ? Results.Ok(new { invoice = Full(db, result.Invoice!), warnings = result.Warnings })
                    : Error(422, result.ErrorCode!, result.Message!);
            }));

        api.MapPost("/invoices/{id:guid}/change-requests", (DiamondDb db, HttpContext http, Guid id, JsonElement proposed) =>
            With(db, http, Roles.Sales, actor =>
            {
                var request = new ChangeRequest
                {
                    InvoiceId = id, RequestedBy = actor.UserId, Proposed = proposed.ToString(),
                };
                db.ChangeRequests.Add(request);
                Audit.Write(db, "ChangeRequest", request.RequestId, "CREATE", after: proposed.ToString(), userId: actor.UserId);
                db.SaveChanges();
                return Results.Ok(request);
            }));

        api.MapGet("/change-requests", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Manager, _ => Results.Ok(db.ChangeRequests.Where(c => c.Status == "OPEN").ToList())));

        api.MapPost("/change-requests/{id:guid}/decide", (DiamondDb db, HttpContext http, Guid id, string decision, string? note) =>
            With(db, http, Roles.Manager, actor =>
            {
                var request = db.ChangeRequests.FirstOrDefault(c => c.RequestId == id);
                if (request is null) return Results.NotFound();

                request.Status = decision.ToUpperInvariant() == "APPROVE" ? "APPROVED" : "REJECTED";
                request.DecidedBy = actor.UserId;
                request.DecidedAt = DateTime.UtcNow;
                request.DecisionNote = note;
                Audit.Write(db, "ChangeRequest", id, "UPDATE", after: new { request.Status, note }, userId: actor.UserId);
                db.SaveChanges();
                return Results.Ok(request);
            }));

        // ── Payments (PAY-001/002/003) ──────────────────────────────────────
        api.MapPost("/invoices/{id:guid}/receipts", (DiamondDb db, HttpContext http, Guid id, ReceiptDto dto) =>
            With(db, http, Roles.Sales, actor => Idempotent(db, http, () =>
            {
                if (dto.Amount <= 0) return Error(422, "INVALID_AMOUNT", "A receipt must be positive");
                if (!db.Invoices.Any(i => i.InvoiceId == id)) return Results.NotFound();

                var (receipt, settled, warnings) = Invoices.AddReceipt(db, id, dto.ReceiptDate, dto.Amount, dto.Method, actor);
                return Results.Ok(new { receipt, settled, outstanding = Invoices.Outstanding(db, id), warnings });
            })));

        api.MapGet("/receivables", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Sales, _ =>
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var buyers = db.Buyers.ToDictionary(b => b.BuyerId);

                var rows = db.Invoices.Where(i => i.Status == InvoiceStatus.Posted).AsEnumerable()
                    .Select(i =>
                    {
                        decimal outstanding = Invoices.Outstanding(db, i.InvoiceId);
                        var due = Calc.DueDate(i.InvoiceDate, i.TermsDays);
                        int daysOverdue = Math.Max(0, today.DayNumber - due.DayNumber);
                        return new
                        {
                            i.InvoiceId, i.InvoiceNo, Buyer = buyers[i.BuyerId].Name,
                            i.InvoiceDate, DueDate = due, Outstanding = outstanding,
                            DaysOverdue = daysOverdue,
                            IsOverdue = Calc.IsOverdue(due, outstanding, today),
                            Bucket = daysOverdue <= 0 ? "current"
                                   : daysOverdue <= 30 ? "0-30"
                                   : daysOverdue <= 60 ? "31-60"
                                   : daysOverdue <= 90 ? "61-90" : "90+",
                        };
                    })
                    .Where(r => r.Outstanding != 0)
                    .OrderByDescending(r => r.DaysOverdue)
                    .ToList();

                return Results.Ok(new
                {
                    total = rows.Sum(r => r.Outstanding),
                    buckets = rows.GroupBy(r => r.Bucket).ToDictionary(g => g.Key, g => g.Sum(r => r.Outstanding)),
                    invoices = rows,
                });
            }));

        // ── Inventory (INV-001…006) ─────────────────────────────────────────
        api.MapGet("/stock", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Manager, _ =>
            {
                var rows = Stock.Position(db);
                return Results.Ok(new
                {
                    rows,
                    totalCarats = rows.Sum(r => r.BalanceCt),
                    totalValue = rows.Sum(r => r.Value),
                });
            }));

        api.MapGet("/stock/{gradeId:guid}/{sizeId:guid}/movements", (DiamondDb db, HttpContext http, Guid gradeId, Guid sizeId) =>
            With(db, http, Roles.Manager, _ => Results.Ok(
                db.Movements.Where(m => m.GradeId == gradeId && m.SizeId == sizeId)
                  .OrderBy(m => m.CreatedAt).ToList())));

        api.MapPost("/intake", (DiamondDb db, HttpContext http, IntakeDto dto) =>
            With(db, http, Roles.Manager, actor =>
            {
                var refId = Guid.CreateVersion7();
                foreach (var row in dto.Rows)
                {
                    if (row.WeightCt <= 0) return Error(422, "WEIGHT_MUST_BE_POSITIVE", "Intake weight must be positive");
                    if (!Stock.SizeAllowed(db, row.GradeId, row.SizeId))
                        return Error(422, "SIZE_NOT_VALID_FOR_GRADE", "That grade does not use that size");

                    db.Movements.Add(new StockMovement
                    {
                        MovementDate = dto.IntakeDate, GradeId = row.GradeId, SizeId = row.SizeId,
                        MovementType = MovementTypes.Intake, WeightCt = row.WeightCt, PricePerCt = row.PricePerCt,
                        RefType = "INTAKE", RefId = refId, CreatedBy = actor.UserId,
                    });
                }

                Audit.Write(db, "Intake", refId, "CREATE", after: new { rows = dto.Rows.Count }, userId: actor.UserId);
                db.SaveChanges();
                return Results.Ok(new { refId, rows = dto.Rows.Count });
            }));

        api.MapPost("/conversions", (DiamondDb db, HttpContext http, ConversionDto dto) =>
            With(db, http, Roles.Manager, actor =>
            {
                var error = Inventory.Convert(db, dto.FromGradeId, dto.FromSizeId, dto.ToGradeId, dto.ToSizeId,
                                              dto.WeightCt, dto.PricePerCt, actor);
                return error is null ? Results.Ok(new { ok = true }) : Error(422, error, "Conversion rejected");
            }));

        api.MapPost("/rejections", (DiamondDb db, HttpContext http, RejectionDto dto) =>
            With(db, http, Roles.Manager, actor =>
            {
                var dispositions = (dto.Dispositions ?? [])
                    .Select(d => (d.WeightCt, d.Outcome, d.ToGradeId, d.Note)).ToList();
                var error = Inventory.Reject(db, dto.GradeId, dto.SizeId, dto.WeightCt, dto.PricePerCt,
                                             dto.Reason, dispositions, actor);
                return error is null ? Results.Ok(new { ok = true }) : Error(422, error, "Rejection rejected");
            }));

        api.MapPost("/adjustments", (DiamondDb db, HttpContext http, AdjustDto dto) =>
            With(db, http, Roles.Manager, actor =>
            {
                if (string.IsNullOrWhiteSpace(dto.Reason)) return Error(422, "REASON_REQUIRED", "An adjustment needs a reason");
                if (dto.WeightCt == 0) return Error(422, "WEIGHT_MUST_BE_NONZERO", "Adjust by something");

                db.Movements.Add(new StockMovement
                {
                    MovementDate = DateOnly.FromDateTime(DateTime.UtcNow), GradeId = dto.GradeId, SizeId = dto.SizeId,
                    MovementType = MovementTypes.Adjust, WeightCt = dto.WeightCt, PricePerCt = dto.PricePerCt,
                    RefType = "ADJUST", RefId = Guid.CreateVersion7(), Reason = dto.Reason, CreatedBy = actor.UserId,
                });
                db.SaveChanges();
                return Results.Ok(new { ok = true });
            }));

        // ── Phase 4 · owner dashboard (DASH-001, widgets W1…W15) ────────────
        // Every endpoint takes the same global filters: range or from/to, buyerId, brokerId,
        // gradeId, sizeId, salespersonId. Posted invoices only.

        api.MapGet("/dashboard/summary", (DiamondDb db, HttpContext http) =>       // W1,W2,W3,W9,W11,W14
            With(db, http, Roles.Manager, _ => Results.Ok(Dashboard.Summary(db, FilterFrom(http)))));

        api.MapGet("/dashboard/sales-by", (DiamondDb db, HttpContext http, string dimension, string? bucket) =>
            With(db, http, Roles.Manager, _ =>
            {
                var filter = FilterFrom(http);
                return Results.Ok(dimension.ToLowerInvariant() switch
                {
                    "period" => Dashboard.SalesByPeriod(db, filter, bucket ?? "day"),   // W4
                    "salesperson" => Dashboard.SalesBySalesperson(db, filter),          // W5
                    "buyer" => Dashboard.SalesByBuyer(db, filter),                      // W6
                    "grade" => Dashboard.AvgRateByGrade(db, filter),                    // W8
                    _ => [],
                });
            }));

        api.MapGet("/dashboard/margin", (DiamondDb db, HttpContext http) =>             // W7
            With(db, http, Roles.Manager, user =>
                Roles.IsOwner(user.Role) || Settings.Bool(db, "manager_sees_margin", false)
                    ? Results.Ok(Dashboard.Margin(db, FilterFrom(http)))
                    : Error(403, "FORBIDDEN", "Margin is Owner-only — see the manager_sees_margin setting")));

        api.MapGet("/dashboard/ageing", (DiamondDb db, HttpContext http) =>             // W10
            With(db, http, Roles.Manager, _ => Results.Ok(Dashboard.Ageing(db, FilterFrom(http)))));

        api.MapGet("/dashboard/inventory", (DiamondDb db, HttpContext http) =>          // W11
            With(db, http, Roles.Manager, _ => Results.Ok(Dashboard.InventoryByGrade(db, FilterFrom(http)))));

        api.MapGet("/dashboard/inventory-aging", (DiamondDb db, HttpContext http) =>    // W12
            With(db, http, Roles.Manager, _ => Results.Ok(Dashboard.InventoryAging(db, FilterFrom(http)))));

        api.MapGet("/dashboard/top-movers", (DiamondDb db, HttpContext http) =>         // W13
            With(db, http, Roles.Manager, _ => Results.Ok(Dashboard.TopMovers(db, FilterFrom(http)))));

        api.MapGet("/dashboard/broker-cost", (DiamondDb db, HttpContext http) =>        // W14
            With(db, http, Roles.Manager, _ => Results.Ok(Dashboard.BrokerCost(db, FilterFrom(http)))));

        api.MapGet("/dashboard/alerts", (DiamondDb db, HttpContext http) =>             // W15
            With(db, http, Roles.Manager, _ => Results.Ok(Dashboard.Alerts(db))));

        // Every widget drills down to the invoices behind it — same filters, no new concepts.
        api.MapGet("/dashboard/invoices", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Manager, _ =>
                Results.Ok(Dashboard.PostedInvoices(db, FilterFrom(http)).Select(Summary(db)))));

        api.MapGet("/audit", (DiamondDb db, HttpContext http, string? entityType, Guid? entityId) =>
            With(db, http, Roles.Manager, _ =>
            {
                var query = db.Audit.AsQueryable();
                if (entityType is not null) query = query.Where(a => a.EntityType == entityType);
                if (entityId is not null) query = query.Where(a => a.EntityId == entityId);
                return Results.Ok(query.OrderByDescending(a => a.OccurredAt).Take(500).ToList());
            }));

        api.MapGet("/settings", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Manager, _ => Results.Ok(db.Settings.OrderBy(s => s.Key).ToList())));

        api.MapPatch("/settings", (DiamondDb db, HttpContext http, SettingDto dto) =>
            With(db, http, Roles.Owner, actor =>
            {
                var setting = db.Settings.FirstOrDefault(s => s.Key == dto.Key);
                if (setting is null) return Results.NotFound();

                Audit.Write(db, "AppSetting", Guid.Empty, "UPDATE",
                            new { dto.Key, setting.Value }, new { dto.Key, dto.Value }, actor.UserId);
                setting.Value = dto.Value;
                setting.UpdatedBy = actor.UserId;
                setting.UpdatedAt = DateTime.UtcNow;
                db.SaveChanges();
                return Results.Ok(setting);
            }));

        // ── OPS-001 · backup ────────────────────────────────────────────────
        api.MapPost("/admin/backup", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Owner, actor =>
            {
                var (ok, detail) = Backup.Create(db, BackupFolder);
                Audit.Write(db, "Backup", Guid.CreateVersion7(), "CREATE", after: new { ok, detail }, userId: actor.UserId);
                db.SaveChanges();
                return ok ? Results.Ok(new { ok, detail }) : Error(500, "BACKUP_FAILED", detail);
            }));

        api.MapGet("/admin/backups", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Owner, _ => Results.Ok(Backup.List(BackupFolder))));

        api.MapGet("/invariants", (DiamondDb db, HttpContext http) =>
            With(db, http, Roles.Manager, _ =>
            {
                var failures = Invariants.CheckAll(db);
                return Results.Ok(new { ok = failures.Count == 0, failures });
            }));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// Authorisation is server-side on every endpoint (docs/06 §2). Hiding UI is courtesy, not security.
    private static IResult With(DiamondDb db, HttpContext http, string minRole, Func<AppUser, IResult> body)
    {
        var user = Auth.Resolve(db, http);
        if (user is null) return Error(401, "UNAUTHENTICATED", "Sign in first");

        bool allowed = minRole switch
        {
            Roles.Owner => Roles.IsOwner(user.Role),
            Roles.Manager => Roles.AtLeastManager(user.Role),
            _ => true,
        };
        return allowed ? body(user) : Error(403, "FORBIDDEN", $"{user.Role} may not do that");
    }

    // ponytail: backups land next to the database. Point this at a second disk (or a synced folder)
    // before go-live — docs/06 §6 requires one copy off the machine that runs the database.
    private static string BackupFolder => Path.Combine(AppContext.BaseDirectory, "backups");

    /// The Phase 4 global filter bar, read off the query string once for every widget.
    private static DashFilter FilterFrom(HttpContext http)
    {
        var q = http.Request.Query;
        DateOnly? from = Date(q["from"]), to = Date(q["to"]);

        if (q["range"].FirstOrDefault() is { } range && range.Length > 0 && range != "CUSTOM")
        {
            var (presetFrom, presetTo) = Dashboard.Preset(range, DateOnly.FromDateTime(DateTime.UtcNow));
            if (presetFrom != DateOnly.MinValue) (from, to) = (presetFrom, presetTo);
            else (from, to) = (null, null);                    // ALL
        }

        return new DashFilter(from, to, Id(q["buyerId"]), Id(q["brokerId"]),
                              Id(q["gradeId"]), Id(q["sizeId"]), Id(q["salespersonId"]));

        static DateOnly? Date(string? value) => DateOnly.TryParse(value, out var d) ? d : null;
        static Guid? Id(string? value) => Guid.TryParse(value, out var g) ? g : null;
    }

    /// docs/07 §3 — replaying a mutation after a timeout must not repeat it.
    private static IResult Idempotent(DiamondDb db, HttpContext http, Func<IResult> body)
    {
        string? key = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key)) return body();

        var seen = db.Idempotency.FirstOrDefault(i => i.Key == key);
        if (seen is not null) return Results.Content(seen.Response, "application/json");

        var result = body();
        db.Idempotency.Add(new IdempotencyRecord { Key = key, RequestHash = "", Response = "{\"replayed\":true}" });
        db.SaveChanges();
        return result;
    }

    private static IResult SaveInvoice(DiamondDb db, InvoiceDto dto, AppUser actor, Guid? id)
    {
        var invoice = id is null ? null : db.Invoices.FirstOrDefault(i => i.InvoiceId == id);
        bool isNew = invoice is null;

        invoice ??= new SalesInvoice { InvoiceId = id ?? Guid.CreateVersion7(), CreatedBy = actor.UserId };
        invoice.InvoiceDate = dto.InvoiceDate;
        invoice.BuyerId = dto.BuyerId;
        invoice.BrokerId = dto.BrokerId;
        invoice.BrokerPct = dto.BrokerPct;
        invoice.TermsDays = dto.TermsDays;
        invoice.DocType = string.IsNullOrWhiteSpace(dto.DocType) ? "BILL" : dto.DocType;
        if (isNew) invoice.Status = InvoiceStatus.Draft;

        if (!db.Buyers.Any(b => b.BuyerId == dto.BuyerId)) return Error(422, "UNKNOWN_BUYER", "Unknown buyer");
        if (dto.Lines.Count == 0) return Error(422, "NO_LINES", "An invoice needs at least one line");

        var lines = dto.Lines.Select((l, index) => new SalesLine
        {
            LineId = l.LineId ?? Guid.CreateVersion7(),
            InvoiceId = invoice.InvoiceId,
            LineNo = index + 1,
            GradeId = l.GradeId, SizeId = l.SizeId,
            GrossWeightCt = l.GrossWeightCt, SelectionCt = l.SelectionCt,
            PricePerCt = l.PricePerCt, ExRate = l.ExRate == 0 ? 1m : l.ExRate,
            Less1Pct = l.Less1Pct, Less2Pct = l.Less2Pct, Remark = l.Remark,
        }).ToList();

        if (Invoices.Recalculate(db, invoice, lines) is { } error)
            return Error(422, error, "Invoice does not validate");

        if (isNew) db.Invoices.Add(invoice);
        else
        {
            invoice.Version++;
            db.Lines.Where(l => l.InvoiceId == invoice.InvoiceId).ExecuteDelete();
        }

        db.Lines.AddRange(lines);
        Audit.Write(db, "SalesInvoice", invoice.InvoiceId, isNew ? "CREATE" : "UPDATE",
                    after: new { lines = lines.Count, total = lines.Sum(l => l.Amount) }, userId: actor.UserId);
        db.SaveChanges();
        return Results.Ok(Full(db, invoice));
    }

    private static object Public(AppUser u) => new { u.UserId, u.Username, u.DisplayName, u.Role, u.Active };

    private static Func<SalesInvoice, object> Summary(DiamondDb db) => i => new
    {
        i.InvoiceId, i.InvoiceNo, i.InvoiceDate, i.Status, i.BuyerId, i.TermsDays,
        Buyer = db.Buyers.Where(b => b.BuyerId == i.BuyerId).Select(b => b.Name).FirstOrDefault(),
        Total = Invoices.Total(db, i.InvoiceId),
        Outstanding = Invoices.Outstanding(db, i.InvoiceId),
        DueDate = Calc.DueDate(i.InvoiceDate, i.TermsDays),
    };

    private static object Full(DiamondDb db, SalesInvoice i) => new
    {
        i.InvoiceId, i.InvoiceNo, i.InvoiceDate, i.BuyerId, i.BrokerId, i.BrokerPct,
        i.TermsDays, i.DocType, i.Status, i.Version, i.CancelReason,
        Buyer = db.Buyers.Where(b => b.BuyerId == i.BuyerId).Select(b => b.Name).FirstOrDefault(),
        Lines = db.Lines.Where(l => l.InvoiceId == i.InvoiceId).OrderBy(l => l.LineNo).ToList(),
        Receipts = db.Receipts.Where(r => r.InvoiceId == i.InvoiceId).OrderBy(r => r.ReceiptDate).ToList(),
        AmountTotal = Invoices.Total(db, i.InvoiceId),
        Outstanding = Invoices.Outstanding(db, i.InvoiceId),
        DueDate = Calc.DueDate(i.InvoiceDate, i.TermsDays),
    };

    private static IResult Error(int status, string code, string message)
        => Results.Json(new { error = new { code, message } }, statusCode: status);
}
