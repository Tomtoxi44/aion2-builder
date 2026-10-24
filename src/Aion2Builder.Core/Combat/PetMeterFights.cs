using System.Text.Json;
using Aion2Builder.GameData;

namespace Aion2Builder.Combat;

/// <summary>
/// Combats écrits par le Pet Meter (lus dans le trafic du jeu) : %LOCALAPPDATA%\AION2-pet-meter\fights\*.json, un fichier
/// par combat. Convertis en <see cref="CombatLog"/> avec les noms de sorts de spells.json ; un sort absent des données
/// (enchaînement, effet déclenché) garde son code.
/// </summary>
public static class PetMeterFights
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AION2-pet-meter", "fights");

    /// <summary>Version de la conversion (2 : lancers groupés à 0,6 s, noms des enchaînements du Templier).</summary>
    public const int Version = 2;

    /// <summary>
    /// Met à jour les combats gardés d'un perso : reconvertit ceux qu'une version plus ancienne a convertis (leur fichier
    /// existe encore), puis reprend les nouveaux (les 20 plus récents au plus). Rend le nombre de nouveaux combats.
    /// </summary>
    public static int Sync(Storage.CombatStore store, string characterKey, string character, GameDatabase db)
    {
        foreach (var (path, log) in store.List(characterKey))
            if (log.Source == "Pet Meter" && log.ImportVersion < Version && log.SourceFile is { } file && File.Exists(Path.Combine(Dir, file)))
            {
                try
                {
                    if (JsonSerializer.Deserialize<Fight>(File.ReadAllText(Path.Combine(Dir, file)), GameDataStore.Json) is { } f)
                        store.Overwrite(path, ToLog(f, Path.Combine(Dir, file), db));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
            }
        var known = store.Imported(characterKey);
        known.UnionWith(store.List(characterKey).Select(x => x.Log.SourceFile).OfType<string>());
        var fresh = For(character).Where(x => !known.Contains(Path.GetFileName(x.Path))).Take(Storage.CombatStore.Keep).ToList();
        foreach (var (path, fight) in fresh) store.Save(characterKey, ToLog(fight, path, db));
        if (fresh.Count == 0) return 0;
        store.MarkImported(characterKey, fresh.Select(x => Path.GetFileName(x.Path)));
        store.Prune(characterKey);
        return fresh.Count;
    }

    /// <summary>Créatures connues : code du Pet Meter → nom.</summary>
    static readonly Dictionary<long, string> Creatures = new() { [2400032] = "Mannequin d'entraînement" };

    public sealed record Fight(int Version, string Meter, string Character, long Entity, string Start, string StartUtc, double Duration,
        double Damage, int Hits, FightTarget Target, List<FightSkill> Skills, List<double[]> Casts, FightTaken Taken, FightHeals Heals, long Map = 0);
    public sealed record FightTarget(long Entity, long Code, double Damage);
    public sealed record FightSkill(long Code, int Hits, double Damage, int Crits, int Back, int Front, int Positioned, double Max, bool Summon);
    public sealed record FightTaken(double Damage, int Hits, double Max);
    public sealed record FightHeals(double Amount, int Hits);

    /// <summary>Combats du Pet Meter pour ce personnage (nom), le plus récent d'abord.</summary>
    public static List<(string Path, Fight Fight)> For(string character)
    {
        if (!Directory.Exists(Dir)) return [];
        var list = new List<(string, Fight)>();
        foreach (var path in Directory.GetFiles(Dir, "*.json").OrderDescending())
        {
            try
            {
                if (JsonSerializer.Deserialize<Fight>(File.ReadAllText(path), GameDataStore.Json) is { } f
                    && string.Equals(f.Character, character, StringComparison.OrdinalIgnoreCase))
                    list.Add((path, f));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    static string Name(GameDatabase db, long code) =>
        code >= 10_000_000 && db.Spell(code) is { } s ? s.Name : $"Sort {code}";

    public static CombatLog ToLog(Fight f, string path, GameDatabase db)
    {
        // Lancers : chaque utilisation envoie deux messages (le lancer sur la cible, puis sa suite 0,15 à 0,4 s après) :
        // un même sort à moins de 0,6 s ne compte qu'une fois (recoupé avec NotMeter : une utilisation par coup).
        var casts = new List<CombatCast>();
        foreach (var c in f.Casts.Where(c => c.Length >= 2).OrderBy(c => c[0]))
        {
            var code = (long)c[1];
            if (casts.LastOrDefault(x => x.Code == code) is { } previous && c[0] - previous.T < 0.6) continue;
            casts.Add(new CombatCast(c[0], code, Name(db, code)));
        }
        var skills = f.Skills.Select(s => new CombatSkill(Name(db, s.Code), null, s.Damage, f.Damage > 0 ? s.Damage / f.Damage : 0,
            casts.Count(c => c.Code == s.Code), s.Hits, s.Hits > 0 ? (double)s.Crits / s.Hits : null,
            s.Positioned > 0 ? (double)s.Back / s.Positioned : null, null)).ToList();
        var start = DateTime.TryParse(f.StartUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t : File.GetLastWriteTimeUtc(path);
        return new CombatLog
        {
            Source = "Pet Meter", ImportedAtUtc = start, Character = f.Character, ImportVersion = Version,
            Target = Creatures.TryGetValue(f.Target.Code, out var n) ? n : f.Target.Code > 0 ? $"créature {f.Target.Code}" : "monstres",
            Duration = f.Duration, TotalDamage = f.Damage, Dps = f.Duration > 0 ? f.Damage / f.Duration : 0, Hits = f.Hits,
            SkillCount = skills.Count, Skills = skills.OrderByDescending(s => s.Damage).ToList(), Pages = 0,
            SourceFile = Path.GetFileName(path), DamageTaken = f.Taken.Damage, HitsTaken = f.Taken.Hits, MaxTaken = f.Taken.Max,
            Heals = f.Heals.Amount, Casts = casts, Map = f.Map > 0 ? f.Map : null,
        };
    }
}
