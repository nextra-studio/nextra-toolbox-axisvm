using System;
using System.Collections.Generic;
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

        [JsonProperty("geom")]
        public string Geom { get; set; } = "line";

        [JsonProperty("start")]
        public double[] Start { get; set; } = new double[3];

        [JsonProperty("end")]
        public double[] End { get; set; } = new double[3];

        [JsonProperty("arcMid", NullValueHandling = NullValueHandling.Ignore)]
        public double[] ArcMid { get; set; }

        [JsonProperty("attrs")]
        public Dictionary<string, string> Attrs { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class DomainDto
    {
        [JsonProperty("guid")]
        public string Guid { get; set; } = "";

        [JsonProperty("contour")]
        public List<double[]> Contour { get; set; } = new List<double[]>();

        [JsonProperty("attrs")]
        public Dictionary<string, string> Attrs { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class ExportModelDto
    {
        [JsonProperty("schema")]
        public int Schema { get; set; } = 1;

        [JsonProperty("source")]
        public SourceDto Source { get; set; } = new SourceDto();

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
