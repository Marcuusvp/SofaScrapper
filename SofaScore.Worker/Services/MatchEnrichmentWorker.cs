using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SofaScore.Shared.Data;
using SofaScore.Shared.Services;
using SofaScoreScraper;

namespace SofaScore.Worker.Services;

public class WorkerSettings
{
    public bool EnableDeepSleep { get; set; } = true;
    public int DeepSleepIntervalMinutes { get; set; } = 8;
    public int PreGameWakeupMinutes { get; set; } = 15;
}

public class MatchEnrichmentWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MatchEnrichmentWorker> _logger;
    private readonly WorkerSettings _settings;
    
    // Configurações de tempo
    private readonly TimeSpan _activeDelay = TimeSpan.FromMinutes(2);   // Ciclo rápido (jogos ao vivo)
    private readonly TimeSpan _idleDelay = TimeSpan.FromMinutes(10);    // Ciclo de hibernação
    private readonly TimeSpan _roundCheckInterval = TimeSpan.FromHours(6); // Checa próxima rodada a cada 6h
    
    private TimeSpan _currentDelay;
    private DateTime _lastRoundCheck = DateTime.MinValue;
    
    // Deep Sleep - variável que armazena o próximo jogo
    private DateTime? _nextGameStartTime = null;

    public MatchEnrichmentWorker(
        IServiceProvider serviceProvider, 
        ILogger<MatchEnrichmentWorker> logger,
        IOptions<WorkerSettings> settings)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _settings = settings.Value;
        _currentDelay = _activeDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Smart Worker v8.0: Live Sync + Enrichment + Standings + Round Scheduler + Deep Sleep");
        _logger.LogInformation("⚙️  Deep Sleep: {Status} | Interval: {Minutes}min | Pre-Game Wakeup: {Wakeup}min", 
            _settings.EnableDeepSleep ? "ENABLED" : "DISABLED",
            _settings.DeepSleepIntervalMinutes,
            _settings.PreGameWakeupMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using (var scope = _serviceProvider.CreateAsyncScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    // --- DEEP SLEEP CHECK ---
                    if (_settings.EnableDeepSleep && await ShouldEnterDeepSleepAsync(dbContext, stoppingToken))
                    {
                        var deepSleepDelay = TimeSpan.FromMinutes(_settings.DeepSleepIntervalMinutes);
                        
                        _logger.LogInformation("😴 Deep Sleep ativado. Próximo jogo inicia em: {NextGame}. Acordando em {Minutes}min...", 
                            _nextGameStartTime?.ToString("dd/MM/yyyy HH:mm:ss") ?? "N/A",
                            _settings.DeepSleepIntervalMinutes);
                        
                        await Task.Delay(deepSleepDelay, stoppingToken);
                        continue; // Volta ao início do loop sem executar scraping
                    }

                    // --- EXECUÇÃO NORMAL DO WORKER ---
                    var scraper = scope.ServiceProvider.GetRequiredService<SofaScraper>();
                    var roundScheduler = scope.ServiceProvider.GetRequiredService<RoundScheduler>();

                    // --- FASE 0: LIMPEZA DE ZUMBIS ---
                    await RemoveZombieMatchesAsync(dbContext, stoppingToken);

                    // --- FASE 0.5: BUSCAR PRÓXIMAS RODADAS (a cada 6h) ---
                    if ((DateTime.UtcNow - _lastRoundCheck) > _roundCheckInterval)
                    {
                        _logger.LogInformation("📅 FASE 0.5: Verificando próximas rodadas...");
                        await roundScheduler.CheckAndFetchNextRoundsAsync(stoppingToken);
                        _lastRoundCheck = DateTime.UtcNow;
                    }

                    // --- FASE 1: LIVE SYNC (score + status apenas) ---
                    _logger.LogInformation("📡 FASE 1: Verificando jogos ao vivo...");
                    await scraper.InitializeAsync();

                    var liveMatches = await scraper.GetLiveMatchesAsync();
                    bool hasLiveGames = liveMatches.Any();

                    if (hasLiveGames)
                    {
                        await SyncLiveMatchesAsync(dbContext, liveMatches, stoppingToken);
                    }
                    await ProcessFinishedLiveMatchesAsync(scraper, dbContext, liveMatches, stoppingToken);
                    
                    // --- FASE 2: ENRIQUECIMENTO PÓS-JOGO + STANDINGS ---
                    bool enrichedSomething = await EnrichFinishedMatchesAsync(scraper, dbContext, stoppingToken);

                    // --- DECISÃO DE SONO ---
                    if (!hasLiveGames && !enrichedSomething)
                    {
                        _logger.LogInformation("💤 Sem jogos ao vivo ou pendentes. Entrando em hibernação ({Minutes} min).", _idleDelay.TotalMinutes);
                        _currentDelay = _idleDelay;
                    }
                    else
                    {
                        _currentDelay = _activeDelay;
                    }

                    // --- FASE 3: LIMBO ---
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
    // DEEP SLEEP: Verifica se deve entrar em modo de economia extrema
    // =================================================================================================
    private async Task<bool> ShouldEnterDeepSleepAsync(AppDbContext dbContext, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nowTimestamp = new DateTimeOffset(now).ToUnixTimeSeconds();
        
        // Query mais barata possível: apenas ID e timestamp do próximo jogo
        var nextMatch = await dbContext.Matches
            .Where(m => m.StartTimestamp > nowTimestamp && 
                       m.ProcessingStatus != MatchProcessingStatus.Cancelled)
            .OrderBy(m => m.StartTimestamp)
            .Select(m => new { m.Id, m.StartTimestamp })
            .FirstOrDefaultAsync(ct);

        if (nextMatch == null)
        {
            _logger.LogDebug("🔍 Deep Sleep Check: Nenhum jogo futuro encontrado no banco.");
            _nextGameStartTime = null;
            return false; // Sem jogos futuros, executa normalmente
        }

        var nextGameTime = DateTimeOffset.FromUnixTimeSeconds(nextMatch.StartTimestamp).UtcDateTime;
        _nextGameStartTime = nextGameTime;

        var timeUntilGame = nextGameTime - now;
        var wakeupThreshold = TimeSpan.FromMinutes(_settings.PreGameWakeupMinutes);

        // Se o jogo está longe (mais de X minutos), pode hibernar profundamente
        if (timeUntilGame > wakeupThreshold)
        {
            return true; // Entra em deep sleep
        }

        _logger.LogInformation("⏰ Próximo jogo em {Minutes} minutos. Modo ativo.", timeUntilGame.TotalMinutes);
        return false; // Jogo próximo, executa normalmente
    }

    // =================================================================================================
    // FASE 1: Sincroniza apenas score e status de jogos ao vivo
    // =================================================================================================
    private async Task SyncLiveMatchesAsync(
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

                // ✅ Se o jogo terminou, transiciona para Pending → FASE 2 vai enriquecê-lo
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
    // FASE 2: Enriquece jogos finalizados + dispara sync de standings quando necessário
    // =================================================================================================
    private async Task<bool> EnrichFinishedMatchesAsync(
        SofaScraper scraper,
        AppDbContext dbContext,
        CancellationToken ct)
    {
        var pendingMatches = await dbContext.Matches
            .Where(m =>
                (m.Status == "Ended" || m.Status == "Finished") &&
                m.ProcessingStatus == MatchProcessingStatus.Pending &&
                m.EnrichmentAttempts < 3)
            .OrderBy(m => m.StartTimestamp)
            .ToListAsync(ct);

        if (!pendingMatches.Any())
            return false;

        _logger.LogInformation("🔍 FASE 2: {Count} jogo(s) finalizado(s) pendente(s) de enriquecimento.", pendingMatches.Count);

        // Coleta os TournamentIds que tiveram jogos enriquecidos com sucesso
        var tournamentIdsToSyncStandings = new HashSet<int>();

        foreach (var match in pendingMatches)
        {
            _logger.LogInformation("🔍 FASE 2: Enriquecendo {Home} vs {Away}...", match.HomeTeam, match.AwayTeam);
            bool success = await ProcessMatchAsync(scraper, dbContext, match, ct);

            if (success)
            {
                tournamentIdsToSyncStandings.Add(match.TournamentId);
            }
        }

        // ✅ Após enriquecimento, sincroniza standings apenas dos campeonatos afetados
        foreach (var tournamentId in tournamentIdsToSyncStandings)
        {
            await SyncStandingsAsync(scraper, dbContext, tournamentId, ct);
        }

        return true;
    }

    // =================================================================================================
    // STANDINGS SYNC: Atualiza classificação de um campeonato no banco
    // =================================================================================================
    private async Task SyncStandingsAsync(
        SofaScraper scraper,
        AppDbContext dbContext,
        int tournamentId,
        CancellationToken ct)
    {
        var seasonId = TournamentsInfo.GetSeasonIdByTournament(tournamentId);
        if (seasonId == null)
        {
            _logger.LogWarning("⚠️ Standings Sync: TournamentId {Id} não está configurado no TournamentsInfo. Ignorando.", tournamentId);
            return;
        }

        _logger.LogInformation("📋 Standings Sync: Atualizando classificação do campeonato {TournamentId}...", tournamentId);

        try
        {
            var standingsData = await scraper.GetStandingsAsync(tournamentId, seasonId.Value);

            if (standingsData?.Rows == null || !standingsData.Rows.Any())
            {
                _logger.LogWarning("⚠️ Standings Sync: Nenhuma linha retornada para campeonato {TournamentId}.", tournamentId);
                return;
            }

            var existingStandings = await dbContext.Standings
                .Include(s => s.Promotions)
                .Where(s => s.TournamentId == tournamentId && s.SeasonId == seasonId.Value)
                .ToDictionaryAsync(s => s.TeamId, ct);

            var now = DateTime.UtcNow;

            foreach (var row in standingsData.Rows)
            {
                if (row.Team == null) continue;

                int teamId = row.Team.Id;
                string teamName = row.Team.Name ?? "Unknown";

                if (existingStandings.TryGetValue(teamId, out var dbStanding))
                {
                    dbStanding.TeamName = teamName;
                    dbStanding.Position = row.Position;
                    dbStanding.Matches = row.Matches;
                    dbStanding.Wins = row.Wins;
                    dbStanding.Draws = row.Draws;
                    dbStanding.Losses = row.Losses;
                    dbStanding.GoalsFor = row.ScoresFor;
                    dbStanding.GoalsAgainst = row.ScoresAgainst;
                    dbStanding.GoalDifference = row.ScoresFor - row.ScoresAgainst;
                    dbStanding.Points = row.Points;
                    dbStanding.UpdatedAt = now;

                    dbContext.StandingPromotions.RemoveRange(dbStanding.Promotions);
                    if (row.Promotion != null)
                    {
                        dbStanding.Promotions.Add(new DbStandingPromotion
                        {
                            PromotionId = row.Promotion.Id,
                            Text = row.Promotion.Text ?? ""
                        });
                    }
                }
                else
                {
                    var newStanding = new DbStanding
                    {
                        TournamentId = tournamentId,
                        SeasonId = seasonId.Value,
                        TeamId = teamId,
                        TeamName = teamName,
                        Position = row.Position,
                        Matches = row.Matches,
                        Wins = row.Wins,
                        Draws = row.Draws,
                        Losses = row.Losses,
                        GoalsFor = row.ScoresFor,
                        GoalsAgainst = row.ScoresAgainst,
                        GoalDifference = row.ScoresFor - row.ScoresAgainst,
                        Points = row.Points,
                        UpdatedAt = now
                    };

                    if (row.Promotion != null)
                    {
                        newStanding.Promotions.Add(new DbStandingPromotion
                        {
                            PromotionId = row.Promotion.Id,
                            Text = row.Promotion.Text ?? ""
                        });
                    }

                    dbContext.Standings.Add(newStanding);
                }
            }

            await dbContext.SaveChangesAsync(ct);
            _logger.LogInformation("✅ Standings Sync: Classificação do campeonato {TournamentId} atualizada ({Count} times).",
                tournamentId, standingsData.Rows.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Standings Sync: Falha ao atualizar classificação do campeonato {TournamentId}.", tournamentId);
        }
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
    // Enriquecimento completo de uma partida finalizada
    // =================================================================================================
    private async Task<bool> ProcessMatchAsync(SofaScraper scraper, AppDbContext dbContext, DbMatch match, CancellationToken ct)
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
                    match.StartTimestamp = data.Details.StartTimestamp;

                if (data.Details.Venue?.Name != null)
                    match.Stadium = data.Details.Venue.Name;
                if (data.Details.Referee?.Name != null)
                    match.Referee = data.Details.Referee.Name;
                if (data.Details.Attendance != null)
                    match.Attendance = data.Details.Attendance;

                if (match.Status is "Ended" or "Finished")
                    match.ProcessingStatus = MatchProcessingStatus.Enriched;
                else if (match.Status == "Postponed")
                    match.ProcessingStatus = MatchProcessingStatus.Postponed;
                else if (match.Status is "Cancelled" or "Canceled")
                    match.ProcessingStatus = MatchProcessingStatus.Cancelled;
            }

            if (data.Statistics?.Statistics != null)
            {
                var oldStats = await dbContext.MatchStats.Where(s => s.MatchId == match.Id).ToListAsync(ct);
                dbContext.MatchStats.RemoveRange(oldStats);
                await dbContext.MatchStats.AddRangeAsync(FlattenStatistics(match.Id, data.Statistics), ct);
            }

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

            return match.ProcessingStatus == MatchProcessingStatus.Enriched;
        }
        catch (Exception ex)
        {
            match.EnrichmentAttempts++;
            match.LastEnrichmentAttempt = DateTime.UtcNow;
            match.LastEnrichmentError = ex.Message;

            try { await dbContext.SaveChangesAsync(ct); } catch { }

            _logger.LogError("❌ Falha no enriquecimento de {Home} vs {Away} (tentativa {Attempt}/3): {Message}",
                match.HomeTeam, match.AwayTeam, match.EnrichmentAttempts, ex.Message);

            return false;
        }
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

    // =================================================================================================
    // FASE 1.5: Detecta jogos que estavam ao vivo no banco, mas sumiram da lista do scraper (acabaram)
    // =================================================================================================
    private async Task ProcessFinishedLiveMatchesAsync(
        SofaScraper scraper,
        AppDbContext dbContext,
        List<Match> currentLiveMatchesFromScraper,
        CancellationToken ct)
    {
        // 1. Pega os IDs que o Scraper diz que estão ao vivo agora
        var sourceLiveIds = currentLiveMatchesFromScraper.Select(m => m.Id).ToHashSet();

        // 2. Busca no banco todos os jogos que NÓS achamos que ainda estão rolando
        var stuckMatches = await dbContext.Matches
            .Where(m => m.ProcessingStatus == MatchProcessingStatus.InProgress 
                     || m.Status == "Live" 
                     || m.Status == "Inplay" 
                     || m.Status == "1st half" 
                     || m.Status == "2nd half" 
                     || m.Status == "Halftime"
                     || m.Status == "Extra time"
                     || m.Status == "Penalties")
            .ToListAsync(ct);

        // 3. Filtra: Se está no banco como Live, mas NÃO está na lista do Scraper, o jogo acabou.
        var finishedMatches = stuckMatches
            .Where(m => !sourceLiveIds.Contains(m.Id))
            .ToList();

        if (finishedMatches.Any())
        {
            _logger.LogInformation("🕵️ FASE 1.5: Detectados {Count} jogos que saíram do ao vivo. Finalizando...", finishedMatches.Count);

            // ✅ LISTA PARA ATUALIZAR TABELA
            var tournamentIdsToSync = new HashSet<int>();

            foreach (var match in finishedMatches)
            {
                // Força o enriquecimento completo
                bool success = await ProcessMatchAsync(scraper, dbContext, match, ct);
                
                // Se atualizou com sucesso, marca o campeonato para atualizar a tabela
                if (success)
                {
                    tournamentIdsToSync.Add(match.TournamentId);
                }
            }

            // ✅ ATUALIZAÇÃO DE TABELA (STANDINGS)
            // Igualzinho à Fase 2, garante que a tabela reflita o jogo que acabou de sair do limbo
            foreach (var tournamentId in tournamentIdsToSync)
            {
                await SyncStandingsAsync(scraper, dbContext, tournamentId, ct);
            }
        }
    }
}