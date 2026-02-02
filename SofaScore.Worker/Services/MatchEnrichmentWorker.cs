using Microsoft.EntityFrameworkCore;
using SofaScore.Shared.Data;
using SofaScore.Shared.Services;
using SofaScoreScraper;

namespace SofaScore.Worker.Services;

public class MatchEnrichmentWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MatchEnrichmentWorker> _logger;
    private readonly TimeSpan _cycleDelay = TimeSpan.FromMinutes(3);
    // Configurações de tempo
    private readonly TimeSpan _activeDelay = TimeSpan.FromMinutes(2);   // Ciclo rápido
    private readonly TimeSpan _idleDelay = TimeSpan.FromMinutes(30);    // Ciclo de hibernação
    private TimeSpan _currentDelay;

    public MatchEnrichmentWorker(IServiceProvider serviceProvider, ILogger<MatchEnrichmentWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _currentDelay = _activeDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Smart Worker v5.2: Auto-Sleep & Resource Mgmt");

        while (!stoppingToken.IsCancellationRequested)
        {
            bool busyCycle = false; // Flag para determinar se houve trabalho real

            try
            {
                // ✅ O Scraper será descartado automaticamente ao fim deste bloco, fechando o Chrome
                await using (var scope = _serviceProvider.CreateAsyncScope())
                {
                    var scraper = scope.ServiceProvider.GetRequiredService<SofaScraper>();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // --- FASE 0: LIMPEZA ---
                    await RemoveZombieMatchesAsync(dbContext, stoppingToken);

                    // --- FASE 1: LIVE SYNC ---
                    // Só iniciamos o scraper se formos realmente usar
                    // Inicializamos explicitamente dentro do bloco try
                    
                    _logger.LogInformation("📡 FASE 1: Verificando jogos ao vivo...");
                    await scraper.InitializeAsync();
                    
                    var liveMatches = await scraper.GetLiveMatchesAsync();
                    
                    if (liveMatches.Any())
                    {
                        busyCycle = true;
                        // Sincroniza dados básicos (Score, Status)
                        var liveIds = liveMatches.Select(m => m.Id).ToList();
                        var matchesInDb = await dbContext.Matches
                            .Where(m => liveIds.Contains(m.Id))
                            .ToListAsync(stoppingToken);

                        foreach (var dbMatch in matchesInDb)
                        {
                            var liveData = liveMatches.First(l => l.Id == dbMatch.Id);
                            // Atualiza apenas se mudou algo para economizar DB writes
                            if (dbMatch.Status != liveData.Status || dbMatch.HomeScore != liveData.HomeScore || dbMatch.AwayScore != liveData.AwayScore)
                            {
                                dbMatch.Status = liveData.Status ?? "Unknown";
                                dbMatch.HomeScore = liveData.HomeScore ?? 0;
                                dbMatch.AwayScore = liveData.AwayScore ?? 0;
                                dbMatch.ProcessingStatus = MatchProcessingStatus.InProgress;
                                dbMatch.StartTimestamp = liveData.StartTimestamp;
                            }
                        }
                        await dbContext.SaveChangesAsync(stoppingToken);
                    }

                    // --- FASE 2: ENRIQUECIMENTO ATIVO ---
                    // Verifica quantos jogos ativos temos no banco
                    var activeMatchesCount = await dbContext.Matches
                        .CountAsync(m => m.ProcessingStatus == MatchProcessingStatus.InProgress, stoppingToken);

                    if (activeMatchesCount > 0)
                    {
                        busyCycle = true;
                        // Processa 1 por vez para não sobrecarregar
                        var matchToProcess = await dbContext.Matches
                            .Where(m => m.ProcessingStatus == MatchProcessingStatus.InProgress)
                            .OrderBy(m => m.LastEnrichmentAttempt) // Pega o que faz mais tempo que não atualiza
                            .FirstOrDefaultAsync(stoppingToken);

                        if (matchToProcess != null)
                        {
                            _logger.LogInformation("🔍 FASE 2: Enriquecendo {Home} vs {Away}...", matchToProcess.HomeTeam, matchToProcess.AwayTeam);
                            await ProcessMatchAsync(scraper, dbContext, matchToProcess, stoppingToken);
                        }
                    }

                    // --- DECISÃO DE SONO ---
                    // Se não tem jogos no SofaScore E não tem jogos InProgress no banco -> Hiberna
                    if (!liveMatches.Any() && activeMatchesCount == 0)
                    {
                        _logger.LogInformation("💤 Sem jogos ao vivo ou ativos. Entrando em hibernação ({Minutes} min).", _idleDelay.TotalMinutes);
                        _currentDelay = _idleDelay;
                    }
                    else
                    {
                        _currentDelay = _activeDelay;
                    }

                    // --- FASE 3: LIMBO (Opcional - só roda se estivermos ativos ou a cada X ciclos) ---
                    // Se estamos hibernando, não rodamos FASE 3 para não acordar o browser a toa
                    if (busyCycle)
                    {
                        await ProcessLimboMatches(scraper, dbContext, stoppingToken);
                    }
                } 
                // 🛑 AQUI o 'using' termina e chama scraper.DisposeAsync(), fechando o Chrome.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro no ciclo do Worker. Tentando recuperar...");
                _currentDelay = _activeDelay; // Em caso de erro, tenta de novo rápido
            }

            await Task.Delay(_currentDelay, stoppingToken);
        }
    }

    private async Task ProcessLimboMatches(SofaScraper scraper, AppDbContext dbContext, CancellationToken ct)
    {
        long cutoffTimestamp = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds();
        var limboMatches = await dbContext.Matches
            .Where(m => (m.Status == "Not started" || m.Status == "Postponed") 
                        && m.StartTimestamp < cutoffTimestamp
                        && m.ProcessingStatus != MatchProcessingStatus.Enriched 
                        && m.ProcessingStatus != MatchProcessingStatus.Cancelled
                        && m.ProcessingStatus != MatchProcessingStatus.Postponed)
            .OrderBy(m => m.StartTimestamp)
            .Take(5) 
            .ToListAsync(ct);

        if (limboMatches.Any())
        {
            _logger.LogInformation("🧟 FASE 3: Verificando {Count} jogos atrasados...", limboMatches.Count);
            foreach (var match in limboMatches)
            {
                await ProcessMatchAsync(scraper, dbContext, match, ct);
            }
        }
    }
    private async Task RemoveZombieMatchesAsync(AppDbContext db, CancellationToken ct)
    {
        // Esta query deleta jogos marcados como Postponed(3) ou Cancelled(4) SE
        // existir outro jogo (m2) com os mesmos times/rodada/torneio que esteja
        // Pending(0), InProgress(1) ou Enriched(2).
        // Isso elimina o "Levante vs Villarreal" velho em favor do novo.
        
        var sql = @"
            DELETE FROM ""Matches"" m1
            WHERE m1.""ProcessingStatus"" IN (3, 4)
            AND EXISTS (
                SELECT 1 FROM ""Matches"" m2
                WHERE m2.""TournamentId"" = m1.""TournamentId""
                AND m2.""SeasonId"" = m1.""SeasonId""
                AND m2.""Round"" = m1.""Round""
                AND m2.""HomeTeam"" = m1.""HomeTeam""
                AND m2.""AwayTeam"" = m1.""AwayTeam""
                AND m2.""Id"" != m1.""Id""
                AND m2.""ProcessingStatus"" IN (0, 1, 2)
            );
        ";

        try 
        {
            int deleted = await db.Database.ExecuteSqlRawAsync(sql, ct);
            if (deleted > 0)
            {
                _logger.LogInformation("🧹 Limpeza de Zumbis: {Count} partidas duplicadas/adiadas removidas.", deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao limpar partidas duplicadas (zumbis)");
        }
    }

    private async Task ProcessMatchAsync(SofaScraper scraper, AppDbContext dbContext, DbMatch match, CancellationToken ct)
    {
        try
        {
            var data = await scraper.EnrichSingleMatchAsync(match.Id);

            if (data.Details != null)
            {
                match.Status = data.Details.Status?.Description ?? match.Status;
                match.HomeScore = data.Details.HomeScore?.Display ?? match.HomeScore;
                match.AwayScore = data.Details.AwayScore?.Display ?? match.AwayScore;
                
                if (data.Details.StartTimestamp > 0)
                {
                    match.StartTimestamp = data.Details.StartTimestamp;
                }

                // Lógica de Status
                if (match.Status == "Ended" || match.Status == "Finished")
                {
                    match.ProcessingStatus = MatchProcessingStatus.Enriched;
                }
                else if (match.Status == "Postponed")
                {
                    match.ProcessingStatus = MatchProcessingStatus.Postponed;
                }
                else if (match.Status == "Cancelled" || match.Status == "Canceled")
                {
                    match.ProcessingStatus = MatchProcessingStatus.Cancelled;
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
            _logger.LogInformation("✅ Atualizado: {Home} vs {Away} -> {Status} (ProcStatus: {PStatus})", 
                match.HomeTeam, match.AwayTeam, match.Status, match.ProcessingStatus);
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