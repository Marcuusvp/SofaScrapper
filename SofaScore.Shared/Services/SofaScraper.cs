using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using SofaScoreScraper;
using System.Text.Json;

namespace SofaScore.Shared.Services;

public class SofaScraper
{
    private IBrowser? _browser;
    private IPage? _page;
    private readonly SemaphoreSlim _pageSemaphore = new(1, 1);
    private readonly ILogger<SofaScraper>? _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private DateTime _lastInitialization = DateTime.MinValue;

    private const int MAX_RECONNECT_ATTEMPTS = 3;
    private const int RECONNECT_DELAY_MS = 2000;
    private const int SESSION_HEALTH_CHECK_MINUTES = 30;

    public SofaScraper(ILogger<SofaScraper>? logger = null)
    {
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            await CleanupAsync();

            var browserArgs = new List<string>
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu",
                "--disable-extensions",
                "--no-first-run",
                "--disable-features=IsolateOrigins,site-per-process",
                "--js-flags=\"--max-old-space-size=256\""
            };

            var launchOptions = new LaunchOptions { Headless = true };
            var executablePath = Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH");

            if (!string.IsNullOrEmpty(executablePath))
            {
                _logger?.LogInformation("🐧 Rodando em modo Docker/Linux. Otimizando memória...");
                browserArgs.Add("--single-process");
                browserArgs.Add("--no-zygote");
                launchOptions.ExecutablePath = executablePath;
            }
            else
            {
                _logger?.LogInformation("💻 Rodando em modo Local. Usando configurações padrão...");
                var browserFetcher = new BrowserFetcher();
                await browserFetcher.DownloadAsync();
            }

            launchOptions.Args = browserArgs.ToArray();
            _browser = await Puppeteer.LaunchAsync(launchOptions);
            _page = await _browser.NewPageAsync();
            _page.DefaultTimeout = 60000;

            await _page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36");

            _lastInitialization = DateTime.UtcNow;
            _logger?.LogInformation("✅ Navegador iniciado com sucesso!");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "❌ Erro ao inicializar scraper");
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // =================================================================================================
    // MÉTODOS PÚBLICOS (Gerenciam a sessão e retry)
    // =================================================================================================

    public async Task<List<Match>> GetLiveMatchesAsync()
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            await page.GoToAsync("https://www.sofascore.com/");
            await Task.Delay(2000);

            var apiUrl = "https://www.sofascore.com/api/v1/sport/football/events/live";
            var json = await FetchJsonFromPage(page, apiUrl);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var response = JsonSerializer.Deserialize<ApiResponse>(json, options);

            if (response?.Events == null) return new List<Match>();

            var filteredEvents = response.Events
                .Where(e => e.Tournament?.UniqueTournament?.Id != null &&
                            TournamentsInfo.MonitoredIds.Contains(e.Tournament.UniqueTournament.Id.Value));

            return filteredEvents.Select(e => new Match
            {
                Id = e.Id,
                //TournamentId = e.Tournament?.UniqueTournament?.Id ?? 0, // Descomente se tiver adicionado ao Match
                HomeTeam = e.HomeTeam?.Name ?? "N/A",
                AwayTeam = e.AwayTeam?.Name ?? "N/A",
                HomeScore = e.HomeScore?.Current,
                AwayScore = e.AwayScore?.Current,
                Status = e.Status?.Description ?? "N/A",
                StartTimestamp = e.StartTimestamp, // ✅ Passando timestamp bruto
                StartTime = DateTimeOffset.FromUnixTimeSeconds(e.StartTimestamp).UtcDateTime
            }).ToList();
        }, "GetLiveMatchesAsync");
    }

    public async Task<EventDetail?> GetMatchDetailsAsync(int eventId)
    {
        return await ExecuteWithRetryAsync(page => FetchMatchDetailsInternal(page, eventId), $"GetMatchDetailsAsync({eventId})");
    }

    public async Task<StatisticsResponse?> GetMatchStatisticsAsync(int eventId)
    {
        return await ExecuteWithRetryAsync(page => FetchMatchStatisticsInternal(page, eventId), $"GetMatchStatisticsAsync({eventId})");
    }

    public async Task<List<Incident>> GetMatchIncidentsAsync(int eventId)
    {
        return await ExecuteWithRetryAsync(page => FetchMatchIncidentsInternal(page, eventId), $"GetMatchIncidentsAsync({eventId})");
    }

    // ✅ MÉTODO CORRIGIDO: Não chama ExecuteWithRetryAsync internamente, usa a página já aberta
    public async Task<MatchEnrichmentData> EnrichSingleMatchAsync(int matchId)
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            // Reutiliza a MESMA página para todas as chamadas (Muito mais rápido e sem deadlock)
            var details = await FetchMatchDetailsInternal(page, matchId);
            // Pequeno delay para não ser bloqueado por rate limit
            await Task.Delay(500); 
            var stats = await FetchMatchStatisticsInternal(page, matchId);
            await Task.Delay(500);
            var incidents = await FetchMatchIncidentsInternal(page, matchId);

            return new MatchEnrichmentData
            {
                Details = details,
                Statistics = stats,
                Incidents = incidents
            };
        }, $"EnrichSingleMatchAsync({matchId})");
    }

    // ... Outros métodos públicos (GetMatchesAsync, etc) mantenha a lógica antiga ou ajuste para chamar FetchJsonFromPage ...
    // Para economizar espaço, vou focar na correção do Deadlock. Se precisar dos outros métodos (GetMatchesAsync) me avise.
    
    public async Task<List<Match>> GetMatchesAsync(int tournamentId, int seasonId, int round)
    {
         return await ExecuteWithRetryAsync(async (page) =>
        {
            await page.GoToAsync($"https://www.sofascore.com/pt/torneio/futebol/england/premier-league/{tournamentId}");
            await Task.Delay(3000);

            var apiUrl = $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/events/round/{round}";
            var json = await FetchJsonFromPage(page, apiUrl);
            
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var response = JsonSerializer.Deserialize<ApiResponse>(json, options);
            
            return response?.Events?.Select(e => new Match
            {
                Id = e.Id,
                HomeTeam = e.HomeTeam?.Name ?? "N/A",
                AwayTeam = e.AwayTeam?.Name ?? "N/A",
                HomeScore = e.HomeScore?.Current,
                AwayScore = e.AwayScore?.Current,
                Status = e.Status?.Description ?? "N/A",
                StartTimestamp = e.StartTimestamp,
                StartTime = DateTimeOffset.FromUnixTimeSeconds(e.StartTimestamp).UtcDateTime
            }).ToList() ?? new List<Match>();
        }, "GetMatchesAsync");
    }
    
    public async Task<List<Match>> GetQualificationMatchesAsync(int tournamentId, int seasonId, int round, string slug, string? prefix = null)
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
             await page.GoToAsync($"https://www.sofascore.com/pt/torneio/futebol/europe/uefa-champions-league/{tournamentId}");
             await Task.Delay(3000);

             var apiUrl = prefix != null
                ? $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/events/round/{round}/slug/{slug}/prefix/{prefix}"
                : $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/events/round/{round}/slug/{slug}";
            
             var json = await FetchJsonFromPage(page, apiUrl);
             var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
             var response = JsonSerializer.Deserialize<ApiResponse>(json, options);
             
             return response?.Events?.Select(e => new Match
             {
                 Id = e.Id,
                 HomeTeam = e.HomeTeam?.Name ?? "N/A",
                 AwayTeam = e.AwayTeam?.Name ?? "N/A",
                 HomeScore = e.HomeScore?.Current,
                 AwayScore = e.AwayScore?.Current,
                 Status = e.Status?.Description ?? "N/A",
                 StartTimestamp = e.StartTimestamp,
                 StartTime = DateTimeOffset.FromUnixTimeSeconds(e.StartTimestamp).UtcDateTime // Corrigido para UtcDateTime
             }).ToList() ?? new List<Match>();
        }, "GetQualificationMatchesAsync");
    }
    
    public async Task<StandingsTable?> GetStandingsAsync(int tournamentId, int seasonId)
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            await page.GoToAsync($"https://www.sofascore.com/");
            var apiUrl = $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/standings/total";
            var json = await FetchJsonFromPage(page, apiUrl);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var response = JsonSerializer.Deserialize<StandingsResponse>(json, options);
            return response?.Standings?.FirstOrDefault(s => s.Type == "total");
        }, $"GetStandingsAsync({tournamentId})");
    }


    // =================================================================================================
    // MÉTODOS PRIVADOS (Lógica Pura - Sem Locks)
    // =================================================================================================

    private async Task<string> FetchJsonFromPage(IPage page, string url)
    {
        return await page.EvaluateFunctionAsync<string>(@"
            async (url) => {
                const response = await fetch(url, {
                    headers: {
                        'accept': '*/*',
                        'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                    }
                });
                return await response.text();
            }
        ", url);
    }

    private async Task<EventDetail?> FetchMatchDetailsInternal(IPage page, int eventId)
    {
        var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}";
        var json = await FetchJsonFromPage(page, apiUrl);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var response = JsonSerializer.Deserialize<EventDetailResponse>(json, options);
        return response?.Event;
    }

    private async Task<StatisticsResponse?> FetchMatchStatisticsInternal(IPage page, int eventId)
    {
        var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}/statistics";
        var json = await FetchJsonFromPage(page, apiUrl);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<StatisticsResponse>(json, options);
    }

    private async Task<List<Incident>> FetchMatchIncidentsInternal(IPage page, int eventId)
    {
        var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}/incidents";
        var json = await FetchJsonFromPage(page, apiUrl);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var response = JsonSerializer.Deserialize<IncidentsResponse>(json, options);
        return response?.Incidents?.OrderBy(i => i.Time).ThenBy(i => i.AddedTime).ToList() ?? new List<Incident>();
    }

    // =================================================================================================
    // INFRAESTRUTURA (Sessão, Retry, Cleanup)
    // =================================================================================================

    private async Task<bool> IsSessionHealthyAsync()
    {
        try
        {
            if (_browser == null || _page == null) return false;
            if (!_browser.IsConnected) return false;
            await _page.EvaluateExpressionAsync<string>("'health-check'");
            return true;
        }
        catch { return false; }
    }

    private async Task EnsureSessionAsync()
    {
        if (_browser == null || _page == null)
        {
            await InitializeAsync();
            return;
        }

        if (await IsSessionHealthyAsync())
        {
            var timeSinceInit = DateTime.UtcNow - _lastInitialization;
            if (timeSinceInit.TotalMinutes > SESSION_HEALTH_CHECK_MINUTES)
            {
                await InitializeAsync();
            }
            return;
        }

        for (int attempt = 1; attempt <= MAX_RECONNECT_ATTEMPTS; attempt++)
        {
            try
            {
                await InitializeAsync();
                if (await IsSessionHealthyAsync()) return;
            }
            catch (Exception)
            {
                if (attempt < MAX_RECONNECT_ATTEMPTS) await Task.Delay(RECONNECT_DELAY_MS * attempt);
            }
        }
        throw new InvalidOperationException("Falha ao reconectar após tentativas máximas");
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<IPage, Task<T>> operation, string operationName)
    {
        // ... (Mantém a lógica de retry exatamente como estava, mas usando EnsureSessionAsync) ...
        for (int attempt = 1; attempt <= MAX_RECONNECT_ATTEMPTS; attempt++)
        {
            await _pageSemaphore.WaitAsync();
            IPage? page = null;
            try
            {
                await EnsureSessionAsync();
                page = await _browser!.NewPageAsync();
                page.DefaultTimeout = 60000;
                await page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36");
                return await operation(page);
            }
            catch (Exception ex) when (attempt < MAX_RECONNECT_ATTEMPTS)
            {
                _logger?.LogWarning(ex, "⚠️ {Operation} falhou, tentando reconectar...", operationName);
                await Task.Delay(RECONNECT_DELAY_MS);
            }
            finally
            {
                if (page != null) try { await page.CloseAsync(); } catch { }
                _pageSemaphore.Release();
            }
        }
        
        // Última tentativa
        await _pageSemaphore.WaitAsync();
        IPage? finalPage = null;
        try
        {
            await EnsureSessionAsync();
            finalPage = await _browser!.NewPageAsync();
            finalPage.DefaultTimeout = 60000;
            return await operation(finalPage);
        }
        finally
        {
            if (finalPage != null) try { await finalPage.CloseAsync(); } catch { }
            _pageSemaphore.Release();
        }
    }

    private async Task CleanupAsync()
    {
        try { if (_page != null) { await _page.CloseAsync(); _page = null; } } catch { }
        try { if (_browser != null) { await _browser.CloseAsync(); _browser = null; } } catch { }
    }
    
    public async Task DisposeAsync()
    {
        await CleanupAsync();
        GC.Collect();
    }
    
    // Métodos legados que não usam page interna (ex: GetRoundEventsAsync)
    // Devem ser refatorados para usar ExecuteWithRetryAsync e FetchJsonFromPage se possível,
    // mas por hora o foco é corrigir o Deadlock do Enrich.
    public async Task<string> GetEventDetailsAsync(int eventId) => await ExecuteWithRetryAsync(p => FetchJsonFromPage(p, $"https://www.sofascore.com/api/v1/event/{eventId}"), "GetEventDetails");
    public async Task<string> GetEventStatisticsAsync(int eventId) => await ExecuteWithRetryAsync(p => FetchJsonFromPage(p, $"https://www.sofascore.com/api/v1/event/{eventId}/statistics"), "GetEventStatistics");
}