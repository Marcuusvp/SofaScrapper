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

    /// <summary>
    /// Busca dados completos de uma partida (cache-first)
    /// </summary>
    public async Task<object?> GetMatchFullDataAsync(int matchId, int? seasonId = null)
    {
        // 1. Busca no cache (banco)
        var existingMatch = await _db.Matches
            .Include(m => m.Stats)
            .Include(m => m.Incidents)
            .FirstOrDefaultAsync(m => m.Id == matchId);

        // 1.1. Se já está enriquecido, retorna do banco
        if (existingMatch?.ProcessingStatus == MatchProcessingStatus.Enriched)
        {
            _logger.LogInformation("Match {MatchId} encontrado no cache (enriched)", matchId);
            return new { 
                Source = "Database", 
                Match = existingMatch, 
                Incidents = existingMatch.Incidents
            };
        }

        // 2. Scrape Web
        _logger.LogInformation("Buscando match {MatchId} via scraping", matchId);
        
        try
        {
            var details = await _scraper.GetMatchDetailsAsync(matchId);
            if (details == null) 
            {
                _logger.LogWarning("Match {MatchId} não encontrado no SofaScore", matchId);
                return null;
            }

            // 3. Criar ou atualizar entidade
            var dbMatch = existingMatch ?? new DbMatch { Id = matchId };
            
            // Mapeamento básico
            dbMatch.HomeTeam = details.HomeTeam?.Name ?? "N/A";
            dbMatch.AwayTeam = details.AwayTeam?.Name ?? "N/A";
            dbMatch.HomeScore = details.HomeScore?.Display ?? 0;
            dbMatch.AwayScore = details.AwayScore?.Display ?? 0;
            dbMatch.Status = details.Status?.Description ?? "Unknown";
            dbMatch.StartTimestamp = details.StartTimestamp;
            dbMatch.TournamentId = details.Tournament?.Id ?? 0;
            dbMatch.Round = details.RoundInfo?.Round ?? 0;
            
            // ✅ FIX #1: SeasonId
            if (seasonId.HasValue)
            {
                dbMatch.SeasonId = seasonId.Value;
            }
            else if (existingMatch != null)
            {
                dbMatch.SeasonId = existingMatch.SeasonId; // Mantém o existente
            }
            else
            {
                _logger.LogWarning("Match {MatchId}: SeasonId não fornecido e não existe no banco", matchId);
            }

            // 4. Atualiza status de processamento baseado no jogo
            dbMatch.ProcessingStatus = details.Status?.Description switch
            {
                "Live" or "Inplay" => MatchProcessingStatus.InProgress,
                "Postponed" => MatchProcessingStatus.Postponed,
                "Cancelled" or "Canceled" => MatchProcessingStatus.Cancelled,
                "Ended" or "Finished" => MatchProcessingStatus.Pending, // Vai enriquecer abaixo
                _ => MatchProcessingStatus.Pending
            };

            // 5. Enriquecimento (só se jogo acabou)
            if (details.Status?.Description is "Ended" or "Finished")
            {
                await EnrichMatchDataAsync(dbMatch, matchId);
            }

            // 6. Persistir no banco
            if (existingMatch == null)
            {
                _db.Matches.Add(dbMatch);
            }
            else
            {
                _db.Matches.Update(dbMatch);
            }

            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Match {MatchId} salvo com status {Status}", 
                matchId, 
                dbMatch.ProcessingStatus
            );

            return new { 
                Source = "WebScraper", 
                Match = dbMatch, 
                Incidents = dbMatch.Incidents
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar match {MatchId}", matchId);
            
            // ✅ FIX #3: Incrementa tentativas em caso de erro
            if (existingMatch != null)
            {
                existingMatch.EnrichmentAttempts++;
                existingMatch.LastEnrichmentAttempt = DateTime.UtcNow;
                existingMatch.LastEnrichmentError = ex.Message;
                existingMatch.ProcessingStatus = MatchProcessingStatus.Error;
                await _db.SaveChangesAsync();
            }

            throw;
        }
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
        var incidents = await _scraper.GetMatchGoalsAsync(matchId);
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
}