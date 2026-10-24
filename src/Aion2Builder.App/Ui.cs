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

    // ----- Refonte visuelle : panneaux, chiffres clés, barres, cases, sections repliables (même esprit IDE que le reste) -----

    /// <summary>Panneau : barre de titre en petites capitales (texte optionnel à droite), bordure fine, coins presque carrés.</summary>
    public static Border Card(Control content, string? title = null, Control? right = null, Thickness? padding = null)
    {
        var body = new DockPanel();
        if (title is not null)
        {
            var head = SectionHeader(title, right);
            head.Background = Tok.Chrome;
            DockPanel.SetDock(head, Dock.Top);
            body.Children.Add(head);
        }
        body.Children.Add(new Border { Padding = padding ?? new Thickness(14, 12), Child = content });
        return new Border
        {
            Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Child = body,
        };
    }

    /// <summary>Chiffre clé : libellé en petites capitales au-dessus d'une grande valeur à chasse fixe.</summary>
    public static Border Kpi(string label, string value, IBrush? color = null, double size = 22) => new()
    {
        Background = Tok.Panel, BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(14, 10),
        Child = new StackPanel { Spacing = 4, Children = { Label(label), T(value, size, color ?? Tok.Text, FontWeight.SemiBold, mono: true) } },
    };

    /// <summary>Badge : texte à chasse fixe coloré, sur fond et bordure de la même couleur.</summary>
    public static Border Badge(string text, IBrush color) => new()
    {
        Background = Tok.Alpha(color, 0.15), BorderBrush = Tok.Alpha(color, 0.3), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
        Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Child = T(text, 12, color, FontWeight.SemiBold, mono: true),
    };

    /// <summary>Barre (0 à 1) : piste sombre, remplissage coloré.</summary>
    public static Border RoundBar(double ratio, IBrush fill, double height = 8)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        var split = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(new GridLength(ratio, GridUnitType.Star)), new ColumnDefinition(new GridLength(1 - ratio, GridUnitType.Star)) },
        };
        split.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(2) });
        return new Border
        {
            Height = height, Background = Tok.HoverStrong, CornerRadius = new CornerRadius(2), ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center, Child = split,
        };
    }

    /// <summary>Ligne « nom · barre · valeur », cliquable si <paramref name="onClick"/> est donné.</summary>
    public static Control BarRow(string name, double ratio, string value, IBrush fill, double nameWidth = 170, Action? onClick = null, string? tip = null,
        bool selected = false)
    {
        var row = ColumnsSpaced($"{nameWidth},*,48", 10, T(name, 13, selected ? Accent.Main : null), RoundBar(ratio, fill),
            T(value, 12, Tok.Text2, mono: true).AlignRight());
        Control result = onClick is null ? row : Clickable(row, onClick, hover: Tok.Hover, padding: new Thickness(0, 2));
        return tip is null ? result : Tip(result, tip);
    }

    /// <summary>
    /// Cases « faits / possibles » : une case verte par fait, orange par occasion perdue, vert pâle au-delà du possible
    /// (réinitialisations). Au plus 24 cases : au-delà, chaque case vaut plusieurs lancers.
    /// </summary>
    public static Control Pips(int done, int max, double width = 14, double height = 8)
    {
        var total = Math.Max(done, max);
        var scale = total > 24 ? total / 24.0 : 1;
        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < (int)Math.Ceiling(total / scale); i++)
        {
            var at = i * scale;
            var color = at < Math.Min(done, max) ? Tok.Good : at < max ? Tok.Warn : Tok.Alpha(Tok.Good, 0.45);
            panel.Children.Add(new Border { Width = width, Height = height, CornerRadius = new CornerRadius(2), Background = color, Margin = new Thickness(0, 2, 3, 2) });
        }
        return panel;
    }

    /// <summary>Piste de frise : des points (lancers) et des bandes (pauses) placés selon le temps, sur toute la largeur.</summary>
    public sealed class TimelineTrack(double duration) : Panel
    {
        readonly List<(double Start, double End)> spans = [];

        public TimelineTrack Dot(double t, IBrush color)
        {
            spans.Add((t, t));
            Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = color });
            return this;
        }

        public TimelineTrack Span(double start, double end, IBrush color)
        {
            spans.Add((start, end));
            Children.Add(new Border { Background = color, CornerRadius = new CornerRadius(2) });
            return this;
        }

        protected override Size MeasureOverride(Size available)
        {
            foreach (var c in Children) c.Measure(new Size(8, 8));
            return new Size(double.IsInfinity(available.Width) ? 100 : available.Width, 14);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var w = Math.Max(1, final.Width - 8);
            for (var i = 0; i < Children.Count; i++)
            {
                var (start, end) = spans[i];
                var x = Math.Clamp(start / Math.Max(1, duration), 0, 1) * w;
                Children[i].Arrange(end > start
                    ? new Rect(x, 2, Math.Max(2, (end - start) / Math.Max(1, duration) * w), 10)
                    : new Rect(x, 3, 8, 8));
            }
            return final;
        }
    }

    /// <summary>Section repliable « ▸ Titre » : son contenu n'est construit qu'une fois ouverte.</summary>
    public static StackPanel Expander(string title, bool open, Action toggle, Func<Control> content, Thickness? margin = null)
    {
        var panel = new StackPanel { Spacing = 10, Margin = margin ?? default };
        panel.Children.Add(Clickable(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { T(open ? "▾" : "▸", 12, Tok.Muted, mono: true), T(title, 13, Tok.Text2) },
        }, toggle, hover: Tok.Hover, padding: new Thickness(4, 4)));
        if (open) panel.Children.Add(content());
        return panel;
    }

    public static TextBlock AlignRight(this TextBlock block)
    {
        block.HorizontalAlignment = HorizontalAlignment.Right;
        block.TextAlignment = TextAlignment.Right;
        return block;
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
