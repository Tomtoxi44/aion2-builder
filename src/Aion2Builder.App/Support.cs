using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aion2Builder.GameData;
using Aion2Builder.Import;

namespace Aion2Builder.App;

/// <summary>
/// Journal d'erreurs du Builder (%APPDATA%\Aion2Builder\journal.log, 256 Ko au plus, la moitié la plus récente gardée) :
/// seulement les erreurs et quelques étapes, jamais ce que tu fais. Il part avec « Aide → Signaler un bug… », anonymisé.
/// </summary>
static class Journal
{
    const int Max = 256 * 1024;
    static readonly Lock Gate = new();
    static string PathOf => Path.Combine(AppPaths.Root, "journal.log");

    /// <summary>Erreurs non rattrapées (fil de l'interface, tâches, domaine) notées avant que l'appli ne s'arrête.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("plantage : " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Write("tâche : " + e.Exception.GetBaseException());
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) => Write("interface : " + e.Exception);
    }

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var info = new FileInfo(PathOf);
                if (info.Exists && info.Length > Max)
                {
                    var text = File.ReadAllText(PathOf);
                    File.WriteAllText(PathOf, text[(text.Length / 2)..]);
                }
                File.AppendAllText(PathOf, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Updater.Current?.ToString(3) ?? "dev"} {line}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* le journal ne doit rien casser */ }
    }

    public static string Tail(int max = 120_000)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(PathOf)) return "";
                var text = File.ReadAllText(PathOf);
                return text.Length > max ? text[^max..] : text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}

/// <summary>
/// « Aide → Signaler un bug… » : ta description, la version, Windows, les réglages d'affichage et la fin du journal, sans
/// noms de persos ni ton nom Windows, vers Aion 2 Todo avec ta clé ; sans clé ou sans réseau, une copie dans
/// %APPDATA%\Aion2Builder\rapports.
/// </summary>
static class BugReports
{
    public static object Build(string text, UiPrefs prefs, IEnumerable<string?> names)
    {
        var head = $"réglages : {SettingsSync.Pick(prefs).ToJsonString()}\nécran : {prefs.Screen}\n\n";
        return new
        {
            app = "builder",
            version = Updater.Current?.ToString(3) ?? "dev",
            text = text.Trim() is { Length: > 4000 } t ? t[..4000] : text.Trim(),
            log = Anonymize(head + Journal.Tail(), names.Append(prefs.Name)),
            system = $"{RuntimeInformation.OSDescription} · {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSArchitecture}",
        };
    }

    /// <summary>(true, n°) une fois sur Aion 2 Todo ; (false, chemin) d'une copie sur ce PC sinon.</summary>
    public static async Task<(bool Sent, string What)> Send(object report, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                using var http = Client(key);
                using var response = await http.PostAsJsonAsync("/api/sync/bug", report);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    return (true, doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "?" : "?");
                }
                Journal.Write($"signalement : HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                Journal.Write("signalement : " + ex.Message);
            }
        }
        var folder = Directory.CreateDirectory(Path.Combine(AppPaths.Root, "rapports")).FullName;
        var path = Path.Combine(folder, $"bug-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, GameDataStore.Json));
        return (false, path);
    }

    /// <summary>Retire ton dossier et ton nom Windows, et les noms des persos (fichiers ouverts, combats, ta saisie).</summary>
    public static string Anonymize(string text, IEnumerable<string?> names)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 3) text = text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (Environment.UserName is { Length: >= 3 } user)
            text = Regex.Replace(text, $@"\b{Regex.Escape(user)}\b", "<utilisateur>", RegexOptions.IgnoreCase);
        var i = 0;
        foreach (var name in names.OfType<string>().Select(n => n.Trim()).Where(n => n.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(n => n.Length))
            text = Regex.Replace(text, Regex.Escape(name), $"<perso {++i}>", RegexOptions.IgnoreCase);
        return text;
    }

    internal static HttpClient Client(string key)
    {
        var http = new HttpClient { BaseAddress = new Uri(PetSyncClient.Site), Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Aion2Builder/{Updater.Current?.ToString(3) ?? "dev"} (+https://github.com/Tomtoxi44/aion2-builder)");
        http.DefaultRequestHeaders.Authorization = new("Bearer", key.Trim());
        return http;
    }
}

/// <summary>
/// Réglages d'affichage suivis entre tes PC par Aion 2 Todo (même clé) : le plus récent gagne. Pas les onglets ni le
/// dernier fichier (chemins propres à chaque PC), ni l'écran ouvert. État local : %APPDATA%\Aion2Builder\settings-sync.json.
/// </summary>
static class SettingsSync
{
    static readonly string[] Keys =
    [
        "mode", "accent", "autoUpdate", "autoInstall", "inspectorWidth", "inspectorHidden", "autoRefreshHours", "mine", "bottomHidden",
        "bottomHeight", "bottomTab", "railOrder", "gearPage", "skillPage", "buildPage", "sideHidden", "detailsByNeed",
    ];
    const string Epoch = "1970-01-01T00:00:00Z";
    static string StatePath => Path.Combine(AppPaths.Root, "settings-sync.json");

    sealed record State(string Changed, string? Hash);

    /// <summary>Réglages enregistrés dans ui.json (n'importe quel fil) : la fenêtre décide s'il faut les envoyer.</summary>
    public static event Action? Saved;
    public static void OnSaved() => Saved?.Invoke();

    public static JsonObject Pick(UiPrefs prefs)
    {
        var all = JsonSerializer.SerializeToNode(prefs, GameDataStore.Json)!.AsObject();
        var picked = new JsonObject();
        foreach (var k in Keys) picked[k] = all[k]?.DeepClone();
        return picked;
    }

    static string Digest(UiPrefs prefs) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Pick(prefs).ToJsonString())))[..16];

    static State Load()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), GameDataStore.Json) ?? new(Epoch, null) : new(Epoch, null); }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(Epoch, null); }
    }

    static void Save(State s)
    {
        try { Directory.CreateDirectory(AppPaths.Root); File.WriteAllText(StatePath, JsonSerializer.Serialize(s, GameDataStore.Json)); }
        catch (IOException) { /* refait au prochain changement */ }
    }

    /// <summary>
    /// Réglages pas encore envoyés tels quels : leur date (la première fois : 1970, pour que les réglages déjà sur le site
    /// gardent la main) et leur empreinte ; null s'ils sont déjà sur le site. Noté seulement une fois l'envoi réussi.
    /// </summary>
    public static (string Updated, JsonObject Settings, string Digest)? Changed(UiPrefs prefs)
    {
        var state = Load();
        var digest = Digest(prefs);
        if (digest == state.Hash) return null;
        return (state.Hash is null ? Epoch : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), Pick(prefs), digest);
    }

    /// <summary>Envoie ; renvoie les réglages du site s'ils sont plus récents (ton autre PC a changé quelque chose depuis).</summary>
    public static async Task<JsonObject?> Push(string key, (string Updated, JsonObject Settings, string Digest) change)
    {
        try
        {
            using var http = BugReports.Client(key);
            using var response = await http.PutAsJsonAsync("/api/sync/settings/builder", new { updated = change.Updated, settings = change.Settings });
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // Le site a gardé les siens (pas plus vieux) : repris s'ils sont plus récents que les tiens ; sinon chacun garde les siens
            // jusqu'au prochain changement, sans renvoi à chaque enregistrement.
            var remote = doc.RootElement.TryGetProperty("kept", out var kept) && kept.GetBoolean() ? await Pull(key) : null;
            if (remote is null) Save(new(change.Updated, change.Digest));
            return remote;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Journal.Write("réglages : " + ex.Message);
            return null;
        }
    }

    /// <summary>Réglages du site plus récents que les tiens ici (null : rien de plus récent, pas de clé, hors ligne).</summary>
    public static async Task<JsonObject?> Pull(string key)
    {
        try
        {
            using var http = BugReports.Client(key);
            var root = JsonNode.Parse(await http.GetStringAsync("/api/sync/settings/builder"))?.AsObject();
            if (root?["updated"]?.GetValue<string>() is not { } updated || root["settings"] is not JsonObject settings) return null;
            if (!DateTime.TryParse(updated, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var remote)
                || !DateTime.TryParse(Load().Changed, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var local)
                || remote <= local)
                return null;
            settings["$updated"] = updated;
            return settings;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            Journal.Write("réglages : " + ex.Message);
            return null;
        }
    }

    /// <summary>Tes réglages avec ceux du site par-dessus ; noté comme à jour (pas de renvoi).</summary>
    public static UiPrefs Apply(UiPrefs prefs, JsonObject remote)
    {
        var all = JsonSerializer.SerializeToNode(prefs, GameDataStore.Json)!.AsObject();
        foreach (var k in Keys)
            if (remote.ContainsKey(k)) all[k] = remote[k]?.DeepClone();
        UiPrefs merged;
        try { merged = all.Deserialize<UiPrefs>(GameDataStore.Json) ?? prefs; }
        catch (JsonException) { return prefs; }
        Save(new(remote["$updated"]?.GetValue<string>() ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), Digest(merged)));
        return merged;
    }
}
