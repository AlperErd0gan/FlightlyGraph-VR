using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>One fact found in the data, pointing at the airport or route it is about.</summary>
public class GraphInsight
{
    public string category;
    /// <summary>Short line for the dashboard list.</summary>
    public string text;
    /// <summary>Sentence for narration / the guided tour.</summary>
    public string spoken;
    public GraphNode node;
    public GraphEdge edge;
}

/// <summary>
/// Finds notable facts in the loaded graph, for the dashboard's Insights tab and the tour.
/// Time-based facts need a monthly time axis (meta file) whose first periods are the
/// baseline (e.g. Jan-Feb 2020, before COVID). They are made season-neutral, because
/// summer airports otherwise look "recovered" in July 2020 against a winter baseline:
/// - growth compares the same months of the latest year with the baseline months;
/// - the pre-COVID yearly level is the baseline divided by the airport's recent share of
///   those months in a year; an airport has recovered in the first month whose trailing
///   12-month average reaches 90% of that level.
/// Cargo facts need market segments. Categories without data are left out, so any
/// dataset works.
/// </summary>
public static class GraphInsights
{
    public const string FastestRecovery = "Fastest recovery";
    public const string SlowestRecovery = "Furthest below pre-COVID";
    public const string BiggestGrowth = "Biggest growth";
    public const string GrowingRoutes = "Fastest growing routes";
    public const string CargoHubs = "Cargo hubs";
    public const string CargoRoutes = "Cargo routes";

    private const int BaselinePeriods = 2;
    private const int CandidateAirports = 100;      // only the busiest airports, to avoid tiny-number noise
    private const float MinRouteFlightsPerDay = 2f;  // growth of routes with at least this many at baseline
    private const float RecoveredShare = 0.9f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<GraphInsight> Compute(GraphLoader graph, MetaData meta, int perCategory = 3)
    {
        var insights = new List<GraphInsight>();
        if (graph == null || !graph.IsLoaded) return insights;

        var airports = new List<GraphNode>(graph.Nodes.Values);
        airports.Sort((a, b) => a.value != b.value ? b.value.CompareTo(a.value) : string.CompareOrdinal(a.id, b.id));
        if (airports.Count > CandidateAirports) airports.RemoveRange(CandidateAirports, airports.Count - CandidateAirports);

        TimeAxis axis = TimeAxis.From(meta);
        if (axis != null)
        {
            AddRecovery(insights, airports, axis, perCategory);
            AddGrowth(insights, airports, axis, perCategory);
            AddRouteGrowth(insights, graph, axis, perCategory);
        }
        AddCargo(insights, graph, airports, meta, perCategory);
        return insights;
    }

    // ---- Time-based ------------------------------------------------------

    private sealed class TimeAxis
    {
        public string[] periods;
        public int[] days;
        public int[] compare;      // latest periods with the same months as the baseline
        public string baseLabel;   // "Jan-Feb 2020"
        public string compareLabel;

        public static TimeAxis From(MetaData meta)
        {
            if (meta == null || meta.periods == null || meta.periodDays == null) return null;
            int n = meta.periods.Length;
            if (n < 14 || meta.periodDays.Length != n) return null;
            string baseYear = Year(meta.periods[0]);
            var baseMonths = new string[BaselinePeriods];
            for (int i = 0; i < BaselinePeriods; i++)
            {
                if (meta.periods[i] == null || meta.periods[i].Length < 7) return null;
                baseMonths[i] = meta.periods[i].Substring(5, 2);
            }
            // Latest later year that has all baseline months.
            for (int year = int.Parse(Year(meta.periods[n - 1]), Inv); year > int.Parse(baseYear, Inv); year--)
            {
                var idx = new int[BaselinePeriods];
                bool all = true;
                for (int i = 0; i < BaselinePeriods && all; i++)
                {
                    idx[i] = System.Array.IndexOf(meta.periods, $"{year}-{baseMonths[i]}");
                    all = idx[i] >= 0;
                }
                if (!all) continue;
                return new TimeAxis
                {
                    periods = meta.periods,
                    days = meta.periodDays,
                    compare = idx,
                    baseLabel = MonthsLabel(baseMonths, baseYear),
                    compareLabel = MonthsLabel(baseMonths, year.ToString(Inv)),
                };
            }
            return null;
        }

        public float[] PerDay(int[] monthly)
        {
            if (monthly == null || monthly.Length != periods.Length) return null;
            var s = new float[monthly.Length];
            for (int i = 0; i < s.Length; i++) s[i] = days[i] > 0 ? (float)monthly[i] / days[i] : 0f;
            return s;
        }

        public float Baseline(float[] s)
        {
            float sum = 0f;
            for (int i = 0; i < BaselinePeriods; i++) sum += s[i];
            return sum / BaselinePeriods;
        }

        public float Compared(float[] s)
        {
            float sum = 0f;
            foreach (int i in compare) sum += s[i];
            return sum / compare.Length;
        }
    }

    private struct AirportTrend
    {
        public GraphNode node;
        public float growth;        // compared months / baseline months
        public int recoveredAt;     // period index, -1 if not yet
        public float currentShare;  // last 12 months / pre-COVID yearly level
    }

    private static List<AirportTrend> Trends(List<GraphNode> airports, TimeAxis axis)
    {
        var trends = new List<AirportTrend>();
        foreach (GraphNode node in airports)
        {
            float[] s = node.data != null ? axis.PerDay(node.data.monthly) : null;
            if (s == null) continue;
            float baseline = axis.Baseline(s);
            float compared = axis.Compared(s);
            float last12 = Trailing12(s, s.Length - 1);
            if (baseline <= 0f || compared <= 0f || last12 <= 0f) continue;

            // Seasonal factor from the recent year: how busy the baseline months are relative to the whole year.
            float preCovidYearly = baseline / (compared / last12);
            int recovered = -1;
            for (int i = 11; i < s.Length; i++)
            {
                if (Trailing12(s, i) >= RecoveredShare * preCovidYearly)
                {
                    recovered = i;
                    break;
                }
            }
            trends.Add(new AirportTrend
            {
                node = node,
                growth = compared / baseline,
                recoveredAt = recovered,
                currentShare = last12 / preCovidYearly,
            });
        }
        return trends;
    }

    private static void AddRecovery(List<GraphInsight> insights, List<GraphNode> airports, TimeAxis axis, int count)
    {
        List<AirportTrend> trends = Trends(airports, axis);
        var recovered = trends.FindAll(t => t.recoveredAt >= 0);
        recovered.Sort((a, b) => a.recoveredAt != b.recoveredAt ? a.recoveredAt.CompareTo(b.recoveredAt) : b.node.value.CompareTo(a.node.value));
        for (int i = 0; i < recovered.Count && i < count; i++)
        {
            AirportTrend t = recovered[i];
            string month = axis.periods[t.recoveredAt];
            insights.Add(new GraphInsight
            {
                category = FastestRecovery,
                text = $"<b>{t.node.ShortCode}</b> {City(t.node)}: back to 90% by {month}",
                spoken = $"{Name(t.node)} recovered fastest, back to ninety percent of its pre-COVID traffic by {MonthName(month)}.",
                node = t.node,
            });
        }

        var slowest = new List<AirportTrend>(trends);
        slowest.Sort((a, b) => a.currentShare.CompareTo(b.currentShare));
        for (int i = 0; i < slowest.Count && i < count; i++)
        {
            AirportTrend t = slowest[i];
            if (t.currentShare >= RecoveredShare) break;
            string pct = (t.currentShare * 100f).ToString("0", Inv);
            insights.Add(new GraphInsight
            {
                category = SlowestRecovery,
                text = $"<b>{t.node.ShortCode}</b> {City(t.node)}: now at {pct}% of pre-COVID",
                spoken = $"{Name(t.node)} is still at {pct} percent of its pre-COVID traffic.",
                node = t.node,
            });
        }
    }

    private static void AddGrowth(List<GraphInsight> insights, List<GraphNode> airports, TimeAxis axis, int count)
    {
        List<AirportTrend> trends = Trends(airports, axis);
        trends.Sort((a, b) => b.growth.CompareTo(a.growth));
        for (int i = 0; i < trends.Count && i < count; i++)
        {
            AirportTrend t = trends[i];
            if (t.growth <= 1f) break;
            string pct = (t.growth * 100f - 100f).ToString("0", Inv);
            insights.Add(new GraphInsight
            {
                category = BiggestGrowth,
                text = $"<b>{t.node.ShortCode}</b> {City(t.node)}: +{pct}% ({axis.compareLabel} vs {axis.baseLabel})",
                spoken = $"{Name(t.node)} grew the most: {pct} percent more flights than before COVID, in the same season.",
                node = t.node,
            });
        }
    }

    private static void AddRouteGrowth(List<GraphInsight> insights, GraphLoader graph, TimeAxis axis, int count)
    {
        var routes = new List<(GraphEdge edge, float before, float after)>();
        foreach (GraphEdge e in graph.Edges)
        {
            float[] s = e.data != null ? axis.PerDay(e.data.monthly) : null;
            if (s == null) continue;
            float before = axis.Baseline(s);
            if (before < MinRouteFlightsPerDay) continue;
            routes.Add((e, before, axis.Compared(s)));
        }
        routes.Sort((a, b) => (b.after / b.before).CompareTo(a.after / a.before));
        for (int i = 0; i < routes.Count && i < count; i++)
        {
            (GraphEdge e, float before, float after) = routes[i];
            if (after <= before) break;
            GraphNode a = graph.Nodes[e.sourceId];
            GraphNode b = graph.Nodes[e.targetId];
            insights.Add(new GraphInsight
            {
                category = GrowingRoutes,
                text = $"<b>{a.ShortCode}-{b.ShortCode}</b>: {before.ToString("0.#", Inv)} -> {after.ToString("0.#", Inv)} flights/day",
                spoken = $"The route from {Name(a)} to {Name(b)} grew from {before.ToString("0.#", Inv)} to {after.ToString("0.#", Inv)} flights a day.",
                edge = e,
            });
        }
    }

    // ---- Cargo -----------------------------------------------------------

    private static void AddCargo(List<GraphInsight> insights, GraphLoader graph, List<GraphNode> airports, MetaData meta, int count)
    {
        var hubs = airports.FindAll(n => n.data != null && n.data.cargoShare > 0f);
        hubs.Sort((a, b) => b.data.cargoShare.CompareTo(a.data.cargoShare));
        for (int i = 0; i < hubs.Count && i < count; i++)
        {
            GraphNode n = hubs[i];
            string pct = (n.data.cargoShare * 100f).ToString("0", Inv);
            insights.Add(new GraphInsight
            {
                category = CargoHubs,
                text = $"<b>{n.ShortCode}</b> {City(n)}: {pct}% cargo flights",
                spoken = $"{Name(n)} is a cargo hub: {pct} percent of its flights carry only freight.",
                node = n,
            });
        }

        // Routes with at least one flight a day on average, mostly cargo; busiest first among equals.
        float totalDays = meta != null && meta.days > 0 ? meta.days : 0f;
        var routes = new List<GraphEdge>();
        foreach (GraphEdge e in graph.Edges)
        {
            if (e.data == null || e.data.cargoShare < 0.5f) continue;
            if (totalDays > 0f && e.weight / totalDays < 1f) continue;
            routes.Add(e);
        }
        routes.Sort((a, b) =>
        {
            int c = b.data.cargoShare.CompareTo(a.data.cargoShare);
            return c != 0 ? c : b.weight.CompareTo(a.weight);
        });
        for (int i = 0; i < routes.Count && i < count; i++)
        {
            GraphEdge e = routes[i];
            GraphNode a = graph.Nodes[e.sourceId];
            GraphNode b = graph.Nodes[e.targetId];
            string pct = (e.data.cargoShare * 100f).ToString("0", Inv);
            string perDay = totalDays > 0f ? $", {(e.weight / totalDays).ToString("0.#", Inv)}/day" : "";
            insights.Add(new GraphInsight
            {
                category = CargoRoutes,
                text = $"<b>{a.ShortCode}-{b.ShortCode}</b>: {pct}% cargo{perDay}",
                spoken = $"Between {Name(a)} and {Name(b)}, {pct} percent of the flights are cargo.",
                edge = e,
            });
        }
    }

    // ---- Helpers ---------------------------------------------------------

    private static float Trailing12(float[] s, int end)
    {
        float sum = 0f;
        for (int i = end - 11; i <= end; i++) sum += s[i];
        return sum / 12f;
    }

    private static string Name(GraphNode node) => NodeNarrator.SpokenShortName(node);

    private static string City(GraphNode node)
    {
        string city = node.data != null && !string.IsNullOrEmpty(node.data.city) ? node.data.city : Name(node);
        int paren = city.IndexOf(" (", System.StringComparison.Ordinal);
        city = paren > 0 ? city.Substring(0, paren) : city;
        return city.Length > 16 ? city.Substring(0, 15) + "." : city;
    }

    private static string Year(string period) => period != null && period.Length >= 4 ? period.Substring(0, 4) : "";

    private static readonly string[] MonthsShort = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    private static readonly string[] MonthsLong =
        { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    private static string MonthsLabel(string[] months, string year)
    {
        var names = new List<string>();
        foreach (string mm in months)
        {
            names.Add(int.TryParse(mm, out int m) && m >= 1 && m <= 12 ? MonthsShort[m - 1] : mm);
        }
        return string.Join("-", names) + " " + year;
    }

    /// <summary>"2021-08" -> "August 2021".</summary>
    private static string MonthName(string period)
    {
        if (period != null && period.Length >= 7 && int.TryParse(period.Substring(5, 2), out int m) && m >= 1 && m <= 12)
        {
            return MonthsLong[m - 1] + " " + period.Substring(0, 4);
        }
        return period;
    }
}
