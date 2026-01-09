using Microsoft.AspNetCore.Mvc;
using SofaScore.Api.Services;
using SofaScoreScraper;

namespace SofaScore.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MatchesController : ControllerBase
{
    private readonly SofaScraper _scraper;
    private readonly DataManager _dataManager;

    public MatchesController(SofaScraper scraper, DataManager dataManager)
    {
        _scraper = scraper;
        _dataManager = dataManager;
    }

    [HttpGet("live")]
    public async Task<IActionResult> GetLive()
    {
        var matches = await _scraper.GetLiveMatchesAsync();
        return Ok(matches);
    }

    [HttpGet("tournament/{tournamentName}/round/{round}")]
    public async Task<IActionResult> GetByRound(string tournamentName, int round)
    {
        try 
        {
            var info = TournamentsInfo.GetTournamentInfo(tournamentName);
            var matches = await _scraper.GetMatchesAsync(info.tournamentId, info.seasonId, round);
            return Ok(matches);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // Endpoint específico para Champions League (devido à lógica complexa de fases)
    [HttpGet("champions-league/phase/{phaseId}")]
    public async Task<IActionResult> GetChampionsLeague(int phaseId)
    {
        // 1-8: Liga, 9+: Mata-mata
        var matches = await _scraper.GetMatchesAsync(TournamentsInfo.ChampionsLeague.TournamentId, TournamentsInfo.ChampionsLeague.SeasonId, phaseId);
        return Ok(matches);
    }

    [HttpGet("{matchId}/details")]
    public async Task<IActionResult> GetDetails(int matchId, [FromQuery] int? seasonId = null)
    {
        try
        {
            var data = await _dataManager.GetMatchFullDataAsync(matchId, seasonId);
            return Ok(data);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
    // GET: api/Matches/champions-league/qualification/2
    // Para as rodadas 1, 2 e 3 da qualificação
    [HttpGet("champions-league/qualification/{round}")]
    public async Task<IActionResult> GetChampionsLeagueQualification(int round)
    {
        if (round < 1 || round > 3)
            return BadRequest("A rodada de qualificação deve ser 1, 2 ou 3.");

        try 
        {
            // Constrói o slug baseado na rodada (ex: qualification-round-2)
            string slug = $"qualification-round-{round}";
            
            var matches = await _scraper.GetQualificationMatchesAsync(
                TournamentsInfo.ChampionsLeague.TournamentId,
                TournamentsInfo.ChampionsLeague.SeasonId,
                round,
                slug,
                null // Prefixo é null para estas rodadas
            );

            return Ok(matches);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // GET: api/Matches/champions-league/playoff
    // Para o Playoff específico de qualificação (aquele ID 636)
    [HttpGet("champions-league/playoff")]
    public async Task<IActionResult> GetChampionsLeaguePlayoff()
    {
        try
        {
            // Dados fixos conforme descoberto no Console App
            int roundId = 636;
            string slug = "playoff-round";
            string prefix = "Qualification";

            var matches = await _scraper.GetQualificationMatchesAsync(
                TournamentsInfo.ChampionsLeague.TournamentId,
                TournamentsInfo.ChampionsLeague.SeasonId,
                roundId,
                slug,
                prefix
            );

            return Ok(matches);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}