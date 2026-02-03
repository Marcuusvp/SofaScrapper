using SofaScore.Shared.Data;
using SofaScore.Shared.Services;
using Microsoft.EntityFrameworkCore;
using SofaScore.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

// 1. Configurar Banco de Dados
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

Console.WriteLine("✅ DbContext registrado");

// 2. Registrar SofaScraper
builder.Services.AddScoped<SofaScraper>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SofaScraper>>();
    return new SofaScraper(logger);
});

Console.WriteLine("✅ SofaScraper registrado");

// 3. Registrar RoundScheduler
builder.Services.AddScoped<RoundScheduler>();

Console.WriteLine("✅ RoundScheduler registrado");

// 4. Registrar o Worker
builder.Services.AddHostedService<MatchEnrichmentWorker>();

Console.WriteLine("✅ MatchEnrichmentWorker registrado");

var host = builder.Build();

// 5. Garantir criação do banco ao iniciar
using (var scope = host.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await context.Database.EnsureCreatedAsync();
    Console.WriteLine("✅ Banco de dados conectado/criado.");
}

Console.WriteLine("🚀 Worker iniciando...");
host.Run();
