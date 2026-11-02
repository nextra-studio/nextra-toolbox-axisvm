using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// What the live link carries besides geometry, element type, section, material and loads: supports, elastic
    /// foundations, releases, springs, node-node / line-to-line links, rigid bodies and edge hinges - read from AxisVM once
    /// per send and written as the Ax_* text an import reads (AttributeParser), in kN and m.
    ///
    /// "Send to Rhino" sends what changed against a baseline (attachment <see cref="Attachment"/>): the text of an
    /// element as the last load built it, or as it was when adopted. So a support AxisVM refused on import is not sent
    /// back as "removed", and an edit made in AxisVM stays pending until Rhino has loaded it.
    /// </summary>
    public class LinkExtras
    {
        /// <summary>Baseline: the element's extras as last built / adopted, "key=value" lines (see <see cref="Text"/>).</summary>
        public const string Attachment = "RhinoExtras";

        private readonly IAxisVMModel model;
        private readonly Dictionary<int, double> springK = new Dictionary<int, double>();

        /// <summary>Line -> its line support / elastic foundation, as Ax_Support text.</summary>
        public readonly Dictionary<int, string> LineSupport = new Dictionary<int, string>();
        /// <summary>Node -> its global nodal support, as Ax_Support text.</summary>
        public readonly Dictionary<int, string> NodeSupport = new Dictionary<int, string>();
        /// <summary>Line -> (domain, Ax_Stiffness text) of every edge hinge along it.</summary>
        public readonly Dictionary<int, List<KeyValuePair<int, string>>> Hinges = new Dictionary<int, List<KeyValuePair<int, string>>>();
        /// <summary>Slave line -> master line of a line-to-line link.</summary>
        public readonly Dictionary<int, int> LineLinks = new Dictionary<int, int>();
        /// <summary>Slave line -> the link's Ax_Stiffness text, when it is not rigid in every direction.</summary>
        public readonly Dictionary<int, string> LineLinkStiffness = new Dictionary<int, string>();
        /// <summary>The two link lines of every line-to-line link (not curves of their own).</summary>
        public readonly HashSet<int> LinkLines = new HashSet<int>();
        /// <summary>Line of a node-node link -> its Ax_CoupledDOF text.</summary>
        public readonly Dictionary<int, string> NodeLinks = new Dictionary<int, string>();
        /// <summary>Lines in a rigid body.</summary>
        public readonly HashSet<int> RigidLines = new HashSet<int>();

        private LinkExtras(IAxisVMModel model) { this.model = model; }

        public static LinkExtras Read(IAxisVMModel model)
        {
            LinkExtras x = new LinkExtras(model);
            int n = Count(() => model.LineSupports.Count);
            for (int i = 1; i <= n; i++)
            {
                Retry(() =>
                {
                    IAxisVMLineSupport s = model.LineSupports.Item[i];
                    int line = model.LineSupports.get_LineId(i);
                    ELineSupportType t = s.SupportType;
                    if (t == ELineSupportType.lstBeamElasticFoundation || t == ELineSupportType.lstRibElasticFoundation)
                    {
                        RStiffnessesXYZ k = new RStiffnessesXYZ();
                        s.GetStiffnessesXYZ(ref k);
                        x.LineSupport[line] = Numbers(k.x, k.y, k.z, 0, 0, 0);
                    }
                    else
                    {
                        RStiffnesses k = new RStiffnesses();
                        s.GetStiffnesses(ref k);
                        x.LineSupport[line] = Numbers(k.x, k.y, k.z, k.xx, k.yy, k.zz);
                    }
                });
            }

            n = Count(() => model.NodalSupports.Count);
            for (int i = 1; i <= n; i++)
            {
                Retry(() =>
                {
                    IAxisVMNodalSupport s = model.NodalSupports.Item[i];
                    if (s.SupportType != ENodalSupportType.nstNodalGlobal) return;
                    RNodalSupportSpringParams p = s.SpringParams;
                    RSpringParamIndexes ix = p.SpringParamIndexes;
                    x.NodeSupport[s.NodeId] = Numbers(x.K(ix.x), x.K(ix.y), x.K(ix.z), x.K(ix.xx), x.K(ix.yy), x.K(ix.zz));
                });
            }

            n = Count(() => model.EdgeConnections.Count);
            for (int i = 1; i <= n; i++)
            {
                Retry(() =>
                {
                    REdgeConnectionRec_V172 rec = new REdgeConnectionRec_V172();
                    if (model.EdgeConnections.GetRec_V172(i, ref rec) <= 0) return;
                    RSpringParamIndexes ix = rec.SpringParamIndexes;
                    List<KeyValuePair<int, string>> l;
                    if (!x.Hinges.TryGetValue(rec.LineId, out l)) { l = new List<KeyValuePair<int, string>>(); x.Hinges[rec.LineId] = l; }
                    l.Add(new KeyValuePair<int, string>(rec.DomainId, Numbers(x.K(ix.x), x.K(ix.y), x.K(ix.z), x.K(ix.xx), x.K(ix.yy), x.K(ix.zz))));
                });
            }

            n = Count(() => model.LinkElements.Count);
            for (int i = 1; i <= n; i++)
            {
                Retry(() =>
                {
                    RLinkElementRec rec = new RLinkElementRec();
                    if (model.LinkElements.GetRec(i, ref rec) <= 0) return;
                    if (model.LinkElements.get_LinkElementType(i) == ELinkElementType.letLL)
                    {
                        RLLLinkElementRec ll = rec.LLLinkElementRec;
                        x.LineLinks[ll.SlaveLine] = ll.MasterLine;
                        RStiffnesses k = ll.Stiffnesses;
                        double[] v = { k.x, k.y, k.z, k.xx, k.yy, k.zz };
                        if (Array.Exists(v, d => d < AttributeParser.RigidThreshold)) x.LineLinkStiffness[ll.SlaveLine] = Numbers(v);
                        x.LinkLines.Add(ll.MasterStartLink);
                        x.LinkLines.Add(ll.MasterEndLink);
                    }
                    else
                    {
                        RNNLinkElementRec nn = rec.NNLinkElementRec;
                        RStiffnesses k = nn.Stiffnesses;
                        x.NodeLinks[nn.LineId] = Coupled(k.x, k.y, k.z, k.xx, k.yy, k.zz);
                    }
                });
            }

            n = Count(() => model.RigidBodies.Count);
            for (int i = 1; i <= n; i++)
            {
                Retry(() =>
                {
                    Array lines;
                    model.RigidBodies.GetLines(i, out lines);
                    foreach (int l in LinkState.IntList(lines)) x.RigidLines.Add(l);
                });
            }
            return x;
        }

        /// <summary>
        /// Runs one record read, again (up to four times) when it throws: an out-of-process AxisVM rejects a call now and
        /// then while it is busy, and a record silently skipped here would look deleted in AxisVM, or new on the next Send.
        /// </summary>
        private static void Retry(Action read)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { read(); return; }
                catch
                {
                    if (attempt >= 3) return;
                    System.Threading.Thread.Sleep(50);
                }
            }
        }

        /// <summary>The stiffness of a spring characteristic (0 = none / free).</summary>
        public double K(int index)
        {
            if (index <= 0) return 0;
            double k;
            if (springK.TryGetValue(index, out k)) return k;
            k = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try { k = model.SpringParams.Item[index].FullRec_V161.K; break; }
                catch { System.Threading.Thread.Sleep(50); }
            }
            springK[index] = k;
            return k;
        }

        // ==================================================================
        // Per element
        // ==================================================================

        /// <summary>
        /// The extras of a curve made of <paramref name="lines"/>, its nodes in curve order (<paramref name="nodes"/>,
        /// first = Rhino start): Ax_Support, Ax_Release, Ax_LineLink (with <paramref name="guidOfLine"/> naming the master
        /// curve), and for springs / node-node links their Ax_Stiffness / Ax_CoupledDOF.
        /// </summary>
        public Dictionary<string, string> Curve(List<int> lines, List<int> nodes, Func<int, string> guidOfLine)
        {
            var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (int l in lines)
            {
                string s;
                if (LineSupport.TryGetValue(l, out s)) { a[AxKeys.Support] = s; break; }
            }
            if (nodes != null && nodes.Count >= 2)
            {
                string rel = Release(lines, nodes[0], nodes[nodes.Count - 1]);
                if (rel != null) a[AxKeys.Release] = rel;
            }
            foreach (int l in lines)
            {
                int master;
                if (!LineLinks.TryGetValue(l, out master)) continue;
                string g = guidOfLine(master);
                if (!string.IsNullOrEmpty(g)) a[AxKeys.LineLink] = g;
                string k;
                if (LineLinkStiffness.TryGetValue(l, out k)) a[AxKeys.Stiffness] = k;
                break;
            }
            if (lines.Count == 1)
            {
                string c;
                if (NodeLinks.TryGetValue(lines[0], out c)) a[AxKeys.CoupledDof] = c;
                string sp = SpringText(lines[0]);
                if (sp != null) a[AxKeys.Stiffness] = sp;
            }
            return a;
        }

        /// <summary>The extras of an edge hinge curve: Ax_Stiffness and the domains it releases (Ax_HingeDomains), from AxisVM's hinges along it.</summary>
        public Dictionary<string, string> Hinge(List<int> lines, Func<int, string> domainGuid, List<string> only = null)
        {
            var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SortedSet<string> domains = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> named = only != null && only.Count > 0 ? new HashSet<string>(only, StringComparer.OrdinalIgnoreCase) : null;
            foreach (int l in lines)
            {
                List<KeyValuePair<int, string>> hs;
                if (!Hinges.TryGetValue(l, out hs)) continue;
                foreach (KeyValuePair<int, string> h in hs)
                {
                    string g = domainGuid(h.Key);
                    // A polysurface may be named by its plain id: its faces are "id_f0", "id_f1"...
                    if (named != null && (g == null || !(named.Contains(g) || named.Contains(LinkFile.BaseGuid(g))))) continue;
                    if (!a.ContainsKey(AxKeys.Stiffness)) a[AxKeys.Stiffness] = h.Value;
                    if (!string.IsNullOrEmpty(g)) domains.Add(g);
                }
            }
            if (domains.Count > 0) a[AxKeys.HingeDomains] = string.Join(",", domains);
            return a;
        }

        /// <summary>Ax_Support of a node (null = none).</summary>
        public string Node(int node)
        {
            string s;
            return NodeSupport.TryGetValue(node, out s) ? s : null;
        }

        /// <summary>Ax_SurfaceSupport of a domain (null = none).</summary>
        public string Domain(int domain)
        {
            try
            {
                IAxisVMDomain d = model.Domains.Item[domain];
                if (d.ElasticFoundationExists != ELongBoolean.lbTrue) return null;
                RStiffnessesXYZ k = new RStiffnessesXYZ();
                RNonLinearityXYZ nl = new RNonLinearityXYZ();
                RResistancesXYZ rs = new RResistancesXYZ();
                d.GetElasticFoundation(ref k, ref nl, ref rs);
                return k.x == 0 && k.y == 0 ? Num(k.z) : Numbers(k.x, k.y, k.z);
            }
            catch { return null; }
        }

        /// <summary>
        /// Ax_Release at the curve's two far ends (interior split points stay rigid), "start=...; end=..." with 1E+10 for
        /// rigid and 0 for hinged; null when both ends are fully rigid. A line's StartRelease sits at its lower-index node
        /// (as AxisModelBuilder.SetEndRelease writes it).
        /// </summary>
        private string Release(List<int> lines, int startNode, int endNode)
        {
            string s = EndRelease(lines, startNode), e = EndRelease(lines, endNode);
            if (s == null && e == null) return null;
            List<string> parts = new List<string>();
            if (s != null) parts.Add("start=" + s);
            if (e != null) parts.Add("end=" + e);
            return string.Join("; ", parts);
        }

        private string EndRelease(List<int> lines, int node)
        {
            foreach (int l in lines)
            {
                int[] ends = LinkState.LineNodes(model, l);
                if (ends == null || (ends[0] != node && ends[1] != node)) continue;
                RLineAttr_V161 a;
                if (!TryAttrV161(l, out a)) return null;
                RReleases_V161 r = node == Math.Min(ends[0], ends[1]) ? a.StartRelease : a.EndRelease;
                double[] v = { Dof(r.x), Dof(r.y), Dof(r.z), Dof(r.xx), Dof(r.yy), Dof(r.zz) };
                bool rigid = true;
                foreach (double d in v) if (d < AttributeParser.RigidThreshold) rigid = false;
                return rigid ? null : Numbers(v);
            }
            return null;
        }

        private double Dof(RRelease_V161 r)
        {
            if (r.ReleaseType == EReleaseType.rtHinged) return 0;
            if (r.ReleaseType == EReleaseType.rtSemiRigid) return K(r.FunctionId);
            return AttributeParser.RigidStiffness;
        }

        /// <summary>Ax_Stiffness of a spring element line (null = not a spring).</summary>
        private string SpringText(int line)
        {
            RLineAttr_V161 a;
            if (!TryAttrV161(line, out a) || a.LineType != ELineType.ltSpring) return null;
            RSpringCharacteristics c = a.SpringCharacteristics;
            return Numbers(K(c.x), K(c.y), K(c.z), K(c.xx), K(c.yy), K(c.zz));
        }

        private bool TryAttrV161(int line, out RLineAttr_V161 attr)
        {
            attr = new RLineAttr_V161();
            try
            {
                Array ids = Array.CreateInstance(typeof(int), new[] { 1 }, new[] { 1 });
                ids.SetValue(line, 1);
                Array got;
                if (model.Lines.BulkGetAttr_V161(ids, out got) < 1 || got == null || got.Length < 1) return false;
                object raw = got.GetValue(got.GetLowerBound(0));
                if (!(raw is RLineAttr_V161)) return false;
                attr = (RLineAttr_V161)raw;
                return true;
            }
            catch { return false; }
        }

        // ==================================================================
        // Text
        // ==================================================================

        /// <summary>The baseline form of some extras: sorted "key=value" lines.</summary>
        public static string Text(Dictionary<string, string> extras)
        {
            List<string> l = new List<string>();
            foreach (KeyValuePair<string, string> kv in extras) l.Add(kv.Key + "=" + kv.Value);
            l.Sort(StringComparer.Ordinal);
            return string.Join("\n", l);
        }

        public static Dictionary<string, string> Parse(string text)
        {
            var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return a;
            foreach (string line in text.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) a[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return a;
        }

        /// <summary>What to send for an element: every key whose value differs from the baseline ("" = removed in AxisVM).</summary>
        public static Dictionary<string, string> Changes(Dictionary<string, string> now, Dictionary<string, string> baseline)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in now)
            {
                string b;
                if (!baseline.TryGetValue(kv.Key, out b) || b != kv.Value) d[kv.Key] = kv.Value;
            }
            foreach (string k in baseline.Keys) if (!now.ContainsKey(k)) d[k] = "";
            return d;
        }

        private static string Coupled(params double[] k)
        {
            string[] t = new string[k.Length];
            for (int i = 0; i < k.Length; i++) t[i] = k[i] >= AttributeParser.RigidThreshold ? "1" : k[i] <= 0 ? "0" : Num(k[i]);
            return string.Join(",", t);
        }

        public static string Numbers(params double[] v)
        {
            string[] t = new string[v.Length];
            for (int i = 0; i < v.Length; i++) t[i] = Num(v[i]);
            return string.Join(",", t);
        }

        private static string Num(double v)
        {
            if (Math.Abs(v) < 1e-12) return "0";
            return v.ToString("G6", CultureInfo.InvariantCulture);
        }

        private static int Count(Func<int> f) { try { return f(); } catch { return 0; } }
    }
}
