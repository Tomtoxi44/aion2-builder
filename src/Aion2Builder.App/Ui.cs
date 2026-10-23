using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Aion2Builder.App;

/// <summary>Petits constructeurs de contrôles pour écrire l'interface en C# (pas de .axaml).</summary>
static class Ui
{
    /// <summary>Couleur secondaire en cours (menu « thème »).</summary>
    public static AccentTheme Accent { get; set; } = AccentTheme.All[0];

    static readonly Cursor Hand = new(StandardCursorType.Hand);

    /// <summary>Indicateur de chargement : arc qui tourne tant qu'il est affiché.</summary>
    public static Control Spinner(double size, IBrush color)
    {
        var rotate = new RotateTransform();
        var arc = new Avalonia.Controls.Shapes.Arc
        {
            Width = size, Height = size, StartAngle = 0, SweepAngle = 270, Stroke = color, StrokeThickness = Math.Max(1.5, size / 7),
            StrokeLineCap = PenLineCap.Round, RenderTransform = rotate, VerticalAlignment = VerticalAlignment.Center,
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => rotate.Angle = (rotate.Angle + 8) % 360;
        arc.AttachedToVisualTree += (_, _) => timer.Start();
        arc.DetachedFromVisualTree += (_, _) => timer.Stop();
        return arc;
    }

    public static TextBlock T(string text, double size = 13, IBrush? color = null, FontWeight weight = FontWeight.Normal,
        bool mono = false, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (mono) block.FontFamily = Fonts.Mono;
        // Ne pas écrire Foreground = null : cela masquerait la couleur héritée.
        if (color is not null) block.Foreground = color;
        return block;
    }

    /// <summary>Libellé de section : 11 px, majuscules, interlettrage 0,08 em.</summary>
    public static TextBlock Label(string text, IBrush? color = null) =>
        new() { Text = text.ToUpperInvariant(), FontSize = 11, LetterSpacing = 0.88, Foreground = color ?? Tok.Muted, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>En-tête de section commun au contenu (padX 20) et à l'inspecteur (padX 14).</summary>
    public static Border SectionHeader(string text, Control? right = null, double padX = 14, bool topBorder = false)
    {
        var dock = new DockPanel();
        if (right is not null)
        {
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);
        }
        dock.Children.Add(Label(text));
        return new Border
        {
            Padding = new Thickness(padX, 8),
            BorderBrush = Tok.Border,
            BorderThickness = new Thickness(0, topBorder ? 1 : 0, 0, 1),
            Child = dock,
        };
    }

    /// <summary>Liste clé / valeur en police à chasse fixe, colonnes 110 px | reste.</summary>
    public static Grid KeyValue(IEnumerable<(string Key, string Value, IBrush? Color)> rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), ColumnSpacing = 10, RowSpacing = 6 };
        var r = 0;
        foreach (var (key, value, color) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = T(key, 12, Tok.Muted, mono: true);
            var v = T(value, 12, color, mono: true, wrap: true);
            Grid.SetRow(k, r);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            grid.Children.Add(k);
            grid.Children.Add(v);
            r++;
        }
        return grid;
    }

    /// <summary>Bloc « libellé en majuscules + texte » de l'inspecteur.</summary>
    public static StackPanel Field(string label, string text, IBrush? color = null) =>
        new() { Spacing = 5, Children = { Label(label), T(text, 13, color, wrap: true) } };

    /// <summary>Ligne de grille à colonnes fixes (ex. "150,100,90,80,80,*"), espacement 12 par défaut.</summary>
    public static Grid Columns(string definitions, params Control[] cells) => ColumnsSpaced(definitions, 12, cells);

    public static Grid ColumnsSpaced(string definitions, double spacing, params Control[] cells)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(definitions), ColumnSpacing = spacing };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    /// <summary>Zone cliquable avec survol : fond normal, fond survolé, action au clic gauche.</summary>
    public static Border Clickable(Control child, Action onClick, IBrush? background = null, IBrush? hover = null, Thickness? padding = null)
    {
        var normal = background ?? Brushes.Transparent;
        var border = new Border { Background = normal, Child = child, Cursor = Hand, Padding = padding ?? default };
        if (hover is not null)
        {
            border.PointerEntered += (_, _) => border.Background = hover;
            border.PointerExited += (_, _) => border.Background = normal;
        }
        border.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            onClick();
        };
        return border;
    }

    /// <summary>Barre de progression plate ; repère optionnel (trait clair 2 × 14) à la cible.</summary>
    public static Control Bar(double ratio, IBrush fill, double height, double? marker = null, IBrush? track = null)
    {
        static Grid Split(double r, Control? left)
        {
            r = Math.Clamp(r, 0, 1);
            var g = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(r, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(1 - r, GridUnitType.Star)),
                },
            };
            if (left is not null) g.Children.Add(left);
            return g;
        }

        var bar = new Panel { Height = height, Background = track ?? Tok.HoverStrong, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(Split(ratio, new Border { Background = fill }));
        if (marker is null) return bar;

        var holder = new Panel { Height = 14 };
        holder.Children.Add(bar);
        var m = Math.Clamp(marker.Value, 0, 1);
        var line = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(m, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(2)),
                new ColumnDefinition(new GridLength(1 - m, GridUnitType.Star)),
            },
        };
        var tick = new Border { Background = Tok.Text, Width = 2, Height = 14 };
        Grid.SetColumn(tick, 1);
        line.Children.Add(tick);
        holder.Children.Add(line);
        return holder;
    }

    /// <summary>Compteur « − N + » : chaque clic appelle <paramref name="onChange"/> tout de suite.</summary>
    public static Border Stepper(int value, Action<int> onChange, IBrush? background = null)
    {
        Border Button(string text, int delta) => Clickable(T(text, 12, Accent.Main, mono: true),
            () => onChange(Math.Max(0, value + delta)), hover: Tok.HoverStrong, padding: new Thickness(9, 3));
        return new Border
        {
            BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Background = background ?? Tok.Panel, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    Button("−", -1),
                    new Border { MinWidth = 24, Child = T(value.ToString(), 12, Tok.Text, FontWeight.SemiBold, mono: true).Centered() },
                    Button("+", 1),
                },
            },
        };
    }

    /// <summary>Pastille de conseil : texte coloré sur fond de la même couleur à 15 %.</summary>
    public static Border Pill(string text, IBrush color) => new()
    {
        Background = Tok.Alpha(color, 0.15), CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 1),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Child = T(text, 12, color, FontWeight.SemiBold, mono: true),
    };

    /// <summary>Bloc repliable « + Titre » : le texte n'apparaît qu'une fois ouvert.</summary>
    public static StackPanel Collapsible(string title, string text, bool open, Action toggle, Thickness padding)
    {
        var head = Clickable(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { T(open ? "−" : "+", 12, Accent.Main, mono: true), T(title, 12, Accent.Main) },
        }, toggle);
        var panel = new StackPanel { Spacing = 8, Margin = padding, Children = { head } };
        if (open) panel.Children.Add(T(text, 12, Tok.Muted, wrap: true).WithLineHeight(1.55));
        return panel;
    }

    public static T Tip<T>(T control, string text) where T : Control
    {
        ToolTip.SetTip(control, text);
        return control;
    }

    public static TextBlock Centered(this TextBlock block)
    {
        block.HorizontalAlignment = HorizontalAlignment.Center;
        block.TextAlignment = TextAlignment.Center;
        return block;
    }

    public static TextBlock WithLineHeight(this TextBlock block, double factor)
    {
        block.LineHeight = block.FontSize * factor;
        return block;
    }

    public static ScrollViewer Scroll(Control content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    public static IBrush Brush(string hex) => Tok.B(hex);
}
