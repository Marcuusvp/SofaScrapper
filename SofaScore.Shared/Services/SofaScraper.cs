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
    private const int RECONNECT_DELAY_MS = 3000;
    // Reduzido para 5 min para forçar limpeza de memória frequente no Railway
    private const int SESSION_HEALTH_CHECK_MINUTES = 5; 

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
                "--disable-dev-shm-usage", // Vital para Docker
                "--disable-gpu",
                "--disable-software-rasterizer",
                
                // OTIMIZAÇÃO DE MEMÓRIA (Railway Free Tier)
                "--js-flags=\"--max-old-space-size=128\"", // Heap JS limitado a 128MB
                "--disable-extensions",
                "--disable-background-networking",
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
                "--disable-breakpad",
                "--disable-component-extensions-with-background-pages",
                "--disable-features=IsolateOrigins,site-per-process,Translate,OptimizationHints,MediaRouter,LazyFrameLoading",
                "--disable-ipc-flooding-protection",
                "--disable-renderer-backgrounding",
                "--metrics-recording-only",
                "--mute-audio",
                "--no-default-browser-check",
                "--no-first-run",
                "--no-pings",
                "--password-store=basic",
                "--use-mock-keychain"
            };

            var launchOptions = new LaunchOptions { Headless = true };
            var executablePath = Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH");

            if (!string.IsNullOrEmpty(executablePath))
            {
                _logger?.LogInformation("🐧 Railway Mode: Flags otimizadas ativas.");
                launchOptions.ExecutablePath = executablePath;
            }
            else
            {
                _logger?.LogInformation("💻 Local Mode: Config padrão.");
                var browserFetcher = new BrowserFetcher();
                await browserFetcher.DownloadAsync();
            }

            launchOptions.Args = browserArgs.ToArray();
            _browser = await Puppeteer.LaunchAsync(launchOptions);
            
            _page = await _browser.NewPageAsync();
            _page.DefaultTimeout = 90000; // Timeout maior para evitar falsos positivos

            // User Agent genérico para evitar bloqueio
            await _page.SetUserAgentAsync("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            _lastInitialization = DateTime.UtcNow;
            _logger?.LogInformation("✅ Navegador iniciado (Sessão Otimizada)!");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "❌ Erro fatal ao iniciar navegador");
            await CleanupAsync();
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // =================================================================================================
    // MÉTODOS PÚBLICOS (Mantidos 100% iguais para não quebrar API/Worker)
    // =================================================================================================

    public async Task<List<Match>> GetLiveMatchesAsync()
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            await page.GoToAsync("https://www.sofascore.com/", new NavigationOptions { Timeout = 60000, WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } });
            
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
                HomeTeam = e.HomeTeam?.Name ?? "N/A",
                AwayTeam = e.AwayTeam?.Name ?? "N/A",
                HomeScore = e.HomeScore?.Current,
                AwayScore = e.AwayScore?.Current,
                Status = e.Status?.Description ?? "N/A",
                StartTimestamp = e.StartTimestamp,
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

    public async Task<MatchEnrichmentData> EnrichSingleMatchAsync(int matchId)
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            var details = await FetchMatchDetailsInternal(page, matchId);
            await Task.Delay(200);
            var stats = await FetchMatchStatisticsInternal(page, matchId);
            await Task.Delay(200);
            var incidents = await FetchMatchIncidentsInternal(page, matchId);

            return new MatchEnrichmentData
            {
                Details = details,
                Statistics = stats,
                Incidents = incidents
            };
        }, $"EnrichSingleMatchAsync({matchId})");
    }

    public async Task<List<Match>> GetMatchesAsync(int tournamentId, int seasonId, int round)
    {
         return await ExecuteWithRetryAsync(async (page) =>
        {
            await page.GoToAsync($"https://www.sofascore.com/tournament/{tournamentId}", new NavigationOptions { Timeout = 60000, WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } });
            
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
             await page.GoToAsync($"https://www.sofascore.com/tournament/{tournamentId}", new NavigationOptions { Timeout = 60000, WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } });

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
                 StartTime = DateTimeOffset.FromUnixTimeSeconds(e.StartTimestamp).UtcDateTime
             }).ToList() ?? new List<Match>();
        }, "GetQualificationMatchesAsync");
    }
    
    public async Task<StandingsTable?> GetStandingsAsync(int tournamentId, int seasonId)
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            await page.GoToAsync($"https://www.sofascore.com/", new NavigationOptions { Timeout = 60000, WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded } });
            var apiUrl = $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/standings/total";
            var json = await FetchJsonFromPage(page, apiUrl);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var response = JsonSerializer.Deserialize<StandingsResponse>(json, options);
            return response?.Standings?.FirstOrDefault(s => s.Type == "total");
        }, $"GetStandingsAsync({tournamentId})");
    }

    // Métodos legados mantidos para compatibilidade
    public async Task<string> GetEventDetailsAsync(int eventId) => await ExecuteWithRetryAsync(p => FetchJsonFromPage(p, $"https://www.sofascore.com/api/v1/event/{eventId}"), "GetEventDetails");
    public async Task<string> GetEventStatisticsAsync(int eventId) => await ExecuteWithRetryAsync(p => FetchJsonFromPage(p, $"https://www.sofascore.com/api/v1/event/{eventId}/statistics"), "GetEventStatistics");


    // =================================================================================================
    // MÉTODOS PRIVADOS (Onde a mágica da estabilidade acontece)
    // =================================================================================================

    private async Task<string> FetchJsonFromPage(IPage page, string url)
    {
        try 
        {
            // Tenta injetar o fetch via JS da página
            return await page.EvaluateFunctionAsync<string>(@"
                async (url) => {
                    const response = await fetch(url, {
                        headers: {
                            'accept': '*/*',
                            'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                        }
                    });
                    if (!response.ok) return null;
                    return await response.text();
                }
            ", url);
        }
        catch (NullReferenceException)
        {
            // CRÍTICO: Se der NullRef aqui, o browser morreu no meio do caminho.
            // Relançamos como Exception para ativar o Retry e reiniciar o browser.
            throw new Exception("Browser crashed/closed during evaluation.");
        }
    }

    private async Task<EventDetail?> FetchMatchDetailsInternal(IPage page, int eventId)
    {
        var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}";
        var json = await FetchJsonFromPage(page, apiUrl);
        if (string.IsNullOrEmpty(json)) return null;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var response = JsonSerializer.Deserialize<EventDetailResponse>(json, options);
        return response?.Event;
    }

    private async Task<StatisticsResponse?> FetchMatchStatisticsInternal(IPage page, int eventId)
    {
        var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}/statistics";
        var json = await FetchJsonFromPage(page, apiUrl);
        if (string.IsNullOrEmpty(json)) return null;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<StatisticsResponse>(json, options);
    }

    private async Task<List<Incident>> FetchMatchIncidentsInternal(IPage page, int eventId)
    {
        var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}/incidents";
        var json = await FetchJsonFromPage(page, apiUrl);
        if (string.IsNullOrEmpty(json)) return null;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var response = JsonSerializer.Deserialize<IncidentsResponse>(json, options);
        return response?.Incidents?.OrderBy(i => i.Time).ThenBy(i => i.AddedTime).ToList() ?? new List<Incident>();
    }

    private async Task EnsureSessionAsync()
    {
        // Reinicia se: Nulo, Desconectado ou Sessão muito longa (liberar RAM)
        bool expired = (DateTime.UtcNow - _lastInitialization).TotalMinutes > SESSION_HEALTH_CHECK_MINUTES;
        
        if (_browser == null || _page == null || !_browser.IsConnected || expired)
        {
            if (expired) _logger?.LogInformation("♻️ Reciclando navegador para liberar memória...");
            else _logger?.LogWarning("⚠️ Navegador instável. Reiniciando...");
            
            await InitializeAsync();
        }
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<IPage, Task<T>> operation, string operationName)
    {
        for (int attempt = 1; attempt <= MAX_RECONNECT_ATTEMPTS; attempt++)
        {
            await _pageSemaphore.WaitAsync();
            try
            {
                await EnsureSessionAsync();
                
                if (_page == null || _page.IsClosed) 
                    throw new Exception("Página fechada inesperadamente.");

                return await operation(_page);
            }
            catch (Exception ex)
            {
                if (attempt < MAX_RECONNECT_ATTEMPTS)
                {
                    _logger?.LogWarning("⚠️ {Operation} falhou (Tentativa {Attempt}). Erro: {Message}", operationName, attempt, ex.Message);
                    await CleanupAsync(); // Força limpeza imediata
                    await Task.Delay(RECONNECT_DELAY_MS);
                }
                else
                {
                    _logger?.LogError(ex, "❌ {Operation} falhou definitivamente.", operationName);
                    throw;
                }
            }
            finally
            {
                _pageSemaphore.Release();
            }
        }
        throw new InvalidOperationException("Unreachable code");
    }

    private async Task CleanupAsync()
    {
        try { if (_page != null) { await _page.CloseAsync(); _page = null; } } catch { }
        try { if (_browser != null) { await _browser.CloseAsync(); _browser = null; } } catch { }
        GC.Collect(); // Força o .NET a limpar a memória
    }
}