using Aion2Builder.Analysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Aion2Builder.App;

/// <summary>Petits constructeurs de contrôles pour écrire l'interface en C#.</summary>
static class Ui
{
    public static readonly IBrush Muted = Brush("#8B93A7");
    public static readonly IBrush CardBackground = Brush("#1AFFFFFF");
    public static readonly IBrush Good = Brush("#46A758");
    public static readonly IBrush Bad = Brush("#E5484D");
    public static readonly IBrush Warn = Brush("#F5A524");
    public static readonly IBrush Accent = Brush("#3FA7D6");

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static IBrush SeverityBrush(Severity s) => s switch
    {
        Analysis.Severity.Critical => Bad,
        Analysis.Severity.Important => Warn,
        Analysis.Severity.Advice => Accent,
        _ => Muted,
    };

    public static IBrush StatusBrush(SlotStatus s) => s switch
    {
        SlotStatus.Empty => Bad,
        SlotStatus.Replace or SlotStatus.Weak => Warn,
        SlotStatus.Enchant => Accent,
        _ => Good,
    };

    public static IBrush ScoreBrush(int score) => score >= 75 ? Good : score >= 50 ? Warn : Bad;

    public static TextBlock Text(string text, double size = 13, bool bold = false, IBrush? color = null, bool wrap = true)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Ne pas écrire Foreground = null : cela masquerait la couleur héritée du thème.
        if (color is not null) block.Foreground = color;
        return block;
    }

    public static Border Card(Control child, IBrush? accent = null, double bottom = 8) => new()
    {
        Background = CardBackground,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 10),
        Margin = new Thickness(0, 0, 0, bottom),
        BorderBrush = accent,
        BorderThickness = accent is null ? new Thickness(0) : new Thickness(4, 0, 0, 0),
        Child = child,
    };

    public static StackPanel Stack(double spacing = 4, params Control[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    public static StackPanel Row(double spacing = 8, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    /// <summary>Pastille colorée (score, statut, rareté).</summary>
    public static Border Chip(string text, IBrush color) => new()
    {
        Background = color,
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brushes.Black },
    };

    public static ScrollViewer Scroll(Control content) => new()
    {
        Content = new Border { Padding = new Thickness(16, 12), Child = content },
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    /// <summary>Barre de répartition avec repère de cible.</summary>
    public static Control ShareBar(double share, double target, IBrush color)
    {
        const double width = 260;
        var canvas = new Canvas { Width = width, Height = 14 };
        canvas.Children.Add(new Border { Width = width, Height = 14, CornerRadius = new CornerRadius(4), Background = CardBackground });
        canvas.Children.Add(new Border { Width = Math.Clamp(share, 0, 1) * width, Height = 14, CornerRadius = new CornerRadius(4), Background = color });
        var marker = new Border { Width = 2, Height = 18, Background = Brushes.White };
        Canvas.SetLeft(marker, Math.Clamp(target, 0, 1) * width - 1);
        Canvas.SetTop(marker, -2);
        canvas.Children.Add(marker);
        return canvas;
    }

    /// <summary>Tableau simple : colonnes de largeur fixe, une ligne par entrée.</summary>
    public static Grid Table(string columns, IEnumerable<Control[]> rows, string[]? headers = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        var r = 0;
        void Add(Control[] cells, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < cells.Length; c++)
            {
                var cell = cells[c];
                cell.Margin = new Thickness(0, header ? 0 : 3, 12, header ? 6 : 3);
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
            r++;
        }
        if (headers is not null) Add(headers.Select(h => (Control)Text(h, 12, true, Muted, wrap: false)).ToArray(), true);
        foreach (var row in rows) Add(row, false);
        return grid;
    }
}
