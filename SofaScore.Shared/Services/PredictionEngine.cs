using Microsoft.Extensions.Logging;
using SofaScoreScraper;

namespace SofaScore.Shared.Services;

/// <summary>
/// Motor de predição - gera probabilidades baseadas em análises de forma
/// </summary>
public class PredictionEngine
{
    private readonly ILogger<PredictionEngine> _logger;

    public PredictionEngine(ILogger<PredictionEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gera predições completas para uma partida
    /// </summary>
    public PredictionResults GeneratePredictions(
        TeamFormAnalysis homeAnalysis,
        TeamFormAnalysis awayAnalysis)
    {
        _logger.LogInformation("Gerando predições baseadas nas análises de forma");

        return new PredictionResults
        {
            Result = PredictResult(homeAnalysis, awayAnalysis),
            Goals = PredictGoals(homeAnalysis, awayAnalysis),
            FirstGoal = PredictFirstGoal(homeAnalysis, awayAnalysis),
            HalfTime = PredictHalfTime(homeAnalysis, awayAnalysis),
            Cards = PredictCards(homeAnalysis, awayAnalysis) // ✅ NOVO
        };
    }

    /// <summary>
    /// Prediz resultado (Vitória Casa / Empate / Vitória Fora)
    /// </summary>
    private ResultPrediction PredictResult(
        TeamFormAnalysis home,
        TeamFormAnalysis away)
    {
        int homeScore = 33; // Base: 33% cada
        int awayScore = 33;

        // 1. Ataque mandante vs Defesa visitante
        if (home.Offensive.AvgGoalsScored > away.Defensive.AvgGoalsConceded)
            homeScore += 12;
        else if (home.Offensive.AvgGoalsScored < away.Defensive.AvgGoalsConceded)
            awayScore += 8;

        // 2. Defesa mandante vs Ataque visitante
        if (home.Defensive.AvgGoalsConceded < away.Offensive.AvgGoalsScored)
            homeScore += 10;
        else if (home.Defensive.AvgGoalsConceded > away.Offensive.AvgGoalsScored)
            awayScore += 10;

        // 3. Fator casa (mandante tem vantagem estatística)
        homeScore += 8;

        // 4. Forma recente (% de vitórias)
        if (home.Results.WinPercentage > away.Results.WinPercentage)
            homeScore += 10;
        else if (home.Results.WinPercentage < away.Results.WinPercentage)
            awayScore += 8;

        // 5. Clean sheets (solidez defensiva)
        double homeCleanSheetRate = home.GamesAnalyzed > 0 
            ? home.Defensive.CleanSheets / (double)home.GamesAnalyzed 
            : 0;
        double awayCleanSheetRate = away.GamesAnalyzed > 0 
            ? away.Defensive.CleanSheets / (double)away.GamesAnalyzed 
            : 0;

        if (homeCleanSheetRate > 0.4) homeScore += 5;
        if (awayCleanSheetRate > 0.4) awayScore += 5;

        // Normalizar para 100%
        int total = homeScore + awayScore + 34; // 34 = empate base
        int drawScore = 100 - homeScore - awayScore;

        // Caps para evitar valores extremos
        homeScore = Math.Min(homeScore, 70);
        awayScore = Math.Min(awayScore, 70);
        drawScore = Math.Max(drawScore, 10);

        // Normalização final
        total = homeScore + drawScore + awayScore;
        homeScore = (int)((homeScore / (double)total) * 100);
        awayScore = (int)((awayScore / (double)total) * 100);
        drawScore = 100 - homeScore - awayScore;

        _logger.LogDebug(
            "Predição resultado: Casa {Home}% | Empate {Draw}% | Fora {Away}%",
            homeScore, drawScore, awayScore
        );

        return new ResultPrediction
        {
            HomeWin = homeScore,
            Draw = drawScore,
            AwayWin = awayScore
        };
    }

    /// <summary>
    /// Prediz cenários de gols (Over/Under 2.5, BTTS)
    /// </summary>
    private GoalsPrediction PredictGoals(
        TeamFormAnalysis home,
        TeamFormAnalysis away)
    {
        // Média combinada de gols
        double avgTotalGoals = home.Offensive.AvgGoalsScored + 
                               away.Offensive.AvgGoalsScored;

        // Over 2.5 baseado na média
        int over25 = avgTotalGoals switch
        {
            > 3.5 => 75,
            > 3.0 => 65,
            > 2.5 => 55,
            > 2.0 => 45,
            _ => 35
        };

        // BTTS (Both Teams To Score)
        double homeScoreRate = home.GamesAnalyzed > 0 
            ? home.Offensive.AvgGoalsScored 
            : 0;
        double awayScoreRate = away.GamesAnalyzed > 0 
            ? away.Offensive.AvgGoalsScored 
            : 0;

        int btts = 50; // Base

        if (homeScoreRate >= 1.5 && awayScoreRate >= 1.0)
            btts += 15;
        if (homeScoreRate >= 1.0 && awayScoreRate >= 1.0)
            btts += 10;

        // Clean sheets reduzem BTTS
        double homeCleanSheetRate = home.GamesAnalyzed > 0
            ? home.Defensive.CleanSheets / (double)home.GamesAnalyzed
            : 0;
        double awayCleanSheetRate = away.GamesAnalyzed > 0
            ? away.Defensive.CleanSheets / (double)away.GamesAnalyzed
            : 0;

        if (homeCleanSheetRate > 0.5) btts -= 10;
        if (awayCleanSheetRate > 0.5) btts -= 10;

        btts = Math.Clamp(btts, 20, 80);

        _logger.LogDebug(
            "Predição gols: Over 2.5 = {Over}% | BTTS = {Btts}%",
            over25, btts
        );

        return new GoalsPrediction
        {
            Over25 = over25,
            Under25 = 100 - over25,
            Btts = btts
        };
    }

    /// <summary>
    /// Prediz quem marca o primeiro gol
    /// </summary>
    private FirstGoalPrediction PredictFirstGoal(
        TeamFormAnalysis home,
        TeamFormAnalysis away)
    {
        // Taxa de marcar primeiro
        double homeFirstRate = home.GamesAnalyzed > 0
            ? home.Offensive.ScoredFirstCount / (double)home.GamesAnalyzed
            : 0;

        // Taxa de sofrer primeiro (visitante)
        double awayConcededFirstRate = away.GamesAnalyzed > 0
            ? away.Defensive.ConcededFirstCount / (double)away.GamesAnalyzed
            : 0;

        // Média ponderada (60% histórico do mandante, 40% do visitante)
        double homeProbability = (homeFirstRate * 0.6) + (awayConcededFirstRate * 0.4);

        // Adiciona fator casa
        homeProbability += 0.1;

        // Limita entre 30% e 80%
        homeProbability = Math.Clamp(homeProbability, 0.30, 0.80);

        int homePercent = (int)(homeProbability * 100);
        int awayPercent = 100 - homePercent;

        string reasoning = $"Mandante marcou primeiro em {home.Offensive.ScoredFirstCount}/{home.GamesAnalyzed} jogos em casa";
        
        if (away.Defensive.ConcededFirstCount > 0)
        {
            reasoning += $", visitante sofreu primeiro em {away.Defensive.ConcededFirstCount}/{away.GamesAnalyzed} jogos fora";
        }

        _logger.LogDebug(
            "Predição primeiro gol: Casa {Home}% | Fora {Away}%",
            homePercent, awayPercent
        );

        return new FirstGoalPrediction
        {
            HomeTeam = homePercent,
            AwayTeam = awayPercent,
            Reasoning = reasoning
        };
    }

    /// <summary>
    /// Prediz cenário no intervalo
    /// </summary>
    private HalfTimePrediction PredictHalfTime(
        TeamFormAnalysis home,
        TeamFormAnalysis away)
    {
        // Gols no primeiro tempo
        double homeFirstHalfRate = home.GamesAnalyzed > 0
            ? home.Offensive.GoalsFirstHalf / (double)home.GamesAnalyzed
            : 0;

        double awayFirstHalfRate = away.GamesAnalyzed > 0
            ? away.Offensive.GoalsFirstHalf / (double)away.GamesAnalyzed
            : 0;

        int homeLeading = 35;
        int draw = 40; // Empate no intervalo é comum
        int awayLeading = 25;

        // Ajusta baseado em força no 1º tempo
        if (homeFirstHalfRate > awayFirstHalfRate)
        {
            homeLeading += 15;
            draw -= 10;
        }
        else if (awayFirstHalfRate > homeFirstHalfRate)
        {
            awayLeading += 15;
            draw -= 10;
        }

        // Fator casa
        homeLeading += 5;
        awayLeading -= 5;

        // Normalizar
        int total = homeLeading + draw + awayLeading;
        homeLeading = (int)((homeLeading / (double)total) * 100);
        awayLeading = (int)((awayLeading / (double)total) * 100);
        draw = 100 - homeLeading - awayLeading;

        _logger.LogDebug(
            "Predição intervalo: Casa {Home}% | Empate {Draw}% | Fora {Away}%",
            homeLeading, draw, awayLeading
        );

        return new HalfTimePrediction
        {
            HomeLeading = homeLeading,
            Draw = draw,
            AwayLeading = awayLeading
        };
    }

    /// <summary>
    /// ✅ NOVO: Prediz cartões na partida
    /// </summary>
    private CardsPrediction PredictCards(
        TeamFormAnalysis home,
        TeamFormAnalysis away)
    {
        // Média combinada de cartões
        double expectedCards = home.Discipline.AvgTotalCards + away.Discipline.AvgTotalCards;

        // Probabilidade de over 3.5 cartões
        int over35 = expectedCards switch
        {
            > 5.0 => 75,
            > 4.5 => 65,
            > 4.0 => 55,
            > 3.5 => 50,
            _ => 35
        };

        // Determina time mais disciplinado
        string mostDisciplined = "Empate";
        if (home.Discipline.AvgTotalCards < away.Discipline.AvgTotalCards - 0.5)
            mostDisciplined = "Mandante";
        else if (away.Discipline.AvgTotalCards < home.Discipline.AvgTotalCards - 0.5)
            mostDisciplined = "Visitante";

        _logger.LogDebug(
            "Predição cartões: Esperado {Expected} | Over 3.5 = {Over}% | Mais disciplinado: {Team}",
            expectedCards.ToString("F1"), over35, mostDisciplined
        );

        return new CardsPrediction
        {
            ExpectedTotalCards = Math.Round(expectedCards, 1),
            Over35Cards = over35,
            Under35Cards = 100 - over35,
            MostDisciplinedTeam = mostDisciplined
        };
    }
}