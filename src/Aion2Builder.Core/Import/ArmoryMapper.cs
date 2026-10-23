using System.Text.Json;
using System.Text.RegularExpressions;
using Aion2Builder.GameData;
using Aion2Builder.Model;

namespace Aion2Builder.Import;

/// <summary>Réponses brutes du site officiel pour un personnage (gardées telles quelles pour pouvoir les rejouer).</summary>
public sealed record RawCharacter(
    string Region, string Info, string Equipment,
    Dictionary<int, string> Items, Dictionary<int, string> Boards, DateTime FetchedAtUtc)
{
    public void SaveTo(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "region.txt"), Region);
        File.WriteAllText(Path.Combine(dir, "info.json"), Info);
        File.WriteAllText(Path.Combine(dir, "equipment.json"), Equipment);
        foreach (var (slot, json) in Items) File.WriteAllText(Path.Combine(dir, $"item_{slot}.json"), json);
        foreach (var (id, json) in Boards) File.WriteAllText(Path.Combine(dir, $"board{id}.json"), json);
    }

    public static RawCharacter LoadFrom(string dir, string? region = null)
    {
        static Dictionary<int, string> Files(string dir, string pattern, string prefix) =>
            Directory.GetFiles(dir, pattern)
                .Select(f => (Name: Path.GetFileNameWithoutExtension(f), Path: f))
                .Where(f => int.TryParse(f.Name[prefix.Length..], out _))
                .ToDictionary(f => int.Parse(f.Name[prefix.Length..]), f => File.ReadAllText(f.Path));

        var regionFile = Path.Combine(dir, "region.txt");
        region ??= File.Exists(regionFile) ? File.ReadAllText(regionFile).Trim() : "eu";
        return new RawCharacter(region,
            File.ReadAllText(Path.Combine(dir, "info.json")),
            File.ReadAllText(Path.Combine(dir, "equipment.json")),
            Files(dir, "item_*.json", "item_"),
            Files(dir, "board*.json", "board"),
            File.GetLastWriteTimeUtc(Path.Combine(dir, "info.json")));
    }
}

/// <summary>Convertit les réponses JSON du site officiel en <see cref="CharacterSnapshot"/>.</summary>
public static partial class ArmoryMapper
{
    /// <summary>pcId officiel (classe × genre × race, par blocs de 4 à partir de 5).</summary>
    static readonly string[] ClassByPcBlock = ["GLADIATOR", "TEMPLAR", "RANGER", "ASSASSIN", "ELEMENTALIST", "SORCERER", "CLERIC", "CHANTER"];

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    public static string StripTags(string? s) => TagRegex().Replace(s ?? "", "");

    public static string? ClassFromPcId(int pcId) =>
        pcId >= 5 && (pcId - 5) / 4 < ClassByPcBlock.Length ? ClassByPcBlock[(pcId - 5) / 4] : null;

    public static CharacterSnapshot Map(RawCharacter raw, GameDatabase data)
    {
        var parser = new EffectParser(data);
        using var infoDoc = JsonDocument.Parse(raw.Info);
        using var eqDoc = JsonDocument.Parse(raw.Equipment);
        var info = infoDoc.RootElement;
        var profile = info.GetProperty("profile");

        var className = Str(profile, "className");
        var classCode = (className is null ? null : data.FindClass(className)?.Code)
                        ?? ClassFromPcId(Int(profile, "pcId"))
                        ?? throw new ArmoryException($"Classe non reconnue : {className}");

        // Compétences d'abord : leurs noms servent à reconnaître les bonus "Nom de compétence +1".
        var skills = Array(eqDoc.RootElement, "skill", "skillList")
            .Select(s => new SkillInfo(Long(s, "id"), Str(s, "name") ?? "", Str(s, "category") ?? "",
                Int(s, "skillLevel"), Int(s, "acquired") == 1, Int(s, "equip") == 1, Int(s, "needLevel"), Str(s, "icon")))
            .ToList();
        var skillNames = skills.GroupBy(s => GameDatabase.NormalizeLabel(s.Name)).ToDictionary(g => g.Key, g => g.First().Name);
        string? SkillLookup(string label) => skillNames.GetValueOrDefault(GameDatabase.NormalizeLabel(label));

        var primary = Array(info, "stat", "statList")
            .Where(s => Str(s, "type") != "ItemLevel")
            .Select(s => new PrimaryStat(Str(s, "type") ?? "", Str(s, "name") ?? "", Int(s, "value"),
                Strings(s, "statSecondList").Select(d => parser.Parse(d)).ToList()))
            .ToList();
        var itemLevel = Array(info, "stat", "statList").Where(s => Str(s, "type") == "ItemLevel").Select(s => Int(s, "value")).FirstOrDefault();

        var equipment = Array(eqDoc.RootElement, "equipment", "equipmentList")
            .Select(e => MapItem(e, raw.Items.GetValueOrDefault(Int(e, "slotPos")), parser))
            .OrderBy(i => i.SlotPos)
            .ToList();

        var titles = Array(info, "title", "titleList")
            .Where(t => Str(t, "equipCategory") is { Length: > 0 })
            .Select(t => new TitleInfo(Long(t, "id"), Str(t, "name") ?? "", Str(t, "equipCategory")!, Str(t, "grade") ?? "",
                Array(t, "equipStatList").Select(d => parser.Parse(Str(d, "desc") ?? "")).Where(x => x.Raw.Length > 0).ToList()))
            .ToList();

        var boards = Array(info, "daevanion", "boardList")
            .Select(b =>
            {
                var id = Int(b, "id");
                var nodes = raw.Boards.TryGetValue(id, out var json) ? MapNodes(json, parser, SkillLookup) : [];
                return new DaevanionBoard(id, Str(b, "name") ?? $"Plateau {id}", Int(b, "open") == 1,
                    Int(b, "openNodeCount"), Int(b, "totalNodeCount"), nodes);
            })
            .ToList();

        var petwing = eqDoc.RootElement.TryGetProperty("petwing", out var pw) ? pw : default;
        ExtraGear? Extra(string kind, string prop) =>
            petwing.ValueKind == JsonValueKind.Object && petwing.TryGetProperty(prop, out var x) && x.ValueKind == JsonValueKind.Object
            && Str(x, "name") is { Length: > 0 } n
                ? new ExtraGear(kind, n, Str(x, "grade") ?? "None", Int(x, "level"), Int(x, "enchantLevel"), Str(x, "icon"))
                : null;

        return new CharacterSnapshot
        {
            CharacterId = Str(profile, "characterId") ?? "",
            Region = raw.Region,
            ServerId = Int(profile, "serverId"),
            ServerName = Str(profile, "serverName") ?? "",
            Name = StripTags(Str(profile, "characterName")),
            ClassCode = classCode,
            ClassName = className,
            Race = Str(profile, "raceName"),
            Level = Int(profile, "characterLevel"),
            ItemLevel = itemLevel,
            CombatPower = Int(profile, "combatPower"),
            FetchedAtUtc = raw.FetchedAtUtc,
            PrimaryStats = primary,
            Equipment = equipment,
            Wing = Extra("Ailes", "wing"),
            Pet = Extra("Familier", "pet"),
            Skills = skills,
            Boards = boards,
            Titles = titles,
        };
    }

    static EquippedItem MapItem(JsonElement e, string? detailJson, EffectParser parser)
    {
        var item = new EquippedItem
        {
            SlotPos = Int(e, "slotPos"),
            Slot = Str(e, "slotPosName") ?? "",
            Id = Long(e, "id"),
            Name = Str(e, "name") ?? "",
            Grade = Str(e, "grade") ?? "None",
            EnchantLevel = Int(e, "enchantLevel"),
            ExceedLevel = Int(e, "exceedLevel"),
            Icon = Str(e, "icon"),
        };
        if (detailJson is null) return item;

        using var doc = JsonDocument.Parse(detailJson);
        var d = doc.RootElement;
        if (!d.TryGetProperty("mainStats", out _)) return item; // réponse d'erreur (404…)

        List<Effect> Stats(string prop) => Array(d, prop)
            .Select(s => parser.FromItemStat(Str(s, "id") ?? "", Str(s, "name") ?? "", Str(s, "value"), Str(s, "extra"), Str(s, "minValue")))
            .ToList();

        return item with
        {
            HasDetail = true,
            ItemLevel = Int(d, "level"),
            MaxEnchantLevel = Int(d, "maxEnchantLevel"),
            Category = Str(d, "categoryName"),
            MagicStoneSlots = Int(d, "magicStoneSlotCount"),
            GodStoneSlots = Int(d, "godStoneSlotCount"),
            RandomSubStats = d.TryGetProperty("subStatRandom", out var r) && r.ValueKind == JsonValueKind.True,
            MainStats = Stats("mainStats"),
            SubStats = Stats("subStats"),
            SubSkills = Array(d, "subSkills").Select(s => $"{Str(s, "name")} +{Int(s, "level")}").ToList(),
            Sources = Strings(d, "sources"),
        };
    }

    static List<DaevanionNode> MapNodes(string json, EffectParser parser, Func<string, string?> skillLookup)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("nodeList", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Where(n => Str(n, "type") is { } t && t != "None")
            .Select(n =>
            {
                var type = Str(n, "type")!;
                var effects = Array(n, "effectList")
                    .Select(x => Str(x, "desc") ?? "").Where(x => x.Length > 0)
                    .Select(x => parser.Parse(x, skillLookup, skillNode: type == "SkillLevel"))
                    .ToList();
                return new DaevanionNode(Long(n, "nodeId"), Int(n, "row"), Int(n, "col"), type, Str(n, "grade") ?? "None",
                    Str(n, "name") ?? "", Int(n, "open") == 1, effects);
            })
            .ToList();
    }

    // ----- Lecture JSON tolérante (le site renvoie parfois null, des nombres en texte, etc.) -----

    static IEnumerable<JsonElement> Array(JsonElement e, params string[] path)
    {
        var cur = e;
        foreach (var p in path)
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return [];
        return cur.ValueKind == JsonValueKind.Array ? cur.EnumerateArray().ToList() : [];
    }

    static List<string> Strings(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;

    static int Int(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n
            : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) ? s : 0
            : 0;

    static long Long(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
