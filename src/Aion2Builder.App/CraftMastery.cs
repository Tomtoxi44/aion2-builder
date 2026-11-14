using Aion2Builder.Analysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Craft : ta maîtrise par métier. Rien ne la donne (ni le site officiel, ni le jeu lu par le Pet Meter) : tu la tapes, une fois
/// par personnage, puisque chaque perso monte ses métiers à part. Une recette qui en demande plus sort de « Ce qui rapporte »,
/// de la proposition et des achats rentables ; une carte montre ce que débloque le palier suivant de chaque métier.
/// </summary>
sealed partial class ScreenViews
{
    /// <summary>Les métiers des recettes officielles (Alchimie, Armure, Artisanat, Cuisine, Forge).</summary>
    static List<string> CraftProfessions() =>
        Market.OfficialRecipes.Select(rc => rc.Profession).OfType<string>().Distinct().OrderBy(p => p, French).ToList();

    /// <summary>
    /// La ligne « Ta maîtrise » de la vue d'ensemble : une case par métier pour le perso ouvert. Une case vide : maîtrise inconnue,
    /// toutes les recettes du métier restent proposées. La saisie est gardée à la sortie de la case (ou sur Entrée).
    /// </summary>
    Control? CraftMasteryRow(CraftModel m)
    {
        var jobs = CraftProfessions();
        if (jobs.Count == 0) return null;
        var who = r.Character.Name;
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Tip(T($"Maîtrise de {who}", 13, Tok.Text, FontWeight.Medium).WithMargin(0, 0, 14, 0),
            "Ton niveau dans chaque métier, lu dans la fenêtre d'artisanat du jeu. Rien ne le donne au Builder : tape-le ici. Chaque perso a le sien."));
        foreach (var job in jobs)
        {
            var name = job;
            var box = new TextBox { Text = m.Mastery.TryGetValue(job, out var have) ? have.ToString() : "", Width = 58, PlaceholderText = "?", MaxLength = 3 };
            void Commit()
            {
                var typed = Num(box.Text) is { } v ? (int?)Math.Min(v, 999) : null;
                var before = m.Mastery.TryGetValue(name, out var old) ? old : (int?)null;
                if (typed == before) return;
                update(st =>
                {
                    if (!st.CraftMastery.TryGetValue(who, out var mine)) st.CraftMastery[who] = mine = [];
                    if (typed is { } level) mine[name] = level; else mine.Remove(name);
                    if (mine.Count == 0) st.CraftMastery.Remove(who);
                });
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, k) => { if (k.Key == Avalonia.Input.Key.Enter) { k.Handled = true; Commit(); } };
            row.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 14, 2),
                Children = { T(job, 12, Tok.Text2), box },
            });
        }
        var locked = m.Plans.Count(p => p.Locked is not null);
        row.Children.Add(T(m.Mastery.Count == 0 ? "vide : toutes les recettes sont proposées · puis Entrée"
            : locked > 0 ? $"{Fmt.Plural(locked, "recette")} hors de portée, rangée{(locked > 1 ? "s" : "")} en bas de la liste" : "toutes les recettes de ces métiers sont à ta portée",
            11, Tok.Muted, mono: true));
        return Card(row, padding: new Thickness(14, 8, 14, 8));
    }

    /// <summary>
    /// « Ce que débloque ton prochain palier » : pour chaque métier dont tu as indiqué la maîtrise, les recettes du premier palier
    /// au-dessus, avec ce qu'elles rapporteraient par fabrication aux prix d'aujourd'hui. Null sans maîtrise indiquée.
    /// </summary>
    Control? CraftNextTierCard(CraftModel m)
    {
        if (m.Mastery.Count == 0) return null;
        const string cols = "*,210,170";
        var rows = new List<Control>();
        foreach (var (job, have) in m.Mastery.OrderBy(x => x.Key, French))
        {
            var above = m.Plans.Where(p => p.Locked is not null && p.Recipe.Profession == job && p.Recipe.Mastery is not null).ToList();
            if (above.Count == 0) continue;
            var next = above.Min(p => p.Recipe.Mastery!.Value);
            foreach (var p in above.Where(p => p.Recipe.Mastery == next).OrderByDescending(p => p.Gain ?? long.MinValue).Take(4))
            {
                var id = p.Recipe.Id;
                var rc = p.Recipe;
                Control worth = p.Gain is { } g
                    ? T($"{(g >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(g))} par fabrication", 12, g > 0 ? Tok.Good : Tok.Text2, FontWeight.SemiBold, mono: true).AlignRight()
                    : p.C.ForSelf && p.Cost is { } cost ? T($"pour toi : coûte {Short(cost)}", 12, Tok.Text2, mono: true).AlignRight()
                    : T("prix manquant", 12, Tok.Faint, mono: true).AlignRight();
                rows.Add(TableRow(Columns(cols, MarketItemLine(p.Item, rc.Output.Qty > 1 ? $"{rc.Output.Name} ×{rc.Output.Qty}" : rc.Output.Name, 13),
                    T($"{job} · maîtrise {next} (encore {next - have})", 12, Tok.Text2), worth), false, () => OpenRecipe(id)));
            }
            var more = above.Count(p => p.Recipe.Mastery == next) - 4;
            if (more > 0)
                rows.Add(new Border { Padding = new Thickness(20, 3), Child = T($"et {Fmt.Plural(more, "autre recette", "autres recettes")} de {job} à la maîtrise {next}", 11, Tok.Muted) });
        }
        if (rows.Count == 0) return null;
        return TableCard("Ce que débloque ton prochain palier", T("aux prix d'aujourd'hui · clic : la recette", 11, Tok.Muted, mono: true),
            TableHeader(cols, "Objet fabriqué", "Métier · palier", "Ce que ça rapporterait"), rows);
    }
}
