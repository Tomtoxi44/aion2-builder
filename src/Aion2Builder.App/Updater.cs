using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using Aion2Builder.Import;

namespace Aion2Builder.App;

/// <summary>
/// Mise à jour par Aion 2 Todo : chaque push sur main y dépose l'exe compilé par GitHub (version 1.1.N).
/// Une compilation locale (publier.cmd) porte la version 0.0.0 : elle ne propose pas de mise à jour.
/// </summary>
static class Updater
{
    public sealed record Release(bool Available, string? Version, long Size, string? Sha256, string? Url)
    {
        public string Label => $"v{Version}";
    }

    /// <summary>Version de l'exe en cours ; null pour une compilation locale.</summary>
    public static Version? Current { get; } = Assembly.GetEntryAssembly()?.GetName().Version is { Major: > 0 } v ? v : null;

    public static string CurrentLabel => Current is { } v ? $"v{v.Major}.{v.Minor}.{v.Build}" : "build locale";

    static HttpClient Client(TimeSpan timeout)
    {
        // Délai large : le site (offre gratuite) se met en veille et met jusqu'à une minute à se réveiller.
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Aion2Builder/{CurrentLabel} (+https://github.com/Tomtoxi44/aion2-builder)");
        return http;
    }

    /// <summary>Version plus récente disponible sur Aion 2 Todo, ou null (à jour, build locale ou rien de publié).</summary>
    public static async Task<Release?> Check(CancellationToken ct = default)
    {
        if (Current is null) return null;
        using var http = Client(TimeSpan.FromSeconds(90));
        var release = await http.GetFromJsonAsync<Release>(PetSyncClient.Site + "/api/downloads/builder", ct);
        return release is { Available: true, Version: { } text, Url: not null } && System.Version.TryParse(text, out var remote) && remote > Current
            ? release : null;
    }

    /// <summary>
    /// Télécharge la nouvelle version, vérifie son empreinte, prend la place de l'exe en cours (renommé, puisqu'un exe lancé
    /// ne peut pas être supprimé) et la lance. L'appelant ferme ensuite l'application.
    /// </summary>
    public static async Task Install(Release release, IProgress<string> progress, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Chemin de l'application introuvable.");
        var dir = Path.GetDirectoryName(exe)!;
        var name = Path.GetFileNameWithoutExtension(exe);
        var downloaded = Path.Combine(dir, $"{name}.nouveau.exe");

        using (var http = Client(TimeSpan.FromMinutes(15)))
        using (var res = await http.GetAsync(PetSyncClient.Site + release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? release.Size;
            await using var source = await res.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(downloaded);
            var buffer = new byte[81920];
            long done = 0;
            var shown = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                var percent = (int)(100 * done / Math.Max(1, total));
                if (percent == shown) continue;
                shown = percent;
                progress.Report($"Téléchargement de la mise à jour {release.Label}… {percent} %");
            }
        }

        string sha;
        await using (var file = File.OpenRead(downloaded)) sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (release.Sha256 is { } expected && !sha.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(downloaded);
            throw new InvalidDataException("Le fichier téléchargé est incomplet ou abîmé : réessaie.");
        }

        File.Move(exe, Path.Combine(dir, $"{name}.ancien-{DateTime.Now:yyyyMMddHHmmss}.exe"));
        File.Move(downloaded, exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
    }

    /// <summary>Efface les versions remplacées et les téléchargements interrompus (au lancement suivant).</summary>
    public static void CleanOld()
    {
        if (Environment.ProcessPath is not { } exe) return;
        var dir = Path.GetDirectoryName(exe)!;
        var name = Path.GetFileNameWithoutExtension(exe);
        foreach (var file in Directory.EnumerateFiles(dir, $"{name}.ancien-*.exe").Append(Path.Combine(dir, $"{name}.nouveau.exe")))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* encore utilisé : au prochain lancement */ }
        }
    }
}
