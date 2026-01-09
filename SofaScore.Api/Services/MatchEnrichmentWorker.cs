using Microsoft.EntityFrameworkCore;
using SofaScore.Api.Data;
using SofaScoreScraper;

namespace SofaScore.Api.Services;

public class MatchEnrichmentWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MatchEnrichmentWorker> _logger;
    private readonly string _instanceId;

    public MatchEnrichmentWorker(
        IServiceProvider serviceProvider,
        ILogger<MatchEnrichmentWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _instanceId = Environment.MachineName + "_" + Guid.NewGuid().ToString()[..8];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("⚙️ Worker iniciado: {InstanceId}", _instanceId);

        // Aguarda 30s antes de começar (dá tempo da API subir completamente)
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAllTournamentsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erro no ciclo do Worker");
            }

            // Aguarda 15 minutos antes do próximo ciclo
            _logger.LogInformation("😴 Aguardando 15 minutos até próxima verificação...");
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    private async Task ProcessAllTournamentsAsync(CancellationToken ct)
    {
        _logger.LogInformation("🔄 Iniciando ciclo de processamento");

        // Itera sobre todos os torneios configurados
        foreach (var tournament in TournamentsInfo.AllTournaments.List)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessTournamentAsync(
                    tournament.tournamentId,
                    tournament.seasonId,
                    tournament.totalRounds,
                    tournament.name,
                    ct
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ Erro ao processar {Tournament}",
                    tournament.name
                );
            }
        }

        _logger.LogInformation("✅ Ciclo de processamento concluído");
    }

    private async Task ProcessTournamentAsync(
        int tournamentId,
        int seasonId,
        int totalRounds,
        string tournamentName,
        CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dataManager = scope.ServiceProvider.GetRequiredService<DataManager>();

        _logger.LogInformation("🏆 Processando {Tournament}", tournamentName);

        // Busca estados de todas as rodadas deste torneio em lote (otimização)
        var roundStates = await db.RoundStates
            .Where(r => r.TournamentId == tournamentId && r.SeasonId == seasonId)
            .ToDictionaryAsync(r => r.Round, ct);

        for (int round = 1; round <= totalRounds; round++)
        {
            if (ct.IsCancellationRequested) break;

            // Verifica se já foi processada
            if (roundStates.TryGetValue(round, out var state) && state.IsFullyProcessed)
            {
                _logger.LogDebug("⏭️ Rodada {Round} já processada, pulando", round);
                continue;
            }

            // Verifica lock (proteção contra múltiplas instâncias)
            if (state?.LockedAt != null && state.LockedBy != _instanceId)
            {
                var lockAge = DateTime.UtcNow - state.LockedAt.Value;
                if (lockAge < TimeSpan.FromMinutes(30))
                {
                    _logger.LogDebug(
                        "🔒 Rodada {Round} travada por {LockedBy}",
                        round, state.LockedBy
                    );
                    continue; // Outra instância está processando
                }
                else
                {
                    _logger.LogWarning(
                        "⚠️ Lock expirado na rodada {Round}, assumindo controle",
                        round
                    );
                }
            }

            await ProcessRoundAsync(
                db,
                dataManager,
                tournamentId,
                seasonId,
                round,
                tournamentName,
                ct
            );
        }
    }

    private async Task ProcessRoundAsync(
    AppDbContext db,
    DataManager dataManager,
    int tournamentId,
    int seasonId,
    int round,
    string tournamentName,
    CancellationToken ct)
    {
        _logger.LogInformation(
            "📋 Processando {Tournament} - Rodada {Round}",
            tournamentName, round
        );

        var roundState = await db.RoundStates.FirstOrDefaultAsync(
            r => r.TournamentId == tournamentId &&
                r.SeasonId == seasonId &&
                r.Round == round,
            ct
        );

        if (roundState == null)
        {
            roundState = new DbRoundState
            {
                TournamentId = tournamentId,
                SeasonId = seasonId,
                Round = round
            };
            db.RoundStates.Add(roundState);
        }

        roundState.LockedAt = DateTime.UtcNow;
        roundState.LockedBy = _instanceId;
        roundState.LastCheck = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        try
        {
            int matchCount = await dataManager.SaveOrUpdateMatchesFromRoundAsync(
                tournamentId,
                seasonId,
                round
            );

            if (matchCount == 0)
            {
                _logger.LogWarning("Nenhum jogo encontrado");
                return;
            }

            var matches = await db.Matches
                .Where(m => m.TournamentId == tournamentId &&
                        m.SeasonId == seasonId &&
                        m.Round == round)
                .ToListAsync(ct);

            roundState.TotalMatches = matches.Count;

            // Enriquecimento
            foreach (var match in matches.Where(m => m.NeedsEnrichment))
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    _logger.LogInformation(
                        "⚡ Enriquecendo jogo {MatchId}: {Home} vs {Away}",
                        match.Id, match.HomeTeam, match.AwayTeam
                    );

                    // ✅ CORRIGIDO: Não passa seasonId (não existe mais)
                    await dataManager.GetMatchFullDataAsync(match.Id);

                    await Task.Delay(2000, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "❌ Falha ao enriquecer jogo {MatchId}",
                        match.Id
                    );
                }
            }

            // ✅ CORRIGIDO: Recarrega do banco para pegar status atualizados
            matches = await db.Matches
                .Where(m => m.TournamentId == tournamentId &&
                        m.SeasonId == seasonId &&
                        m.Round == round)
                .ToListAsync(ct);

            roundState.EnrichedMatches = matches.Count(m => m.ProcessingStatus == MatchProcessingStatus.Enriched);
            roundState.PostponedMatches = matches.Count(m => m.ProcessingStatus == MatchProcessingStatus.Postponed);
            roundState.CancelledMatches = matches.Count(m => m.ProcessingStatus == MatchProcessingStatus.Cancelled);

            if (roundState.ShouldBeMarkedComplete)
            {
                roundState.IsFullyProcessed = true;
                roundState.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation(
                    "✅ Rodada {Round} CONCLUÍDA: {Enriched} enriquecidos, {Cancelled} cancelados",
                    round, roundState.EnrichedMatches, roundState.CancelledMatches
                );
            }
            else
            {
                _logger.LogInformation(
                    "⏳ Rodada {Round} PARCIAL: {Enriched}/{Total} completos",
                    round, roundState.EnrichedMatches, roundState.TotalMatches
                );
            }
        }
        catch (Exception ex)
        {
            roundState.FailedAttempts++;
            roundState.LastError = ex.Message;
            _logger.LogError(ex, "❌ Erro ao processar rodada {Round}", round);
        }
        finally
        {
            roundState.LockedAt = null;
            roundState.LockedBy = null;
            await db.SaveChangesAsync(ct);
        }
    }
}