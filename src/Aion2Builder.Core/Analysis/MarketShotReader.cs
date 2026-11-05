using System.Globalization;
using System.Text;
using Aion2Builder.Combat;

namespace Aion2Builder.Analysis;

/// <summary>
/// Une ligne lue sur une capture de l'hôtel des ventes du jeu : nom de l'objet, nombre d'annonces, prix unitaire (Kinah).
/// Lowest : lu sur la liste des annonces d'un objet (une ligne par annonce) : Price est le prix unitaire le plus bas des
/// Listings annonces lues.
/// </summary>
public sealed record MarketShotLine(string Name, int? Listings, long Price, bool Lowest = false);

/// <summary>
/// Lit une capture de la fenêtre Marché du jeu, reconnue par l'OCR de Windows. Deux vues :
/// <list type="bullet">
/// <item>la liste des objets (onglet Principal) : « Objet », « Liste de vente » (nombre d'annonces), « Prix de vente du
/// serveur » ; une ligne par objet ;</item>
/// <item>les annonces d'un objet : « Objet », « Prix de vente par unité », « Prix de vente » (total), « Temps restant » ; une
/// ligne par annonce : on garde le prix unitaire le plus bas et le nombre d'annonces lues.</item>
/// </list>
/// Chaque nombre va à la colonne dont le titre est le plus proche ; les groupes de chiffres (« 1 490 000 ») sont recollés et
/// l'icône des Kinah lue comme un « 0 » devant le prix est écartée. Sans la ligne des titres (« Objet », « Prix… »), la capture
/// est refusée : deviner où s'arrête un nombre donne des prix faux. Proposition #65 : la capture reste sur le PC, seuls les
/// prix que tu valides partent vers Aion 2 Todo.
/// </summary>
public static class MarketShotReader
{
    static readonly string[] Titles = ["objet", "grade", "liste", "prix", "temps"];

    /// <summary>Pourquoi une capture est refusée : la ligne des titres des colonnes n'y est pas (ou illisible).</summary>
    public const string NoTitles = "je ne vois pas les titres des colonnes (Objet, Prix de vente…). Refais la capture en gardant la ligne des titres au-dessus des objets.";

    /// <param name="problem">Non nul quand la capture est refusée (titres des colonnes absents) : aucune ligne n'est lue.</param>
    public static List<MarketShotLine> Read(IReadOnlyList<OcrWord> words, out string? problem)
    {
        problem = NoTitles;
        if (words.Count == 0) return [];
        var height = Median(words.Select(w => w.Height));
        var objet = words.FirstOrDefault(w => Fold(w.Text) == "objet");
        if (objet is null) return [];
        // Titres des colonnes : les mots de la ligne de « Objet », regroupés derrière chaque mot-clé (« Prix de vente p… »).
        var header = words.Where(w => Math.Abs(Center(w) - Center(objet)) < height * 0.6).OrderBy(w => w.X).ToList();
        var titles = new List<(string Key, double Left, double Right)>();
        foreach (var w in header)
        {
            var key = Fold(w.Text);
            if (Titles.Contains(key)) titles.Add((key, w.X, w.X + w.Width));
            else if (titles.Count > 0) titles[^1] = titles[^1] with { Right = w.X + w.Width };
        }
        var numeric = titles.Where(t => t.Key is "liste" or "prix" or "temps").ToList();
        var price = numeric.FirstOrDefault(t => t.Key == "prix");  // le premier « Prix » : par unité (annonces) ou du serveur
        if (price.Key is null) return [];
        problem = null;
        var count = numeric.FirstOrDefault(t => t.Key == "liste");
        // Les nombres commencent un peu avant leur titre (icône des Kinah) ; le nom est bien plus à gauche.
        var nameEnd = numeric.Min(t => t.Left) - height * 3;
        // Colonne « Grade » (liste des objets) : à sa gauche, le menu des catégories ne fait pas partie des noms.
        var grade = titles.FirstOrDefault(t => t.Key == "grade");
        var left = grade.Key is null ? double.MinValue : grade.Left - height * 2;
        var top = objet.Y + objet.Height + height * 0.3;
        var read = new List<(string Name, int? Count, long Price)>();
        foreach (var row in Lines(words.Where(w => w.Y > top && w.X >= left).ToList(), height))
        {
            var sorted = row.OrderBy(w => w.X).ToList();
            var name = Name(sorted.Where(w => w.X + w.Width / 2 < nameEnd));
            var cells = sorted.Where(w => w.X + w.Width / 2 >= nameEnd)
                .GroupBy(w => numeric.MinBy(t => Math.Abs((t.Left + t.Right) / 2 - (w.X + w.Width / 2))))
                .ToDictionary(g => g.Key, g => g.ToList());
            var value = cells.TryGetValue(price, out var p) ? Number(p) : null;
            if (name.Length < 2 || value is not > 0) continue;
            int? listings = count.Key is not null && cells.TryGetValue(count, out var c) && Number(c) is { } n and < 100_000 ? (int)n : null;
            read.Add((name, listings, value.Value));
        }
        if (count.Key is not null || numeric.All(t => t.Key != "temps"))
            return read.Select(r => new MarketShotLine(r.Name, r.Count, r.Price)).ToList();
        // Annonces d'un objet : une ligne par annonce ; le prix unitaire le plus bas, et combien d'annonces sont lues.
        return read.GroupBy(r => Fold(r.Name)).Select(g => new MarketShotLine(g.First().Name, g.Count(), g.Min(r => r.Price), Lowest: true)).ToList();
    }

    /// <summary>Le nom : les mots avec des lettres (le « 1 » de la pile sur l'icône et les signes seuls sont écartés).</summary>
    static string Name(IEnumerable<OcrWord> words) =>
        // L'OCR coupe « d'Yggdrasil » en « d' » + « Yggdrasil » : l'espace après l'apostrophe est retiré.
        System.Text.RegularExpressions.Regex.Replace(
            string.Join(' ', words.Select(w => w.Text.Trim()).Where(t => t.Length > 0 && Letters(t) > 0 || t.Any(char.IsDigit) && t.Length > 1)).Replace('’', '\''),
            @"'\s+", "'");

    /// <summary>
    /// Un nombre écrit en groupes (« 1 490 000 ») : les chiffres des mots recollés. L'icône des Kinah devant est parfois lue
    /// « 0 », « O » ou « © », seule ou collée au nombre (« 01493 878 ») : un prix ne commence jamais par 0.
    /// </summary>
    static long? Number(List<OcrWord> cell)
    {
        var parts = cell.OrderBy(w => w.X).Select(w => w.Text.Trim()).Where(t => t.Length > 0 && Letters(t) <= 1).ToList();
        if (parts.Count == 0 || parts.Any(t => Letters(t) == 1 && Digits(t).Length > 0 && !IsIcon(t))) return null;  // « 2j12h » : une durée
        var digits = string.Concat(parts.Select(Digits)).TrimStart('0');
        return digits.Length is > 0 and <= 13 && long.TryParse(digits, out var n) ? n : null;
    }

    static bool IsIcon(string t) => t.Length > 1 && t[0] is 'O' or 'o' or 'Q' or '©' or '@' or '®' && t[1..].All(char.IsDigit);

    /// <summary>Mots regroupés en lignes : même hauteur à une demi-hauteur de texte près, de haut en bas.</summary>
    static List<List<OcrWord>> Lines(List<OcrWord> words, double height)
    {
        var lines = new List<List<OcrWord>>();
        foreach (var w in words.OrderBy(Center))
        {
            var line = lines.LastOrDefault(l => Math.Abs(l.Average(Center) - Center(w)) < height * 0.6);
            if (line is null) lines.Add([w]);
            else line.Add(w);
        }
        return lines;
    }

    static double Center(OcrWord w) => w.Y + w.Height / 2;

    static string Fold(string s)
    {
        var d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetter(c)).ToArray());
    }

    static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
    static int Letters(string s) => s.Count(char.IsLetter);
    static double Median(IEnumerable<double> values) => values.OrderBy(v => v).ToList() is { Count: > 0 } v ? v[v.Count / 2] : 16;
}
