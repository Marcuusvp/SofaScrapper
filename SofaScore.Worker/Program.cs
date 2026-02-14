using SofaScore.Shared.Data;
using SofaScore.Shared.Services;
using Microsoft.EntityFrameworkCore;
using SofaScore.Worker.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

Console.WriteLine("✅ DbContext registrado");

builder.Services.Configure<WorkerSettings>(
    builder.Configuration.GetSection("WorkerSettings"));

Console.WriteLine("✅ WorkerSettings configurado");

builder.Services.AddScoped<SofaScraper>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SofaScraper>>();
    return new SofaScraper(logger);
});

Console.WriteLine("✅ SofaScraper registrado");

builder.Services.AddScoped<RoundScheduler>();

Console.WriteLine("✅ RoundScheduler registrado");

builder.Services.AddHostedService<MatchEnrichmentWorker>();

Console.WriteLine("✅ MatchEnrichmentWorker registrado");

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.MapGet("/ready", async (AppDbContext db) =>
{
    try
    {
        await db.Database.CanConnectAsync();
        return Results.Ok(new { status = "ready", timestamp = DateTime.UtcNow });
    }
    catch
    {
        return Results.StatusCode(503);
    }
});

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await context.Database.EnsureCreatedAsync();
    Console.WriteLine("✅ Banco de dados conectado/criado.");
}

var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
Console.WriteLine($"🚀 Worker iniciando na porta {port}...");
app.Run($"http://0.0.0.0:{port}");
