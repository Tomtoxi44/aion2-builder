using Aion2Builder.Analysis;

namespace Aion2Builder.App;

/// <summary>
/// Lecture d'une capture de la fenêtre d'artisanat : deux passages de l'OCR de Windows (image normale, puis éclaircie pour les
/// quantités en rouge), réunis. La capture est lue sur le PC, rien n'est envoyé.
/// </summary>
static class CraftShots
{
    public static async Task<(CraftShot? Shot, string? Problem)> ReadAsync(string path, double scale = 2)
    {
        var plain = CraftShotReader.Read(await ScreenshotOcr.Words(path, InGameStatsReader.MinOcrSize, minScale: scale, colors: true), out var problem);
        var lit = CraftShotReader.Read(await ScreenshotOcr.Words(path, InGameStatsReader.MinOcrSize, minScale: scale, colors: true, brighten: true), out var problem2);
        var shot = CraftShotReader.Merge(plain, lit);
        return (shot, shot is null ? problem ?? problem2 : null);
    }
}
