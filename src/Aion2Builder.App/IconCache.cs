using Aion2Builder.GameData;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Aion2Builder.App;

/// <summary>
/// Icônes officielles (pièces, compétences) lues à l'exécution sur le CDN du jeu, jamais livrées avec l'appli : téléchargées à
/// la première demande, réduites à 48 px et gardées dans %APPDATA%\Aion2Builder\icons, puis en mémoire. L'écran n'attend
/// jamais : la case s'affiche vide et l'image y arrive une fois prête ; hors ligne, la case reste vide.
/// </summary>
static class IconCache
{
    /// <summary>Taille gardée : deux fois les plus grandes cases (écrans à 200 %).</summary>
    const int Pixels = 48;

    /// <summary>Après un échec (hors ligne, fichier introuvable), on ne réessaie pas avant ce délai : les écrans se reconstruisent souvent.</summary>
    static readonly TimeSpan Retry = TimeSpan.FromMinutes(2);

    static string Dir => Path.Combine(AppPaths.Root, "icons");

    // Lus et écrits sur le fil de l'interface seulement : pas de verrou.
    static readonly Dictionary<string, Bitmap> Loaded = [];
    static readonly Dictionary<string, Task<Bitmap?>> Pending = [];
    static readonly Dictionary<string, DateTime> Failed = [];

    /// <summary>Une soixantaine d'icônes par personnage : quelques téléchargements à la fois.</summary>
    static readonly SemaphoreSlim Downloads = new(6);

    static readonly Lazy<HttpClient> Http = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Aion2Builder/{Updater.CurrentLabel} (+https://github.com/Tomtoxi44/aion2-builder)");
        return http;
    });

    /// <summary>
    /// Case carrée de <paramref name="size"/> px (cadre compris) : cadre et fond donnés, l'icône de <paramref name="url"/> dedans
    /// dès qu'elle est prête. Une adresse absente ou hors du CDN du jeu laisse la case vide.
    /// </summary>
    public static Border Box(string? url, double size, IBrush frame, IBrush background)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        var box = new Border
        {
            Width = size, Height = size, BorderBrush = frame, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Background = background, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Child = image,
        };
        if (!Official(url)) return box;
        if (Loaded.TryGetValue(url!, out var bitmap)) image.Source = bitmap;
        else if (!Failed.TryGetValue(url!, out var at) || DateTime.UtcNow - at > Retry)
        {
            if (!Pending.TryGetValue(url!, out var task)) Pending[url!] = task = Load(url!);
            Fill(image, task);
        }
        return box;
    }

    /// <summary>Seulement les images du jeu (https, CDN ou site de NC) : le fichier du perso pourrait avoir été modifié à la main.</summary>
    static bool Official(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.EndsWith(".playnccdn.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".plaync.com", StringComparison.OrdinalIgnoreCase));

    static async void Fill(Image image, Task<Bitmap?> task)
    {
        if (await task is { } bitmap) image.Source = bitmap;
    }

    static async Task<Bitmap?> Load(string url)
    {
        var bitmap = await Task.Run(() => Fetch(url));
        Pending.Remove(url);
        if (bitmap is null) Failed[url] = DateTime.UtcNow;
        else Loaded[url] = bitmap;
        return bitmap;
    }

    /// <summary>Hors du fil de l'interface : le fichier du cache s'il existe, sinon le CDN (image réduite puis enregistrée).</summary>
    static async Task<Bitmap?> Fetch(string url)
    {
        var name = string.Concat(Path.GetFileName(new Uri(url).AbsolutePath).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (name.Length == 0) return null;
        var file = Path.Combine(Dir, name);
        try
        {
            if (File.Exists(file)) return new Bitmap(file);
        }
        catch
        {
            // Fichier abîmé : on le retélécharge.
        }
        await Downloads.WaitAsync();
        try
        {
            using var stream = new MemoryStream(await Http.Value.GetByteArrayAsync(url));
            var bitmap = Bitmap.DecodeToWidth(stream, Pixels, BitmapInterpolationMode.HighQuality);
            Save(bitmap, file);
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            Downloads.Release();
        }
    }

    /// <summary>Enregistre sous un nom provisoire puis renomme : jamais de fichier à moitié écrit. Un échec n'empêche pas l'affichage.</summary>
    static void Save(Bitmap bitmap, string file)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var temp = file + ".tmp";
            bitmap.Save(temp, PngBitmapEncoderOptions.Default);
            File.Move(temp, file, true);
        }
        catch
        {
            // Disque plein ou dossier protégé : l'icône reste en mémoire pour cette session.
        }
    }
}
