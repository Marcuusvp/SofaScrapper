# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

SofaScrapper is a .NET 8 application that scrapes football match data from SofaScore.com, stores it in PostgreSQL, and provides match predictions via a REST API. The system consists of three projects:

- **SofaScore.Api**: ASP.NET Core Web API exposing match data and predictions
- **SofaScore.Worker**: Background service that scrapes and enriches match data continuously
- **SofaScore.Shared**: Class library with models, database context, and shared services

## Common Commands

### Build and Run
```bash
# Build entire solution
dotnet build

# Run the API (http://localhost:5000 / https://localhost:5001)
dotnet run --project SofaScore.Api

# Run the Worker (background scraper)
dotnet run --project SofaScore.Worker

# Run both simultaneously (separate terminals)
dotnet run --project SofaScore.Api & dotnet run --project SofaScore.Worker
```

### Database (Entity Framework Core with PostgreSQL)
```bash
# Add a new migration (run from solution root)
dotnet ef migrations add <MigrationName> --context AppDbContext --startup-project SofaScore.Api --project SofaScore.Api

# Apply migrations to database
dotnet ef database update --startup-project SofaScore.Api

# Generate SQL script for migrations
dotnet ef migrations script --startup-project SofaScore.Api
```

### Testing and Debugging
```bash
# Run in watch mode for development
dotnet watch run --project SofaScore.Api

# Run with specific environment
dotnet run --project SofaScore.Api --environment Development
```

## High-Level Architecture

### Data Flow
1. **Worker** periodically scrapes SofaScore.com using PuppeteerSharp
2. Match data flows through `SofaScraper` service with retry logic and session management
3. Raw match data is stored in PostgreSQL via Entity Framework Core
4. When matches finish, the Worker "enriches" them with detailed stats and incidents
5. **API** serves cached data from the database to clients
6. **Prediction Engine** analyzes historical match data to generate predictions

### Key Services

**SofaScraper (Shared)**
- Browser automation using PuppeteerSharp with aggressive memory-optimized flags for Linux/Railway deployment
- Session management with automatic recycling after 10 operations or 2 minutes
- Retry logic with exponential backoff for failed requests
- Methods: `GetMatchesAsync`, `GetLiveMatchesAsync`, `GetMatchDetailsAsync`, `GetMatchStatisticsAsync`, `GetMatchIncidentsAsync`, `GetStandingsAsync`

**MatchEnrichmentWorker (Worker)**
- Main background service that orchestrates scraping in 4 phases:
  1. Live match sync (updates scores/status every 2 min when games are live)
  2. Enrichment of finished matches (fetches stats + incidents)
  3. Standings synchronization (updates league tables after matches complete)
  4. Limbo processing (handles stuck/postponed matches)
- Dynamically adjusts polling interval (2 min active vs 10 min idle)

**RoundScheduler (Worker)**
- Proactively fetches next rounds before they're requested via API
- Runs every 6 hours to check for new fixtures
- Special handling for Champions League (league phase 1-8 + knockout rounds)

**DataManager (Shared)**
- Bridge between scraper and database
- Methods: `SaveOrUpdateMatchesFromRoundAsync`, `GetMatchFullDataAsync`
- Handles `MatchProcessingStatus` transitions

**Prediction Services (Shared)**
- `MatchDataFetcher`: Retrieves historical matches for analysis
- `StatsAnalyzer`: Calculates team form metrics (offensive, defensive, discipline)
- `PredictionEngine`: Generates probability-based predictions for results, goals, first goal, halftime, and cards

### Database Schema (AppDbContext)

Core entities:
- **DbMatch**: Match header data with processing status tracking
- **DbMatchStat**: Statistics per period (ALL, 1ST, 2ND) - linked to match
- **DbIncident**: Goals, cards, substitutions - linked to match
- **DbStanding**: League table positions per team/tournament/season
- **DbStandingPromotion**: Promotion/relegation info per standing
- **DbRoundState**: Tracks which rounds have been fully processed

Key indexes:
- Matches: TournamentId + Round + ProcessingStatus
- Standings: TournamentId + SeasonId + TeamId (unique)
- RoundStates: TournamentId + SeasonId + Round (unique)

### Tournament Configuration

Tournament IDs are hardcoded in `TournamentsInfo.cs`:
- Premier League (17), La Liga (8), Serie A (23), Ligue 1 (34), Bundesliga (35)
- Brasileirão (325), Champions League (7)

Champions League has special handling with league phase (rounds 1-8) and knockout phases (Playoff, R16, QF, SF, Final) stored in `KnockoutPhases` list.

### Match Processing Status States

```csharp
public enum MatchProcessingStatus
{
    Pending = 0,      // Aguardando processamento
    InProgress = 1,   // Jogo rolando (ignorar worker)
    Enriched = 2,     // Sucesso completo
    Postponed = 3,    // Adiado
    Cancelled = 4,    // Cancelado
    PartialData = 5,  // Dados parciais
    Error = 99        // Falha
}
```

### Configuration

**Connection Strings**: PostgreSQL connection configured in `appsettings.json`
- Uses Supabase hosted PostgreSQL in production
- Connection string includes pooling parameters for Railway deployment

**PredictionSettings**: Configured in `appsettings.json`
- `MinGamesForAnalysis`: 5 (target games for analysis)
- `MinGamesForReliability`: 3 (minimum for any prediction)
- `ConfidenceLevels`: High=5, Medium=3, Low=1 games

### API Endpoints (MatchesController)

- `GET /api/matches/live` - Live matches with safety cutoff
- `GET /api/matches/tournament/{name}/round/{round}` - Matches by tournament and round
- `GET /api/matches/champions-league/phase/{phaseId}` - Champions League league phase
- `GET /api/matches/champions-league/playoff/{roundId}` - Champions League knockouts
- `GET /api/matches/{matchId}/details` - Full match details with stats/incidents
- `GET /api/matches/tournament/{name}/standings` - League table
- `POST /api/matches/admin/sync-all-standings` - Admin: initial standings population

### API Endpoints (PredictionsController)

- `GET /api/predictions/match/{matchId}` - Generate prediction for a match
- `GET /api/predictions/settings` - View prediction configuration

### Important Implementation Notes

**Browser Session Management**: `SofaScraper` uses aggressive resource cleanup with `MAX_OPERATIONS_PER_SESSION = 10` and `SESSION_HEALTH_CHECK_MINUTES = 2` to prevent memory leaks in containerized environments.

**Champions League Round Ambiguity**: Round ID 5 is used for both league phase (matches before playoffs) and Round of 16 (matches after playoffs). The system uses timestamp-based filtering to distinguish between them.

**Worker Downtime Handling**: When the Worker restarts, it checks for "stuck" matches (marked InProgress but not in live feed) and processes them immediately.

**Zombie Match Cleanup**: SQL query in `RemoveZombieMatchesAsync` removes duplicate postponed/cancelled matches when a valid version exists.
