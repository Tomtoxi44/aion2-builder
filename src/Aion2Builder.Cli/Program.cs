using System.Text;
using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.Cli;
using Aion2Builder.GameData;
using Aion2Builder.Import;
using Aion2Builder.Model;
using Aion2Builder.Storage;

Console.OutputEncoding = Encoding.UTF8;

var cli = Args.Parse(args);
try
{
    return cli.Command switch
    {
        "search" => await Search(cli),
        "import" => await Import(cli),
        "import-raw" => ImportRaw(cli),
        "advise" or "conseils" => await Advise(cli),
        "list" => List(),
        "data" => Data(cli),
        _ => Help(),
    };
}
catch (Exception ex) when (ex is ArmoryException or InvalidDataException or InvalidOperationException or FileNotFoundException)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine(ex.Message);
    Console.ResetColor();
    return 1;
}

static int Help()
{
    Console.WriteLine("""
        Aion 2 Builder — conseils de build par personnage (JcE / JcJ)

          search <nom> [--server <serveur>] [--region eu]     Cherche un personnage sur le site officiel
          import <nom> [--server <serveur>] [--region eu]     Importe et enregistre le personnage
                 [--raw <dossier>]                            (garde aussi les réponses brutes du site)
          import-raw <dossier> [--region eu]                  Réimporte des réponses brutes sauvegardées
          advise <nom|fichier.json> [--server <serveur>]      Analyse et conseils
                 [--mode pve|pvp|both] [--refresh] [--json]   (importe le personnage s'il n'est pas enregistré)
          list                                                Personnages enregistrés
          data [export]                                       Fichiers de données (poids, paliers…) modifiables

        Régions : eu, naw, nae, la, as. Données : %APPDATA%\Aion2Builder
        """);
    return 0;
}

static async Task<int> Search(Args cli)
{
    var name = cli.Positional(0) ?? throw new InvalidOperationException("Précise un nom : search <nom>");
    using var client = ArmoryClient.Create();
    var db = GameDataStore.Load();
    var hits = await client.Search(cli.Region, name, cli.Get("server"));
    if (hits.Count == 0) { Console.WriteLine("Aucun résultat."); return 0; }
    foreach (var h in hits)
        Console.WriteLine($"{h.Name,-20} niv. {h.Level,-3} {(h.ClassCode is null ? "?" : db.FindClass(h.ClassCode)?.Name),-14} {h.ServerName,-14} {h.Race}");
    return 0;
}

static async Task<int> Import(Args cli)
{
    var name = cli.Positional(0) ?? throw new InvalidOperationException("Précise un nom : import <nom> --server <serveur>");
    var db = GameDataStore.Load();
    var (snapshot, raw) = await Fetch(db, cli, name);
    if (cli.Get("raw") is { } rawDir) { raw.SaveTo(rawDir); Console.WriteLine($"Réponses brutes : {Path.GetFullPath(rawDir)}"); }
    var path = new CharacterStore().Save(snapshot);
    PrintImported(snapshot, db, path);
    return 0;
}

static int ImportRaw(Args cli)
{
    var dir = cli.Positional(0) ?? throw new InvalidOperationException("Précise le dossier : import-raw <dossier>");
    var db = GameDataStore.Load();
    var snapshot = ArmoryMapper.Map(RawCharacter.LoadFrom(dir, cli.Get("region")), db);
    var path = new CharacterStore().Save(snapshot);
    PrintImported(snapshot, db, path);
    return 0;
}

static async Task<int> Advise(Args cli)
{
    var target = cli.Positional(0) ?? throw new InvalidOperationException("Précise un personnage : advise <nom> [--server <serveur>]");
    var db = GameDataStore.Load();
    var store = new CharacterStore();
    CharacterSnapshot snapshot;
    if (File.Exists(target)) snapshot = store.Load(target);
    else if (!cli.Has("refresh") && store.Find(target, cli.Get("server")) is { } saved) snapshot = store.Load(saved.Path);
    else
    {
        (snapshot, _) = await Fetch(db, cli, target);
        store.Save(snapshot);
    }

    var modes = (cli.Get("mode") ?? "pve").ToLowerInvariant() switch
    {
        "pvp" or "jcj" => new[] { GameMode.PvP },
        "both" or "les2" => [GameMode.PvE, GameMode.PvP],
        _ => [GameMode.PvE],
    };
    var advisor = new Advisor(db);
    var reports = modes.Select(m => advisor.Analyze(snapshot, m)).ToList();
    if (cli.Has("json"))
    {
        Console.WriteLine(JsonSerializer.Serialize<object>(reports.Count == 1 ? reports[0] : reports, GameDataStore.Json));
        return 0;
    }
    foreach (var r in reports) new ReportPrinter(db).Print(r);
    return 0;
}

static int List()
{
    var list = new CharacterStore().List();
    if (list.Count == 0) { Console.WriteLine("Aucun personnage enregistré. Utilise : import <nom> --server <serveur>"); return 0; }
    var db = GameDataStore.Load();
    foreach (var c in list)
        Console.WriteLine($"{c.Name,-20} {db.FindClass(c.ClassCode)?.Name,-14} niv. {c.Level,-3} NO {c.ItemLevel,-5} {c.ServerName} ({c.Region})  lu le {c.FetchedAtUtc.ToLocalTime():dd/MM HH:mm}");
    return 0;
}

static int Data(Args cli)
{
    if (cli.Positional(0) == "export")
    {
        var written = GameDataStore.ExportDefaults(overwrite: cli.Has("force"));
        Console.WriteLine(written.Count == 0 ? "Fichiers déjà présents (ajoute --force pour les remplacer)." : string.Join(Environment.NewLine, written.Select(w => $"Écrit : {w}")));
    }
    Console.WriteLine($"Dossier des données : {AppPaths.DataDir}");
    foreach (var (file, overridden) in GameDataStore.Sources())
        Console.WriteLine($"  {file,-18} {(overridden ? "personnalisé" : "par défaut (intégré)")}");
    Console.WriteLine("Un fichier présent dans ce dossier remplace la version intégrée. « data export » y copie les valeurs par défaut.");
    return 0;
}

static async Task<(CharacterSnapshot, RawCharacter)> Fetch(GameDatabase db, Args cli, string name)
{
    using var client = ArmoryClient.Create();
    var progress = new Progress<string>(m => Console.Error.Write($"\r{m,-40}"));
    var result = await client.FetchByName(db, cli.Region, name, cli.Get("server"), progress);
    Console.Error.Write($"\r{new string(' ', 40)}\r");
    return result;
}

static void PrintImported(CharacterSnapshot s, GameDatabase db, string path)
{
    Console.WriteLine($"Importé : {s.Name} — {db.FindClass(s.ClassCode)?.Name} niv. {s.Level}, niveau d'objet {s.ItemLevel}, PC {s.CombatPower}");
    Console.WriteLine($"  {s.Equipment.Count} pièces ({s.Equipment.Count(i => i.HasDetail)} détaillées), {s.Skills.Count} compétences, " +
                      $"{s.Boards.Count(b => b.Nodes.Count > 0)}/{s.Boards.Count} plateaux Daevanion détaillés");
    Console.WriteLine($"  Enregistré : {path}");
}

namespace Aion2Builder.Cli
{
    /// <summary>Arguments : commande, valeurs positionnelles et options --clé [valeur].</summary>
    sealed class Args
    {
        readonly List<string> positional = [];
        readonly Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);
        public string Command { get; private init; } = "";

        public static Args Parse(string[] args)
        {
            var result = new Args { Command = args.Length > 0 ? args[0].ToLowerInvariant() : "" };
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    var key = args[i][2..];
                    var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
                    result.options[key] = value;
                }
                else result.positional.Add(args[i]);
            }
            return result;
        }

        public string? Positional(int i) => i < positional.Count ? positional[i] : null;
        public string? Get(string key) => options.GetValueOrDefault(key);
        public bool Has(string key) => options.ContainsKey(key);
        public string Region => Get("region") ?? "eu";
    }
}
