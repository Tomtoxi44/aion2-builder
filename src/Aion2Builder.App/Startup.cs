using Aion2Builder.Analysis;
using Aion2Builder.Combat;
using Aion2Builder.GameData;
using Aion2Builder.Model;
using Aion2Builder.Storage;

namespace Aion2Builder.App;

/// <summary>
/// Lancement en parallèle : pendant qu'Avalonia démarre et crée la fenêtre (fil de l'interface), un autre fil lit les données de
/// jeu, les fiches des onglets, les combats du Pet Meter, et analyse le dernier personnage affiché — le même calcul que
/// <c>MainWindow.Recompute</c>, sur les mêmes fichiers. La fenêtre reprend ce travail tel quel ; s'il a échoué ou ne
/// correspond plus (autre fiche, autre mode), elle refait le calcul comme avant.
/// </summary>
static class Startup
{
    /// <summary>Analyse du personnage rouvert, telle que <c>Recompute</c> l'aurait faite.</summary>
    public sealed record Analysis(CharacterSnapshot Character, GameMode Mode, PlayerInputs Inputs,
        List<(string Path, CombatLog Log)> Combats, int Fresh, AdviceReport Report);

    /// <param name="Snapshots">Fiches des onglets lues sans erreur (une fiche illisible est relue par la fenêtre, qui dit pourquoi).</param>
    public sealed record Prepared(GameDatabase Db, PetStore.Saved Pets, Dictionary<string, CharacterSnapshot> Snapshots, Analysis? First);

    static Task<Prepared?>? running;

    /// <summary>Lancé par <c>Program.Main</c>, juste avant Avalonia.</summary>
    public static void Begin() => running = Task.Run(Prepare);

    /// <summary>Le travail du lancement (attendu s'il n'est pas fini), une seule fois ; null s'il n'a pas eu lieu ou a échoué.</summary>
    public static Prepared? Take()
    {
        var task = running;
        running = null;
        return task?.GetAwaiter().GetResult();
    }

    static Prepared? Prepare()
    {
        // Rien d'indispensable ici : en cas d'imprévu, la fenêtre refait tout elle-même et montre l'erreur comme avant.
        try
        {
            var prefs = UiPrefs.Load();
            // Les données de jeu se lisent sur un autre fil pendant que celui-ci lit les fiches et les combats.
            var data = Task.Run(() => GameDataStore.Load());
            var pets = new PetStore().Load();
            var store = new CharacterStore();
            var snapshots = new Dictionary<string, CharacterSnapshot>();
            var order = new List<string>();
            foreach (var path in (prefs.Tabs ?? []).Append(prefs.LastFile).OfType<string>().Distinct())
            {
                if (!File.Exists(path)) continue;
                try
                {
                    snapshots[path] = store.Load(path);
                    order.Add(path);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidDataException) { }
            }
            // Même choix que la fenêtre : le dernier perso affiché, sinon le premier onglet.
            var last = prefs.LastFile is not null && snapshots.ContainsKey(prefs.LastFile) ? prefs.LastFile : order.FirstOrDefault();
            // Ses combats enregistrés, lus d'avance (gardés en mémoire par CombatStore) en attendant les données.
            if (last is not null) new CombatStore().List(Path.GetFileNameWithoutExtension(last));
            GameDatabase db;
            try { db = data.GetAwaiter().GetResult(); }
            catch (InvalidDataException) { return null; } // fichier de données invalide : la fenêtre le signale et prend les données par défaut
            return new Prepared(db, pets, snapshots, last is null ? null : Analyze(db, store, pets, snapshots[last], last, prefs.Mode));
        }
        catch (Exception) { return null; }
    }

    static Analysis? Analyze(GameDatabase db, CharacterStore store, PetStore.Saved pets, CharacterSnapshot c, string path, GameMode mode)
    {
        try
        {
            var key = Path.GetFileNameWithoutExtension(path);
            var combatStore = new CombatStore();
            var fresh = PetMeterFights.Sync(combatStore, key, c.Name, db);
            var combats = combatStore.List(key);
            var inputs = store.LoadInputs(c);
            var report = new Advisor(db).Analyze(c, mode, inputs with { MyPeers = new PeerStore().For(c.ClassCode) }, pets.Collection,
                combats.Select(x => x.Log).ToList());
            return new Analysis(c, mode, inputs, combats, fresh, report);
        }
        catch (Exception) { return null; } // classe inconnue, fichier illisible… : la fenêtre refait le calcul et affiche l'erreur
    }
}
