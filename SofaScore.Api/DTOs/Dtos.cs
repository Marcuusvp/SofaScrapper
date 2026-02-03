using SofaScore.Shared.Data;

namespace SofaScore.Api.DTOs;


/// <summary>
/// DTO para retorno de partida (lista de jogos)
/// Mantém compatibilidade com o frontend
/// </summary>
public class MatchResponse
{
    public int Id { get; set; }
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public string Status { get; set; } = string.Empty;
    public string StartTime { get; set; } = string.Empty;
    public string? Tournament { get; set; }
    public int? Round { get; set; }
}

/// <summary>
/// DTO para retorno de detalhes completos da partida
/// Inclui stats e incidents
/// </summary>
public class MatchDetailResponse
{
    public int Id { get; set; }
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public string Status { get; set; } = string.Empty;
    public string StartTime { get; set; } = string.Empty;
    public string? Tournament { get; set; }
    public int? Round { get; set; }
    
    // Detalhes adicionais
    public string? Stadium { get; set; }
    public string? Referee { get; set; }
    public int? Attendance { get; set; }
    
    // Estatísticas e incidentes
    public List<MatchStatDTO> Stats { get; set; } = new();
    public List<IncidentDTO> Incidents { get; set; } = new();
}

/// <summary>
/// DTO para estatística de partida
/// </summary>
public class MatchStatDTO
{
    public string Period { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string HomeValue { get; set; } = string.Empty;
    public string AwayValue { get; set; } = string.Empty;
    public int CompareCode { get; set; }
}

/// <summary>
/// DTO para incidente de partida
/// </summary>
public class IncidentDTO
{
    public string IncidentType { get; set; } = string.Empty;
    public string? IncidentClass { get; set; }
    public int Time { get; set; }
    public int AddedTime { get; set; }
    public bool IsHome { get; set; }
    public string? PlayerName { get; set; }
    public string? AssistName { get; set; }
}

/// <summary>
/// Classe com métodos de mapeamento de DbMatch para DTOs
/// </summary>
public static class MatchMapper
{
    /// <summary>
    /// Mapeia DbMatch para MatchResponse (lista de jogos)
    /// </summary>
    public static MatchResponse ToMatchResponse(DbMatch dbMatch)
    {
        return new MatchResponse
        {
            Id = dbMatch.Id,
            HomeTeam = dbMatch.HomeTeam,
            AwayTeam = dbMatch.AwayTeam,
            HomeScore = dbMatch.HomeScore,
            AwayScore = dbMatch.AwayScore,
            Status = dbMatch.Status,
            StartTime = DateTimeOffset.FromUnixTimeSeconds(dbMatch.StartTimestamp)
                .UtcDateTime
                .ToString("o"), // ISO 8601 format
            Tournament = GetTournamentName(dbMatch.TournamentId),
            Round = dbMatch.Round
        };
    }

    /// <summary>
    /// Mapeia DbMatch para MatchDetailResponse (detalhes completos)
    /// </summary>
    public static MatchDetailResponse ToMatchDetailResponse(DbMatch dbMatch)
    {
        return new MatchDetailResponse
        {
            Id = dbMatch.Id,
            HomeTeam = dbMatch.HomeTeam,
            AwayTeam = dbMatch.AwayTeam,
            HomeScore = dbMatch.HomeScore,
            AwayScore = dbMatch.AwayScore,
            Status = dbMatch.Status,
            StartTime = DateTimeOffset.FromUnixTimeSeconds(dbMatch.StartTimestamp)
                .UtcDateTime
                .ToString("o"), // ISO 8601 format
            Tournament = GetTournamentName(dbMatch.TournamentId),
            Round = dbMatch.Round,
            Stadium = dbMatch.Stadium,
            Referee = dbMatch.Referee,
            Attendance = dbMatch.Attendance,
            Stats = dbMatch.Stats.Select(s => new MatchStatDTO
            {
                Period = s.Period,
                Name = s.Name,
                HomeValue = s.HomeValue,
                AwayValue = s.AwayValue,
                CompareCode = s.CompareCode
            }).ToList(),
            Incidents = dbMatch.Incidents.Select(i => new IncidentDTO
            {
                IncidentType = i.IncidentType,
                IncidentClass = i.IncidentClass,
                Time = i.Time,
                AddedTime = i.AddedTime,
                IsHome = i.IsHome,
                PlayerName = i.PlayerName,
                AssistName = i.AssistName
            }).ToList()
        };
    }

    /// <summary>
    /// Helper para obter nome do torneio baseado no ID
    /// </summary>
    private static string GetTournamentName(int tournamentId)
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