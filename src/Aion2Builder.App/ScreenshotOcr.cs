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
    public static async Task<List<Word>> Words(string path)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("fr-FR")) ?? OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? throw new InvalidOperationException("Aucun moteur de reconnaissance de texte dans Windows : Paramètres → Heure et langue → Langue → ajoute le français.");
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        // Le moteur refuse les images trop grandes (écran 4K) : réduites, les positions restent proportionnelles.
        var scale = Math.Min(1.0, OcrEngine.MaxImageDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale), ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        return result.Lines.SelectMany(l => l.Words)
            .Select(w => new Word(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height)).ToList();
    }
}
