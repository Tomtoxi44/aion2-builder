using Aion2Builder.Analysis;
using Aion2Builder.Combat;
using Aion2Builder.GameData;
using Aion2Builder.Model;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Écran Combat, un onglet par joueur (choix « A » de la passe du 8 novembre) : Groupe, Toi, puis chaque joueur du combat
/// (Pet Meter v0.19.0 et plus). Chaque allié a la même page que la tienne : son DPS, ce qu'il a bien fait, ce qu'il a mal
/// fait, ses sorts (lancers face aux recharges, parts des dégâts) et sa frise face à la tienne. Sa fiche (stats, équipement)
/// n'est qu'un lien discret de l'inspecteur.
/// </summary>
sealed partial class ScreenViews
{
    /// <summary>Onglet « Groupe » de <see cref="UiState.CombatPlayer"/>.</summary>
    const string GroupTab = "*";

    readonly Dictionary<string, CombatReport?> allyReports = [];

    /// <summary>La rotation d'un joueur du combat analysée comme la tienne ; null sans ses lancers datés (Pet Meter v0.21.1) ou classe inconnue.</summary>
    CombatReport? AllyReport(CombatLog log, CombatPlayer p)
    {
        var key = PlayerKey(p);
        if (allyReports.TryGetValue(key, out var known)) return known;
        CombatReport? report = null;
        if (CombatAnalyzer.PlayerLog(log, p) is { } theirs)
            try { report = new CombatAnalyzer(db).Analyze(theirs, db.Resolve(p.ClassCode!, log.Kind == "pvp" ? GameMode.PvP : GameMode.PvE)); }
            catch (InvalidOperationException) { }  // classe inconnue des données
        return allyReports[key] = report;
    }

    static List<Finding> Fixes(CombatReport report) =>
        report.Findings.Where(f => f.Severity <= Severity.Advice).OrderBy(f => f.Severity).ThenByDescending(f => f.Priority).ToList();

    /// <summary>Le joueur de l'onglet ouvert ; null pour toi ou le groupe (et pour un joueur qui n'est pas dans ce combat).</summary>
    CombatPlayer? OpenAlly(CombatLog log) =>
        s.CombatPlayer is { } key && key != GroupTab ? log.Players?.FirstOrDefault(p => !p.Me && PlayerKey(p) == key) : null;

    bool GroupOpen(CombatLog log) => s.CombatPlayer == GroupTab && log.Players?.Any(p => !p.Me) == true;

    /// <summary>Icône d'un sort : la tienne pour ta classe, sinon celle d'un perso de sa classe importé sur ce PC.</summary>
    Control? IconFor(string name, string? classCode, double size = 18) =>
        classCode is null || classCode == r.Character.ClassCode ? SkillIcon(name, size, false) : SpellIcon(name, null, classCode, size);

    /// <summary>
    /// Les onglets du combat : Groupe, Toi, puis chaque joueur par DPS ; chacun avec son DPS et une pastille (points à corriger,
    /// ✓ si rien, rien si ses lancers manquent).
    /// </summary>
    Control PlayerTabs(CombatReport mine, List<CombatPlayer> others)
    {
        var log = mine.Log;
        var row = new WrapPanel();
        void Tab(string label, string? cls, double? dps, int? fixes, bool on, string? key, string tip)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            line.Children.Add(T(label, 13, on ? Tok.Text : Tok.Text2, on ? FontWeight.SemiBold : FontWeight.Normal));
            if (cls is not null) line.Children.Add(T(cls, 11, Tok.Muted).WithMargin(0, 1, 0, 0));
            if (dps is { } d) line.Children.Add(T(Fmt.Big(d), 12, on ? Accent.Main : Tok.Text, FontWeight.SemiBold, mono: true).WithMargin(0, 1, 0, 0));
            if (fixes is { } n)
                line.Children.Add(new Border
                {
                    MinWidth = 18, Height = 17, CornerRadius = new CornerRadius(9), Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center,
                    Background = Tok.Alpha(n == 0 ? Tok.Good : Tok.Warn, 0.18),
                    Child = T(n == 0 ? "✓" : n.ToString(), 11, n == 0 ? Tok.Good : Tok.Warn, FontWeight.SemiBold, mono: true).Centered(),
                });
            var tab = Clickable(line, () => update(st => { st.CombatPlayer = key; st.CombatSkill = null; }), on ? Tok.Panel : null, Tok.Hover, new Thickness(12, 7, 12, 6));
            tab.BorderBrush = on ? Accent.Main : Tok.Border;
            tab.BorderThickness = on ? new Thickness(0, 2, 0, 0) : new Thickness(0, 0, 0, 1);
            tab.CornerRadius = new CornerRadius(4, 4, 0, 0);
            row.Children.Add(Tip(tab, tip).WithMargin(0, 0, 2, 0));
        }
        var all = log.Players!;
        Tab("Groupe", null, null, null, s.CombatPlayer == GroupTab, GroupTab, $"Les {all.Count} joueurs côte à côte : DPS, part des dégâts, points à corriger.");
        var me = all.FirstOrDefault(p => p.Me);
        var myFixes = mine.Findings.Count(f => f.Severity <= Severity.Advice);
        Tab("Toi", r.Profile.Class.Name, me?.Dps ?? log.Dps, myFixes, OpenAlly(log) is null && s.CombatPlayer != GroupTab, null, "Ta page : ton DPS, tes points à corriger, tes sorts, ta frise.");
        foreach (var p in others.OrderByDescending(p => p.Dps))
        {
            var report = AllyReport(log, p);
            var cls = p.ClassCode is { } code ? db.FindClass(code)?.Name : null;
            var name = PlayerName(p);
            Tab(name, p.Name is null ? null : cls, p.Dps, report is null ? null : Fixes(report).Count, OpenAlly(log) == p, PlayerKey(p),
                report is null ? $"{name} : ses dégâts par sort (ses lancers datés manquent : Pet Meter v0.21.1 ou plus pendant le combat)."
                : $"{name} : ce qu'il a bien fait, ce qu'il a mal fait, ses sorts et sa frise face à la tienne.");
        }
        return new Border { BorderBrush = Tok.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
    }

    /// <summary>Onglet Groupe : un joueur par ligne (DPS en barre, part, en bref) ; clic : son onglet.</summary>
    Control GroupView(CombatLog log)
    {
        var players = log.Players!.OrderByDescending(p => p.Damage).ToList();
        var top = players.Max(p => p.Dps);
        const string cols = "*,*,64,56,*";
        var table = new StackPanel();
        table.Children.Add(TableHeader(cols, "Joueur", "DPS", "", "Part", "En bref"));
        foreach (var p in players)
        {
            var cls = p.ClassCode is { } code ? db.FindClass(code)?.Name : null;
            var name = p.Me ? "Toi" : PlayerName(p);
            var report = p.Me ? Combat : AllyReport(log, p);
            var brief = new WrapPanel();
            if (report is null)
                brief.Children.Add(Tip(Badge("lancers non lus", Tok.Muted), "Ses lancers datés ne sont pas dans ce combat : Pet Meter v0.21.1 ou plus, joueur près de toi.").WithMargin(0, 0, 6, 0));
            else
            {
                var fixes = Fixes(report);
                var idle = fixes.FirstOrDefault(f => f.Title.StartsWith("Temps morts", StringComparison.Ordinal));
                brief.Children.Add(Badge(idle is null ? "bon rythme" : "pauses", idle is null ? Tok.Good : Tok.Warn).WithMargin(0, 0, 6, 0));
                brief.Children.Add(Badge(fixes.Count == 0 ? "rien à corriger" : Fmt.Plural(fixes.Count, "point à corriger", "points à corriger"), fixes.Count == 0 ? Tok.Good : Tok.Warn));
            }
            var bar = new Border
            {
                Height = 6, CornerRadius = new CornerRadius(3), Background = p.Me ? Accent.Main : Tok.Text2, HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(4, 160 * p.Dps / Math.Max(1, top)), VerticalAlignment = VerticalAlignment.Center,
            };
            var who = new StackPanel { Spacing = 1, Children = { T(name, 13, p.Me ? Accent.Main : null, p.Me ? FontWeight.SemiBold : FontWeight.Normal) } };
            if (cls is not null) who.Children.Add(T(cls + (p.Power is { } pw ? $" · puissance {Fmt.Big(pw)}" : ""), 11, Tok.Muted));
            var row = Columns(cols, who, bar, T(Fmt.Big(p.Dps), 12, p.Me ? Accent.Main : Tok.Text, mono: true).AlignRight(),
                T(Fmt.Pct(p.Share), 12, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(), brief.WithMargin(12, 0, 0, 0));
            var key = p.Me ? null : PlayerKey(p);
            var line = Clickable(row, () => update(st => { st.CombatPlayer = key; st.CombatSkill = null; }), p.Me ? Accent.Dim : null, Tok.Hover, new Thickness(20, 7));
            ToolTip.SetTip(line, PlayerTip(p, $"{name}{(cls is null ? "" : $" · {cls}")} : {Fmt.Big(p.Damage)} dégâts" + (p.Crit is { } c ? $", {Fmt.Pct(c)} de critiques" : "") + " · clic : sa page"));
            table.Children.Add(line);
        }
        var note = log.GroupDuration is { } group
            ? $"DPS et part sur le combat du groupe ({Duration(group)}, du premier au dernier coup de quiconque sur {Who(log)}). Clic sur un joueur : sa page."
            : "DPS et part sur le combat du groupe. Clic sur un joueur : sa page.";
        var page = new StackPanel { Spacing = 14 };
        page.Children.Add(Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new Border { BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Skin.Radius), ClipToBounds = true, Child = table },
                T(note, 12, Tok.Muted, wrap: true).WithLineHeight(1.4),
            },
        }, "Le groupe sur ce combat", T(Fmt.Plural(players.Count, "joueur"), 12, Tok.Muted)));
        if (log.Casts is { Count: > 2 } && log.Duration > 0) page.Children.Add(RotationStrip(log));
        return page;
    }

    /// <summary>
    /// Onglet d'un joueur du combat : la même page que la tienne — son DPS, ce qu'il a bien fait, ce qu'il a mal fait, où vont ses
    /// dégâts et ses sorts à recharge, sa frise face à la tienne. Sans ses lancers datés : ses dégâts par sort seulement.
    /// </summary>
    Control AllyView(CombatLog log, CombatPlayer p)
    {
        var page = new StackPanel { Spacing = 14 };
        var name = PlayerName(p);
        var cls = p.ClassCode is { } code ? db.FindClass(code)?.Name : null;
        var report = AllyReport(log, p);
        var fixes = report is null ? [] : Fixes(report);
        var rank = log.Players!.OrderByDescending(x => x.Dps).ToList().IndexOf(p) + 1;

        // 1. Son DPS et deux badges.
        var badges = new WrapPanel();
        if (report is not null)
        {
            var idle = fixes.FirstOrDefault(f => f.Title.StartsWith("Temps morts", StringComparison.Ordinal));
            badges.Children.Add(Badge(idle is null ? "Bon rythme : aucune pause" : idle.Title, idle is null ? Tok.Good : Tok.Warn).WithMargin(0, 0, 8, 6));
            badges.Children.Add(Badge(fixes.Count == 0 ? "✓ rien à corriger" : $"{fixes.Count} point{(fixes.Count > 1 ? "s" : "")} à corriger", fixes.Count == 0 ? Tok.Good : Tok.Warn)
                .WithMargin(0, 0, 8, 6));
        }
        else badges.Children.Add(Badge("ses lancers ne sont pas dans ce combat", Tok.Muted));
        page.Children.Add(Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                T($"{name}{(cls is null ? "" : $" · {cls}")}{(p.Power is { } pw ? $" · puissance {Fmt.Big(pw)}" : "")}", 13, Tok.Muted, wrap: true),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 12,
                    Children =
                    {
                        T(Fmt.Big(p.Dps), 44, Accent.Main, FontWeight.SemiBold, mono: true), T("DPS", 15, Tok.Text2).WithMargin(0, 0, 8, 0),
                        T($"{Fmt.Pct(p.Share)} des dégâts du groupe · {rank}{(rank == 1 ? "er" : "e")} sur {log.Players!.Count}", 13, Tok.Text2).WithMargin(0, 14, 0, 0),
                    },
                },
                badges,
            },
        }, padding: new Thickness(22, 18)));

        if (report is null)
        {
            // Sans lancers datés : ses dégâts par sort, tels que le Pet Meter les a comptés.
            var max = p.Skills.Select(k => k.Share).DefaultIfEmpty(1).Max();
            var list = new StackPanel { Spacing = 8 };
            foreach (var k in p.Skills.OrderByDescending(k => k.Share).Take(10))
                list.Children.Add(BarRow(k.Name, k.Share / Math.Max(0.0001, max), Fmt.Pct(k.Share) + (k.Casts > 0 ? $" · {k.Casts}×" : ""), Tok.Text2,
                    nameWidth: 210, icon: IconFor(k.Name, p.ClassCode)));
            list.Children.Add(T("Ses lancers datés ne sont pas dans ce combat : il faut le Pet Meter v0.21.1 ou plus pendant le combat, et le joueur près de toi. " +
                                "Sans eux, pas de lancers face aux recharges ni de frise : seulement ses dégâts par sort.", 12, Tok.Muted, wrap: true).WithLineHeight(1.4));
            page.Children.Add(Card(list, "Où vont ses dégâts", T("mesuré sur ce combat", 12, Tok.Muted)));
            return page;
        }

        // 2. Ce qu'il a bien fait, ce qu'il a mal fait.
        static Control Item(string mark, IBrush color, Control? icon, string title, string? detail)
        {
            var text = new StackPanel { Spacing = 2, Children = { T(title, 13, Tok.Text, FontWeight.SemiBold, wrap: true) } };
            if (detail is not null) text.Children.Add(T(detail, 12, Tok.Text2, wrap: true).WithLineHeight(1.4));
            var head = icon is null ? (Control)text : WithIcon(icon, text, 8);
            return ColumnsSpaced("16,*", 6, T(mark, 13, color, FontWeight.Bold, mono: true), head);
        }
        var good = new StackPanel { Spacing = 10 };
        foreach (var x in report.Skills.Where(x => x.Possible is { } pos && pos >= 2 && !x.Situational && x.Skill.Uses >= 0.9 * pos)
                     .OrderByDescending(x => x.Skill.Share).Take(3))
            good.Children.Add(Item("✓", Tok.Good, IconFor(x.Skill.Name, p.ClassCode, 16), $"« {x.Skill.Name} » relancé dès sa recharge",
                $"{x.Skill.Uses} lancers sur {x.Possible} possibles · {Fmt.Pct(x.Skill.Share)} de ses dégâts"));
        if (!fixes.Any(f => f.Title.StartsWith("Temps morts", StringComparison.Ordinal)))
            good.Children.Add(Item("✓", Tok.Good, null, "Aucune pause de plus de 2,5 s", $"{Fmt.Plural(report.Log.Casts!.Count, "lancer")} sur {Duration(report.Log.Duration)}"));
        if (rank == 1) good.Children.Add(Item("✓", Tok.Good, null, "Meilleur DPS du groupe", null));
        if (good.Children.Count == 0) good.Children.Add(T("Rien de marquant : regarde ce qui est à corriger.", 12, Tok.Muted, wrap: true));
        var bad = new StackPanel { Spacing = 10 };
        foreach (var f in fixes.Take(5))
            bad.Children.Add(Item("!", f.Severity == Severity.Important ? Tok.Warn : Tok.Text2, FindingIcon(f.Title, p.ClassCode), f.Title, f.Action ?? f.Detail));
        if (fixes.Count > 5) bad.Children.Add(T($"et {Fmt.Plural(fixes.Count - 5, "autre point", "autres points")}", 12, Tok.Muted));
        if (fixes.Count == 0) bad.Children.Add(T("Rien à corriger sur sa rotation.", 12, Tok.Good, wrap: true));
        page.Children.Add(Columns("*,*",
            Card(good, "Ce qu'il a bien fait", T("d'après ses lancers", 12, Tok.Muted)).WithMargin(0, 0, 12, 0),
            Card(bad, "Ce qu'il a mal fait", T(fixes.Count == 0 ? "rien" : Fmt.Plural(fixes.Count, "point"), 12, fixes.Count == 0 ? Tok.Good : Tok.Warn))));

        // 3. Où vont ses dégâts, ses sorts à recharge : comme les tiens.
        page.Children.Add(DamageAndRecharge(report, p.ClassCode, false));

        // 4. Sa frise face à la tienne.
        if (log.Casts is { Count: > 2 } && log.Duration > 0) page.Children.Add(RotationStrip(log, p));
        page.Children.Add(T($"D'après {Fmt.Plural(report.Log.Casts!.Count, "lancer")} vus par ton Pet Meter ; les buffs des autres joueurs ne sont pas lus.",
            11, Tok.Muted, wrap: true));
        return page;
    }

    /// <summary>
    /// « Où vont les dégâts » (barres par sort) et « Sorts à recharge » (lancés / possibles en cases), côte à côte : les tiens
    /// (clic : le sort dans l'inspecteur) ou ceux d'un joueur du combat.
    /// </summary>
    Control DamageAndRecharge(CombatReport cr, string? classCode, bool mine)
    {
        var log = cr.Log;
        void Select(string name) => update(st => st.CombatSkill = name);
        var max = cr.Skills.Select(x => x.Skill.Share).DefaultIfEmpty(1).Max();
        var damage = new StackPanel { Spacing = 8 };
        foreach (var x in cr.Skills.OrderByDescending(x => x.Skill.Share).Take(8))
            damage.Children.Add(BarRow(x.Skill.Name, x.Skill.Share / Math.Max(0.0001, max), Fmt.Pct(x.Skill.Share), mine ? Accent.Main : Tok.Text2,
                onClick: mine ? () => Select(x.Skill.Name) : null, tip: x.Verdict, selected: mine && x.Skill.Name == s.CombatSkill, icon: IconFor(x.Skill.Name, classCode)));
        var rest = cr.Skills.OrderByDescending(x => x.Skill.Share).Skip(8).ToList();
        if (rest.Count > 0)
            damage.Children.Add(T($"{rest.Count} autre{(rest.Count > 1 ? "s" : "")} sort{(rest.Count > 1 ? "s" : "")} : {Fmt.Pct(rest.Sum(x => x.Skill.Share))}" +
                                  (mine ? " · clic sur un sort = son détail à droite" : ""), 12, Tok.Muted));
        var timed = cr.Skills.Where(x => x.Possible is not null).OrderByDescending(x => x.Spell?.Cooldown ?? 0).ToList();
        var recharge = new StackPanel { Spacing = 8 };
        foreach (var x in timed)
        {
            var done = x.Skill.Uses;
            var possible = x.Possible!.Value;
            var color = done >= possible ? Tok.Good : done >= 0.8 * possible || x.Situational ? Tok.Text2 : Tok.Warn;
            var row = ColumnsSpaced("190,*,58", 10, WithIcon(IconFor(x.Skill.Name, classCode), T(x.Skill.Name, 13, mine && x.Skill.Name == s.CombatSkill ? Accent.Main : null)),
                Pips(done, possible), T($"{done} / {possible}", 12, color, mono: true).AlignRight());
            Control line = mine ? Clickable(row, () => Select(x.Skill.Name), hover: Tok.Hover, padding: new Thickness(0, 2)) : row.WithMargin(0, 2);
            recharge.Children.Add(Tip(line, x.Verdict));
        }
        if (timed.Count == 0) recharge.Children.Add(T("Aucun sort à recharge reconnu dans ce combat.", 13, Tok.Muted));
        recharge.Children.Add(T("vert = lancé · orange = occasion perdue · vert pâle = au-delà de la recharge (réinitialisations) ; " +
                                "chiffre gris : sort de situation (provocation, protection, soin, déplacement), pas jugé sur ses lancers", 12, Tok.Muted, wrap: true));
        return Columns("*,*",
            Card(damage, mine ? "Où vont tes dégâts" : "Où vont ses dégâts", T(log.Source == "Pet Meter" || log.Pages > 0 ? "mesuré sur ce combat" : "", 12, Tok.Muted)).WithMargin(0, 0, 12, 0),
            Card(recharge, mine ? "Tes sorts à recharge" : "Ses sorts à recharge", T("lancés / possibles", 12, Tok.Muted)));
    }

    /// <summary>Inspecteur, onglet d'un joueur ou du groupe : qui c'est, ses chiffres, et (discret) sa fiche.</summary>
    Control PlayerInspector(CombatLog log, CombatPlayer? p)
    {
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(14) };
        if (p is null)
        {
            body.Children.Add(T("Le groupe", 17, weight: FontWeight.SemiBold));
            body.Children.Add(KeyValue(
            [
                ("joueurs", log.Players!.Count.ToString(), null),
                ("durée", log.GroupDuration is { } g ? Duration(g) : Duration(log.Duration), null),
                ("dégâts", Fmt.Big(log.Players!.Sum(x => x.Damage)), null),
                ("DPS du groupe", Fmt.Big(log.Players!.Sum(x => x.Dps)), Accent.Main),
            ]));
            body.Children.Add(T("Clic sur un joueur, ou sur son onglet en haut : sa page, comme la tienne.", 12, Tok.Muted, wrap: true));
            return body;
        }
        var cls = p.ClassCode is { } code ? db.FindClass(code)?.Name : null;
        var report = AllyReport(log, p);
        body.Children.Add(T(PlayerName(p), 17, weight: FontWeight.SemiBold, wrap: true));
        body.Children.Add(KeyValue(
        [
            ("classe", cls ?? "—", null),
            ("puissance", p.Power is { } pw ? Fmt.Big(pw) : "—", null),
            ("DPS", Fmt.Big(p.Dps), Accent.Main),
            ("part", Fmt.Pct(p.Share), null),
            ("dégâts", Fmt.Big(p.Damage), null),
            ("critique", p.Crit is { } c ? Fmt.Pct(c) : "—", null),
            ("lancers", p.Casts is { Count: > 0 } casts ? casts.Count.ToString() : "non lus", report is null ? Tok.Muted : null),
            ("à corriger", report is null ? "—" : Fixes(report).Count.ToString(), report is not null && Fixes(report).Count > 0 ? Tok.Warn : null),
        ]));
        if (p.Name is { } known)
        {
            Border? button = null;
            button = Clickable(T("sa fiche (stats, équipement) ›", 12, Tok.Muted), () => openPlayer(known, p.ClassCode, button!), hover: Tok.Hover, padding: new Thickness(0, 2));
            body.Children.Add(Tip(button, $"Importer {known} depuis le site officiel et l'ouvrir dans un onglet."));
        }
        return body;
    }
}
