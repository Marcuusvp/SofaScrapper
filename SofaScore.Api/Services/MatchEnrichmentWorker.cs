using Microsoft.EntityFrameworkCore;
using SofaScore.Api.Data;
using SofaScoreScraper;

namespace SofaScore.Api.Services;

public class MatchEnrichmentWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MatchEnrichmentWorker> _logger;

    // ✅ Configurações de timing
    private const int IDLE_CHECK_INTERVAL_SECONDS = 120; // 2 minutos quando não há partidas
    private const int ACTIVE_CHECK_INTERVAL_SECONDS = 180; // 3 minutos durante partidas
    private const int MATCH_LOOKAHEAD_MINUTES = 30; // Começar a processar 30min antes

    public MatchEnrichmentWorker(
        IServiceProvider serviceProvider,
        ILogger<MatchEnrichmentWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Smart Worker iniciado - Aguardando partidas ao vivo");

        // Aguarda 30s antes de começar
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // ✅ 1. Verifica se há partidas ao vivo ou próximas
                var liveMatches = await GetLiveOrUpcomingMatchesAsync(db, stoppingToken);

                if (!liveMatches.Any())
                {
                    _logger.LogInformation(
                        "😴 Modo IDLE - Nenhuma partida próxima. Verificando novamente em {Seconds}s",
                        IDLE_CHECK_INTERVAL_SECONDS
                    );
                    await Task.Delay(TimeSpan.FromSeconds(IDLE_CHECK_INTERVAL_SECONDS), stoppingToken);
                    continue;
                }

                // ✅ 2. Há partidas! Entrar em modo ATIVO
                _logger.LogInformation(
                    "⚽ Modo ATIVO - {Count} partidas detectadas. Iniciando enriquecimento...",
                    liveMatches.Count
                );

                await ProcessMatchesSequentiallyAsync(liveMatches, stoppingToken);

                // ✅ 3. Aguarda intervalo menor durante partidas ativas
                _logger.LogInformation(
                    "⏳ Aguardando {Seconds}s até próxima verificação (modo ativo)",
                    ACTIVE_CHECK_INTERVAL_SECONDS
                );
                await Task.Delay(TimeSpan.FromSeconds(ACTIVE_CHECK_INTERVAL_SECONDS), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro no ciclo do Smart Worker");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    /// <summary>
    /// ✅ Busca partidas ao vivo ou que começarão em breve
    /// </summary>
    private async Task<List<DbMatch>> GetLiveOrUpcomingMatchesAsync(
        AppDbContext db, 
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var lookAhead = DateTimeOffset.UtcNow.AddMinutes(MATCH_LOOKAHEAD_MINUTES).ToUnixTimeSeconds();
        var twoHoursAgo = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();

        return await db.Matches
            .Where(m =>
                // Partidas ao vivo (começaram há até 2 horas)
                (m.StartTimestamp <= now && m.StartTimestamp >= twoHoursAgo) ||
                // Partidas que começam nos próximos 30 minutos
                (m.StartTimestamp > now && m.StartTimestamp <= lookAhead)
            )
            .Where(m =>
                m.ProcessingStatus != MatchProcessingStatus.Enriched &&
                m.ProcessingStatus != MatchProcessingStatus.Cancelled &&
                m.EnrichmentAttempts < 10 // Limite de tentativas
            )
            .OrderBy(m => m.StartTimestamp)
            .ToListAsync(ct);
    }

    /// <summary>
    /// ✅ Processa partidas UMA POR VEZ
    /// </summary>
    private async Task ProcessMatchesSequentiallyAsync(
        List<DbMatch> matches,
        CancellationToken ct)
    {
        SofaScraper? scraper = null;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<SofaScraper>>();

            // ✅ Cria e inicializa scraper UMA VEZ
            scraper = new SofaScraper(logger);
            await scraper.InitializeAsync();

            _logger.LogInformation("✅ Chromium inicializado. Processando {Count} partidas...", matches.Count);

            foreach (var match in matches)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    _logger.LogInformation(
                        "🔄 [{Current}/{Total}] Enriquecendo: {Home} vs {Away} (ID: {MatchId})",
                        matches.IndexOf(match) + 1,
                        matches.Count,
                        match.HomeTeam,
                        match.AwayTeam,
                        match.Id
                    );

                    // ✅ Enriquece UMA partida por vez
                    var enrichmentData = await scraper.EnrichSingleMatchAsync(match.Id);

                    // Atualiza dados no banco
                    await SaveEnrichmentDataAsync(db, match, enrichmentData, ct);

                    _logger.LogInformation("✅ Match {Id} enriquecido com sucesso", match.Id);

                    // Pequeno delay entre partidas
                    await Task.Delay(1000, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Erro ao enriquecer match {Id}", match.Id);

                    match.EnrichmentAttempts++;
                    match.LastEnrichmentAttempt = DateTime.UtcNow;
                    match.LastEnrichmentError = ex.Message;
                    await db.SaveChangesAsync(ct);
                }
            }
        }
        finally
        {
            // ✅ SEMPRE fecha e libera memória
            if (scraper != null)
            {
                _logger.LogInformation("🧹 Fechando Chromium e liberando recursos...");
                await scraper.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// ✅ Salva dados enriquecidos no banco
    /// </summary>
    private async Task SaveEnrichmentDataAsync(
        AppDbContext db,
        DbMatch match,
        MatchEnrichmentData data,
        CancellationToken ct)
    {
        // Atualiza detalhes básicos
        if (data.Details != null)
        {
            match.Status = data.Details.Status?.Description ?? match.Status;
            match.HomeScore = data.Details.HomeScore?.Display ?? match.HomeScore;
            match.AwayScore = data.Details.AwayScore?.Display ?? match.AwayScore;
        }

        // Remove estatísticas antigas
        var oldStats = await db.MatchStats.Where(s => s.MatchId == match.Id).ToListAsync(ct);
        if (oldStats.Any()) db.MatchStats.RemoveRange(oldStats);

        // Adiciona novas estatísticas
        if (data.Statistics?.Statistics != null)
        {
            foreach (var period in data.Statistics.Statistics)
            {
                if (period.Groups == null) continue;
                foreach (var group in period.Groups)
                {
                    if (group.StatisticsItems == null) continue;
                    foreach (var item in group.StatisticsItems)
                    {
                        match.Stats.Add(new DbMatchStat
                        {
                            Period = period.Period ?? "UNK",
                            Name = item.Name ?? "UNK",
                            HomeValue = item.Home ?? "0",
                            AwayValue = item.Away ?? "0",
                            CompareCode = item.CompareCode
                        });
                    }
                }
            }
        }

        // Remove incidentes antigos
        var oldIncidents = await db.Incidents.Where(i => i.MatchId == match.Id).ToListAsync(ct);
        if (oldIncidents.Any()) db.Incidents.RemoveRange(oldIncidents);

        // Adiciona novos incidentes
        if (data.Incidents != null)
        {
            foreach (var inc in data.Incidents)
            {
                match.Incidents.Add(new DbIncident
                {
                    IncidentType = inc.IncidentType ?? "unknown",
                    IncidentClass = inc.IncidentClass,
                    Time = inc.Time,
                    AddedTime = inc.AddedTime,
                    IsHome = inc.IsHome,
                    PlayerName = inc.Player?.Name ?? inc.Player?.ShortName,
                    AssistName = inc.Assist1?.Name ?? inc.Assist1?.ShortName
                });
            }
        }

        // Marca como enriquecido
        match.ProcessingStatus = MatchProcessingStatus.Enriched;
        match.LastEnrichmentAttempt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }
}
