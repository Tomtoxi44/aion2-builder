namespace Aion2Builder.Model;

public enum GameMode { PvE, PvP }

/// <summary>Portée d'un effet : toujours actif, ou seulement en JcE / JcJ ("ATQ JcJ", "Boost de DGT JcE").</summary>
public enum StatScope { All, PvE, PvP }

/// <summary>
/// Un effet chiffré lu sur le site officiel : une stat ("PV +600", "VIT de combat +1.5%")
/// ou un bonus de niveau de compétence ("Cri de l'âme +1"). <see cref="StatKey"/> et
/// <see cref="SkillName"/> sont nuls si le libellé n'est pas reconnu.
/// </summary>
public sealed record Effect(string Raw, string? StatKey, StatScope Scope, double Value, bool IsPercent, string? SkillName = null)
{
    public bool IsStat => StatKey is not null;
    public bool IsSkill => SkillName is not null;
}

/// <summary>Stat principale (Force, Santé…) ou divine (Justice [Nezekan]…) avec ses effets dérivés.</summary>
public sealed record PrimaryStat(string Type, string Name, int Value, List<Effect> Effects);

public sealed record EquippedItem
{
    public required int SlotPos { get; init; }
    public required string Slot { get; init; }
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required string Grade { get; init; }
    public int EnchantLevel { get; init; }
    public int ExceedLevel { get; init; }
    public string? Icon { get; init; }

    // Détail de la pièce (null/0 si la fiche détaillée n'a pas pu être lue).
    public bool HasDetail { get; init; }
    public int ItemLevel { get; init; }
    public int MaxEnchantLevel { get; init; }
    public string? Category { get; init; }
    public int MagicStoneSlots { get; init; }
    public int GodStoneSlots { get; init; }
    public bool RandomSubStats { get; init; }
    public List<Effect> MainStats { get; init; } = [];
    public List<Effect> SubStats { get; init; } = [];
    public List<string> SubSkills { get; init; } = [];
    public List<string> Sources { get; init; } = [];

    /// <summary>Pierres gravées (pierres de mana sur arme et armure, pierres d'âme sur les accessoires).</summary>
    public List<ItemStone> Stones { get; init; } = [];
    /// <summary>Lien d'âme (0 à 100 %) : progression des gravures d'âme ; à 100 % elles sont au maximum et verrouillées.</summary>
    public double? SoulBindRate { get; init; }
    /// <summary>Nombre de lignes de gravure d'âme de la pièce (0 = pas de gravure d'âme).</summary>
    public int SoulLineCount { get; init; }
    public string? SetName { get; init; }
    /// <summary>Bonus de panoplie actifs (texte du site ; souvent conditionnels, non comptés dans les stats).</summary>
    public List<string> ActiveSetBonuses { get; init; } = [];

    /// <summary>Niveau d'objet effectif : niveau de base + bonus d'enchantement.</summary>
    public int Power => ItemLevel + EnchantLevel;
}

/// <summary>Pierre gravée dans une pièce, avec son grade (Common, Rare, Legend, Unique).</summary>
public sealed record ItemStone(Effect Effect, string Grade, bool IsSoulStone);

public sealed record ExtraGear(string Kind, string Name, string Grade, int Level, int EnchantLevel, string? Icon);

public sealed record SkillInfo(long Id, string Name, string Category, int Level, bool Acquired, bool Equipped, int NeedLevel, string? Icon)
{
    public bool IsActive => Category.Equals("Active", StringComparison.OrdinalIgnoreCase);
    public bool IsPassive => Category.Equals("Passive", StringComparison.OrdinalIgnoreCase);
}

public sealed record DaevanionNode(long Id, int Row, int Col, string Type, string Grade, string Name, bool Open, List<Effect> Effects)
{
    public bool IsStart => Type == "Start";
    public bool IsSkill => Type == "SkillLevel";
}

public sealed record DaevanionBoard(int Id, string Name, bool Unlocked, int OpenNodes, int TotalNodes, List<DaevanionNode> Nodes)
{
    public int OpenPercent => TotalNodes == 0 ? 0 : (int)Math.Round(100.0 * OpenNodes / TotalNodes);
}

public sealed record TitleInfo(long Id, string Name, string Category, string Grade, List<Effect> Effects);

/// <summary>Photo d'un personnage telle que lue sur le site officiel (aion2.plaync.com).</summary>
public sealed record CharacterSnapshot
{
    public required string CharacterId { get; init; }
    public required string Region { get; init; }
    public required int ServerId { get; init; }
    public required string ServerName { get; init; }
    public required string Name { get; init; }
    /// <summary>Code de classe officiel : GLADIATOR, TEMPLAR, RANGER, ASSASSIN, ELEMENTALIST, SORCERER, CLERIC, CHANTER.</summary>
    public required string ClassCode { get; init; }
    public string? ClassName { get; init; }
    public string? Race { get; init; }
    public int Level { get; init; }
    public int ItemLevel { get; init; }
    public int CombatPower { get; init; }
    public DateTime FetchedAtUtc { get; init; }

    public List<PrimaryStat> PrimaryStats { get; init; } = [];
    public List<EquippedItem> Equipment { get; init; } = [];
    public ExtraGear? Wing { get; init; }
    public ExtraGear? Pet { get; init; }
    public List<SkillInfo> Skills { get; init; } = [];
    public List<DaevanionBoard> Boards { get; init; } = [];
    public List<TitleInfo> Titles { get; init; } = [];

    public string FileKey => $"{Region}-{ServerName}-{Name}".ToLowerInvariant();
}

/// <summary>
/// Ce que le site officiel ne montre pas et que le joueur relève en jeu : les points pas encore dépensés.
/// Les points déjà dépensés, eux, sont recalculés à partir de la fiche.
/// </summary>
public sealed record PlayerInputs
{
    public int UnspentSkillPoints { get; init; }
    public int UnspentDaevanion { get; init; }
    public int UnspentAriel { get; init; }
    public int UnspentAzphel { get; init; }
    /// <summary>Build suivi par les conseils : « players » (guides et joueurs observés) ou « designed » (build conçu).</summary>
    public string Build { get; init; } = "players";
    [System.Text.Json.Serialization.JsonIgnore] public bool UsesDesigned => Build == "designed";

    /// <summary>
    /// À qui comparer tes stats : « level » (les joueurs de ton niveau d'objet, par défaut), « best » (les mieux bâtis de ton
    /// niveau, d'après la note du Builder), « top » (le top de la classe), « mine » (ta liste).
    /// </summary>
    public string Comparison { get; init; } = "level";
    /// <summary>Ta liste de joueurs de la classe : relue par l'appli (comparison.json), pas enregistrée avec tes réglages.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<GameData.BenchPlayer> MyPeers { get; init; } = [];

    /// <summary>
    /// Conseils cochés « fait » (par titre), valables pour l'import <see cref="DoneFor"/> : au prochain import,
    /// le site dit lui-même ce qui a été fait, et la liste repart de zéro.
    /// </summary>
    public List<string> Done { get; init; } = [];
    public DateTime? DoneFor { get; init; }

    public bool IsDone(string title, DateTime fetchedAtUtc) => DoneFor == fetchedAtUtc && Done.Contains(title);

    public int UnspentFor(string pool) => pool switch
    {
        "PvE" => UnspentAriel,
        "PvP" => UnspentAzphel,
        _ => UnspentDaevanion,
    };
}
