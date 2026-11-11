using Word = Aion2Builder.Combat.OcrWord;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Aion2Builder.App;

/// <summary>
/// Reconnaissance de texte intégrée à Windows (Windows.Media.Ocr) : la capture est lue sur le PC, rien n'est envoyé.
/// Le moteur français est préféré (accents des noms de sorts), sinon celui de la langue de Windows.
/// </summary>
static class ScreenshotOcr
{
    /// <param name="minSize">Agrandit une petite capture jusqu'à ce côté (pixels) : l'OCR de Windows saute des chiffres sur un petit texte.</param>
    /// <param name="minScale">Agrandissement minimal (2 = image doublée) : le petit texte d'une fenêtre du jeu se lit mieux agrandi.</param>
    /// <param name="colors">Lit aussi la couleur du texte de chaque mot (le rang d'un objet du Marché se voit à la couleur de son nom).</param>
    /// <param name="brighten">
    /// Lit une copie où chaque pixel prend sa couleur la plus vive, en gris : un texte rouge ou bleu sur le fond sombre du jeu
    /// (« 0/11 » en rouge dans la fenêtre d'artisanat) devient presque blanc, alors que l'OCR le perd à sa luminosité normale.
    /// </param>
    public static async Task<List<Word>> Words(string path, int minSize = 0, double minScale = 1.0, bool colors = false, bool brighten = false)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("fr-FR")) ?? OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? throw new InvalidOperationException("Aucun moteur de reconnaissance de texte dans Windows : Paramètres → Heure et langue → Langue → ajoute le français.");
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = ScaleFor(decoder, minSize, minScale);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale), ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        byte[]? pixels = null;
        if (colors || brighten)
        {
            var buffer = new Windows.Storage.Streams.Buffer((uint)(width * height * 4));
            bitmap.CopyToBuffer(buffer);
            pixels = new byte[buffer.Length];
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
            reader.ReadBytes(pixels);
        }
        OcrResult result;
        if (brighten)
        {
            var bright = new byte[pixels!.Length];
            for (var i = 0; i < bright.Length; i += 4)
            {
                var top = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                bright[i] = bright[i + 1] = bright[i + 2] = top;
                bright[i + 3] = 255;
            }
            var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(bright);
            using var lit = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            result = await engine.RecognizeAsync(lit);
        }
        else result = await engine.RecognizeAsync(bitmap);
        var words = result.Lines.SelectMany(l => l.Words)
            .Select(w => new Word(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height)).ToList();
        if (!colors) return words;
        return words.Select(w => w with { Rgb = TextColor(pixels!, width, height, w) }).ToList();
    }

    /// <summary>Agrandissement appliqué par Words : le moteur refuse les images trop grandes (écran 4K), réduites, les positions restent proportionnelles.</summary>
    static double ScaleFor(BitmapDecoder decoder, int minSize, double minScale)
    {
        var size = (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight);
        return Math.Min(OcrEngine.MaxImageDimension / size, Math.Max(Math.Max(1.0, minScale), minSize / size));
    }

    /// <summary>
    /// Relit une zone de l'image, agrandie « zoom » fois de plus que la lecture de toute l'image faite par Words(path, minSize,
    /// minScale) : un mot court qu'elle a manqué (« Od » au Marché) se lit sur la zone seule, plus grande. Zone et mots rendus
    /// dans les coordonnées de cette lecture-là.
    /// </summary>
    public static async Task<List<Word>> WordsIn(string path, int minSize, double minScale, (double X, double Y, double W, double H) zone, double zoom, bool colors = false)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("fr-FR")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return [];
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var at = ScaleFor(decoder, minSize, minScale);
        var scale = at * zoom;
        uint fullW = (uint)Math.Max(1, decoder.PixelWidth * scale), fullH = (uint)Math.Max(1, decoder.PixelHeight * scale);
        // La zone, agrandie, sans sortir de l'image ni dépasser la taille que le moteur accepte.
        var x0 = (uint)Math.Clamp(zone.X / at * scale, 0, fullW - 1);
        var y0 = (uint)Math.Clamp(zone.Y / at * scale, 0, fullH - 1);
        var w = (uint)Math.Clamp(zone.W / at * scale, 1, Math.Min(fullW - x0, OcrEngine.MaxImageDimension));
        var h = (uint)Math.Clamp(zone.H / at * scale, 1, Math.Min(fullH - y0, OcrEngine.MaxImageDimension));
        var transform = new BitmapTransform
        {
            ScaledWidth = fullW, ScaledHeight = fullH, InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = x0, Y = y0, Width = w, Height = h },
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        var words = result.Lines.SelectMany(l => l.Words)
            .Select(x => new Word(x.Text, x.BoundingRect.X, x.BoundingRect.Y, x.BoundingRect.Width, x.BoundingRect.Height)).ToList();
        if (colors)
        {
            var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
            bitmap.CopyToBuffer(buffer);
            var pixels = new byte[buffer.Length];
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
            reader.ReadBytes(pixels);
            words = words.Select(x => x with { Rgb = TextColor(pixels, bitmap.PixelWidth, bitmap.PixelHeight, x) }).ToList();
        }
        // Retour aux coordonnées de la lecture de toute l'image.
        var back = at / scale;
        return words.Select(x => x with { X = (x0 + x.X) * back, Y = (y0 + x.Y) * back, Width = x.Width * back, Height = x.Height * back }).ToList();
    }

    /// <summary>
    /// Couleur du texte d'un mot : la moyenne de ses pixels les plus clairs (le fond du jeu est sombre ; les bords adoucis des
    /// lettres gardent la teinte). -1 si rien de clair.
    /// </summary>
    static int TextColor(byte[] bgra, int width, int height, Word w)
    {
        int x0 = Math.Max(0, (int)w.X), y0 = Math.Max(0, (int)w.Y);
        int x1 = Math.Min(width, (int)Math.Ceiling(w.X + w.Width)), y1 = Math.Min(height, (int)Math.Ceiling(w.Y + w.Height));
        var top = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * width + x) * 4;
                top = Math.Max(top, Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])));
            }
        if (top < 90) return -1;
        long r = 0, g = 0, b = 0, n = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * width + x) * 4;
                if (Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])) < top * 0.65) continue;
                b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; n++;
            }
        return n == 0 ? -1 : (int)(r / n) << 16 | (int)(g / n) << 8 | (int)(b / n);
    }
}
