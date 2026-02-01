using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SofaScore.Shared.Data;
using SofaScore.Shared.Services;


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

builder.Services.AddScoped<SofaScraper>();

// 4. Configurar DataManager (Scoped para usar o DbContext por requisição)
builder.Services.AddScoped<DataManager>();
builder.Services.AddLogging();

builder.Services.Configure<PredictionSettings>(
    builder.Configuration.GetSection("PredictionSettings"));

builder.Services.AddScoped<MatchDataFetcher>();
builder.Services.AddScoped<StatsAnalyzer>();
builder.Services.AddScoped<PredictionEngine>();
builder.Services.AddScoped<MatchPredictionService>();
builder.Services.AddScoped<SofaScraper>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SofaScraper>>();
    return new SofaScraper(logger);
});
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