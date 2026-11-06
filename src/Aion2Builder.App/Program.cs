using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Aion2Builder.App;

sealed class Program
{
    // N'utiliser aucune API Avalonia avant AppMain : rien n'est encore initialisé.
    [STAThread]
    public static void Main(string[] args)
    {
        // Diagnostic sans fenêtre : « --ocr capture.png mots.tsv » écrit les mots reconnus (x, y, largeur, hauteur, texte),
        // le format que lit « combat <nom> mots.tsv » du CLI ; « --ocr capture.png mots.tsv 2000 » agrandit d'abord une petite
        // capture (fenêtre Stats du jeu, lue par « stats-jeu <nom> mots.tsv »).
        if (args is ["--ocr", var image, var output, ..] && args.Length <= 4)
        {
            var words = ScreenshotOcr.Words(image, args.Length == 4 && int.TryParse(args[3], out var min) ? min : 0, colors: true).GetAwaiter().GetResult();
            static string N(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // 6e colonne : couleur du texte (#RRGGBB) et le rang du Marché qu'elle donne.
            File.WriteAllLines(output, words.Select(w => $"{N(w.X)}\t{N(w.Y)}\t{N(w.Width)}\t{N(w.Height)}\t{w.Text}\t" +
                (w.Rgb < 0 ? "" : $"#{w.Rgb:X6} {Aion2Builder.Analysis.MarketShotReader.GradeOf(w.Rgb)}")));
            return;
        }
        // Relevé HdV du Pet Meter, sans fenêtre : lire une capture, puis noter les lignes lues (voir HdvRelay).
        if (args is ["--hdv-read", var shot, var read])
        {
            HdvRelay.Read(shot, read);
            return;
        }
        if (args is ["--hdv-send", var lines, var sent])
        {
            HdvRelay.Send(lines, sent);
            return;
        }
        // Essai sans fenêtre, lancé par la mise à jour avant de remplacer l'exe : 0 si cette version démarre sur ce PC.
        if (args is ["--selftest"])
        {
            Environment.ExitCode = Updater.SelfTest();
            return;
        }
        // Données et analyse du dernier perso lues sur un autre fil pendant qu'Avalonia démarre (voir Startup).
        Startup.Begin();
        HdvRelay.NoteExe();  // le Pet Meter lance ce Builder pour lire ses relevés HdV
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // Thème sombre : les couleurs de rareté du jeu (blanc, vert, bleu, jaune…) y sont lisibles.
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
