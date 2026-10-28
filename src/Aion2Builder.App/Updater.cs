using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Aion2Builder.Analysis;
using Aion2Builder.GameData;
using Aion2Builder.Import;
using Aion2Builder.Model;
using Aion2Builder.Storage;

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
    /// Télécharge la nouvelle version et vérifie son empreinte. Avant de remplacer quoi que ce soit, la lance une fois en
    /// essai, sans fenêtre (<see cref="SelfTest"/>) : si Windows la bloque (Smart App Control refuse un exe non signé qu'il ne
    /// connaît pas) ou si elle plante, la version en cours reste en place. Ensuite seulement, elle prend la place de l'exe
    /// en cours (renommé, puisqu'un exe lancé ne peut pas être supprimé) et se lance ; si elle s'arrête avant d'avoir ouvert
    /// sa fenêtre, l'ancienne est remise en place. L'appelant ferme ensuite l'application.
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

        // 1. Essai sans fenêtre, avant de toucher à l'exe en cours.
        progress.Report($"Essai de la version {release.Label} avant de l'installer…");
        Process trial;
        try { trial = Process.Start(new ProcessStartInfo(downloaded, "--selftest") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir })!; }
        catch (Win32Exception ex)
        {
            File.Delete(downloaded);
            throw new InvalidOperationException(Refused(release, ex));
        }
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try { await trial.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                trial.Kill();
                await trial.WaitForExitAsync(CancellationToken.None);
                File.Delete(downloaded);
                throw new InvalidOperationException($"La version {release.Label} ne répond pas à l'essai : ta version {CurrentLabel} reste en place.");
            }
        }
        if (trial.ExitCode != 0)
        {
            var reason = File.Exists(SelfTestLog) ? File.ReadLines(SelfTestLog).FirstOrDefault() : null;
            File.Delete(downloaded);
            throw new InvalidOperationException($"La version {release.Label} plante au démarrage sur ce PC{(reason is null ? "" : $" ({reason})")} : ta version {CurrentLabel} reste en place.");
        }

        // 2. Remplacement, puis lancement : la nouvelle version écrit « .maj-ok » une fois sa fenêtre ouverte.
        var old = Path.Combine(dir, $"{name}.ancien-{DateTime.Now:yyyyMMddHHmmss}.exe");
        var ok = Path.Combine(dir, $"{name}.maj-ok");
        File.Delete(ok);
        File.Move(exe, old);
        File.Move(downloaded, exe);
        void Restore()
        {
            File.Move(exe, Path.Combine(dir, $"{name}.nouveau.exe"), overwrite: true);
            File.Move(old, exe);
        }
        Process started;
        try { started = Process.Start(new ProcessStartInfo(exe, AfterUpdate) { UseShellExecute = false, WorkingDirectory = dir })!; }
        catch (Win32Exception ex)
        {
            Restore();
            throw new InvalidOperationException(Refused(release, ex));
        }
        progress.Report($"Démarrage de la version {release.Label}…");
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until && !File.Exists(ok) && !started.HasExited) await Task.Delay(250, CancellationToken.None);
        if (File.Exists(ok))
        {
            try { File.Delete(ok); } catch (IOException) { }
            return;
        }
        if (started.HasExited)
        {
            Restore();
            throw new InvalidOperationException($"La version {release.Label} s'est arrêtée au démarrage : ta version {CurrentLabel} est remise en place.");
        }
        // Toujours en route au bout d'une minute, sans fenêtre encore : on la laisse finir de démarrer.
    }

    /// <summary>Message quand Windows refuse de lancer la nouvelle version.</summary>
    static string Refused(Release release, Win32Exception ex) => ex.NativeErrorCode is 4551 or 1260
        ? $"Windows bloque la version {release.Label} (Smart App Control ou contrôle des applications : l'exe n'est pas signé et Windows " +
          $"ne le connaît pas encore). Ta version {CurrentLabel} reste en place ; la mise à jour sera reproposée."
        : $"Windows refuse de lancer la version {release.Label} ({ex.Message}) : ta version {CurrentLabel} reste en place.";

    /// <summary>Argument passé à la version qui vient d'être installée : elle confirme son démarrage (<see cref="ConfirmStarted"/>).</summary>
    const string AfterUpdate = "--after-update";

    static string SelfTestLog => Path.Combine(Path.GetTempPath(), "Aion2Builder-selftest.txt");

    /// <summary>
    /// Essai sans fenêtre (« --selftest »), lancé par la mise à jour avant de remplacer l'exe : charge les données de jeu et
    /// analyse le dernier personnage ouvert avec ses combats, comme au lancement. 0 si tout va bien ; sinon 1, et l'erreur
    /// dans %TEMP%\Aion2Builder-selftest.txt.
    /// </summary>
    public static int SelfTest()
    {
        try
        {
            var db = GameDataStore.Load();
            var prefs = UiPrefs.Load();
            // Une fiche illisible n'est pas la faute de la nouvelle version (l'appli la saute aussi) : seule l'analyse compte.
            var store = new CharacterStore();
            CharacterSnapshot? character = null;
            if (prefs.LastFile is { } path && File.Exists(path))
                try { character = store.Load(path); }
                catch (Exception ex) when (ex is JsonException or IOException) { }
            if (character is not null)
            {
                var combats = new CombatStore().List(Path.GetFileNameWithoutExtension(prefs.LastFile!)).Select(x => x.Log).ToList();
                new Advisor(db).Analyze(character, prefs.Mode, store.LoadInputs(character), new PetStore().Load().Collection, combats);
            }
            File.Delete(SelfTestLog);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(SelfTestLog, $"{ex.GetType().Name} : {ex.Message}\n{ex}"); } catch (IOException) { }
            return 1;
        }
    }

    /// <summary>Fenêtre ouverte après une mise à jour : prévient l'ancienne version, qui attend avant de se fermer.</summary>
    public static void ConfirmStarted()
    {
        if (!Environment.GetCommandLineArgs().Contains(AfterUpdate) || Environment.ProcessPath is not { } exe) return;
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, $"{Path.GetFileNameWithoutExtension(exe)}.maj-ok"), CurrentLabel); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
