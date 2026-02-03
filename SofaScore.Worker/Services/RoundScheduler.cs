using Microsoft.EntityFrameworkCore;
using SofaScore.Shared.Data;
using SofaScore.Shared.Services;
using SofaScoreScraper;

namespace SofaScore.Worker.Services;

/// <summary>
/// Responsável por identificar e buscar a próxima rodada de cada campeonato
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
    /// Verifica e busca a próxima rodada para todos os campeonatos configurados
    /// </summary>
    public async Task CheckAndFetchNextRoundsAsync(CancellationToken ct)
    {
        _logger.LogInformation("🔄 Verificando próximas rodadas para todos os campeonatos...");

        foreach (var tournament in TournamentsInfo.AllTournaments.List)
        {
            try
            {
                await CheckAndFetchNextRoundForTournamentAsync(
                    tournament.tournamentId,
                    tournament.seasonId,
                    tournament.totalRounds,
                    tournament.name,
                    ct
                );

                // Delay entre campeonatos para não sobrecarregar o scraper
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "Erro ao verificar próxima rodada para {Tournament}", 
                    tournament.name
                );
            }
        }
    }

    /// <summary>
    /// Verifica e busca a próxima rodada para um campeonato específico
    /// </summary>
    private async Task CheckAndFetchNextRoundForTournamentAsync(
        int tournamentId,
        int seasonId,
        int totalRounds,
        string tournamentName,
        CancellationToken ct)
    {
        // 1. Descobre qual é a rodada atual (maior rodada que tem jogos)
        var currentRound = await _db.Matches
            .Where(m => m.TournamentId == tournamentId && m.SeasonId == seasonId)
            .Select(m => m.Round)
            .DefaultIfEmpty(0)
            .MaxAsync(ct);

        if (currentRound == 0)
        {
            _logger.LogWarning(
                "⚠️ {Tournament}: Nenhuma rodada encontrada no banco. Campeonato pode não estar inicializado.",
                tournamentName
            );
            return;
        }

        // 2. Verifica se a rodada atual está "resolvida"
        bool isCurrentRoundResolved = await IsRoundResolvedAsync(
            tournamentId, 
            seasonId, 
            currentRound, 
            ct
        );

        if (!isCurrentRoundResolved)
        {
            _logger.LogDebug(
                "📍 {Tournament}: Rodada {Round} ainda não está completa. Aguardando...",
                tournamentName, currentRound
            );
            return;
        }

        // 3. Calcula a próxima rodada
        int nextRound = currentRound + 1;

        if (nextRound > totalRounds)
        {
            _logger.LogInformation(
                "🏁 {Tournament}: Todas as {Total} rodadas já foram processadas.",
                tournamentName, totalRounds
            );
            return;
        }

        // 4. Verifica se a próxima rodada já existe no banco
        bool nextRoundExists = await _db.Matches
            .AnyAsync(m => 
                m.TournamentId == tournamentId && 
                m.SeasonId == seasonId && 
                m.Round == nextRound,
                ct
            );

        if (nextRoundExists)
        {
            _logger.LogDebug(
                "✅ {Tournament}: Rodada {Round} já existe no banco.",
                tournamentName, nextRound
            );
            return;
        }

        // 5. Busca a próxima rodada via scraper
        _logger.LogInformation(
            "🔍 {Tournament}: Buscando rodada {Round}...",
            tournamentName, nextRound
        );

        try
        {
            var matches = await _scraper.GetMatchesAsync(tournamentId, seasonId, nextRound);

            if (!matches.Any())
            {
                _logger.LogWarning(
                    "⚠️ {Tournament}: Rodada {Round} não retornou jogos. Pode não estar disponível ainda.",
                    tournamentName, nextRound
                );
                return;
            }

            // 6. Salva os jogos no banco
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
                    StartTimestamp = match.StartTimestamp,
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

            _logger.LogInformation(
                "✅ {Tournament}: Rodada {Round} adicionada ({Count} jogos)",
                tournamentName, nextRound, matches.Count
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "❌ Erro ao buscar rodada {Round} de {Tournament}",
                nextRound, tournamentName
            );
        }
    }

    /// <summary>
    /// Verifica se uma rodada está "resolvida" (todos jogos em estado terminal)
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

        // Uma rodada está resolvida quando todos os jogos estão em:
        // - Enriched (finalizado e processado)
        // - Cancelled (cancelado)
        // - Postponed (adiado - será um novo jogo em outra rodada)
        var terminalStatuses = new[]
        {
            MatchProcessingStatus.Enriched,
            MatchProcessingStatus.Cancelled,
            MatchProcessingStatus.Postponed
        };

        bool allResolved = matches.All(m => terminalStatuses.Contains(m.ProcessingStatus));

        return allResolved;
    }
}