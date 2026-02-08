# AGENTS.md - Guia para Agentes de Código

Este arquivo fornece orientações para agentes de IA que trabalham no código deste repositório. Leia este documento antes de fazer qualquer modificação.

---

## Visão Geral do Projeto

**SofaScrapper** é uma aplicação .NET 8 que realiza scraping de dados de partidas de futebol do site SofaScore.com, armazena em PostgreSQL e fornece predições via API REST. O sistema consiste em três projetos:

| Projeto | Tipo | Descrição |
|---------|------|-----------|
| `SofaScore.Api` | ASP.NET Core Web API | Exponde dados de partidas e predições via endpoints REST |
| `SofaScore.Worker` | Background Service Worker | Realiza scraping e enriquecimento de dados continuamente |
| `SofaScore.Shared` | Class Library | Modelos, contexto de banco de dados e serviços compartilhados |

---

## Stack Tecnológica

- **Framework**: .NET 8.0
- **Banco de Dados**: PostgreSQL (via Supabase em produção)
- **ORM**: Entity Framework Core 8.0
- **Web Scraping**: PuppeteerSharp 20.2.5 (automação de browser)
- **API Documentation**: Swagger / OpenAPI (Swashbuckle.AspNetCore)
- **Provider PostgreSQL**: Npgsql.EntityFrameworkCore.PostgreSQL 8.0.11

---

## Arquitetura do Projeto

### Fluxo de Dados

1. **Worker** faz scraping periódico do SofaScore.com usando PuppeteerSharp
2. Dados das partidas fluem através do serviço `SofaScraper` com lógica de retry e gerenciamento de sessão
3. Dados brutos são armazenados no PostgreSQL via Entity Framework Core
4. Quando partidas terminam, o Worker as "enriquece" com estatísticas detalhadas e incidentes
5. **API** serve dados em cache do banco para os clientes
6. **Motor de Predição** analisa dados históricos para gerar predições

### Estrutura de Diretórios

```
SofaScrapper/
├── SofaScore.Api/              # Projeto da API REST
│   ├── Controllers/            # Controladores ASP.NET Core
│   │   ├── MatchesController.cs
│   │   └── PredictionsController.cs
│   ├── Migrations/             # Migrations do EF Core
│   ├── Properties/
│   ├── appsettings.json        # Configurações (connection string, etc.)
│   └── Program.cs              # Ponto de entrada da API
│
├── SofaScore.Shared/           # Biblioteca compartilhada
│   ├── Data/
│   │   └── AppDbContext.cs     # DbContext do EF Core
│   ├── Models/
│   │   └── Models.cs           # Entidades e DTOs
│   ├── Services/
│   │   ├── SofaScraper.cs      # Serviço de scraping
│   │   ├── DataManager.cs      # Gerenciamento de dados
│   │   ├── PredictionEngine.cs # Motor de predições
│   │   ├── MatchPredictionService.cs
│   │   ├── MatchDataFetcher.cs
│   │   └── StatsAnalyzer.cs
│   └── Utils/
│       ├── TournamentsInfo.cs  # Configuração de campeonatos
│       └── TimestampHelper.cs
│
├── SofaScore.Worker/           # Serviço de background
│   ├── Services/
│   │   ├── MatchEnrichmentWorker.cs  # Worker principal
│   │   └── RoundScheduler.cs         # Agendador de rodadas
│   ├── appsettings.json
│   └── Program.cs
│
└── scrapper.sln                # Solution file
```

---

## Configuração de Campeonatos

Os IDs dos campeonatos estão hardcoded em `SofaScore.Shared/Utils/TournamentsInfo.cs`:

| Campeonato | TournamentId | SeasonId | Total Rodadas |
|------------|-------------|----------|---------------|
| Premier League | 17 | 76986 | 38 |
| La Liga | 8 | 77559 | 38 |
| Serie A | 23 | 76457 | 38 |
| Ligue 1 | 34 | 77356 | 34 |
| Bundesliga | 35 | 77333 | 34 |
| Brasileirão | 325 | 87678 | 38 |
| Champions League | 7 | 76953 | 13 |

**Nota especial para Champions League:** Possui fase de liga (rodadas 1-8) e fases eliminatórias (Playoff, Oitavas, Quartas, Semi, Final) configuradas na lista `KnockoutPhases`.

---

## Esquema do Banco de Dados

### Entidades Principais (AppDbContext)

- **DbMatch**: Dados básicos da partida com controle de status de processamento
- **DbMatchStat**: Estatísticas por período (ALL, 1ST, 2ND) - relacionado à partida
- **DbIncident**: Gols, cartões, substituições - relacionado à partida
- **DbStanding**: Posições na tabela por time/campeonato/temporada
- **DbStandingPromotion**: Informações de promoção/rebaixamento
- **DbRoundState**: Rastreia quais rodadas foram totalmente processadas

### Status de Processamento

```csharp
public enum MatchProcessingStatus
{
    Pending = 0,      // Aguardando processamento
    InProgress = 1,   // Jogo rolando (ignorar worker)
    Enriched = 2,     // Sucesso completo
    Postponed = 3,    // Adiado
    Cancelled = 4,    // Cancelado
    PartialData = 5,  // Dados parciais
    Error = 99        // Falha no enriquecimento
}
```

### Índices Importantes

- Matches: `TournamentId + Round + ProcessingStatus`
- Standings: `TournamentId + SeasonId + TeamId` (unique)
- RoundStates: `TournamentId + SeasonId + Round` (unique)

---

## Comandos de Build e Execução

### Build

```bash
# Build de toda a solution
dotnet build

# Build de projeto específico
dotnet build SofaScore.Api
dotnet build SofaScore.Worker
```

### Execução

```bash
# Executar a API (http://localhost:5000 / https://localhost:5001)
dotnet run --project SofaScore.Api

# Executar o Worker (scraper em background)
dotnet run --project SofaScore.Worker

# Modo watch para desenvolvimento
dotnet watch run --project SofaScore.Api
```

### Banco de Dados (Entity Framework Core)

```bash
# Adicionar nova migration (executar da raiz da solution)
dotnet ef migrations add <NomeMigration> --context AppDbContext --startup-project SofaScore.Api --project SofaScore.Api

# Aplicar migrations ao banco
dotnet ef database update --startup-project SofaScore.Api

# Gerar script SQL das migrations
dotnet ef migrations script --startup-project SofaScore.Api
```

---

## Endpoints da API

### MatchesController

| Método | Endpoint | Descrição |
|--------|----------|-----------|
| GET | `/api/matches/live` | Partidas ao vivo |
| GET | `/api/matches/tournament/{nome}/round/{rodada}` | Partidas por campeonato e rodada |
| GET | `/api/matches/champions-league/phase/{phaseId}` | Fase de liga da Champions |
| GET | `/api/matches/champions-league/playoff/{roundId}` | Fases eliminatórias da Champions |
| GET | `/api/matches/{matchId}/details` | Detalhes completos da partida |
| GET | `/api/matches/tournament/{nome}/standings` | Tabela de classificação |
| POST | `/api/matches/admin/sync-all-standings` | [Admin] Popula standings inicialmente |

### PredictionsController

| Método | Endpoint | Descrição |
|--------|----------|-----------|
| GET | `/api/predictions/match/{matchId}` | Gera predição para uma partida |
| GET | `/api/predictions/settings` | Configurações do sistema de predição |

---

## Configurações (appsettings.json)

### ConnectionStrings
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "User Id=...;Password=...;Server=...;Port=5432;Database=postgres"
  }
}
```

Usa PostgreSQL hospedado no Supabase em produção.

### PredictionSettings
```json
{
  "PredictionSettings": {
    "MinGamesForAnalysis": 5,
    "MinGamesForReliability": 3,
    "ConfidenceLevels": {
      "High": 5,
      "Medium": 3,
      "Low": 1
    }
  }
}
```

### WorkerSettings
```json
{
  "WorkerSettings": {
    "EnableDeepSleep": true,
    "DeepSleepIntervalMinutes": 8,
    "PreGameWakeupMinutes": 15
  }
}
```

---

## Serviços Principais

### SofaScraper (Shared)

Serviço de automação de browser usando PuppeteerSharp:

- **Flags otimizadas**: Diferentes configurações para Linux (produção/Railway) e Windows (desenvolvimento)
- **Gerenciamento de sessão**: Reciclagem automática após 10 operações ou 2 minutos
- **Retry logic**: Exponential backoff para requisições falhas
- **Métodos principais**:
  - `GetMatchesAsync(tournamentId, seasonId, round)`
  - `GetLiveMatchesAsync()`
  - `GetMatchDetailsAsync(eventId)`
  - `GetMatchStatisticsAsync(eventId)`
  - `GetMatchIncidentsAsync(eventId)`
  - `GetStandingsAsync(tournamentId, seasonId)`
  - `EnrichSingleMatchAsync(matchId)`

### MatchEnrichmentWorker (Worker)

Worker principal que orquestra o scraping em 4 fases:

1. **Fase 0**: Limpeza de partidas zumbis (duplicadas/adiadas)
2. **Fase 0.5**: Busca próximas rodadas (a cada 6h)
3. **Fase 1**: Sincronização de jogos ao vivo (score + status)
4. **Fase 1.5**: Processa jogos que saíram do ao vivo
5. **Fase 2**: Enriquecimento de partidas finalizadas + sync de standings
6. **Fase 3**: Processamento de partidas em "limbo" (atrasadas)

**Deep Sleep**: Quando não há jogos próximos, o Worker entra em modo de economia.

### RoundScheduler (Worker)

Busca proativamente próximas rodadas antes de serem requisitadas pela API:
- Executa a cada 6 horas
- Tratamento especial para Champions League (fase de liga + eliminatórias)

### PredictionEngine (Shared)

Gera predições baseadas em análise de forma recente:
- Média de gols marcados/sofridos
- Finalizações no gol
- Gols por tempo (1º e 2º)
- Frequência de marcar/sofrer primeiro
- Clean sheets
- Cartões e disciplina

---

## Diretrizes de Código

### Estilo

- Usar `Nullable` habilitado (anotações de nullabilidade obrigatórias)
- Usar `ImplicitUsings` habilitado
- Nomes em **inglês** para classes, métodos e propriedades
- Comentários e logs em **português**
- Chaves em nova linha (estilo C# padrão)

### Convenções de Nomenclatura

```csharp
// Classes e interfaces: PascalCase
public class MatchEnrichmentWorker : BackgroundService
public interface IScraperService

// Métodos e propriedades: PascalCase
public async Task<List<Match>> GetMatchesAsync()
public string HomeTeam { get; set; }

// Variáveis locais e parâmetros: camelCase
var matches = await scraper.GetMatchesAsync(tournamentId);

// Constantes: PascalCase ou UPPER_SNAKE_CASE
private const int MaxOperationsPerSession = 10;
private const int SESSION_HEALTH_CHECK_MINUTES = 2;

// Campos privados: _camelCase com underscore
private readonly ILogger<MatchEnrichmentWorker> _logger;
```

### Tratamento de Erros

```csharp
// Sempre usar try/catch em operações de scraping
try
{
    var data = await scraper.EnrichSingleMatchAsync(matchId);
}
catch (Exception ex)
{
    _logger.LogError(ex, "Falha ao enriquecer partida {MatchId}", matchId);
    // Incrementar tentativas, salvar erro
}

// Usar Result ou null para operações que podem falhar
public async Task<MatchDetail?> GetMatchDetailsAsync(int eventId)
```

### Logs

Usar os níveis de log apropriados:
- `LogInformation`: Eventos importantes do fluxo (scraping, enriquecimento)
- `LogWarning`: Situações anormais mas recuperáveis (retry, timeouts)
- `LogError`: Falhas que requerem atenção
- `LogDebug`: Informações detalhadas para debugging

Prefixos comuns em logs:
- `✅` Sucesso
- `❌` Erro
- `⚠️` Aviso
- `🔍` Buscando dados
- `📊` Estatísticas
- `🚀` Inicialização

---

## Considerações de Segurança

### Connection Strings
- **NUNCA** commite `appsettings.json` com credenciais reais
- O arquivo está no `.gitignore`
- Usar variáveis de ambiente em produção

### Scraping
- Respeitar rate limits do site alvo
- Usar User-Agent realista
- Implementar delays entre requisições

### PuppeteerSharp
- Flags específicas para Linux/Railway para economia de memória
- Sessões recicladas frequentemente para prevenir vazamentos
- Limpeza agressiva de recursos (`GC.Collect()`)

---

## Notas de Implementação Importantes

### Ambiguidade de Rodada na Champions League
O Round ID 5 é usado tanto para fase de liga (jogos antes dos playoffs) quanto para Oitavas de Final (jogos depois dos playoffs). O sistema usa filtragem por timestamp para distinguir entre eles.

### Zombie Match Cleanup
Query SQL em `RemoveZombieMatchesAsync` remove partidas duplicadas adiadas/canceladas quando existe uma versão válida.

### Worker Downtime
Quando o Worker reinicia, ele verifica partidas "presas" (marcadas como InProgress mas não estão no feed ao vivo) e as processa imediatamente.

### Deep Sleep
Quando não há jogos próximos, o Worker entra em modo de economia (deep sleep) por 8 minutos para reduzir consumo de recursos.

---

## Testes

O projeto **não possui testes automatizados** atualmente. Ao adicionar:
- Usar xUnit (padrão do .NET)
- Criar projeto `SofaScrapper.Tests`
- Usar InMemory database para testes de integração com EF Core
- Mockar `SofaScraper` para testes unitários

---

## Recursos Úteis

- Documentação PuppeteerSharp: https://www.puppeteersharp.com/
- Entity Framework Core: https://docs.microsoft.com/ef/core/
- ASP.NET Core: https://docs.microsoft.com/aspnet/core/
- API do SofaScore: Não documentada oficialmente (reverse engineering via browser DevTools)
