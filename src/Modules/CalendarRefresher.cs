using System.Diagnostics;
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
/// Built-in data refresher for the calendar module. Reads today's events from
/// Microsoft Graph and writes <c>modules/calendar/data.js</c> for the overlay.
///
/// Two sign-in providers are supported and chosen by the module's auth method
/// (per-user <c>config.json</c> → manifest <c>settings.authMethod</c> → "auto"):
///   • <b>workiq</b> — the WorkIQ CLI (<c>npx @microsoft/workiq call-function</c>),
///     which reuses your existing Windows/WAM M365 sign-in through an app
///     registration that is already approved in locked-down tenants (e.g.
///     microsoft.com, where generic Graph clients need admin consent).
///   • <b>msal</b> — MSAL.NET: the Windows broker (WAM) first, then device code,
///     with a DPAPI token cache for silent background refresh.
/// "auto" tries WorkIQ first and falls back to MSAL.
/// </summary>
internal sealed class CalendarRefresher
{
    // Public client defaults for the MSAL provider. Overridable per-install via
    // the calendar module's settings ("clientId"/"tenant"/"scopes").
    private const string DefaultClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e"; // Microsoft Graph PowerShell
    private const string DefaultTenant = "organizations";
    private static readonly string[] DefaultScopes = { "Calendars.Read" };

    private readonly ModuleManifest _module;
    private readonly string _clientId;
    private readonly string _tenant;
    private readonly string[] _scopes;
    private readonly string _cacheDir;
    private readonly string _authMethod;

    public CalendarRefresher(ModuleManifest module)
    {
        _module = module;
        _cacheDir = module.Dir;
        _clientId = SettingString(module, "clientId") ?? DefaultClientId;
        _tenant = SettingString(module, "tenant") ?? DefaultTenant;
        string[]? scopes = SettingStringArray(module, "scopes");
        _scopes = (scopes is { Length: > 0 }) ? scopes : DefaultScopes;
        _authMethod = ResolveAuthMethod(module);
    }

    /// <summary>
    /// Acquire events (via the configured provider order) and refresh data.js.
    /// </summary>
    /// <param name="interactive">Allow an interactive sign-in (WAM window / device code / WorkIQ login). False for the unattended timer.</param>
    /// <param name="parentWindow">HWND to parent the WAM dialog to (interactive MSAL only).</param>
    public async Task<bool> RefreshAsync(bool interactive, IntPtr parentWindow, TextWriter log)
    {
        (string start, string end) = TodayWindow();

        string[] order = _authMethod switch
        {
            "workiq" => new[] { "workiq" },
            "msal" or "wam" or "graph" => new[] { "msal" },
            _ => new[] { "workiq", "msal" }, // auto
        };

        List<CalEvent>? events = null;
        string? used = null;
        foreach (string provider in order)
        {
            try
            {
                events = provider == "workiq"
                    ? await FetchViaWorkIqAsync(start, end, log)
                    : await FetchViaMsalAsync(start, end, interactive, parentWindow, log);
                if (events is not null) { used = provider; break; }
            }
            catch (Exception ex)
            {
                log.WriteLine($"Calendar: '{provider}' sign-in failed: {ex.Message}");
            }
        }

        if (events is null)
        {
            log.WriteLine(interactive
                ? "Calendar: no sign-in method succeeded. Nothing was changed."
                : "Calendar: no cached sign-in; run 'HtmlWallpaper.exe module enable calendar' to sign in.");
            return false;
        }

        WriteDataJs(events);
        log.WriteLine($"Calendar: wrote {events.Count} event(s) via {used}.");
        return true;
    }

    // ---- Provider: WorkIQ CLI --------------------------------------------------

    private async Task<List<CalEvent>> FetchViaWorkIqAsync(string start, string end, TextWriter log)
    {
        string path =
            "/me/calendarView" +
            $"?startDateTime={start}&endDateTime={end}" +
            "&$select=subject,start,end,isAllDay,isCancelled,isOnlineMeeting,onlineMeeting,location,showAs,webLink" +
            "&$orderby=start/dateTime&$top=100";

        log.WriteLine("Calendar: fetching via WorkIQ (uses your existing Windows M365 sign-in)...");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c npx -y @microsoft/workiq@latest call-function -u \"{path}\"",
            WorkingDirectory = _cacheDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // A login/unattended session may not inherit the interactive PATH, so npx
        // could be missing. Rehydrate PATH from the registry (Machine + User).
        RehydratePath(psi);

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("could not start npx (is Node.js installed?)");
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        string outText = await outTask;
        string errText = await errTask;

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"WorkIQ CLI exit {p.ExitCode}. {Truncate(errText.Trim(), 300)}");

        JsonElement value = ExtractValueArray(outText);
        return ParseValue(value);
    }

    /// <summary>Pull the Graph <c>value</c> array out of WorkIQ's stdout, which may
    /// carry log noise around the JSON and may wrap it under a <c>data</c> node.</summary>
    private static JsonElement ExtractValueArray(string stdout)
    {
        int first = stdout.IndexOf('{');
        int last = stdout.LastIndexOf('}');
        if (first < 0 || last <= first)
            throw new InvalidOperationException("WorkIQ returned no JSON.");
        string json = stdout.Substring(first, last - first + 1);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement node = root;
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("data", out JsonElement data))
            node = data;
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("value", out JsonElement value))
            return value.Clone();
        if (node.ValueKind == JsonValueKind.Array)
            return node.Clone();
        throw new InvalidOperationException("WorkIQ response had no events array.");
    }

    // ---- Provider: MSAL.NET (WAM broker + device code) -------------------------

    private async Task<List<CalEvent>?> FetchViaMsalAsync(string start, string end, bool interactive, IntPtr parentWindow, TextWriter log)
    {
        IPublicClientApplication app = await BuildAppAsync();

        string? token = await GetTokenSilentAsync(app, log);
        if (token is null && interactive)
            token = await GetTokenInteractiveAsync(app, parentWindow, log);
        if (token is null)
            return null; // let the caller fall through / report

        JsonElement value = await GraphGetValueAsync(token, start, end, log);
        return ParseValue(value);
    }

    private async Task<IPublicClientApplication> BuildAppAsync()
    {
        PublicClientApplicationBuilder builder = PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority($"https://login.microsoftonline.com/{_tenant}")
            .WithDefaultRedirectUri()
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows));

        // Opt-in verbose diagnostics (incl. PII) for troubleshooting broker
        // failures: set HTMLWP_MSAL_DEBUG=1. Off by default so tokens/PII are
        // never logged in normal use.
        if (Environment.GetEnvironmentVariable("HTMLWP_MSAL_DEBUG") == "1")
        {
            builder = builder.WithLogging(
                (level, message, _) => Console.Error.WriteLine($"[MSAL {level}] {message}"),
                LogLevel.Verbose, enablePiiLogging: true, enableDefaultPlatformLogging: false);
        }

        IPublicClientApplication app = builder.Build();

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

    private async Task<JsonElement> GraphGetValueAsync(string token, string start, string end, TextWriter log)
    {
        string tz = TimeZoneInfo.Local.Id; // Graph accepts Windows tz IDs
        string url =
            "https://graph.microsoft.com/v1.0/me/calendarView" +
            $"?startDateTime={start}&endDateTime={end}" +
            "&$select=subject,start,end,isAllDay,isCancelled,isOnlineMeeting,onlineMeeting,location,showAs,webLink" +
            "&$orderby=start/dateTime&$top=100";

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Prefer", $"outlook.timezone=\"{tz}\"");

        using HttpResponseMessage resp = await http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Graph returned {(int)resp.StatusCode}. {Truncate(body, 300)}");

        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out JsonElement value))
            return JsonDocument.Parse("[]").RootElement.Clone();
        return value.Clone();
    }

    // ---- Shared parsing --------------------------------------------------------

    private static (string start, string end) TodayWindow()
    {
        DateTime startLocal = DateTime.Today;
        DateTime endLocal = startLocal.AddDays(1);
        string start = startLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        string end = endLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return (start, end);
    }

    private static List<CalEvent> ParseValue(JsonElement value)
    {
        var results = new List<CalEvent>();
        if (value.ValueKind != JsonValueKind.Array) return results;

        foreach (JsonElement e in value.EnumerateArray())
        {
            results.Add(new CalEvent
            {
                Subject = Str(e, "subject") ?? "(no subject)",
                Start = NormalizeGraphTime(e, "start"),
                End = NormalizeGraphTime(e, "end"),
                IsAllDay = Bool(e, "isAllDay"),
                IsCancelled = Bool(e, "isCancelled"),
                IsOnline = Bool(e, "isOnlineMeeting"),
                Location = Nested(e, "location", "displayName"),
                ShowAs = Str(e, "showAs"),
                WebLink = Str(e, "webLink"),
            });
        }
        return results;
    }

    /// <summary>
    /// Graph returns event times as a naive <c>dateTime</c> plus a sibling
    /// <c>timeZone</c> (Windows or "UTC"), which differs by provider (MSAL asks for
    /// local time; WorkIQ returns UTC). Combine them into an absolute ISO-8601
    /// string with offset so the browser's <c>new Date()</c> always renders the
    /// correct local time regardless of which provider produced the data.
    /// </summary>
    private static string? NormalizeGraphTime(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out JsonElement p) || p.ValueKind != JsonValueKind.Object) return null;
        string? dt = p.TryGetProperty("dateTime", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        string? tz = p.TryGetProperty("timeZone", out JsonElement z) && z.ValueKind == JsonValueKind.String ? z.GetString() : null;
        if (string.IsNullOrWhiteSpace(dt)) return null;

        if (!DateTime.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime naive))
            return dt; // hand back the raw string; better than dropping the event time

        naive = DateTime.SpecifyKind(naive, DateTimeKind.Unspecified);
        TimeZoneInfo tzi;
        try
        {
            tzi = string.IsNullOrWhiteSpace(tz) || string.Equals(tz, "UTC", StringComparison.OrdinalIgnoreCase)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(tz);
        }
        catch { tzi = TimeZoneInfo.Utc; }

        TimeSpan offset = tzi.GetUtcOffset(naive);
        var dto = new DateTimeOffset(naive, offset);
        return dto.ToString("o", CultureInfo.InvariantCulture);
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

    // ---- helpers ---------------------------------------------------------------

    private static string ResolveAuthMethod(ModuleManifest m)
    {
        // Per-user override (written by `module enable calendar --auth <method>`)
        // wins over the shipped manifest default.
        try
        {
            string cfg = Path.Combine(m.Dir, "config.json");
            if (File.Exists(cfg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("authMethod", out JsonElement a) &&
                    a.ValueKind == JsonValueKind.String)
                {
                    string? v = a.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim().ToLowerInvariant();
                }
            }
        }
        catch { /* fall through to manifest / default */ }

        return (SettingString(m, "authMethod") ?? "auto").ToLowerInvariant();
    }

    /// <summary>Persist the chosen auth method so the unattended scheduler reuses it.</summary>
    public static void SaveAuthMethod(ModuleManifest m, string method)
    {
        string norm = (method ?? "auto").Trim().ToLowerInvariant();
        string cfg = Path.Combine(m.Dir, "config.json");
        var obj = new Dictionary<string, object?> { ["authMethod"] = norm };
        File.WriteAllText(cfg, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static void RehydratePath(ProcessStartInfo psi)
    {
        string? machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
        string? user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        string current = psi.Environment.TryGetValue("PATH", out string? cur) ? cur ?? "" : Environment.GetEnvironmentVariable("PATH") ?? "";
        string combined = string.Join(";", new[] { current, machine, user }.Where(s => !string.IsNullOrEmpty(s)));
        psi.Environment["PATH"] = combined;
    }

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
        public string? WebLink { get; set; }
    }
}
