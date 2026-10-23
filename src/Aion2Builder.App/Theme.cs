using Avalonia.Media;

namespace Aion2Builder.App;

/// <summary>Jetons de couleur de la refonte « IDE » (voir design_handoff_ide_redesign).</summary>
static class Tok
{
    // Fond et surfaces
    public static readonly IBrush Window = B("#151619");
    public static readonly IBrush Panel = B("#1B1C20");
    public static readonly IBrush Chrome = B("#1D1E22");
    public static readonly IBrush Frame = B("#18191C");
    public static readonly IBrush Hover = B("#1E1F23");
    public static readonly IBrush HoverStrong = B("#24262B");
    public static readonly IBrush TileHover = B("#22242A");
    public static readonly IBrush Border = B("#2A2C31");
    public static readonly IBrush RowLine = B("#1F2024");
    public static readonly IBrush RowLine2 = B("#24262B");
    public static readonly IBrush CellOff = B("#1F2024");
    public static readonly IBrush Dashed = B("#2E3036");

    // Texte
    public static readonly IBrush Text = B("#D4D7DD");
    public static readonly IBrush Text2 = B("#A3A8B1");
    public static readonly IBrush Muted = B("#858A93");
    public static readonly IBrush Faint = B("#565A62");
    public static readonly IBrush OnAccent = B("#0E0F11");
    public static readonly IBrush StatusText = B("#FFF6EE");

    // Sémantique
    public static readonly IBrush Good = B("#46A758");
    public static readonly IBrush Warn = B("#F5A524");
    public static readonly IBrush Bad = B("#E5484D");
    public static readonly IBrush Info = B("#3FA7D6");

    public static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>Même couleur avec une opacité (fonds de pastilles, bordures de cases fermées).</summary>
    public static IBrush Alpha(IBrush brush, double alpha) =>
        brush is ISolidColorBrush s ? new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), s.Color.R, s.Color.G, s.Color.B)) : brush;

    public static IBrush Score(int score) => score >= 75 ? Good : score >= 50 ? Warn : Bad;
}

/// <summary>Couleur secondaire choisie dans le menu « thème » : main (accents), bar (barre d'état), dim (sélection).</summary>
sealed record AccentTheme(string Name, string MainHex, string BarHex)
{
    public IBrush Main { get; } = Tok.B(MainHex);
    public IBrush Bar { get; } = Tok.B(BarHex);
    public IBrush Dim => Tok.Alpha(Main, 0.13);

    public static readonly AccentTheme[] All =
    [
        new("Orange", "#E8833A", "#8A4718"),
        new("Vert", "#3DBE7A", "#1C6440"),
        new("Bleu", "#4C9AFF", "#2456A0"),
        new("Violet", "#A57CF0", "#5A3B9A"),
        new("Rose", "#F06BA8", "#963A66"),
        new("Cyan", "#33C9D6", "#1A6E76"),
        new("Jaune", "#E6C04A", "#7E6618"),
        new("Rouge", "#EF5B5B", "#8C2C2C"),
    ];

    public static AccentTheme Find(string? name) => All.FirstOrDefault(a => a.Name == name) ?? All[0];
}

/// <summary>
/// Polices : la maquette utilise IBM Plex Sans et JetBrains Mono. L'interface embarque Inter ;
/// pour les chiffres, JetBrains Mono si elle est installée, sinon Cascadia Mono ou Consolas (Windows).
/// </summary>
static class Fonts
{
    public static readonly FontFamily Sans = new("fonts:Inter#Inter");
    public static readonly FontFamily Mono = new("JetBrains Mono, Cascadia Mono, Consolas");
}
