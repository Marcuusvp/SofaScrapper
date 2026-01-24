namespace SofaScoreScraper;

// Modelos para deserialização da API
public class ApiResponse
{
    public List<Event>? Events { get; set; }
}

public class Event
{
    public int Id { get; set; }
    public Team? HomeTeam { get; set; }
    public Team? AwayTeam { get; set; }
    public Score? HomeScore { get; set; }
    public Score? AwayScore { get; set; }
    public Status? Status { get; set; }
    public long StartTimestamp { get; set; }
}

public class Team
{
    public string? Name { get; set; }
    public int Id { get; set; }
}

public class Score
{
    public int Current { get; set; }
    public int Display { get; set; }
    public int Period1 { get; set; }
    public int Period2 { get; set; }
}

public class Status
{
    public string? Description { get; set; }
    public string? Type { get; set; }
}

// Modelo simplificado para uso no programa
public class Match
{
    public int Id { get; set; }
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
}

// Modelo para detalhes completos do evento
public class EventDetailResponse
{
    public EventDetail? Event { get; set; }
}

public class EventDetail
{
    public int Id { get; set; }
    public Team? HomeTeam { get; set; }
    public Team? AwayTeam { get; set; }
    public Score? HomeScore { get; set; }
    public Score? AwayScore { get; set; }
    public Status? Status { get; set; }
    public long StartTimestamp { get; set; }
    public Venue? Venue { get; set; }
    public Referee? Referee { get; set; }
    public int? Attendance { get; set; }
    public RoundInfo? RoundInfo { get; set; }
    public Tournament? Tournament { get; set; }
}

public class Venue
{
    public string? Name { get; set; }
    public City? City { get; set; }
    public int? Capacity { get; set; }
}

public class City
{
    public string? Name { get; set; }
}

public class Referee
{
    public string? Name { get; set; }
}

public class RoundInfo
{
    public int Round { get; set; }
}

public class Tournament
{
    public string? Name { get; set; }
    public int Id { get; set; } // <--- ADICIONE ESTA LINHA
}

// Modelo para estatísticas
public class StatisticsResponse
{
    public List<StatisticsPeriod>? Statistics { get; set; }
}

public class StatisticsPeriod
{
    public string? Period { get; set; }
    public List<StatisticsGroup>? Groups { get; set; }
}

public class StatisticsGroup
{
    public string? GroupName { get; set; }
    public List<StatisticsItem>? StatisticsItems { get; set; }
}

public class StatisticsItem
{
    public string? Name { get; set; }
    public string? Home { get; set; }
    public string? Away { get; set; }
    public int CompareCode { get; set; } // 1=home melhor, 2=away melhor, 3=empate
}

// Adicione estas classes junto com as outras (Event, Match, etc.)

public class IncidentsResponse
{
    public List<Incident>? Incidents { get; set; }
}

public class Incident
{
    public string? IncidentType { get; set; } // "goal", "card", "substitution", "period"
    public string? IncidentClass { get; set; } // "regular", "penalty", "ownGoal"
    public int Time { get; set; }
    public int AddedTime { get; set; }
    public bool IsHome { get; set; } // true = mandante, false = visitante
    public Player? Player { get; set; } // Quem fez o gol
    public Player? Assist1 { get; set; } // Quem deu assistência
}

public class Player
{
    public string? Name { get; set; }
    public string? ShortName { get; set; }
}

public enum MatchProcessingStatus
{
    Pending = 0,      // Aguardando processamento
    InProgress = 1,   // Jogo rolando (ignorar worker)
    Enriched = 2,     // Sucesso completo
    Postponed = 3,    // Adiado (ignorar temporariamente)
    Cancelled = 4,    // Cancelado (ignorar permanentemente)
    PartialData = 5,  // Dados parciais (falta algo)
    Error = 99        // Falha no enriquecimento
}

/// <summary>
/// Análise de forma de um time (mandante ou visitante)
/// </summary>
public class TeamFormAnalysis
{
    public int GamesAnalyzed { get; set; }
    public string Context { get; set; } = string.Empty; // Ex: "Últimos 5 jogos como mandante"
    
    public OffensiveMetrics Offensive { get; set; } = new();
    public DefensiveMetrics Defensive { get; set; } = new();
    public DisciplineMetrics Discipline { get; set; } = new(); // ✅ NOVO
    public ResultMetrics Results { get; set; } = new();
}

public class OffensiveMetrics
{
    public double AvgGoalsScored { get; set; }
    public double AvgShotsOnTarget { get; set; }
    public int GoalsFirstHalf { get; set; }
    public int GoalsSecondHalf { get; set; }
    public int ScoredFirstCount { get; set; } // Quantas vezes marcou primeiro
}

public class DefensiveMetrics
{
    public double AvgGoalsConceded { get; set; }
    public int CleanSheets { get; set; }
    public int GoalsConcededFirstHalf { get; set; }
    public int GoalsConcededSecondHalf { get; set; }
    public int ConcededFirstCount { get; set; } // Quantas vezes sofreu primeiro
}

public class ResultMetrics
{
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    
    public double WinPercentage => GamesPlayed > 0 ? (Wins / (double)GamesPlayed) * 100 : 0;
    public int GamesPlayed => Wins + Draws + Losses;
}

/// <summary>
/// Predições para uma partida
/// </summary>
public class MatchPrediction
{
    public MatchInfo Match { get; set; } = new();
    public TeamFormAnalysis HomeTeamAnalysis { get; set; } = new();
    public TeamFormAnalysis AwayTeamAnalysis { get; set; } = new();
    public PredictionResults Predictions { get; set; } = new();
    
    public string Confidence { get; set; } = "Low"; // High, Medium, Low
    public List<string> Warnings { get; set; } = new();
}

public class MatchInfo
{
    public int Id { get; set; }
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public string Tournament { get; set; } = string.Empty;
    public int Round { get; set; }
    public DateTime DateTime { get; set; }
}

public class PredictionResults
{
    public ResultPrediction Result { get; set; } = new();
    public GoalsPrediction Goals { get; set; } = new();
    public FirstGoalPrediction FirstGoal { get; set; } = new();
    public HalfTimePrediction HalfTime { get; set; } = new();
    public CardsPrediction Cards { get; set; } = new();
}

public class ResultPrediction
{
    public int HomeWin { get; set; }
    public int Draw { get; set; }
    public int AwayWin { get; set; }
}

public class GoalsPrediction
{
    public int Over25 { get; set; }
    public int Under25 { get; set; }
    public int Btts { get; set; } // Both Teams To Score
}

public class FirstGoalPrediction
{
    public int HomeTeam { get; set; }
    public int AwayTeam { get; set; }
    public string Reasoning { get; set; } = string.Empty;
}

public class HalfTimePrediction
{
    public int HomeLeading { get; set; }
    public int Draw { get; set; }
    public int AwayLeading { get; set; }
}

/// <summary>
/// Métricas de disciplina (cartões e faltas)
/// </summary>
public class DisciplineMetrics
{
    public double AvgYellowCards { get; set; }
    public double AvgRedCards { get; set; }
    public double AvgTotalCards { get; set; }
    public double AvgFoulsCommitted { get; set; }
    public int TotalYellowCards { get; set; }
    public int TotalRedCards { get; set; }
}

/// <summary>
/// Predição de cartões
/// </summary>
public class CardsPrediction
{
    public double ExpectedTotalCards { get; set; }
    public int Over35Cards { get; set; } // Probabilidade de mais de 3.5 cartões
    public int Under35Cards { get; set; }
    public string MostDisciplinedTeam { get; set; } = string.Empty;
}
public class StandingsResponse
{
    public List<StandingsTable>? Standings { get; set; }
}

public class StandingsTable
{
    public Tournament? Tournament { get; set; } // Agora é um objeto
    public string? Type { get; set; }
    public List<StandingRow>? Rows { get; set; }
}

public class StandingRow
{
    public Team? Team { get; set; }
    public int Position { get; set; }
    public int Matches { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public int ScoresFor { get; set; }      // Corrigido para int
    public int ScoresAgainst { get; set; }  // Corrigido para int
    public int Points { get; set; }
    public string? ScoreDiffFormatted { get; set; }
    public Promotion? Promotion { get; set; } // Corrigido para Objeto Promotion
}

public class Promotion
{
    public string? Text { get; set; }
    public int Id { get; set; }
}