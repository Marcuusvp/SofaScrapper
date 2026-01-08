using Microsoft.EntityFrameworkCore;
using SofaScore.Api.Data;
using SofaScoreScraper; // Seus modelos DTO e Incidentes

namespace SofaScore.Api.Services;

public class DataManager
{
    private readonly SofaScraper _scraper;
    private readonly AppDbContext _db; // Injeção do Contexto

    public DataManager(SofaScraper scraper, AppDbContext db)
    {
        _scraper = scraper;
        _db = db;
    }

    // Este método retorna um objeto complexo com dados do banco + incidentes
    public async Task<object?> GetMatchFullDataAsync(int matchId)
    {
        // 1. Tenta Cache (Banco)
        var existingMatch = await _db.Matches
            .Include(m => m.Stats)
            .FirstOrDefaultAsync(m => m.Id == matchId);

        // Se está finalizado no banco, retorna do banco
        if (existingMatch != null && (existingMatch.Status == "Ended" || existingMatch.Status == "Finished"))
        {
            // Para a API, vamos buscar os incidentes na hora (Scrape) ou precisaríamos salvar no banco também.
            // Por simplicidade agora, vamos buscar incidentes sempre ao vivo ou salvar se quiser.
            // Vou manter a lógica de buscar incidentes via scrape rápido para complementar o cache.
            var incidents = await _scraper.GetMatchGoalsAsync(matchId);
            
            return new { 
                Source = "Database", 
                Match = existingMatch, 
                Incidents = incidents 
            };
        }

        // 2. Scrape Web
        var details = await _scraper.GetMatchDetailsAsync(matchId);
        if (details == null) return null;

        var statsResponse = await _scraper.GetMatchStatisticsAsync(matchId);
        var liveIncidents = await _scraper.GetMatchGoalsAsync(matchId);

        if (details == null) return null;

        // 3. Atualiza/Salva no Banco
        var dbMatch = existingMatch ?? new DbMatch { Id = matchId };
        
        // Mapeamento (Simplificado para o exemplo)
        dbMatch.HomeTeam = details.HomeTeam?.Name ?? "N/A";
        dbMatch.AwayTeam = details.AwayTeam?.Name ?? "N/A";
        dbMatch.HomeScore = details.HomeScore?.Display ?? 0;
        dbMatch.AwayScore = details.AwayScore?.Display ?? 0;
        dbMatch.Status = details.Status?.Description ?? "Unknown";
        dbMatch.StartTimestamp = details.StartTimestamp;
        dbMatch.TournamentId = details.Tournament?.Id ?? 0; // Ajustar conforme seu modelo
        dbMatch.Round = details.RoundInfo?.Round ?? 0;
        
        // Salva estatísticas apenas se acabou
        if ((dbMatch.Status == "Ended" || dbMatch.Status == "Finished") && statsResponse?.Statistics != null)
        {
            if(existingMatch != null) 
            {
                 var oldStats = await _db.MatchStats.Where(s => s.MatchId == matchId).ToListAsync();
                 _db.MatchStats.RemoveRange(oldStats);
            }

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

        if (existingMatch == null) _db.Matches.Add(dbMatch);
        else _db.Matches.Update(dbMatch);

        await _db.SaveChangesAsync();

        return new { 
            Source = "WebScraper", 
            Match = dbMatch, 
            Incidents = liveIncidents 
        };
    }
}