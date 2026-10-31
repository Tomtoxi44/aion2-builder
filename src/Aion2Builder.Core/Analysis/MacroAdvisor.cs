using Aion2Builder.Combat;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Analysis;

/// <summary>Une compétence d'une ligne de raccourci : sa priorité dans la pile (0 = la première essayée) et pourquoi là.</summary>
public sealed record MacroSlot(int Priority, string Skill, string Why, string? Detail);

/// <summary>Une ligne de raccourci (jusqu'à 4 compétences empilées sur une même touche) : une étape de la macro.</summary>
public sealed record MacroLine(string Name, List<MacroSlot> Slots);

/// <summary>Une compétence de la rotation gardée hors macro, et pourquoi.</summary>
public sealed record MacroManual(string Skill, string Why);

/// <summary>Macro en jeu conseillée : ses lignes (dans l'ordre des étapes), ce qui reste à la main, ce qui manque au perso.</summary>
public sealed record MacroPlan(List<MacroLine> Lines, List<MacroManual> Manual, List<string> Missing, string Source);

/// <summary>
/// Macro en jeu (fenêtre des compétences, K → Macro) : elle appuie pour toi sur des lignes de raccourcis tant que sa touche
/// est tenue. Une ligne empile jusqu'à 4 compétences sur une touche ; à chaque appui, la plus prioritaire qui est prête part
/// (0 d'abord), une compétence en recharge est sautée. D'après les guides de joueurs (pas de documentation officielle).
/// La macro conseillée part des lignes de la rotation du build suivi (classes.json → play) : compétences de situation, buffs,
/// mantras, compétences à charger ou à longue recharge et ouverture restent à la main ; dans une ligne, ce qui ne part
/// qu'après un autre sort d'abord, puis les recharges par importance, et une seule compétence sans recharge, en dernier
/// (toujours prête, elle bloquerait celles d'après). Seules les compétences apprises (stigmas : équipés) y entrent.
/// </summary>
public static class MacroAdvisor
{
    public const int LineSize = 4;
    /// <summary>Recharge à partir de laquelle une compétence est gardée pour la bonne fenêtre plutôt que lancée par la macro.</summary>
    public const double LongCooldown = 60;

    sealed record Candidate(string Name, SkillKnowledge? Knowledge, SpellData? Spell, double Share)
    {
        public double Cooldown => Spell?.Cooldown ?? 0;
        public bool Conditional => Spell?.Requires is { Count: > 0 };
    }

    public static MacroPlan Plan(GameDatabase db, AdviceReport r)
    {
        var p = r.Profile;
        var source = $"lignes de la rotation du build {(p.UsesDesigned ? "conçu" : "des joueurs")} en {GameDatabase.ModeName(p.Mode)}";
        if (p.Play is not { Rotation.Count: > 0 } play) return new([], [], [], source);

        static string Norm(string s) => GameDatabase.NormalizeLabel(s);
        static string Clean(string step) => step.EndsWith("(stigma)", StringComparison.Ordinal) ? step[..^"(stigma)".Length].Trim() : step.Trim();
        var shares = r.Skills.Skills.GroupBy(a => Norm(a.Skill.Name)).ToDictionary(g => g.Key, g => g.Max(a => a.DamageShare));
        var spells = db.Spells.Values.Where(s => s.Class == p.Class.Code && s.Kind != "Passive").ToList();
        // Apprise ; un stigma seulement s'il est dans un emplacement (équipé et monté).
        bool Has(string name) => r.Character.Skills.Any(s => Norm(s.Name) == Norm(name) && s.Acquired
                                                             && (s.Category != "Dp" || (s.Equipped && s.Level > 0)));
        Candidate Make(string name) => new(name, p.Skill(name), spells.FirstOrDefault(s => Norm(s.Name) == Norm(name)), shares.GetValueOrDefault(Norm(name)));

        var manual = new List<MacroManual>();
        var missing = new List<string>();
        var seen = new HashSet<string>();
        string? ManualWhy(Candidate c)
        {
            var role = c.Knowledge?.Role ?? "";
            if (c.Spell?.TriggeredBy is { Count: > 0 } by) return $"se déclenche seule à chaque {string.Join(" ou ", by.Take(2))} : rien à appuyer";
            if (CombatAnalyzer.SituationalRole(p, c.Name) is { } situation)
                return $"{situation} : à lancer au bon moment, la macro la lancerait dès qu'elle est prête";
            if (role.StartsWith("Mantra", StringComparison.OrdinalIgnoreCase) || role.StartsWith("Aura", StringComparison.OrdinalIgnoreCase))
                return $"{role} : un 2e appui la coupe, la macro l'éteindrait en boucle";
            if (role.StartsWith("Buff", StringComparison.OrdinalIgnoreCase))
                return $"{role} : lance-le toi-même au début de ta fenêtre de dégâts (une compétence lancée à la main passe avant la macro)";
            if (role.Contains("chargé", StringComparison.OrdinalIgnoreCase) || c.Spell?.Specs.Any(x => x.Text.Contains("à charger", StringComparison.OrdinalIgnoreCase)) == true)
                return "à charger : la macro ne garde pas la touche enfoncée, elle partirait au premier palier";
            if (c.Cooldown >= LongCooldown)
                return $"recharge de {c.Cooldown:0} s : à garder pour la bonne fenêtre (burst, boss), pas lancée au hasard par la macro";
            return null;
        }

        // Lignes : celles de la rotation, sauf l'ouverture et le remplissage (ajouté à la fin d'une ligne).
        static bool Opener(RotationPhase ph) => ph.Name.StartsWith("Ouverture", StringComparison.OrdinalIgnoreCase);
        static bool Filler(RotationPhase ph) => ph.Name.StartsWith("Remplissage", StringComparison.OrdinalIgnoreCase);
        var lines = new List<(string Name, List<Candidate> Skills)>();
        var fillers = new List<Candidate>();
        foreach (var ph in play.Rotation.Where(ph => !Opener(ph)))
        {
            var kept = new List<Candidate>();
            foreach (var name in ph.Steps.Select(Clean).Distinct())
            {
                if (!seen.Add(Norm(name))) continue;
                var c = Make(name);
                if (ManualWhy(c) is { } why) { if (Has(name)) manual.Add(new(name, why)); continue; }
                if (!Has(name)) { missing.Add(name); continue; }
                kept.Add(c);
            }
            if (Filler(ph)) fillers.AddRange(kept);
            else if (kept.Count > 0) lines.Add((ph.Name, kept));
        }
        // L'ouverture : ce qui n'est dans aucune ligne se lance à la main, une fois au début.
        foreach (var ph in play.Rotation.Where(Opener))
            foreach (var name in ph.Steps.Select(Clean).Distinct())
                if (seen.Add(Norm(name)) && Has(name)) manual.Add(new(name, ManualWhy(Make(name)) ?? "ouverture : lancée une fois au début du combat"));

        double Value(Candidate c) => (c.Knowledge?.Priority.GetValueOrDefault(p.Mode) ?? 0.5) * (1 + 2 * c.Share);
        var result = new List<MacroLine>();
        List<MacroSlot> Order(List<Candidate> skills, out List<Candidate> overflow, out List<Candidate> extraFillers)
        {
            // Ce qui ne part qu'après un autre sort d'abord (il part dès qu'il est débloqué), puis les recharges, puis une seule
            // compétence sans recharge en dernier.
            var noCooldown = skills.Where(c => c.Cooldown <= 0 && !c.Conditional).OrderByDescending(Value).ToList();
            var ordered = skills.Where(c => c.Conditional).OrderByDescending(Value)
                .Concat(skills.Where(c => !c.Conditional && c.Cooldown > 0).OrderByDescending(Value)).ToList();
            extraFillers = noCooldown.Skip(1).ToList();
            var filler = noCooldown.FirstOrDefault();
            var room = LineSize - (filler is null ? 0 : 1);
            overflow = ordered.Skip(room).ToList();
            var slots = ordered.Take(room).Append(filler).OfType<Candidate>().ToList();
            return slots.Select((c, i) => new MacroSlot(i, c.Name, SlotWhy(c, i, c == filler), c.Knowledge?.Why)).ToList();
        }
        string SlotWhy(Candidate c, int priority, bool filler)
        {
            var share = c.Share > 0.005 ? $" · {Fmt.Pct(c.Share)} de tes dégâts" : "";
            if (filler) return $"sans recharge : en dernier, il remplit les temps morts{share}";
            if (c.Conditional)
                return $"ne part qu'après {string.Join(" ou ", c.Spell!.Requires!.Take(2))} : en priorité {priority}, il part dès qu'il est débloqué{share}";
            return $"recharge de {c.Cooldown:0.#} s{share}";
        }

        var pendingFillers = new List<Candidate>(fillers);
        foreach (var (name, skills) in lines)
        {
            var queue = skills;
            var part = 0;
            while (queue.Count > 0)
            {
                // La ligne n'a pas de compétence sans recharge : elle prend le remplissage, s'il en reste.
                if (!queue.Any(c => c.Cooldown <= 0 && !c.Conditional) && pendingFillers.Count > 0 && queue.Count < LineSize)
                {
                    queue = [.. queue, pendingFillers[0]];
                    pendingFillers.RemoveAt(0);
                }
                var slots = Order(queue, out var overflow, out var extra);
                result.Add(new(part++ == 0 ? name : $"{name} (suite)", slots));
                pendingFillers.AddRange(extra);
                queue = overflow;
            }
        }
        // Remplissage restant : sa propre ligne (une compétence sans recharge par ligne).
        foreach (var f in pendingFillers.DistinctBy(f => Norm(f.Name)))
            result.Add(new("Remplissage", [new MacroSlot(0, f.Name, SlotWhy(f, 0, true), f.Knowledge?.Why)]));
        return new(result, manual, missing.Distinct().ToList(), source);
    }
}
