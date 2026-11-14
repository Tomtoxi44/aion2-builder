using Aion2Builder.Analysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static Aion2Builder.App.Ui;

namespace Aion2Builder.App;

/// <summary>
/// Crafts épinglés : épinglés depuis la page d'une recette, ils ont chacun une carte en haut de la vue d'ensemble du Craft, avec
/// le prix de vente, le coût en achetant tout, et leurs courbes sur 3 jours ; des cases ajoutent ou retirent la vente, le coût et
/// chaque ingrédient (sa part du coût : quantité × prix).
/// </summary>
sealed partial class ScreenViews
{
    const int PinDays = 3;

    bool CraftPinned(int id) => s.CraftPins.Contains(id);

    void ToggleCraftPin(int id) => update(st => { if (!st.CraftPins.Remove(id)) st.CraftPins.Add(id); });

    /// <summary>Courbe d'une carte épinglée affichée : la vente et le coût le sont d'office, les ingrédients non ; un clic inverse.</summary>
    bool PinLineOn(int id, string line, bool byDefault) => byDefault ^ s.CraftPinLines.Contains($"{id}:{line}");

    void TogglePinLine(int id, string line) => update(st => { var k = $"{id}:{line}"; if (!st.CraftPinLines.Remove(k)) st.CraftPinLines.Add(k); });

    /// <summary>Les crafts épinglés, une carte chacun (deux par ligne), sous les chiffres de la vue d'ensemble.</summary>
    IEnumerable<Control> CraftPinBlocks(CraftModel m)
    {
        var pinned = m.Plans.Where(p => s.CraftPins.Contains(p.Recipe.Id)).ToList();
        if (pinned.Count == 0) yield break;
        var now = DateTime.UtcNow;
        var from = now.AddDays(-PinDays);
        // Relevés du marché montré, par objet, du plus ancien au plus récent.
        var history = m.Data.Prices.Where(x => x.Market.Equals(m.Shown, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.ItemKey).ToDictionary(g => g.Key, g => g.Select(x => (At: x.At.ToUniversalTime(), x.Price)).OrderBy(x => x.At).ToList());
        yield return Label($"Épinglés · {pinned.Count}");
        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = pinned.Count == 1 ? 1 : 2 };
        foreach (var p in pinned) grid.Children.Add(CraftPinCard(m, p, history, from, now).WithMargin(0, 0, 12, 12));
        yield return grid;
    }

    /// <summary>Le dernier prix connu à cette date (null avant le premier relevé).</summary>
    static long? PriceAt(List<(DateTime At, long Price)>? h, DateTime t)
    {
        if (h is null) return null;
        long? v = null;
        foreach (var x in h)
        {
            if (x.At > t) break;
            v = x.Price;
        }
        return v;
    }

    Control CraftPinCard(CraftModel m, CraftPlan p, Dictionary<string, List<(DateTime At, long Price)>> history, DateTime from, DateTime now)
    {
        var rc = p.Recipe;
        var id = rc.Id;
        List<(DateTime At, long Price)>? HistoryOf(RecipePart part) =>
            history.TryGetValue(part.Key, out var h) ? h : m.Book.ItemOf(part) is { } i && history.TryGetValue(i.Key, out var h2) ? h2 : null;

        // Les dates où quelque chose change : le début de la période et chaque relevé dedans.
        var times = new SortedSet<DateTime> { from };
        void AddTimes(List<(DateTime At, long Price)>? h)
        {
            if (h is null) return;
            foreach (var x in h) if (x.At > from && x.At <= now) times.Add(x.At);
        }

        // Le prix à l'unité de chaque ingrédient à une date, comme le coût de la recette, mais tout acheté : PNJ, hôtel ou Od du
        // même rang (le moins cher des deux), coût actuel de sa recette s'il se fabrique ; null = pas encore de prix à cette date.
        var units = new List<(RecipePart Part, Func<DateTime, long?> Unit, bool Known)>();
        foreach (var part in rc.Inputs)
        {
            if (part.Npc is { } npc) { units.Add((part, _ => npc, true)); continue; }
            var pc = m.Book.CostOf(rc, part);
            switch (pc.Source)
            {
                case PartSource.Market or PartSource.Od:
                    var own = HistoryOf(part);
                    var od = m.Book.OdFor(part) is { } o && history.TryGetValue(o.Od.Key, out var oh) ? oh : null;
                    AddTimes(own);
                    AddTimes(od);
                    units.Add((part, t => (PriceAt(own, t), PriceAt(od, t)) switch
                    {
                        ({ } a, { } b) => Math.Min(a, b),
                        ({ } a, null) => a,
                        (null, { } b) => b,
                        _ => null,
                    }, true));
                    break;
                case PartSource.Recipe when pc.Unit is { } made:
                    units.Add((part, _ => made, true));
                    break;
                default:
                    units.Add((part, _ => 0, false));
                    break;
            }
        }
        var sellHistory = p.C.ForSelf ? null : HistoryOf(rc.Output);
        AddTimes(sellHistory);
        var tax = m.Tax;

        // Une courbe : sa valeur à chaque date, sans répéter un même prix (un point = un changement).
        List<(DateTime At, long Price)> Points(Func<DateTime, long?> value)
        {
            var list = new List<(DateTime At, long Price)>();
            foreach (var t in times)
                if (value(t) is { } v && (list.Count == 0 || list[^1].Price != v)) list.Add((t, v));
            return list;
        }

        var percent = PinLineOn(id, "%", false);
        var series = new List<PriceSeries>();
        var chips = new WrapPanel();
        var noData = new List<string>();
        List<(DateTime At, long Price)>? sellPts = null, costPts = null;

        void Line(string key, string name, IBrush color, bool byDefault, Func<DateTime, long?> value, string tip, Action<List<(DateTime, long)>>? keep = null)
        {
            var pts = Points(value);
            keep?.Invoke(pts);
            var on = PinLineOn(id, key, byDefault);
            if (on && pts.Count > 0) series.Add(new PriceSeries(name, color, pts));
            if (on && pts.Count == 0) noData.Add(name);
            chips.Children.Add(PinChip(name, color, on, () => TogglePinLine(id, key), tip));
        }

        if (!p.C.ForSelf)
            Line("sell", "Vente", SeriesColors[0], true,
                t => PriceAt(sellHistory, t) is { } u ? (long)Math.Round(u * (1 - tax) * rc.Output.Qty) : null,
                $"Prix de vente à l'hôtel{(rc.Output.Qty > 1 ? $" des {rc.Output.Qty} fabriqués" : "")}, frais de vente de {m.TaxNote} déduits.", pts => sellPts = pts);
        var partial = units.Any(u => !u.Known);
        Line("cost", partial ? "Coût (au moins)" : "Coût", SeriesColors[1], true, t =>
            {
                long sum = p.C.Fee;
                foreach (var (part, unit, _) in units)
                {
                    if (unit(t) is not { } u) return null;
                    sum += u * part.Qty;
                }
                return sum;
            },
            "Coût d'une fabrication en achetant tout (hôtel, Od ou PNJ, le moins cher) plus le coût de fabrication" +
            (partial ? " ; des ingrédients sans prix comptent 0." : "."), pts => costPts = pts);
        for (var i = 0; i < units.Count && i < SeriesColors.Length - 2; i++)
        {
            var (part, unit, known) = units[i];
            if (!known) continue;
            var label = part.Qty > 1 ? $"{part.Name} ×{part.Qty}" : part.Name;
            var source = part.Npc is not null ? "prix du PNJ" : m.Book.CostOf(rc, part).Source switch
            {
                PartSource.Recipe => "coût de sa recette",
                PartSource.Od => "prix de l'hôtel ou de l'Od du même rang, le moins cher",
                _ => "prix de l'hôtel (ou de l'Od du même rang s'il est moins cher)",
            };
            Line(part.Key, label, SeriesColors[2 + i], false, t => unit(t) is { } u ? u * part.Qty : null,
                $"{part.Name} : {part.Qty} × {source}, sa part du coût d'une fabrication.");
        }

        // En-tête : l'objet (clic : la recette), retirer l'épingle.
        var title = Clickable(MarketItemLine(p.Item, rc.Output.Qty > 1 ? $"{rc.Output.Name} ×{rc.Output.Qty}" : rc.Output.Name, 14), () => OpenRecipe(id), null, Tok.Hover, new Thickness(4, 2));
        title.CornerRadius = new CornerRadius(3);
        var unpin = Clickable(T("✕", 11, Tok.Muted), () => ToggleCraftPin(id), null, Tok.Hover, new Thickness(6, 2));
        unpin.CornerRadius = new CornerRadius(3);
        var head = Columns("*,Auto", Tip(title, "Ouvrir la recette : ses ingrédients et le calcul."), Tip(unpin, "Désépingler ce craft"));

        // Les chiffres du moment, et leur évolution sur les 3 jours.
        Control Figure(string label, List<(DateTime At, long Price)>? pts, IBrush? color = null)
        {
            var panel = new StackPanel { Spacing = 1 };
            panel.Children.Add(T(label, 10, Tok.Muted, mono: true));
            if (pts is not { Count: > 0 }) { panel.Children.Add(T("—", 15, Tok.Faint, FontWeight.SemiBold, mono: true)); return panel; }
            var last = pts[^1].Price;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(T(Short(last), 15, color ?? Tok.Text, FontWeight.SemiBold, mono: true));
            var first = pts[0].Price;
            if (pts.Count > 1 && first > 0)
            {
                var pct = (last - first) * 100.0 / first;
                line.Children.Add(Tip(T(Math.Abs(pct) < 0.05 ? "=" : $"{(pct > 0 ? "+" : "−")}{Math.Abs(pct):0.#} %", 11, pct > 0 ? Tok.Good : pct < 0 ? Tok.Bad : Tok.Muted, mono: true)
                    .WithMargin(0, 4, 0, 0), $"{Fmt.Big(first)} → {Fmt.Big(last)} Kinah sur {PinDays} jours"));
            }
            panel.Children.Add(line);
            return Tip(panel, $"{Fmt.Big(last)} Kinah");
        }
        var gap = sellPts is { Count: > 0 } sp && costPts is { Count: > 0 } cp && !partial ? sp[^1].Price - cp[^1].Price : (long?)null;
        var figures = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        figures.Children.Add(Figure(p.C.ForSelf ? "VENTE · ne se vend pas" : "VENTE, FRAIS DÉDUITS", sellPts));
        figures.Children.Add(Figure(partial ? "COÛT, AU MOINS" : "COÛT, TOUT ACHETÉ", costPts));
        var gapPanel = new StackPanel { Spacing = 1 };
        gapPanel.Children.Add(T("ÉCART", 10, Tok.Muted, mono: true));
        gapPanel.Children.Add(gap is { } g
            ? T($"{(g >= 0 ? "+" : "−")}{Short(Math.Abs(g))}", 15, g > 0 ? Tok.Good : Tok.Bad, FontWeight.SemiBold, mono: true)
            : T("—", 15, Tok.Faint, FontWeight.SemiBold, mono: true));
        figures.Children.Add(Tip(gapPanel, "Vente (frais de vente déduits) moins coût en achetant tout, aux derniers prix."));
        // Prix plancher : le prix affiché à l'hôtel sous lequel la vente ne rembourse plus le coût, taxe comprise.
        var floor = !p.C.ForSelf && costPts is { Count: > 0 } fp && fp[^1].Price > 0 ? (long)Math.Ceiling(fp[^1].Price / (rc.Output.Qty * (1 - tax))) : (long?)null;
        var floorPanel = new StackPanel { Spacing = 1 };
        floorPanel.Children.Add(T("PLANCHER, L'UNITÉ", 10, Tok.Muted, mono: true));
        floorPanel.Children.Add(T(floor is { } fl ? (partial ? "≥ " : "") + Short(fl) : "—", 15, floor is null ? Tok.Faint : Tok.Text, FontWeight.SemiBold, mono: true));
        figures.Children.Add(Tip(floorPanel, floor is { } fv
            ? $"Ne vends pas sous {Fmt.Big(fv)} Kinah l'unité : en dessous, la vente (frais de vente de {m.TaxNote} déduits) ne rembourse pas le coût en achetant tout."
            : "Le prix sous lequel la vente ne rembourse pas le coût : il faut le coût."));

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(head);
        body.Children.Add(figures);
        var measure = Tip(Choice([("Kinah", !percent, () => { if (percent) TogglePinLine(id, "%"); }), ("%", percent, () => { if (!percent) TogglePinLine(id, "%"); })]),
            "Kinah : les prix ; % : l'évolution de chaque courbe depuis le début des 3 jours, pour comparer des prix très différents.");
        body.Children.Add(Columns("*,Auto", chips, measure));
        if (series.Count > 0) body.Children.Add(new MultiChart(series, from, now, percent, 170));
        else body.Children.Add(T(noData.Count > 0 ? $"Pas de prix sur {m.Data.Label(m.Shown)} pour : {string.Join(", ", noData)}."
                                                 : "Coche la vente, le coût ou un ingrédient pour voir sa courbe.", 12, Tok.Muted, wrap: true).WithMargin(4, 8, 4, 8));
        if (series.Count > 0 && noData.Count > 0)
            body.Children.Add(T($"Sans prix sur {m.Data.Label(m.Shown)} : {string.Join(", ", noData)}.", 11, Tok.Muted, wrap: true));
        body.Children.Add(T($"{PinDays} derniers jours · prix de {m.Data.Label(m.Shown)}" + (percent ? "" : " · ingrédient : quantité × prix"), 10, Tok.Faint, mono: true));
        return Card(body, padding: new Thickness(12, 10, 12, 10));
    }

    /// <summary>Case d'une courbe : sa couleur quand elle est tracée, un clic l'ajoute ou la retire.</summary>
    static Control PinChip(string text, IBrush color, bool on, Action toggle, string tip)
    {
        var swatch = new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
            Background = on ? color : Brushes.Transparent, BorderBrush = on ? null : Tok.Faint, BorderThickness = new Thickness(on ? 0 : 1),
        };
        var label = text.Length > 28 ? text[..27] + "…" : text;
        var b = Clickable(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { swatch, T(label, 11, on ? Tok.Text : Tok.Muted) } },
            toggle, null, Tok.Hover, new Thickness(6, 3));
        b.CornerRadius = new CornerRadius(3);
        return Tip(b, tip).WithMargin(0, 0, 4, 4);
    }
}
