using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SofaScoreScraper;

namespace SofaScore.Shared.Services;

/// <summary>
/// Serviço principal de predição de partidas
/// </summary>
public class MatchPredictionService
{
    private readonly MatchDataFetcher _dataFetcher;
    private readonly StatsAnalyzer _statsAnalyzer;
    private readonly PredictionEngine _predictionEngine;
    private readonly ILogger<MatchPredictionService> _logger;
    private readonly PredictionSettings _settings;

    public MatchPredictionService(
        MatchDataFetcher dataFetcher,
        StatsAnalyzer statsAnalyzer,
        PredictionEngine predictionEngine,
        ILogger<MatchPredictionService> logger,
        IOptions<PredictionSettings> settings)
    {
        _dataFetcher = dataFetcher;
        _statsAnalyzer = statsAnalyzer;
        _predictionEngine = predictionEngine;
        _logger = logger;
        _settings = settings.Value;
    }

    /// <summary>
    /// Gera predição completa para uma partida
    /// </summary>
    public async Task<MatchPrediction?> GeneratePredictionAsync(int matchId)
    {
        _logger.LogInformation("Gerando predição para partida {MatchId}", matchId);

        // 1. Buscar dados da partida
        var match = await _dataFetcher.GetMatchByIdAsync(matchId);
        if (match == null)
        {
            _logger.LogWarning("Partida {MatchId} não encontrada", matchId);
            return null;
        }

        // 2. Verificar se pode ser analisada
        if (!await _dataFetcher.CanAnalyzeMatchAsync(matchId))
        {
            _logger.LogWarning("Partida {MatchId} não pode ser analisada", matchId);
            return null;
        }

        // 3. Buscar histórico do mandante
        var homeMatches = await _dataFetcher.GetHomeMatchesAsync(
            match.HomeTeam,
            match.TournamentId,
            match.SeasonId,
            match.StartTimestamp,
            _settings.MinGamesForAnalysis
        );

        // 4. Buscar histórico do visitante
        var awayMatches = await _dataFetcher.GetAwayMatchesAsync(
            match.AwayTeam,
            match.TournamentId,
            match.SeasonId,
            match.StartTimestamp,
            _settings.MinGamesForAnalysis
        );

        // 5. Analisar forma dos times
        var homeAnalysis = _statsAnalyzer.AnalyzeHomeForm(homeMatches, match.HomeTeam);
        var awayAnalysis = _statsAnalyzer.AnalyzeAwayForm(awayMatches, match.AwayTeam);

        // 6. Determinar confiança e warnings
        var confidence = DetermineConfidence(homeMatches.Count, awayMatches.Count);
        var warnings = GenerateWarnings(homeMatches.Count, awayMatches.Count);

        // 7. Gerar predições
        var predictions = _predictionEngine.GeneratePredictions(homeAnalysis, awayAnalysis);

        // 8. Montar resposta
        var result = new MatchPrediction
        {
            Match = new MatchInfo
            {
                Id = match.Id,
                HomeTeam = match.HomeTeam,
                AwayTeam = match.AwayTeam,
                Tournament = GetTournamentName(match.TournamentId),
                Round = match.Round,
                DateTime = DateTimeOffset.FromUnixTimeSeconds(match.StartTimestamp).DateTime
            },
            HomeTeamAnalysis = homeAnalysis,
            AwayTeamAnalysis = awayAnalysis,
            Predictions = predictions,
            Confidence = confidence,
            Warnings = warnings
        };

        _logger.LogInformation(
            "Predição gerada para {Home} vs {Away}: Confiança {Confidence}",
            match.HomeTeam, match.AwayTeam, confidence
        );

        return result;
    }

    /// <summary>
    /// Determina o nível de confiança baseado na quantidade de dados
    /// </summary>
    private string DetermineConfidence(int homeGames, int awayGames)
    {
        int minGames = Math.Min(homeGames, awayGames);

        if (minGames >= _settings.ConfidenceLevels.High)
            return "High";
        
        if (minGames >= _settings.ConfidenceLevels.Medium)
            return "Medium";
        
        return "Low";
    }

    /// <summary>
    /// Gera avisos sobre limitações dos dados
    /// </summary>
    private List<string> GenerateWarnings(int homeGames, int awayGames)
    {
        var warnings = new List<string>();
        int targetGames = _settings.MinGamesForAnalysis;

        if (homeGames < targetGames)
        {
            warnings.Add($"Dados limitados para mandante ({homeGames}/{targetGames} jogos)");
        }

        if (awayGames < targetGames)
        {
            warnings.Add($"Dados limitados para visitante ({awayGames}/{targetGames} jogos)");
        }

        if (homeGames < _settings.ConfidenceLevels.Medium || 
            awayGames < _settings.ConfidenceLevels.Medium)
        {
            warnings.Add("⚠️ Predição com baixa confiabilidade devido à falta de histórico suficiente");
        }

        return warnings;
    }

    /// <summary>
    /// Retorna nome do torneio baseado no ID
    /// </summary>
    private string GetTournamentName(int tournamentId)
    {
        return tournamentId switch
        {
            17 => "Premier League",
            8 => "La Liga",
            23 => "Serie A",
            34 => "Ligue 1",
            35 => "Bundesliga",
            325 => "Brasileirão",
            7 => "Champions League",
            _ => "Unknown"
        };
    }
}

/// <summary>
/// Configurações de predição (vindas do appsettings.json)
/// </summary>
public class PredictionSettings
{
    public int MinGamesForAnalysis { get; set; } = 5;
    public int MinGamesForReliability { get; set; } = 3;
    public ConfidenceLevelsSettings ConfidenceLevels { get; set; } = new();
}

public class ConfidenceLevelsSettings
{
    public int High { get; set; } = 5;
    public int Medium { get; set; } = 3;
    public int Low { get; set; } = 1;
}