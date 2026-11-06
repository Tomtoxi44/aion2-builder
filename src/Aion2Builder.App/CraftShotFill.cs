using Aion2Builder.Analysis;

namespace Aion2Builder.App;

/// <summary>
/// Remplit « Noter une recette » avec une capture lue de la fenêtre d'artisanat. Un nom coupé par le jeu (« Minerai d'orichalque
/// écl… ») est complété quand UN seul objet connu (liste des prix, objets du PNJ, recettes notées) commence ainsi et n'a pas un
/// autre rang que la couleur lue ; sinon il reste avec ses « … », à compléter (la case propose les objets qui correspondent).
/// </summary>
static class CraftShotFill
{
    /// <summary>Remplit le formulaire ; renvoie le compte rendu à montrer sous les boutons.</summary>
    public static string Fill(CraftDraft draft, CraftShot shot, MarketData? data)
    {
        var known = (data?.Items ?? []).Select(i => (i.Name, Grade: i.GameGrade))
            .Concat((data?.Npc ?? []).Select(n => (n.Name, Grade: (string?)null)))
            .Concat((data?.Recipes ?? []).SelectMany(r => r.Inputs.Append(r.Output)).Select(p => (p.Name, Grade: (string?)null)))
            .GroupBy(x => Market.Fold(x.Name)).Select(g => g.First()).ToList();
        var completed = new List<string>();
        var open = new List<string>();

        string Resolve(CraftShotPart p)
        {
            var folded = Market.Fold(p.Name);
            if (known.FirstOrDefault(k => Market.Fold(k.Name) == folded) is { Name: { } same } && !p.Cut) return same;
            if (!p.Cut) return p.Name;
            var candidates = known.Where(k => Market.Fold(k.Name).StartsWith(folded) && Market.Fold(k.Name) != folded).ToList();
            // Le rang lu à la couleur écarte un objet d'un autre rang (une pièce bleue n'est pas la jaune du même nom).
            if (p.Grade is { } g && candidates.Count > 1) candidates = candidates.Where(k => k.Grade is null || k.Grade == g).ToList();
            if (candidates.Count == 1)
            {
                completed.Add($"« {p.Name}… » → {candidates[0].Name}");
                return candidates[0].Name;
            }
            open.Add(candidates.Count > 1
                ? $"« {p.Name}… » ({string.Join(", ", candidates.Take(4).Select(c => c.Name))}{(candidates.Count > 4 ? "…" : "")} ?)"
                : $"« {p.Name}… »");
            return p.Name + "…";
        }

        if (shot.Output is { } output)
        {
            draft.Output = Resolve(output);
            draft.OutputQty = "1";
        }
        draft.Inputs.Clear();
        var noQty = new List<string>();
        foreach (var part in shot.Inputs)
        {
            var name = Resolve(part);
            if (part.Qty is null) noQty.Add(name);
            draft.Inputs.Add(new CraftDraftLine { Name = name, Qty = (part.Qty ?? 1).ToString() });
        }
        if (draft.Inputs.Count == 0) draft.Inputs.Add(new CraftDraftLine());

        var report = new List<string>
        {
            shot.Output is null
                ? $"Lu sur la capture : {Fmt.Plural(shot.Inputs.Count, "matériau", "matériaux")}, mais pas l'objet fabriqué : choisis-le."
                : $"Lu sur la capture : {draft.Output} et {Fmt.Plural(shot.Inputs.Count, "matériau", "matériaux")}.",
        };
        if (completed.Count > 0) report.Add("Noms coupés complétés d'après les objets connus, à vérifier : " + string.Join(" ; ", completed) + ".");
        if (open.Count > 0) report.Add("À compléter (le jeu coupe les noms longs, le nom entier s'affiche au survol en jeu) : " + string.Join(" ; ", open) + ".");
        if (noQty.Count > 0) report.Add($"Quantité non lue, mise à 1 : {string.Join(", ", noQty)}.");
        report.Add("Le coût en Kinah n'est pas sur la capture : note-le s'il y en a un, puis « noter la recette ».");
        return string.Join(" ", report);
    }
}
