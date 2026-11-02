using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>What one "Send to Rhino" wrote.</summary>
    public class SendResult
    {
        public int Changed;
        public int Unchanged;
        public int Adopted;
        public int Deleted;
        /// <summary>Materials, cross-sections, load cases and load combinations in the library that was sent.</summary>
        public int[] Library = new int[4];
        /// <summary>Library entries Rhino does not have yet.</summary>
        public int LibraryNew;
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// "Send to Rhino": writes what was changed in AxisVM into the "axisvm" section of the live link file.
    ///
    /// For every entity standing for a Rhino object (RhinoGuid, from an import of this link) the current
    /// state is compared with its RhinoState baseline: moved geometry is sent as the rebuilt curve /
    /// outline / point, and of the properties only those that differ (Ax_Type, Ax_Section, Ax_Material,
    /// Ax_FEType, Ax_Thickness). Unchanged entities are listed too (changed:false), and "deleted" lists the
    /// objects the last "Load from Rhino" built that are gone, so Rhino can tell what was removed.
    ///
    /// Beams, ribs, trusses and domains drawn in AxisVM get a new id and are "adopted" (RhinoAdopted): sent
    /// with all their properties and "new": true (Rhino creates only those, so a curve deleted in Rhino is
    /// never brought back), and replaced by a later "Load from Rhino" only once Rhino holds them.
    ///
    /// The baseline is NOT refreshed here: until AxisVM loads Rhino's model again, every send carries every
    /// change since then, so sending twice before Rhino has loaded cannot drop the first send's changes.
    /// Coordinates and Ax_Thickness are in metres ("unitSystem": "Meters"); Rhino scales them.
    /// </summary>
    public static class RhinoLinkSender
    {
        private const double Tol = 1e-6;

        public static SendResult Send(IAxisVMModel model, string linkPath)
        {
            SendResult res = new SendResult();
            string key = AxisModelBuilder.LinkKey(linkPath);

            JsonValue current = File.Exists(linkPath) && new FileInfo(linkPath).Length > 0 ? LinkFile.Read(linkPath) : null;
            HashSet<string> rhinoKnows = LinkFile.RhinoKnownGuids(current);
            JsonValue oldSection = LinkFile.IsLink(current) ? current[LinkFile.AxisVmSection] : null;
            Dictionary<string, double[]> rhinoStarts = RhinoLineStarts(current);

            JsonValue section = JsonValue.Obj()
                .Set("sent", JsonValue.Str(LinkFile.Now()))
                .Set("basedOnRhino", JsonValue.Str(LinkFile.Get(oldSection, "basedOnRhino")))
                .Set("built", oldSection != null && oldSection["built"] != null ? oldSection["built"] : JsonValue.Arr())
                .Set("segments", oldSection != null && oldSection["segments"] != null ? oldSection["segments"] : JsonValue.Obj())
                .Set("panelIds", oldSection != null && oldSection["panelIds"] != null ? oldSection["panelIds"] : JsonValue.Obj())
                .Set("hingeIds", oldSection != null && oldSection["hingeIds"] != null ? oldSection["hingeIds"] : JsonValue.Obj())
                .Set("unitSystem", JsonValue.Str("Meters"))
                .Set("model", JsonValue.Str(SafeFileName(model)));

            Context ctx = new Context { Model = model, Key = key, RhinoKnows = rhinoKnows, Section = section, Res = res, Loads = LinkLoads.Read(model) };
            ctx.ReadMembers();
            ctx.Extras = LinkExtras.Read(model);
            ctx.RhinoTypes = RhinoTypes(current);
            ctx.RhinoHingeDomains = RhinoLineAttr(current, AxKeys.HingeDomains);
            ctx.RhinoHingeSegs = RhinoHingeSegs(current);

            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            LinkState.BeginCache(model);
            try
            {
                ctx.ReadDiaphragms(RhinoDiaphragms(current));
                JsonValue lines = Lines(ctx, rhinoStarts, present);
                section.Set("lines", lines);
                JsonValue domains = Domains(model, key, rhinoKnows, present, section, res, ctx);
                Panels(ctx, present, domains);
                Hinges(ctx, present, lines);
                section.Set("domains", domains);
                section.Set("points", Points(ctx, present));
            }
            finally { LinkState.EndCache(); }

            JsonValue library = LinkLibrary.Read(model, out res.Library);
            res.LibraryNew = LinkLibrary.CountNew(library, LinkFile.IsLink(current) ? current[LinkFile.RhinoSection] : current);
            section.Set("library", library).Set("libraryNew", JsonValue.Num(res.LibraryNew));

            JsonValue deleted = JsonValue.Arr();
            JsonValue built = section["built"];
            for (int i = 0; built != null && i < built.Count; i++)
            {
                string g = built.At(i).AsString("");
                if (g.Length > 0 && !present.Contains(g)) { deleted.Add(JsonValue.Str(g)); res.Deleted++; }
            }
            section.Set("deleted", deleted);
            KeepSignatures(oldSection, section);

            foreach (KeyValuePair<string, int> kv in ctx.Loads.NotSent)
                res.Notes.Add(string.Format("{0} load(s) of type '{1}' are not sent to Rhino.", kv.Value, kv.Key));
            if (ctx.Loads.Averaged > 0)
                res.Notes.Add(ctx.Loads.Averaged + " load panel load(s) vary over the panel; Rhino gets their average intensity.");
            res.Notes.Add("Sent besides geometry, types, sections and materials: supports, elastic foundations, releases, springs, node-node and " +
                          "line-to-line links, rigid bodies, edge hinges and surface supports made or changed in AxisVM; loads of elements drawn in AxisVM.");
            JsonValue notes = JsonValue.Arr();
            foreach (string n in res.Notes) notes.Add(JsonValue.Str(n));
            section.Set("notes", notes);

            LinkFile.Update(linkPath, root => root.Set(LinkFile.AxisVmSection, section));
            return res;
        }

        /// <summary>
        /// The signatures "only changes" compares with (LinkDiff), carried across a send: an element drawn in AxisVM gets
        /// the signature of what was sent for it - what Rhino will echo back - so the next load keeps it instead of
        /// rebuilding it; an element changed or deleted in AxisVM loses its signature, so the next load rebuilds it from
        /// Rhino's version (which by then holds the change).
        /// </summary>
        private static void KeepSignatures(JsonValue oldSection, JsonValue section)
        {
            JsonValue sigs = JsonValue.Obj();
            JsonValue old = oldSection == null ? null : oldSection["sigs"];
            if (old != null && old.ObjectValue != null) foreach (KeyValuePair<string, JsonValue> kv in old.ObjectValue) sigs.Set(kv.Key, kv.Value);
            foreach (string list in new[] { "points", "lines", "domains" })
            {
                JsonValue arr = section[list];
                for (int i = 0; arr != null && i < arr.Count; i++)
                {
                    JsonValue e = arr.At(i);
                    string guid = LinkFile.Get(e, "guid");
                    if (guid.Length == 0 || e["changed"] == null || !e["changed"].BoolValue) continue;
                    if (e["new"] != null && e["new"].BoolValue) sigs.Set(guid, JsonValue.Str(LinkSignature.Of(e, list, 1.0)));
                    else if (sigs.ObjectValue.ContainsKey(guid)) sigs.ObjectValue.Remove(guid);
                }
            }
            JsonValue deleted = section["deleted"];
            for (int i = 0; deleted != null && i < deleted.Count; i++) sigs.ObjectValue.Remove(deleted.At(i).AsString(""));
            section.Set("sigs", sigs);
            foreach (string k in new[] { "sigSettings", "panelUids" })
                if (oldSection != null && oldSection[k] != null) section.Set(k, oldSection[k]);
        }

        /// <summary>
        /// The signatures, their settings, the load panel ids and what was built, as recorded by the last load or send
        /// (LinkDiff input). <paramref name="built"/> is null when the link file does not say.
        /// </summary>
        public static void ReadRecord(JsonValue root, out Dictionary<string, string> sigs, out string settings, out Dictionary<int, string> panelGuids,
                                      out HashSet<string> built)
        {
            sigs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            panelGuids = new Dictionary<int, string>();
            settings = "";
            built = null;
            JsonValue sec = LinkFile.IsLink(root) ? root[LinkFile.AxisVmSection] : null;
            if (sec == null) return;
            JsonValue b = sec["built"];
            if (b != null && b.Type == JsonValue.JsonType.Array)
            {
                built = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < b.Count; i++) built.Add(b.At(i).AsString(""));
                JsonValue ids = sec["panelIds"];
                if (ids != null && ids.ObjectValue != null) foreach (JsonValue v in ids.ObjectValue.Values) built.Add(v.AsString(""));
                ids = sec["panelUids"];
                if (ids != null && ids.ObjectValue != null) foreach (JsonValue v in ids.ObjectValue.Values) built.Add(v.AsString(""));
            }
            settings = LinkFile.Get(sec, "sigSettings");
            JsonValue s = sec["sigs"];
            if (s != null && s.ObjectValue != null) foreach (KeyValuePair<string, JsonValue> kv in s.ObjectValue) sigs[kv.Key] = kv.Value.AsString("");
            JsonValue p = sec["panelUids"];
            int uid;
            if (p != null && p.ObjectValue != null)
                foreach (KeyValuePair<string, JsonValue> kv in p.ObjectValue)
                    if (int.TryParse(kv.Key, out uid)) panelGuids[uid] = kv.Value.AsString("");
        }

        /// <summary>
        /// After "Load from Rhino": AxisVM is in line with that Rhino model. The axisvm section is reset
        /// (no changes) and lists what was built, so a later send can tell what was deleted in AxisVM. With
        /// <paramref name="sigs"/> it also records every Rhino object's signature, the settings they were read with and
        /// which load panel stands for which Rhino surface - what the next "only changes" load compares with.
        /// </summary>
        public static void MarkLoaded(IAxisVMModel model, string linkPath, string rhinoSent,
                                      Dictionary<string, string> sigs = null, string settings = null, Dictionary<int, string> panelGuids = null,
                                      HashSet<string> rebuilt = null, bool baselines = true)
        {
            string key = AxisModelBuilder.LinkKey(linkPath);
            if (baselines && (rebuilt == null || rebuilt.Count > 0))
            {
                try { WriteBaselines(model, key, File.Exists(linkPath) ? LinkFile.Read(linkPath) : null, rebuilt); } catch { }
            }
            HashSet<string> built = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> segments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            CollectGuids(model, key, built, segments);

            JsonValue arr = JsonValue.Arr();
            foreach (string g in built) arr.Add(JsonValue.Str(g));
            JsonValue segs = JsonValue.Obj();
            foreach (KeyValuePair<string, int> kv in segments) segs.Set(kv.Key, JsonValue.Num(kv.Value));
            JsonValue section = JsonValue.Obj()
                .Set("sent", JsonValue.Str(""))
                .Set("basedOnRhino", JsonValue.Str(rhinoSent ?? ""))
                .Set("built", arr)
                .Set("segments", segs)
                .Set("panelIds", PanelIds(linkPath))
                .Set("unitSystem", JsonValue.Str("Meters"))
                .Set("model", JsonValue.Str(SafeFileName(model)))
                .Set("lines", JsonValue.Arr()).Set("domains", JsonValue.Arr()).Set("points", JsonValue.Arr())
                .Set("deleted", JsonValue.Arr()).Set("notes", JsonValue.Arr());
            if (sigs != null)
            {
                JsonValue s = JsonValue.Obj();
                foreach (KeyValuePair<string, string> kv in sigs) s.Set(kv.Key, JsonValue.Str(kv.Value));
                JsonValue p = JsonValue.Obj();
                if (panelGuids != null) foreach (KeyValuePair<int, string> kv in panelGuids) p.Set(kv.Key.ToString(CultureInfo.InvariantCulture), JsonValue.Str(kv.Value));
                section.Set("sigs", s).Set("sigSettings", JsonValue.Str(settings ?? "")).Set("panelUids", p);
            }
            LinkFile.Update(linkPath, root => root.Set(LinkFile.AxisVmSection, section));
        }

        /// <summary>
        /// The extras baseline (supports, releases, springs, links, hinges - see LinkExtras) of what a load built: AxisVM's
        /// state of them right after the load, which "Send to Rhino" compares with. Only for <paramref name="only"/> (the
        /// rebuilt objects; null = all): a kept element keeps its baseline, so an AxisVM edit made to it stays pending.
        /// </summary>
        private static void WriteBaselines(IAxisVMModel model, string key, JsonValue root, HashSet<string> only)
        {
            Context ctx = new Context { Model = model, Key = key, Extras = LinkExtras.Read(model), RhinoTypes = RhinoTypes(root), RhinoHingeDomains = RhinoLineAttr(root, AxKeys.HingeDomains) };
            Dictionary<string, double[]> starts = RhinoLineStarts(root);
            AxisVMAttachments la = model.Lines.Attachments, da = model.Domains.Attachments, na = model.Nodes.Attachments;
            var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            LinkState.BeginCache(model);
            try
            {
                int n = model.Lines.Count;
                for (int i = 1; i <= n; i++)
                {
                    if (!Ours(la, i, key)) continue;
                    foreach (string g in LinkState.Guids(la, i))
                    {
                        if (g.IndexOf('#') > 0) continue;
                        if (!ctx.LineGuid.ContainsKey(i)) ctx.LineGuid[i] = g;
                        if (only == null || only.Contains(g)) Group(groups, g, i);
                    }
                }
                foreach (KeyValuePair<string, List<int>> kv in groups)
                {
                    double[] start;
                    starts.TryGetValue(kv.Key, out start);
                    WriteBaseline(la, kv.Value, kv.Key, LinkExtras.Text(CurveExtras(ctx, kv.Key, kv.Value, CurveNodes(model, kv.Value, start))));
                }
                for (int i = 1; i <= model.Domains.Count; i++)
                {
                    if (!Ours(da, i, key)) continue;
                    foreach (string g in LinkState.Guids(da, i))
                        if (only == null || only.Contains(g)) WriteBaseline(da, new[] { i }, g, LinkExtras.Text(DomainExtras(ctx.Extras, i)));
                }
                for (int i = 1; i <= model.Nodes.Count; i++)
                {
                    if (!Ours(na, i, key)) continue;
                    foreach (string g in LinkState.Guids(na, i))
                        if (only == null || only.Contains(g)) WriteBaseline(na, new[] { i }, g, LinkExtras.Text(NodeExtras(ctx.Extras, i)));
                }
            }
            finally { LinkState.EndCache(); }
        }

        /// <summary>The polygon load panel ids recorded by earlier sends (kept across a load: the panels Rhino has not loaded yet still need them).</summary>
        private static JsonValue PanelIds(string linkPath)
        {
            try
            {
                JsonValue root = File.Exists(linkPath) ? LinkFile.Read(linkPath) : null;
                JsonValue sec = LinkFile.IsLink(root) ? root[LinkFile.AxisVmSection] : null;
                if (sec != null && sec["panelIds"] != null) return sec["panelIds"];
            }
            catch { }
            return JsonValue.Obj();
        }

        /// <summary>The "panelIds" map of the link file as outline state -> guid, for AxisModelBuilder (ImportOptions.AdoptedPanels).</summary>
        public static Dictionary<string, string> AdoptedPanels(JsonValue root)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            JsonValue sec = LinkFile.IsLink(root) ? root[LinkFile.AxisVmSection] : null;
            JsonValue ids = sec == null ? null : sec["panelIds"];
            if (ids != null && ids.Type == JsonValue.JsonType.Object)
                foreach (KeyValuePair<string, JsonValue> kv in ids.ObjectValue) map[kv.Key] = kv.Value.AsString("");
            return map;
        }

        /// <summary>Everything one send needs: the model, the link, its loads and members.</summary>
        private class Context
        {
            public IAxisVMModel Model;
            public string Key;
            public HashSet<string> RhinoKnows;
            public JsonValue Section;
            public SendResult Res;
            public LinkLoads Loads;
            public LinkExtras Extras;
            /// <summary>Ax_Type of every Rhino curve in the rhino section (to tell Rhino's edge hinge curves).</summary>
            public Dictionary<string, string> RhinoTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Ax_HingeDomains of the Rhino curves that name their domains.</summary>
            public Dictionary<string, string> RhinoHingeDomains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>The Rhino edge hinge curves by geometry (see <see cref="AlongRhinoHinge"/>).</summary>
            public List<HingeSeg> RhinoHingeSegs = new List<HingeSeg>();
            /// <summary>Line -> the curve it belongs to (filled while the curves are sent).</summary>
            public readonly Dictionary<int, string> LineGuid = new Dictionary<int, string>();

            public string DomainGuid(int domain)
            {
                try { List<string> g = LinkState.Guids(Model.Domains.Attachments, domain); return g.Count > 0 ? g[0] : null; }
                catch { return null; }
            }
            /// <summary>Line -> structural member; member -> its lines.</summary>
            public readonly Dictionary<int, int> LineMember = new Dictionary<int, int>();
            public readonly Dictionary<int, List<int>> MemberLines = new Dictionary<int, List<int>>();
            /// <summary>Entity guid -> the member (or single line) an adopted curve stands for, for its loads and direction.</summary>
            public readonly Dictionary<string, int> GuidMember = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> GuidLine = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public void ReadMembers()
            {
                int n;
                try { n = Model.Members.Count; } catch { return; }
                if (n <= 0) return;
                Array ids = Array.CreateInstance(typeof(int), new[] { n }, new[] { 1 });
                for (int i = 1; i <= n; i++) ids.SetValue(i, i);
                try
                {
                    Array counts, lines;
                    Model.Members.BulkGetLines(ids, out counts, out lines);
                    List<int> c = LinkState.IntList(counts), l = LinkState.IntList(lines);
                    int at = 0;
                    for (int m = 1; m <= n && m - 1 < c.Count; m++)
                    {
                        List<int> own = new List<int>();
                        for (int k = 0; k < c[m - 1] && at < l.Count; k++, at++) { own.Add(l[at]); LineMember[l[at]] = m; }
                        MemberLines[m] = own;
                    }
                }
                catch { }
            }

            /// <summary>Rigid diaphragms: line -> its diaphragm's name, domain -> the diaphragm all its edges are in.</summary>
            public readonly Dictionary<int, string> LineDiaphragm = new Dictionary<int, string>();
            public readonly Dictionary<int, string> DomainDiaphragm = new Dictionary<int, string>();
            /// <summary>Lines whose diaphragm is already said by their domain's Ax_Diaphragm (not repeated on curves along them).</summary>
            public readonly HashSet<int> DiaphragmByDomain = new HashSet<int>();
            /// <summary>Ax_Diaphragm as Rhino holds it now, per Rhino id.</summary>
            public Dictionary<string, string> RhinoDiaphragm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Reads AxisVM's diaphragms. Each keeps the name Rhino gave it (the Ax_Diaphragm most of its lines' and
            /// domains' Rhino objects carry); one drawn in AxisVM is called D1, D2 ... by its index.
            /// </summary>
            public void ReadDiaphragms(Dictionary<string, string> rhinoNames)
            {
                RhinoDiaphragm = rhinoNames;
                int n;
                try { n = Model.Diaphragm.Count; } catch { return; }
                if (n <= 0) return;

                var lineDia = new Dictionary<int, int>();
                for (int d = 1; d <= n; d++)
                {
                    try
                    {
                        Array arr;
                        Model.Diaphragm.GetLines(d, out arr);
                        foreach (int l in LinkState.IntList(arr)) lineDia[l] = d;
                    }
                    catch { }
                }

                AxisVMAttachments la = Model.Lines.Attachments, da = Model.Domains.Attachments;
                var votes = new Dictionary<int, Dictionary<string, int>>();
                Action<int, string> vote = (d, guid) =>
                {
                    string v;
                    if (!rhinoNames.TryGetValue(guid, out v) || v.Length == 0) return;
                    Dictionary<string, int> c;
                    if (!votes.TryGetValue(d, out c)) { c = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); votes[d] = c; }
                    int k; c.TryGetValue(v, out k); c[v] = k + 1;
                };
                foreach (KeyValuePair<int, int> kv in lineDia)
                    foreach (string g in LinkState.Guids(la, kv.Key)) vote(kv.Value, g);

                var domainDia = new Dictionary<int, int>();
                int nd = 0;
                try { nd = Model.Domains.Count; } catch { }
                for (int i = 1; i <= nd; i++)
                {
                    List<int> edges = LinkState.ContourLines(Model, i);
                    foreach (List<int> h in LinkState.HoleLines(Model, i)) edges.AddRange(h);
                    int d = 0;
                    foreach (int l in edges)
                    {
                        int x;
                        if (!lineDia.TryGetValue(l, out x) || (d != 0 && x != d)) { d = -1; break; }
                        d = x;
                    }
                    if (d <= 0) continue;
                    domainDia[i] = d;
                    foreach (string g in LinkState.Guids(da, i)) vote(d, g);
                }

                var names = new Dictionary<int, string>();
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int d = 1; d <= n; d++)
                {
                    string best = null;
                    int most = 0;
                    Dictionary<string, int> c;
                    // A Rhino name may name several AxisVM diaphragms: AxisVM splits one into its connected parts.
                    if (votes.TryGetValue(d, out c))
                        foreach (KeyValuePair<string, int> kv in c) if (kv.Value > most) { most = kv.Value; best = kv.Key; }
                    if (best == null) { int k = d; do best = "D" + k++; while (taken.Contains(best) || rhinoNames.ContainsValue(best)); }
                    taken.Add(best);
                    names[d] = best;
                }

                foreach (KeyValuePair<int, int> kv in lineDia) LineDiaphragm[kv.Key] = names[kv.Value];
                foreach (KeyValuePair<int, int> kv in domainDia)
                {
                    DomainDiaphragm[kv.Key] = names[kv.Value];
                    List<int> edges = LinkState.ContourLines(Model, kv.Key);
                    foreach (List<int> h in LinkState.HoleLines(Model, kv.Key)) edges.AddRange(h);
                    DiaphragmByDomain.UnionWith(edges);
                }
            }

            /// <summary>
            /// The Ax_Diaphragm to send for a curve or domain, or null when Rhino already has it right. <paramref name="names"/>
            /// are the diaphragms the entity's lines are in (Rhino's name counts as right if any of them has it),
            /// <paramref name="want"/> the one to state ("" = none).
            /// </summary>
            public string DiaphragmChange(string guid, ICollection<string> names, string want)
            {
                string had;
                if (!RhinoDiaphragm.TryGetValue(guid, out had)) had = "";
                if (had.Length > 0 && names.Contains(had)) return null;
                return string.Equals(want, had, StringComparison.OrdinalIgnoreCase) ? null : want;
            }
        }

        /// <summary>Ax_Diaphragm of every Rhino curve and surface in the link file's rhino section.</summary>
        private static Dictionary<string, string> RhinoDiaphragms(JsonValue root)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JsonValue r;
            try { r = root == null ? null : LinkFile.RhinoPart(root); } catch { return map; }
            if (r == null) return map;
            foreach (string part in new[] { "lines", "domains" })
            {
                JsonValue arr = r[part];
                for (int i = 0; arr != null && i < arr.Count; i++)
                {
                    string v = LinkFile.Get(arr.At(i)["attrs"], AxKeys.Diaphragm);
                    if (!string.IsNullOrEmpty(v)) map[LinkFile.Get(arr.At(i), "guid")] = v.Trim();
                }
            }
            return map;
        }

        /// <summary>Every Rhino id the link's entities stand for, and how many AxisVM lines each curve became.</summary>
        private static void CollectGuids(IAxisVMModel model, string key, HashSet<string> into, Dictionary<string, int> lineSegments)
        {
            Action<AxisVMAttachments, int, Dictionary<string, int>> take = (a, n, count) =>
            {
                for (int i = 1; i <= n; i++)
                {
                    if (!Ours(a, i, key)) continue;
                    foreach (string g in LinkState.Guids(a, i))
                    {
                        if (g.IndexOf('#') > 0) continue;   // a line-to-line link's link line
                        into.Add(g);
                        if (count == null) continue;
                        int c;
                        count.TryGetValue(g, out c);
                        count[g] = c + 1;
                    }
                }
            };
            try { take(model.Lines.Attachments, model.Lines.Count, lineSegments); } catch { }
            try { take(model.Domains.Attachments, model.Domains.Count, null); } catch { }
            try { take(model.Nodes.Attachments, model.Nodes.Count, null); } catch { }
        }

        /// <summary>Imported from this link, or adopted into it.</summary>
        private static bool Ours(AxisVMAttachments a, int i, string key)
        {
            if (string.Equals(LinkState.Read(a, LinkState.Link, i), key, StringComparison.Ordinal)) return true;
            List<string> owners;
            return string.Equals(LinkState.AdoptedKey(LinkState.Read(a, LinkState.Adopted, i), out owners), key, StringComparison.Ordinal);
        }

        private static bool Native(AxisVMAttachments a, int i)
        {
            return LinkState.Read(a, LinkState.Link, i) == null && LinkState.Read(a, LinkState.Adopted, i) == null && LinkState.Read(a, LinkState.Guid, i) == null;
        }

        // ==================================================================
        // Lines
        // ==================================================================

        private static JsonValue Lines(Context ctx, Dictionary<string, double[]> rhinoStarts, HashSet<string> present)
        {
            IAxisVMModel model = ctx.Model;
            string key = ctx.Key;
            AxisVMAttachments la = model.Lines.Attachments;
            Dictionary<string, List<int>> groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> adoptedMembers = new HashSet<int>();
            HashSet<string> pendingHinges = PendingHinges(ctx);
            HashSet<string> hingeCurves = HingeCurves(ctx);
            int n = model.Lines.Count;

            for (int i = 1; i <= n; i++)
            {
                List<string> owners = LinkState.Guids(la, i);
                // The link lines of a line-to-line link ("guid#link") belong to the link, not to a curve's shape;
                // edge hinge curves Rhino has not loaded yet are sent by Hinges().
                if (owners.Count > 0 && owners.TrueForAll(o => o.IndexOf('#') > 0)) continue;
                owners.RemoveAll(o => o.IndexOf('#') > 0 || pendingHinges.Contains(o));
                if (owners.Count == 0 && LinkState.Read(la, LinkState.Guid, i) != null) continue;
                if (owners.Count > 0)
                {
                    if (!Ours(la, i, key)) continue;
                    List<string> adoptedOwners;
                    bool adopted = LinkState.AdoptedKey(LinkState.Read(la, LinkState.Adopted, i), out adoptedOwners) != null;
                    foreach (string g in owners)
                    {
                        Group(groups, g, i);
                        if (!ctx.LineGuid.ContainsKey(i)) ctx.LineGuid[i] = g;
                        // Adopted per owner: a Rhino curve sharing a line with an adopted one is still Rhino's. An edge hinge
                        // curve adopted from AxisVM is not a member: the member loads on its line are the member's.
                        bool adoptedG = adopted && adoptedOwners.Contains(g) && !hingeCurves.Contains(g);
                        if (adopted && adoptedOwners.Contains(g) && !ctx.RhinoKnows.Contains(g)) full.Add(g);
                        if (adoptedG && !ctx.RhinoKnows.Contains(g)) full.Add(g);
                        int member;
                        if (adoptedG && ctx.LineMember.TryGetValue(i, out member)) ctx.GuidMember[g] = member;
                        else if (adoptedG) ctx.GuidLine[g] = i;
                    }
                    continue;
                }

                // Drawn in AxisVM: only typed members (beams, ribs, trusses, springs, node-node links) and rigid bodies
                // become Rhino curves (plain lines are slab edges & co.), one curve per structural member, so a member
                // split by its supports stays one curve with its loads. Link lines of line-to-line links are not curves.
                if (!Native(la, i) || ctx.Extras.LinkLines.Contains(i)) continue;
                RLineAttr attr;
                if (!LinkState.TryLineAttr(model, i, out attr)) continue;
                if (LinkState.LineTypeName(attr.LineType).Length == 0 && !ctx.Extras.RigidLines.Contains(i)) continue;

                int m;
                List<int> lines;
                if (ctx.LineMember.TryGetValue(i, out m) && ctx.MemberLines.TryGetValue(m, out lines) && lines.Count > 0)
                {
                    if (!adoptedMembers.Add(m)) continue;
                    lines = lines.FindAll(l => Native(la, l));
                    if (lines.Count == 0) continue;
                }
                else { m = 0; lines = new List<int> { i }; }

                string guid = System.Guid.NewGuid().ToString();
                foreach (int l in lines)
                {
                    LinkState.Write(la, LinkState.Guid, l, guid);
                    LinkState.Write(la, LinkState.Adopted, l, key + "|" + guid);
                    LinkState.Write(la, LinkState.State, l, LinkState.LineState(model, l));
                    Group(groups, guid, l);
                    ctx.LineGuid[l] = guid;
                }
                if (m > 0) ctx.GuidMember[guid] = m; else ctx.GuidLine[guid] = i;
                LinkState.Write(la, LoadsAttachment, lines[0], LoadsHash(CurveLoads(ctx, guid, lines)));
                full.Add(guid);
                Built(ctx.Section, guid, lines.Count);
                ctx.Res.Adopted++;
            }

            JsonValue arr = JsonValue.Arr();
            foreach (KeyValuePair<string, List<int>> kv in groups)
            {
                string guid = kv.Key;
                present.Add(guid);

                JsonValue e = JsonValue.Obj().Set("guid", JsonValue.Str(guid));
                double[] start;
                if (!rhinoStarts.TryGetValue(guid, out start)) start = CurveStart(ctx, guid);
                if (!WriteCurve(model, kv.Value, start, e))
                    ctx.Res.Notes.Add(string.Format("Curve {0}: its AxisVM lines no longer form one chain; its shape was not sent.", Short(guid)));

                // A curve some of whose AxisVM lines were deleted has changed shape even if the rest did not move.
                JsonValue expected = ctx.Section["segments"][guid];
                bool geomChanged = expected != null && expected.AsInt(kv.Value.Count) != kv.Value.Count;
                string[] now = null, then = null;
                foreach (int id in kv.Value)
                {
                    string[] cur = LinkState.Fields(LinkState.LineState(model, id), 5);
                    string[] baseState = LinkState.Fields(LinkState.Read(la, LinkState.State, id), 5);
                    if (LinkState.LineGeom(cur[0]) != LinkState.LineGeom(baseState[0])) geomChanged = true;
                    if (now == null) { now = cur; then = baseState; }
                }

                JsonValue attrs = JsonValue.Obj();
                bool isFull = full.Contains(guid);
                bool rigidBody = isFull && ctx.Extras.RigidLines.Contains(kv.Value[0]);
                if (rigidBody) attrs.Set("Ax_Type", JsonValue.Str("RigidLink"));
                // A Rhino edge hinge / plain line / rigid link curve along a member's line takes nothing of the member.
                else if (now != null && !NotAMember(ctx, guid))
                {
                    if (!now[1].StartsWith("other", StringComparison.Ordinal) && (isFull || now[1] != then[1])) attrs.Set("Ax_Type", JsonValue.Str(now[1]));
                    // Only beams, ribs and trusses have a section and material; a spring's or link's line record still
                    // holds whatever section AxisVM used last.
                    bool frame = now[1] == "Beam" || now[1] == "Rib" || now[1] == "Truss";
                    if (frame && (isFull || now[2] != then[2] || now[3] != then[3]))
                        attrs.Set("Ax_Section", JsonValue.Str(now[2] == now[3] || now[3].Length == 0 ? now[2] : now[2] + ";" + now[3]));
                    if (frame && (isFull || now[4] != then[4])) attrs.Set("Ax_Material", JsonValue.Str(now[4]));
                }

                // Rigid diaphragm: stated on the curve unless a domain along it already says so - and only when all of
                // the curve's own lines are in it. A line it shares with another curve (overlapping members) is that
                // curve's business as much as this one's, so it alone does not put this curve in a diaphragm.
                var diaNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string diaWant = "";
                List<int> own = kv.Value.FindAll(id => LinkState.Guids(la, id).Count <= 1);
                if (own.Count == 0) own = kv.Value;
                bool allIn = true;
                foreach (int id in kv.Value)
                {
                    string d;
                    if (!ctx.LineDiaphragm.TryGetValue(id, out d)) { if (own.Contains(id)) allIn = false; continue; }
                    // A curve lying wholly on another's lines: a diaphragm that other curve has in Rhino is the other's.
                    if (own == kv.Value && LinkState.Guids(la, id).Exists(o =>
                    {
                        string od;
                        return !string.Equals(o, guid, StringComparison.OrdinalIgnoreCase)
                            && ctx.RhinoDiaphragm.TryGetValue(o, out od) && string.Equals(od, d, StringComparison.OrdinalIgnoreCase);
                    }))
                    {
                        // Still a diaphragm the line is in: a curve that has it in Rhino too is left as it is.
                        diaNames.Add(d);
                        allIn = false;
                        continue;
                    }
                    diaNames.Add(d);
                    if (diaWant.Length == 0 && own.Contains(id) && !ctx.DiaphragmByDomain.Contains(id)) diaWant = d;
                }
                if (!allIn) diaWant = "";
                string dia = NotAMember(ctx, guid) || hingeCurves.Contains(guid) ? null : ctx.DiaphragmChange(guid, diaNames, diaWant);
                if (dia != null) attrs.Set(AxKeys.Diaphragm, JsonValue.Str(dia));

                // Loads of curves drawn in AxisVM (Rhino owns the loads of its own curves).
                if (ctx.GuidMember.ContainsKey(guid) || ctx.GuidLine.ContainsKey(guid))
                {
                    Dictionary<string, List<string>> loads = CurveLoads(ctx, guid, kv.Value);
                    int first = kv.Value[0];
                    if (isFull || LoadsHash(loads) != (LinkState.Read(la, LoadsAttachment, first) ?? "")) SetLoads(attrs, loads);
                }

                // Supports, releases, springs, links, hinges: what changed against the baseline.
                Dictionary<string, string> extras = CurveExtras(ctx, guid, kv.Value, CurveNodes(model, kv.Value, start));
                ApplyExtras(attrs, extras, ReadBaseline(la, kv.Value[0], guid), isFull, text => WriteBaseline(la, kv.Value, guid, text));

                bool changed = isFull || geomChanged || attrs.Count > 0;
                e.Set("changed", JsonValue.Bool(changed)).Set("geomChanged", JsonValue.Bool(isFull || geomChanged))
                 .Set("new", JsonValue.Bool(isFull)).Set("attrs", attrs);
                if (changed) ctx.Res.Changed++; else ctx.Res.Unchanged++;
                arr.Add(e);
            }
            return arr;
        }

        // ==================================================================
        // Supports, releases, springs, links, hinges (LinkExtras)
        // ==================================================================

        private static string NormType(string t) { return (t ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", ""); }

        private static bool IsHingeType(Dictionary<string, string> types, string guid)
        {
            string t;
            return types.TryGetValue(guid, out t) && (NormType(t) == "edgehinge" || NormType(t) == "hinge");
        }

        /// <summary>A Rhino curve that is not a member itself (edge hinge, plain line, rigid link) - it may run along one.</summary>
        private static bool NotAMember(Context ctx, string guid)
        {
            string t;
            if (!ctx.RhinoTypes.TryGetValue(guid, out t)) return false;
            switch (NormType(t))
            {
                case "edgehinge": case "hinge": case "simple": case "simpleline": case "line":
                case "rigidlink": case "rigid": case "rigidbody": return true;
                default: return false;
            }
        }

        /// <summary>Ax_Type of every Rhino curve in the link file's rhino section.</summary>
        private static Dictionary<string, string> RhinoTypes(JsonValue root)
        {
            return RhinoLineAttr(root, AxKeys.Type);
        }

        /// <summary>One attribute of every Rhino curve that has it, by curve guid.</summary>
        private static Dictionary<string, string> RhinoLineAttr(JsonValue root, string key)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JsonValue r;
            try { r = root == null ? null : LinkFile.RhinoPart(root); } catch { return map; }
            JsonValue arr = r == null ? null : r["lines"];
            for (int i = 0; arr != null && i < arr.Count; i++)
            {
                string t = LinkFile.Get(arr.At(i)["attrs"], key);
                if (t.Length > 0) map[LinkFile.Get(arr.At(i), "guid")] = t;
            }
            return map;
        }

        /// <summary>Edge hinge curves adopted from AxisVM that Rhino has not loaded yet (sent by Hinges, not as curves).</summary>
        private static HashSet<string> PendingHinges(Context ctx)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonValue ids = ctx.Section["hingeIds"];
            if (ids != null && ids.ObjectValue != null)
                foreach (JsonValue v in ids.ObjectValue.Values) { string g = v.AsString(""); if (g.Length > 0 && !ctx.RhinoKnows.Contains(g)) set.Add(g); }
            return set;
        }

        /// <summary>Every edge hinge curve adopted from AxisVM ("hingeIds"), loaded by Rhino or not.</summary>
        private static HashSet<string> HingeCurves(Context ctx)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonValue ids = ctx.Section["hingeIds"];
            if (ids != null && ids.ObjectValue != null)
                foreach (JsonValue v in ids.ObjectValue.Values) { string g = v.AsString(""); if (g.Length > 0) set.Add(g); }
            return set;
        }

        /// <summary>The nodes along a curve's lines, from the end nearest <paramref name="start"/> (null when they are no chain).</summary>
        private static List<int> CurveNodes(IAxisVMModel model, List<int> lines, double[] start)
        {
            List<int> nodes = Walk(model, lines, false);
            if (nodes == null || nodes.Count < 2 || start == null) return nodes;
            double[] a = LinkState.NodeXyz(model, nodes[0]), b = LinkState.NodeXyz(model, nodes[nodes.Count - 1]);
            if (a != null && b != null && Dist(b, start) < Dist(a, start)) nodes.Reverse();
            return nodes;
        }

        /// <summary>The extras of a curve, as its kind of Rhino curve carries them.</summary>
        private static Dictionary<string, string> CurveExtras(Context ctx, string guid, List<int> lines, List<int> nodes)
        {
            if (IsHingeType(ctx.RhinoTypes, guid))
            {
                // Two domains on one line each have their own hinge (and curve): look only at this curve's domains.
                string named;
                ctx.RhinoHingeDomains.TryGetValue(guid, out named);
                return ctx.Extras.Hinge(lines, ctx.DomainGuid, AttributeParser.SplitList(named));
            }
            Dictionary<string, string> ex = ctx.Extras.Curve(lines, nodes, l => { string g; return ctx.LineGuid.TryGetValue(l, out g) ? g : null; });
            if (NotAMember(ctx, guid))
            {
                string s;
                bool support = ex.TryGetValue(AxKeys.Support, out s);
                ex.Clear();
                if (support) ex[AxKeys.Support] = s;
            }
            return ex;
        }

        /// <summary>
        /// Puts the extras to send into <paramref name="attrs"/>: all of them for a new element, else those that differ from
        /// the baseline ("" = removed in AxisVM). With no baseline yet (an element loaded before extras were tracked), the
        /// current state becomes the baseline and nothing is sent.
        /// </summary>
        private static void ApplyExtras(JsonValue attrs, Dictionary<string, string> extras, Dictionary<string, string> baseline, bool isFull, Action<string> writeBaseline)
        {
            if (isFull)
            {
                foreach (KeyValuePair<string, string> kv in extras) attrs.Set(kv.Key, JsonValue.Str(kv.Value));
                writeBaseline(LinkExtras.Text(extras));
                return;
            }
            if (baseline == null) { writeBaseline(LinkExtras.Text(extras)); return; }
            foreach (KeyValuePair<string, string> kv in LinkExtras.Changes(extras, baseline)) attrs.Set(kv.Key, JsonValue.Str(kv.Value));
        }

        /// <summary>One element's baseline in the <see cref="LinkExtras.Attachment"/> of an entity: one "guid|k=v&#9;k=v" line per element.</summary>
        private static Dictionary<string, string> ReadBaseline(AxisVMAttachments a, int index, string guid)
        {
            string all = LinkState.Read(a, LinkExtras.Attachment, index);
            if (all == null) return null;
            foreach (string line in all.Split('\n'))
                if (line.StartsWith(guid + "|", StringComparison.OrdinalIgnoreCase))
                    return LinkExtras.Parse(line.Substring(guid.Length + 1).Replace('\t', '\n'));
            return null;
        }

        private static void WriteBaseline(AxisVMAttachments a, IEnumerable<int> indices, string guid, string text)
        {
            foreach (int index in indices)
            {
                string all = LinkState.Read(a, LinkExtras.Attachment, index) ?? "";
                List<string> keep = new List<string>();
                foreach (string line in all.Split('\n'))
                    if (line.Length > 0 && !line.StartsWith(guid + "|", StringComparison.OrdinalIgnoreCase)) keep.Add(line);
                keep.Add(guid + "|" + text.Replace('\n', '\t'));
                LinkState.Write(a, LinkExtras.Attachment, index, string.Join("\n", keep));
            }
        }

        /// <summary>
        /// Edge hinges made in AxisVM become Rhino EdgeHinge curves (Ax_Stiffness, Ax_HingeDomains), one per straight run
        /// of hinged lines along one domain with one stiffness. Their ids are kept per line and domain ("hingeIds"), and on
        /// the lines (RhinoGuid), so each is adopted once; once Rhino holds one it is an ordinary curve (sent by Lines).
        /// Hinges along a Rhino edge hinge curve are Rhino's.
        /// </summary>
        private static void Hinges(Context ctx, HashSet<string> present, JsonValue arr)
        {
            IAxisVMModel model = ctx.Model;
            AxisVMAttachments la = model.Lines.Attachments;
            JsonValue ids = ctx.Section["hingeIds"];
            var lines = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var info = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);   // guid -> domain guid, stiffness
            var fresh = new Dictionary<string, List<int>>(StringComparer.Ordinal);

            foreach (KeyValuePair<int, List<KeyValuePair<int, string>>> kv in ctx.Extras.Hinges)
            {
                int line = kv.Key;
                List<string> owners = LinkState.Guids(la, line);
                // Hinges along a Rhino edge hinge curve are Rhino's - but only onto the domains that curve names (a line
                // between two domains has one hinge per domain, and the other one may be new in AxisVM).
                List<string> rhinoHinges = owners.FindAll(o => IsHingeType(ctx.RhinoTypes, o) && ctx.RhinoKnows.Contains(o));
                int uid;
                try { uid = model.Lines.get_UID(line); } catch { continue; }
                foreach (KeyValuePair<int, string> h in kv.Value)
                {
                    string dg = ctx.DomainGuid(h.Key);
                    if (dg == null) continue;
                    if (rhinoHinges.Exists(o =>
                    {
                        string named;
                        if (!ctx.RhinoHingeDomains.TryGetValue(o, out named)) return true;   // blank = every domain along it
                        List<string> l = AttributeParser.SplitList(named);
                        return l.Count == 0 || l.Exists(n => string.Equals(n, dg, StringComparison.OrdinalIgnoreCase)
                                                           || string.Equals(n, LinkFile.BaseGuid(dg), StringComparison.OrdinalIgnoreCase));
                    })) continue;
                    if (AlongRhinoHinge(ctx, line, dg)) continue;
                    JsonValue known = ids[uid + "|" + dg];
                    string g = known == null ? "" : known.AsString("");
                    if (g.Length > 0)
                    {
                        if (ctx.RhinoKnows.Contains(g)) continue;   // Rhino's now - sent with the curves
                        Group(lines, g, line);
                        info[g] = new[] { dg, h.Value };
                        continue;
                    }
                    Group(fresh, dg + "\n" + h.Value, line);
                }
            }

            foreach (KeyValuePair<string, List<int>> kv in fresh)
            {
                string[] key = kv.Key.Split('\n');
                foreach (List<int> chain in Runs(model, kv.Value))
                {
                    string g = System.Guid.NewGuid().ToString();
                    foreach (int l in chain)
                    {
                        try { ids.Set(model.Lines.get_UID(l) + "|" + key[0], JsonValue.Str(g)); } catch { }
                        List<string> owners = LinkState.Guids(la, l);
                        owners.Add(g);
                        LinkState.Write(la, LinkState.Guid, l, string.Join(";", owners));
                        // A plain (mesh / edge) line never had a baseline: without one it would count as moved.
                        if (LinkState.Read(la, LinkState.State, l) == null) LinkState.Write(la, LinkState.State, l, LinkState.LineState(model, l));
                        List<string> adopted;
                        string k = LinkState.AdoptedKey(LinkState.Read(la, LinkState.Adopted, l), out adopted);
                        if (k == null || k == ctx.Key) { adopted.Add(g); LinkState.Write(la, LinkState.Adopted, l, ctx.Key + "|" + string.Join(";", adopted)); }
                    }
                    lines[g] = chain;
                    info[g] = key;
                    Built(ctx.Section, g, chain.Count);
                    ctx.Res.Adopted++;
                }
            }

            foreach (KeyValuePair<string, List<int>> kv in lines)
            {
                string g = kv.Key;
                present.Add(g);
                JsonValue e = JsonValue.Obj().Set("guid", JsonValue.Str(g));
                if (!WriteCurve(model, kv.Value, null, e)) continue;
                e.Set("changed", JsonValue.Bool(true)).Set("geomChanged", JsonValue.Bool(true)).Set("new", JsonValue.Bool(true))
                 .Set("attrs", JsonValue.Obj().Set(AxKeys.Type, JsonValue.Str("EdgeHinge")).Set(AxKeys.Stiffness, JsonValue.Str(info[g][1]))
                                             .Set(AxKeys.HingeDomains, JsonValue.Str(info[g][0])));
                ctx.Res.Changed++;
                arr.Add(e);
            }
        }

        /// <summary>The lines split into straight runs: lines meeting end to end in one direction, each run one simple chain.</summary>
        private static List<List<int>> Runs(IAxisVMModel model, List<int> lines)
        {
            Dictionary<int, int> parent = new Dictionary<int, int>();
            Func<int, int> find = null;
            find = x => { int p = parent[x]; if (p == x) return x; int r = find(p); parent[x] = r; return r; };
            foreach (int l in lines) parent[l] = l;
            Dictionary<int, List<int>> at = new Dictionary<int, List<int>>();
            foreach (int l in lines)
            {
                int[] e = LinkState.LineNodes(model, l);
                if (e == null) continue;
                foreach (int nd in e) { List<int> x; if (!at.TryGetValue(nd, out x)) { x = new List<int>(); at[nd] = x; } x.Add(l); }
            }
            foreach (List<int> meeting in at.Values)
            {
                if (meeting.Count != 2 || !Collinear(model, meeting[0], meeting[1])) continue;
                parent[find(meeting[0])] = find(meeting[1]);
            }
            var runs = new Dictionary<int, List<int>>();
            foreach (int l in lines) { int r = find(l); List<int> x; if (!runs.TryGetValue(r, out x)) { x = new List<int>(); runs[r] = x; } x.Add(l); }
            List<List<int>> result = new List<List<int>>();
            foreach (List<int> run in runs.Values)
            {
                if (run.Count == 1 || Walk(model, run, false) != null) result.Add(run);
                else foreach (int l in run) result.Add(new List<int> { l });
            }
            return result;
        }

        private static bool Collinear(IAxisVMModel model, int a, int b)
        {
            int[] ea = LinkState.LineNodes(model, a), eb = LinkState.LineNodes(model, b);
            if (ea == null || eb == null || LinkState.IsArc(model, a) || LinkState.IsArc(model, b)) return false;
            double[] a0 = LinkState.NodeXyz(model, ea[0]), a1 = LinkState.NodeXyz(model, ea[1]), b0 = LinkState.NodeXyz(model, eb[0]), b1 = LinkState.NodeXyz(model, eb[1]);
            if (a0 == null || a1 == null || b0 == null || b1 == null) return false;
            double[] u = { a1[0] - a0[0], a1[1] - a0[1], a1[2] - a0[2] }, v = { b1[0] - b0[0], b1[1] - b0[1], b1[2] - b0[2] };
            double lu = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]), lv = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (lu < Tol || lv < Tol) return false;
            double dot = Math.Abs(u[0] * v[0] + u[1] * v[1] + u[2] * v[2]) / (lu * lv);
            return dot > 1 - 1e-6;
        }

        /// <summary>Attachment on the first line of an adopted curve: its loads when it was adopted, to send only changed loads later.</summary>
        private const string LoadsAttachment = "RhinoLoads";

        private static readonly string[] LoadFamilies = { "Ax_DistLoad", "Ax_PointLoad", "Ax_NodalLoad" };

        /// <summary>Writes whole load families (Rhino replaces each family it receives); an empty family clears it.</summary>
        private static void SetLoads(JsonValue attrs, Dictionary<string, List<string>> loads)
        {
            foreach (KeyValuePair<string, List<string>> kv in loads)
            {
                if (kv.Value.Count == 0) { attrs.Set(kv.Key, JsonValue.Str("")); continue; }
                for (int k = 0; k < kv.Value.Count; k++) attrs.Set(LinkLoads.FamilyKey(kv.Key, k), JsonValue.Str(kv.Value[k]));
            }
        }

        private static string LoadsHash(Dictionary<string, List<string>> loads)
        {
            List<string> all = new List<string>();
            foreach (KeyValuePair<string, List<string>> kv in loads) foreach (string v in kv.Value) all.Add(kv.Key + "=" + v);
            all.Sort(StringComparer.Ordinal);
            return string.Join("\n", all);
        }

        /// <summary>Member loads, plus the line loads of its lines turned into positions along the whole curve.</summary>
        private static Dictionary<string, List<string>> CurveLoads(Context ctx, string guid, List<int> lines)
        {
            List<string> dist = new List<string>(), point = new List<string>();
            int member;
            List<string> got;
            if (ctx.GuidMember.TryGetValue(guid, out member))
            {
                if (ctx.Loads.MemberDist.TryGetValue(member, out got)) dist.AddRange(got);
                if (ctx.Loads.MemberPoint.TryGetValue(member, out got)) point.AddRange(got);
            }

            // Lines in curve order from the curve start, with their offset and direction, for line-based loads.
            double[] start = CurveStart(ctx, guid);
            List<int> nodes = Walk(ctx.Model, lines, false);
            if (nodes != null && nodes.Count >= 2)
            {
                if (start != null && Dist(LinkState.NodeXyz(ctx.Model, nodes[nodes.Count - 1]) ?? start, start) < Dist(LinkState.NodeXyz(ctx.Model, nodes[0]) ?? start, start)) nodes.Reverse();
                double total = 0;
                List<double[]> spans = new List<double[]>();   // line id, offset, length, reversed
                for (int k = 0; k + 1 < nodes.Count; k++)
                {
                    int a = nodes[k], b = nodes[k + 1];
                    int line = lines.Find(l => { int[] e = LinkState.LineNodes(ctx.Model, l); return e != null && ((e[0] == a && e[1] == b) || (e[0] == b && e[1] == a)); });
                    double len = Dist(LinkState.NodeXyz(ctx.Model, a), LinkState.NodeXyz(ctx.Model, b));
                    int[] ends = LinkState.LineNodes(ctx.Model, line);
                    spans.Add(new double[] { line, total, len, ends != null && ends[0] == b ? 1 : 0 });
                    total += len;
                }
                foreach (double[] sp in spans)
                {
                    List<LinkLoads.LineLoad> ll;
                    if (!ctx.Loads.LineLoads.TryGetValue((int)sp[0], out ll) || total <= 1e-9) continue;
                    foreach (LinkLoads.LineLoad l in ll)
                    {
                        Func<double, double> along = pos => (sp[1] + (sp[3] > 0 ? sp[2] - pos : pos)) / total;
                        if (l.Distributed)
                        {
                            double a = along(l.From), b = along(l.To);
                            double[] q1 = l.V1, q2 = l.V2;
                            if (a > b) { double t = a; a = b; b = t; q1 = l.V2; q2 = l.V1; }
                            dist.Add(LinkLoads.DistText(l.Case, l.System, q1, q2, Math.Max(0, a), Math.Min(1, b)));
                        }
                        else point.Add(LinkLoads.PointText(l.Case, l.System, l.V1, Math.Max(0, Math.Min(1, along(l.From)))));
                    }
                }
            }
            return new Dictionary<string, List<string>> { { "Ax_DistLoad", dist }, { "Ax_PointLoad", point } };
        }

        /// <summary>Where an adopted curve starts: its member's start node, or its single line's start node.</summary>
        private static double[] CurveStart(Context ctx, string guid)
        {
            int member, line;
            try
            {
                if (ctx.GuidMember.TryGetValue(guid, out member)) return LinkState.NodeXyz(ctx.Model, ctx.Model.Members.Item[member].StartNode);
                if (ctx.GuidLine.TryGetValue(guid, out line)) return LinkState.NodeXyz(ctx.Model, ctx.Model.Lines.Item[line].StartNode);
            }
            catch { }
            return null;
        }

        /// <summary>Records an adopted entity as built, so deleting it in AxisVM later is sent as a deletion.</summary>
        private static void Built(JsonValue section, string guid, int segments)
        {
            section["built"].Add(JsonValue.Str(guid));
            if (segments > 0) section["segments"].Set(guid, JsonValue.Num(segments));
        }

        private static void Group(Dictionary<string, List<int>> groups, string guid, int id)
        {
            List<int> l;
            if (!groups.TryGetValue(guid, out l)) { l = new List<int>(); groups[guid] = l; }
            // A member adopted in this send stamps all its lines at once; the loop then meets those lines again.
            if (!l.Contains(id)) l.Add(id);
        }

        /// <summary>
        /// Rebuilds a curve from its AxisVM lines: one arc stays an arc, otherwise the chain becomes a line or
        /// polyline (vertices where it only continues straight are dropped), running the way the Rhino curve did.
        /// </summary>
        private static bool WriteCurve(IAxisVMModel model, List<int> lineIds, double[] rhinoStart, JsonValue e)
        {
            if (lineIds.Count == 1 && LinkState.IsArc(model, lineIds[0]))
            {
                int[] ends = LinkState.LineNodes(model, lineIds[0]);
                double[] a = ends == null ? null : LinkState.NodeXyz(model, ends[0]);
                double[] b = ends == null ? null : LinkState.NodeXyz(model, ends[1]);
                double[] m = LinkState.ArcMid(model, lineIds[0]);
                if (a == null || b == null || m == null) return false;
                if (rhinoStart != null && Dist(b, rhinoStart) < Dist(a, rhinoStart)) { double[] t = a; a = b; b = t; }
                e.Set("geom", JsonValue.Str("arc")).Set("start", P(a)).Set("end", P(b)).Set("arcMid", P(m));
                return true;
            }

            List<int> nodes = Walk(model, lineIds, false);
            if (nodes == null) return false;
            List<double[]> pts = new List<double[]>();
            foreach (int nd in nodes)
            {
                double[] p = LinkState.NodeXyz(model, nd);
                if (p == null) return false;
                pts.Add(p);
            }
            if (rhinoStart != null && Dist(pts[pts.Count - 1], rhinoStart) < Dist(pts[0], rhinoStart)) pts.Reverse();
            pts = DropStraight(pts, false);

            e.Set("geom", JsonValue.Str(pts.Count == 2 ? "line" : "polyline")).Set("start", P(pts[0])).Set("end", P(pts[pts.Count - 1]));
            if (pts.Count > 2)
            {
                JsonValue list = JsonValue.Arr();
                foreach (double[] p in pts) list.Add(P(p));
                e.Set("points", list);
            }
            return true;
        }

        /// <summary>
        /// The node ids along a chain of lines, end to end (a loop when <paramref name="closed"/>: the
        /// first node is not repeated). Null when the lines do not form one simple chain.
        /// </summary>
        private static List<int> Walk(IAxisVMModel model, List<int> lineIds, bool closed)
        {
            Dictionary<int, List<int>> at = new Dictionary<int, List<int>>();
            Dictionary<int, int[]> ends = new Dictionary<int, int[]>();
            foreach (int id in lineIds)
            {
                int[] e = LinkState.LineNodes(model, id);
                if (e == null || e[0] == e[1]) return null;
                ends[id] = e;
                foreach (int nd in e)
                {
                    List<int> l;
                    if (!at.TryGetValue(nd, out l)) { l = new List<int>(); at[nd] = l; }
                    l.Add(id);
                }
            }

            int start = -1;
            foreach (KeyValuePair<int, List<int>> kv in at)
            {
                if (kv.Value.Count > 2) return null;
                if (kv.Value.Count == 1) { if (closed) return null; if (start < 0) start = kv.Key; }
            }
            if (start < 0)
            {
                if (!closed) return null;
                foreach (int k in at.Keys) { start = k; break; }
            }

            List<int> path = new List<int> { start };
            HashSet<int> used = new HashSet<int>();
            int node = start;
            while (used.Count < lineIds.Count)
            {
                int next = -1;
                foreach (int id in at[node]) if (!used.Contains(id)) { next = id; break; }
                if (next < 0) return null;
                used.Add(next);
                node = ends[next][0] == node ? ends[next][1] : ends[next][0];
                if (closed && node == start) break;
                path.Add(node);
            }
            return used.Count == lineIds.Count ? path : null;
        }

        private static List<double[]> DropStraight(List<double[]> pts, bool closed)
        {
            if (pts.Count < 3) return pts;
            List<double[]> keep = new List<double[]>();
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                bool end = !closed && (i == 0 || i == n - 1);
                if (end) { keep.Add(pts[i]); continue; }
                double[] a = pts[(i - 1 + n) % n], b = pts[i], c = pts[(i + 1) % n];
                if (OffLine(a, c, b) > Tol * 100) keep.Add(b);
            }
            return keep.Count >= (closed ? 3 : 2) ? keep : pts;
        }

        private static double OffLine(double[] a, double[] c, double[] b)
        {
            double[] u = { c[0] - a[0], c[1] - a[1], c[2] - a[2] };
            double len = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]);
            if (len < Tol) return Dist(a, b);
            double[] w = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            double[] x = { u[1] * w[2] - u[2] * w[1], u[2] * w[0] - u[0] * w[2], u[0] * w[1] - u[1] * w[0] };
            return Math.Sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2]) / len;
        }

        // ==================================================================
        // Domains
        // ==================================================================

        private static JsonValue Domains(IAxisVMModel model, string key, HashSet<string> rhinoKnows, HashSet<string> present, JsonValue section, SendResult res, Context ctx)
        {
            AxisVMAttachments da = model.Domains.Attachments;
            AxisVMAttachments la = model.Lines.Attachments;
            JsonValue arr = JsonValue.Arr();
            int n = model.Domains.Count;

            for (int i = 1; i <= n; i++)
            {
                List<string> owners = LinkState.Guids(da, i);
                bool isFull;
                string guid;
                if (owners.Count > 0)
                {
                    if (!Ours(da, i, key)) continue;
                    guid = owners[0];
                    List<string> adoptedOwners;
                    isFull = LinkState.AdoptedKey(LinkState.Read(da, LinkState.Adopted, i), out adoptedOwners) != null && !rhinoKnows.Contains(guid);
                }
                else
                {
                    if (!Native(da, i)) continue;
                    guid = System.Guid.NewGuid().ToString();
                    LinkState.Write(da, LinkState.Guid, i, guid);
                    LinkState.Write(da, LinkState.Adopted, i, key + "|" + guid);
                    LinkState.Write(da, LinkState.State, i, LinkState.DomainState(model, i));
                    // Its edge lines go with it, so a later Rhino load does not draw them a second time.
                    List<int> edges = LinkState.ContourLines(model, i);
                    foreach (List<int> h in LinkState.HoleLines(model, i)) edges.AddRange(h);
                    foreach (int l in edges) AdoptEdge(la, l, key, guid);
                    LinkState.Write(da, LoadsAttachment, i, LoadsHash(DomainLoads(ctx, i)));
                    isFull = true;
                    Built(section, guid, 0);
                    res.Adopted++;
                }
                present.Add(guid);

                JsonValue e = JsonValue.Obj().Set("guid", JsonValue.Str(guid));
                if (!WriteOutline(model, i, e))
                    res.Notes.Add(string.Format("Surface {0}: its outline could not be traced; its shape was not sent.", Short(guid)));

                string[] now = LinkState.Fields(LinkState.DomainState(model, i), 4);
                string[] then = LinkState.Fields(LinkState.Read(da, LinkState.State, i), 4);
                JsonValue attrs = JsonValue.Obj();
                if (isFull || now[1] != then[1]) attrs.Set("Ax_FEType", JsonValue.Str(now[1]));
                if (isFull || now[2] != then[2]) attrs.Set("Ax_Thickness", JsonValue.Str(now[2]));
                if (isFull || now[3] != then[3]) attrs.Set("Ax_Material", JsonValue.Str(now[3]));
                string domainDia;
                if (!ctx.DomainDiaphragm.TryGetValue(i, out domainDia)) domainDia = "";
                string dia = ctx.DiaphragmChange(guid, domainDia.Length > 0 ? new[] { domainDia } : new string[0], domainDia);
                if (dia != null) attrs.Set(AxKeys.Diaphragm, JsonValue.Str(dia));
                List<string> adoptedOwners2;
                if (LinkState.AdoptedKey(LinkState.Read(da, LinkState.Adopted, i), out adoptedOwners2) != null)
                {
                    Dictionary<string, List<string>> loads = DomainLoads(ctx, i);
                    if (isFull || LoadsHash(loads) != (LinkState.Read(da, LoadsAttachment, i) ?? "")) SetLoads(attrs, loads);
                }
                int domainIndex = i;
                ApplyExtras(attrs, DomainExtras(ctx.Extras, i), ReadBaseline(da, i, guid), isFull, text => WriteBaseline(da, new[] { domainIndex }, guid, text));

                bool changed = isFull || now[0] != then[0] || attrs.Count > 0;
                e.Set("changed", JsonValue.Bool(changed)).Set("geomChanged", JsonValue.Bool(isFull || now[0] != then[0]))
                 .Set("new", JsonValue.Bool(isFull)).Set("attrs", attrs);
                if (changed) res.Changed++; else res.Unchanged++;
                arr.Add(e);
            }
            return arr;
        }

        private static Dictionary<string, string> DomainExtras(LinkExtras extras, int domain)
        {
            var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string s = extras.Domain(domain);
            if (s != null) a[AxKeys.SurfaceSupport] = s;
            return a;
        }

        private static Dictionary<string, string> NodeExtras(LinkExtras extras, int node)
        {
            var a = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string s = extras.Node(node);
            if (s != null) a[AxKeys.Support] = s;
            return a;
        }

        private static Dictionary<string, List<string>> DomainLoads(Context ctx, int domain)
        {
            List<string> got;
            return new Dictionary<string, List<string>> { { "Ax_DistLoad", ctx.Loads.DomainArea.TryGetValue(domain, out got) ? got : new List<string>() } };
        }

        private static void AdoptEdge(AxisVMAttachments la, int lineId, string key, string owner)
        {
            if (LinkState.Read(la, LinkState.Link, lineId) != null) return;   // imported: replaced anyway
            List<string> owners;
            string k = LinkState.AdoptedKey(LinkState.Read(la, LinkState.Adopted, lineId), out owners);
            if (k != null && k != key) return;
            if (!owners.Contains(owner)) owners.Add(owner);
            LinkState.Write(la, LinkState.Adopted, lineId, key + "|" + string.Join(";", owners));
        }

        private static bool WriteOutline(IAxisVMModel model, int domainId, JsonValue e)
        {
            List<double[]> outer = Loop(model, LinkState.ContourLines(model, domainId));
            if (outer == null) return false;
            JsonValue contour = JsonValue.Arr();
            foreach (double[] p in outer) contour.Add(P(p));
            e.Set("contour", contour);

            JsonValue holes = JsonValue.Arr();
            foreach (List<int> h in LinkState.HoleLines(model, domainId))
            {
                List<double[]> loop = Loop(model, h);
                if (loop == null) continue;
                JsonValue hole = JsonValue.Arr();
                foreach (double[] p in loop) hole.Add(P(p));
                holes.Add(hole);
            }
            if (holes.Count > 0) e.Set("holes", holes);
            return true;
        }

        private static List<double[]> Loop(IAxisVMModel model, List<int> lineIds)
        {
            if (lineIds.Count < 3) return null;
            List<int> nodes = Walk(model, lineIds, true);
            if (nodes == null) return null;
            List<double[]> pts = new List<double[]>();
            foreach (int nd in nodes)
            {
                double[] p = LinkState.NodeXyz(model, nd);
                if (p == null) return null;
                pts.Add(p);
            }
            return DropStraight(pts, true);
        }

        // ==================================================================
        // Load panels
        // ==================================================================

        /// <summary>
        /// "guid|outline state" on every contour line of a load panel drawn in AxisVM. Load panels cannot carry
        /// attachments themselves; their contour lines do, and AxisModelBuilder.Link deletes the panel with its lines.
        /// </summary>
        private const string PanelAttachment = "RhinoPanel";

        /// <summary>
        /// Load panels drawn in AxisVM become Rhino surfaces with Ax_FEType = LoadPanel (two-way). A panel whose contour
        /// lines all came from an import is Rhino's own and is left alone. Sent in the "domains" list.
        /// </summary>
        private static void Panels(Context ctx, HashSet<string> present, JsonValue arr)
        {
            IAxisVMModel model = ctx.Model;
            AxisVMAttachments la = model.Lines.Attachments;
            JsonValue panelIds = ctx.Section["panelIds"];
            int n;
            try { n = model.LoadPanels.Count; } catch { return; }

            for (int i = 1; i <= n; i++)
            {
                List<int> lines;
                try
                {
                    Array ids = null;
                    model.LoadPanels.Item[i].GetContourLineIDs(ref ids);
                    lines = LinkState.IntList(ids);
                }
                catch { lines = new List<int>(); }

                string guid = null, state;
                bool isNew;
                List<double[]> outline;
                if (lines.Count >= 3)
                {
                    string baseline = "";
                    bool allImported = true;
                    foreach (int l in lines)
                    {
                        string stamp = LinkState.Read(la, PanelAttachment, l);
                        if (stamp != null && guid == null)
                        {
                            int bar = stamp.IndexOf('|');
                            guid = bar < 0 ? stamp : stamp.Substring(0, bar);
                            baseline = bar < 0 ? "" : stamp.Substring(bar + 1);
                        }
                        if (LinkState.Read(la, LinkState.Link, l) == null) allImported = false;
                    }
                    state = LinkState.SegmentsState(model, lines);
                    if (guid == null)
                    {
                        if (allImported) continue;   // Rhino's own panel
                        guid = System.Guid.NewGuid().ToString();
                        foreach (int l in lines)
                        {
                            LinkState.Write(la, PanelAttachment, l, guid + "|" + state);
                            AdoptEdge(la, l, ctx.Key, guid);
                        }
                        Built(ctx.Section, guid, 0);
                        ctx.Res.Adopted++;
                        isNew = true;
                    }
                    else isNew = !ctx.RhinoKnows.Contains(guid);
                    outline = Loop(model, lines);
                    if (!isNew && state == baseline) { present.Add(guid); ctx.Res.Unchanged++; continue; }
                }
                else
                {
                    // Drawn as a free polygon: no lines behind it. Its outline is its identity (panelIds: outline -> guid).
                    outline = LinkState.PanelPolygon(model, i);
                    if (outline == null) { ctx.Res.Notes.Add("Load panel " + i + ": its outline could not be read; it was not sent."); continue; }
                    state = LinkState.PolygonState(outline);
                    JsonValue known = panelIds[state];
                    guid = known != null ? known.AsString("") : "";
                    if (guid.Length == 0)
                    {
                        guid = System.Guid.NewGuid().ToString();
                        panelIds.Set(state, JsonValue.Str(guid));
                        Built(ctx.Section, guid, 0);
                        ctx.Res.Adopted++;
                    }
                    isNew = !ctx.RhinoKnows.Contains(guid);
                    if (!isNew) { present.Add(guid); ctx.Res.Unchanged++; continue; }
                }
                present.Add(guid);

                JsonValue e = JsonValue.Obj().Set("guid", JsonValue.Str(guid));
                if (outline == null)
                {
                    ctx.Res.Notes.Add(string.Format("Load panel {0}: its outline could not be traced; its shape was not sent.", Short(guid)));
                    continue;
                }
                JsonValue contour = JsonValue.Arr();
                foreach (double[] p in outline) contour.Add(P(p));
                e.Set("contour", contour);

                JsonValue attrs = JsonValue.Obj().Set("Ax_FEType", JsonValue.Str("LoadPanel"));
                List<string> area, points;
                SetLoads(attrs, new Dictionary<string, List<string>>
                {
                    { "Ax_DistLoad", ctx.Loads.PanelArea.TryGetValue(i, out area) ? area : new List<string>() },
                    { "Ax_PointLoad", ctx.Loads.PanelPoint.TryGetValue(i, out points) ? points : new List<string>() }
                });
                e.Set("changed", JsonValue.Bool(true)).Set("geomChanged", JsonValue.Bool(true)).Set("new", JsonValue.Bool(isNew)).Set("attrs", attrs);
                ctx.Res.Changed++;
                arr.Add(e);
            }
        }

        // ==================================================================
        // Points
        // ==================================================================

        private static JsonValue Points(Context ctx, HashSet<string> present)
        {
            IAxisVMModel model = ctx.Model;
            AxisVMAttachments na = model.Nodes.Attachments;
            JsonValue arr = JsonValue.Arr();

            // Nodes drawn in AxisVM that carry nodal loads or a support become Rhino points with Ax_NodalLoad / Ax_Support.
            HashSet<int> adopt = new HashSet<int>(ctx.Loads.Nodal.Keys);
            adopt.UnionWith(ctx.Extras.NodeSupport.Keys);
            foreach (int node in adopt)
            {
                if (node <= 0 || !Native(na, node)) continue;
                string guid = System.Guid.NewGuid().ToString();
                LinkState.Write(na, LinkState.Guid, node, guid);
                LinkState.Write(na, LinkState.Adopted, node, ctx.Key + "|" + guid);
                LinkState.Write(na, LinkState.State, node, LinkState.NodeState(model, node));
                Built(ctx.Section, guid, 0);
                ctx.Res.Adopted++;
            }

            int n = model.Nodes.Count;
            for (int i = 1; i <= n; i++)
            {
                List<string> owners = LinkState.Guids(na, i);
                if (owners.Count == 0 || !Ours(na, i, ctx.Key)) continue;
                double[] p = LinkState.NodeXyz(model, i);
                if (p == null) continue;
                bool changed = LinkState.NodeState(model, i) != LinkState.Read(na, LinkState.State, i);
                List<string> adoptedOwners;
                bool adopted = LinkState.AdoptedKey(LinkState.Read(na, LinkState.Adopted, i), out adoptedOwners) != null;
                foreach (string g in owners)
                {
                    present.Add(g);
                    bool isNew = adopted && !ctx.RhinoKnows.Contains(g);
                    JsonValue attrs = JsonValue.Obj();
                    if (isNew)
                    {
                        List<string> loads;
                        SetLoads(attrs, new Dictionary<string, List<string>> { { "Ax_NodalLoad", ctx.Loads.Nodal.TryGetValue(i, out loads) ? loads : new List<string>() } });
                    }
                    int nodeIndex = i;
                    string owner = g;
                    ApplyExtras(attrs, NodeExtras(ctx.Extras, i), ReadBaseline(na, i, g), isNew, text => WriteBaseline(na, new[] { nodeIndex }, owner, text));
                    bool send = changed || isNew || attrs.Count > 0;
                    arr.Add(JsonValue.Obj().Set("guid", JsonValue.Str(g)).Set("xyz", P(p))
                        .Set("changed", JsonValue.Bool(send)).Set("geomChanged", JsonValue.Bool(changed || isNew)).Set("new", JsonValue.Bool(isNew)).Set("attrs", attrs));
                    if (send) ctx.Res.Changed++; else ctx.Res.Unchanged++;
                }
            }
            return arr;
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>A Rhino edge hinge curve [m] and the domains it names (null = every domain along it).</summary>
        private class HingeSeg { public double[] A, B; public HashSet<string> Domains; }

        /// <summary>The straight Rhino edge hinge curves, to know a hinge as Rhino's by where it lies even where a rebuild left its line without the curve's id.</summary>
        private static List<HingeSeg> RhinoHingeSegs(JsonValue root)
        {
            var segs = new List<HingeSeg>();
            JsonValue r;
            try { r = root == null ? null : LinkFile.RhinoPart(root); } catch { return segs; }
            if (r == null) return segs;
            string unit = LinkFile.Get(r["source"], "unitSystem");
            double scale = unit.Length == 0 ? 1.0 : AxUnits.LengthUnitFactors[AxUnits.LengthIndexFromRhinoName(unit)];
            JsonValue lines = r["lines"];
            for (int i = 0; lines != null && i < lines.Count; i++)
            {
                JsonValue l = lines.At(i), s = l["start"], e = l["end"];
                string t = NormType(LinkFile.Get(l["attrs"], AxKeys.Type));
                if ((t != "edgehinge" && t != "hinge") || s == null || e == null || s.Count < 3 || e.Count < 3 || l["points"] != null || l["arcMid"] != null) continue;
                List<string> named = AttributeParser.SplitList(LinkFile.Get(l["attrs"], AxKeys.HingeDomains));
                segs.Add(new HingeSeg
                {
                    A = new[] { s.At(0).AsDouble(0) * scale, s.At(1).AsDouble(0) * scale, s.At(2).AsDouble(0) * scale },
                    B = new[] { e.At(0).AsDouble(0) * scale, e.At(1).AsDouble(0) * scale, e.At(2).AsDouble(0) * scale },
                    Domains = named.Count == 0 ? null : new HashSet<string>(named, StringComparer.OrdinalIgnoreCase)
                });
            }
            return segs;
        }

        /// <summary>True when the line lies along a Rhino edge hinge curve that names this domain (or names none).</summary>
        private static bool AlongRhinoHinge(Context ctx, int line, string domainGuid)
        {
            if (ctx.RhinoHingeSegs.Count == 0) return false;
            int[] ends = LinkState.LineNodes(ctx.Model, line);
            if (ends == null) return false;
            double[] p = LinkState.NodeXyz(ctx.Model, ends[0]), q = LinkState.NodeXyz(ctx.Model, ends[1]);
            if (p == null || q == null) return false;
            const double tol = 0.002;
            foreach (HingeSeg h in ctx.RhinoHingeSegs)
            {
                if (h.Domains != null && !h.Domains.Contains(domainGuid) && !h.Domains.Contains(LinkFile.BaseGuid(domainGuid))) continue;
                if (OnSegment(h.A, h.B, p, tol) && OnSegment(h.A, h.B, q, tol)) return true;
            }
            return false;
        }

        private static bool OnSegment(double[] a, double[] b, double[] p, double tol)
        {
            double[] u = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            double len2 = u[0] * u[0] + u[1] * u[1] + u[2] * u[2];
            if (len2 < 1e-12) return false;
            double t = ((p[0] - a[0]) * u[0] + (p[1] - a[1]) * u[1] + (p[2] - a[2]) * u[2]) / len2;
            double len = Math.Sqrt(len2);
            if (t < -tol / len || t > 1 + tol / len) return false;
            double[] c = { a[0] + t * u[0] - p[0], a[1] + t * u[1] - p[1], a[2] + t * u[2] - p[2] };
            return Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]) <= tol;
        }

        /// <summary>Start point [m] of every curve in the Rhino section, to send curves back the same way round.</summary>
        private static Dictionary<string, double[]> RhinoLineStarts(JsonValue root)
        {
            Dictionary<string, double[]> starts = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            if (root == null) return starts;
            JsonValue r;
            try { r = LinkFile.RhinoPart(root); } catch { return starts; }
            double scale = AxUnits.LengthUnitFactors[AxUnits.LengthIndexFromRhinoName(LinkFile.Get(r["source"], "unitSystem"))];
            JsonValue lines = r["lines"];
            for (int i = 0; lines != null && i < lines.Count; i++)
            {
                JsonValue l = lines.At(i), s = l["start"];
                if (s == null || s.Count < 3) continue;
                starts[LinkFile.Get(l, "guid")] = new[] { s.At(0).AsDouble(0) * scale, s.At(1).AsDouble(0) * scale, s.At(2).AsDouble(0) * scale };
            }
            return starts;
        }

        private static JsonValue P(double[] p)
        {
            return JsonValue.Xyz(Math.Round(p[0], 9), Math.Round(p[1], 9), Math.Round(p[2], 9));
        }

        private static double Dist(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static string Short(string guid)
        {
            return guid != null && guid.Length > 8 ? guid.Substring(0, 8) : guid;
        }

        private static string SafeFileName(IAxisVMModel model)
        {
            try { return model.FileName ?? ""; } catch { return ""; }
        }
    }
}
