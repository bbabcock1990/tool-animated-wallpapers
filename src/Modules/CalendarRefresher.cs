using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace HtmlWallpaper.Modules;

/// <summary>
/// Built-in data refresher for the calendar module. Signs in to Microsoft 365
/// with MSAL — using the Windows broker (WAM) first and falling back to the
/// device-code flow — then reads today's events from Microsoft Graph and writes
/// <c>modules/calendar/data.js</c> for the overlay to render.
/// </summary>
internal sealed class CalendarRefresher
{
    // Public client defaults. Overridable per-install via the calendar module's
    // settings ("clientId"/"tenant"/"scopes") for tenants that require a
    // specific approved app registration.
    private const string DefaultClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e"; // Microsoft Graph PowerShell
    private const string DefaultTenant = "organizations";
    private static readonly string[] DefaultScopes = { "Calendars.Read" };

    private readonly string _clientId;
    private readonly string _tenant;
    private readonly string[] _scopes;
    private readonly string _cacheDir;

    public CalendarRefresher(ModuleManifest module)
    {
        _cacheDir = module.Dir;
        _clientId = SettingString(module, "clientId") ?? DefaultClientId;
        _tenant = SettingString(module, "tenant") ?? DefaultTenant;
        string[]? scopes = SettingStringArray(module, "scopes");
        _scopes = (scopes is { Length: > 0 }) ? scopes : DefaultScopes;
    }

    /// <summary>
    /// Acquire a token and refresh the calendar data file.
    /// </summary>
    /// <param name="interactive">Allow an interactive sign-in (WAM window / device code). Use false for the unattended timer.</param>
    /// <param name="parentWindow">HWND to parent the WAM dialog to (interactive only).</param>
    public async Task<bool> RefreshAsync(bool interactive, IntPtr parentWindow, TextWriter log)
    {
        IPublicClientApplication app = await BuildAppAsync();

        string? token = await GetTokenSilentAsync(app, log);
        if (token is null && interactive)
            token = await GetTokenInteractiveAsync(app, parentWindow, log);

        if (token is null)
        {
            log.WriteLine(interactive
                ? "Calendar: sign-in did not complete."
                : "Calendar: no cached sign-in; run 'HtmlWallpaper.exe module enable calendar' to sign in.");
            return false;
        }

        List<CalEvent> events = await FetchTodayAsync(token, log);
        WriteDataJs(events);
        log.WriteLine($"Calendar: wrote {events.Count} event(s).");
        return true;
    }

    private async Task<IPublicClientApplication> BuildAppAsync()
    {
        IPublicClientApplication app = PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority($"https://login.microsoftonline.com/{_tenant}")
            .WithDefaultRedirectUri()
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            .Build();

        // Persist tokens across runs in a DPAPI-encrypted cache next to the module
        // so the unattended timer can refresh silently after the first sign-in.
        try
        {
            var storage = new StorageCreationPropertiesBuilder("calendar-token.bin", _cacheDir).Build();
            MsalCacheHelper helper = await MsalCacheHelper.CreateAsync(storage);
            helper.RegisterCache(app.UserTokenCache);
        }
        catch { /* in-memory cache still works for this run */ }

        return app;
    }

    private async Task<string?> GetTokenSilentAsync(IPublicClientApplication app, TextWriter log)
    {
        try
        {
            IEnumerable<IAccount> accounts = await app.GetAccountsAsync();
            IAccount? account = accounts.FirstOrDefault();
            if (account is null) return null;
            AuthenticationResult r = await app.AcquireTokenSilent(_scopes, account).ExecuteAsync();
            return r.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
        catch (Exception ex)
        {
            log.WriteLine("Calendar: silent token refresh failed: " + ex.Message);
            return null;
        }
    }

    private async Task<string?> GetTokenInteractiveAsync(IPublicClientApplication app, IntPtr parentWindow, TextWriter log)
    {
        // Primary: Windows broker (WAM). Satisfies Conditional Access token-protection
        // policies that block broker-free public clients.
        try
        {
            log.WriteLine("Calendar: signing in via the Windows broker (a window may open)...");
            AuthenticationResult r = await app.AcquireTokenInteractive(_scopes)
                .WithParentActivityOrWindow(parentWindow)
                .ExecuteAsync();
            return r.AccessToken;
        }
        catch (Exception ex)
        {
            log.WriteLine("Calendar: broker sign-in unavailable (" + ex.Message + "). Falling back to device code.");
        }

        // Fallback: device code. No broker, no browser redirect handling required.
        try
        {
            AuthenticationResult r = await app.AcquireTokenWithDeviceCode(_scopes, dc =>
            {
                log.WriteLine();
                log.WriteLine("=== Microsoft 365 sign-in (device code) ===");
                log.WriteLine(dc.Message);
                log.WriteLine("===========================================");
                log.WriteLine();
                return Task.CompletedTask;
            }).ExecuteAsync();
            return r.AccessToken;
        }
        catch (Exception ex)
        {
            log.WriteLine("Calendar: device-code sign-in failed: " + ex.Message);
            return null;
        }
    }

    private async Task<List<CalEvent>> FetchTodayAsync(string token, TextWriter log)
    {
        var results = new List<CalEvent>();

        DateTime startLocal = DateTime.Today;
        DateTime endLocal = startLocal.AddDays(1);
        string tz = TimeZoneInfo.Local.Id; // Graph accepts Windows tz IDs

        string start = startLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        string end = endLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        string url =
            "https://graph.microsoft.com/v1.0/me/calendarView" +
            $"?startDateTime={start}&endDateTime={end}" +
            "&$select=subject,start,end,isAllDay,isCancelled,isOnlineMeeting,onlineMeeting,location,showAs" +
            "&$orderby=start/dateTime&$top=100";

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Prefer", $"outlook.timezone=\"{tz}\"");

        using HttpResponseMessage resp = await http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            log.WriteLine($"Calendar: Graph returned {(int)resp.StatusCode}. {Truncate(body, 300)}");
            return results;
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out JsonElement value)) return results;

        foreach (JsonElement e in value.EnumerateArray())
        {
            results.Add(new CalEvent
            {
                Subject = Str(e, "subject") ?? "(no subject)",
                Start = Nested(e, "start", "dateTime"),
                End = Nested(e, "end", "dateTime"),
                IsAllDay = Bool(e, "isAllDay"),
                IsCancelled = Bool(e, "isCancelled"),
                IsOnline = Bool(e, "isOnlineMeeting"),
                Location = Nested(e, "location", "displayName"),
                ShowAs = Str(e, "showAs"),
            });
        }
        return results;
    }

    private void WriteDataJs(List<CalEvent> events)
    {
        var payload = new Dictionary<string, object?>
        {
            ["generatedAt"] = DateTimeOffset.Now.ToString("o"),
            ["events"] = events,
        };
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        string js = "/* Generated by HtmlWallpaper calendar module. */\n" +
                    "window.CALENDAR_DATA = " + JsonSerializer.Serialize(payload, opts) + ";\n";
        File.WriteAllText(Path.Combine(_cacheDir, "data.js"), js, new UTF8Encoding(false));
    }

    // ---- small JSON helpers ----
    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static string? Nested(JsonElement e, string prop, string child) =>
        e.TryGetProperty(prop, out JsonElement p) && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty(child, out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

    private static string? SettingString(ModuleManifest m, string key)
    {
        if (m.Settings is { } s && s.ValueKind == JsonValueKind.Object &&
            s.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String)
        {
            string? val = v.GetString();
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
        return null;
    }

    private static string[]? SettingStringArray(ModuleManifest m, string key)
    {
        if (m.Settings is { } s && s.ValueKind == JsonValueKind.Object &&
            s.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
        return null;
    }

    private sealed class CalEvent
    {
        public string Subject { get; set; } = "";
        public string? Start { get; set; }
        public string? End { get; set; }
        public bool IsAllDay { get; set; }
        public bool IsCancelled { get; set; }
        public bool IsOnline { get; set; }
        public string? Location { get; set; }
        public string? ShowAs { get; set; }
    }
}
