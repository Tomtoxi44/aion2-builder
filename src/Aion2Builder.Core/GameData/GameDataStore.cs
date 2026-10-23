using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion2Builder.GameData;

/// <summary>Emplacements des fichiers de l'application (%APPDATA%\Aion2Builder sous Windows).</summary>
public static class AppPaths
{
    public static string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aion2Builder");

    /// <summary>Fichiers de données utilisateur : un fichier présent ici remplace celui embarqué.</summary>
    public static string DataDir => Path.Combine(Root, "data");
    public static string CharactersDir => Path.Combine(Root, "characters");
}

/// <summary>
/// Charge les données de jeu : version embarquée par défaut, remplacée fichier par fichier
/// par celles du dossier <see cref="AppPaths.DataDir"/> (pour ajuster poids, paliers, coûts sans recompiler).
/// </summary>
public static class GameDataStore
{
    public static readonly string[] FileNames = ["stats.json", "classes.json", "progression.json", "settings.json"];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static GameDatabase Load(string? overrideDir = null)
    {
        overrideDir ??= AppPaths.DataDir;
        return new GameDatabase(
            Read<StatCatalogFile>("stats.json", overrideDir),
            Read<ClassesFile>("classes.json", overrideDir),
            Read<ProgressionFile>("progression.json", overrideDir),
            Read<SettingsFile>("settings.json", overrideDir));
    }

    /// <summary>Indique, pour chaque fichier, s'il vient du dossier utilisateur ou de l'application.</summary>
    public static IEnumerable<(string File, bool Overridden)> Sources(string? overrideDir = null)
    {
        overrideDir ??= AppPaths.DataDir;
        return FileNames.Select(f => (f, File.Exists(Path.Combine(overrideDir, f))));
    }

    /// <summary>Copie les fichiers par défaut dans le dossier utilisateur (sans écraser ceux déjà modifiés).</summary>
    public static List<string> ExportDefaults(string? targetDir = null, bool overwrite = false)
    {
        targetDir ??= AppPaths.DataDir;
        Directory.CreateDirectory(targetDir);
        var written = new List<string>();
        foreach (var name in FileNames)
        {
            var path = Path.Combine(targetDir, name);
            if (File.Exists(path) && !overwrite) continue;
            File.WriteAllText(path, ReadEmbedded(name));
            written.Add(path);
        }
        return written;
    }

    static T Read<T>(string name, string overrideDir)
    {
        var path = Path.Combine(overrideDir, name);
        var text = File.Exists(path) ? File.ReadAllText(path) : ReadEmbedded(name);
        try
        {
            return JsonSerializer.Deserialize<T>(text, Json) ?? throw new InvalidDataException($"{name} est vide.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{(File.Exists(path) ? path : name)} est invalide : {ex.Message}", ex);
        }
    }

    static string ReadEmbedded(string name)
    {
        using var stream = typeof(GameDataStore).Assembly.GetManifestResourceStream($"Aion2Builder.Defaults.{name}")
                           ?? throw new FileNotFoundException($"Ressource embarquée manquante : {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
