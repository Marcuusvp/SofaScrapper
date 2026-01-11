using Microsoft.EntityFrameworkCore;
using SofaScore.Api.Data;
using SofaScoreScraper;

namespace SofaScore.Api.Services;

/// <summary>
/// Responsável por buscar dados históricos de partidas para análise
/// </summary>
public class MatchDataFetcher
{
    private readonly AppDbContext _db;
    private readonly ILogger<MatchDataFetcher> _logger;

    public MatchDataFetcher(AppDbContext db, ILogger<MatchDataFetcher> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Busca os últimos N jogos de um time como mandante
    /// </summary>
    public async Task<List<DbMatch>> GetHomeMatchesAsync(
        string teamName,
        int tournamentId,
        int seasonId,
        long beforeTimestamp,
        int limit)
    {
        _logger.LogDebug(
            "Buscando últimos {Limit} jogos de {Team} como mandante (Tournament: {Tournament})",
            limit, teamName, tournamentId
        );

        var matches = await _db.Matches
            .Include(m => m.Stats)
            .Include(m => m.Incidents)
            .Where(m =>
                m.HomeTeam == teamName &&
                m.TournamentId == tournamentId &&
                m.SeasonId == seasonId &&
                m.ProcessingStatus == MatchProcessingStatus.Enriched &&
                m.StartTimestamp < beforeTimestamp
            )
            .OrderByDescending(m => m.StartTimestamp)
            .Take(limit)
            .ToListAsync();

        _logger.LogInformation(
            "Encontrados {Count} jogos de {Team} como mandante",
            matches.Count, teamName
        );

        return matches;
    }

    /// <summary>
    /// Busca os últimos N jogos de um time como visitante
    /// </summary>
    public async Task<List<DbMatch>> GetAwayMatchesAsync(
        string teamName,
        int tournamentId,
        int seasonId,
        long beforeTimestamp,
        int limit)
    {
        _logger.LogDebug(
            "Buscando últimos {Limit} jogos de {Team} como visitante (Tournament: {Tournament})",
            limit, teamName, tournamentId
        );

        var matches = await _db.Matches
            .Include(m => m.Stats)
            .Include(m => m.Incidents)
            .Where(m =>
                m.AwayTeam == teamName &&
                m.TournamentId == tournamentId &&
                m.SeasonId == seasonId &&
                m.ProcessingStatus == MatchProcessingStatus.Enriched &&
                m.StartTimestamp < beforeTimestamp
            )
            .OrderByDescending(m => m.StartTimestamp)
            .Take(limit)
            .ToListAsync();

        _logger.LogInformation(
            "Encontrados {Count} jogos de {Team} como visitante",
            matches.Count, teamName
        );

        return matches;
    }

    /// <summary>
    /// Busca detalhes completos de uma partida específica
    /// </summary>
    public async Task<DbMatch?> GetMatchByIdAsync(int matchId)
    {
        return await _db.Matches
            .Include(m => m.Stats)
            .Include(m => m.Incidents)
            .FirstOrDefaultAsync(m => m.Id == matchId);
    }

    /// <summary>
    /// Verifica se uma partida existe e pode ser analisada
    /// </summary>
    public async Task<bool> CanAnalyzeMatchAsync(int matchId)
    {
        var match = await _db.Matches.FindAsync(matchId);
        
        if (match == null)
        {
            _logger.LogWarning("Partida {MatchId} não encontrada", matchId);
            return false;
        }

        // Só podemos analisar jogos que ainda não aconteceram ou que já terminaram
        // (para validar predições)
        if (match.Status == "Live" || match.Status == "Inplay")
        {
            _logger.LogWarning("Partida {MatchId} está em andamento", matchId);
            return false;
        }

        return true;
    }
}