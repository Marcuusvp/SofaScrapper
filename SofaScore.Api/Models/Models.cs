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