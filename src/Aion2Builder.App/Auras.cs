using System.Text.Json;
using System.Text.Json.Serialization;
using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Aion2Builder.Model;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>Un sort suivi par les auras : son code sans variante (celui des lancers lus par le Pet Meter), son nom, sa recharge de la table.</summary>
sealed record AuraSkill(long Code, string Name, double Cooldown);

/// <summary>
/// Auras de recharge d'un perso : affichées ou coupées, taille des icônes (px), sorts en recharge cachés au lieu d'être
/// grisés, et les sorts suivis dans l'ordre de l'overlay.
/// </summary>
sealed record AuraSet(bool On = false, int Size = 40, bool HideCooling = false, List<AuraSkill>? Skills = null)
{
    [JsonIgnore] public List<AuraSkill> List => Skills ?? [];
}

/// <summary>
/// %APPDATA%\Aion2Builder\auras.json : les auras de chaque perso (par nom), choisies dans Compétences → Auras. Le Pet Meter
/// (v0.26.0 ou plus) le relit et dessine les icônes au-dessus du jeu ; rien n'est envoyé.
/// </summary>
static class AuraStore
{
    sealed record File(int Version, Dictionary<string, AuraSet> Chars);

    static string PathOf => Path.Combine(AppPaths.Root, "auras.json");

    static Dictionary<string, AuraSet> All()
    {
        try
        {
            return System.IO.File.Exists(PathOf)
                ? JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(PathOf), JsonSerializerOptions.Web)?.Chars ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return []; }
    }

    public static AuraSet For(string character) => All().GetValueOrDefault(character) ?? new();

    public static void Save(string character, AuraSet set)
    {
        var all = All();
        all[character] = set;
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var tmp = PathOf + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(new File(1, all), JsonSerializerOptions.Web));
            System.IO.File.Move(tmp, PathOf, overwrite: true);
        }
        catch (IOException) { /* réglage non vital : la page le redira au prochain affichage */ }
    }
}

sealed partial class ScreenViews
{
    const int MaxAuras = 12;

    /// <summary>Tes sorts à recharge (actifs appris, stigmas équipés ; recharge d'au moins 5 s), la plus longue d'abord.</summary>
    List<(SkillInfo Skill, AuraSkill Aura)> AuraCandidates() =>
        r.Character.Skills
            .Where(k => k.IsActive ? k.Acquired : k.Category.Equals("Dp", StringComparison.OrdinalIgnoreCase) && k.Equipped && k.Level > 0)
            .Select(k => (Skill: k, Aura: new AuraSkill(k.Id / 10000 * 10000, k.Name, db.Spell(k.Id)?.Cooldown ?? 0)))
            .Where(x => x.Aura.Cooldown >= 5)
            .GroupBy(x => x.Aura.Code).Select(g => g.First())
            .OrderByDescending(x => x.Aura.Cooldown).ThenBy(x => x.Aura.Name, StringComparer.Create(new System.Globalization.CultureInfo("fr-FR"), true))
            .ToList();

    /// <summary>
    /// Compétences → Auras : les sorts que le Pet Meter montre au-dessus du jeu, grisés avec le compte à rebours pendant leur
    /// recharge, allumés quand ils sont prêts (comme les WeakAuras de WoW). Proposés d'abord : ceux que la macro laisse à la main.
    /// </summary>
    Control AurasPage()
    {
        var who = r.Character.Name;
        var set = AuraStore.For(who);
        var chosen = set.List;
        var candidates = AuraCandidates();
        var macro = MacroAdvisor.Plan(db, r);
        var origin = new Dictionary<string, string>();
        foreach (var x in macro.Manual) origin.TryAdd(GameDatabase.NormalizeLabel(x.Skill), "à la main");
        for (var i = 0; i < macro.Lines.Count; i++)
            foreach (var sl in macro.Lines[i].Slots) origin.TryAdd(GameDatabase.NormalizeLabel(sl.Skill), $"macro, ligne {i + 1}");
        string? OriginOf(AuraSkill a) =>
            origin.GetValueOrDefault(GameDatabase.NormalizeLabel(a.Name)) ??
            (db.Spell(a.Code) is { } sp ? origin.GetValueOrDefault(GameDatabase.NormalizeLabel(sp.Name)) : null);

        void Save(AuraSet next) { AuraStore.Save(who, next); update(_ => { }); }
        void Toggle(AuraSkill a)
        {
            var list = set.List.ToList();
            if (list.RemoveAll(x => x.Code == a.Code) == 0)
            {
                if (list.Count >= MaxAuras) return;
                list.Add(a);
            }
            // Le premier sort choisi allume les auras : c'est le choix de les avoir.
            Save(set with { Skills = list, On = set.On || (set.List.Count == 0 && list.Count > 0) });
        }
        void Up(AuraSkill a)
        {
            var list = set.List.ToList();
            var i = list.FindIndex(x => x.Code == a.Code);
            if (i <= 0) return;
            (list[i - 1], list[i]) = (list[i], list[i - 1]);
            Save(set with { Skills = list });
        }
        void Propose()
        {
            // Les sorts que la macro laisse à la main (buffs, burst, défenses), puis les plus longues recharges : 8 au plus.
            var manual = candidates.Where(x => OriginOf(x.Aura) == "à la main").Select(x => x.Aura);
            var rest = candidates.Where(x => x.Aura.Cooldown >= 30).Select(x => x.Aura);
            var list = manual.Concat(rest).DistinctBy(a => a.Code).Take(8).ToList();
            if (list.Count > 0) Save(set with { Skills = list, On = true });
        }

        var blocks = new List<Control?>
        {
            T("Le Pet Meter affiche au-dessus du jeu les sorts choisis ici : grisés avec le compte à rebours pendant leur recharge, allumés quand ils " +
              "sont prêts. Il voit chacun de tes lancers ; la recharge est celle du jeu, corrigée par tes combats (le plus petit écart vu entre deux " +
              "lancers du même sort, réductions de recharge comprises). Seulement tes sorts, rien n'est envoyé au jeu.", 12, Tok.Muted, wrap: true).WithLineHeight(1.4),
        };

        var settings = new StackPanel { Spacing = 8 };
        void Row(string label, Control value) => settings.Children.Add(ColumnsSpaced("170,*", 12, T(label, 13, Tok.Text2), value));
        Row("Auras", Choice([("affichées", set.On, () => Save(set with { On = true })), ("coupées", !set.On, () => Save(set with { On = false }))]));
        Row("Taille des icônes", Choice(new[] { (32, "petites"), (40, "moyennes"), (48, "grandes") }
            .Select(x => (x.Item2, set.Size == x.Item1, (Action)(() => Save(set with { Size = x.Item1 }))))));
        Row("Pendant la recharge", Choice([("grisées, compte à rebours", !set.HideCooling, () => Save(set with { HideCooling = false })),
                                           ("cachées", set.HideCooling, () => Save(set with { HideCooling = true }))]));
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Tip(Clickable(T("proposer mes sorts", 12, Accent.Main, FontWeight.SemiBold, mono: true), Propose, null, Tok.Hover, new Thickness(6, 2)),
            "Coche les sorts que ta macro laisse à la main (buffs, burst, défenses), puis tes plus longues recharges : 8 au plus. Remplace la sélection."));
        if (chosen.Count > 0)
            actions.Children.Add(Clickable(T("tout décocher", 12, Tok.Muted, mono: true), () => Save(set with { Skills = [] }), null, Tok.Hover, new Thickness(6, 2)));
        Row("Sélection", actions);
        blocks.Add(Card(settings, "Réglages"));

        if (candidates.Count == 0)
            blocks.Add(Card(T("Aucun sort à recharge connu pour ce perso (actifs appris, stigmas équipés) : actualise-le depuis le site officiel.", 13, Tok.Muted, wrap: true)));
        else
        {
            const string cols = "36,*,90,140,40";
            var rows = new List<Control>();
            // Les sorts choisis d'abord, dans l'ordre de l'overlay ; puis les autres.
            var order = chosen.Select(a => candidates.FirstOrDefault(x => x.Aura.Code == a.Code))
                .Where(x => x.Skill is not null)
                .Concat(candidates.Where(x => chosen.All(a => a.Code != x.Aura.Code))).ToList();
            foreach (var (skill, aura) in order)
            {
                var on = chosen.Any(a => a.Code == aura.Code);
                var rank = chosen.FindIndex(a => a.Code == aura.Code);
                var box = Tip(Clickable(Tick(on), () => Toggle(aura), null, Tok.Hover, new Thickness(8, 4)),
                    on ? "Retirer ce sort des auras." : chosen.Count >= MaxAuras ? $"{MaxAuras} sorts au plus." : "Montrer ce sort dans les auras.");
                Control up = on && rank > 0
                    ? Tip(Clickable(T("↑", 13, Tok.Muted, mono: true), () => Up(aura), null, Tok.Hover, new Thickness(6, 0)), "Plus à gauche dans l'overlay.")
                    : new Panel();
                rows.Add(new Border
                {
                    Padding = new Thickness(14, 4),
                    Child = Columns(cols, box,
                        WithIcon(SkillIcon(skill, 20), T((on ? $"{rank + 1}. " : "") + aura.Name, 13, on ? Tok.Text : Tok.Text2)),
                        T($"{aura.Cooldown:0.#} s", 12, Tok.Text2, mono: true),
                        T(OriginOf(aura) ?? "", 11, Tok.Muted),
                        up),
                });
            }
            blocks.Add(TableCard("Tes sorts à recharge", T($"{chosen.Count} choisi{(chosen.Count > 1 ? "s" : "")} sur {MaxAuras} au plus", 11, Tok.Muted, mono: true),
                TableHeader(cols, "", "Compétence", "Recharge", "Dans ta macro", ""), rows));
        }

        var steps = new StackPanel { Spacing = 8 };
        void Step(string n, string text) => steps.Children.Add(ColumnsSpaced("24,*", 8, T(n, 13, Accent.Main, FontWeight.Bold, mono: true),
            T(text, 13, wrap: true).WithLineHeight(1.45)));
        Step("1", "Lance le Pet Meter (v0.26.0 ou plus) sur ce PC, avec ce perso en jeu : il relit ces réglages tout seul, même en combat.");
        Step("2", "Pet Meter → ⋯ → Affichage → « Placer les auras » : glisse la rangée où tu veux, puis clique la coche ✓ à droite pour la verrouiller. Verrouillée, " +
                  "elle laisse passer les clics vers le jeu.");
        Step("3", "En combat, chaque icône se grise au lancer avec son compte à rebours, puis s'allume quand le sort est prêt. Hors combat, les auras " +
                  "disparaissent dès que plus rien n'est en recharge. Le jeu doit être en fenêtre ou en plein écran fenêtré (comme le Pet Meter).");
        blocks.Add(Card(steps, "Dans le jeu"));
        blocks.Add(T("La recharge de la table est celle du sort sans spécialisation : un sort dont la recharge est réduite par une spécialisation ou " +
                     "par un autre sort s'allume un peu tard au début, puis le Pet Meter se cale sur tes vrais lancers.", 11, Tok.Faint, wrap: true).WithLineHeight(1.4));

        var badges = Badges(Badge(set.On && chosen.Count > 0 ? $"{chosen.Count} sort{(chosen.Count > 1 ? "s" : "")} suivi{(chosen.Count > 1 ? "s" : "")}" : "coupées",
            set.On && chosen.Count > 0 ? Accent.Main : Tok.Muted));
        return Page($"Compétences · auras de recharge · {who}", badges, blocks.ToArray());
    }
}
