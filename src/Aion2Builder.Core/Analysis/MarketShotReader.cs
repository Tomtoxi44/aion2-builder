using Aion2Builder.Combat;

namespace Aion2Builder.Analysis;

/// <summary>Une ligne lue sur une capture de l'hôtel des ventes du jeu : nom de l'objet, nombre d'annonces, prix unitaire (Kinah).</summary>
public sealed record MarketShotLine(string Name, int? Listings, long Price);

/// <summary>
/// Lit une capture de la fenêtre Marché du jeu (onglet Principal), reconnue par l'OCR de Windows : chaque ligne donne le nom de
/// l'objet, sous la colonne « Objet », le nombre d'annonces (« Liste de vente ») et le prix (« Prix de vente… », après l'icône
/// des Kinah). Les colonnes sont prises sur les titres quand ils sont lus ; sinon, les deux derniers nombres de la ligne.
/// Proposition #65 : la capture reste sur le PC, seuls les prix que tu valides partent vers Aion 2 Todo.
/// </summary>
public static class MarketShotReader
{
    public static List<MarketShotLine> Read(IReadOnlyList<OcrWord> words)
    {
        if (words.Count == 0) return [];
        var height = Median(words.Select(w => w.Height));
        // Titres des colonnes : « Liste » (de vente) et « Prix » (de vente du serveur), sur une même ligne.
        var liste = words.FirstOrDefault(w => w.Text.Equals("Liste", StringComparison.OrdinalIgnoreCase));
        var prix = words.FirstOrDefault(w => w.Text.Equals("Prix", StringComparison.OrdinalIgnoreCase)
                                             && (liste is null || Math.Abs(w.Y - liste.Y) < height));
        // Colonne « Grade » : à sa gauche, le menu des catégories (Tout, armes…) ne fait pas partie des noms.
        var grade = words.FirstOrDefault(w => w.Text.Equals("Grade", StringComparison.OrdinalIgnoreCase));
        var left = grade is null ? double.MinValue : grade.X - height * 2;
        var top = Math.Max(liste is null ? 0 : liste.Y + liste.Height, prix is null ? 0 : prix.Y + prix.Height);
        var rows = Lines(words.Where(w => w.Y > top + height * 0.3 && w.X >= left).ToList(), height);
        var result = new List<MarketShotLine>();
        foreach (var row in rows)
        {
            var sorted = row.OrderBy(w => w.X).ToList();
            List<OcrWord> nameWords, countWords, priceWords;
            if (liste is not null && prix is not null)
            {
                var split = liste.X + (prix.X - liste.X) * 0.6;  // entre le nombre d'annonces et l'icône des Kinah
                nameWords = sorted.Where(w => w.X + w.Width < liste.X - height).ToList();
                countWords = sorted.Where(w => w.X + w.Width >= liste.X - height && w.X < split).ToList();
                priceWords = sorted.Where(w => w.X >= split).ToList();
            }
            else
            {
                // Sans titres : le prix est le dernier nombre, les annonces celui d'avant, le nom ce qui précède.
                var numbers = sorted.Select((w, i) => (w, i)).Where(x => Digits(x.w.Text).Length > 0 && Letters(x.w.Text) == 0).ToList();
                if (numbers.Count == 0) continue;
                var last = numbers[^1].i;
                var before = numbers.Count > 1 && numbers[^2].i == last - 1 ? numbers[^2].i : last;
                priceWords = [sorted[last]];
                countWords = before < last ? [sorted[before]] : [];
                nameWords = sorted.Take(Math.Min(before, last)).ToList();
            }
            // L'icône des Kinah devant le prix est parfois lue comme « 0 », « O » ou « © » : un signe seul devant d'autres chiffres.
            if (priceWords.Count > 1 && priceWords[0].Text is "0" or "O" or "o" or "©" or "@" or "®" or "Q")
                priceWords.RemoveAt(0);
            var priceText = string.Concat(priceWords.Select(w => Digits(w.Text)));
            var name = string.Join(' ', nameWords.Select(w => w.Text.Trim()).Where(t => t.Length > 0 && Letters(t) > 0 || t.Any(char.IsDigit) && t.Length > 1));
            if (name.Length < 2 || priceText.Length == 0 || priceText.Length > 13 || !long.TryParse(priceText, out var price) || price < 1) continue;
            var countText = string.Concat(countWords.Select(w => Digits(w.Text)));
            result.Add(new MarketShotLine(name, int.TryParse(countText, out var count) ? count : null, price));
        }
        return result;
    }

    /// <summary>Mots regroupés en lignes : même hauteur à une demi-hauteur de texte près, de haut en bas.</summary>
    static List<List<OcrWord>> Lines(List<OcrWord> words, double height)
    {
        var lines = new List<List<OcrWord>>();
        foreach (var w in words.OrderBy(w => w.Y + w.Height / 2))
        {
            var center = w.Y + w.Height / 2;
            var line = lines.LastOrDefault(l => Math.Abs(l.Average(x => x.Y + x.Height / 2) - center) < height * 0.6);
            if (line is null) lines.Add([w]);
            else line.Add(w);
        }
        return lines;
    }

    static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
    static int Letters(string s) => s.Count(char.IsLetter);
    static double Median(IEnumerable<double> values) => values.OrderBy(v => v).ToList() is { Count: > 0 } v ? v[v.Count / 2] : 16;
}
