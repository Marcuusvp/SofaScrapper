using Microsoft.EntityFrameworkCore;
using SofaScore.Shared.Data;
using SofaScore.Shared.Services;
using SofaScoreScraper;

namespace SofaScore.Worker.Services;

public class MatchEnrichmentWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MatchEnrichmentWorker> _logger;
    private readonly TimeSpan _cycleDelay = TimeSpan.FromMinutes(1);

    public MatchEnrichmentWorker(IServiceProvider serviceProvider, ILogger<MatchEnrichmentWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Smart Worker v4: Live Sync + Active Enrich + Limbo Recovery");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var scraper = scope.ServiceProvider.GetRequiredService<SofaScraper>();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // ==============================================================================
                    // FASE 1: SMART SYNC (Prioridade Máxima - Tempo Real)
                    // ==============================================================================
                    _logger.LogInformation("📡 FASE 1: Sincronizando jogos ao vivo...");

                    await scraper.InitializeAsync();
                    var liveMatches = await scraper.GetLiveMatchesAsync();

                    if (liveMatches.Any())
                    {
                        var liveIds = liveMatches.Select(m => m.Id).ToList();
                        var matchesInDb = await dbContext.Matches
                            .Where(m => liveIds.Contains(m.Id))
                            .ToListAsync(stoppingToken);

                        int updatesCount = 0;
                        foreach (var dbMatch in matchesInDb)
                        {
                            var liveData = liveMatches.First(l => l.Id == dbMatch.Id);

                            // Atualiza se houver qualquer discrepância
                            if (dbMatch.Status != liveData.Status ||
                                dbMatch.HomeScore != liveData.HomeScore ||
                                dbMatch.AwayScore != liveData.AwayScore ||
                                dbMatch.ProcessingStatus != MatchProcessingStatus.InProgress)
                            {
                                dbMatch.Status = liveData.Status ?? "Unknown";
                                dbMatch.HomeScore = liveData.HomeScore ?? 0;
                                dbMatch.AwayScore = liveData.AwayScore ?? 0;
                                dbMatch.ProcessingStatus = MatchProcessingStatus.InProgress;
                                dbMatch.StartTimestamp = liveData.StartTimestamp; // Auto-correção de horário
                                updatesCount++;
                            }
                        }

                        if (updatesCount > 0)
                        {
                            await dbContext.SaveChangesAsync(stoppingToken);
                            _logger.LogInformation("⚡ Smart Sync: {Count} partidas atualizadas.", updatesCount);
                        }
                    }

                    // ==============================================================================
                    // FASE 2: ENRIQUECIMENTO ATIVO (Jogos rolando agora)
                    // ==============================================================================
                    
                    var activeMatches = await dbContext.Matches
                        .Where(m => m.ProcessingStatus == MatchProcessingStatus.InProgress)
                        .OrderBy(m => m.LastEnrichmentAttempt)
                        .Take(2) // Reduzi para 2 para dar espaço para a Fase 3
                        .ToListAsync(stoppingToken);

                    if (activeMatches.Any())
                    {
                        _logger.LogInformation("🔍 FASE 2: Enriquecendo {Count} jogos ativos...", activeMatches.Count);
                        foreach (var match in activeMatches)
                        {
                            await ProcessMatchAsync(scraper, dbContext, match, stoppingToken);
                        }
                    }

                    // ==============================================================================
                    // FASE 3: RECUPERAÇÃO DE LIMBO (Jogos velhos que ficaram para trás)
                    // ==============================================================================
                    
                    // Critério: Jogos "Not Started" cujo horário de início já passou há mais de 3 horas
                    // Isso pega o jogo do Real Madrid que acabou e o worker não viu
                    long cutoffTimestamp = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds();

                    var limboMatches = await dbContext.Matches
                        .Where(m => (m.Status == "Not started" || m.Status == "Postponed") 
                                    && m.StartTimestamp < cutoffTimestamp
                                    && m.ProcessingStatus != MatchProcessingStatus.Enriched // Não queremos reprocessar os prontos
                                    && m.ProcessingStatus != MatchProcessingStatus.Cancelled)
                        .OrderBy(m => m.StartTimestamp) // Pega os mais antigos primeiro
                        .Take(1) // Um por vez é suficiente para limpar o backlog aos poucos
                        .ToListAsync(stoppingToken);

                    if (limboMatches.Any())
                    {
                        _logger.LogInformation("🧟 FASE 3: Recuperando {Count} jogos do limbo...", limboMatches.Count);
                        foreach (var match in limboMatches)
                        {
                            _logger.LogInformation("🚑 Recuperando jogo perdido: {Home} vs {Away} (Era para ser: {Date})", 
                                match.HomeTeam, match.AwayTeam, DateTimeOffset.FromUnixTimeSeconds(match.StartTimestamp));
                            
                            await ProcessMatchAsync(scraper, dbContext, match, stoppingToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro fatal no ciclo do Worker");
            }

            await Task.Delay(_cycleDelay, stoppingToken);
        }
    }

    // Método auxiliar para não repetir lógica entre Fase 2 e 3
    private async Task ProcessMatchAsync(SofaScraper scraper, AppDbContext dbContext, DbMatch match, CancellationToken ct)
    {
        _logger.LogInformation("🔄 Processando: {Home} vs {Away} [{Status}]", match.HomeTeam, match.AwayTeam, match.Status);

        try
        {
            var data = await scraper.EnrichSingleMatchAsync(match.Id);

            // Atualiza status final se o jogo acabou
            if (data.Details != null)
            {
                match.Status = data.Details.Status?.Description ?? match.Status;
                match.HomeScore = data.Details.HomeScore?.Display ?? match.HomeScore;
                match.AwayScore = data.Details.AwayScore?.Display ?? match.AwayScore;

                // Se acabou, marca como Enriched para sair da fila de processamento
                if (match.Status == "Ended" || match.Status == "Finished")
                {
                    match.ProcessingStatus = MatchProcessingStatus.Enriched;
                }
            }

            // Estatísticas
            if (data.Statistics?.Statistics != null)
            {
                var oldStats = await dbContext.MatchStats.Where(s => s.MatchId == match.Id).ToListAsync(ct);
                dbContext.MatchStats.RemoveRange(oldStats);
                var dbStats = FlattenStatistics(match.Id, data.Statistics);
                await dbContext.MatchStats.AddRangeAsync(dbStats, ct);
            }

            // Incidentes
            if (data.Incidents != null)
            {
                var oldIncidents = await dbContext.Incidents.Where(i => i.MatchId == match.Id).ToListAsync(ct);
                dbContext.Incidents.RemoveRange(oldIncidents);

                var dbIncidents = data.Incidents.Select(inc => new DbIncident
                {
                    MatchId = match.Id,
                    IncidentType = inc.IncidentType ?? "unknown",
                    IncidentClass = inc.IncidentClass,
                    Time = inc.Time,
                    AddedTime = inc.AddedTime,
                    IsHome = inc.IsHome,
                    PlayerName = inc.Player?.Name ?? inc.Player?.ShortName,
                    AssistName = inc.Assist1?.Name ?? inc.Assist1?.ShortName
                }).ToList();

                await dbContext.Incidents.AddRangeAsync(dbIncidents, ct);
            }

            match.LastEnrichmentAttempt = DateTime.UtcNow;
            match.EnrichmentAttempts++;

            await dbContext.SaveChangesAsync(ct);
            _logger.LogInformation("✅ Sucesso para {Home} vs {Away} -> Novo Status: {Status}", match.HomeTeam, match.AwayTeam, match.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ Falha no jogo {Id}: {Message}", match.Id, ex.Message);
        }

        await Task.Delay(2000, ct);
    }

    private List<DbMatchStat> FlattenStatistics(int matchId, StatisticsResponse stats)
    {
        var list = new List<DbMatchStat>();
        if (stats?.Statistics == null) return list;

        foreach (var period in stats.Statistics)
        {
            if (period.Groups == null) continue;
            foreach (var group in period.Groups)
            {
                if (group.StatisticsItems == null) continue;
                foreach (var item in group.StatisticsItems)
                {
                    list.Add(new DbMatchStat
                    {
                        MatchId = matchId,
                        Period = period.Period ?? "UNK",
                        Name = item.Name ?? "UNK",
                        HomeValue = item.Home ?? "0",
                        AwayValue = item.Away ?? "0",
                        CompareCode = item.CompareCode
                    });
                }
            }
        }
        return list;
    }
}