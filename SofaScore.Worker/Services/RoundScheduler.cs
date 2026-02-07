using Microsoft.EntityFrameworkCore;
using SofaScore.Shared.Data;
using SofaScore.Shared.Services;
using SofaScore.Shared.Utils;
using SofaScoreScraper;

namespace SofaScore.Worker.Services;

/// <summary>
/// Responsável por identificar e buscar a próxima rodada de cada campeonato de forma proativa.
/// Garante que os dados da próxima rodada estejam disponíveis antes de serem consultados pela API.
/// Suporta tanto ligas (rodadas sequenciais) quanto torneios de copa (fases eliminatórias).
/// </summary>
public class RoundScheduler
{
    private readonly AppDbContext _db;
    private readonly SofaScraper _scraper;
    private readonly ILogger<RoundScheduler> _logger;

    public RoundScheduler(
        AppDbContext db, 
        SofaScraper scraper, 
        ILogger<RoundScheduler> logger)
    {
        _db = db;
        _scraper = scraper;
        _logger = logger;
    }

    /// <summary>
    /// Verifica e busca a próxima rodada para todos os campeonatos configurados.
    /// Executa periodicamente (a cada 6h conforme configurado no Worker).
    /// </summary>
    public async Task CheckAndFetchNextRoundsAsync(CancellationToken ct)
    {
        _logger.LogInformation("🔄 Round Scheduler: Verificando próximas rodadas para todos os campeonatos...");

        foreach (var tournament in TournamentsInfo.AllTournaments.List)
        {
            try
            {
                // ✅ Champions League tem lógica especial (fase de liga + eliminatórias)
                if (tournament.tournamentId == TournamentsInfo.ChampionsLeague.TournamentId)
                {
                    await HandleChampionsLeagueAsync(ct);
                }
                else
                {
                    // ✅ Ligas normais (rodadas sequenciais)
                    await CheckAndFetchNextRoundForTournamentAsync(
                        tournament.tournamentId,
                        tournament.seasonId,
                        tournament.totalRounds,
                        tournament.name,
                        ct
                    );
                }

                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "❌ Round Scheduler: Erro ao verificar próxima rodada para {Tournament}", 
                    tournament.name
                );
            }
        }

        _logger.LogInformation("✅ Round Scheduler: Verificação completa.");
    }

    /// <summary>
    /// Lógica especial para Champions League: fase de liga (1-8) + fases eliminatórias.
    /// </summary>
    private async Task HandleChampionsLeagueAsync(CancellationToken ct)
    {
        int tournamentId = TournamentsInfo.ChampionsLeague.TournamentId;
        int seasonId = TournamentsInfo.ChampionsLeague.SeasonId;
        string tournamentName = TournamentsInfo.ChampionsLeague.Name;

        // 1. Busca fase de liga (rodadas 1-8) normalmente
        var leaguePhaseRounds = await _db.Matches
            .Where(m => m.TournamentId == tournamentId && 
                       m.SeasonId == seasonId && 
                       m.Round >= TournamentsInfo.ChampionsLeague.LeaguePhaseStart && 
                       m.Round <= TournamentsInfo.ChampionsLeague.LeaguePhaseEnd)
            .Select(m => m.Round)
            .Distinct()
            .ToListAsync(ct);

        if (leaguePhaseRounds.Any())
        {
            var maxLeagueRound = leaguePhaseRounds.Max();

            // Se ainda estamos na fase de liga (rodadas < 8)
            if (maxLeagueRound < TournamentsInfo.ChampionsLeague.LeaguePhaseEnd)
            {
                bool isCurrentRoundResolved = await IsRoundResolvedAsync(tournamentId, seasonId, maxLeagueRound, ct);

                if (isCurrentRoundResolved)
                {
                    int nextRound = maxLeagueRound + 1;

                    if (nextRound <= TournamentsInfo.ChampionsLeague.LeaguePhaseEnd)
                    {
                        bool exists = await _db.Matches.AnyAsync(m => 
                            m.TournamentId == tournamentId && 
                            m.SeasonId == seasonId && 
                            m.Round == nextRound, ct);

                        if (!exists)
                        {
                            _logger.LogInformation("🔍 {Tournament}: Buscando rodada {Round} (fase de liga)...", 
                                tournamentName, nextRound);
                            await FetchLeaguePhaseRoundAsync(tournamentId, seasonId, nextRound, ct);
                        }
                    }
                }
                return; // Ainda na fase de liga, não buscar eliminatórias
            }

            // Se rodada 8 está completa, verificar fases eliminatórias
            if (maxLeagueRound == TournamentsInfo.ChampionsLeague.LeaguePhaseEnd)
            {
                bool isLeaguePhaseComplete = await IsRoundResolvedAsync(tournamentId, seasonId, maxLeagueRound, ct);

                if (isLeaguePhaseComplete)
                {
                    await HandleKnockoutPhasesAsync(tournamentId, seasonId, tournamentName, ct);
                }
            }
        }
        else
        {
            // Banco vazio, buscar rodada 1
            _logger.LogWarning("⚠️ {Tournament}: Nenhuma rodada encontrada no banco. Campeonato pode não estar inicializado.", 
                tournamentName);
        }
    }

    private async Task HandleKnockoutPhasesAsync(int tournamentId, int seasonId, string tournamentName, CancellationToken ct)
    {
        var knockoutRoundIds = TournamentsInfo.ChampionsLeague.KnockoutPhases.Select(p => p.RoundId).ToList();

        // ✅ Pegar o timestamp da rodada 8 (última rodada da fase de liga)
        var leaguePhaseEndTimestamp = await _db.Matches
        .Where(m => m.TournamentId == tournamentId && 
            m.SeasonId == seasonId && 
            m.Round == TournamentsInfo.ChampionsLeague.LeaguePhaseEnd)
        .OrderByDescending(m => m.StartTimestamp) // Ordena pelo maior
        .Select(m => m.StartTimestamp)
        .FirstOrDefaultAsync(ct); // Pega o primeiro ou 0 (default do long)

        // Se não encontrou rodada 8, usa timestamp zero (aceita qualquer jogo)
        // Isso garante que se não há fase de liga, considera tudo como fase eliminatória
        long cutoffTimestamp = leaguePhaseEndTimestamp;

        // ✅ Filtrar jogos de fases eliminatórias (excluindo jogos da fase de liga com Round 5)
        var existingKnockoutRounds = await _db.Matches
            .Where(m => m.TournamentId == tournamentId && 
                    m.SeasonId == seasonId && 
                    knockoutRoundIds.Contains(m.Round) &&
                    m.StartTimestamp > cutoffTimestamp) // Apenas jogos APÓS a fase de liga
            .Select(m => m.Round)
            .Distinct()
            .ToListAsync(ct);

        if (!existingKnockoutRounds.Any())
        {
            var firstPhase = TournamentsInfo.ChampionsLeague.KnockoutPhases.First();
            await TryFetchKnockoutPhaseAsync(tournamentId, seasonId, firstPhase, tournamentName, ct);
            return;
        }

        int lastPhaseIndex = -1;
        for (int i = TournamentsInfo.ChampionsLeague.KnockoutPhases.Count - 1; i >= 0; i--)
        {
            var phase = TournamentsInfo.ChampionsLeague.KnockoutPhases[i];
            if (existingKnockoutRounds.Contains(phase.RoundId))
            {
                lastPhaseIndex = i;
                break;
            }
        }

        if (lastPhaseIndex == -1)
        {
            _logger.LogWarning("⚠️ {Tournament}: Fases eliminatórias encontradas no banco mas não correspondem ao mapeamento.", 
                tournamentName);
            return;
        }

        var lastPhase = TournamentsInfo.ChampionsLeague.KnockoutPhases[lastPhaseIndex];
        var lastPhaseRoundId = lastPhase.RoundId;

        bool isLastPhaseComplete = await IsRoundResolvedAsync(tournamentId, seasonId, lastPhaseRoundId, ct);

        if (!isLastPhaseComplete)
        {
            _logger.LogDebug("📍 {Tournament}: {Phase} (round {RoundId}) ainda não está completa.", 
                tournamentName, lastPhase.Name, lastPhaseRoundId);
            return;
        }

        int nextPhaseIndex = lastPhaseIndex + 1;

        if (nextPhaseIndex >= TournamentsInfo.ChampionsLeague.KnockoutPhases.Count)
        {
            _logger.LogInformation("🏆 {Tournament}: Todas as fases eliminatórias foram processadas (Final completa).", 
                tournamentName);
            return;
        }

        var nextPhase = TournamentsInfo.ChampionsLeague.KnockoutPhases[nextPhaseIndex];
        await TryFetchKnockoutPhaseAsync(tournamentId, seasonId, nextPhase, tournamentName, ct);
    }



    /// <summary>
    /// Tenta buscar uma fase eliminatória específica (Playoff, Oitavas, etc).
    /// </summary>
    private async Task TryFetchKnockoutPhaseAsync(
        int tournamentId,
        int seasonId,
        KnockoutPhase phase,
        string tournamentName,
        CancellationToken ct)
    {
        // Verifica se já existe
        bool exists = await _db.Matches.AnyAsync(m => 
            m.TournamentId == tournamentId && 
            m.SeasonId == seasonId && 
            m.Round == phase.RoundId, ct);

        if (exists)
        {
            _logger.LogDebug("✅ {Tournament}: {Phase} (round {RoundId}) já existe no banco.", 
                tournamentName, phase.Name, phase.RoundId);
            return;
        }

        _logger.LogInformation("🔍 {Tournament}: Buscando {Phase} (round {RoundId})...", 
            tournamentName, phase.Name, phase.RoundId);

        try
        {
            var matches = await _scraper.GetQualificationMatchesAsync(
                tournamentId,
                seasonId,
                phase.RoundId,
                phase.Slug,
                phase.Prefix
            );

            if (!matches.Any())
            {
                _logger.LogWarning("⚠️ {Tournament}: {Phase} não retornou jogos. Chaveamento pode não estar disponível ainda.", 
                    tournamentName, phase.Name);
                return;
            }

            // Salva os jogos no banco
            foreach (var match in matches)
            {
                var dbMatch = new DbMatch
                {
                    Id = match.Id,
                    TournamentId = tournamentId,
                    SeasonId = seasonId,
                    Round = phase.RoundId,
                    HomeTeam = match.HomeTeam,
                    AwayTeam = match.AwayTeam,
                    HomeScore = match.HomeScore ?? 0,
                    AwayScore = match.AwayScore ?? 0,
                    Status = match.Status,
                    StartTimestamp = TimestampHelper.FixSofaScoreTimestamp(match.StartTimestamp),
                    ProcessingStatus = match.Status switch
                    {
                        "Live" or "Inplay" => MatchProcessingStatus.InProgress,
                        "Postponed" => MatchProcessingStatus.Postponed,
                        "Cancelled" or "Canceled" => MatchProcessingStatus.Cancelled,
                        "Ended" or "Finished" => MatchProcessingStatus.Pending,
                        _ => MatchProcessingStatus.Pending
                    }
                };

                _db.Matches.Add(dbMatch);
            }

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("✅ {Tournament}: {Phase} adicionada ({Count} jogos)", 
                tournamentName, phase.Name, matches.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao buscar {Phase} de {Tournament}", 
                phase.Name, tournamentName);
        }
    }

    /// <summary>
    /// Busca uma rodada da fase de liga (1-8) usando API padrão.
    /// </summary>
    private async Task FetchLeaguePhaseRoundAsync(int tournamentId, int seasonId, int round, CancellationToken ct)
    {
        try
        {
            var matches = await _scraper.GetMatchesAsync(tournamentId, seasonId, round);

            if (!matches.Any())
            {
                _logger.LogWarning("⚠️ Champions League: Rodada {Round} não retornou jogos.", round);
                return;
            }

            foreach (var match in matches)
            {
                var dbMatch = new DbMatch
                {
                    Id = match.Id,
                    TournamentId = tournamentId,
                    SeasonId = seasonId,
                    Round = round,
                    HomeTeam = match.HomeTeam,
                    AwayTeam = match.AwayTeam,
                    HomeScore = match.HomeScore ?? 0,
                    AwayScore = match.AwayScore ?? 0,
                    Status = match.Status,
                    StartTimestamp = TimestampHelper.FixSofaScoreTimestamp(match.StartTimestamp),
                    ProcessingStatus = match.Status switch
                    {
                        "Live" or "Inplay" => MatchProcessingStatus.InProgress,
                        "Postponed" => MatchProcessingStatus.Postponed,
                        "Cancelled" or "Canceled" => MatchProcessingStatus.Cancelled,
                        "Ended" or "Finished" => MatchProcessingStatus.Pending,
                        _ => MatchProcessingStatus.Pending
                    }
                };

                _db.Matches.Add(dbMatch);
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("✅ Champions League: Rodada {Round} adicionada ({Count} jogos)", round, matches.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao buscar rodada {Round} da Champions League", round);
        }
    }

    /// <summary>
    /// Verifica e busca a próxima rodada para um campeonato de liga (rodadas sequenciais).
    /// </summary>
    private async Task CheckAndFetchNextRoundForTournamentAsync(
        int tournamentId,
        int seasonId,
        int totalRounds,
        string tournamentName,
        CancellationToken ct)
    {
        var rounds = await _db.Matches
            .Where(m => m.TournamentId == tournamentId && m.SeasonId == seasonId)
            .Select(m => m.Round)
            .Distinct()
            .ToListAsync(ct);

        if (!rounds.Any())
        {
            _logger.LogWarning("⚠️ {Tournament}: Nenhuma rodada encontrada no banco. Campeonato pode não estar inicializado.", 
                tournamentName);
            return;
        }

        var currentRound = rounds.Max();
        bool isCurrentRoundResolved = await IsRoundResolvedAsync(tournamentId, seasonId, currentRound, ct);

        if (!isCurrentRoundResolved)
        {
            _logger.LogDebug("📍 {Tournament}: Rodada {Round} ainda não está completa. Aguardando...", 
                tournamentName, currentRound);
            return;
        }

        int nextRound = currentRound + 1;

        if (nextRound > totalRounds)
        {
            _logger.LogInformation("🏁 {Tournament}: Todas as {Total} rodadas já foram processadas.", 
                tournamentName, totalRounds);
            return;
        }

        bool nextRoundExists = await _db.Matches.AnyAsync(m => 
            m.TournamentId == tournamentId && 
            m.SeasonId == seasonId && 
            m.Round == nextRound, ct);

        if (nextRoundExists)
        {
            _logger.LogDebug("✅ {Tournament}: Rodada {Round} já existe no banco.", 
                tournamentName, nextRound);
            return;
        }

        _logger.LogInformation("🔍 {Tournament}: Buscando rodada {Round}...", 
            tournamentName, nextRound);

        try
        {
            var matches = await _scraper.GetMatchesAsync(tournamentId, seasonId, nextRound);

            if (!matches.Any())
            {
                _logger.LogWarning("⚠️ {Tournament}: Rodada {Round} não retornou jogos. Pode não estar disponível ainda.", 
                    tournamentName, nextRound);
                return;
            }

            foreach (var match in matches)
            {
                var dbMatch = new DbMatch
                {
                    Id = match.Id,
                    TournamentId = tournamentId,
                    SeasonId = seasonId,
                    Round = nextRound,
                    HomeTeam = match.HomeTeam,
                    AwayTeam = match.AwayTeam,
                    HomeScore = match.HomeScore ?? 0,
                    AwayScore = match.AwayScore ?? 0,
                    Status = match.Status,
                    StartTimestamp = TimestampHelper.FixSofaScoreTimestamp(match.StartTimestamp),
                    ProcessingStatus = match.Status switch
                    {
                        "Live" or "Inplay" => MatchProcessingStatus.InProgress,
                        "Postponed" => MatchProcessingStatus.Postponed,
                        "Cancelled" or "Canceled" => MatchProcessingStatus.Cancelled,
                        "Ended" or "Finished" => MatchProcessingStatus.Pending,
                        _ => MatchProcessingStatus.Pending
                    }
                };

                _db.Matches.Add(dbMatch);
            }

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("✅ {Tournament}: Rodada {Round} adicionada ({Count} jogos)", 
                tournamentName, nextRound, matches.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao buscar rodada {Round} de {Tournament}", 
                nextRound, tournamentName);
        }
    }

    /// <summary>
    /// Verifica se uma rodada está "resolvida" (todos jogos em estado terminal).
    /// </summary>
    private async Task<bool> IsRoundResolvedAsync(
        int tournamentId,
        int seasonId,
        int round,
        CancellationToken ct)
    {
        var matches = await _db.Matches
            .Where(m => 
                m.TournamentId == tournamentId && 
                m.SeasonId == seasonId && 
                m.Round == round)
            .ToListAsync(ct);

        if (!matches.Any())
            return false;

        // ✅ ADICIONAR: Se nenhum jogo começou ainda (todos Not started), não considerar como "resolvido"
        var hasStarted = matches.Any(m => m.Status != "Not started");
        if (!hasStarted)
        {
            // Jogos existem mas ainda não começaram - não considerar fase como completa
            return false;
        }

        var terminalStatuses = new[]
        {
            MatchProcessingStatus.Enriched,
            MatchProcessingStatus.Cancelled,
            MatchProcessingStatus.Postponed
        };

        return matches.All(m => terminalStatuses.Contains(m.ProcessingStatus));
    }

}
