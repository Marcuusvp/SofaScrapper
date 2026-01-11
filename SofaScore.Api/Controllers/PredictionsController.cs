using Microsoft.AspNetCore.Mvc;
using SofaScore.Api.Services;

namespace SofaScore.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PredictionsController : ControllerBase
{
    private readonly MatchPredictionService _predictionService;
    private readonly ILogger<PredictionsController> _logger;

    public PredictionsController(
        MatchPredictionService predictionService,
        ILogger<PredictionsController> logger)
    {
        _predictionService = predictionService;
        _logger = logger;
    }

    /// <summary>
    /// Gera predição para uma partida específica
    /// </summary>
    /// <param name="matchId">ID da partida</param>
    /// <returns>Predição completa com análises e probabilidades</returns>
    /// <response code="200">Predição gerada com sucesso</response>
    /// <response code="404">Partida não encontrada</response>
    /// <response code="400">Partida não pode ser analisada (ex: jogo em andamento)</response>
    /// <response code="500">Erro interno no servidor</response>
    [HttpGet("match/{matchId}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetMatchPrediction(int matchId)
    {
        try
        {
            _logger.LogInformation("Requisição de predição para partida {MatchId}", matchId);

            var prediction = await _predictionService.GeneratePredictionAsync(matchId);

            if (prediction == null)
            {
                return NotFound(new
                {
                    error = "Partida não encontrada ou não pode ser analisada",
                    matchId
                });
            }

            return Ok(prediction);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Erro de validação ao gerar predição para {MatchId}", matchId);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao gerar predição para partida {MatchId}", matchId);
            return StatusCode(500, new { error = "Erro interno ao processar predição" });
        }
    }

    /// <summary>
    /// Retorna informações sobre as configurações de predição
    /// </summary>
    /// <returns>Configurações atuais do sistema de predição</returns>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult GetSettings()
    {
        return Ok(new
        {
            description = "Sistema de predição de partidas baseado em forma recente",
            minGamesAnalyzed = 5,
            minGamesForReliability = 3,
            metrics = new[]
            {
                "Média de gols marcados/sofridos",
                "Finalizações no gol",
                "Gols por tempo (1º e 2º)",
                "Frequência de marcar/sofrer primeiro",
                "Clean sheets",
                "Resultados (vitórias/empates/derrotas)"
            },
            confidenceLevels = new
            {
                high = "5+ jogos disponíveis para análise",
                medium = "3-4 jogos disponíveis",
                low = "1-2 jogos disponíveis"
            }
        });
    }
}