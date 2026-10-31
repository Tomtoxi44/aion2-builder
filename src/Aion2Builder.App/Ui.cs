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
        new() { Text = text.ToUpperInvariant(), FontSize = 11, LetterSpacing = 0.88, Foreground = color ?? Tok.Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

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

    /// <summary>
    /// Compteur « − N + » : clic ±1, Maj+clic ±10, molette ±1 (Maj : ±10) ; clic sur le nombre pour le taper (Entrée ou clic
    /// ailleurs valide, Échap annule). Chaque changement appelle <paramref name="onChange"/> tout de suite.
    /// </summary>
    const string AzertyDigits = "&é\"'(-è_çà";

    public static Border Stepper(int value, Action<int> onChange, IBrush? background = null)
    {
        void Change(int v)
        {
            v = Math.Max(0, v);
            if (v != value) onChange(v);
        }
        static int Step(KeyModifiers keys) => keys.HasFlag(KeyModifiers.Shift) ? 10 : 1;

        Border Button(string text, int sign)
        {
            var button = new Border { Background = Brushes.Transparent, Child = T(text, 12, Accent.Main, mono: true), Cursor = Hand, Padding = new Thickness(9, 3) };
            button.PointerEntered += (_, _) => button.Background = Tok.HoverStrong;
            button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
            button.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                Change(value + sign * Step(e.KeyModifiers));
            };
            return button;
        }

        // Le nombre : un clic le remplace par une zone de saisie.
        var number = new Border { MinWidth = 24, Cursor = new Cursor(StandardCursorType.Ibeam), Background = Brushes.Transparent };
        number.Child = T(value.ToString(), 12, Tok.Text, FontWeight.SemiBold, mono: true).Centered();
        number.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(number).Properties.IsLeftButtonPressed || number.Child is TextBox) return;
            e.Handled = true;
            var box = new TextBox
            {
                Text = value.ToString(), MinWidth = 52, FontSize = 12, FontFamily = Fonts.Mono, Padding = new Thickness(4, 1),
                MinHeight = 0, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            var done = false;
            void Finish(bool apply)
            {
                if (done) return;
                done = true;
                // Clavier AZERTY : les touches du haut sans Maj (& é " ' ( - è _ ç à) valent 1 à 0 ; le reste est ignoré.
                var digits = new string((box.Text ?? "").Select(ch => AzertyDigits.IndexOf(ch) is var i and >= 0 ? (char)('0' + (i + 1) % 10) : ch)
                    .Where(char.IsAsciiDigit).ToArray());
                if (apply && int.TryParse(digits, out var typed) && typed != value) onChange(typed);
                else number.Child = T(value.ToString(), 12, Tok.Text, FontWeight.SemiBold, mono: true).Centered();
            }
            box.KeyDown += (_, k) =>
            {
                if (k.Key == Key.Enter) { k.Handled = true; Finish(true); }
                else if (k.Key == Key.Escape) { k.Handled = true; Finish(false); }
            };
            box.LostFocus += (_, _) => Finish(true);
            number.Child = box;
            Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
        };

        var stepper = new Border
        {
            BorderBrush = Tok.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Background = background ?? Tok.Panel, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { Button("−", -1), number, Button("+", 1) } },
        };
        // Molette sur le compteur : ±1 par cran (Maj : ±10) ; la page ne défile pas pendant ce temps. Maj+molette peut
        // arriver en défilement horizontal : on prend l'axe qui bouge.
        stepper.PointerWheelChanged += (_, e) =>
        {
            var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            if (delta == 0 || number.Child is TextBox) return;
            e.Handled = true;
            Change(value + Math.Sign(delta) * Step(e.KeyModifiers));
        };
        ToolTip.SetTip(stepper, "Clic : ±1 · Maj+clic : ±10 · molette : ±1 (Maj : ±10) · clic sur le nombre pour le taper");
        return stepper;
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

    /// <summary>Ligne « [icône] nom · barre · valeur », cliquable si <paramref name="onClick"/> est donné.</summary>
    public static Control BarRow(string name, double ratio, string value, IBrush fill, double nameWidth = 170, Action? onClick = null, string? tip = null,
        bool selected = false, Control? icon = null)
    {
        Control label = T(name, 13, selected ? Accent.Main : null);
        if (icon is not null)
        {
            icon.Margin = new Thickness(0, 0, 7, 0);
            DockPanel.SetDock(icon, Dock.Left);
            label = new DockPanel { Children = { icon, label } };
        }
        var row = ColumnsSpaced($"{nameWidth},*,48", 10, label, RoundBar(ratio, fill),
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

    /// <summary>
    /// Piste de paliers : barre de <c>min</c> à <c>max</c> remplie jusqu'à la valeur, un trait et un libellé par palier,
    /// libellés alternés au-dessus et au-dessous pour que deux paliers proches ne se chevauchent pas.
    /// </summary>
    public sealed class MilestoneTrack : Panel
    {
        readonly double value, min, max;
        readonly List<double> marks = [];

        public MilestoneTrack(double value, double min, double max, IBrush fill)
        {
            (this.value, this.min, this.max) = (value, min, max);
            Children.Add(new Border { Background = Tok.HoverStrong, CornerRadius = new CornerRadius(2) });
            Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(2) });
        }

        public MilestoneTrack Mark(double at, string label, IBrush color)
        {
            marks.Add(at);
            Children.Add(new Border { Background = color });
            Children.Add(new TextBlock { Text = label, FontFamily = Fonts.Mono, FontSize = 11, Foreground = color });
            return this;
        }

        double X(double v, double width) => Math.Clamp((v - min) / Math.Max(1, max - min), 0, 1) * width;

        protected override Size MeasureOverride(Size available)
        {
            foreach (var c in Children) c.Measure(Size.Infinity);
            return new Size(double.IsInfinity(available.Width) ? 300 : available.Width, 48);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var w = final.Width;
            Children[0].Arrange(new Rect(0, 19, w, 8));
            Children[1].Arrange(new Rect(0, 19, X(value, w), 8));
            for (var i = 0; i < marks.Count; i++)
            {
                var x = X(marks[i], w);
                var above = i % 2 == 0;
                Children[2 + i * 2].Arrange(new Rect(Math.Clamp(x - 1, 0, Math.Max(0, w - 2)), above ? 14 : 19, 2, 13));
                var label = Children[3 + i * 2];
                var lw = label.DesiredSize.Width;
                label.Arrange(new Rect(Math.Clamp(x - lw / 2, 0, Math.Max(0, w - lw)), above ? 0 : 33, lw, label.DesiredSize.Height));
            }
            return final;
        }
    }

    /// <summary>
    /// Barres verticales sur toute la largeur, une par valeur (une seconde de combat), hauteur selon la plus grande ;
    /// une valeur nulle laisse un trou : on voit d'un coup d'œil quand on frappait et quand non.
    /// </summary>
    public sealed class Histogram : Panel
    {
        readonly IReadOnlyList<double> values;
        readonly double peak;

        public Histogram(IReadOnlyList<double> values, IBrush fill)
        {
            this.values = values;
            peak = values.Count > 0 ? Math.Max(1e-9, values.Max()) : 1;
            Children.Add(new Border { Background = Tok.Border });  // ligne de base
            foreach (var _ in values) Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(1, 1, 0, 0) });
        }

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 300 : available.Width, double.IsNaN(Height) ? 80 : Height);

        protected override Size ArrangeOverride(Size final)
        {
            var w = final.Width / Math.Max(1, values.Count);
            var gap = w >= 4 ? 1 : 0;
            Children[0].Arrange(new Rect(0, final.Height - 1, final.Width, 1));
            for (var i = 0; i < values.Count; i++)
            {
                var h = values[i] / peak * (final.Height - 2);
                Children[i + 1].Arrange(new Rect(i * w, final.Height - 1 - h, Math.Max(1, w - gap), h));
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

    static readonly Cursor Help = new(StandardCursorType.Help);

    /// <summary>Appelé à la première infobulle ouverte (l'astuce de la barre d'état n'est plus utile).</summary>
    public static Action? TipShown { get; set; }

    /// <summary>
    /// Une infobulle, et qu'on sache qu'elle existe : un texte est souligné de pointillés, et le curseur devient « ? » sur
    /// tout ce qui n'est pas déjà cliquable (une ligne cliquable garde sa main).
    /// </summary>
    public static T Tip<T>(T control, string text) where T : Control
    {
        ToolTip.SetTip(control, text);
        ToolTip.SetShowDelay(control, 350);
        if (control is TextBlock block) block.TextDecorations = Dotted;
        if (control.Cursor is null) control.Cursor = Help;
        control.AddHandler(ToolTip.ToolTipOpeningEvent, (_, _) => TipShown?.Invoke());
        return control;
    }

    /// <summary>Soulignement en pointillés discrets : « survole-moi ».</summary>
    static TextDecorationCollection Dotted => [new TextDecoration
    {
        Location = TextDecorationLocation.Underline, Stroke = Tok.Muted, StrokeThickness = 1,
        StrokeDashArray = [1.5, 2.5], StrokeOffset = 2, StrokeOffsetUnit = TextDecorationUnit.Pixel,
    }];

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
