using System;
using System.Collections.Generic;
using AxisVM;

namespace AxisVM_plugin.Functions
{
    /// <summary>
    /// Carries the loads of a load panel across its conversion to one-way panels.
    ///
    /// AxisVM has no typed getter for load panel loads: IAxisVMLoads.GetLoad returns the raw record
    /// bytes. The layouts below were measured on AxisVM X8 (type library 18.100):
    ///
    ///   whole-panel area load (AddLoadPanelLinear)      LoadType -1, 120 bytes
    ///     int LoadCaseId @0, LoadPanelId @4, DistributionType @8, LoadDistributionType @12,
    ///     Component @16, double P1 @24, P2 @32, P3 @40, x1..z3 @48..112
    ///   polygon area load (AddLoadPanelPolyArea)        LoadType 0x21, 120 bytes, panels via
    ///                                                   GetDomains_Surfaces_LoadPanels
    ///     int LoadCaseId @0, DistributionType @4, LoadDistributionType @8, Component @12,
    ///     double P1 @16, P2 @24, P3 @32, x1..z3 @40..104, int WindowLoad @112
    ///   point load (AddLoadPanelConcentrated)           LoadType -1, 88 bytes
    ///     int LoadCaseId @0, LoadPanelId @4, double Fx..Mz @8..48, x @56, y @64, z @72,
    ///     int SystemGLR @80, ReferenceId @84
    ///
    /// For a constant load AxisVM stores the intensity in the slot of its axis (P3 for a Z load),
    /// so every non-zero slot is re-added as its own load in that axis.
    /// </summary>
    internal static class PanelLoadCarrier
    {
        private const int PanelLoadType = -1;
        private const int PolyAreaLoadType = 0x21;
        private const int AreaRecordLength = 120;
        private const int PointRecordLength = 88;

        internal enum Kind { Area, Polygon, Point }

        internal sealed class CarriedLoad
        {
            public Kind Kind;
            public int LoadCaseId, DistributionType, LoadDistributionType, Component, WindowLoad;
            public double[] P = new double[3];        // P1..P3, or Fx..Fz for a point load
            public double[] Ref = new double[9];      // x1..z3 reference points, or x,y,z of a point load
            public double[] M = new double[3];        // Mx..Mz of a point load
            public int SystemGLR, ReferenceId;
            public List<RLine3d> Polygon;             // load outline of a polygon load
        }

        internal sealed class Captured
        {
            public List<CarriedLoad> Loads = new List<CarriedLoad>();
            public List<int> LoadIndexes = new List<int>();   // model indexes to delete before the panel
            public int NotCarried;                            // other loads on the panel (line loads ...)
        }

        /// <summary>Reads every load that sits on the panel.</summary>
        internal static Captured Capture(IAxisVMModel model, int panelIdx)
        {
            Captured result = new Captured();
            int count = 0;
            try { count = model.Loads.Count; } catch { }

            for (int i = 1; i <= count; i++)
            {
                int type = int.MinValue, elem = 0;
                try { type = (int)model.Loads.get_LoadType(i); } catch { }
                try { elem = model.Loads.get_ElementId(i); } catch { }

                bool onPanelByList = PanelListContains(model, i, panelIdx);
                bool onPanelById = type == PanelLoadType && elem == panelIdx;
                if (!onPanelByList && !onPanelById) continue;

                byte[] raw = ReadRecord(model, i);
                CarriedLoad load = null;
                if (raw != null)
                {
                    if (onPanelByList && type == PolyAreaLoadType && raw.Length >= AreaRecordLength)
                        load = ParsePolygon(model, i, raw);
                    else if (onPanelById && raw.Length >= AreaRecordLength && I(raw, 4) == panelIdx)
                        load = ParseArea(raw);
                    else if (onPanelById && raw.Length == PointRecordLength && I(raw, 4) == panelIdx)
                        load = ParsePoint(raw);
                }

                if (load == null) { result.NotCarried++; continue; }
                result.Loads.Add(load);
                result.LoadIndexes.Add(i);
            }
            return result;
        }

        /// <summary>Deletes the captured loads (highest index first, so the others keep their index).</summary>
        internal static void DeleteCaptured(IAxisVMModel model, Captured captured)
        {
            List<int> indexes = new List<int>(captured.LoadIndexes);
            indexes.Sort();
            for (int k = indexes.Count - 1; k >= 0; k--)
            {
                if (model.Loads.Delete(indexes[k]) <= 0)
                    throw new InvalidOperationException("AxisVM refused to delete load #" + indexes[k] + ".");
            }
        }

        /// <summary>
        /// Re-applies every captured load to the new panels. A whole-panel load goes onto each new panel;
        /// a polygon load is re-added once with its own outline, so AxisVM attaches it to every panel it
        /// covers; a point load goes to the panel that contains it. Returns the number of loads applied.
        /// </summary>
        internal static int Reapply(IAxisVMModel model, List<CarriedLoad> loads, List<int> panelUids, string label, List<string> notes)
        {
            int applied = 0;
            foreach (CarriedLoad load in loads)
            {
                string err = null;
                bool ok;
                switch (load.Kind)
                {
                    case Kind.Area: ok = ApplyArea(model, load, panelUids, out err); break;
                    case Kind.Polygon: ok = ApplyPolygon(model, load, out err); break;
                    default: ok = ApplyPoint(model, load, panelUids, out err); break;
                }
                if (ok) applied++;
                else notes.Add(string.Format("{0}: a {1} load in load case {2} could NOT be re-applied ({3}).",
                    label, Describe(load.Kind), load.LoadCaseId, err ?? "rejected by AxisVM"));
            }
            return applied;
        }

        internal static string Describe(Kind kind)
        {
            return kind == Kind.Area ? "area" : kind == Kind.Polygon ? "polygon area" : "point";
        }

        // ------------------------------------------------------------------
        // Parsing
        // ------------------------------------------------------------------

        private static CarriedLoad ParseArea(byte[] raw)
        {
            CarriedLoad l = new CarriedLoad
            {
                Kind = Kind.Area,
                LoadCaseId = I(raw, 0),
                DistributionType = I(raw, 8),
                LoadDistributionType = I(raw, 12),
                Component = I(raw, 16)
            };
            for (int k = 0; k < 3; k++) l.P[k] = D(raw, 24 + 8 * k);
            for (int k = 0; k < 9; k++) l.Ref[k] = D(raw, 48 + 8 * k);
            return l;
        }

        private static CarriedLoad ParsePolygon(IAxisVMModel model, int loadIdx, byte[] raw)
        {
            List<RLine3d> outline = ReadPolygon(model, loadIdx);
            if (outline == null) return null;

            CarriedLoad l = new CarriedLoad
            {
                Kind = Kind.Polygon,
                LoadCaseId = I(raw, 0),
                DistributionType = I(raw, 4),
                LoadDistributionType = I(raw, 8),
                Component = I(raw, 12),
                WindowLoad = I(raw, 112),
                Polygon = outline
            };
            for (int k = 0; k < 3; k++) l.P[k] = D(raw, 16 + 8 * k);
            for (int k = 0; k < 9; k++) l.Ref[k] = D(raw, 40 + 8 * k);
            return l;
        }

        private static CarriedLoad ParsePoint(byte[] raw)
        {
            CarriedLoad l = new CarriedLoad
            {
                Kind = Kind.Point,
                LoadCaseId = I(raw, 0),
                SystemGLR = I(raw, 80),
                ReferenceId = I(raw, 84)
            };
            for (int k = 0; k < 3; k++)
            {
                l.P[k] = D(raw, 8 + 8 * k);
                l.M[k] = D(raw, 32 + 8 * k);
                l.Ref[k] = D(raw, 56 + 8 * k);
            }
            return l;
        }

        private static List<RLine3d> ReadPolygon(IAxisVMModel model, int loadIdx)
        {
            AxisVMLines3d poly = null;
            try { model.Loads.GetPoly(loadIdx, out poly); } catch { return null; }
            if (poly == null || poly.Count < 3) return null;

            List<RLine3d> items = new List<RLine3d>(poly.Count);
            for (int i = 1; i <= poly.Count; i++)
            {
                RLine3d seg = new RLine3d();
                poly.GetItem(i, ref seg);
                items.Add(seg);
            }
            return items;
        }

        // ------------------------------------------------------------------
        // Re-applying
        // ------------------------------------------------------------------

        private static bool ApplyArea(IAxisVMModel model, CarriedLoad load, List<int> panelUids, out string err)
        {
            err = null;
            bool any = false;
            foreach (int uid in panelUids)
            {
                int pidx = model.LoadPanels.get_IndexOfUID(uid);
                if (pidx <= 0) continue;

                foreach (int[] part in Parts(load))
                {
                    RLoadPanelLinear rec = new RLoadPanelLinear();
                    rec.LoadCaseId = load.LoadCaseId;
                    rec.LoadPanelId = pidx;
                    rec.DistributionType = (EDistributionType)load.DistributionType;
                    rec.LoadDistributionType = (ELoadDistributionType)load.LoadDistributionType;
                    rec.Component = (EAxis)part[0];
                    SetIntensity(load, part, out rec.P1, out rec.P2, out rec.P3);
                    rec.x1 = load.Ref[0]; rec.y1 = load.Ref[1]; rec.z1 = load.Ref[2];
                    rec.x2 = load.Ref[3]; rec.y2 = load.Ref[4]; rec.z2 = load.Ref[5];
                    rec.x3 = load.Ref[6]; rec.y3 = load.Ref[7]; rec.z3 = load.Ref[8];

                    int id = model.Loads.AddLoadPanelLinear(ref rec);
                    if (id <= 0) { err = "AddLoadPanelLinear returned " + id; return false; }
                    any = true;
                }
            }
            if (!any) err = "no new panel to load";
            return any;
        }

        private static bool ApplyPolygon(IAxisVMModel model, CarriedLoad load, out string err)
        {
            err = null;
            foreach (int[] part in Parts(load))
            {
                AxisVMLines3d poly = new AxisVMLines3d();
                foreach (RLine3d seg in load.Polygon)
                {
                    RLine3d s = seg;
                    poly.Add(ref s);
                }

                RLoadPanelPolyArea rec = new RLoadPanelPolyArea();
                rec.LoadCaseId = load.LoadCaseId;
                rec.DistributionType = (EDistributionType)load.DistributionType;
                rec.LoadDistributionType = (ELoadDistributionType)load.LoadDistributionType;
                rec.Component = (EAxis)part[0];
                SetIntensity(load, part, out rec.P1, out rec.P2, out rec.P3);
                rec.x1 = load.Ref[0]; rec.y1 = load.Ref[1]; rec.z1 = load.Ref[2];
                rec.x2 = load.Ref[3]; rec.y2 = load.Ref[4]; rec.z2 = load.Ref[5];
                rec.x3 = load.Ref[6]; rec.y3 = load.Ref[7]; rec.z3 = load.Ref[8];
                rec.WindowLoad = (ELongBoolean)load.WindowLoad;

                int id = model.Loads.AddLoadPanelPolyArea(poly, ref rec);
                if (id <= 0) { err = "AddLoadPanelPolyArea returned " + id; return false; }
            }
            return true;
        }

        private static bool ApplyPoint(IAxisVMModel model, CarriedLoad load, List<int> panelUids, out string err)
        {
            err = "the point is not inside any new panel";
            foreach (int uid in panelUids)
            {
                int pidx = model.LoadPanels.get_IndexOfUID(uid);
                if (pidx <= 0) continue;

                RLoadPanelConcentrated rec = new RLoadPanelConcentrated();
                rec.LoadCaseId = load.LoadCaseId;
                rec.LoadPanelId = pidx;
                rec.Fx = load.P[0]; rec.Fy = load.P[1]; rec.Fz = load.P[2];
                rec.Mx = load.M[0]; rec.My = load.M[1]; rec.Mz = load.M[2];
                rec.x = load.Ref[0]; rec.y = load.Ref[1]; rec.z = load.Ref[2];
                rec.SystemGLR = (ESystem)load.SystemGLR;
                rec.ReferenceId = load.ReferenceId;

                int id;
                try { id = model.Loads.AddLoadPanelConcentrated(ref rec); } catch { id = 0; }
                if (id > 0) { err = null; return true; }
            }
            return false;
        }

        /// <summary>
        /// The loads to add for one captured load: a linear load as stored, a constant load once per
        /// axis with a non-zero intensity. Each entry is { component, slot } (slot -1 = as stored).
        /// </summary>
        private static List<int[]> Parts(CarriedLoad load)
        {
            List<int[]> parts = new List<int[]>();
            if (load.LoadDistributionType != (int)ELoadDistributionType.ldtConst)
            {
                parts.Add(new int[] { load.Component, -1 });
                return parts;
            }
            for (int k = 0; k < 3; k++)
                if (Math.Abs(load.P[k]) > 1e-12) parts.Add(new int[] { k, k });
            return parts;
        }

        private static void SetIntensity(CarriedLoad load, int[] part, out double p1, out double p2, out double p3)
        {
            if (part[1] < 0) { p1 = load.P[0]; p2 = load.P[1]; p3 = load.P[2]; return; }
            p1 = p2 = p3 = load.P[part[1]];
        }

        // ------------------------------------------------------------------
        // Raw record helpers
        // ------------------------------------------------------------------

        private static bool PanelListContains(IAxisVMModel model, int loadIdx, int panelIdx)
        {
            Array dom = null, srf = null, lps = null;
            try
            {
                if (model.Loads.GetDomains_Surfaces_LoadPanels(loadIdx, out dom, out srf, out lps) <= 0) return false;
            }
            catch { return false; }
            if (lps == null) return false;
            foreach (object o in lps)
            {
                if (Convert.ToInt32(o) == panelIdx) return true;
            }
            return false;
        }

        private static byte[] ReadRecord(IAxisVMModel model, int loadIdx)
        {
            Array data = null;
            try { model.Loads.GetLoad(loadIdx, out data); } catch { return null; }
            if (data == null || data.Length == 0) return null;

            byte[] raw = new byte[data.Length];
            int lower = data.GetLowerBound(0);
            for (int b = 0; b < raw.Length; b++) raw[b] = Convert.ToByte(data.GetValue(lower + b));
            return raw;
        }

        private static int I(byte[] raw, int offset)
        {
            return offset + 4 <= raw.Length ? BitConverter.ToInt32(raw, offset) : 0;
        }

        private static double D(byte[] raw, int offset)
        {
            return offset + 8 <= raw.Length ? BitConverter.ToDouble(raw, offset) : 0.0;
        }
    }
}
