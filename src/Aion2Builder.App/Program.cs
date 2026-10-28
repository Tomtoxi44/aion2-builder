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
        // le format que lit « combat <nom> mots.tsv » du CLI.
        if (args is ["--ocr", var image, var output])
        {
            var words = ScreenshotOcr.Words(image).GetAwaiter().GetResult();
            static string N(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllLines(output, words.Select(w => $"{N(w.X)}\t{N(w.Y)}\t{N(w.Width)}\t{N(w.Height)}\t{w.Text}"));
            return;
        }
        // Essai sans fenêtre, lancé par la mise à jour avant de remplacer l'exe : 0 si cette version démarre sur ce PC.
        if (args is ["--selftest"])
        {
            Environment.ExitCode = Updater.SelfTest();
            return;
        }
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
