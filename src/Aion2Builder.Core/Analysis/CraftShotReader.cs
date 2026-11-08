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

/// <summary>
/// Une recette lue : l'objet fabriqué (null s'il n'a pas été lu) et ses matériaux, dans l'ordre de la fenêtre ; HasMaterials =
/// la colonne « Matériaux de fabrication » est sur la capture. Listed : les noms entiers de la liste des recettes à gauche (sans
/// celui qui défile, coupé aux deux bouts) ; Bound : « l'objet fabriqué sera lié d'âme » ; Category : l'onglet ouvert (« Forge ·
/// Épées »), null s'il n'est pas lu.
/// </summary>
public sealed record CraftShot(CraftShotPart? Output, List<CraftShotPart> Inputs, bool HasMaterials = true, List<CraftShotPart>? Listed = null,
    bool Bound = false, string? Category = null);

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

    static readonly string[] MainTabs = ["forge", "armure", "artisanat", "alchimie", "cuisine"];

    /// <param name="problem">
    /// Non nul quand la capture est refusée : ni la colonne « Matériaux de fabrication », ni le nom d'un objet fabriqué. Sans la
    /// colonne, l'objet et la liste des recettes sont quand même lus (HasMaterials = false).
    /// </param>
    public static CraftShot? Read(IReadOnlyList<OcrWord> words, out string? problem)
    {
        problem = NoHeader;
        if (words.Count == 0) return null;
        var header = words.FirstOrDefault(w => Fold(w.Text) == "materiaux"
            && words.Any(o => Fold(o.Text).StartsWith("fabrication") && o.X > w.X && Math.Abs(Center(o) - Center(w)) < w.Height));
        var h = header?.Height ?? Median(words.Select(w => w.Height));
        var left = header is null ? words.Max(w => w.X + w.Width) + 1 : header.X - 8 * h;

        // La colonne des matériaux : sous le titre, à sa hauteur de texte (les icônes lues comme « 64 » ou « @ » sont plus hautes
        // ou plus à gauche).
        var column = header is null ? [] : words.Where(w => w.X >= left && w.Y > header.Y + h * 0.8 && w.Height < 2.6 * h).ToList();
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

        // L'objet fabriqué : le morceau de ligne au plus gros texte à gauche de la colonne (le titre doré sous l'image) ; une ligne
        // est coupée là où les mots s'écartent (la liste des recettes, à gauche, peut être à la même hauteur).
        var body = words.Where(w => w.X + w.Width < left).ToList();
        var segments = Segments(Lines(body, h), h);
        CraftShotPart? output = null;
        var title = segments.Where(l => l.Any(w => w.Text.Count(char.IsLetter) >= 3))
            .Select(l => (Line: l, Size: Median(l.Select(w => w.Height)))).Where(x => x.Size >= 1.4 * h).MaxBy(x => x.Size);
        if (title.Line is { } t) output = Part(Text(t), GradeOf(t), 1);
        if (output is null && header is null) return null;
        problem = null;

        // La liste des recettes, à gauche du titre : les noms entiers qui commencent par une majuscule (assombris quand ils ne sont
        // pas choisis : leur couleur n'est souvent pas lue). Les noms des amis (blancs, un seul mot) et celui qui défile (coupé, il
        // touche le bord de la liste) sont écartés.
        var listed = new List<CraftShotPart>();
        if (title.Line is { } tl)
        {
            var titleX = tl.Min(w => w.X);
            var rows = segments.Where(l => l != tl && l.Max(w => w.X + w.Width) < titleX - 2 * h && l.Count(w => w.Text.Count(char.IsLetter) >= 2) >= 2
                                           && GradeOf(l) != "Common").ToList();
            var edge = rows.Count >= 2 ? rows.Max(l => l.Max(w => w.X + w.Width)) : double.MaxValue;
            foreach (var row in rows)
            {
                var part = Part(Text(row), GradeOf(row), null);
                if (part.Cut || part.Name.Length < 3 || !char.IsUpper(part.Name[0]) || row.Max(w => w.X + w.Width) >= edge - 0.6 * h) continue;
                if (listed.All(p => Fold(p.Name) != Fold(part.Name)) && Fold(part.Name) != Fold(output?.Name ?? "")) listed.Add(part);
            }
        }

        // « l'objet fabriqué sera lié d'âme pour le personnage qui l'a créé »
        var all = Fold(string.Join(' ', Lines(words, h).Select(Text)));
        var bound = all.Contains("lie d'ame pour le personnage") || all.Contains("sera lie d'ame");

        // L'onglet ouvert : le plus clair des onglets (Forge, Armure…) et des sous-onglets (Tout, Épées…), blancs, les autres gris.
        string? Selected(List<OcrWord> tabs)
        {
            var lit = tabs.Where(w => w.Rgb >= 0).OrderByDescending(w => Bright(w.Rgb)).ToList();
            return lit.Count >= 2 && Bright(lit[0].Rgb) >= 190 && Bright(lit[1].Rgb) < 160 ? lit[0].Text : lit.Count == 1 && Bright(lit[0].Rgb) >= 190 ? lit[0].Text : null;
        }
        var main = Selected(words.Where(w => MainTabs.Contains(Fold(w.Text))).ToList());
        var tout = words.FirstOrDefault(w => Fold(w.Text) == "tout");
        var sub = tout is null ? null : Selected(words.Where(w => Math.Abs(Center(w) - Center(tout)) < tout.Height && w.Text.Count(char.IsLetter) >= 3).ToList());
        if (sub is not null && Fold(sub) == "tout") sub = null;
        var category = main is null ? sub : sub is null ? main : $"{main} · {sub}";
        return new CraftShot(output, inputs, header is not null, listed, bound, category);
    }

    static int Bright(int rgb) => Math.Max(rgb >> 16 & 255, Math.Max(rgb >> 8 & 255, rgb & 255));

    /// <summary>Une ligne coupée en morceaux là où deux mots voisins sont écartés de plus de 3 hauteurs de texte.</summary>
    static List<List<OcrWord>> Segments(List<List<OcrWord>> lines, double h)
    {
        var parts = new List<List<OcrWord>>();
        foreach (var line in lines)
        {
            var current = new List<OcrWord>();
            foreach (var w in line)
            {
                if (current.Count > 0 && w.X - (current[^1].X + current[^1].Width) > 3 * Math.Max(h, w.Height)) { parts.Add(current); current = []; }
                current.Add(w);
            }
            if (current.Count > 0) parts.Add(current);
        }
        return parts;
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
        var listed = (main.Listed ?? []).Concat(other.Listed ?? []).GroupBy(p => Fold(p.Name)).Select(g => g.OrderByDescending(p => p.Name.Length).First()).ToList();
        return new CraftShot(output, inputs, main.HasMaterials || other.HasMaterials, listed, main.Bound || other.Bound, main.Category ?? other.Category);
    }

    static CraftShotPart Part(string text, string? grade, int? qty)
    {
        // Bruit des icônes lu devant le nom (« g Grimoire… », « -4 Écaille… », « + Grimoire… » pour l'étoile) : mots d'un seul
        // signe ou sans lettre en tête, puis tout ce qui n'est pas une lettre.
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && (tokens[0].Length == 1 || !tokens[0].Any(char.IsLetter))) tokens.RemoveAt(0);
        // Et derrière (« Corne dure de Balaur raffinée @ » : l'icône « % » à droite du titre, lue « @ » ou « % ») : mots sans lettre ni chiffre, sauf les
        // « … » d'un nom coupé ; puis les signes d'icône collés au dernier mot.
        while (tokens.Count > 1 && !tokens[^1].Any(char.IsLetterOrDigit) && !tokens[^1].Any(c => c is '.' or '…' or '„' or ','))
            tokens.RemoveAt(tokens.Count - 1);
        text = string.Join(' ', tokens).TrimEnd('@', '%', '©', '®', '#', '*', '~', '^', ' ');
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
