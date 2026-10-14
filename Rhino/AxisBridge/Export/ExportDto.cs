using System;
using System.Collections.Generic;
using AxisBridge.Library;
using Newtonsoft.Json;

namespace AxisBridge.Export
{
    public class SourceDto
    {
        [JsonProperty("file")]
        public string File { get; set; } = "";

        [JsonProperty("unitSystem")]
        public string UnitSystem { get; set; } = "";

        [JsonProperty("tolerance")]
        public double Tolerance { get; set; } = 0.001;
    }

    public class PointDto
    {
        [JsonProperty("guid")]
        public string Guid { get; set; } = "";

        [JsonProperty("xyz")]
        public double[] Xyz { get; set; } = new double[3];

        [JsonProperty("attrs")]
        public Dictionary<string, string> Attrs { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class LineDto
    {
        [JsonProperty("guid")]
        public string Guid { get; set; } = "";

        /// <summary>"line", "arc" (with arcMid) or "polyline" (with points: one member through every vertex).</summary>
        [JsonProperty("geom")]
        public string Geom { get; set; } = "line";

        [JsonProperty("start")]
        public double[] Start { get; set; } = new double[3];

        [JsonProperty("end")]
        public double[] End { get; set; } = new double[3];

        [JsonProperty("arcMid", NullValueHandling = NullValueHandling.Ignore)]
        public double[] ArcMid { get; set; }

        [JsonProperty("points", NullValueHandling = NullValueHandling.Ignore)]
        public List<double[]> Points { get; set; }

        [JsonProperty("attrs")]
        public Dictionary<string, string> Attrs { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class DomainDto
    {
        /// <summary>Rhino id; faces of a polysurface get "id_f0", "id_f1", ...</summary>
        [JsonProperty("guid")]
        public string Guid { get; set; } = "";

        [JsonProperty("contour")]
        public List<double[]> Contour { get; set; } = new List<double[]>();

        /// <summary>Inner boundaries (openings), each a closed vertex loop.</summary>
        [JsonProperty("holes", NullValueHandling = NullValueHandling.Ignore)]
        public List<List<double[]>> Holes { get; set; }

        [JsonProperty("attrs")]
        public Dictionary<string, string> Attrs { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class ExportModelDto
    {
        /// <summary>2 = adds library, polylines, holes and multi-face surfaces.</summary>
        [JsonProperty("schema")]
        public int Schema { get; set; } = 2;

        [JsonProperty("source")]
        public SourceDto Source { get; set; } = new SourceDto();

        [JsonProperty("library")]
        public AxLibrary Library { get; set; } = new AxLibrary();

        [JsonProperty("points")]
        public List<PointDto> Points { get; set; } = new List<PointDto>();

        [JsonProperty("lines")]
        public List<LineDto> Lines { get; set; } = new List<LineDto>();

        [JsonProperty("domains")]
        public List<DomainDto> Domains { get; set; } = new List<DomainDto>();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
