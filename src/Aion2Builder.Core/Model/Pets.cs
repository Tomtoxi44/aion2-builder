namespace Aion2Builder.Model;

/// <summary>Familier du catalogue Aion 2 Todo : identifiant du jeu, nom français et espèce (cogni, fera, natura, varian, special).</summary>
public sealed record PetInfo(int Id, string Name, string Genus);

/// <summary>
/// Collection de familiers relevée en jeu par le Pet Meter et gardée sur Aion 2 Todo : âmes cumulées par familier.
/// Elle est liée au compte Aion 2 Todo, pas à un personnage.
/// </summary>
public sealed record PetCollection
{
    public Dictionary<int, int> Souls { get; init; } = [];
    /// <summary>Âmes cumulées pour obtenir le familier (niveau 1), puis pour les niveaux 2 et 3.</summary>
    public List<int> Tiers { get; init; } = [5, 30, 105];
    public List<PetInfo> Catalog { get; init; } = [];
    public DateTime FetchedAtUtc { get; init; }

    public int SoulsOf(int petId) => Souls.GetValueOrDefault(petId);

    /// <summary>Niveau 0 (pas encore obtenu) à 3 : nombre de paliers d'âmes atteints.</summary>
    public int Level(int petId) => Tiers.Count(t => SoulsOf(petId) >= t);
}
