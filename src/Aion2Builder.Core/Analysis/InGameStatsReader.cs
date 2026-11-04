using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aion2Builder.Combat;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>
/// Captures de la fenêtre « Infos sur les stats » du jeu (touche P ; onglets Stats principales et Stats détaillées), lues par
/// l'OCR de Windows : chaque ligne y a deux cases « libellé valeur ». Un libellé est gardé s'il est celui d'une stat connue ;
/// coupé par le jeu (« Tolérance aux DGT… »), seulement s'il ne peut être que d'une stat. Les stats principales (Force…) sont
/// laissées : le site officiel les donne déjà.
/// </summary>
public static partial class InGameStatsReader
{
    /// <summary>Côté minimal de la capture pour l'OCR : la fenêtre Stats est petite, ses chiffres sont mieux lus agrandis.</summary>
    public const int MinOcrSize = 2000;

    /// <summary>Valeur affichée par le jeu, par clé de stat (la première lue l'emporte : le jeu montre la vraie ligne avant les variantes coupées).</summary>
    public static Dictionary<string, double> Read(GameDatabase db, IEnumerable<IReadOnlyList<OcrWord>> pages)
    {
        var labels = db.Stats.Where(s => s.Category != StatCategory.Primary)
            .SelectMany(s => s.Labels.Append(s.Name).Select(l => (Label: Plain(l), s.Key))).Distinct().ToList();
        string? Match(string text)
        {
            var cut = text.EndsWith('…') || text.EndsWith("...", StringComparison.Ordinal);
            var plain = Plain(text);
            if (plain.Length < 2) return null;
            var exact = labels.Where(l => l.Label == plain).Select(l => l.Key).Distinct().ToList();
            if (!cut && exact.Count == 1) return exact[0];
            var prefix = labels.Where(l => l.Label.StartsWith(plain, StringComparison.Ordinal)).Select(l => l.Key).Distinct().ToList();
            return cut && prefix.Count == 1 ? prefix[0] : null;
        }

        var found = new Dictionary<string, double>();
        foreach (var words in pages)
            foreach (var row in Rows(words))
            {
                var label = new List<string>();
                for (var i = 0; i < row.Count; i++)
                {
                    var text = row[i].Text.Trim();
                    if (Number(text) is not { } value)
                    {
                        if (text != "%") label.Add(text);
                        continue;
                    }
                    // Milliers coupés par l'OCR (« 15 620 ») : un groupe de 3 chiffres juste après.
                    while (!text.Contains(',') && !text.Contains('.') && i + 1 < row.Count && ThreeDigits().IsMatch(row[i + 1].Text.Trim())
                           && row[i + 1].X - (row[i].X + row[i].Width) < row[i].Height)
                    {
                        text += row[++i].Text.Trim();
                        value = Number(text) ?? value;
                    }
                    if (label.Count > 0 && Match(string.Join(' ', label)) is { } key) found.TryAdd(key, value);
                    label.Clear();
                }
            }
        return found;
    }

    /// <summary>Lignes de la capture : mots regroupés par hauteur (centre à moins de 0,6 hauteur de mot), de gauche à droite.</summary>
    static List<List<OcrWord>> Rows(IReadOnlyList<OcrWord> words)
    {
        var rows = new List<List<OcrWord>>();
        foreach (var w in words.OrderBy(w => w.Y + w.Height / 2))
        {
            var center = w.Y + w.Height / 2;
            var last = rows.Count > 0 ? rows[^1] : null;
            if (last is not null && Math.Abs(last.Average(x => x.Y + x.Height / 2) - center) <= 0.6 * Math.Max(w.Height, last.Max(x => x.Height)))
                last.Add(w);
            else rows.Add([w]);
        }
        return rows.Select(r => r.OrderBy(w => w.X).ToList()).ToList();
    }

    static double? Number(string text)
    {
        var m = NumberPattern().Match(text);
        return m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Libellé comparable : minuscules, sans accents (l'OCR les perd parfois), espaces simples, sans « … » final.</summary>
    static string Plain(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in GameDatabase.NormalizeLabel(text).Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().TrimEnd('…', '.', ' ');
    }

    [GeneratedRegex(@"^(-?\d+(?:[.,]\d+)?)\s*%?$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^\d{3}(?:[.,]\d+)?%?$")]
    private static partial Regex ThreeDigits();

    /// <summary>
    /// Tes stats en jeu ajoutées à tes réglages : chaque valeur reçue remplace la précédente, et sa part cachée est calculée sur
    /// le perso affiché (les autres gardent la leur, calculée à leur relevé).
    /// </summary>
    public static PlayerInputs WithInGame(AdviceReport r, PlayerInputs inputs, IReadOnlyDictionary<string, double?> values)
    {
        var inGame = new Dictionary<string, double>(inputs.InGame);
        var hidden = new Dictionary<string, double>(inputs.Hidden);
        foreach (var (key, value) in values)
        {
            if (value is not { } v || r.Stats.Stats.FirstOrDefault(x => x.Def.Key == key) is not { } stat)
            {
                inGame.Remove(key);
                hidden.Remove(key);
                continue;
            }
            inGame[key] = v;
            hidden[key] = Math.Round(StatAnalyzer.HiddenPart(stat, v), 2);
        }
        return inputs with { InGame = inGame, Hidden = hidden, InGameAt = DateTime.UtcNow, InGameItemLevel = r.Character.ItemLevel };
    }
}
