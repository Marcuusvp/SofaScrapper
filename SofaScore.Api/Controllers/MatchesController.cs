using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SofaScore.Api.DTOs;
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
    private readonly ILogger<MatchesController> _logger;

    public MatchesController(SofaScraper scraper, DataManager dataManager, AppDbContext db, ILogger<MatchesController> logger)
    {
        _scraper = scraper;
        _dataManager = dataManager;
        _db = db;
        _logger = logger;
    }

    [HttpGet("live")]
    public async Task<IActionResult> GetLive()
    {
        // Define uma janela de segurança: Jogos que começaram nas últimas 5 horas.
        // Jogos mais antigos que isso com status "Ao Vivo" são considerados lixo/erro.
        var safetyCutoff = DateTimeOffset.UtcNow.AddHours(-5).ToUnixTimeSeconds();

        var liveMatches = await _db.Matches
            .Where(m => 
                // 1. Deve ter começado recentemente
                m.StartTimestamp >= safetyCutoff && 
                
                // 2. E ter status de jogo em andamento
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

        return Ok(liveMatches);
    }

    /// <summary>
    /// Retorna jogos de uma rodada específica de um campeonato
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
                _logger.LogWarning(
                    "Nenhum jogo encontrado para {Tournament} rodada {Round}", 
                    tournamentName, round
                );
                return Ok(new List<MatchResponse>());
            }

            var response = matches.Select(MatchMapper.ToMatchResponse).ToList();
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Retorna jogos de uma fase da Champions League
    /// </summary>
    [HttpGet("champions-league/phase/{phaseId}")]
    public async Task<IActionResult> GetChampionsLeague(int phaseId)
    {
        var matches = await _db.Matches
            .Where(m => 
                m.TournamentId == TournamentsInfo.ChampionsLeague.TournamentId &&
                m.SeasonId == TournamentsInfo.ChampionsLeague.SeasonId &&
                m.Round == phaseId)
            .OrderBy(m => m.StartTimestamp)
            .ToListAsync();

        var response = matches.Select(MatchMapper.ToMatchResponse).ToList();
        return Ok(response);
    }

    /// <summary>
    /// Retorna detalhes completos de uma partida específica
    /// Inclui estatísticas e incidentes
    /// </summary>
    [HttpGet("{matchId}/details")]
    public async Task<IActionResult> GetDetails(int matchId)
    {
        var match = await _db.Matches
            .Include(m => m.Stats)
            .Include(m => m.Incidents)
            .FirstOrDefaultAsync(m => m.Id == matchId);

        if (match == null)
        {
            _logger.LogWarning("Partida {MatchId} não encontrada", matchId);
            return NotFound(new { error = "Partida não encontrada" });
        }

        // Se a partida ainda não foi enriquecida, retorna com dados básicos
        if (match.ProcessingStatus != MatchProcessingStatus.Enriched)
        {
            _logger.LogInformation(
                "Partida {MatchId} ainda não foi enriquecida (Status: {Status})",
                matchId, match.ProcessingStatus
            );
        }

        var response = MatchMapper.ToMatchDetailResponse(match);
        return Ok(response);
    }
    /// <summary>
    /// Retorna jogos das rodadas de qualificação da Champions League (1, 2 ou 3)
    /// </summary>
    [HttpGet("champions-league/qualification/{round}")]
    public async Task<IActionResult> GetChampionsLeagueQualification(int round)
    {
        if (round < 1 || round > 3)
            return BadRequest("A rodada de qualificação deve ser 1, 2 ou 3.");

        var matches = await _db.Matches
            .Where(m => 
                m.TournamentId == TournamentsInfo.ChampionsLeague.TournamentId &&
                m.SeasonId == TournamentsInfo.ChampionsLeague.SeasonId &&
                m.Round == round)
            .OrderBy(m => m.StartTimestamp)
            .ToListAsync();

        var response = matches.Select(MatchMapper.ToMatchResponse).ToList();
        return Ok(response);
    }

    /// <summary>
    /// Retorna jogos do playoff de qualificação da Champions League
    /// </summary>
    [HttpGet("champions-league/playoff")]
    public async Task<IActionResult> GetChampionsLeaguePlayoff()
    {
        // Round 636 é o ID específico do playoff conforme identificado anteriormente
        const int playoffRoundId = 636;

        var matches = await _db.Matches
            .Where(m => 
                m.TournamentId == TournamentsInfo.ChampionsLeague.TournamentId &&
                m.SeasonId == TournamentsInfo.ChampionsLeague.SeasonId &&
                m.Round == playoffRoundId)
            .OrderBy(m => m.StartTimestamp)
            .ToListAsync();

        var response = matches.Select(MatchMapper.ToMatchResponse).ToList();
        return Ok(response);
    }

    /// <summary>
    /// Retorna a classificação atual de um campeonato.
    /// Os dados são mantidos pelo Worker e atualizados automaticamente
    /// quando uma partida do campeonato é enriquecida.
    /// </summary>
    /// <param name="tournamentName">Nome do campeonato (ex: premierleague, laliga, seriea)</param>
    /// <returns>Classificação com todas as linhas e informações de promoção</returns>
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