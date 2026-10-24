using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aion2Builder.Combat;

/// <summary>
/// Lit la fenêtre « Détails du combat » de NotMeter (en français) à partir des mots reconnus par l'OCR de Windows.
/// Les colonnes sont repérées par leurs titres (« Dégâts / Soins », « Part de vos dégâts », « Temps actif des buffs »),
/// pas par des positions fixes : la fenêtre peut être redimensionnée. Erreurs d'OCR courantes corrigées : « % » lu
/// « 0/0 », « 1 » lu « I » ou « l », « 0 » lu « O ». Un sort fait trois lignes : nom (+ niveau, dégâts, part),
/// critique / coups de dos, utilisations / coups / intervalle moyen.
/// </summary>
public static partial class NotMeterReader
{
    [GeneratedRegex(@"^\d{1,3}([,.]\d{3})+$")]
    private static partial Regex Thousands();

    [GeneratedRegex(@"[0oO]/[0oO]?$")]
    private static partial Regex PercentRead();

    [GeneratedRegex(@"^\d{1,2}:\d{2}$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^[xX×]([0-9lIoO]+)$")]
    private static partial Regex Times();

    sealed record Row(double Y, List<OcrWord> Words)
    {
        public int Index(string key) => Words.FindIndex(w => Key(w.Text) == key);
        public bool Has(string key) => Index(key) >= 0;
        public OcrWord? After(string key) => Index(key) is >= 0 and var i && i + 1 < Words.Count ? Words[i + 1] : null;
        public Row Where(Func<OcrWord, bool> keep) => new(Y, Words.Where(keep).ToList());
    }

    /// <summary>Minuscules sans accents ni ponctuation : « Dégâts » → « degats ».</summary>
    public static string Key(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString().Replace("œ", "oe");
    }

    static string Digits(string s) => s.Replace('I', '1').Replace('l', '1').Replace('|', '1').Replace('O', '0').Replace('o', '0');

    /// <summary>« 537,198 » → 537198 ; « 2,26K » → 2260 ; « 1344.95ms » → 1344,95 ; « IO » → 10.</summary>
    public static double? Number(string text)
    {
        var t = text.Trim().TrimEnd('.', '…');
        if (t.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) t = t[..^2];
        var factor = 1.0;
        if (t.EndsWith('K') || t.EndsWith('k')) { factor = 1_000; t = t[..^1]; }
        else if (t.EndsWith('M')) { factor = 1_000_000; t = t[..^1]; }
        t = Digits(t);
        t = Thousands().IsMatch(t) ? t.Replace(",", "").Replace(".", "") : t.Replace(',', '.');
        return t.Length > 0 && t.All(c => char.IsDigit(c) || c == '.') && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v * factor : null;
    }

    /// <summary>« 21,50/0 » (le « % » lu « 0/0 ») → 0,215 ; « 100% » → 1 ; « 87,1 » suivi de « % » → 0,871.</summary>
    static double? Percent(Row row, OcrWord? w)
    {
        if (w is null) return null;
        var t = w.Text.Trim();
        var i = row.Words.IndexOf(w);
        var next = i + 1 < row.Words.Count ? row.Words[i + 1].Text.Trim() : "";
        if (t.EndsWith('%')) t = t[..^1];
        else if (PercentRead().Match(t) is { Success: true } m && m.Index > 0) t = t[..m.Index];
        else if (next != "%") return null;
        return Number(t) is { } v && v <= 100 ? v / 100 : null;
    }

    static bool IsPercent(OcrWord w) => w.Text.Contains('%') || PercentRead().IsMatch(w.Text);

    static bool HasLetters(OcrWord w) => Key(w.Text).Any(char.IsLetter);

    /// <summary>Mots d'un nom ; deux morceaux collés (l'OCR coupe parfois un mot : « Pa rchemin ») sont recollés.</summary>
    static string Name(List<OcrWord> words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0 && words[i].X - (words[i - 1].X + words[i - 1].Width) > words[i].Height * 0.2) sb.Append(' ');
            sb.Append(words[i].Text);
        }
        return sb.ToString();
    }

    static List<Row> Rows(IEnumerable<OcrWord> words)
    {
        var sorted = words.OrderBy(w => w.Y + w.Height / 2).ToList();
        if (sorted.Count == 0) return [];
        var height = sorted.Select(w => w.Height).OrderBy(x => x).ElementAt(sorted.Count / 2);
        var rows = new List<Row>();
        foreach (var w in sorted)
        {
            var center = w.Y + w.Height / 2;
            if (rows.Count > 0 && Math.Abs(rows[^1].Y - center) <= height * 0.5) rows[^1].Words.Add(w);
            else rows.Add(new Row(center, [w]));
        }
        foreach (var r in rows) r.Words.Sort((a, b) => a.X.CompareTo(b.X));
        return rows;
    }

    /// <summary>Une capture de la fenêtre « Détails du combat ».</summary>
    public static CombatLog ReadPage(IReadOnlyList<OcrWord> words)
    {
        var rows = Rows(words);
        var width = words.Count == 0 ? 0 : words.Max(w => w.X + w.Width);

        // En-tête : personnage / cible, totaux, durée, coups.
        var character = "";
        var target = "";
        if (rows.FirstOrDefault(r => r.Words.Any(w => w.Text == "/") && r.Words.Count >= 3) is { } who)
        {
            var slash = who.Words.FindIndex(w => w.Text == "/");
            character = who.Words[0].Text;
            target = string.Join(" ", who.Words.Skip(slash + 1).Select(w => w.Text));
        }
        double total = 0, dps = 0, duration = 0;
        var hits = 0;
        var totalsLabel = rows.FindIndex(r => r.Has("degats") && r.Has("totaux"));
        if (totalsLabel >= 0)
        {
            // Les nombres sont sur la ligne suivante : dégâts totaux puis DPS, de gauche à droite.
            var numbers = rows.Skip(totalsLabel + 1).Take(2).SelectMany(r => r.Words)
                .Where(w => !IsPercent(w) && Number(w.Text) is > 0).OrderBy(w => w.X).Select(w => Number(w.Text)!.Value).ToList();
            if (numbers.Count > 0) total = numbers[0];
            if (numbers.Count > 1) dps = numbers[1];
        }
        if (rows.FirstOrDefault(r => r.Has("duree")) is { } d && d.Words.FirstOrDefault(w => Clock().IsMatch(w.Text)) is { } clock)
            duration = int.Parse(clock.Text[..clock.Text.IndexOf(':')]) * 60 + int.Parse(clock.Text[(clock.Text.IndexOf(':') + 1)..]);
        if (rows.FirstOrDefault(r => r.Has("tous") && r.Has("coups")) is { } summary && Number(summary.After("coups")?.Text ?? "") is { } h)
            hits = (int)h;

        // Titres des colonnes de la liste des sorts.
        var header = rows.FirstOrDefault(r => r.Has("competences"));
        var damageX = header?.Words.FirstOrDefault(w => Key(w.Text) == "degats" && w.X > width * 0.3)?.X ?? width * 0.64;
        var shareX = header?.Words.FirstOrDefault(w => Key(w.Text) == "part" && w.X > damageX)?.X ?? width * 0.72;
        var buffX = header?.Words.FirstOrDefault(w => Key(w.Text) is "temps" or "tems" or "tps")?.X
                    ?? (header?.Words.FirstOrDefault(w => Key(w.Text) == "actif")?.X - 40) ?? width * 0.83;
        var skillCount = header is not null && Number(header.After("competences")?.Text ?? "") is { } n ? (int)n : 0;
        var start = header is null ? 0 : rows.IndexOf(header) + 1;

        // Sorts : à gauche de la colonne des buffs.
        var skills = new List<CombatSkill>();
        foreach (var full in rows.Skip(start))
        {
            var row = full.Where(w => w.X < buffX - 5);
            if (row.Words.Count == 0) continue;
            if (row.Has("utilisations"))
            {
                if (skills.Count == 0) continue;
                var uses = Number(row.After("utilisations")?.Text ?? "");
                var count = Number(row.After("coups")?.Text ?? "");
                var interval = Number(row.After("moyen")?.Text ?? "");
                skills[^1] = skills[^1] with { Uses = (int)(uses ?? 0), Hits = (int)(count ?? 0), IntervalMs = interval };
            }
            else if (row.Has("critique"))
            {
                if (skills.Count == 0) continue;
                skills[^1] = skills[^1] with { Crit = Percent(row, row.After("critique")), Back = Percent(row, row.After("dos")) };
            }
            else
            {
                var name = row.Words.Where(w => w.X < damageX - 20 && HasLetters(w) && Key(w.Text) is not ("lv" or "dot")).ToList();
                if (name.Count == 0) continue;
                var level = row.Words.Where(w => w.X > name[0].X && w.X < damageX - 20 && !HasLetters(w) && Number(w.Text) is > 0 and <= 40)
                    .Select(w => (int?)Number(w.Text)).FirstOrDefault();
                var damage = row.Words.Where(w => w.X >= damageX - 80 && w.X < shareX - 20 && !IsPercent(w)).Select(w => Number(w.Text)).FirstOrDefault(v => v is not null);
                var share = row.Words.Where(w => w.X >= shareX - 60).Select(w => Percent(row, w)).FirstOrDefault(v => v is not null);
                if (damage is null && share is null) continue;
                skills.Add(new CombatSkill(Name(name), level, damage ?? 0, share ?? 0, 0, 0, null, null, null));
            }
        }
        // Un nombre de dégâts manqué par l'OCR se déduit de la part.
        skills = skills.Select(s => s.Damage > 0 || total <= 0 ? s : s with { Damage = Math.Round(s.Share * total) })
            .Select(s => s.Share > 0 || total <= 0 ? s : s with { Share = s.Damage / total }).ToList();

        // Buffs : la colonne de droite. Une ligne nom + temps actif, puis une ligne durée · ×N · niveau.
        var buffs = new List<CombatBuff>();
        foreach (var full in rows.Skip(start))
        {
            var row = full.Where(w => w.X >= buffX - 5);
            if (row.Words.Count == 0 || row.Has("faites") || row.Has("glisser")) continue;
            var times = row.Words.Select(w => Times().Match(w.Text)).FirstOrDefault(m => m.Success);
            // Ligne de détail (« 01:02 · x20 · Lv. 12 ») : une durée, un « ×N » ou un niveau, même si l'OCR a abîmé le reste.
            var detail = times is not null || row.Words.Any(w => Clock().IsMatch(w.Text) || Key(w.Text) is var k && (k.StartsWith("lv") || k.StartsWith("cv")));
            if (detail)
            {
                if (buffs.Count == 0) continue;
                var level = row.Words.FindIndex(w => Key(w.Text) is var k && (k.StartsWith("lv") || k.StartsWith("cv")));
                int? lv = null;
                if (level >= 0)
                {
                    var rest = Key(row.Words[level].Text)[2..];
                    lv = rest.Length > 0 && Number(rest) is { } a ? (int)a : level + 1 < row.Words.Count && Number(row.Words[level + 1].Text) is { } b ? (int)b : null;
                }
                buffs[^1] = buffs[^1] with { Count = times is not null && Number(times.Groups[1].Value) is { } c ? (int)c : buffs[^1].Count, Level = lv ?? buffs[^1].Level };
            }
            else
            {
                var uptime = row.Words.Select(w => Percent(row, w)).LastOrDefault(v => v is not null);
                var name = row.Words.Where(w => HasLetters(w) && !IsPercent(w)).Select(w => w.Text).ToList();
                if (name.Count == 0) continue;
                buffs.Add(new CombatBuff(Name(row.Words.Where(w => HasLetters(w) && !IsPercent(w)).ToList()), uptime, 1, null));
            }
        }

        if (dps <= 0 && total > 0 && duration > 0) dps = total / duration;
        return new CombatLog
        {
            ImportedAtUtc = DateTime.UtcNow, Character = character, Target = target, Duration = duration, TotalDamage = total, Dps = dps,
            Hits = hits, SkillCount = skillCount, Skills = skills, Buffs = buffs, Pages = 1,
        };
    }

    /// <summary>
    /// Plusieurs captures du même combat (liste des sorts défilée) : les sorts et buffs sont fusionnés par nom.
    /// Des captures de combats différents (dégâts totaux différents) sont refusées.
    /// </summary>
    public static CombatLog Read(IEnumerable<IReadOnlyList<OcrWord>> pages)
    {
        CombatLog? log = null;
        foreach (var page in pages.Select(ReadPage))
        {
            if (page.Skills.Count == 0 && page.TotalDamage <= 0) continue;
            if (log is null) { log = page; continue; }
            if (log.TotalDamage > 0 && page.TotalDamage > 0 && Math.Abs(log.TotalDamage - page.TotalDamage) > 1)
                throw new InvalidDataException($"Captures de deux combats différents ({log.TotalDamage:N0} et {page.TotalDamage:N0} de dégâts) : importe-les séparément.");
            var current = log;
            log = log with
            {
                Character = current.Character.Length > 0 ? current.Character : page.Character,
                Target = current.Target.Length > 0 ? current.Target : page.Target,
                Duration = current.Duration > 0 ? current.Duration : page.Duration,
                TotalDamage = current.TotalDamage > 0 ? current.TotalDamage : page.TotalDamage,
                Dps = current.Dps > 0 ? current.Dps : page.Dps,
                Hits = current.Hits > 0 ? current.Hits : page.Hits,
                SkillCount = Math.Max(current.SkillCount, page.SkillCount),
                Skills = [.. current.Skills, .. page.Skills.Where(s => current.Skills.All(x => Key(x.Name) != Key(s.Name)))],
                Buffs = [.. current.Buffs, .. page.Buffs.Where(b => current.Buffs.All(x => Key(x.Name) != Key(b.Name)))],
                Pages = current.Pages + 1,
            };
        }
        if (log is null || log.Skills.Count == 0)
            throw new InvalidDataException("Pas de fenêtre « Détails du combat » de NotMeter reconnue sur ces captures (NotMeter en français, liste des compétences visible).");
        return log with { Skills = log.Skills.OrderByDescending(s => s.Damage).ToList() };
    }
}
