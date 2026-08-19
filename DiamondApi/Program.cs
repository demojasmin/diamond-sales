using DiamondApi;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<DiamondDb>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Db") ?? "Data Source=diamond.db"));

// ponytail: SQLite + EnsureCreated, not PostgreSQL + migrations. The schema is docs/03 either way;
// swap the provider and generate the first migration once a real server exists (D5).

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    Seed.Run(scope.ServiceProvider.GetRequiredService<DiamondDb>());

Endpoints.MapAll(app);

app.MapGet("/", () => Results.Ok(new
{
    service = "Diamond Sales & Inventory API",
    contract = "docs/07-api-contract.md",
    firstRun = "POST /api/v1/auth/login { \"username\": \"owner\", \"password\": \"owner\" }",
}));

app.Run();
