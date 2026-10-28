using System.Text.RegularExpressions;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>
/// Compétences. Les points montent une compétence du niveau 1 au niveau 10 (1 point par niveau, réinitialisation
/// gratuite) ; au-delà, seuls le Daevanion et l'équipement donnent des niveaux. On répartit donc un budget :
/// d'abord ce qui compte dans la rotation de la classe (dégâts principaux, burst, débuffs), et on reprend des points
/// aux compétences nettement moins utiles dans ce mode — avec la raison à chaque fois.
/// Les stigmas sont à part : 4 emplacements, niveaux montés avec des fragments.
/// </summary>
public sealed partial class SkillAdvisor(GameDatabase db)
{
    [GeneratedRegex(@"^(?<name>.+?)\s*\+(?<n>\d+)$")]
    private static partial Regex BonusRegex();

    static string Norm(string s) => GameDatabase.NormalizeLabel(s);

    public static SkillKind KindOf(SkillInfo s) =>
        s.Category.Equals("Dp", StringComparison.OrdinalIgnoreCase) || s.Category.Equals("Stigma", StringComparison.OrdinalIgnoreCase)
            ? SkillKind.Stigma
            : s.IsPassive ? SkillKind.Passive : SkillKind.Active;

    bool CommonPvp(SkillInfo s) => db.Settings.Skills.CommonPvpSkills.Any(n => Norm(n) == Norm(s.Name));

    /// <summary>Compétences sur une touche fixe (ex. Dissipation de choc) : le site les dit toujours « non équipées ».</summary>
    bool FixedSlot(SkillInfo s) => db.Settings.Skills.FixedSlotSkills.Any(n => Norm(n) == Norm(s.Name));

    public bool InBar(SkillInfo s) => s.Equipped || FixedSlot(s);

    /// <summary>Importance de la compétence pour ce profil : 0 = inutile, 1 = indispensable.</summary>
    public double Priority(SkillInfo s, ResolvedProfile p)
    {
        if (p.Skill(s.Name, KindOf(s).ToString()) is { } k) return k.Priority.GetValueOrDefault(p.Mode);
        var set = db.Settings.Skills;
        if (p.Mode == GameMode.PvP && CommonPvp(s)) return 1.0;
        return KindOf(s) switch
        {
            SkillKind.Passive => set.UnknownPassivePriority,
            SkillKind.Stigma => s.Equipped ? set.UnknownStigmaPriority : 0.2,
            // Sans fiche de classe, la barre du joueur fait foi : une active non équipée ne sert pas.
            _ => InBar(s) ? set.UnknownActivePriority : 0.05,
        };
    }

    public HashSet<string> CoreSet(ResolvedProfile p) =>
        p.CoreSkills.Concat(p.Mode == GameMode.PvP ? db.Settings.Skills.CommonPvpSkills : []).Select(Norm).ToHashSet();

    /// <summary>Niveaux gratuits par compétence : cases Daevanion ouvertes et compétences bonus de l'équipement.</summary>
    public static Dictionary<string, int> BonusLevels(CharacterSnapshot c)
    {
        var bonus = new Dictionary<string, int>();
        void Add(string name, int n) => bonus[Norm(name)] = bonus.GetValueOrDefault(Norm(name)) + n;
        foreach (var e in c.Boards.SelectMany(b => b.Nodes).Where(n => n.Open && n.IsSkill).SelectMany(n => n.Effects).Where(e => e.IsSkill))
            Add(e.SkillName!, (int)e.Value);
        foreach (var sub in c.Equipment.SelectMany(i => i.SubSkills))
            if (BonusRegex().Match(sub) is { Success: true } m)
                Add(m.Groups["name"].Value, int.Parse(m.Groups["n"].Value));
        return bonus;
    }

    /// <summary>Niveaux gratuits des cases Daevanion ouvertes et des arcanes, séparément (le reste vient des autres pièces).</summary>
    static (Dictionary<string, int> Daevanion, Dictionary<string, int> Arcana) BonusSources(CharacterSnapshot c)
    {
        var daevanion = new Dictionary<string, int>();
        var arcana = new Dictionary<string, int>();
        foreach (var e in c.Boards.SelectMany(b => b.Nodes).Where(n => n.Open && n.IsSkill).SelectMany(n => n.Effects).Where(e => e.IsSkill))
            daevanion[Norm(e.SkillName!)] = daevanion.GetValueOrDefault(Norm(e.SkillName!)) + (int)e.Value;
        foreach (var sub in c.Equipment.Where(IsArcana).SelectMany(i => i.SubSkills))
            if (BonusRegex().Match(sub) is { Success: true } m)
                arcana[Norm(m.Groups["name"].Value)] = arcana.GetValueOrDefault(Norm(m.Groups["name"].Value)) + int.Parse(m.Groups["n"].Value);
        return (daevanion, arcana);
    }

    static bool IsArcana(EquippedItem i) => i.Slot.StartsWith("Arcana", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Part des dégâts de chaque compétence dans la rotation en vigueur, simulée sur 2 minutes contre une cible.
    /// Chaque niveau monte les dégâts d'un sort : un niveau rapporte d'autant plus que le sort pèse lourd dans la rotation.
    /// </summary>
    public Dictionary<string, double> DamageShares(ResolvedProfile p)
    {
        var sim = RotationSimulator.Run(db, p.Class.Code, RotationSimulator.Priority(p.Play), 120, 1, boss: p.Mode != GameMode.PvP);
        return sim.Damage <= 0 ? [] : sim.Casts.GroupBy(x => Norm(x.Name)).ToDictionary(g => g.Key, g => g.Sum(x => x.Damage) / sim.Damage);
    }

    sealed class Row(SkillInfo skill, SkillKnowledge? known, int bonus, int baseLevel, double priority, int target)
    {
        public SkillInfo Skill { get; } = skill;
        public SkillKnowledge? Known { get; } = known;
        public int Bonus { get; } = bonus;
        public int Base { get; } = baseLevel;
        public double Priority { get; } = priority;
        public int Target { get; } = target;
        public int Rec { get; set; } = baseLevel;
        public double Share { get; init; }
        public int? Rank { get; set; }
    }

    /// <param name="combats">Combats gardés, les plus récents d'abord : en JcE ceux de boss et de mannequin, en JcJ ceux contre des
    /// joueurs ; leurs parts de dégâts (additionnées, suites d'enchaînement comptées avec leur sort) remplacent la simulation.</param>
    public SkillPlan Plan(CharacterSnapshot c, ResolvedProfile p, PlayerInputs inputs, IReadOnlyList<Combat.CombatLog>? combats = null)
    {
        var set = db.Settings.Skills;
        var bonus = BonusLevels(c);
        var (daevanion, arcana) = BonusSources(c);
        var openable = c.Boards.SelectMany(b => b.Nodes).Where(n => !n.Open && n.IsSkill)
            .SelectMany(n => n.Effects.Where(e => e.IsSkill)).GroupBy(e => Norm(e.SkillName!))
            .ToDictionary(g => g.Key, g => g.Count());
        var fights = Measured(combats, p.Mode);
        var chains = fights.Count > 0 ? Combat.CombatAnalyzer.Chains(fights, c.Skills.Select(s => Norm(s.Name)).ToHashSet()) : [];
        var measured = fights.Count > 0 ? Combat.CombatAnalyzer.Shares(fights, chains) : null;
        if (measured is not null)
        {
            // Part de chaque suite à elle seule, pour montrer ce que contient la part de son sort.
            var alone = Combat.CombatAnalyzer.Shares(fights, []);
            chains = chains.Select(l => l with { Share = alone.GetValueOrDefault(Norm(l.Skill)) }).ToList();
        }
        var shares = measured ?? DamageShares(p);
        var mode = p.Mode == GameMode.PvP ? "JcJ" : "JcE";

        var rows = c.Skills.Where(s => KindOf(s) != SkillKind.Stigma).Select(s =>
        {
            var known = p.Skill(s.Name, KindOf(s).ToString());
            var b = bonus.GetValueOrDefault(Norm(s.Name));
            var priority = Priority(s, p);
            var target = known?.Target.GetValueOrDefault(p.Mode) is > 0 and var t ? t : priority >= 0.3 ? set.BaseMax : 1;
            return new Row(s, known, b, s.Acquired ? Math.Clamp(s.Level - b, 1, set.BaseMax) : 0, priority, Math.Clamp(target, 1, set.BaseMax))
            {
                Share = KindOf(s) == SkillKind.Active ? shares.GetValueOrDefault(Norm(s.Name)) : 0,
            };
        }).ToList();
        var rank = 0;
        foreach (var x in rows.Where(x => x.Share > 0).OrderByDescending(x => x.Share)) x.Rank = ++rank;

        var spent = rows.Where(r => r.Skill.Acquired).Sum(r => (r.Base - 1) * set.CostPerLevel);
        var free = inputs.UnspentSkillPoints / Math.Max(1, set.CostPerLevel);
        var acquired = rows.Where(r => r.Skill.Acquired).ToList();

        // Qui donne à qui (donneur null : points non dépensés), pour expliquer chaque déplacement.
        var moved = new Dictionary<(Row? From, Row To), int>();
        void Move(Row? from, Row to) => moved[(from, to)] = moved.GetValueOrDefault((from, to)) + 1;

        // 1. Les compétences sous leur niveau visé, de la plus importante à la moins importante, prennent les points
        //    libres, puis ceux des compétences nettement moins utiles (au-dessus de leur cible d'abord).
        foreach (var need in acquired.Where(r => r.Rec < r.Target).OrderByDescending(r => r.Priority).ThenByDescending(r => r.Share))
            while (need.Rec < need.Target)
            {
                if (free > 0) { free--; need.Rec++; Move(null, need); continue; }
                // Donneur : au-dessus de son propre niveau visé d'abord, puis le moins utile ; à utilité égale, celui qui a le
                // plus de marge sur son niveau visé (on répartit au lieu de vider une seule compétence sous son visé).
                var donor = acquired.Where(r => r != need && r.Rec > 1 && r.Priority + set.ChangeThreshold <= need.Priority)
                    .OrderBy(r => r.Rec > r.Target ? 0 : 1).ThenBy(r => r.Priority).ThenBy(r => r.Share).ThenByDescending(r => r.Rec - r.Target).FirstOrDefault();
                if (donor is null) break;
                donor.Rec--;
                need.Rec++;
                Move(donor, need);
            }

        // 2. Points encore libres : vers les compétences pas encore au maximum, les plus utiles d'abord. Tout point
        //    non dépensé trouve une place (la réinitialisation est gratuite : mieux vaut un point posé qu'un point oublié).
        foreach (var r in acquired.OrderByDescending(r => r.Priority).ThenByDescending(r => r.Share))
            while (free > 0 && r.Rec < set.BaseMax) { free--; r.Rec++; Move(null, r); }

        // 3. Points posés au-dessus du niveau visé d'une compétence nettement moins utile : vers les compétences utiles
        //    pas encore au maximum. Sans ça, ils dorment là et la répartition conseillée change d'un passage à l'autre.
        foreach (var r in acquired.Where(r => r.Priority >= 0.3).OrderByDescending(r => r.Priority).ThenByDescending(r => r.Share))
            while (r.Rec < set.BaseMax)
            {
                var donor = acquired.Where(d => d != r && d.Rec > d.Target && d.Priority + set.ChangeThreshold <= r.Priority)
                    .OrderBy(d => d.Priority).ThenBy(d => d.Share).ThenByDescending(d => d.Rec - d.Target).FirstOrDefault();
                if (donor is null) break;
                donor.Rec--;
                r.Rec++;
                Move(donor, r);
            }

        var decisions = rows.ToDictionary(r => r, r => Decide(r, moved, mode, measured is not null ? "mesurés" : "simulés"));
        var advice = rows.ToDictionary(r => r, r => new SkillAdvice(r.Skill, KindOf(r.Skill), r.Known?.Role ?? DefaultRole(r.Skill),
            r.Known?.Why ?? DefaultWhy(r.Skill, p), r.Known is not null, r.Known?.Unverified ?? false, r.Priority,
            r.Base, r.Bonus, r.Target, r.Rec, openable.GetValueOrDefault(Norm(r.Skill.Name)), InBar(r.Skill))
        {
            BonusDaevanion = daevanion.GetValueOrDefault(Norm(r.Skill.Name)),
            BonusArcana = arcana.GetValueOrDefault(Norm(r.Skill.Name)),
            DamageShare = r.Share,
            DamageRank = r.Rank,
            Decision = decisions[r].Full,
            Reason = decisions[r].Short,
        });
        var skills = advice.Values.OrderBy(s => s.Kind).ThenByDescending(s => s.Priority).ToList();
        var moves = moved.Select(m => new SkillMove(m.Key.From is { } f ? advice[f] : null, advice[m.Key.To], m.Value))
            .OrderByDescending(m => m.To.Priority).ThenBy(m => m.From?.Priority ?? -1).ToList();

        // Prochains points (quand tu en gagnes), à partir de la répartition conseillée : niveau visé d'abord,
        // puis le maximum achetable des compétences utiles, jusqu'au bout (le site ne montre pas les points non
        // dépensés : la liste doit couvrir ceux que le joueur a sans les avoir indiqués). Les points consécutifs
        // sur une même compétence sont groupés.
        var simulated = acquired.ToDictionary(r => r, r => r.Rec);
        var nextPoints = new List<SkillStep>();
        for (var i = 0; i < 200; i++)
        {
            var next = acquired.Where(r => simulated[r] < r.Target).OrderByDescending(r => r.Priority).ThenByDescending(r => r.Share).FirstOrDefault()
                       ?? acquired.Where(r => r.Priority >= 0.3 && simulated[r] < set.BaseMax).OrderByDescending(r => r.Priority).ThenByDescending(r => r.Share).FirstOrDefault();
            if (next is null) break;
            var from = simulated[next]++;
            if (nextPoints.Count > 0 && nextPoints[^1].Skill == advice[next]) nextPoints[^1] = nextPoints[^1] with { To = from + 1 };
            else nextPoints.Add(new SkillStep(advice[next], from, from + 1, null));
        }
        nextPoints = nextPoints.Select(x => x with
        {
            Specialty = set.SpecialtyLevels.Where(l => l > x.From + x.Skill.Bonus && l <= x.To + x.Skill.Bonus).Cast<int?>().FirstOrDefault(),
        }).ToList();

        // Stigmas : les plus utiles occupent les emplacements, montés jusqu'au niveau visé.
        var st = db.Settings.Stigmas;
        var stigmaRows = c.Skills.Where(s => KindOf(s) == SkillKind.Stigma)
            .Select(s => (Skill: s, Known: p.Skill(s.Name, KindOf(s).ToString()), Priority: Priority(s, p)))
            .OrderByDescending(x => x.Priority).ThenByDescending(x => x.Skill.Level).ToList();
        var chosen = stigmaRows.Take(st.Slots).Select(x => x.Skill.Id).ToHashSet();
        var stigmas = stigmaRows.Select(x =>
        {
            var target = x.Known?.Target.GetValueOrDefault(p.Mode) is > 0 and var t ? t : x.Skill.Equipped ? 10 : 1;
            // Déjà au-dessus du niveau visé : on garde le niveau actuel (les fragments ne se récupèrent pas).
            target = Math.Max(target, x.Skill.Level);
            var recommended = chosen.Contains(x.Skill.Id);
            return new StigmaAdvice(x.Skill, x.Known?.Role ?? "Stigma", x.Known?.Why ?? DefaultWhy(x.Skill, p), x.Known is not null,
                x.Known?.Unverified ?? false, x.Priority, recommended, target,
                recommended ? st.ShardCost(x.Skill.Level, Math.Min(target, st.MaxLevel)) : 0);
        }).ToList();

        // Prochains fragments : par paliers de 5 niveaux (où les effets des stigmas se débloquent), tous les stigmas
        // conseillés au palier suivant avant d'aller plus haut, le plus important d'abord.
        var shardSteps = new List<StigmaStep>();
        foreach (var g in stigmas.Where(x => x.Recommended))
        {
            var cap = Math.Min(g.Target, st.MaxLevel);
            for (var level = g.Skill.Level; level < cap;)
            {
                var to = Math.Min(cap, (level / 5 + 1) * 5);
                shardSteps.Add(new StigmaStep(g, level, to, st.ShardCost(level, to)));
                level = to;
            }
        }
        var nextShards = shardSteps.OrderBy(x => x.To).ThenByDescending(x => x.Stigma.Priority).Take(8).ToList();

        // Compétences bonus de l'équipement et des arcanes : un niveau gratuit vaut ce que vaut la compétence pour toi.
        var gear = new List<GearSkillBonus>();
        foreach (var item in c.Equipment)
            foreach (var sub in item.SubSkills)
            {
                if (BonusRegex().Match(sub) is not { Success: true } m) continue;
                var name = m.Groups["name"].Value;
                var a = advice.Values.FirstOrDefault(x => Norm(x.Skill.Name) == Norm(name));
                var (useful, strong, verdict) = a switch
                {
                    null => (false, false, "Stigma ou compétence absente de ta liste : ne monte rien d'utile."),
                    { Skill.Acquired: false } => (false, false, "Pas encore apprise : le bonus attend."),
                    { Kind: SkillKind.Active, InBar: false } => (false, false, $"Hors de ta barre {mode} : bonus perdu, à remplacer en priorité."),
                    { Priority: < 0.3 } => (false, false, $"{Importance(a.Priority)} en {mode} : bonus presque perdu, à remplacer en priorité."),
                    { DamageShare: > 0 } => (true, true, $"{Fmt.Pct(a.DamageShare)} des dégâts (n° {a.DamageRank}) : bon bonus."),
                    { Priority: >= 0.7 } => (true, true, $"{Importance(a.Priority)} en {mode} ({a.Role.ToLowerInvariant()}) : bon bonus."),
                    _ => (true, false, $"{Importance(a.Priority)} en {mode} ({a.Role.ToLowerInvariant()}) : correct sans plus, à remplacer par un sort du focus dégâts."),
                };
                gear.Add(new GearSkillBonus(item.Slot, item.Name, IsArcana(item), name, int.Parse(m.Groups["n"].Value), a, useful, strong, verdict));
            }
        var focus = advice.Values.Where(a => a.Skill.Acquired && a.DamageShare > 0).OrderByDescending(a => a.DamageShare).ToList();
        // À chercher sur les prochaines arcanes et pièces : les compétences qui pèsent le plus dans les dégâts (chaque niveau
        // y rapporte le plus), à défaut les indispensables.
        var wanted = focus.Count > 0 ? focus.Take(5).ToList()
            : advice.Values.Where(a => a.Skill.Acquired && a.Priority >= 0.85).OrderByDescending(a => a.Priority).Take(5).ToList();

        return new SkillPlan(skills, stigmas, spent, inputs.UnspentSkillPoints, p.Class.Skills.Count > 0, p.UsesDesigned ? $"Build conçu : {p.Class.Designed!.Method}" : p.Class.SkillsSource,
            moves, nextPoints, nextShards)
        {
            Focus = focus,
            DamageSource = measured is not null ? Source(fights)
                : shares.Count == 0 ? null
                : $"rotation {(p.UsesDesigned && p.Class.Designed?.Play.ContainsKey(p.Mode) == true ? "conçue" : "des joueurs")} simulée, {mode}, 1 cible, 2 min",
            GearBonuses = gear,
            BonusWanted = wanted,
            Chains = chains,
            MeasuredFights = fights.Count,
        };
    }

    /// <summary>Combats dont on prend les parts de dégâts : les 10 plus récents du mode (boss et mannequin en JcE, joueurs en JcJ).</summary>
    static List<Combat.CombatLog> Measured(IReadOnlyList<Combat.CombatLog>? combats, GameMode mode) =>
        (combats ?? []).Where(l => l.Skills.Count > 0 && (mode == GameMode.PvP ? l.Kind == "pvp" : l.Kind is null or "boss" or "dummy"))
            .Take(10).ToList();

    /// <summary>« mesuré : 6 combats (boss, mannequin) du 24/10 au 27/10, 9 min de combat ».</summary>
    static string Source(List<Combat.CombatLog> fights)
    {
        var kinds = fights.Select(l => Combat.PetMeterFights.KindName(l) ?? l.Source).Distinct();
        var first = fights.Min(l => l.ImportedAtUtc).ToLocalTime();
        var last = fights.Max(l => l.ImportedAtUtc).ToLocalTime();
        var seconds = fights.Sum(l => l.Duration);
        return $"mesuré : {(fights.Count == 1 ? "1 combat" : $"{fights.Count} combats")} ({string.Join(", ", kinds)}) " +
               (first.Date == last.Date ? $"du {first:dd/MM}" : $"du {first:dd/MM} au {last:dd/MM}") +
               $", {(seconds >= 120 ? $"{seconds / 60:0} min" : $"{seconds:0} s")} de combat";
    }

    /// <summary>
    /// Pourquoi la répartition conseillée monte, baisse ou garde une compétence : son importance dans ce mode, sa part
    /// des dégâts simulés, et qui donne ou reçoit ses points.
    /// </summary>
    (string Full, string Short) Decide(Row r, Dictionary<(Row? From, Row To), int> moved, string mode, string damage)
    {
        var set = db.Settings.Skills;
        string What(Row x) => $"{Importance(x.Priority)} en {mode}" + (x.Share > 0 ? $", {Fmt.Pct(x.Share)} des dégâts {damage} (n° {x.Rank})"
            : KindOf(x.Skill) == SkillKind.Active ? ", pas de dégâts directs" : "");
        // Même mot d'importance des deux côtés : le nombre de la fiche départage.
        string Donor(Row x) => $"{x.Skill.Name} ({Importance(x.Priority)}" + (Importance(x.Priority) == Importance(r.Priority) ? $" {x.Priority:0.00} contre {r.Priority:0.00}" : "") +
                               (x.Share > 0 ? $", {Fmt.Pct(x.Share)} des dégâts" : "") + ")";
        static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
        var what = What(r);
        if (!r.Skill.Acquired) return ($"Pas encore apprise (niveau {r.Skill.NeedLevel}) : {what}.", Cap($"{what}."));
        if (r.Rec > r.Base)
        {
            var donors = moved.Where(m => m.Key.To == r && m.Key.From is not null).Select(m => Donor(m.Key.From!)).ToList();
            var spec = set.SpecialtyLevels.Where(l => l > r.Base + r.Bonus && l <= r.Rec + r.Bonus).Cast<int?>().LastOrDefault();
            var unlock = spec is { } s ? $" Débloque la spécialisation du niveau {s}." : "";
            return ($"Monte à {r.Rec} : {what}." +
                    (donors.Count > 0 ? $" Points pris à {string.Join(", ", donors)} : ils rapportent moins là-bas." : " Avec tes points non dépensés.") + unlock,
                Cap($"{what}.{unlock}"));
        }
        if (r.Rec < r.Base)
        {
            var receivers = moved.Where(m => m.Key.From == r).Select(m => m.Key.To.Skill.Name).Distinct().ToList();
            var bar = KindOf(r.Skill) == SkillKind.Active && !InBar(r.Skill) ? ", hors de ta barre" : "";
            // Les prochains points remontent jusqu'au maximum toute compétence au moins secondaire.
            var back = r.Priority >= 0.3 ? " Elle remontera avec tes prochains points, après les plus importantes." : "";
            var full = $"Descend à {r.Rec} : {what}{bar}. Ces points servent davantage à {string.Join(", ", receivers)}.{back}";
            return (full, full);
        }
        var other = OtherCases();
        return (other, other);

        string OtherCases()
        {
            if (r.Rec >= set.BaseMax) return $"Au maximum achetable ({set.BaseMax}) : {what}. La suite passe par le Daevanion, les arcanes et l'équipement.";
            if (r.Rec > r.Target) return $"Au-dessus du niveau visé ({r.Target}) : {what}. Ces points iront ailleurs si une compétence plus utile en manque.";
            if (r.Rec == r.Target)
                return r.Target <= 1 ? $"Laissée au niveau 1 : {what} ; un point rapporte plus ailleurs." : $"Au niveau visé ({r.Target}) : {what}.";
            return $"Sous le niveau visé ({r.Target}) : {what}. Elle prend tes prochains points (voir l'ordre).";
        }
    }

    /// <summary>Importance en clair, à la place du nombre de la fiche (0 = inutile, 1 = indispensable).</summary>
    public static string Importance(double priority) =>
        priority >= 0.85 ? "indispensable" : priority >= 0.7 ? "importante" : priority >= 0.5 ? "utile" : priority >= 0.3 ? "secondaire" : "peu utile";

    string DefaultRole(SkillInfo s) => KindOf(s) switch
    {
        SkillKind.Passive => "Passif",
        SkillKind.Stigma => "Stigma",
        _ => InBar(s) ? "Dans ta barre" : "Hors de ta barre",
    };

    string DefaultWhy(SkillInfo s, ResolvedProfile p)
    {
        if (p.Mode == GameMode.PvP && CommonPvp(s))
            return "Compétence commune qui libère des effets de contrôle : indispensable en JcJ.";
        return KindOf(s) switch
        {
            SkillKind.Passive => "Passif actif en permanence une fois appris (pas de fiche détaillée pour cette classe).",
            SkillKind.Stigma => s.Equipped ? "Stigma équipé (pas de fiche détaillée pour cette classe)." : "Stigma non équipé.",
            _ => InBar(s)
                ? "Dans ta barre : considérée comme faisant partie de ta rotation (pas de fiche détaillée pour cette classe)."
                : "Pas dans ta barre : les points investis ne servent à rien tant qu'elle n'est pas équipée.",
        };
    }

    static string Levels(SkillAdvice a) =>
        $"niveau acheté {a.Base} → {a.RecommendedBase} ({(a.Delta > 0 ? "+" : "")}{a.Delta} point{(Math.Abs(a.Delta) > 1 ? "s" : "")})";

    public List<Finding> Findings(SkillPlan plan, CharacterSnapshot c, ResolvedProfile p)
    {
        var findings = new List<Finding>();
        var set = db.Settings.Skills;
        var mode = GameDatabase.ModeName(p.Mode);

        findings.Add(new Finding(Area.Skills, Severity.Info, $"Points de compétence : {plan.SpentPoints} investis + {plan.UnspentPoints} non dépensés",
            $"Chaque point monte une compétence d'un niveau, jusqu'au niveau {set.BaseMax} ; au-delà, seuls le Daevanion et l'équipement en donnent. " +
            $"La réinitialisation est gratuite : la répartition conseillée réutilise les {plan.Budget} points dont tu disposes. {set.PointSources}" +
            (plan.UnspentPoints == 0 ? " Si tu as des points non dépensés, indique-les (onglet Compétences ou --sp) pour qu'ils soient répartis." : "")));

        var remove = plan.ToRemove.ToList();
        var add = plan.ToAdd.ToList();
        string Line(SkillAdvice a) => $"« {a.Skill.Name} » ({a.Role}) : {Levels(a)}. {(a.Delta < 0 ? a.Decision : a.Reason)}";
        if (remove.Count > 0)
            findings.Add(new Finding(Area.Skills, Severity.Important, $"Compétences : retire des points à {string.Join(", ", remove.Select(a => a.Skill.Name))}",
                string.Join("\n", remove.Select(Line)),
                $"Réinitialise tes compétences (gratuit) et refais la répartition {mode} de l'onglet Compétences : ces points servent davantage ailleurs.",
                60 + 5 * remove.Sum(a => -a.Delta) * remove.Max(a => 1 - a.Priority) / 3));
        if (add.Count > 0)
            findings.Add(new Finding(Area.Skills, remove.Count > 0 || add.Any(a => a.Priority >= 0.85) ? Severity.Important : Severity.Advice,
                $"Compétences : ajoute des points à {string.Join(", ", add.Select(a => a.Skill.Name))}",
                string.Join("\n", add.Select(Line)),
                plan.UnspentPoints > 0 && remove.Count == 0
                    ? "Place tes points non dépensés dans cet ordre."
                    : "Mets les points récupérés dans cet ordre (la plus importante d'abord).",
                55 + 5 * add.Sum(a => a.Delta * a.Priority) / 3));

        foreach (var a in plan.Skills.Where(a => a.Kind == SkillKind.Active && a.Priority >= 0.85 && a.Skill.Acquired && !a.InBar))
            findings.Add(new Finding(Area.Skills, Severity.Important, $"Équipe « {a.Skill.Name} »",
                $"{a.Role} : {a.Why}", $"Mets « {a.Skill.Name} » dans ta barre {mode}.", 75));
        foreach (var a in plan.Skills.Where(a => a.Priority >= 0.85 && !a.Skill.Acquired))
            findings.Add(new Finding(Area.Skills, Severity.Important, $"Apprends « {a.Skill.Name} »",
                $"{a.Role} : {a.Why}", $"Apprends-la dès le niveau {a.Skill.NeedLevel}.", 70));

        // Au-delà du niveau 10 : spécialisations aux niveaux 12, 16, 20, atteignables seulement par le Daevanion et l'équipement.
        var specialties = set.SpecialtyLevels.Where(l => l > set.BaseMax).ToList();
        var nextSpecialty = plan.Skills.Where(a => a.Priority >= 0.8 && a.Skill.Acquired && a.OpenableNodes > 0)
            .Select(a => (Advice: a, Next: specialties.FirstOrDefault(l => l > a.TotalAfter)))
            .Where(x => x.Next > 0).OrderByDescending(x => x.Advice.Priority).Take(5).ToList();
        if (nextSpecialty.Count > 0)
            findings.Add(new Finding(Area.Skills, Severity.Advice, "Spécialisations au-delà du niveau 10",
                $"Spécialisations aux niveaux {string.Join(", ", set.SpecialtyLevels)} (niveau total). Au-delà de {set.BaseMax}, seules les cases Daevanion et l'équipement montent une compétence : " +
                string.Join(" · ", nextSpecialty.Select(x => $"{x.Advice.Skill.Name} niv. {x.Advice.TotalAfter} → {x.Next} ({x.Advice.OpenableNodes} case(s) Daevanion encore fermée(s))")),
                "Privilégie ces cases de compétence dans ton Daevanion (le plan Daevanion en tient compte).", 35));

        // Stigmas.
        var st = db.Settings.Stigmas;
        var wanted = plan.Stigmas.Where(s => s.Recommended).ToList();
        var toUnequip = plan.Stigmas.Where(s => s.Skill.Equipped && !s.Recommended).ToList();
        var toEquip = wanted.Where(s => !s.Skill.Equipped).ToList();
        if (toEquip.Count > 0 || toUnequip.Count > 0)
            findings.Add(new Finding(Area.Skills, Severity.Important, $"Stigmas {mode} : {string.Join(", ", wanted.Select(s => s.Skill.Name))}",
                string.Join("\n", toUnequip.Select(s => $"Retire « {s.Skill.Name} » : {s.Why}")
                    .Concat(toEquip.Select(s => $"Mets « {s.Skill.Name} » ({s.Role}){(s.Skill.Acquired ? "" : " — à obtenir d'abord")} : {s.Why}"))),
                $"Échange tes stigmas (gratuit) : {st.Slots} emplacements.", 65));
        var shards = wanted.Sum(s => s.ShardsToTarget);
        if (shards > 0)
            findings.Add(new Finding(Area.Skills, Severity.Advice, $"Stigmas : {shards} fragments pour atteindre les niveaux visés",
                string.Join(" · ", wanted.Where(s => s.ShardsToTarget > 0).Select(s => $"{s.Skill.Name} niv. {s.Skill.Level} → {s.Target} ({s.ShardsToTarget} fragments)")) +
                $". Coût par niveau : 1 jusqu'au niveau 5, puis 2, 4 et 8 fragments. Sources : {st.ShardSources}",
                $"Monte d'abord « {wanted.Where(s => s.ShardsToTarget > 0).OrderByDescending(s => s.Priority).First().Skill.Name} ».", 40));

        if (!plan.HasKnowledge)
            findings.Add(new Finding(Area.Skills, Severity.Info, $"Pas de fiche de compétences pour {p.Class.Name}",
                "Sans fiche, l'analyse considère ta barre comme ta rotation : les actives hors barre sont jugées inutiles. " +
                "Ajoute le rôle de chaque compétence dans classes.json (« skills ») pour des conseils détaillés."));
        else if (plan.Skills.Concat<object>(plan.Stigmas).OfType<SkillAdvice>().Any(a => a.Unverified && a.Delta != 0))
            findings.Add(new Finding(Area.Skills, Severity.Info, "Certaines raisons sont « à valider »",
                $"Fiche de compétences : {plan.Source} Corrige-les dans classes.json si ton expérience de jeu dit autre chose."));
        return findings;
    }
}
