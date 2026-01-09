using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SofaScore.Api.Data;
using SofaScore.Api.Services;
using SofaScoreScraper;

var builder = WebApplication.CreateBuilder(args);

// 1. Configurar Serviços
builder.Services.AddControllers()
.AddJsonOptions(options =>
{
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 2. Configurar Banco de Dados (Supabase)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// 3. Configurar Scraper (Singleton para manter navegador aberto)
builder.Services.AddSingleton<SofaScraper>();

// 4. Configurar DataManager (Scoped para usar o DbContext por requisição)
builder.Services.AddScoped<DataManager>();
builder.Services.AddLogging();
builder.Services.AddHostedService<MatchEnrichmentWorker>();
var app = builder.Build();

// 5. Inicializações ao subir a API
// Criar escopo para acessar serviços Scoped/Singleton na inicialização
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    
    // A. Garantir banco criado
    var context = services.GetRequiredService<AppDbContext>();
    await context.Database.EnsureCreatedAsync();
    Console.WriteLine("✅ Banco de dados conectado/criado.");

    // B. Baixar Chromium e iniciar Scraper
    var scraper = services.GetRequiredService<SofaScraper>();
    await scraper.InitializeAsync();
    Console.WriteLine("✅ Scraper inicializado.");
}

// 6. Middleware Pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();