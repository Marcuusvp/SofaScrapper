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
    public async Task<IActionResult> GetDetails(int matchId)
    {
        try
        {
            var data = await _dataManager.GetMatchFullDataAsync(matchId);
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

    [HttpGet("tournament/{tournamentName}/standings")]
    public async Task<IActionResult> GetStandings(string tournamentName)
    {
        try 
        {
            var info = TournamentsInfo.GetTournamentInfo(tournamentName);
            var standings = await _scraper.GetStandingsAsync(info.tournamentId, info.seasonId);
            return Ok(standings);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}