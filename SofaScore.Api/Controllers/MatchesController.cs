using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SofaScore.Shared.Data;
using SofaScore.Shared.Services;
using SofaScoreScraper;

namespace SofaScore.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MatchesController : ControllerBase
{
    private readonly SofaScraper _scraper;
    private readonly DataManager _dataManager;
    private readonly AppDbContext _db;

    public MatchesController(SofaScraper scraper, DataManager dataManager, AppDbContext db)
    {
        _scraper = scraper;
        _dataManager = dataManager;
        _db = db;
    }

    /// <summary>
    /// Retorna jogos ao vivo baseado em janela de segurança e status.
    /// </summary>
    [HttpGet("live")]
    public async Task<IActionResult> GetLive()
    {
        var safetyCutoff = DateTimeOffset.UtcNow.AddHours(-5).ToUnixTimeSeconds();

        var liveMatches = await _db.Matches
            .Where(m => 
                m.StartTimestamp >= safetyCutoff && 
                (m.ProcessingStatus == MatchProcessingStatus.InProgress 
                 || m.Status == "Live" 
                 || m.Status == "Inplay"
                 || m.Status == "1st half"
                 || m.Status == "2nd half"
                 || m.Status == "Halftime"
                 || m.Status == "Extra time"
                 || m.Status == "Penalties"))
            .OrderBy(m => m.StartTimestamp)
            .ToListAsync();

        // Mapeia para MatchResponse usando as extensões
        var response = liveMatches.Select(m => m.ToResponse()).ToList();
        return Ok(response);
    }

    /// <summary>
    /// Retorna jogos de uma rodada específica de um campeonato.
    /// Dados devem estar previamente no banco (populados pelo Worker).
    /// </summary>
    [HttpGet("tournament/{tournamentName}/round/{round}")]
    public async Task<IActionResult> GetByRound(string tournamentName, int round)
    {
        try 
        {
            var info = TournamentsInfo.GetTournamentInfo(tournamentName);

            var matches = await _db.Matches
                .Where(m => 
                    m.TournamentId == info.tournamentId && 
                    m.SeasonId == info.seasonId && 
                    m.Round == round)
                .OrderBy(m => m.StartTimestamp)
                .ToListAsync();

            if (!matches.Any())
            {
                return Ok(new
                {
                    message = $"Nenhum jogo encontrado para {info.name} - Rodada {round}. Os dados serão carregados em breve.",
                    tournament = info.name,
                    round = round,
                    matches = Array.Empty<MatchResponse>()
                });
            }

            // Mapeia para MatchResponse incluindo o nome do torneio
            var response = matches.Select(m => m.ToResponse(info.name)).ToList();
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Retorna jogos de uma fase específica da Champions League (rodadas 1-8 da fase de liga).
    /// </summary>
    [HttpGet("champions-league/phase/{phaseId}")]
    public async Task<IActionResult> GetChampionsLeague(int phaseId)
    {
        try
        {
            var matches = await _db.Matches
                .Where(m => 
                    m.TournamentId == TournamentsInfo.ChampionsLeague.TournamentId && 
                    m.SeasonId == TournamentsInfo.ChampionsLeague.SeasonId && 
                    m.Round == phaseId)
                .OrderBy(m => m.StartTimestamp)
                .ToListAsync();

            if (!matches.Any())
            {
                return Ok(new
                {
                    message = $"Nenhum jogo encontrado para Champions League - Fase {phaseId}.",
                    tournament = TournamentsInfo.ChampionsLeague.Name,
                    phase = phaseId,
                    matches = Array.Empty<MatchResponse>()
                });
            }

            var response = matches.Select(m => m.ToResponse(TournamentsInfo.ChampionsLeague.Name)).ToList();
            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Retorna jogos das rodadas de qualificação da Champions League (rodadas 1, 2 ou 3).
    /// </summary>
    [HttpGet("champions-league/qualification/{round}")]
    public async Task<IActionResult> GetChampionsLeagueQualification(int round)
    {
        if (round < 1 || round > 3)
            return BadRequest("A rodada de qualificação deve ser 1, 2 ou 3.");

        try 
        {
            var matches = await _db.Matches
                .Where(m => 
                    m.TournamentId == TournamentsInfo.ChampionsLeague.TournamentId && 
                    m.SeasonId == TournamentsInfo.ChampionsLeague.SeasonId && 
                    m.Round == round)
                .OrderBy(m => m.StartTimestamp)
                .ToListAsync();

            if (!matches.Any())
            {
                return Ok(new
                {
                    message = $"Nenhum jogo encontrado para Champions League - Qualificação Rodada {round}.",
                    tournament = TournamentsInfo.ChampionsLeague.Name,
                    round = round,
                    matches = Array.Empty<MatchResponse>()
                });
            }

            var response = matches.Select(m => m.ToResponse(TournamentsInfo.ChampionsLeague.Name)).ToList();
            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Retorna jogos das fases eliminatórias (Mata-Mata).
    /// Para as Oitavas (Round 5), valida dinamicamente se os Playoffs (Round 636) já terminaram.
    /// </summary>
    [HttpGet("champions-league/playoff/{roundId}")]
    public async Task<IActionResult> GetChampionsLeagueKnockout(int roundId)
    {
        try
        {
            var validKnockoutIds = new[] { 636, 5, 27, 28, 29 };
            
            if (!validKnockoutIds.Contains(roundId))
            {
                return BadRequest(new { message = "ID de rodada inválido para fase eliminatória." });
            }

            // Lógica Especial para Ambiguidade do Round 5 (Oitavas vs Liga)
            if (roundId == 5)
            {
                // 1. Busca os jogos dos Playoffs (Round 636) para usar como referência
                var playoffMatches = await _db.Matches
                    .Where(m => 
                        m.TournamentId == TournamentsInfo.ChampionsLeague.TournamentId && 
                        m.SeasonId == TournamentsInfo.ChampionsLeague.SeasonId && 
                        m.Round == 636)
                    .ToListAsync();

                // 2. Valida se os Playoffs já foram FINALIZADOS
                // Consideramos finalizado se existem jogos E todos estão com status de encerrado
                // Adicione outros status de encerramento se necessário (ex: "Finished", "AET", "AP")
                bool isPlayoffFinished = playoffMatches.Any() && 
                                         playoffMatches.All(m => m.Status == "Ended" || m.Status == "Finished");

                if (!isPlayoffFinished)
                {
                    // Se o playoff não acabou, as Oitavas ainda não estão definidas (ou não deveriam ser mostradas)
                    return Ok(new
                    {
                        message = "As Oitavas de Final serão definidas após o término da fase de Playoffs.",
                        tournament = TournamentsInfo.ChampionsLeague.Name,
                        round = roundId,
                        matches = Array.Empty<MatchResponse>()
                    });
                }

                // 3. Pega a data do último jogo do Playoff
                var lastPlayoffDate = playoffMatches.Max(m => m.StartTimestamp);

                // 4. Busca jogos do Round 5 que acontecem DEPOIS do último jogo do playoff
                // Isso filtra automaticamente os jogos da Fase de Liga (Round 5) que ocorreram meses antes
                var matchesR16 = await _db.Matches
                    .Where(m => 
                        m.TournamentId == TournamentsInfo.ChampionsLeague.TournamentId && 
                        m.SeasonId == TournamentsInfo.ChampionsLeague.SeasonId && 
                        m.Round == roundId &&
                        m.StartTimestamp > lastPlayoffDate)
                    .OrderBy(m => m.StartTimestamp)
                    .ToListAsync();

                if (!matchesR16.Any())
                {
                    return Ok(new
                    {
                        message = "Confrontos das Oitavas de Final ainda não definidos.",
                        tournament = TournamentsInfo.ChampionsLeague.Name,
                        round = roundId,
                        matches = Array.Empty<MatchResponse>()
                    });
                }

                return Ok(matchesR16.Select(m => m.ToResponse(TournamentsInfo.ChampionsLeague.Name)));
            }

            // --- Lógica Padrão para Outras Fases (636, 27, 28, 29) ---
            // Essas fases não possuem conflito de ID com a fase de liga
            var matches = await _db.Matches
                .Where(m => 
                    m.TournamentId == TournamentsInfo.ChampionsLeague.TournamentId && 
                    m.SeasonId == TournamentsInfo.ChampionsLeague.SeasonId && 
                    m.Round == roundId)
                .OrderBy(m => m.StartTimestamp)
                .ToListAsync();

            if (!matches.Any())
            {
                var phaseName = TournamentsInfo.ChampionsLeague.KnockoutPhases
                    .FirstOrDefault(p => p.RoundId == roundId)?.Name ?? "Fase Eliminatória";

                return Ok(new
                {
                    message = $"Nenhum jogo encontrado para {phaseName}.",
                    tournament = TournamentsInfo.ChampionsLeague.Name,
                    round = roundId,
                    matches = Array.Empty<MatchResponse>()
                });
            }

            return Ok(matches.Select(m => m.ToResponse(TournamentsInfo.ChampionsLeague.Name)));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
    /// <summary>
    /// Retorna detalhes completos de uma partida (stats + incidents).
    /// Apenas dados do banco - não faz scraping.
    /// </summary>
    [HttpGet("{matchId}/details")]
    public async Task<IActionResult> GetDetails(int matchId)
    {
        try
        {
            var match = await _db.Matches
                .Include(m => m.Stats)
                .Include(m => m.Incidents)
                .FirstOrDefaultAsync(m => m.Id == matchId);

            if (match == null)
            {
                return NotFound(new 
                { 
                    error = $"Partida {matchId} não encontrada no banco de dados.",
                    message = "Aguarde o Worker processar esta partida ou verifique se o ID está correto."
                });
            }

            // Se o jogo não foi enriquecido ainda, avisa o frontend
            if (match.ProcessingStatus != MatchProcessingStatus.Enriched)
            {
                return Ok(new
                {
                    match = match.ToDetailResponse(),
                    warning = $"Dados parciais. Status de processamento: {match.ProcessingStatus}",
                    isPartial = true
                });
            }

            var response = match.ToDetailResponse();
            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Retorna a classificação atual de um campeonato.
    /// Os dados são mantidos pelo Worker e atualizados automaticamente
    /// quando uma partida do campeonato é enriquecida.
    /// </summary>
    [HttpGet("tournament/{tournamentName}/standings")]
    public async Task<IActionResult> GetStandings(string tournamentName)
    {
        try
        {
            var info = TournamentsInfo.GetTournamentInfo(tournamentName);

            var standings = await _db.Standings
                .Include(s => s.Promotions)
                .Where(s => s.TournamentId == info.tournamentId && s.SeasonId == info.seasonId)
                .OrderBy(s => s.Position)
                .ToListAsync();

            if (!standings.Any())
            {
                return Ok(new
                {
                    tournament = info.name,
                    message = "Classificação ainda não foi sincronizada. Será atualizada após o primeiro jogo ser enriquecido.",
                    rows = Array.Empty<object>()
                });
            }

            // Monta resposta no mesmo formato que o frontend já espera
            var response = standings.Select(s => new
            {
                Team = new { Id = s.TeamId, Name = s.TeamName },
                Position = s.Position,
                Matches = s.Matches,
                Wins = s.Wins,
                Draws = s.Draws,
                Losses = s.Losses,
                ScoresFor = s.GoalsFor,
                ScoresAgainst = s.GoalsAgainst,
                Points = s.Points,
                ScoreDiffFormatted = (s.GoalDifference >= 0 ? "+" : "") + s.GoalDifference.ToString(),
                Promotion = s.Promotions.Any()
                    ? new { Id = s.Promotions.First().PromotionId, Text = s.Promotions.First().Text }
                    : null,
                UpdatedAt = s.UpdatedAt
            });

            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// [ADMIN] Popula inicialmente a tabela Standings para todos os torneios configurados.
    /// Este endpoint deve ser executado apenas localmente via Swagger.
    /// </summary>
    [HttpPost("admin/sync-all-standings")]
    public async Task<IActionResult> SyncAllStandings()
    {
        try
        {
            var results = new List<object>();

            foreach (var tournament in TournamentsInfo.AllTournaments.List)
            {
                // Verifica se já existem dados para este torneio
                var existingCount = await _db.Standings
                    .CountAsync(s => s.TournamentId == tournament.tournamentId 
                                && s.SeasonId == tournament.seasonId);

                if (existingCount > 0)
                {
                    results.Add(new
                    {
                        Tournament = tournament.name,
                        Status = "Skipped",
                        Message = $"Já possui {existingCount} registros",
                        TournamentId = tournament.tournamentId,
                        SeasonId = tournament.seasonId
                    });
                    continue;
                }

                // Busca dados via scraping
                var standingsTable = await _scraper.GetStandingsAsync(
                    tournament.tournamentId, 
                    tournament.seasonId
                );

                if (standingsTable?.Rows == null || !standingsTable.Rows.Any())
                {
                    results.Add(new
                    {
                        Tournament = tournament.name,
                        Status = "Error",
                        Message = "Nenhum dado retornado pelo scraper",
                        TournamentId = tournament.tournamentId,
                        SeasonId = tournament.seasonId
                    });
                    continue;
                }

                // Insere na tabela Standings
                foreach (var row in standingsTable.Rows)
                {
                    var standing = new DbStanding
                    {
                        TournamentId = tournament.tournamentId,
                        SeasonId = tournament.seasonId,
                        TeamId = row.Team.Id,
                        TeamName = row.Team.Name,
                        Position = row.Position,
                        Matches = row.Matches,
                        Wins = row.Wins,
                        Draws = row.Draws,
                        Losses = row.Losses,
                        GoalsFor = row.ScoresFor,
                        GoalsAgainst = row.ScoresAgainst,
                        GoalDifference = row.ScoresFor - row.ScoresAgainst,
                        Points = row.Points,
                        UpdatedAt = DateTime.UtcNow
                    };

                    _db.Standings.Add(standing);

                    // Adiciona promoções/rebaixamentos
                    if (row.Promotion != null)
                    {
                        var promotion = new DbStandingPromotion
                        {
                            Standing = standing,
                            PromotionId = row.Promotion.Id,
                            Text = row.Promotion.Text
                        };
                        _db.StandingPromotions.Add(promotion);
                    }
                }

                await _db.SaveChangesAsync();

                results.Add(new
                {
                    Tournament = tournament.name,
                    Status = "Success",
                    Message = $"{standingsTable.Rows.Count} times inseridos",
                    TournamentId = tournament.tournamentId,
                    SeasonId = tournament.seasonId
                });

                // Delay para não sobrecarregar o scraper
                await Task.Delay(1000);
            }

            return Ok(new
            {
                Message = "Sincronização completa",
                Timestamp = DateTime.UtcNow,
                Results = results
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex}", "Erro ao sincronizar standings");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
