using Microsoft.EntityFrameworkCore;
using SofaScore.Shared.Data;
using SofaScore.Shared.Services;
using SofaScoreScraper;

namespace SofaScore.Worker.Services;

public class MatchEnrichmentWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MatchEnrichmentWorker> _logger;
    // Configurações de tempo
    private readonly TimeSpan _activeDelay = TimeSpan.FromMinutes(2);   // Ciclo rápido (jogos ao vivo)
    private readonly TimeSpan _idleDelay = TimeSpan.FromMinutes(10);    // Ciclo de hibernação
    private TimeSpan _currentDelay;

    public MatchEnrichmentWorker(IServiceProvider serviceProvider, ILogger<MatchEnrichmentWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _currentDelay = _activeDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Smart Worker v6.0: Live Sync + Post-Game Enrichment");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (var scope = _serviceProvider.CreateAsyncScope())
                {
                    var scraper = scope.ServiceProvider.GetRequiredService<SofaScraper>();
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // --- FASE 0: LIMPEZA DE ZUMBIS ---
                    await RemoveZombieMatchesAsync(dbContext, stoppingToken);

                    // --- FASE 1: LIVE SYNC (score + status apenas) ---
                    _logger.LogInformation("📡 FASE 1: Verificando jogos ao vivo...");
                    await scraper.InitializeAsync();

                    var liveMatches = await scraper.GetLiveMatchesAsync();
                    bool hasLiveGames = liveMatches.Any();

                    if (hasLiveGames)
                    {
                        await SyncLiveMatchesAsync(scraper, dbContext, liveMatches, stoppingToken);
                    }

                    // --- FASE 2: ENRIQUECIMENTO PÓS-JOGO ---
                    // Busca jogos que já terminaram mas ainda não foram enriquecidos
                    bool enrichedSomething = await EnrichFinishedMatchesAsync(scraper, dbContext, stoppingToken);

                    // --- DECISÃO DE SONO ---
                    // Hiberna apenas se não há jogos ao vivo E não há jogos pendentes de enriquecimento
                    if (!hasLiveGames && !enrichedSomething)
                    {
                        _logger.LogInformation("💤 Sem jogos ao vivo ou pendentes. Entrando em hibernação ({Minutes} min).", _idleDelay.TotalMinutes);
                        _currentDelay = _idleDelay;
                    }
                    else
                    {
                        _currentDelay = _activeDelay;
                    }

                    // --- FASE 3: LIMBO (só roda se estamos ativos) ---
                    if (hasLiveGames || enrichedSomething)
                    {
                        await ProcessLimboMatches(scraper, dbContext, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro no ciclo do Worker. Tentando recuperar...");
                _currentDelay = _activeDelay;
            }

            await Task.Delay(_currentDelay, stoppingToken);
        }
    }

    // =================================================================================================
    // FASE 1: Sincroniza apenas score e status de jogos ao vivo
    // =================================================================================================
    private async Task SyncLiveMatchesAsync(
        SofaScraper scraper,
        AppDbContext dbContext,
        List<Match> liveMatches,
        CancellationToken ct)
    {
        var liveIds = liveMatches.Select(m => m.Id).ToList();

        var matchesInDb = await dbContext.Matches
            .Where(m => liveIds.Contains(m.Id))
            .ToListAsync(ct);

        foreach (var dbMatch in matchesInDb)
        {
            var liveData = liveMatches.First(l => l.Id == dbMatch.Id);

            if (dbMatch.Status != liveData.Status ||
                dbMatch.HomeScore != (liveData.HomeScore ?? 0) ||
                dbMatch.AwayScore != (liveData.AwayScore ?? 0))
            {
                dbMatch.HomeScore = liveData.HomeScore ?? 0;
                dbMatch.AwayScore = liveData.AwayScore ?? 0;
                dbMatch.StartTimestamp = liveData.StartTimestamp;

                // ✅ CRÍTICO: Se o jogo terminou, transiciona para Pending
                // Isso é o "sinal" que a FASE 2 usará para enriquecê-lo
                if (liveData.Status is "Ended" or "Finished")
                {
                    dbMatch.Status = liveData.Status;
                    dbMatch.ProcessingStatus = MatchProcessingStatus.Pending;
                    _logger.LogInformation("🏁 Jogo finalizado: {Home} vs {Away} ({HomeScore}-{AwayScore}). Marcado como Pending para enriquecimento.",
                        dbMatch.HomeTeam, dbMatch.AwayTeam, dbMatch.HomeScore, dbMatch.AwayScore);
                }
                else
                {
                    dbMatch.Status = liveData.Status ?? "Unknown";
                    dbMatch.ProcessingStatus = MatchProcessingStatus.InProgress;
                }
            }
        }

        await dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("📊 FASE 1: {Count} jogos ao vivo sincronizados.", matchesInDb.Count);
    }

    // =================================================================================================
    // FASE 2: Enriquece apenas jogos que já terminaram e ainda não foram enriquecidos
    // =================================================================================================
    private async Task<bool> EnrichFinishedMatchesAsync(
        SofaScraper scraper,
        AppDbContext dbContext,
        CancellationToken ct)
    {
        // Busca jogos finaliza dos que ainda precisam de enriquecimento
        // Condições:
        //   - Status é "Ended" ou "Finished" (confirmado pelo SofaScore)
        //   - ProcessingStatus é Pending (não foi enriquecido ainda)
        //   - Tentativas < 3 (evita loop infinito em caso de falha)
        var pendingMatches = await dbContext.Matches
            .Where(m =>
                (m.Status == "Ended" || m.Status == "Finished") &&
                m.ProcessingStatus == MatchProcessingStatus.Pending &&
                m.EnrichmentAttempts < 3)
            .OrderBy(m => m.StartTimestamp) // Enriquece primeiro os mais antigos
            .ToListAsync(ct);

        if (!pendingMatches.Any())
            return false;

        _logger.LogInformation("🔍 FASE 2: {Count} jogo(s) finalizado(s) pendente(s) de enriquecimento.", pendingMatches.Count);

        foreach (var match in pendingMatches)
        {
            _logger.LogInformation("🔍 FASE 2: Enriquecendo {Home} vs {Away}...", match.HomeTeam, match.AwayTeam);
            await ProcessMatchAsync(scraper, dbContext, match, ct);
        }

        return true;
    }

    // =================================================================================================
    // FASE 3: Limbo — jogos que ficaram presos em status indefinido
    // =================================================================================================
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
            _logger.LogInformation("🧟 FASE 3: Verificando {Count} jogo(s) atrasado(s)...", limboMatches.Count);
            foreach (var match in limboMatches)
            {
                await ProcessMatchAsync(scraper, dbContext, match, ct);
            }
        }
    }

    // =================================================================================================
    // FASE 0: Limpa jogos duplicados (Postponed/Cancelled) que já têm uma versão válida
    // =================================================================================================
    private async Task RemoveZombieMatchesAsync(AppDbContext db, CancellationToken ct)
    {
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
                _logger.LogInformation("🧹 Limpeza de Zumbis: {Count} partida(s) duplicada(s)/adiada(s) removida(s).", deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao limpar partidas duplicadas (zumbis)");
        }
    }

    // =================================================================================================
    // Enriquecimento completo de uma partida finalizada (stats + incidentes + detalhes)
    // =================================================================================================
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

                // Atualiza detalhes extras se disponíveis
                if (data.Details.Venue?.Name != null)
                    match.Stadium = data.Details.Venue.Name;
                if (data.Details.Referee?.Name != null)
                    match.Referee = data.Details.Referee.Name;
                if (data.Details.Attendance != null)
                    match.Attendance = data.Details.Attendance;

                // Lógica de Status final
                if (match.Status is "Ended" or "Finished")
                {
                    match.ProcessingStatus = MatchProcessingStatus.Enriched;
                }
                else if (match.Status == "Postponed")
                {
                    match.ProcessingStatus = MatchProcessingStatus.Postponed;
                }
                else if (match.Status is "Cancelled" or "Canceled")
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
            _logger.LogInformation("✅ Enriquecido: {Home} vs {Away} -> {Status} (ProcStatus: {PStatus})",
                match.HomeTeam, match.AwayTeam, match.Status, match.ProcessingStatus);
        }
        catch (Exception ex)
        {
            match.EnrichmentAttempts++;
            match.LastEnrichmentAttempt = DateTime.UtcNow;
            match.LastEnrichmentError = ex.Message;

            try { await dbContext.SaveChangesAsync(ct); } catch { }

            _logger.LogError("❌ Falha no enriquecimento de {Home} vs {Away} (tentativa {Attempt}/3): {Message}",
                match.HomeTeam, match.AwayTeam, match.EnrichmentAttempts, ex.Message);
        }

        await Task.Delay(2000, ct);
    }

    // =================================================================================================
    // Helper: Achata a estrutura de estatísticas em uma lista plana para o banco
    // =================================================================================================
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