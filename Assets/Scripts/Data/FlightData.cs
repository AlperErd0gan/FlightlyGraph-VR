using System;
using System.Collections.Generic;
using Newtonsoft.Json;

// flights_ectrl.json (scripts/build_flight_sim.py), read by FlightSimulator.
// "day" / "startLabel" are not mapped on purpose: Newtonsoft would parse date-like
// strings as DateTime; the clock is computed from startEpoch instead.

[Serializable]
public class FlightSimData
{
    public string source;
    public string flightsFile;
    public string pointsFile;
    /// <summary>Window start, Unix time (UTC). Flight times are seconds from here.</summary>
    public long startEpoch;
    public int durationSec;
    public int candidateFlights;
    public int flightCount;
    /// <summary>"actual" / "greatCircle" -> number of flights with that path.</summary>
    public Dictionary<string, int> pathSources;
    public List<FlightData> flights;
}

[Serializable]
public class FlightData
{
    public string id;
    [JsonProperty("from")] public string origin;
    [JsonProperty("to")] public string destination;
    /// <summary>ICAO operator code ("ZZZ" = anonymised), may be null.</summary>
    public string op;
    /// <summary>ICAO aircraft type, may be null.</summary>
    public string type;
    /// <summary>"actual" (EUROCONTROL point profile) or "greatCircle" (approximate route, real times).</summary>
    public string path;
    public int dep;
    public int arr;
    // Parallel arrays, one entry per path point.
    public int[] t;
    public float[] lat;
    public float[] lon;
    /// <summary>Flight level (hundreds of feet).</summary>
    public int[] fl;
    /// <summary>Course to the next point, degrees from true north.</summary>
    public int[] hdg;
}
