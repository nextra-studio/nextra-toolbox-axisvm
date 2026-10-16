using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// Attachments and per-entity state of the live link.
    ///
    /// Attachments (all UTF-8 text):
    ///   RhinoGuid    - the Rhino object(s) an entity stands for; ";"-joined when curves overlap on one line.
    ///   RhinoLink    - the link/export file name an import of which created the entity (see AxisModelBuilder.Link).
    ///   RhinoAdopted - "file|guid;guid": an entity drawn in AxisVM that "Send to Rhino" handed over, and the
    ///                  Rhino object(s) it became. It is replaced only once Rhino holds those objects.
    ///   RhinoState   - the entity as it was when the link last brought it in line with Rhino: "geometry|type|
    ///                  start section|end section|material" for lines, "geometry|type|thickness|material" for
    ///                  domains, "geometry" for nodes. "Send to Rhino" compares against it field by field, so only
    ///                  what was changed in AxisVM goes back - never what AxisVM normalised on import (a Beam
    ///                  on a slab edge that became a Rib, a substituted section).
    /// </summary>
    public static class LinkState
    {
        public const string Guid = "RhinoGuid";
        public const string Link = "RhinoLink";
        public const string Adopted = "RhinoAdopted";
        public const string State = "RhinoState";

        // ==================================================================
        // Attachments
        // ==================================================================

        public static string Read(AxisVMAttachments attachments, string name, int itemIndex)
        {
            if (attachments == null) return null;
            try
            {
                Array data;
                if (attachments.GetData(name, itemIndex, out data) <= 0 || data == null || data.Length == 0) return null;
                byte[] bytes = new byte[data.Length];
                int k = 0;
                foreach (object b in data) bytes[k++] = Convert.ToByte(b);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }

        /// <summary>Writes (or replaces) one attachment.</summary>
        public static bool Write(AxisVMAttachments attachments, string name, int itemIndex, string text)
        {
            if (attachments == null) return false;
            try
            {
                try { attachments.Delete(name, itemIndex); } catch { }
                byte[] payload = Encoding.UTF8.GetBytes(text ?? "");
                Array data = Array.CreateInstance(typeof(byte), new[] { payload.Length }, new[] { 1 });
                for (int i = 0; i < payload.Length; i++) data.SetValue(payload[i], i + 1);
                return attachments.AddData(name, itemIndex, data) > 0;
            }
            catch { return false; }
        }

        public static List<string> Guids(AxisVMAttachments attachments, int itemIndex)
        {
            return SplitGuids(Read(attachments, Guid, itemIndex));
        }

        public static List<string> SplitGuids(string text)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string g in text.Split(';'))
                if (g.Trim().Length > 0 && !list.Contains(g.Trim())) list.Add(g.Trim());
            return list;
        }

        /// <summary>RhinoAdopted -> (file key, owner guids); null when the entity is not adopted.</summary>
        public static string AdoptedKey(string adopted, out List<string> owners)
        {
            owners = new List<string>();
            if (string.IsNullOrEmpty(adopted)) return null;
            int bar = adopted.IndexOf('|');
            if (bar < 0) return adopted;
            owners = SplitGuids(adopted.Substring(bar + 1));
            return adopted.Substring(0, bar);
        }

        // ==================================================================
        // Reading the model
        // ==================================================================

        public static double[] NodeXyz(IAxisVMModel model, int nodeId)
        {
            if (nodeId <= 0) return null;
            try
            {
                RPoint3d p = new RPoint3d();
                if (model.Nodes.GetNodeCoord(nodeId, ref p) <= 0) return null;
                return new[] { p.x, p.y, p.z };
            }
            catch { return null; }
        }

        public static bool TryLineAttr(IAxisVMModel model, int lineId, out RLineAttr attr)
        {
            attr = new RLineAttr();
            try
            {
                Array ids = Array.CreateInstance(typeof(int), new[] { 1 }, new[] { 1 });
                ids.SetValue(lineId, 1);
                Array got;
                model.Lines.BulkGetAttr(ids, out got);
                if (got == null || got.Length < 1) return false;
                attr = (RLineAttr)got.GetValue(got.GetLowerBound(0));
                return true;
            }
            catch { return false; }
        }

        public static string SectionName(IAxisVMModel model, int index)
        {
            if (index <= 0) return "";
            try { return (model.CrossSections.Item[index].Name ?? "").Trim(); } catch { return ""; }
        }

        public static string MaterialName(IAxisVMModel model, int index)
        {
            if (index <= 0) return "";
            try { return (model.Materials.Item[index].Name ?? "").Trim(); } catch { return ""; }
        }

        /// <summary>Ax_Type spelling of an AxisVM line type ("" for types the link does not carry).</summary>
        public static string LineTypeName(ELineType t)
        {
            switch (t)
            {
                case ELineType.ltBeam: return "Beam";
                case ELineType.ltRib: return "Rib";
                case ELineType.ltTruss: return "Truss";
                default: return "";
            }
        }

        public static string SurfaceTypeName(ESurfaceType t)
        {
            switch (t)
            {
                case ESurfaceType.stPlate: return "Plate";
                case ESurfaceType.stMembraneStress: return "Membrane";
                default: return "Shell";
            }
        }

        public static bool IsArc(IAxisVMModel model, int lineId)
        {
            try { return model.Lines.Item[lineId].GeomType == ELineGeomType.lgtCircleArc; } catch { return false; }
        }

        /// <summary>
        /// The middle of an arc line, from its own centre, normal and sweep (IAxisVMLine.GetMidPoint returns
        /// (0,0,0) for arcs in AxisVM 18 r1o). Null when it cannot be read.
        /// </summary>
        public static double[] ArcMid(IAxisVMModel model, int lineId)
        {
            try
            {
                IAxisVMLine line = model.Lines.Item[lineId];
                RLineGeomData g = new RLineGeomData();
                line.GetGeomData(ref g);
                double[] start = NodeXyz(model, Math.Abs(line.StartNode));
                if (start == null) return null;

                double[] c = { g.CircleArc.Center.x, g.CircleArc.Center.y, g.CircleArc.Center.z };
                double[] n = { g.CircleArc.NormalVector.x, g.CircleArc.NormalVector.y, g.CircleArc.NormalVector.z };
                double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (len < 1e-12) return null;
                n = new[] { n[0] / len, n[1] / len, n[2] / len };

                // Rodrigues: the start point turned by half the sweep about n.
                double[] v = { start[0] - c[0], start[1] - c[1], start[2] - c[2] };
                double half = g.CircleArc.Alfa / 2.0, cos = Math.Cos(half), sin = Math.Sin(half);
                double[] nv = { n[1] * v[2] - n[2] * v[1], n[2] * v[0] - n[0] * v[2], n[0] * v[1] - n[1] * v[0] };
                double nd = n[0] * v[0] + n[1] * v[1] + n[2] * v[2];
                return new[]
                {
                    c[0] + v[0] * cos + nv[0] * sin + n[0] * nd * (1 - cos),
                    c[1] + v[1] * cos + nv[1] * sin + n[1] * nd * (1 - cos),
                    c[2] + v[2] * cos + nv[2] * sin + n[2] * nd * (1 - cos)
                };
            }
            catch { return null; }
        }

        public static int[] LineNodes(IAxisVMModel model, int lineId)
        {
            try
            {
                IAxisVMLine ln = model.Lines.Item[lineId];
                return new[] { ln.StartNode, ln.EndNode };
            }
            catch { return null; }
        }

        public static List<int> IntList(Array a)
        {
            List<int> list = new List<int>();
            if (a == null) return list;
            foreach (object o in a) list.Add(Convert.ToInt32(o));
            return list;
        }

        public static List<int> ContourLines(IAxisVMModel model, int domainId)
        {
            try { return IntList(model.Domains.Item[domainId].ContourLines); } catch { return new List<int>(); }
        }

        public static List<List<int>> HoleLines(IAxisVMModel model, int domainId)
        {
            List<List<int>> holes = new List<List<int>>();
            try
            {
                IAxisVMDomain d = model.Domains.Item[domainId];
                int n = d.HoleCount;
                for (int h = 1; h <= n; h++) holes.Add(IntList(d.HoleLines[h]));
            }
            catch { }
            return holes;
        }

        public static bool TrySurfaceAttr(IAxisVMModel model, int domainId, out RSurfaceAttr attr)
        {
            attr = new RSurfaceAttr();
            try { return model.Domains.Item[domainId].GetSurfaceAttr(ref attr) > 0; } catch { return false; }
        }

        // ==================================================================
        // State strings
        // ==================================================================

        private static string F(double[] p)
        {
            if (p == null) return "?";
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0000},{1:0.0000},{2:0.0000}", p[0], p[1], p[2]);
        }

        public static string NodeState(IAxisVMModel model, int nodeId)
        {
            return F(NodeXyz(model, nodeId));
        }

        /// <summary>Line: "x,y,z;x,y,z[;arc]|type|start section|end section|material" (0.1 mm).</summary>
        public static string LineState(IAxisVMModel model, int lineId)
        {
            int[] n = LineNodes(model, lineId);
            string geom = n == null ? "?" : F(NodeXyz(model, n[0])) + ";" + F(NodeXyz(model, n[1]));
            if (IsArc(model, lineId)) geom += ";arc " + F(ArcMid(model, lineId));

            RLineAttr a;
            if (!TryLineAttr(model, lineId, out a)) return geom + "||||";
            string type = LineTypeName(a.LineType);
            if (type.Length == 0) type = "other" + (int)a.LineType;
            return string.Join("|", geom, type, SectionName(model, a.StartCrossSectionIndex), SectionName(model, a.EndCrossSectionIndex), MaterialName(model, a.MaterialIndex));
        }

        /// <summary>The sorted end-point pairs of some lines: the shape of a domain or load panel outline, order independent.</summary>
        public static string SegmentsState(IAxisVMModel model, List<int> lines)
        {
            List<string> segs = new List<string>();
            foreach (int l in lines)
            {
                int[] n = LineNodes(model, l);
                if (n == null) continue;
                string a = F(NodeXyz(model, n[0])), b = F(NodeXyz(model, n[1]));
                segs.Add(string.CompareOrdinal(a, b) < 0 ? a + ">" + b : b + ">" + a);
            }
            segs.Sort(StringComparer.Ordinal);
            return string.Join(";", segs);
        }

        /// <summary>Domain: "sorted contour/hole segments|type|thickness [m]|material".</summary>
        public static string DomainState(IAxisVMModel model, int domainId)
        {
            List<int> lines = ContourLines(model, domainId);
            foreach (List<int> h in HoleLines(model, domainId)) lines.AddRange(h);
            string segs = SegmentsState(model, lines);

            RSurfaceAttr s;
            if (!TrySurfaceAttr(model, domainId, out s)) return segs + "|||";
            return string.Join("|", segs, SurfaceTypeName(s.SurfaceType),
                s.Thickness.ToString("0.######", CultureInfo.InvariantCulture), MaterialName(model, s.MaterialId));
        }

        public static string[] Fields(string state, int count)
        {
            string[] f = (state ?? "").Split('|');
            if (f.Length >= count) return f;
            string[] padded = new string[count];
            for (int i = 0; i < count; i++) padded[i] = i < f.Length ? f[i] : "";
            return padded;
        }
    }
}
