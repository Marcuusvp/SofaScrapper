using SofaScore.Api.Data;
using SofaScoreScraper;

namespace SofaScore.Api.Services;

/// <summary>
/// Analisa estatísticas de partidas e calcula métricas
/// </summary>
public class StatsAnalyzer
{
    private readonly ILogger<StatsAnalyzer> _logger;

    public StatsAnalyzer(ILogger<StatsAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analisa forma de um time como mandante
    /// </summary>
    public TeamFormAnalysis AnalyzeHomeForm(List<DbMatch> matches, string teamName)
    {
        if (!matches.Any())
        {
            _logger.LogWarning("Nenhum jogo encontrado para análise de {Team} como mandante", teamName);
            return new TeamFormAnalysis { Context = "Sem dados disponíveis" };
        }

        var analysis = new TeamFormAnalysis
        {
            GamesAnalyzed = matches.Count,
            Context = $"Últimos {matches.Count} jogos como mandante"
        };

        // Métricas Ofensivas
        analysis.Offensive.AvgGoalsScored = matches.Average(m => m.HomeScore);
        analysis.Offensive.AvgShotsOnTarget = CalculateAvgStat(matches, "Shots on target", isHome: true);

        foreach (var match in matches)
        {
            var homeGoals = match.Incidents.Where(i => i.IncidentType == "goal" && i.IsHome).ToList();
            
            analysis.Offensive.GoalsFirstHalf += homeGoals.Count(g => g.Time <= 45);
            analysis.Offensive.GoalsSecondHalf += homeGoals.Count(g => g.Time > 45);

            if (ScoredFirst(match, isHome: true))
                analysis.Offensive.ScoredFirstCount++;
        }

        // Métricas Defensivas
        analysis.Defensive.AvgGoalsConceded = matches.Average(m => m.AwayScore);
        analysis.Defensive.CleanSheets = matches.Count(m => m.AwayScore == 0);

        foreach (var match in matches)
        {
            var awayGoals = match.Incidents.Where(i => i.IncidentType == "goal" && !i.IsHome).ToList();
            
            analysis.Defensive.GoalsConcededFirstHalf += awayGoals.Count(g => g.Time <= 45);
            analysis.Defensive.GoalsConcededSecondHalf += awayGoals.Count(g => g.Time > 45);

            if (ConcededFirst(match, isHome: true))
                analysis.Defensive.ConcededFirstCount++;
        }

        // Métricas de Resultado
        analysis.Results.Wins = matches.Count(m => m.HomeScore > m.AwayScore);
        analysis.Results.Draws = matches.Count(m => m.HomeScore == m.AwayScore);
        analysis.Results.Losses = matches.Count(m => m.HomeScore < m.AwayScore);

        _logger.LogInformation(
            "Análise mandante {Team}: {Games} jogos, {Avg} gols/jogo, {Wins}V {Draws}E {Losses}D",
            teamName, matches.Count, analysis.Offensive.AvgGoalsScored.ToString("F1"),
            analysis.Results.Wins, analysis.Results.Draws, analysis.Results.Losses
        );

        return analysis;
    }

    /// <summary>
    /// Analisa forma de um time como visitante
    /// </summary>
    public TeamFormAnalysis AnalyzeAwayForm(List<DbMatch> matches, string teamName)
    {
        if (!matches.Any())
        {
            _logger.LogWarning("Nenhum jogo encontrado para análise de {Team} como visitante", teamName);
            return new TeamFormAnalysis { Context = "Sem dados disponíveis" };
        }

        var analysis = new TeamFormAnalysis
        {
            GamesAnalyzed = matches.Count,
            Context = $"Últimos {matches.Count} jogos como visitante"
        };

        // Métricas Ofensivas
        analysis.Offensive.AvgGoalsScored = matches.Average(m => m.AwayScore);
        analysis.Offensive.AvgShotsOnTarget = CalculateAvgStat(matches, "Shots on target", isHome: false);

        foreach (var match in matches)
        {
            var awayGoals = match.Incidents.Where(i => i.IncidentType == "goal" && !i.IsHome).ToList();
            
            analysis.Offensive.GoalsFirstHalf += awayGoals.Count(g => g.Time <= 45);
            analysis.Offensive.GoalsSecondHalf += awayGoals.Count(g => g.Time > 45);

            if (ScoredFirst(match, isHome: false))
                analysis.Offensive.ScoredFirstCount++;
        }

        // Métricas Defensivas
        analysis.Defensive.AvgGoalsConceded = matches.Average(m => m.HomeScore);
        analysis.Defensive.CleanSheets = matches.Count(m => m.HomeScore == 0);

        foreach (var match in matches)
        {
            var homeGoals = match.Incidents.Where(i => i.IncidentType == "goal" && i.IsHome).ToList();
            
            analysis.Defensive.GoalsConcededFirstHalf += homeGoals.Count(g => g.Time <= 45);
            analysis.Defensive.GoalsConcededSecondHalf += homeGoals.Count(g => g.Time > 45);

            if (ConcededFirst(match, isHome: false))
                analysis.Defensive.ConcededFirstCount++;
        }

        // Métricas de Resultado
        analysis.Results.Wins = matches.Count(m => m.AwayScore > m.HomeScore);
        analysis.Results.Draws = matches.Count(m => m.AwayScore == m.HomeScore);
        analysis.Results.Losses = matches.Count(m => m.AwayScore < m.HomeScore);

        _logger.LogInformation(
            "Análise visitante {Team}: {Games} jogos, {Avg} gols/jogo, {Wins}V {Draws}E {Losses}D",
            teamName, matches.Count, analysis.Offensive.AvgGoalsScored.ToString("F1"),
            analysis.Results.Wins, analysis.Results.Draws, analysis.Results.Losses
        );

        return analysis;
    }

    /// <summary>
    /// Calcula média de uma estatística específica
    /// </summary>
    private double CalculateAvgStat(List<DbMatch> matches, string statName, bool isHome)
    {
        var values = new List<double>();

        foreach (var match in matches)
        {
            var stat = match.Stats.FirstOrDefault(s => 
                s.Name == statName && 
                s.Period == "ALL"
            );

            if (stat != null)
            {
                var value = isHome ? stat.HomeValue : stat.AwayValue;
                if (double.TryParse(value, out double parsed))
                {
                    values.Add(parsed);
                }
            }
        }

        return values.Any() ? values.Average() : 0;
    }

    /// <summary>
    /// Verifica se o time marcou o primeiro gol da partida
    /// </summary>
    private bool ScoredFirst(DbMatch match, bool isHome)
    {
        var firstGoal = match.Incidents
            .Where(i => i.IncidentType == "goal")
            .OrderBy(i => i.Time)
            .ThenBy(i => i.AddedTime)
            .FirstOrDefault();

        if (firstGoal == null) return false;

        return firstGoal.IsHome == isHome;
    }

    /// <summary>
    /// Verifica se o time sofreu o primeiro gol da partida
    /// </summary>
    private bool ConcededFirst(DbMatch match, bool isHome)
    {
        var firstGoal = match.Incidents
            .Where(i => i.IncidentType == "goal")
            .OrderBy(i => i.Time)
            .ThenBy(i => i.AddedTime)
            .FirstOrDefault();

        if (firstGoal == null) return false;

        return firstGoal.IsHome != isHome;
    }
}