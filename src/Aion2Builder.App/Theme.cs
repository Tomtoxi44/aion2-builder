using Avalonia.Media;

namespace Aion2Builder.App;

/// <summary>
/// Jetons de couleur de la refonte « IDE » (voir design_handoff_ide_redesign). La présentation « Confort » a les siens
/// (« sombre adouci » : un peu plus clair, texte plus contrasté) : <see cref="Apply"/> les échange, et l'affichage suivant
/// redessine tout avec eux.
/// </summary>
static class Tok
{
    // Fond et surfaces
    public static IBrush Window { get; private set; } = B("#151619");
    public static IBrush Panel { get; private set; } = B("#1B1C20");
    public static IBrush Chrome { get; private set; } = B("#1D1E22");
    public static IBrush Frame { get; private set; } = B("#18191C");
    public static IBrush Hover { get; private set; } = B("#1E1F23");
    public static IBrush HoverStrong { get; private set; } = B("#24262B");
    public static IBrush TileHover { get; private set; } = B("#22242A");
    public static IBrush Border { get; private set; } = B("#2A2C31");
    public static IBrush RowLine { get; private set; } = B("#1F2024");
    public static IBrush RowLine2 { get; private set; } = B("#24262B");
    public static IBrush CellOff { get; private set; } = B("#1F2024");
    public static IBrush Dashed { get; private set; } = B("#2E3036");

    // Texte
    public static IBrush Text { get; private set; } = B("#D4D7DD");
    public static IBrush Text2 { get; private set; } = B("#A3A8B1");
    public static IBrush Muted { get; private set; } = B("#858A93");
    public static IBrush Faint { get; private set; } = B("#565A62");
    public static IBrush OnAccent { get; private set; } = B("#0E0F11");
    public static IBrush StatusText { get; private set; } = B("#FFF6EE");

    // Sémantique
    public static IBrush Good { get; private set; } = B("#46A758");
    public static IBrush Warn { get; private set; } = B("#F5A524");
    public static IBrush Bad { get; private set; } = B("#E5484D");
    public static IBrush Info { get; private set; } = B("#3FA7D6");

    /// <summary>Les couleurs de la présentation : IDE (celles d'origine) ou « sombre adouci » de la présentation Confort.</summary>
    public static void Apply(bool soft)
    {
        Window = B(soft ? "#1A1C21" : "#151619");
        Panel = B(soft ? "#24272E" : "#1B1C20");
        Chrome = B(soft ? "#1F2228" : "#1D1E22");
        Frame = B(soft ? "#1D2026" : "#18191C");
        Hover = B(soft ? "#262A32" : "#1E1F23");
        HoverStrong = B(soft ? "#2F343E" : "#24262B");
        TileHover = B(soft ? "#2C3039" : "#22242A");
        Border = B(soft ? "#383D49" : "#2A2C31");
        RowLine = B(soft ? "#2B2F38" : "#1F2024");
        RowLine2 = B(soft ? "#323743" : "#24262B");
        CellOff = B(soft ? "#2A2E36" : "#1F2024");
        Dashed = B(soft ? "#3C4250" : "#2E3036");
        Text = B(soft ? "#F1F3F7" : "#D4D7DD");
        Text2 = B(soft ? "#C8CCD5" : "#A3A8B1");
        Muted = B(soft ? "#9CA2AE" : "#858A93");
        Faint = B(soft ? "#6E7481" : "#565A62");
        OnAccent = B(soft ? "#17191D" : "#0E0F11");
        StatusText = B(soft ? "#C8CCD5" : "#FFF6EE");
        Good = B(soft ? "#62C674" : "#46A758");
        Warn = B(soft ? "#F2B544" : "#F5A524");
        Bad = B(soft ? "#F26B73" : "#E5484D");
        Info = B(soft ? "#5BB8E4" : "#3FA7D6");
    }

    public static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>Même couleur avec une opacité (fonds de pastilles, bordures de cases fermées).</summary>
    public static IBrush Alpha(IBrush brush, double alpha) =>
        brush is ISolidColorBrush s ? new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), s.Color.R, s.Color.G, s.Color.B)) : brush;

    public static IBrush Score(int score) => score >= 75 ? Good : score >= 50 ? Warn : Bad;
}

/// <summary>
/// La présentation en cours. IDE (par défaut) : rien ne change. Confort : couleurs adoucies, coins arrondis, titres en
/// minuscules, et sur la première page d'un écran l'essentiel d'abord (une phrase, trois choses à faire, le détail replié).
/// </summary>
static class Skin
{
    public static bool Comfort { get; private set; }

    /// <summary>Rayon des coins des panneaux.</summary>
    public static double Radius => Comfort ? 9 : 4;

    public static void Use(bool comfort)
    {
        if (Comfort == comfort) return;
        Comfort = comfort;
        Tok.Apply(comfort);
    }
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
