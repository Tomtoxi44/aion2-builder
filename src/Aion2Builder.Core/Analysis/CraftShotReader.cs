using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aion2Builder.Combat;

namespace Aion2Builder.Analysis;

/// <summary>
/// Un objet lu sur la fiche d'une recette : son nom tel qu'affiché (sans les « … » de fin), Cut = le jeu a coupé le nom (il
/// finit par « … ») ; Qty = quantité demandée pour une fabrication (le 2e nombre de « 8/12 »), null si elle n'a pas été lue ;
/// Grade = rang du jeu lu à la couleur du nom.
/// </summary>
public sealed record CraftShotPart(string Name, bool Cut, int? Qty, string? Grade);

/// <summary>Une recette lue : l'objet fabriqué (null s'il n'a pas été lu) et ses matériaux, dans l'ordre de la fenêtre.</summary>
public sealed record CraftShot(CraftShotPart? Output, List<CraftShotPart> Inputs);

/// <summary>
/// Lit une capture de la fenêtre d'artisanat du jeu (Forge, Armure, Artisanat, Alchimie…), reconnue par l'OCR de Windows. Sous
/// le titre « Matériaux de fabrication » : pour chaque matériau, une ligne avec son nom, puis une ligne « ce que tu as / ce qu'il
/// faut » (on garde le 2e nombre) ; le petit bandeau « de transfert » est écarté. L'objet fabriqué est le plus gros texte à
/// gauche de cette colonne (« ★ Épée du Roi dragon blanc »). Le jeu coupe les noms longs (« Minerai d'orichalque écl… ») :
/// Cut le dit, et l'écran Craft les complète d'après les objets connus. La capture reste sur le PC.
/// </summary>
public static class CraftShotReader
{
    public const string NoHeader = "je ne vois pas « Matériaux de fabrication » : refais la capture avec la fiche de la recette (l'objet fabriqué et la liste de ses matériaux).";

    static readonly Regex QtyLine = new(@"^([0-9OoIl|]{1,6})\s*/\s*([0-9OoIl|]{1,6})$");
    static readonly Regex CutEnd = new(@"\s*(\.{2,}|…|\.?„|,,)[.…„,]*$");

    /// <param name="problem">Non nul quand la capture est refusée (pas de colonne « Matériaux de fabrication »).</param>
    public static CraftShot? Read(IReadOnlyList<OcrWord> words, out string? problem)
    {
        problem = NoHeader;
        var header = words.FirstOrDefault(w => Fold(w.Text) == "materiaux"
            && words.Any(o => Fold(o.Text).StartsWith("fabrication") && o.X > w.X && Math.Abs(Center(o) - Center(w)) < w.Height));
        if (header is null) return null;
        problem = null;
        var h = header.Height;
        var left = header.X - 8 * h;

        // La colonne des matériaux : sous le titre, à sa hauteur de texte (les icônes lues comme « 64 » ou « @ » sont plus hautes
        // ou plus à gauche).
        var column = words.Where(w => w.X >= left && w.Y > header.Y + h * 0.8 && w.Height < 2.6 * h).ToList();
        var inputs = new List<CraftShotPart>();
        (string Text, string? Grade)? pending = null;
        foreach (var line in Lines(column, h))
        {
            var text = Text(line);
            var q = QtyLine.Match(text.Replace(" ", ""));
            if (q.Success)
            {
                if (pending is { } name) inputs.Add(Part(name.Text, name.Grade, Number(q.Groups[2].Value)));
                pending = null;
                continue;
            }
            if (Fold(text).Contains("transfe")) continue;      // bandeau « de transfert » au-dessus du nom
            if (!line.Any(w => w.Text.Count(char.IsLetter) >= 2)) continue;
            if (pending is { } missed) inputs.Add(Part(missed.Text, missed.Grade, null));  // quantité non lue
            pending = (text, GradeOf(line));
        }
        if (pending is { } last) inputs.Add(Part(last.Text, last.Grade, null));

        // L'objet fabriqué : la ligne au plus gros texte à gauche de la colonne (le titre doré sous l'image).
        var body = words.Where(w => w.X + w.Width < left).ToList();
        CraftShotPart? output = null;
        var title = Lines(body, h).Where(l => l.Any(w => w.Text.Count(char.IsLetter) >= 3))
            .Select(l => (Line: l, Size: Median(l.Select(w => w.Height)))).Where(x => x.Size >= 1.4 * h).MaxBy(x => x.Size);
        if (title.Line is { } t) output = Part(Text(t).TrimStart('*', '★', '☆', '•', '·', ' '), GradeOf(t), 1);
        return new CraftShot(output, inputs);
    }

    /// <summary>
    /// Deux lectures de la même capture (normale et éclaircie) réunies : les matériaux de la plus complète, et pour chacun la
    /// quantité de l'autre lecture si elle manque (même nom, sinon même rang dans la liste).
    /// </summary>
    public static CraftShot? Merge(CraftShot? a, CraftShot? b)
    {
        if (a is null || b is null) return a ?? b;
        var (main, other) = b.Inputs.Count > a.Inputs.Count || (b.Inputs.Count == a.Inputs.Count && b.Inputs.Count(i => i.Qty is null) < a.Inputs.Count(i => i.Qty is null)) ? (b, a) : (a, b);
        var inputs = main.Inputs.Select((p, i) =>
        {
            var twin = other.Inputs.FirstOrDefault(o => Fold(o.Name) == Fold(p.Name))
                       ?? (other.Inputs.Count == main.Inputs.Count ? other.Inputs[i] : null);
            if (twin is null) return p;
            if (p.Qty is null && twin.Qty is { } q) p = p with { Qty = q };
            if (twin.Name.Length > p.Name.Length && Fold(twin.Name).StartsWith(Fold(p.Name))) p = p with { Name = twin.Name, Cut = twin.Cut };
            return p;
        }).ToList();
        var output = main.Output ?? other.Output;
        if (output is { Cut: true } && other.Output is { Cut: false } o && o.Name.Length > output.Name.Length) output = o;
        return new CraftShot(output, inputs);
    }

    static CraftShotPart Part(string text, string? grade, int? qty)
    {
        // Bruit des icônes lu devant le nom (« g Grimoire… », « -4 Écaille… », « + Grimoire… » pour l'étoile) : mots d'un seul
        // signe ou sans lettre en tête, puis tout ce qui n'est pas une lettre.
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && (tokens[0].Length == 1 || !tokens[0].Any(char.IsLetter))) tokens.RemoveAt(0);
        text = string.Join(' ', tokens);
        var start = 0;
        while (start < text.Length && !char.IsLetter(text[start])) start++;
        text = text[start..];
        var m = CutEnd.Match(text);
        var cut = m.Success;
        var name = cut ? text[..m.Index] : text.TrimEnd('.', ' ');
        return new CraftShotPart(name.Trim(), cut, qty, grade);
    }

    static int? Number(string s)
    {
        var digits = s.Replace('O', '0').Replace('o', '0').Replace('I', '1').Replace('l', '1').Replace('|', '1');
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>Mots regroupés en lignes (centres à moins de 0,6 hauteur de texte), de haut en bas, chacune de gauche à droite.</summary>
    static List<List<OcrWord>> Lines(IEnumerable<OcrWord> words, double h)
    {
        var lines = new List<List<OcrWord>>();
        foreach (var w in words.OrderBy(Center))
        {
            var line = lines.LastOrDefault(l => Math.Abs(l.Average(Center) - Center(w)) < 0.6 * Math.Max(h, w.Height));
            if (line is null) lines.Add([w]);
            else line.Add(w);
        }
        return lines.Select(l => l.OrderBy(w => w.X).ToList()).ToList();
    }

    static string Text(List<OcrWord> line) => string.Join(' ', line.Select(w => w.Text));

    static string? GradeOf(List<OcrWord> line) =>
        line.Select(w => MarketShotReader.GradeOf(w.Rgb)).OfType<string>().GroupBy(g => g).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

    static double Center(OcrWord w) => w.Y + w.Height / 2;

    static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
