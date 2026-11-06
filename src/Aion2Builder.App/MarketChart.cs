using System.Globalization;
using Aion2Builder.Analysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>Une courbe du graphique HdV : nom de l'objet, couleur de sa case, relevés (date UTC, prix unitaire).</summary>
sealed record PriceSeries(string Name, IBrush Color, IReadOnlyList<(DateTime At, long Price)> Points);

sealed partial class ScreenViews
{
    /// <summary>
    /// Couleurs des courbes, dans cet ordre (palette catégorielle validée pour fond sombre : daltonisme, contraste). Une couleur
    /// suit son objet : elle ne change pas quand on en retire un autre. Le rang du jeu reste sur la barre à gauche du nom.
    /// </summary>
    static readonly IBrush[] SeriesColors =
        new[] { "#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767" }.Select(Tok.B).ToArray();

    /// <summary>Les objets qui ont un prix sur ce marché, rangés comme l'hôtel du jeu : la récolte par famille, du blanc au jaune, puis le reste.</summary>
    List<(string Key, MarketItem? Item, List<MarketPrice> List)> MarketRows(MarketData data, List<MarketPrice> here)
    {
        var items = data.Items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        // Famille d'une ressource : son numéro sans le dernier chiffre (Aria, Aria fraîche, pure, éclatante), nommée par la plus commune.
        var family = data.Items.Where(i => i.IsGathering && long.TryParse(i.Key, out _)).GroupBy(i => long.Parse(i.Key) / 10)
            .ToDictionary(g => g.Key, g => g.MinBy(i => i.Rank)!.Name);
        string SortName((string Key, MarketItem? Item, List<MarketPrice> List) x) =>
            x.Item?.IsGathering == true && long.TryParse(x.Key, out var id) && family.TryGetValue(id / 10, out var f) ? f : x.List[^1].ItemName;
        int GradeRank(MarketItem? i) => i?.GameGrade is { } g ? db.Grade(g).Rank : 0;
        return here.GroupBy(p => p.ItemKey).Select(g => (Key: g.Key, Item: items.GetValueOrDefault(g.Key), List: g.OrderBy(p => p.At).ToList()))
            .OrderByDescending(x => x.Item?.IsGathering == true).ThenBy(SortName, French).ThenBy(x => GradeRank(x.Item)).ThenBy(x => x.List[^1].ItemName, French).ToList();
    }

    /// <summary>
    /// Prix HdV en graphique : les objets cochés à droite (8 au plus), une courbe chacun ; la période (7, 30, 90 jours, tout) et
    /// la mesure (prix en Kinah, ou évolution en % depuis le début de la période, pour comparer des objets de prix très
    /// différents) ; sous le graphique, la légende avec les chiffres de chaque objet.
    /// </summary>
    IEnumerable<Control> MarketChartBlocks(MarketData data, string shown, List<(string Key, MarketItem? Item, List<MarketPrice> List)> rows)
    {
        var now = DateTime.UtcNow;
        var days = s.MarketDays;
        var from = days > 0 ? now.AddDays(-days) : rows.Count == 0 ? now.AddDays(-30) : rows.Min(x => x.List[0].At.ToUniversalTime());
        var periods = new[] { (7, "7 jours"), (30, "30 jours"), (90, "90 jours"), (0, "tout") };
        yield return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children =
            {
                T("Période", 13, weight: FontWeight.Medium).WithMargin(0, 4, 0, 0),
                Choice(periods.Select(p => (p.Item2, days == p.Item1, (Action)(() => update(st => st.MarketDays = p.Item1))))),
                T("Afficher", 13, weight: FontWeight.Medium).WithMargin(14, 4, 0, 0),
                Choice([("prix en Kinah", !s.MarketPercent, () => update(st => st.MarketPercent = false)),
                        ("évolution %", s.MarketPercent, () => update(st => st.MarketPercent = true))]),
            },
        };

        // Les courbes, dans l'ordre des cases (une couleur par case).
        var series = new List<PriceSeries>();
        var legend = new StackPanel();
        const string cols = "14,*,100,110,70,26";
        legend.Children.Add(TableHeader(cols, "", "Objet", "Dernier prix", "Sur la période", "Relevés", ""));
        var lasts = new List<long>();
        for (var slot = 0; slot < s.MarketSeries.Length; slot++)
        {
            if (s.MarketSeries[slot] is not { } key) continue;
            var color = SeriesColors[slot];
            var index = slot;
            var row = rows.FirstOrDefault(x => x.Key == key);
            var item = row.Item ?? data.Items.FirstOrDefault(i => i.Key == key);
            var name = row.List?[^1].ItemName ?? item?.Name ?? key;
            var swatch = new Border { Width = 12, Height = 3, CornerRadius = new CornerRadius(1.5), Background = color, VerticalAlignment = VerticalAlignment.Center };
            var remove = Clickable(T("✕", 11, Tok.Muted), () => update(st => st.MarketSeries[index] = null), null, Tok.Hover, new Thickness(6, 2));
            remove.CornerRadius = new CornerRadius(3);
            Control last, change, count;
            if (row.List is not { Count: > 0 } list)
            {
                last = T("aucun prix sur ce marché", 11, Tok.Muted);
                change = T("", 11);
                count = T("", 11);
            }
            else
            {
                series.Add(new PriceSeries(name, color, list.Select(p => (p.At.ToUniversalTime(), p.Price)).ToList()));
                lasts.Add(list[^1].Price);
                // Sur la période : du prix connu à son début (dernier relevé d'avant, sinon le premier dedans) au dernier.
                var start = list.LastOrDefault(p => p.At.ToUniversalTime() <= from) ?? list.FirstOrDefault(p => p.At.ToUniversalTime() > from);
                var inside = list.Count(p => p.At.ToUniversalTime() > from);
                last = Tip(T(Fmt.Big(list[^1].Price), 13, Tok.Text, FontWeight.SemiBold, mono: true).AlignRight(),
                    $"Dernier relevé le {list[^1].At.ToLocalTime():dddd dd/MM à HH:mm}, par {By(list[^1])}");
                if (start is null || start.Price == 0 || inside == 0)
                    change = T("=", 12, Tok.Muted, mono: true).AlignRight();
                else
                {
                    var d = list[^1].Price - start.Price;
                    var pct = d * 100.0 / start.Price;
                    change = Tip(T(d == 0 ? "=" : $"{(d > 0 ? "+" : "−")}{Math.Abs(pct):0.#} %", 12, d > 0 ? Tok.Good : d < 0 ? Tok.Bad : Tok.Muted, mono: true).AlignRight(),
                        $"{Fmt.Big(start.Price)} → {Fmt.Big(list[^1].Price)} Kinah ({(d >= 0 ? "+" : "−")}{Fmt.Big(Math.Abs(d))}) sur la période");
                }
                count = T(inside.ToString(), 12, Tok.Text2, mono: true).AlignRight();
            }
            legend.Children.Add(new Border
            {
                Padding = new Thickness(20, 4),
                Child = Columns(cols, swatch, MarketItemLine(item, name), last, change, count, Tip(remove, "Retirer cette courbe")),
            });
        }

        var body = new StackPanel { Spacing = 10 };
        if (series.Count == 0)
            body.Children.Add(T(s.MarketSeries.Any(k => k is not null)
                ? "Les objets choisis n'ont pas de prix sur ce marché : choisis-en d'autres à droite, ou un autre marché."
                : "Choisis des objets dans la liste à droite : une courbe par objet, 8 au plus.", 13, Tok.Muted, wrap: true).WithMargin(8, 12, 8, 12));
        else
        {
            body.Children.Add(new MultiChart(series, from, now, s.MarketPercent, 400));
            if (!s.MarketPercent && lasts.Count > 1 && lasts.Min() > 0 && lasts.Max() / (double)lasts.Min() > 20)
                body.Children.Add(T("Prix très différents : les petits se lisent mal sur le même axe ; « évolution % » les compare mieux.", 11, Tok.Muted, wrap: true).WithMargin(8, 0, 8, 0));
        }
        if (s.MarketSeries.Any(k => k is not null)) body.Children.Add(legend);
        var periodText = days > 0 ? $"{days} derniers jours" : "depuis le premier relevé";
        var chart = Card(body, $"Courbes des prix · {data.Label(shown)}", T($"{periodText} · {(s.MarketPercent ? "évolution %" : "prix unitaire en Kinah")}", 11, Tok.Muted, mono: true),
            new Thickness(12, 10, 12, 8));

        yield return Columns("*,320", chart, MarketChartPicker(rows));
        yield return T("Courbes en escalier : un prix vaut jusqu'au relevé suivant ; le pointillé prolonge le dernier prix connu jusqu'à aujourd'hui. " +
                       "Survole le graphique : le prix de chaque objet à ce moment-là. En « évolution % », chaque courbe part de 0 au début de la période " +
                       "(le dernier prix connu avant, sinon son premier relevé).", 11, Tok.Muted, wrap: true).WithLineHeight(1.4);
    }

    /// <summary>Colonne de droite du graphique : les objets du marché à cocher (recherche, rang, type), avec leur dernier prix.</summary>
    Control MarketChartPicker(List<(string Key, MarketItem? Item, List<MarketPrice> List)> rows)
    {
        var used = s.MarketSeries.Count(k => k is not null);
        var full = used >= s.MarketSeries.Length;
        string KindOf(MarketItem? i) => i?.IsGathering == true ? "Récolte" : "Équipement et autres";
        var present = rows.Select(x => x.Item?.GameGrade).OfType<string>().Distinct().OrderBy(g => db.Grade(g).Rank).ToList();
        var kinds = rows.Select(x => KindOf(x.Item)).Distinct().ToList();
        var body = new StackPanel { Spacing = 8 };
        var query = new TextBox { Text = s.MarketQuery ?? "", PlaceholderText = "chercher : aria, anneau…" };
        body.Children.Add(query);
        if (present.Count > 1)
            body.Children.Add(Choice(new[] { ("tous", s.MarketGrade is null, (Action)(() => update(st => st.MarketGrade = null))) }
                .Concat(present.Select(g => (Market.GradeWord(g), s.MarketGrade == g, (Action)(() => update(st => st.MarketGrade = g))))), wrap: true));
        if (kinds.Count > 1)
            body.Children.Add(Choice(new[] { ("tout", s.MarketKind is null, (Action)(() => update(st => st.MarketKind = null))) }
                .Concat(kinds.Select(k => (k == "Récolte" ? "récolte" : "équipement et autres", s.MarketKind == k, (Action)(() => update(st => st.MarketKind = k))))), wrap: true));

        var list = new StackPanel();
        var lines = new List<(Control Row, string Text)>();
        foreach (var x in rows.Where(x => (s.MarketGrade is null || x.Item?.GameGrade == s.MarketGrade) && (s.MarketKind is null || KindOf(x.Item) == s.MarketKind)))
        {
            var key = x.Key;
            var slot = Array.IndexOf(s.MarketSeries, key);
            var box = new Border
            {
                Width = 11, Height = 11, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
                Background = slot >= 0 ? SeriesColors[slot] : Brushes.Transparent, BorderBrush = slot >= 0 ? null : Tok.Faint, BorderThickness = new Thickness(slot >= 0 ? 0 : 1),
            };
            var content = ColumnsSpaced("12,*,74", 8, box, MarketItemLine(x.Item, x.List[^1].ItemName, 12),
                T(Fmt.Big(x.List[^1].Price), 11, Tok.Text2, mono: true).AlignRight());
            var row = Clickable(content, () => update(st =>
            {
                var i = Array.IndexOf(st.MarketSeries, key);
                if (i >= 0) st.MarketSeries[i] = null;
                else if (Array.IndexOf(st.MarketSeries, null) is var free and >= 0) st.MarketSeries[free] = key;
            }), slot >= 0 ? Accent.Dim : null, Tok.Hover, new Thickness(6, 4));
            row.CornerRadius = new CornerRadius(3);
            list.Children.Add(slot < 0 && full ? Tip(row, "8 courbes au plus : retires-en une pour tracer celui-ci.") : row);
            lines.Add((row, $"{x.List[^1].ItemName} {x.Item?.Category}"));
        }
        var none = T("Aucun objet ne correspond.", 12, Tok.Muted).WithMargin(6, 6, 6, 6);
        list.Children.Add(none);
        void Filter()
        {
            var visible = 0;
            foreach (var (row, text) in lines) { row.IsVisible = Market.Matches(query.Text, text); if (row.IsVisible) visible++; }
            none.IsVisible = visible == 0;
        }
        query.TextChanged += (_, _) => { s.MarketQuery = query.Text; Filter(); };
        Filter();
        body.Children.Add(new ScrollViewer { MaxHeight = 470, Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return Card(body, "Objets à tracer", T($"{used} / {s.MarketSeries.Length}", 11, full ? Tok.Warn : Tok.Muted, mono: true), new Thickness(12, 10, 12, 10));
    }
}

/// <summary>
/// Graphique à plusieurs courbes de Prix HdV, sur un seul axe : prix en Kinah, ou évolution en % depuis le début de la période.
/// Courbes en escalier (un prix vaut jusqu'au relevé suivant), pointillé jusqu'à aujourd'hui pour le dernier prix connu. Survol :
/// trait vertical au relevé le plus proche et le prix de chaque objet à ce moment-là. Nom au bout des courbes jusqu'à 4 courbes.
/// </summary>
sealed class MultiChart : Control
{
    sealed record Pt(DateTime At, double Value, long Price, bool Carried);

    readonly List<(PriceSeries Series, List<Pt> Points)> lines = [];
    readonly List<DateTime> stops;
    readonly DateTime from, to;
    readonly bool percent;
    Rect plot;
    DateTime? hover;
    static readonly Typeface Mono = new(Fonts.Mono);

    public MultiChart(IReadOnlyList<PriceSeries> series, DateTime from, DateTime to, bool percent, double height)
    {
        (this.from, this.to, this.percent) = (from, to, percent);
        Height = height;
        foreach (var s in series)
        {
            var sorted = s.Points.OrderBy(p => p.At).ToList();
            var pts = new List<Pt>();
            var before = sorted.FindLastIndex(p => p.At <= from);  // prix connu au début de la période
            if (before >= 0) pts.Add(new Pt(from, 0, sorted[before].Price, true));
            pts.AddRange(sorted.Where(p => p.At > from && p.At <= to).Select(p => new Pt(p.At, 0, p.Price, false)));
            if (pts.Count == 0) continue;
            double basis = pts[0].Price;
            lines.Add((s, pts.Select(p => p with { Value = percent ? basis > 0 ? (p.Price / basis - 1) * 100 : 0 : p.Price }).ToList()));
        }
        stops = lines.SelectMany(l => l.Points.Where(p => !p.Carried).Select(p => p.At)).Distinct().Order().ToList();
        PointerMoved += (_, e) =>
        {
            if (stops.Count == 0) return;
            var x = e.GetPosition(this).X;
            var near = stops.MinBy(t => Math.Abs(X(t) - x));
            if (near == hover) return;
            hover = near;
            InvalidateVisual();
        };
        PointerExited += (_, _) => { hover = null; InvalidateVisual(); };
    }

    /// <summary>Fond transparent : le survol est reçu sur toute la zone, pas seulement sur les traits.</summary>
    static readonly IBrush Background = Brushes.Transparent;

    protected override Size MeasureOverride(Size available) => new(double.IsInfinity(available.Width) ? 600 : available.Width, Height);

    double X(DateTime t)
    {
        var span = (to - from).TotalSeconds;
        return span < 1 ? plot.X + plot.Width / 2 : plot.X + Math.Clamp((t - from).TotalSeconds / span, 0, 1) * plot.Width;
    }

    string Value(double v) => percent
        ? $"{(v > 0.05 ? "+" : v < -0.05 ? "−" : "")}{Math.Abs(v).ToString(Math.Abs(v) < 10 ? "0.#" : "0", CultureInfo.CurrentCulture)} %"
        : Fmt.Big(v);

    static double Nice(double raw)
    {
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / mag;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * mag;
    }

    static FormattedText Text(string s, IBrush color, double size = 10) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Mono, size, color);

    static string Short(string name, int max = 22) => name.Length <= max ? name : name[..(max - 1)] + "…";

    /// <summary>Dates en bas : un repère par jour, 2 jours, semaine… selon la place ; le premier et le dernier jour à défaut.</summary>
    List<DateTime> DateTicks()
    {
        var span = (to - from).TotalDays;
        var room = Math.Max(2, (int)(plot.Width / 72));
        var step = new[] { 1, 2, 7, 14, 30, 61, 91, 182, 365 }.FirstOrDefault(d => span / d <= room, 730);
        var ticks = new List<DateTime>();
        var t = from.ToLocalTime().Date;
        if (t < from.ToLocalTime()) t = t.AddDays(1);
        for (; t <= to.ToLocalTime(); t = t.AddDays(step)) ticks.Add(t.ToUniversalTime());
        return ticks.Count > 0 ? ticks : new List<DateTime> { from, to };
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Background, new Rect(Bounds.Size));
        if (lines.Count == 0) return;

        // Axe des valeurs : 4 à 5 repères ronds ; en %, le 0 (prix du début) est toujours visible.
        var values = lines.SelectMany(l => l.Points.Select(p => p.Value)).ToList();
        if (percent) values.Add(0);
        double lo = values.Min(), hi = values.Max();
        if (hi - lo < 1e-9) { var pad = Math.Max(1, Math.Abs(hi) * 0.05); lo -= pad; hi += pad; }
        var step = Nice((hi - lo) / 4);
        if (!percent) step = Math.Max(1, step);
        lo = Math.Floor(lo / step) * step;
        hi = Math.Ceiling(hi / step) * step;
        var ticks = new List<double>();
        for (var v = lo; v <= hi + step / 2; v += step) ticks.Add(v);
        var tickText = ticks.Select(v => Text(Value(v), Tok.Muted)).ToList();
        var left = tickText.Max(t => t.Width) + 10;

        // Noms au bout des courbes (4 courbes au plus) : la place à droite.
        var direct = lines.Count <= 4;
        var names = direct ? lines.Select(l => Text(Short(l.Series.Name), Tok.Text2, 11)).ToList() : new List<FormattedText>();
        var right = direct ? names.Max(n => n.Width) + 26 : 14;
        plot = new Rect(left, 10, Math.Max(1, Bounds.Width - left - right), Math.Max(1, Bounds.Height - 36));
        double Y(double v) => plot.Bottom - (v - lo) / (hi - lo) * plot.Height;

        var grid = new Pen(Tok.RowLine2, 1);
        for (var k = 0; k < ticks.Count; k++)
        {
            var y = Y(ticks[k]);
            ctx.DrawLine(percent && Math.Abs(ticks[k]) < step / 1000 ? new Pen(Tok.Faint, 1) : grid, new Point(plot.X, y), new Point(plot.Right, y));
            ctx.DrawText(tickText[k], new Point(plot.X - 8 - tickText[k].Width, y - tickText[k].Height / 2));
        }
        var lastRight = double.MinValue;
        foreach (var t in DateTicks())
        {
            var label = Text(t.ToLocalTime().ToString("dd/MM"), Tok.Muted);
            var x = Math.Clamp(X(t) - label.Width / 2, 0, Math.Max(0, Bounds.Width - label.Width));
            if (x < lastRight + 8) continue;
            ctx.DrawLine(grid, new Point(X(t), plot.Bottom), new Point(X(t), plot.Bottom + 4));
            ctx.DrawText(label, new Point(x, plot.Bottom + 8));
            lastRight = x + label.Width;
        }

        // Courbes en escalier, points aux relevés (anneau de la couleur du fond quand ils se chevauchent), pointillé jusqu'à aujourd'hui.
        var ring = new Pen(Tok.Panel, 2);
        var ends = new List<(int Line, double Y)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var (s, pts) = lines[i];
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(X(pts[0].At), Y(pts[0].Value)), false);
                for (var j = 1; j < pts.Count; j++)
                {
                    g.LineTo(new Point(X(pts[j].At), Y(pts[j - 1].Value)));
                    g.LineTo(new Point(X(pts[j].At), Y(pts[j].Value)));
                }
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(s.Color, 2, lineJoin: PenLineJoin.Round), geo);
            var end = new Point(X(pts[^1].At), Y(pts[^1].Value));
            if (plot.Right - end.X > 2)
                ctx.DrawLine(new Pen(Tok.Alpha(s.Color, 0.6), 1.5, new DashStyle(new double[] { 2, 2 }, 0)), end, new Point(plot.Right, end.Y));
            foreach (var p in pts.Where(p => !p.Carried))
                ctx.DrawEllipse(s.Color, ring, new Point(X(p.At), Y(p.Value)), 3.5, 3.5);
            ends.Add((i, end.Y));
        }

        // Noms au bout, écartés pour ne pas se chevaucher.
        if (direct)
        {
            var placed = ends.OrderBy(e => e.Y).ToList();
            var h = names.Max(n => n.Height) + 2;
            var ys = placed.Select(e => e.Y).ToArray();
            for (var k = 1; k < ys.Length; k++) ys[k] = Math.Max(ys[k], ys[k - 1] + h);
            var overflow = ys.Length > 0 ? ys[^1] + h / 2 - Bounds.Height : 0;
            if (overflow > 0) for (var k = 0; k < ys.Length; k++) ys[k] -= overflow;
            for (var k = 0; k < placed.Count; k++)
            {
                var (line, _) = placed[k];
                var y = ys[k];
                ctx.DrawLine(new Pen(lines[line].Series.Color, 2), new Point(plot.Right + 6, y), new Point(plot.Right + 14, y));
                ctx.DrawText(names[line], new Point(plot.Right + 18, y - names[line].Height / 2));
            }
        }

        if (hover is not { } at) return;
        // Survol : trait vertical, le prix de chaque objet à ce moment-là (le dernier relevé jusque-là), du plus haut au plus bas.
        var hx = X(at);
        ctx.DrawLine(new Pen(Tok.Alpha(Tok.Text2, 0.4), 1), new Point(hx, plot.Y), new Point(hx, plot.Bottom));
        var rows = new List<(IBrush Color, FormattedText Name, FormattedText Value, double Sort)>();
        foreach (var (s, pts) in lines)
        {
            var j = pts.FindLastIndex(p => p.At <= at);
            if (j < 0) continue;
            var p = pts[j];
            ctx.DrawEllipse(s.Color, ring, new Point(hx, Y(p.Value)), 5, 5);
            var text = percent ? $"{Value(p.Value)} · {Fmt.Big(p.Price)}" : $"{Fmt.Big(p.Price)} Kinah";
            rows.Add((s.Color, Text(Short(s.Name, 26), Tok.Text2, 11), Text(text, p.At == at ? Tok.Text : Tok.Text2, 11), p.Value));
        }
        var head = Text(at.ToLocalTime().ToString("dddd dd/MM HH:mm", CultureInfo.CurrentCulture), Tok.Text, 11);
        var nameW = rows.Count > 0 ? rows.Max(r => r.Name.Width) : 0;
        var valueW = rows.Count > 0 ? rows.Max(r => r.Value.Width) : 0;
        var lineH = head.Height + 3;
        var boxW = Math.Max(head.Width, 14 + nameW + 14 + valueW) + 16;
        var boxH = 8 + lineH * (rows.Count + 1);
        var bx = hx + 14 + boxW <= Bounds.Width ? hx + 14 : Math.Max(0, hx - 14 - boxW);
        // En haut ou en bas du graphique : là où elle cache le moins de relevés.
        var dots = lines.SelectMany(l => l.Points.Select(p => new Point(X(p.At), Y(p.Value)))).ToList();
        var top = new Rect(bx, plot.Y, boxW, boxH);
        var bottom = new Rect(bx, Math.Max(plot.Y, plot.Bottom - boxH), boxW, boxH);
        var box = dots.Count(bottom.Contains) < dots.Count(top.Contains) ? bottom : top;
        ctx.DrawRectangle(Tok.HoverStrong, new Pen(Tok.Border, 1), box, 4, 4);
        ctx.DrawText(head, new Point(box.X + 8, box.Y + 4));
        var ry = box.Y + 4 + lineH;
        foreach (var r in rows.OrderByDescending(r => r.Sort))
        {
            ctx.DrawEllipse(r.Color, null, new Point(box.X + 12, ry + r.Name.Height / 2), 3.5, 3.5);
            ctx.DrawText(r.Name, new Point(box.X + 22, ry));
            ctx.DrawText(r.Value, new Point(box.Right - 8 - r.Value.Width, ry));
            ry += lineH;
        }
    }
}
