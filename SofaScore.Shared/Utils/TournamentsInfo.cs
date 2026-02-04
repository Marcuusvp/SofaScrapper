namespace SofaScoreScraper;

// ✅ Record definido no namespace raiz (fora de classes)
public record KnockoutPhase(int RoundId, string Slug, string? Prefix, string Name);

public static class TournamentsInfo
{
    public static class PremierLeague
    {
        public const int TournamentId = 17;
        public const int SeasonId = 76986;
        public const int TotalRounds = 38;
        public const string Name = "Premier League";
        public const string Country = "Inglaterra";
    }

    public static class LaLiga
    {
        public const int TournamentId = 8;
        public const int SeasonId = 77559;
        public const int TotalRounds = 38;
        public const string Name = "La Liga";
        public const string Country = "Espanha";
    }

    public static class SerieA
    {
        public const int TournamentId = 23;
        public const int SeasonId = 76457;
        public const int TotalRounds = 38;
        public const string Name = "Serie A";
        public const string Country = "Itália";
    }

    public static class Ligue1
    {
        public const int TournamentId = 34;
        public const int SeasonId = 77356;
        public const int TotalRounds = 34;
        public const string Name = "Ligue 1";
        public const string Country = "França";
    }

    public static class Bundesliga
    {
        public const int TournamentId = 35;
        public const int SeasonId = 77333;
        public const int TotalRounds = 34;
        public const string Name = "Bundesliga";
        public const string Country = "Alemanha";
    }

    public static class Brasileirao
    {
        public const int TournamentId = 325;
        public const int SeasonId = 87678;
        public const int TotalRounds = 38;
        public const string Name = "Brasileirão Série A";
        public const string Country = "Brasil";
    }

    public static class ChampionsLeague
    {
        public const int TournamentId = 7;
        public const int SeasonId = 76953;
        public const string Name = "Champions League";

        public const int LeaguePhaseStart = 1;
        public const int LeaguePhaseEnd = 8;

        public static readonly List<KnockoutPhase> KnockoutPhases = new()
        {
            new(636, "playoff-round", null, "Playoff Round"),
            new(5, "round-of-16", null, "Round of 16"),
            new(27, "quarterfinals", null, "Quarter Finals"),
            new(28, "semifinals", null, "Semi Finals"),
            new(29, "final", null, "Final")
        };
    }

    public static (int tournamentId, int seasonId, int totalRounds, string name) GetTournamentInfo(string tournament)
    {
        return tournament.ToLower() switch
        {
            "premier" or "premierleague" => (PremierLeague.TournamentId, PremierLeague.SeasonId, PremierLeague.TotalRounds, PremierLeague.Name),
            "laliga" => (LaLiga.TournamentId, LaLiga.SeasonId, LaLiga.TotalRounds, LaLiga.Name),
            "seriea" => (SerieA.TournamentId, SerieA.SeasonId, SerieA.TotalRounds, SerieA.Name),
            "ligue1" => (Ligue1.TournamentId, Ligue1.SeasonId, Ligue1.TotalRounds, Ligue1.Name),
            "bundesliga" => (Bundesliga.TournamentId, Bundesliga.SeasonId, Bundesliga.TotalRounds, Bundesliga.Name),
            "brasileirao" => (Brasileirao.TournamentId, Brasileirao.SeasonId, Brasileirao.TotalRounds, Brasileirao.Name),
            "championsleague" => (ChampionsLeague.TournamentId, ChampionsLeague.SeasonId, 13, ChampionsLeague.Name),
            _ => throw new ArgumentException($"Campeonato '{tournament}' não encontrado")
        };
    }

    public static class AllTournaments
    {
        public static readonly List<(int tournamentId, int seasonId, int totalRounds, string name, string country)> List = new()
        {
            (PremierLeague.TournamentId, PremierLeague.SeasonId, PremierLeague.TotalRounds, PremierLeague.Name, PremierLeague.Country),
            (LaLiga.TournamentId, LaLiga.SeasonId, LaLiga.TotalRounds, LaLiga.Name, LaLiga.Country),
            (SerieA.TournamentId, SerieA.SeasonId, SerieA.TotalRounds, SerieA.Name, SerieA.Country),
            (Ligue1.TournamentId, Ligue1.SeasonId, Ligue1.TotalRounds, Ligue1.Name, Ligue1.Country),
            (Bundesliga.TournamentId, Bundesliga.SeasonId, Bundesliga.TotalRounds, Bundesliga.Name, Bundesliga.Country),
            (Brasileirao.TournamentId, Brasileirao.SeasonId, Brasileirao.TotalRounds, Brasileirao.Name, Brasileirao.Country),
            (ChampionsLeague.TournamentId, ChampionsLeague.SeasonId, 13, ChampionsLeague.Name, "Europa")
        };
    }

    public static int? GetSeasonIdByTournament(int tournamentId)
    {
        return tournamentId switch
        {
            PremierLeague.TournamentId => PremierLeague.SeasonId,
            LaLiga.TournamentId => LaLiga.SeasonId,
            SerieA.TournamentId => SerieA.SeasonId,
            Ligue1.TournamentId => Ligue1.SeasonId,
            Bundesliga.TournamentId => Bundesliga.SeasonId,
            Brasileirao.TournamentId => Brasileirao.SeasonId,
            ChampionsLeague.TournamentId => ChampionsLeague.SeasonId,
            _ => null
        };
    }

    public static int? GetTotalRoundsByTournament(int tournamentId)
    {
        return tournamentId switch
        {
            PremierLeague.TournamentId => PremierLeague.TotalRounds,
            LaLiga.TournamentId => LaLiga.TotalRounds,
            SerieA.TournamentId => SerieA.TotalRounds,
            Ligue1.TournamentId => Ligue1.TotalRounds,
            Bundesliga.TournamentId => Bundesliga.TotalRounds,
            Brasileirao.TournamentId => Brasileirao.TotalRounds,
            _ => null
        };
    }

    public static readonly HashSet<int> MonitoredIds = new()
    {
        PremierLeague.TournamentId,
        LaLiga.TournamentId,
        SerieA.TournamentId,
        Ligue1.TournamentId,
        Bundesliga.TournamentId,
        Brasileirao.TournamentId,
        ChampionsLeague.TournamentId
    };
}
