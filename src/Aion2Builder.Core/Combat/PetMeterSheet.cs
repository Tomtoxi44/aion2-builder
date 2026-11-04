using System.Globalization;
using System.Text.Json;

namespace Aion2Builder.Combat;

/// <summary>
/// Fiche de stats que le jeu envoie au Pet Meter (v0.20.9 et plus) : la fiche complète à la connexion (49 36) puis ses
/// changements (4a 36), notée hors combat par perso dans %LOCALAPPDATA%\AION2-pet-meter\ingame-stats.json, avec les
/// identifiants bruts du jeu. Lue seulement. Seules les stats dont l'identifiant est sûr sont reprises (décodage du 04/11) :
/// ATQ, DÉF, Précision, CRIT… ne sont pas dans la fiche (le jeu les calcule).
/// </summary>
public static class PetMeterSheet
{
    public static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AION2-pet-meter", "ingame-stats.json");

    /// <summary>Tes valeurs en jeu par clé de stat du Builder, et quand le Pet Meter les a notées ; null sans fiche pour ce perso.</summary>
    public sealed record Sheet(Dictionary<string, double> Values, DateTime At);

    static (DateTime Stamp, Dictionary<string, Sheet> Chars)? cache;
    static readonly object Lock = new();

    public static Sheet? For(string character)
    {
        var chars = Load();
        return chars.TryGetValue(character, out var s) ? s
            : chars.FirstOrDefault(kv => string.Equals(kv.Key, character, StringComparison.OrdinalIgnoreCase)).Value;
    }

    static Dictionary<string, Sheet> Load()
    {
        var stamp = System.IO.File.Exists(File) ? System.IO.File.GetLastWriteTimeUtc(File) : DateTime.MinValue;
        lock (Lock)
        {
            if (cache is { } c && c.Stamp == stamp) return c.Chars;
            var chars = new Dictionary<string, Sheet>();
            try
            {
                if (stamp != DateTime.MinValue)
                {
                    using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
                    foreach (var ch in doc.RootElement.GetProperty("chars").EnumerateObject())
                    {
                        var raw = new Dictionary<int, long>();
                        foreach (var s in ch.Value.GetProperty("stats").EnumerateObject())
                            if (int.TryParse(s.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && s.Value.TryGetInt64(out var v))
                                raw[id] = v;
                        var at = ch.Value.TryGetProperty("at", out var a) && DateTimeOffset.TryParseExact(a.GetString(), "yyyy-MM-dd'T'HH:mm:sszzz",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.UtcDateTime : stamp;
                        var values = Values(raw);
                        if (values.Count > 0) chars[ch.Name] = new Sheet(values, at);
                    }
                }
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // fichier en cours d'écriture ou d'un autre format : rien de repris
            }
            cache = (stamp, chars);
            return chars;
        }
    }

    /// <summary>
    /// Identifiants sûrs : PV max = ⌈base (0xc1) × (1 + 0xc4 / 10 000)⌉, PM max pareil avec 0xc7 et 0xc5 (vérifié avec les jauges
    /// du jeu) ; VIT de combat 0x11a et de déplacement 0xcd en % × 100 ; Perforation 0x11c.
    /// </summary>
    static Dictionary<string, double> Values(Dictionary<int, long> s)
    {
        var v = new Dictionary<string, double>();
        if (s.TryGetValue(0xc1, out var hp) && hp > 0)
            v["HP"] = Math.Ceiling(hp * (1 + s.GetValueOrDefault(0xc4) / 10000.0));
        if (s.TryGetValue(0xc7, out var mp) && mp > 0)
            v["MP"] = Math.Ceiling(mp * (1 + s.GetValueOrDefault(0xc5) / 10000.0));
        if (s.TryGetValue(0x11a, out var cs)) v["CombatSpeed"] = cs / 100.0;
        if (s.TryGetValue(0xcd, out var ms)) v["MoveSpeed"] = ms / 100.0;
        if (s.TryGetValue(0x11c, out var pierce)) v["Pierce"] = pierce;
        return v;
    }
}
