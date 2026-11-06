using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Aion2Builder.Storage;

namespace Aion2Builder.App;

/// <summary>
/// Relevé HdV du Pet Meter : pendant le jeu, une touche (F9) capture la fenêtre du jeu, et le Pet Meter lance le Builder sans
/// fenêtre pour lire chaque capture (« --hdv-read ») puis noter le tout à la fin (« --hdv-send »). La lecture est celle de
/// l'écran Prix HdV (titres des colonnes, prix unitaire, rang à la couleur du nom). Les objets déjà dans la liste partent ;
/// un nom inconnu (nouvel objet, ou nom mal lu) et un prix refusé attendent dans l'écran Prix HdV, à vérifier avant de noter.
/// Fichiers dans %APPDATA%\Aion2Builder : hdv-relay.json (l'exe de ce Builder et le marché choisi, lus par le Pet Meter),
/// hdv-pending.json (les lignes qui attendent : aussi celles d'une capture lue dans l'écran et pas encore notée).
/// </summary>
static class HdvRelay
{
    public static string PendingFile => Path.Combine(AppPaths.Root, "hdv-pending.json");
    static string RelayFile => Path.Combine(AppPaths.Root, "hdv-relay.json");
    static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    /// <param name="Exe">Ce Builder (assez récent pour lire les relevés) ; le Pet Meter ne lance que celui-là.</param>
    /// <param name="Market">Marché choisi dans l'écran Prix HdV : celui où partent les prix du relevé.</param>
    public sealed record Relay(string? Exe, string? Market, string? Label);

    /// <summary>Résultat d'un envoi, lu par le Pet Meter : notés, en attente dans le Builder, le marché, l'erreur s'il y en a une.</summary>
    public sealed record Sent(int Done, int Pending, string? Market, string? Error, List<string> Refused);

    static Relay? relayCache;

    static Relay LoadRelay()
    {
        if (relayCache is not null) return relayCache;
        try { relayCache = File.Exists(RelayFile) ? JsonSerializer.Deserialize<Relay>(File.ReadAllText(RelayFile), Json) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return relayCache ??= new Relay(null, null, null);
    }

    static void SaveRelay(Relay relay)
    {
        if (relay == LoadRelay()) return;  // écrit seulement quand il change (l'écran Prix HdV le note à chaque affichage)
        relayCache = relay;
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(RelayFile, JsonSerializer.Serialize(relay, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Au lancement : le Pet Meter saura où trouver ce Builder.</summary>
    public static void NoteExe()
    {
        if (Environment.ProcessPath is { } exe) SaveRelay(LoadRelay() with { Exe = exe });
    }

    /// <summary>Marché montré dans l'écran Prix HdV : le relevé du Pet Meter y note ses prix.</summary>
    public static void NoteMarket(string code, string label) => SaveRelay(LoadRelay() with { Market = code, Label = label });

    /// <summary>« --hdv-read capture.png lu.json » : les lignes lues (ou le problème), comme dans l'écran Prix HdV.</summary>
    /// <summary>
    /// Niveau de la lecture, renvoyé au Pet Meter avec chaque lecture : 2 = les pièces d'équipement du Marché (nom et « Niv.
    /// d'objet » sur deux lignes, v1.1.121). Un Builder plus ancien ne le renvoie pas : le Pet Meter demande de le relancer.
    /// </summary>
    const int Reader = 2;

    public static void Read(string image, string output)
    {
        object result;
        try
        {
            // Image doublée, comme dans l'écran : sur un écran 1080p, le texte du Marché est trop fin pour l'OCR.
            var words = ScreenshotOcr.Words(image, InGameStatsReader.MinOcrSize, minScale: 2, colors: true).GetAwaiter().GetResult();
            var lines = MarketShotReader.Read(words, out var problem);
            result = new { problem = problem is null ? null : MarketShotReader.NoTitles, lines, reader = Reader };
        }
        catch (Exception ex)  // processus lancé par le Pet Meter : il lit l'erreur dans le fichier
        {
            result = new { problem = $"lecture impossible ({ex.Message})", lines = Array.Empty<MarketShotLine>(), reader = Reader };
        }
        File.WriteAllText(output, JsonSerializer.Serialize(result, Json));
    }

    /// <summary>« --hdv-send lignes.json envoi.json » : note les lignes du relevé (la dernière lecture d'un objet l'emporte).</summary>
    public static void Send(string input, string output)
    {
        Sent sent;
        try { sent = SendAsync(JsonSerializer.Deserialize<List<MarketShotLine>>(File.ReadAllText(input), Json) ?? []).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            sent = new Sent(0, 0, null, $"relevé illisible ({ex.Message})", []);
        }
        File.WriteAllText(output, JsonSerializer.Serialize(sent, Json));
    }

    static async Task<Sent> SendAsync(List<MarketShotLine> read)
    {
        var lines = read.Where(l => l.Name.Trim().Length > 0 && l.Price > 0).GroupBy(l => Market.Fold(l.Name)).Select(g => g.Last()).ToList();
        if (lines.Count == 0) return new Sent(0, 0, null, null, []);
        // La clé du Builder, sinon celle du Pet Meter (comme l'écran Prix HdV).
        var keys = new[] { new PetStore().Load().Key, PetStore.PetMeterKey() }.Where(k => k is { Length: > 0 }).Distinct().ToList();
        MarketData? data = null;
        string? key = null, error = null;
        foreach (var k in keys)
        {
            try { data = await Market.Load(k!); key = k; break; }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized) { error = "clé Aion 2 Todo refusée"; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { error = $"Aion 2 Todo injoignable ({ex.Message})"; break; }
        }
        if (key is null || data is null)
        {
            AddPending(lines);
            return new Sent(0, lines.Count, null, error ?? "il faut ta clé Aion 2 Todo (Builder, écran Familiers)", []);
        }
        var relay = LoadRelay();
        var known = data.Markets?.Select(m => m.Code).ToList() ?? [];
        var market = known.FirstOrDefault(c => c.Equals(relay.Market, StringComparison.OrdinalIgnoreCase)) ?? data.DefaultMarket ?? "*eu";
        var pending = new List<MarketShotLine>();
        var refused = new List<string>();
        var done = 0;
        foreach (var line in lines)
        {
            // Nom inconnu : peut-être mal lu ; il attend dans le Builder plutôt que d'entrer dans la liste de tous sous ce nom.
            if (data.Items.FirstOrDefault(i => Market.Fold(i.Name) == Market.Fold(line.Name)) is not { } item) { pending.Add(line); continue; }
            string? e;
            try { e = await Market.Add(key, item.Key, item.Name, line.Price, market); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { e = $"Aion 2 Todo injoignable ({ex.Message})"; }
            if (e is null) done++;
            else { pending.Add(line); refused.Add($"{item.Name} : {e}"); }
        }
        AddPending(pending);
        return new Sent(done, pending.Count, data.Label(market), null, refused);
    }

    /// <summary>Lignes qui attendent dans l'écran Prix HdV ; null s'il n'y en a pas (ou si le fichier est illisible).</summary>
    public static List<MarketShotLine>? LoadPending()
    {
        try { return File.Exists(PendingFile) ? JsonSerializer.Deserialize<List<MarketShotLine>>(File.ReadAllText(PendingFile), Json) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Remplace les lignes qui attendent (aucune : le fichier part).</summary>
    public static void SavePending(IReadOnlyList<MarketShotLine>? lines)
    {
        try
        {
            if (lines is not { Count: > 0 }) { File.Delete(PendingFile); return; }
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(PendingFile, JsonSerializer.Serialize(lines, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static void AddPending(List<MarketShotLine> lines)
    {
        if (lines.Count == 0) return;
        SavePending((LoadPending() ?? []).Concat(lines).GroupBy(l => Market.Fold(l.Name)).Select(g => g.Last()).ToList());
    }
}
