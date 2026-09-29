using System;
using System.Collections.Generic;

// Plain data classes matching the JSON files in data/processed/.
// Field names must stay identical to the JSON keys: Newtonsoft maps them verbatim.
// Fields missing from a file keep their defaults (0 / null), so the same classes
// read nodes.json (OpenFlights), nodes_3d*.json and the EUROCONTROL *_ectrl.json.

[Serializable]
public class NodeData
{
    public string id;
    public string label;
    public float x;
    public float y;
    public float z;
    public int value;
    public float lat;
    public float lon;
    // Only in nodes_3d*.json (layout_3d.py); 0 when absent.
    public int community;

    // Only in EUROCONTROL files (build_graph_ectrl.py). Nullable where the JSON can hold null.
    public string city;
    public string country;
    public int departures;
    public int arrivals;
    public float? avgDepDelayMin;
    public float? avgArrDelayMin;
    public float scheduledShare;
    public float cargoShare;
    public Dictionary<string, int> segments;
    public string topOperator;
    public string topAcType;
    // Time axis: element i belongs to MetaData.periods[i] (one month each).
    public int[] monthly;
    public float?[] monthlyDepDelayMin;
    public float?[] monthlyArrDelayMin;
    public int[] hourly;
}

[Serializable]
public class EdgeData
{
    public string source;
    public string target;
    public int weight;

    // Only in EUROCONTROL files (build_graph_ectrl.py). Nullable where the JSON can hold null.
    public int forward;
    public int backward;
    public float? avgDistanceNm;
    public float? avgDurationMin;
    public float? avgDelayMin;
    public float scheduledShare;
    public float cargoShare;
    public Dictionary<string, int> segments;
    public string topOperator;
    public string topAcType;
    // Time axis: element i belongs to MetaData.periods[i] (one month each).
    public int[] monthly;
    public float?[] monthlyDelayMin;
    public int[] hourly;
}

/// <summary>
/// meta_ectrl.json: the time axis shared by every monthly[] array.
/// monthly[i] / periodDays[i] is flights per day in periods[i] ("yyyy-MM").
/// dateFrom / dateTo are left out on purpose: Newtonsoft would parse the
/// "yyyy-MM-dd" strings as DateTime and hand back a culture-formatted string.
/// </summary>
[Serializable]
public class MetaData
{
    public string source;
    public string[] files;
    public string airportInfo;
    public string[] periods;
    public int[] periodDays;
    public int[] periodFlights;
    // Share of flights in periods[i] with a known market segment (0 for months the source left unclassified).
    public float[] segmentCoverage;
    public int days;
    public string timezone;
    public int totalFlights;
    public int topN;
    public float minDaily;
    public int nodeCount;
    public int edgeCount;
}
