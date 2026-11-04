using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Aion2Builder.Combat;
using Aion2Builder.Storage;

namespace Aion2Builder.App;

/// <summary>
/// Le Pet Meter de ce PC vu depuis le Builder, sans lui parler : ses processus et les fichiers qu'il écrit
/// (%LOCALAPPDATA%\AION2-pet-meter : data_dir.txt, fights, gathering ; son état à côté de son exe). Le lancer ne fait
/// qu'ouvrir son exe, comme un double-clic.
/// </summary>
static class PetMeterLink
{
    public const string ExeName = "aion2-pet-meter.exe";

    static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AION2-pet-meter");

    /// <param name="Exe">Son exe s'il est trouvé (choisi, en cours, à côté de son état, de l'installeur ou du Builder).</param>
    /// <param name="Version">Celle qui tourne, sinon celle de son dernier fichier écrit ; null si rien ne la dit.</param>
    /// <param name="Key">Clé Aion 2 Todo collée dans le Pet Meter (le Builder s'en sert pour les familiers et la récolte du compte).</param>
    public sealed record Status(bool Running, string? Exe, string? Version, bool Key, DateTime? LastFight, DateTime? LastGather);

    /// <summary>
    /// État lu sur le disque et dans les processus (hors du fil de l'interface). La clé n'y est pas : <see cref="WithKey"/>, sur le
    /// fil de l'interface, la reprend de <see cref="PetStore.PetMeterKey"/> (qui garde sa dernière lecture).
    /// </summary>
    public static Status Read(string? chosen)
    {
        var running = RunningPath(chosen);
        var exe = running ?? FindExe(chosen);
        var fight = Newest(PetMeterFights.Dir);
        var gather = Newest(PetMeterGathering.Dir);
        var version = (running is not null ? RunningVersion() : null) ?? FileVersion(fight, gather);
        return new Status(running is not null || IsRunning(exe), exe, version, false, fight?.LastWriteTime, gather?.LastWriteTime);
    }

    public static Status WithKey(Status s) => s with { Key = PetStore.PetMeterKey() is not null };

    /// <summary>Dossier d'état noté par le Pet Meter (souvent celui de son exe) ; null s'il n'a jamais tourné sur ce PC.</summary>
    static string? DataDir()
    {
        try
        {
            var file = Path.Combine(Local, "data_dir.txt");
            return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } dir ? dir : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>L'exe : celui que tu as choisi, sinon à côté de son état, à l'endroit de l'installeur, à côté du Builder.</summary>
    static string? FindExe(string? chosen)
    {
        if (chosen is { Length: > 0 } && File.Exists(chosen)) return chosen;
        var builder = Path.GetDirectoryName(Environment.ProcessPath);
        string?[] places =
        [
            DataDir() is { } dir ? Path.Combine(dir, ExeName) : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Aion2", "PetMeter", ExeName),
            builder is null ? null : Path.Combine(builder, "..", "PetMeter", ExeName),
            builder is null ? null : Path.Combine(builder, ExeName),
        ];
        return places.OfType<string>().Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    /// <summary>Noms de processus possibles : celui de l'exe d'origine, et celui de l'exe choisi s'il a été renommé.</summary>
    static IEnumerable<string> Names(string? exe) =>
        new[] { Path.GetFileNameWithoutExtension(ExeName), exe is null ? null : Path.GetFileNameWithoutExtension(exe) }.OfType<string>().Distinct();

    static bool IsRunning(string? exe) => Names(exe).Any(name =>
    {
        var found = Process.GetProcessesByName(name);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    });

    /// <summary>Chemin de l'exe d'un Pet Meter en cours, s'il y en a un (et si Windows le laisse lire).</summary>
    static string? RunningPath(string? chosen)
    {
        foreach (var name in Names(chosen))
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                    try { if (p.MainModule?.FileName is { } path) return path; }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
        return null;
    }

    /// <summary>
    /// Version du Pet Meter qui tourne : l'exe « un seul fichier » se décompresse dans %TEMP%\_MEI…, avec son version.txt ;
    /// le plus récent de ces dossiers qui contient ses données est le sien.
    /// </summary>
    static string? RunningVersion()
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories("_MEI*")
                .Where(d => File.Exists(Path.Combine(d.FullName, "version.txt")) && File.Exists(Path.Combine(d.FullName, "data", "npc_pets.json")))
                .MaxBy(d => d.CreationTimeUtc);
            return dir is null ? null : Clean(File.ReadAllText(Path.Combine(dir.FullName, "version.txt")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static FileInfo? Newest(string dir)
    {
        try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*.json").MaxBy(f => f.LastWriteTimeUtc) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static (string Path, DateTime Stamp, string? Version)? lastFile;
    static readonly object LastFileLock = new();

    /// <summary>Version notée dans le dernier fichier écrit (« meter » d'un combat, « client » d'une journée de récolte), relu s'il a changé.</summary>
    static string? FileVersion(FileInfo? fight, FileInfo? gather)
    {
        var file = new[] { fight, gather }.OfType<FileInfo>().MaxBy(f => f.LastWriteTimeUtc);
        if (file is null) return null;
        lock (LastFileLock)
            if (lastFile is { } known && known.Path == file.FullName && known.Stamp == file.LastWriteTimeUtc) return known.Version;
        string? version = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file.FullName));
            foreach (var key in new[] { "meter", "client" })
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && Clean(v.GetString()) is { } found)
                    version = found;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        lock (LastFileLock) lastFile = (file.FullName, file.LastWriteTimeUtc, version);
        return version;
    }

    /// <summary>« Pet Meter v0.21.2 », « pet-meter v0.21.2 » ou « v0.21.2 » → « v0.21.2 » ; « dev » et le reste : null.</summary>
    static string? Clean(string? text) =>
        text?.Trim().Split(' ').LastOrDefault() is { Length: > 1 } v && v[0] == 'v' && char.IsDigit(v[1]) ? v : null;

    /// <summary>Lance l'exe comme un double-clic (dans son dossier, où il range son état).</summary>
    public static void Start(string exe)
    {
        using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
    }
}
