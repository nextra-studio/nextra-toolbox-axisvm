using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AxisVM;

namespace AxisVM_plugin.Core
{
    public partial class AxisModelBuilder
    {
        /// <summary>Node index of the model after intersection (ids changed); null when no intersection ran.</summary>
        private NodeIndex liveNodes;

        /// <summary>The current node id at a position (after intersection, if any).</summary>
        private int LiveNode(double[] p)
        {
            int id;
            NodeIndex idx = liveNodes ?? nodes;
            return idx.TryFind(p[0], p[1], p[2], out id) ? id : 0;
        }

        // ==================================================================
        // 4g - parts
        // ==================================================================

        private void BuildParts()
        {
            Dictionary<string, List<RPartItem>> partItems = new Dictionary<string, List<RPartItem>>(StringComparer.OrdinalIgnoreCase);

            foreach (RhinoPoint p in rhino.Points)
                Collect(partItems, p, EPartItemType.pitNode, new[] { opt.DryRun ? 1 : LiveNode(M(p.Xyz)) });

            foreach (LineJob job in lineJobs)
                Collect(partItems, job.Src, EPartItemType.pitLine, opt.DryRun ? new[] { 1 } : job.Chain.Ids().ToArray());

            foreach (SurfaceJob s in surfaceJobs)
            {
                if (IsPanel(s.Role))
                {
                    if (s.Src.Attr(AxKeys.Parts) != null)
                        notes.Add(s.Context + ": AxisVM parts cannot contain load panels; its Ax_Parts was ignored.");
                    continue;
                }
                Collect(partItems, s.Src, EPartItemType.pitDomain, new[] { opt.DryRun ? 1 : s.DomainId });
            }

            if (opt.DryRun) { stats.PartsCreated = partItems.Count; return; }
            if (partItems.Count == 0) return;

            IAxisVMCustomPartFolder root;
            try { root = model.CustomParts.RootFolder; }
            catch (Exception ex)
            {
                notes.Add("Parts could not be created: the custom parts folder is unavailable (" + ex.Message + ").");
                return;
            }

            foreach (KeyValuePair<string, List<RPartItem>> kv in partItems)
            {
                if (kv.Value.Count == 0) continue;

                Array items = Array.CreateInstance(typeof(RPartItem), new[] { kv.Value.Count }, new[] { 1 });
                for (int i = 0; i < kv.Value.Count; i++) items.SetValue(kv.Value[i], i + 1);

                try
                {
                    int id = root.AddPart(kv.Key, ref items);
                    if (id > 0) stats.PartsCreated++;
                    else notes.Add(string.Format("Part '{0}' was rejected by AxisVM (code {1}) - does a part with that name already exist?", kv.Key, id));
                }
                catch (Exception ex)
                {
                    notes.Add(string.Format("Part '{0}' could not be created ({1}).", kv.Key, ex.Message));
                }
            }
        }

        private static void Collect(Dictionary<string, List<RPartItem>> partItems, RhinoEntity entity, EPartItemType type, int[] ids)
        {
            string text = entity.Attr(AxKeys.Parts);
            if (text == null) return;

            foreach (string name in AttributeParser.SplitList(text))
            {
                List<RPartItem> list;
                if (!partItems.TryGetValue(name, out list)) { list = new List<RPartItem>(); partItems[name] = list; }
                foreach (int id in ids)
                    if (id > 0) list.Add(new RPartItem { ItemType = type, Id = id });
            }
        }

        // ==================================================================
        // 4h - Rhino id stamping
        // ==================================================================

        private bool attachmentsWork = true;
        private readonly List<string> idMap = new List<string>();

        /// <summary>
        /// Records which Rhino object each AxisVM entity came from (attachment "RhinoGuid", or a
        /// sidecar file beside the JSON when the AxisVM build does not keep attachments).
        /// </summary>
        private void StampRhinoIds()
        {
            if (opt.DryRun) return;

            foreach (RhinoPoint p in rhino.Points)
            {
                int nodeId = LiveNode(M(p.Xyz));
                if (nodeId <= 0) continue;
                idMap.Add(string.Format("point\t{0}\t{1}", p.Guid, nodeId));
                Stamp(() => model.Nodes.Attachments, nodeId, p.Guid);
            }

            foreach (LineJob job in lineJobs)
                foreach (int id in job.Chain.Ids())
                {
                    idMap.Add(string.Format("line\t{0}\t{1}", job.Src.Guid, id));
                    Stamp(() => model.Lines.Attachments, id, job.Src.Guid);
                }

            foreach (SurfaceJob s in surfaceJobs)
            {
                if (s.DomainId > 0)
                {
                    idMap.Add(string.Format("domain\t{0}\t{1}", s.Src.Guid, s.DomainId));
                    Stamp(() => model.Domains.Attachments, s.DomainId, s.Src.Guid);
                }
                foreach (int pid in s.PanelIds) idMap.Add(string.Format("loadpanel\t{0}\t{1}", s.Src.Guid, pid));
            }

            if (!attachmentsWork) WriteSidecar();
        }

        private void Stamp(Func<AxisVMAttachments> getAttachments, int itemIndex, string guid)
        {
            if (!attachmentsWork || string.IsNullOrEmpty(guid)) return;
            try
            {
                AxisVMAttachments attachments = getAttachments();
                if (attachments == null) return;

                byte[] payload = Encoding.UTF8.GetBytes(guid);
                Array data = Array.CreateInstance(typeof(byte), new[] { payload.Length }, new[] { 1 });
                for (int i = 0; i < payload.Length; i++) data.SetValue(payload[i], i + 1);
                attachments.AddData("RhinoGuid", itemIndex, data);
            }
            catch (Exception ex)
            {
                // One failure means the mechanism is unavailable in this AxisVM build - stop trying.
                attachmentsWork = false;
                notes.Add("Rhino ids could not be attached to the AxisVM entities (" + ex.Message + "), so they were written to a sidecar file instead.");
            }
        }

        private void WriteSidecar()
        {
            if (string.IsNullOrEmpty(opt.SourcePath) || idMap.Count == 0) return;
            try
            {
                string path = Path.ChangeExtension(opt.SourcePath, ".axismap.txt");
                using (StreamWriter w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# kind\trhinoGuid\taxisVmId");
                    foreach (string line in idMap) w.WriteLine(line);
                }
                notes.Add("Rhino id map written to " + path);
            }
            catch (Exception ex)
            {
                notes.Add("The Rhino id map could not be written (" + ex.Message + ").");
            }
        }

        // ==================================================================
        // COM helpers
        // ==================================================================

        private int SafeLineCount() { try { return model.Lines.Count; } catch { return 0; } }
        private int SafeNodeCount() { try { return model.Nodes.Count; } catch { return 0; } }

        private double[] NodeXyz(int nodeId)
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

        private bool LineTypeIs(int lineId, ELineType type)
        {
            RLineAttr_V161 a;
            return TryGetLineAttrV161(lineId, out a) && a.LineType == type;
        }

        private bool TryGetLineAttr(int lineId, out RLineAttr attr)
        {
            attr = new RLineAttr();
            try
            {
                Array attrsOut;
                model.Lines.BulkGetAttr(Ids(lineId), out attrsOut);
                if (attrsOut == null || attrsOut.Length < 1) return false;
                attr = (RLineAttr)attrsOut.GetValue(attrsOut.GetLowerBound(0));
                return true;
            }
            catch { return false; }
        }

        private bool TrySetLineAttr(int lineId, RLineAttr attr)
        {
            try
            {
                Array attrsIn = Array.CreateInstance(typeof(RLineAttr), new[] { 1 }, new[] { 1 });
                attrsIn.SetValue(attr, 1);
                return model.Lines.BulkSetAttr(Ids(lineId), attrsIn) > 0;
            }
            catch { return false; }
        }

        private bool TryGetLineAttrV161(int lineId, out RLineAttr_V161 attr)
        {
            attr = new RLineAttr_V161();
            try
            {
                Array got;
                int n = model.Lines.BulkGetAttr_V161(Ids(lineId), out got);
                if (n < 1 || got == null || got.Length < 1) return false;
                object raw = got.GetValue(got.GetLowerBound(0));
                if (!(raw is RLineAttr_V161)) return false;
                attr = (RLineAttr_V161)raw;
                return true;
            }
            catch { return false; }
        }

        private bool TrySetLineAttrV161(int lineId, RLineAttr_V161 attr)
        {
            try
            {
                // Fresh 1-based array of our own type - the array BulkGetAttr_V161 returns is only
                // type-equivalent (EmbedInteropTypes) and SetValue would reject writes into it.
                Array arr = Array.CreateInstance(typeof(RLineAttr_V161), new[] { 1 }, new[] { 1 });
                arr.SetValue(attr, 1);
                return model.Lines.BulkSetAttr_V161(Ids(lineId), arr) > 0;
            }
            catch { return false; }
        }

        /// <summary>AxisVM's COM arrays are 1-based, so they cannot be a plain C# array.</summary>
        private static Array Ids(params int[] values)
        {
            Array a = Array.CreateInstance(typeof(int), new[] { values.Length }, new[] { 1 });
            for (int i = 0; i < values.Length; i++) a.SetValue(values[i], i + 1);
            return a;
        }

        private static RStiffnesses ToStiffnesses(double[] k)
        {
            RStiffnesses s = new RStiffnesses();
            s.x = k[0]; s.y = k[1]; s.z = k[2];
            s.xx = k[3]; s.yy = k[4]; s.zz = k[5];
            return s;
        }

        /// <summary>
        /// Release record for one end. The warping (ew) slot is forced to rigid and Beam7DOF is set
        /// false by the caller, so AxisVM shows no capacity values and the e_w row stays disabled.
        /// </summary>
        private RReleases_V161 ToReleasesV161(ReleaseSet set)
        {
            RReleases_V161 r = new RReleases_V161();
            r.x = DofV161(set.Dof[0], true);
            r.y = DofV161(set.Dof[1], true);
            r.z = DofV161(set.Dof[2], true);
            r.xx = DofV161(set.Dof[3], false);
            r.yy = DofV161(set.Dof[4], false);
            r.zz = DofV161(set.Dof[5], false);
            r.ew = new RRelease_V161 { ReleaseType = EReleaseType.rtRigid, FunctionId = 0 };
            return r;
        }

        private RRelease_V161 DofV161(ReleaseDof dof, bool translational)
        {
            switch (dof.Kind)
            {
                case 0:
                    return new RRelease_V161 { ReleaseType = EReleaseType.rtHinged, FunctionId = 0 };
                case 2:
                    int fi = translational ? springs.Translational(dof.Stiffness) : springs.Rotational(dof.Stiffness);
                    return fi > 0
                        ? new RRelease_V161 { ReleaseType = EReleaseType.rtSemiRigid, FunctionId = fi }
                        : new RRelease_V161 { ReleaseType = EReleaseType.rtHinged, FunctionId = 0 };
                default:
                    return new RRelease_V161 { ReleaseType = EReleaseType.rtRigid, FunctionId = 0 };
            }
        }

        private static RPoint3d Point(RhinoXyz v)
        {
            RPoint3d p = new RPoint3d();
            p.x = v.X; p.y = v.Y; p.z = v.Z;
            return p;
        }

        // ==================================================================
        // Small geometry helpers
        // ==================================================================

        private static string PairKey(int a, int b)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            return lo.ToString(CultureInfo.InvariantCulture) + "|" + hi.ToString(CultureInfo.InvariantCulture);
        }

        private static string Fmt(double[] p)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", p[0], p[1], p[2]);
        }

        private static double Dist(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        private static double[] Neg(double[] a) { return new[] { -a[0], -a[1], -a[2] }; }
        private static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }

        /// <summary>
        /// Centre, unit normal and sweep [rad] of the circular arc through s, m, e. The normal is
        /// oriented so that going s -> m -> e is counter-clockwise about it (right-hand rule) and the
        /// sweep is positive, measured from s.
        /// </summary>
        private static bool ArcData(double[] s, double[] m, double[] e, out double[] center, out double[] normal, out double sweep)
        {
            center = normal = null;
            sweep = 0;

            double[] ab = Sub(m, s), ac = Sub(e, s);
            double[] n = Cross(ab, ac);
            double n2 = Dot(n, n);
            if (n2 < 1e-18) return false;

            // Circumcentre: s + (|ac|^2 (n x ab) + |ab|^2 (ac x n)) / (2 |n|^2)
            double[] t1 = Cross(n, ab), t2 = Cross(ac, n);
            double ac2 = Dot(ac, ac), ab2 = Dot(ab, ab);
            center = new[]
            {
                s[0] + (ac2 * t1[0] + ab2 * t2[0]) / (2 * n2),
                s[1] + (ac2 * t1[1] + ab2 * t2[1]) / (2 * n2),
                s[2] + (ac2 * t1[2] + ab2 * t2[2]) / (2 * n2)
            };

            double[] turn = Cross(Sub(m, s), Sub(e, m));
            double len = Math.Sqrt(Dot(turn, turn));
            if (len < 1e-12) return false;
            normal = new[] { turn[0] / len, turn[1] / len, turn[2] / len };

            double[] u = Sub(s, center), v = Sub(e, center);
            sweep = Math.Atan2(Dot(Cross(u, v), normal), Dot(u, v));
            if (sweep <= 0) sweep += 2 * Math.PI;
            return true;
        }

        private static double ArcLength(double[] s, double[] m, double[] e)
        {
            double[] c, n;
            double sweep;
            if (!ArcData(s, m, e, out c, out n, out sweep)) return Dist(s, e);
            return Dist(s, c) * sweep;
        }

        // ==================================================================
        // Node lookup
        // ==================================================================

        /// <summary>
        /// A spatial hash over node positions. Probing the 26 neighbouring cells and then measuring the
        /// real distance makes the lookup exact within the tolerance (a plain rounded-coordinate key
        /// splits points that straddle a bucket edge into two nodes).
        /// </summary>
        private class NodeIndex
        {
            private class Entry
            {
                public double X, Y, Z;
                public int NodeId;
            }

            private readonly double cell;
            private readonly double tolerance;
            private readonly Dictionary<long, List<Entry>> buckets = new Dictionary<long, List<Entry>>();

            public NodeIndex(double tolerance)
            {
                this.tolerance = Math.Max(tolerance, 1e-9);
                this.cell = this.tolerance;
            }

            public bool TryFind(double x, double y, double z, out int nodeId)
            {
                nodeId = 0;
                long cx = (long)Math.Floor(x / cell), cy = (long)Math.Floor(y / cell), cz = (long)Math.Floor(z / cell);
                double best = tolerance * tolerance;
                bool found = false;

                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                        for (long dz = -1; dz <= 1; dz++)
                        {
                            List<Entry> bucket;
                            if (!buckets.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out bucket)) continue;
                            foreach (Entry e in bucket)
                            {
                                double ex = e.X - x, ey = e.Y - y, ez = e.Z - z;
                                double d2 = ex * ex + ey * ey + ez * ez;
                                if (d2 > best) continue;
                                best = d2;
                                nodeId = e.NodeId;
                                found = true;
                            }
                        }
                return found;
            }

            public void Add(double x, double y, double z, int nodeId)
            {
                long key = Key((long)Math.Floor(x / cell), (long)Math.Floor(y / cell), (long)Math.Floor(z / cell));
                List<Entry> bucket;
                if (!buckets.TryGetValue(key, out bucket)) { bucket = new List<Entry>(); buckets[key] = bucket; }
                bucket.Add(new Entry { X = x, Y = y, Z = z, NodeId = nodeId });
            }

            /// <summary>Packs three cell indices (21 bits each, offset to be positive) into one key.</summary>
            private static long Key(long x, long y, long z)
            {
                const long off = 1L << 20, mask = (1L << 21) - 1;
                return (((x + off) & mask) << 42) | (((y + off) & mask) << 21) | ((z + off) & mask);
            }
        }
    }
}
