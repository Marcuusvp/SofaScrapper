using PuppeteerSharp;
using System.Text.Json;

namespace SofaScoreScraper;

public class SofaScraper
{
    private IBrowser? _browser;
    private IPage? _page;
    private readonly SemaphoreSlim _pageSemaphore = new(1, 1); // Máx 1 páginas simultâneas
    private readonly ILogger<SofaScraper>? _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private DateTime _lastInitialization = DateTime.MinValue;

    //Configurações de resiliência
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
            // Fecha recursos antigos se existirem
            await CleanupAsync();

            _logger?.LogInformation("Baixando Chromium (se necessário)...");

            var browserFetcher = new BrowserFetcher();

            _logger?.LogInformation("Verificando instalação do Chromium...");
            await browserFetcher.DownloadAsync();

            _logger?.LogInformation("Iniciando navegador...");
            _browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                Args = new[] 
                { 
                    "--no-sandbox", 
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-gpu",
                    "--disable-extensions",
                    "--no-first-run",
                    "--no-zygote",
                    "--single-process",
                    "--disable-features=IsolateOrigins,site-per-process",
                    "--js-flags=\"--max-old-space-size=256\""
                }
            });

            _page = await _browser.NewPageAsync();

            // ✅ NOVO: Configura timeout padrão mais alto
            _page.DefaultTimeout = 60000; // 60 segundos

            await _page.SetUserAgentAsync(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36"
            );

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
    public async Task<string> GetRoundEventsAsync(int tournamentId, int seasonId, int round)
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            await _page!.GoToAsync($"https://www.sofascore.com/pt/torneio/futebol/england/premier-league/{tournamentId}");
            await Task.Delay(3000);

            var apiUrl = $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/events/round/{round}";

            return await _page.EvaluateFunctionAsync<string>(@"
                async (url) => {
                    const response = await fetch(url, {
                        headers: {
                            'accept': '*/*',
                            'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                        }
                    });
                    return await response.text();
                }
            ", apiUrl);
        }, $"GetRoundEventsAsync({tournamentId}, {seasonId}, {round})");
    }

    public async Task<string> GetRoundEventsWithSlugAsync(int tournamentId, int seasonId, int round, string slug, string? prefix = null)
    {
        if (_page == null)
            throw new InvalidOperationException("Scraper não foi inicializado. Chame InitializeAsync() primeiro.");

        // Navegar para a página do torneio (estabelece sessão e cookies)
        await _page.GoToAsync($"https://www.sofascore.com/pt/torneio/futebol/europe/uefa-champions-league/{tournamentId}");
        
        // Aguardar página carregar
        await Task.Delay(3000);

        // Construir URL com slug e prefix (para fases de qualificação)
        var apiUrl = prefix != null
            ? $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/events/round/{round}/slug/{slug}/prefix/{prefix}"
            : $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/events/round/{round}/slug/{slug}";
        
        var json = await _page.EvaluateFunctionAsync<string>(@"
            async (url) => {
                const response = await fetch(url, {
                    headers: {
                        'accept': '*/*',
                        'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                    }
                });
                return await response.text();
            }
        ", apiUrl);

        return json;
    }

    public async Task<List<Match>> GetMatchesAsync(int tournamentId, int seasonId, int round)
    {
        var json = await GetRoundEventsAsync(tournamentId, seasonId, round);
        
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        
        var response = JsonSerializer.Deserialize<ApiResponse>(json, options);
        
        return response?.Events?.Select(e => new Match
        {
            Id = e.Id,
            HomeTeam = e.HomeTeam?.Name ?? "N/A",
            AwayTeam = e.AwayTeam?.Name ?? "N/A",
            HomeScore = e.HomeScore?.Current,
            AwayScore = e.AwayScore?.Current,
            Status = e.Status?.Description ?? "N/A",
            StartTime = DateTimeOffset.FromUnixTimeSeconds(e.StartTimestamp).UtcDateTime
        }).ToList() ?? new List<Match>();
    }

    public async Task<List<Match>> GetQualificationMatchesAsync(int tournamentId, int seasonId, int round, string slug, string? prefix = null)
    {
        var json = await GetRoundEventsWithSlugAsync(tournamentId, seasonId, round, slug, prefix);
        
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        
        var response = JsonSerializer.Deserialize<ApiResponse>(json, options);
        
        return response?.Events?.Select(e => new Match
        {
            Id = e.Id,
            HomeTeam = e.HomeTeam?.Name ?? "N/A",
            AwayTeam = e.AwayTeam?.Name ?? "N/A",
            HomeScore = e.HomeScore?.Current,
            AwayScore = e.AwayScore?.Current,
            Status = e.Status?.Description ?? "N/A",
            StartTime = DateTimeOffset.FromUnixTimeSeconds(e.StartTimestamp).DateTime
        }).ToList() ?? new List<Match>();
    }

    public async Task<List<Match>> GetLiveMatchesAsync()
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            await _page!.GoToAsync("https://www.sofascore.com/");
            await Task.Delay(3000);

            var apiUrl = "https://www.sofascore.com/api/v1/sport/football/events/live";

            var json = await _page.EvaluateFunctionAsync<string>(@"
                async (url) => {
                    const response = await fetch(url, {
                        headers: {
                            'accept': '*/*',
                            'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                        }
                    });
                    return await response.text();
                }
            ", apiUrl);

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
                StartTime = DateTimeOffset.FromUnixTimeSeconds(e.StartTimestamp).UtcDateTime
            }).ToList() ?? new List<Match>();
        }, "GetLiveMatchesAsync");
    }

    public async Task DisposeAsync()
    {
        await CleanupAsync();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public async Task<string> GetEventDetailsAsync(int eventId)
    {
        if (_page == null)
            throw new InvalidOperationException("Scraper não foi inicializado.");

        var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}";

        var json = await _page.EvaluateFunctionAsync<string>(@"
            async (url) => {
                const response = await fetch(url, {
                    headers: {
                        'accept': '*/*',
                        'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                    }
                });
                return await response.text();
            }
        ", apiUrl);

        return json;
    }

    public async Task<string> GetEventStatisticsAsync(int eventId)
    {
        if (_page == null)
            throw new InvalidOperationException("Scraper não foi inicializado.");

        var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}/statistics";

        var json = await _page.EvaluateFunctionAsync<string>(@"
            async (url) => {
                const response = await fetch(url, {
                    headers: {
                        'accept': '*/*',
                        'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                    }
                });
                return await response.text();
            }
        ", apiUrl);

        return json;
    }

    public async Task<EventDetail?> GetMatchDetailsAsync(int eventId)
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}";

            var json = await _page!.EvaluateFunctionAsync<string>(@"
                async (url) => {
                    const response = await fetch(url, {
                        headers: {
                            'accept': '*/*',
                            'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                        }
                    });
                    return await response.text();
                }
            ", apiUrl);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var response = JsonSerializer.Deserialize<EventDetailResponse>(json, options);
            return response?.Event;
        }, $"GetMatchDetailsAsync({eventId})");
    }

    public async Task<StatisticsResponse?> GetMatchStatisticsAsync(int eventId)
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}/statistics";

            var json = await _page!.EvaluateFunctionAsync<string>(@"
                async (url) => {
                    const response = await fetch(url, {
                        headers: {
                            'accept': '*/*',
                            'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                        }
                    });
                    return await response.text();
                }
            ", apiUrl);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<StatisticsResponse>(json, options);
        }, $"GetMatchStatisticsAsync({eventId})");
    }

    public async Task<List<Incident>> GetMatchIncidentsAsync(int eventId)
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            var apiUrl = $"https://www.sofascore.com/api/v1/event/{eventId}/incidents";

            var json = await _page!.EvaluateFunctionAsync<string>(@"
                async (url) => {
                    const response = await fetch(url, {
                        headers: {
                            'accept': '*/*',
                            'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                        }
                    });
                    return await response.text();
                }
            ", apiUrl);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var response = JsonSerializer.Deserialize<IncidentsResponse>(json, options);

            return response?.Incidents?
                .OrderBy(i => i.Time)
                .ThenBy(i => i.AddedTime)
                .ToList() ?? new List<Incident>();
        }, $"GetMatchIncidentsAsync({eventId})");
    }
    /// <summary>
    /// ✅ MANTIDO: Método de conveniência para buscar apenas gols
    /// </summary>
    public async Task<List<Incident>> GetMatchGoalsAsync(int eventId)
    {
        var allIncidents = await GetMatchIncidentsAsync(eventId);
        return allIncidents.Where(i => i.IncidentType == "goal").ToList();
    }

    /// <summary>
    /// Verifica se a sessão está saudável
    /// </summary>
    private async Task<bool> IsSessionHealthyAsync()
    {
        try
        {
            // Verifica se os objetos existem
            if (_browser == null || _page == null)
            {
                _logger?.LogWarning("⚠️ Browser ou Page é null");
                return false;
            }

            // Verifica se o browser ainda está conectado
            if (!_browser.IsConnected)
            {
                _logger?.LogWarning("⚠️ Browser desconectado");
                return false;
            }

            // Verifica se a página ainda está válida (tenta uma operação simples)
            var _ = await _page.EvaluateExpressionAsync<string>("'health-check'");

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "⚠️ Health check falhou");
            return false;
        }
    }
    /// <summary>
    /// Garante que a sessão está válida, reconectando se necessário
    /// </summary>
    private async Task EnsureSessionAsync()
    {
        // Se não foi inicializado ainda, inicializa
        if (_browser == null || _page == null)
        {
            _logger?.LogInformation("🔄 Primeira inicialização do scraper");
            await InitializeAsync();
            return;
        }

        // Se a sessão parece estar OK, retorna
        if (await IsSessionHealthyAsync())
        {
            // Verifica se passou muito tempo desde a última inicialização
            var timeSinceInit = DateTime.UtcNow - _lastInitialization;
            if (timeSinceInit.TotalMinutes > SESSION_HEALTH_CHECK_MINUTES)
            {
                _logger?.LogInformation("🔄 Reiniciando sessão preventivamente após {Minutes} minutos", 
                    timeSinceInit.TotalMinutes);
                await InitializeAsync();
            }
            return;
        }

        // Sessão está inválida, tenta reconectar
        _logger?.LogWarning("🔄 Sessão inválida detectada, reconectando...");

        for (int attempt = 1; attempt <= MAX_RECONNECT_ATTEMPTS; attempt++)
        {
            try
            {
                await InitializeAsync();

                if (await IsSessionHealthyAsync())
                {
                    _logger?.LogInformation("✅ Reconexão bem-sucedida na tentativa {Attempt}", attempt);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "❌ Falha na tentativa {Attempt} de reconexão", attempt);

                if (attempt < MAX_RECONNECT_ATTEMPTS)
                {
                    await Task.Delay(RECONNECT_DELAY_MS * attempt);
                }
            }
        }

        throw new InvalidOperationException(
            $"Falha ao reconectar após {MAX_RECONNECT_ATTEMPTS} tentativas"
        );
    }

    /// <summary>
    /// Wrapper para executar operações com retry
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<IPage, Task<T>> operation, 
        string operationName)
    {
        for (int attempt = 1; attempt <= MAX_RECONNECT_ATTEMPTS; attempt++)
        {
            await _pageSemaphore.WaitAsync();
            IPage? page = null;
            
            try
            {
                await EnsureSessionAsync();
                
                // Cria uma página dedicada para esta operação
                page = await _browser!.NewPageAsync();
                page.DefaultTimeout = 60000;
                await page.SetUserAgentAsync(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
                );

                return await operation(page);
            }
            catch (Exception ex) when (attempt < MAX_RECONNECT_ATTEMPTS)
            {
                _logger?.LogWarning(ex, 
                    "⚠️ {Operation} falhou na tentativa {Attempt}, tentando reconectar...",
                    operationName, attempt);

                await Task.Delay(RECONNECT_DELAY_MS);
            }
            finally
            {
                if (page != null)
                {
                    try { await page.CloseAsync(); } 
                    catch { /* ignora */ }
                }
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
            if (finalPage != null)
            {
                try { await finalPage.CloseAsync(); } 
                catch { /* ignora */ }
            }
            _pageSemaphore.Release();
        }
    }
    private async Task CleanupAsync()
    {
        try
        {
            if (_page != null)
            {
                await _page.CloseAsync();
                _page = null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Erro ao fechar página");
        }

        try
        {
            if (_browser != null)
            {
                await _browser.CloseAsync();
                _browser = null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Erro ao fechar browser");
        }
    }

    // Adicione dentro da classe SofaScraper

    public async Task<StandingsTable?> GetStandingsAsync(int tournamentId, int seasonId)
    {
        // Reutiliza a lógica de retry e sessão
        return await ExecuteWithRetryAsync(async (page) =>
        {
            // Navega para a página do torneio para garantir cookies válidos
            await page.GoToAsync($"https://www.sofascore.com/");
            
            var apiUrl = $"https://www.sofascore.com/api/v1/unique-tournament/{tournamentId}/season/{seasonId}/standings/total";

            var json = await _page.EvaluateFunctionAsync<string>(@"
                async (url) => {
                    const response = await fetch(url, {
                        headers: {
                            'accept': '*/*',
                            'x-requested-with': document.querySelector('meta[name=""x-requested-with""]')?.content || ''
                        }
                    });
                    return await response.text();
                }
            ", apiUrl);

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var response = JsonSerializer.Deserialize<StandingsResponse>(json, options);
            
            // Retorna a tabela 'total' (geralmente a primeira)
            return response?.Standings?.FirstOrDefault(s => s.Type == "total");
        }, $"GetStandingsAsync({tournamentId})");
    }

    public async Task<MatchEnrichmentData> EnrichSingleMatchAsync(int matchId)
    {
        return await ExecuteWithRetryAsync(async (page) =>
        {
            // Busca tudo sequencialmente na mesma página
            var details = await GetMatchDetailsAsync(matchId);
            var stats = await GetMatchStatisticsAsync(matchId);
            var incidents = await GetMatchIncidentsAsync(matchId);

            return new MatchEnrichmentData
            {
                Details = details,
                Statistics = stats,
                Incidents = incidents
            };
        }, $"EnrichSingleMatchAsync({matchId})");
    }

}