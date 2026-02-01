using SofaScore.Shared.Data;
using SofaScore.Shared.Services; // Ajuste conforme seus namespaces
using Microsoft.EntityFrameworkCore;
using SofaScore.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

// 1. Configurar Banco de Dados (Mesma string de conexão da API)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// 2. Registrar Serviços do Shared
builder.Services.AddScoped<SofaScraper>();
builder.Services.AddScoped<SofaScraper>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SofaScraper>>();
    return new SofaScraper(logger);
});

// 3. Registrar o Worker
builder.Services.AddHostedService<MatchEnrichmentWorker>();

var host = builder.Build();

// 4. Garantir criação do banco ao iniciar (opcional, mas útil no worker)
using (var scope = host.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Evite MigrateAsync se a API já faz isso, mas EnsureCreated é seguro
    await context.Database.EnsureCreatedAsync(); 
}

host.Run();