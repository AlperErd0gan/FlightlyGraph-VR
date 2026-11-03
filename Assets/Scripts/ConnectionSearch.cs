using System.Collections.Generic;

/// <summary>
/// Real connections over one day's flights (TASKS B7): every way to fly from one airport to
/// another with at most maxLegs flights, each next flight leaving minConnection .. maxWait
/// after the previous one arrives at the same airport. Flights are FlightSimulator's
/// (flights_ectrl.json: actual off-block / arrival times), passenger segments only (no
/// cargo, business jets, ...). For each flight leaving the origin the best way on is kept
/// (earliest arrival, then fewer flights, then less waiting); of those, an option that
/// leaves earlier and does not arrive earlier than another is dropped, so the options run
/// from the first departure of the day to the last, each arriving later than the one before.
/// Exhaustive for up to three flights, over indexes by origin and by airport pair.
/// Physically possible, not necessarily bookable: no tickets, alliances, terminals.
/// </summary>
public class ConnectionSearch
{
    public struct Settings
    {
        public int minConnectionSec;
        public int maxWaitSec;
        public int maxLegs;
        public bool sameAirline;

        public static Settings Default => new Settings { minConnectionSec = 45 * 60, maxWaitSec = 6 * 3600, maxLegs = 3 };
    }

    /// <summary>One way from A to B: its flights in order.</summary>
    public class Itinerary
    {
        public readonly List<FlightData> legs;

        public Itinerary(List<FlightData> legs) { this.legs = legs; }

        public int Departure => legs[0].dep;
        public int Arrival => legs[legs.Count - 1].arr;
        public int Transfers => legs.Count - 1;

        /// <summary>Time waited at the airports in between (s).</summary>
        public int WaitSec
        {
            get
            {
                int wait = 0;
                for (int i = 1; i < legs.Count; i++) wait += legs[i].dep - legs[i - 1].arr;
                return wait;
            }
        }
    }

    /// <summary>STATFOR market segments that carry passengers.</summary>
    public static readonly HashSet<string> PassengerSegments = new HashSet<string> { "Mainline", "Lowcost", "Regional Aircraft", "Charter" };

    /// <summary>Operator code EUROCONTROL uses for anonymised operators.</summary>
    public const string HiddenOperator = "ZZZ";

    public int PassengerFlights { get; }
    public int OtherFlights { get; }
    /// <summary>False for an older flights file without market segments: then every flight is used.</summary>
    public bool HasSegments { get; }

    private readonly Dictionary<string, List<FlightData>> byFrom = new Dictionary<string, List<FlightData>>();
    private readonly Dictionary<(string, string), List<FlightData>> byPair = new Dictionary<(string, string), List<FlightData>>();
    // Airports with a flight to the key airport (skips third-leg lookups that cannot succeed).
    private readonly Dictionary<string, HashSet<string>> into = new Dictionary<string, HashSet<string>>();

    public ConnectionSearch(IEnumerable<FlightData> flights)
    {
        var list = new List<FlightData>(flights);
        foreach (FlightData f in list) if (!string.IsNullOrEmpty(f.seg)) HasSegments = true;
        foreach (FlightData f in list)
        {
            if (f.origin == null || f.destination == null || f.origin == f.destination || f.arr <= f.dep) continue;
            if (HasSegments && (f.seg == null || !PassengerSegments.Contains(f.seg)))
            {
                OtherFlights++;
                continue;
            }
            PassengerFlights++;
            if (!byFrom.TryGetValue(f.origin, out List<FlightData> from)) byFrom[f.origin] = from = new List<FlightData>();
            from.Add(f);
            if (!byPair.TryGetValue((f.origin, f.destination), out List<FlightData> pair)) byPair[(f.origin, f.destination)] = pair = new List<FlightData>();
            pair.Add(f);
            if (!into.TryGetValue(f.destination, out HashSet<string> sources)) into[f.destination] = sources = new HashSet<string>();
            sources.Add(f.origin);
        }
        foreach (List<FlightData> l in byFrom.Values) l.Sort(ByDeparture);
        foreach (List<FlightData> l in byPair.Values) l.Sort(ByDeparture);
    }

    private static int ByDeparture(FlightData a, FlightData b) => a.dep != b.dep ? a.dep.CompareTo(b.dep) : string.CompareOrdinal(a.id, b.id);

    /// <summary>True for an operator code that names an airline (not empty, not anonymised).</summary>
    public static bool KnownOperator(string op) => !string.IsNullOrEmpty(op) && op != HiddenOperator;

    /// <summary>The useful ways from `from` to `to` this day, in departure order (see the class summary).</summary>
    public List<Itinerary> Options(string from, string to, Settings s)
    {
        var found = new List<Itinerary>();
        if (from == to || !byFrom.TryGetValue(from, out List<FlightData> firsts)) return found;
        into.TryGetValue(to, out HashSet<string> intoTo);

        foreach (FlightData f1 in firsts)
        {
            if (s.sameAirline && !KnownOperator(f1.op)) continue;
            Itinerary best = null;
            string hub = f1.destination;
            if (hub == to)
            {
                best = new Itinerary(new List<FlightData> { f1 });
            }
            else if (s.maxLegs >= 2 && intoTo != null)
            {
                if (intoTo.Contains(hub))
                {
                    foreach (FlightData f2 in Window(byPair[(hub, to)], f1.arr, s))
                    {
                        if (SameAirlineOk(s, f1, f2)) best = Better(best, new List<FlightData> { f1, f2 });
                    }
                }
                if (s.maxLegs >= 3 && byFrom.TryGetValue(hub, out List<FlightData> fromHub))
                {
                    foreach (FlightData f2 in Window(fromHub, f1.arr, s))
                    {
                        string hub2 = f2.destination;
                        if (hub2 == from || hub2 == to || hub2 == hub || !intoTo.Contains(hub2) || !SameAirlineOk(s, f1, f2)) continue;
                        foreach (FlightData f3 in Window(byPair[(hub2, to)], f2.arr, s))
                        {
                            if (SameAirlineOk(s, f1, f3)) best = Better(best, new List<FlightData> { f1, f2, f3 });
                        }
                    }
                }
            }
            if (best != null) found.Add(best);
        }

        // Latest departure first: keep an option only if it arrives before every later-leaving one.
        found.Sort((a, b) => a.Departure != b.Departure ? b.Departure.CompareTo(a.Departure) : Compare(a, b));
        var useful = new List<Itinerary>();
        int earliestLater = int.MaxValue;
        foreach (Itinerary it in found)
        {
            if (it.Arrival >= earliestLater) continue;
            useful.Add(it);
            earliestLater = it.Arrival;
        }
        useful.Reverse();
        return useful;
    }

    private static bool SameAirlineOk(Settings s, FlightData first, FlightData next) => !s.sameAirline || next.op == first.op;

    // Flights of `sorted` (by departure) leaving minConnection .. maxWait after `arrival`.
    private static IEnumerable<FlightData> Window(List<FlightData> sorted, int arrival, Settings s)
    {
        int earliest = arrival + s.minConnectionSec;
        int latest = arrival + s.maxWaitSec;
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid].dep < earliest) lo = mid + 1;
            else hi = mid;
        }
        for (int i = lo; i < sorted.Count && sorted[i].dep <= latest; i++) yield return sorted[i];
    }

    private static Itinerary Better(Itinerary current, List<FlightData> candidate)
    {
        var it = new Itinerary(candidate);
        return current == null || Compare(it, current) < 0 ? it : current;
    }

    // Earlier arrival, then fewer flights, then less waiting.
    private static int Compare(Itinerary a, Itinerary b)
    {
        if (a.Arrival != b.Arrival) return a.Arrival.CompareTo(b.Arrival);
        if (a.legs.Count != b.legs.Count) return a.legs.Count.CompareTo(b.legs.Count);
        return a.WaitSec.CompareTo(b.WaitSec);
    }
}
