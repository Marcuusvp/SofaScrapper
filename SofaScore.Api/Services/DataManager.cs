using Microsoft.EntityFrameworkCore;
using SofaScore.Api.Data;
using SofaScoreScraper;

namespace SofaScore.Api.Services;

public class DataManager
{
    private readonly SofaScraper _scraper;
    private readonly AppDbContext _db;
    private readonly ILogger<DataManager> _logger;

    public DataManager(SofaScraper scraper, AppDbContext db, ILogger<DataManager> logger)
    {
        _scraper = scraper;
        _db = db;
        _logger = logger;
    }

    // ✅ CORRIGIDO: Removido parâmetro seasonId opcional
    public async Task<object?> GetMatchFullDataAsync(int matchId)
    {
        var existingMatch = await _db.Matches
            .Include(m => m.Stats)
            .Include(m => m.Incidents)
            .FirstOrDefaultAsync(m => m.Id == matchId);

        // Se já está enriquecido, retorna do cache
        if (existingMatch?.ProcessingStatus == MatchProcessingStatus.Enriched)
        {
            _logger.LogInformation("Match {MatchId} encontrado no cache (enriched)", matchId);
            return new { 
                Source = "Database", 
                Match = existingMatch, 
                Incidents = existingMatch.Incidents
            };
        }

        // Busca detalhes via scraper
        var details = await _scraper.GetMatchDetailsAsync(matchId);
        if (details == null)
        {
            _logger.LogWarning("Match {MatchId} não encontrado no SofaScore", matchId);
            return null;
        }

        // ✅ Infere o SeasonId de forma limpa
        int seasonId = DetermineSeasonId(existingMatch, details);

        // Criar ou atualizar entidade
        var dbMatch = existingMatch ?? new DbMatch { Id = matchId };
        
        dbMatch.HomeTeam = details.HomeTeam?.Name ?? "N/A";
        dbMatch.AwayTeam = details.AwayTeam?.Name ?? "N/A";
        dbMatch.HomeScore = details.HomeScore?.Display ?? 0;
        dbMatch.AwayScore = details.AwayScore?.Display ?? 0;
        dbMatch.Status = details.Status?.Description ?? "Unknown";
        dbMatch.StartTimestamp = details.StartTimestamp;
        dbMatch.TournamentId = details.Tournament?.Id ?? 0;
        dbMatch.Round = details.RoundInfo?.Round ?? 0;
        dbMatch.SeasonId = seasonId;

        dbMatch.ProcessingStatus = details.Status?.Description switch
        {
            "Live" or "Inplay" => MatchProcessingStatus.InProgress,
            "Postponed" => MatchProcessingStatus.Postponed,
            "Cancelled" or "Canceled" => MatchProcessingStatus.Cancelled,
            "Ended" or "Finished" => MatchProcessingStatus.Pending,
            _ => MatchProcessingStatus.Pending
        };

        if (details.Status?.Description is "Ended" or "Finished")
        {
            await EnrichMatchDataAsync(dbMatch, matchId);
        }

        if (existingMatch == null)
        {
            _db.Matches.Add(dbMatch);
        }
        else
        {
            _db.Matches.Update(dbMatch);
        }

        await _db.SaveChangesAsync();

        return new { 
            Source = "WebScraper", 
            Match = dbMatch, 
            Incidents = dbMatch.Incidents
        };
    }

    // ✅ NOVO: Método auxiliar para determinar SeasonId
    private int DetermineSeasonId(DbMatch? existingMatch, EventDetail details)
    {
        // Prioridade 1: Usa o que já estava salvo
        if (existingMatch?.SeasonId > 0)
        {
            return existingMatch.SeasonId;
        }

        // Prioridade 2: Infere do TournamentsInfo
        if (details.Tournament?.Id != null)
        {
            var seasonId = TournamentsInfo.GetSeasonIdByTournament(details.Tournament.Id);
            if (seasonId.HasValue)
            {
                return seasonId.Value;
            }
        }

        // Falhou
        _logger.LogError(
            "Não foi possível determinar SeasonId para Tournament {TournamentId}",
            details.Tournament?.Id
        );
        throw new InvalidOperationException(
            $"Tournament {details.Tournament?.Id} não está configurado em TournamentsInfo"
        );
    }
        
    /// <summary>
    /// Enriquece uma partida finalizada com stats e incidents
    /// </summary>
    private async Task EnrichMatchDataAsync(DbMatch dbMatch, int matchId)
    {
        _logger.LogInformation("Enriquecendo match {MatchId}", matchId);

        // Incrementa contador de tentativas
        dbMatch.EnrichmentAttempts++;
        dbMatch.LastEnrichmentAttempt = DateTime.UtcNow;

        // 1. Limpa dados antigos (se for re-enriquecimento)
        var oldStats = await _db.MatchStats.Where(s => s.MatchId == matchId).ToListAsync();
        var oldIncidents = await _db.Incidents.Where(i => i.MatchId == matchId).ToListAsync();
        
        if (oldStats.Any()) _db.MatchStats.RemoveRange(oldStats);
        if (oldIncidents.Any()) _db.Incidents.RemoveRange(oldIncidents);

        // 2. Busca estatísticas
        var statsResponse = await _scraper.GetMatchStatisticsAsync(matchId);
        if (statsResponse?.Statistics != null)
        {
            foreach (var period in statsResponse.Statistics)
            {
                if (period.Groups == null) continue;
                foreach (var group in period.Groups)
                {
                    if (group.StatisticsItems == null) continue;
                    foreach (var item in group.StatisticsItems)
                    {
                        dbMatch.Stats.Add(new DbMatchStat
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

        // 3. Busca incidentes
        var incidents = await _scraper.GetMatchIncidentsAsync(matchId);
        if (incidents != null)
        {
            foreach (var inc in incidents)
            {
                dbMatch.Incidents.Add(new DbIncident
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

        // ✅ FIX #2: Marca como enriquecido com sucesso
        dbMatch.ProcessingStatus = MatchProcessingStatus.Enriched;
        
        _logger.LogInformation(
            "Match {MatchId} enriquecido: {StatsCount} stats, {IncidentsCount} incidents", 
            matchId, 
            dbMatch.Stats.Count, 
            dbMatch.Incidents.Count
        );
    }

    /// <summary>
    /// Salva/atualiza jogos de uma rodada (usado pelo Worker)
    /// </summary>
    public async Task<int> SaveOrUpdateMatchesFromRoundAsync(
        int tournamentId, 
        int seasonId, 
        int round)
    {
        _logger.LogInformation(
            "Buscando jogos: Tournament {TournamentId}, Season {SeasonId}, Round {Round}",
            tournamentId, seasonId, round
        );

        // 1. Busca jogos da rodada via scraper
        var matches = await _scraper.GetMatchesAsync(tournamentId, seasonId, round);
        
        if (!matches.Any())
        {
            _logger.LogWarning("Nenhum jogo encontrado para esta rodada");
            return 0;
        }

        // 2. Salva/atualiza cada jogo
        int insertCount = 0;
        int updateCount = 0;
        foreach (var match in matches)
        {
            var dbMatch = await _db.Matches.FindAsync(match.Id);
            
            if (dbMatch == null)
            {
                // Primeira inserção
                dbMatch = new DbMatch
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
                    StartTimestamp = new DateTimeOffset(match.StartTime).ToUnixTimeSeconds(),
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
                insertCount++;
            }
            else
            {
                // Atualiza apenas scores e status (não enriquece aqui)
                dbMatch.HomeScore = match.HomeScore ?? 0;
                dbMatch.AwayScore = match.AwayScore ?? 0;
                dbMatch.Status = match.Status;
                
                // Atualiza status de processamento
                if (dbMatch.Status is "Ended" or "Finished" && 
                    dbMatch.ProcessingStatus == MatchProcessingStatus.InProgress)
                {
                    dbMatch.ProcessingStatus = MatchProcessingStatus.Pending;
                }
                
                _db.Matches.Update(dbMatch);
                updateCount++;
            }
        }

        await _db.SaveChangesAsync();
        
        _logger.LogInformation(
            "Rodada {Round}: {Total} jogos ({Inserted} novos, {Updated} atualizados)",
            round, matches.Count, insertCount, updateCount
        );

        return matches.Count;
    }
}