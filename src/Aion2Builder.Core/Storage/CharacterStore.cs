using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Storage;

public sealed record SavedCharacter(string Key, string Name, string ServerName, string Region, string ClassCode, int Level, int ItemLevel, DateTime FetchedAtUtc, string Path);

/// <summary>
/// Un import, pour suivre la progression : niveau, niveau d'objet, puissance de combat, et de quoi dire ce qui a changé
/// d'un import à l'autre (pièces, niveaux de compétences, cases Daevanion ouvertes), avec les notes calculées par mode.
/// </summary>
public sealed record ProgressPoint(DateTime FetchedAtUtc, int Level, int ItemLevel, int CombatPower)
{
    /// <summary>Notes calculées pour cet import, par mode (« PvE », « PvP ») ; mises à jour au dernier calcul.</summary>
    public Dictionary<string, AreaScores>? Scores { get; init; }
    /// <summary>Emplacement → « nom +enchantement (grade) ».</summary>
    public Dictionary<string, string>? Gear { get; init; }
    /// <summary>« nom|catégorie » → niveau total (compétences apprises).</summary>
    public Dictionary<string, int>? Skills { get; init; }
    /// <summary>Plateau Daevanion → cases ouvertes.</summary>
    public Dictionary<string, int>? Boards { get; init; }

    public static ProgressPoint Of(CharacterSnapshot c) => new(c.FetchedAtUtc, c.Level, c.ItemLevel, c.CombatPower)
    {
        Gear = c.Equipment.GroupBy(i => i.Slot).ToDictionary(g => g.Key, g => $"{g.First().Name} +{g.First().EnchantLevel} ({g.First().Grade})"),
        Skills = c.Skills.Where(s => s.Acquired).GroupBy(s => $"{s.Name}|{s.Category}").ToDictionary(g => g.Key, g => g.First().Level),
        Boards = c.Boards.Where(b => b.Unlocked).GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First().OpenNodes),
    };

    /// <summary>Même état que l'autre point (hors date et notes).</summary>
    public bool SameState(ProgressPoint o) =>
        Level == o.Level && ItemLevel == o.ItemLevel && CombatPower == o.CombatPower
        && Same(Gear, o.Gear) && Same(Skills, o.Skills) && Same(Boards, o.Boards);

    static bool Same<T>(Dictionary<string, T>? a, Dictionary<string, T>? b) =>
        a is null || b is null ? a is null == b is null : a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && Equals(v, kv.Value));

    /// <summary>Ce qui a changé depuis le point précédent, en phrases courtes (pièces, compétences, plateaux).</summary>
    public List<string> ChangesSince(ProgressPoint previous, Func<string, string> slotName)
    {
        var changes = new List<string>();
        if (Level != previous.Level) changes.Add($"niveau {previous.Level} → {Level}");
        if (Gear is not null && previous.Gear is not null)
            foreach (var slot in Gear.Keys.Union(previous.Gear.Keys))
            {
                var before = previous.Gear.GetValueOrDefault(slot);
                var after = Gear.GetValueOrDefault(slot);
                if (before != after) changes.Add($"{slotName(slot)} : {before ?? "vide"} → {after ?? "vide"}");
            }
        if (Skills is not null && previous.Skills is not null)
            foreach (var key in Skills.Keys.Union(previous.Skills.Keys))
            {
                var before = previous.Skills.GetValueOrDefault(key);
                var after = Skills.GetValueOrDefault(key);
                if (before != after) changes.Add($"{key.Split('|')[0]} {(before == 0 ? "apprise" : before.ToString())} → {after}");
            }
        if (Boards is not null && previous.Boards is not null)
            foreach (var (board, open) in Boards)
            {
                var delta = open - previous.Boards.GetValueOrDefault(board);
                if (delta != 0) changes.Add($"Daevanion {board} : {(delta > 0 ? "+" : "")}{delta} {(Math.Abs(delta) <= 1 ? "case" : "cases")}");
            }
        return changes;
    }
}

/// <summary>Personnages importés, un fichier JSON chacun, pour travailler hors ligne et comparer dans le temps.</summary>
public sealed class CharacterStore(string? dir = null)
{
    public string Directory { get; } = dir ?? AppPaths.CharactersDir;

    public string Save(CharacterSnapshot snapshot)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"{Sanitize(snapshot.FileKey)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, GameDataStore.Json));
        AddProgress(snapshot);
        return path;
    }

    string ProgressPath(CharacterSnapshot c) => Path.Combine(Directory, "history", $"{Sanitize(c.FileKey)}.progress.json");

    /// <summary>
    /// Imports successifs du personnage, du plus ancien au plus récent par date des données (un point par import qui change
    /// quelque chose). Rangés par date, pas par ordre d'écriture : rouvrir un ancien fichier ajoutait un point daté d'avant
    /// à la fin, et la progression comparait alors des imports dans le désordre.
    /// </summary>
    public List<ProgressPoint> Progress(CharacterSnapshot c)
    {
        var path = ProgressPath(c);
        try
        {
            var points = File.Exists(path) ? JsonSerializer.Deserialize<List<ProgressPoint>>(File.ReadAllText(path), GameDataStore.Json) ?? [] : [];
            return points.OrderBy(p => p.FetchedAtUtc).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return []; }
    }

    void AddProgress(CharacterSnapshot c)
    {
        var points = Progress(c);
        var point = ProgressPoint.Of(c);
        if (points.Any(p => p.FetchedAtUtc == point.FetchedAtUtc)) return; // déjà là
        if (points.LastOrDefault(p => p.FetchedAtUtc < point.FetchedAtUtc) is { } before && before.SameState(point)) return; // rien de neuf
        points.Add(point);
        SaveProgress(c, points.OrderBy(p => p.FetchedAtUtc).ToList());
    }

    /// <summary>Notes calculées pour l'import en cours (un mode à la fois), gardées dans son point de progression.</summary>
    public void RecordScores(CharacterSnapshot c, GameMode mode, AreaScores scores)
    {
        var points = Progress(c);
        var index = points.FindLastIndex(p => p.FetchedAtUtc == c.FetchedAtUtc);
        if (index < 0)
        {
            points = points.Append(ProgressPoint.Of(c)).OrderBy(p => p.FetchedAtUtc).ToList();
            index = points.FindLastIndex(p => p.FetchedAtUtc == c.FetchedAtUtc);
        }
        var key = mode.ToString();
        var current = points[index].Scores ?? [];
        if (current.TryGetValue(key, out var known) && known == scores) return;
        points[index] = points[index] with { Scores = new Dictionary<string, AreaScores>(current) { [key] = scores } };
        SaveProgress(c, points);
    }

    void SaveProgress(CharacterSnapshot c, List<ProgressPoint> points)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(ProgressPath(c))!);
            File.WriteAllText(ProgressPath(c), JsonSerializer.Serialize(points.TakeLast(200).ToList(), GameDataStore.Json));
        }
        catch (IOException) { /* historique non vital */ }
    }

    public CharacterSnapshot Load(string path) =>
        JsonSerializer.Deserialize<CharacterSnapshot>(File.ReadAllText(path), GameDataStore.Json)
        ?? throw new InvalidDataException($"{path} est vide.");

    public List<SavedCharacter> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var list = new List<SavedCharacter>();
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json"))
        {
            try
            {
                var c = Load(path);
                list.Add(new SavedCharacter(Path.GetFileNameWithoutExtension(path), c.Name, c.ServerName, c.Region, c.ClassCode,
                    c.Level, c.ItemLevel, c.FetchedAtUtc, path));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                // Fichier abîmé ou d'un ancien format : ignoré.
            }
        }
        return list.OrderByDescending(c => c.FetchedAtUtc).ToList();
    }

    /// <summary>Retrouve un personnage enregistré par nom (et serveur si précisé).</summary>
    public SavedCharacter? Find(string name, string? server = null) =>
        List().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                   && (server is null || c.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase)));

    string InputsPath(CharacterSnapshot c) => Path.Combine(Directory, "inputs", $"{Sanitize(c.FileKey)}.json");

    /// <summary>Points non dépensés déclarés pour ce personnage (zéro si rien n'a été saisi).</summary>
    public PlayerInputs LoadInputs(CharacterSnapshot c)
    {
        var path = InputsPath(c);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<PlayerInputs>(File.ReadAllText(path), GameDataStore.Json) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(); }
    }

    public void SaveInputs(CharacterSnapshot c, PlayerInputs inputs)
    {
        var path = InputsPath(c);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(inputs, GameDataStore.Json));
    }

    // Historique de consultation : dernière ouverture de chaque personnage (clé = nom du fichier).
    string ViewsPath => Path.Combine(Directory, "history", "views.json");

    Dictionary<string, DateTime> LoadViews()
    {
        try { return File.Exists(ViewsPath) ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(ViewsPath)) ?? [] : []; }
        catch (Exception ex) when (ex is JsonException or IOException) { return []; }
    }

    public void MarkViewed(string path)
    {
        var views = LoadViews();
        views[Path.GetFileName(path)] = DateTime.UtcNow;
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(ViewsPath)!);
            File.WriteAllText(ViewsPath, JsonSerializer.Serialize(views));
        }
        catch (IOException) { /* historique non vital */ }
    }

    /// <summary>Personnages consultés, du plus récent au plus ancien ; un personnage jamais rouvert date de son import.</summary>
    public List<(SavedCharacter Character, DateTime ViewedAtUtc)> History()
    {
        var views = LoadViews();
        return List()
            .Select(c => (c, views.TryGetValue(Path.GetFileName(c.Path), out var at) ? at : c.FetchedAtUtc))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }

    static string Sanitize(string key) => string.Concat(key.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
