using Aion2Builder.Analysis;
using Aion2Builder.Combat;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Présentation « Confort » : l'essentiel des écrans qui n'ont pas de note (Combat, Build, Simulation, Progression, Tous les
/// conseils, Récolte, Inventaire, Hôtel des ventes). Chaque écran appelle la sienne là où ses chiffres sont déjà calculés, et
/// la pose dans <see cref="pageLead"/> ; <see cref="Compose"/> la met en tête de la page. Rien n'est calculé en plus.
/// </summary>
sealed partial class ScreenViews
{
    /// <summary>L'essentiel de la page en cours de construction, posé par l'écran lui-même (null : aucun, ou écran noté).</summary>
    PageLead? pageLead;

    void GoMarket(MarketPage page) => update(st => { st.Screen = Screen.Market; st.MarketPage = page; });

    /// <summary>Inventaire : ce que vaut le stock, l'objet le plus précieux, quoi vendre, les objets à nommer.</summary>
    PageLead InventoryLead(InventoryModel m)
    {
        var priced = m.Entries.Where(e => e.Value is not null).ToList();
        var worth = priced.Sum(e => e.Value!.Value);
        var unnamed = m.Entries.Count(e => e.Family == "Sans nom");
        var actions = new List<LeadAction>();
        if (priced.MaxBy(e => e.Value) is { } top)
        {
            var id = top.Id;
            actions.Add(new LeadAction($"Ton objet le plus précieux : {top.Name}", $"{Fmt.Big(top.Qty)} × {Fmt.Big(top.Price!.Value)} = ≈ {Fmt.Big(top.Value!.Value)} Kinah",
                "voir son détail", () => update(st => st.InventoryItem = id), "Où il est rangé, son prix, les recettes qui l'utilisent.", true));
        }
        if (m.Entries.Any(e => e.Family == "Récolte"))
            actions.Add(new LeadAction("Vendre ou garder ta récolte ?", "Ce que vaut ton stock de récolte, et ce qui a monté ou baissé cette semaine.", "voir", () => GoMarket(MarketPage.Stock)));
        if (unnamed > 0)
            actions.Add(new LeadAction($"{Fmt.Plural(unnamed, "objet n'a", "objets n'ont")} pas de nom", "Le jeu ne donne que leur numéro : nomme-les une fois, le nom vaut pour tous les joueurs.",
                "les nommer", () => update(st => { st.InventoryFamily = "Sans nom"; st.InventoryItem = null; })));
        var noPrice = m.Entries.Count - priced.Count;
        return new PageLead(m.All ? "Le stock de tes persos vaut " : "Ton stock vaut ", worth > 0 ? $"≈ {Fmt.Big(worth)}" : "0", worth > 0 ? Tok.Good : Tok.Text, " Kinah aux prix connus.",
            $"{Fmt.Plural(m.Entries.Count, "objet différent", "objets différents")}" + (noPrice > 0 ? $", dont {noPrice} sans prix noté" : "") +
            (m.Kinah > 0 ? $" ; {Fmt.Big(m.Kinah)} Kinah en poche." : "."), actions);
    }

    /// <summary>Hôtel des ventes, derniers prix : combien de prix ont bougé en 7 jours, la plus forte hausse, la plus forte baisse, noter un prix.</summary>
    PageLead MarketLead(MarketData data, string shown, int items, List<(string Key, string Name, long Price, double Pct)> moves, int waiting)
    {
        var actions = new List<LeadAction>();
        if (waiting > 0)
            actions.Add(new LeadAction($"{Fmt.Plural(waiting, "ligne lue attend", "lignes lues attendent")} ta vérification", "Une capture de l'hôtel a été lue : vérifie les prix avant de les noter.",
                "vérifier", () => update(st => st.MarketPage = MarketPage.Add)));
        LeadAction Move((string Key, string Name, long Price, double Pct) x)
        {
            var key = x.Key;
            return new LeadAction($"{x.Name} a {(x.Pct > 0 ? "monté" : "baissé")} de {(x.Pct > 0 ? "+" : "−")}{Math.Abs(x.Pct):0.#} %", $"{Fmt.Big(x.Price)} Kinah l'unité au dernier relevé",
                "voir ses prix", () => update(st => { st.MarketItem = key; st.MarketConfirm = null; }), "Sa courbe et chacun de ses relevés.", x.Pct > 0);
        }
        // Une hausse de plus de 300 % ou une baisse de plus de 75 % en une semaine est sans doute une faute de frappe : pas mise en avant.
        var ups = moves.Where(x => x.Pct is > 0 and <= 300).OrderByDescending(x => x.Pct).ToList();
        var downs = moves.Where(x => x.Pct is < 0 and >= -75).OrderBy(x => x.Pct).ToList();
        if (ups.Count > 0) actions.Add(Move(ups[0]));
        if (downs.Count > 0) actions.Add(Move(downs[0]));
        if (actions.Count < 3)
            actions.Add(new LeadAction("Tu as vu un prix à l'hôtel ?", "Note-le depuis une capture d'écran, ou à la main : il sert à tous les joueurs.", "noter un prix", () => update(st => st.MarketPage = MarketPage.Add)));
        var label = data.Label(shown);
        return new PageLead("", moves.Count == 0 ? "Aucun" : moves.Count.ToString(), moves.Count == 0 ? Tok.Text : Accent.Main,
            (moves.Count > 1 ? " prix ont bougé" : moves.Count == 1 ? " prix a bougé" : " prix n'a bougé") + $" cette semaine sur {label}.",
            $"{Fmt.Plural(items, "objet a", "objets ont")} un prix noté sur ce marché.", actions.Take(3).ToList());
    }

    /// <summary>Récolte : les récoltes du jour face au plafond (ou le total de la période), ce qu'elles valent, quoi en faire.</summary>
    PageLead GatherLead(GatherServer g, int cap, int span, bool today, long? value)
    {
        var items = g.Items ?? [];
        var total = items.Sum(i => (long)i.Qty);
        var radiant = items.Where(i => i.Rank == 4).Sum(i => (long)i.Qty);
        var left = Math.Max(0, cap - g.Count);
        var actions = new List<LeadAction>();
        if (total > 0)
            actions.Add(new LeadAction(value is { } v ? $"Cette récolte vaut ≈ {Fmt.Big(v)} Kinah" : "Que vaut cette récolte ?",
                "Vendre ou garder : ton stock de récolte au dernier prix, avec ce qui a monté cette semaine.", "voir quoi vendre", () => GoMarket(MarketPage.Stock)));
        actions.Add(new LeadAction("Que fabriquer avec ta récolte ?", "Le Craft dit quelles recettes rapportent plus que vendre les ingrédients.", "ouvrir le Craft",
            () => update(st => { st.Screen = Screen.Craft; st.CraftPage = CraftPage.Overview; })));
        var sub = span > 0 ? $"{Fmt.Big(total)} ressources ramassées" + (radiant > 0 ? $", dont {Fmt.Big(radiant)} éclatantes." : ".")
            : left == 0 ? "Le plafond du jour est atteint."
            : today ? $"Il t'en reste {Fmt.Big(left)} avant le plafond du jour." : $"Il en restait {Fmt.Big(left)} avant le plafond.";
        return span > 0
            ? new PageLead($"En {span} jours, tu as fait ", Fmt.Big(g.Count), Accent.Main, g.Count > 1 ? " récoltes." : " récolte.", sub, actions)
            : new PageLead(today ? "Aujourd'hui, tu as fait " : "Ce jour-là, tu as fait ", Fmt.Big(g.Count), g.Count >= cap ? Tok.Good : Accent.Main, $" récoltes sur {Fmt.Big(cap)}.", sub, actions);
    }

    /// <summary>Tous les conseils : combien il en reste, et les trois qui rapportent le plus ; la liste complète reste dessous, telle quelle.</summary>
    PageLead DetailsLead()
    {
        var todo = r.ActionPlan.Where(f => !IsDone(f)).ToList();
        var (advice, important, _) = Fmt.Counts(r.Findings);
        return new PageLead("Il te reste ", advice.ToString(), advice == 0 ? Tok.Good : important > 0 ? Tok.Warn : Tok.Text, advice > 1 ? " conseils à suivre." : " conseil à suivre.",
            advice == 0 ? "Rien à faire pour l'instant." : (important > 0 ? $"{Fmt.Plural(important, "important")}. " : "") + "Les trois qui rapportent le plus, dans l'ordre :",
            todo.Take(3).Select(f => new LeadAction(f.Title, f.Action, "y aller", () => Go(f), f.Detail)).ToList()) { Fold = false, Ordered = true };
    }

    /// <summary>Progression : la note (sinon le niveau d'objet) entre le premier import et aujourd'hui.</summary>
    PageLead? ProgressLead(List<(string Title, List<ChartPoint> Points, Func<double, string> Format)> series)
    {
        var scored = series.Count > 2 && series[2].Points.Count >= 2;
        var pick = scored ? series[2] : series.FirstOrDefault(x => x.Points.Count >= 2);
        if (pick.Points is not { Count: >= 2 } points) return null;
        var (first, last) = (points[0], points[^1]);
        var delta = last.Value - first.Value;
        var what = scored ? "ta note" : "ton niveau d'objet";
        var actions = new List<LeadAction>
        {
            new("Tu as progressé depuis ?", "Relis ton personnage sur le site officiel : chaque import qui change quelque chose ajoute un point ici.", "actualiser", refresh),
            new("Te comparer à un autre perso", "Tes pièces, tes stats et tes notes à côté de celles d'un perso que tu as déjà ouvert.", "comparer", () => update(st => st.ProgressCompare = true)),
        };
        return new PageLead($"Depuis le {first.At.ToLocalTime():dd/MM}, {what} est passé{(scored ? "e" : "")} de {pick.Format(first.Value)} à ", pick.Format(last.Value),
            delta > 0 ? Tok.Good : delta < 0 ? Tok.Bad : Tok.Text, ".",
            delta == 0 ? "Aucun changement entre le premier et le dernier import." : $"{(delta > 0 ? "+" : "−")}{pick.Format(Math.Abs(delta))} en {Fmt.Plural(points.Count, "import")}.", actions);
    }

    /// <summary>Simulation : la note globale avant et après les changements cochés ; l'écran reste entier (c'est un outil).</summary>
    static PageLead SimulateLead(bool any, int before, int after) => any
        ? new PageLead($"Avec ces changements, ta note passerait de {before} à ", after.ToString(), after > before ? Tok.Good : after < before ? Tok.Bad : Tok.Text, ".",
            after == before ? "Aucun effet sur la note globale : regarde les familles de stats dessous." : "Calculé sur une copie de ton personnage : rien n'est enregistré.", []) { Fold = false }
        : new PageLead("Coche un changement : ta note se recalcule ", "aussitôt", Tok.Text, ".",
            "Une pièce remise à niveau, un arcane, des points de Daevanion : tu vois ce que ça rapporte avant de le faire en jeu.", []) { Fold = false };

    /// <summary>Build : quel build suivent les conseils, et l'écart de dégâts entre les deux rotations simulées.</summary>
    PageLead BuildLead(bool designed, bool hasDesigned, double playersDps, double designedDps)
    {
        var actions = new List<LeadAction>
        {
            new("Voir les deux rotations", "Le même moteur joue les deux rotations pendant 2 minutes, sur 1 cible ou sur un pack de 4.", "voir les rotations", () => update(st => st.BuildPage = BuildPage.Rotations)),
        };
        if (hasDesigned)
            actions.Add(new LeadAction("Ce qui change entre les deux builds", "Chaque différence, et pourquoi.", "voir les différences", () => update(st => st.BuildPage = BuildPage.Differences)));
        actions.Add(new LeadAction("Régler tes compétences", "Les points à déplacer pour suivre ce build.", "ouvrir Compétences", () => GoTo(Screen.Skills)));
        string sub;
        if (playersDps > 0 && designedDps > 0 && Math.Abs(designedDps / playersDps - 1) >= 0.005)
        {
            var gap = designedDps / playersDps - 1;
            sub = $"En simulation, la rotation {(gap > 0 ? "conçue" : "des joueurs")} fait {Math.Abs(gap):P0} de dégâts de plus que l'autre.";
        }
        else sub = hasDesigned ? "En simulation, les deux rotations font les mêmes dégâts." : "Il n'y a pas encore de build conçu pour cette classe.";
        return new PageLead("Tes conseils suivent le build ", designed ? "conçu" : "des joueurs", Accent.Main, ".", sub, actions);
    }

    /// <summary>Combat : le DPS du combat ouvert et, au plus, trois points à corriger au prochain (rien à cliquer : c'est en jeu que ça se corrige).</summary>
    PageLead CombatLead(CombatReport cr, List<Finding> todo)
    {
        var log = cr.Log;
        return new PageLead($"Sur {log.Target}, tu as fait ", Fmt.Big(log.Dps), Accent.Main, $" de DPS en {log.Duration:0} s.",
            todo.Count == 0 ? "Rien à corriger sur ce combat." : $"{Fmt.Plural(todo.Count, "point")} à corriger au prochain combat" + (todo.Count > 3 ? ", les trois premiers :" : " :"),
            todo.Take(3).Select(f => new LeadAction(f.Title, f.Action ?? f.Detail, "", null, f.Detail)).ToList()) { Ordered = true };
    }
}
