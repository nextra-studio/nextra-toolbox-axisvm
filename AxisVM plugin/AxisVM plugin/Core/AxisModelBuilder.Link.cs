using System;
using System.Collections.Generic;
using System.IO;
using AxisVM;

namespace AxisVM_plugin.Core
{
    /// <summary>What an earlier import of one Rhino file left in the model, found by its RhinoLink mark.</summary>
    public class LinkedItems
    {
        public int Nodes;
        public int Lines;
        public int Domains;
        public int LoadPanels;
        public int Parts;

        public int Total { get { return Nodes + Lines + Domains + LoadPanels + Parts; } }

        public override string ToString()
        {
            return string.Format("{0} nodes, {1} lines, {2} domains, {3} load panels, {4} parts", Nodes, Lines, Domains, LoadPanels, Parts);
        }
    }

    /// <summary>
    /// Re-import as an update: every node, line, domain and part an import creates is marked with the
    /// attachment "RhinoLink" = the export's file name. Importing the same file again with
    /// ImportOptions.ReplacePrevious first deletes everything carrying that mark (load panels are found
    /// through their contour lines, which carry it) and then builds as usual, so a curve moved in Rhino
    /// arrives moved instead of next to its old copy.
    ///
    /// Marking by "created by this run" (UIDs absent before it) rather than per Rhino object is what
    /// catches the lines and nodes no Rhino object owns on its own: slab edges, intersection splits,
    /// one-way panel strips. AxisVM keeps attachments with their entity when indices shift and saves
    /// them in the .axs, and deleting the lines and nodes removes their members, supports, links,
    /// rigid bodies, edge connections and loads with them.
    ///
    /// Entities drawn in AxisVM and handed to Rhino by "Send to Rhino" carry RhinoAdopted instead
    /// (see RhinoLinkSender). They are replaced only once the incoming Rhino model knows the objects
    /// they became (ImportOptions.RhinoKnownGuids); until then Rhino has never seen them, and deleting
    /// them would lose them.
    /// </summary>
    public partial class AxisModelBuilder
    {
        public const string LinkAttachment = LinkState.Link;

        /// <summary>The mark an import of this file writes: its file name, case-insensitive.</summary>
        public static string LinkKey(string jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath)) return "";
            try { return Path.GetFileName(jsonPath.Trim()).ToLowerInvariant(); }
            catch { return jsonPath.Trim().ToLowerInvariant(); }
        }

        private HashSet<int> nodeUidsBefore = new HashSet<int>();
        private HashSet<int> domainUidsBefore = new HashSet<int>();
        private HashSet<int> partUidsBefore = new HashSet<int>();

        // ==================================================================
        // Finding an earlier import
        // ==================================================================

        /// <summary>UIDs of everything an earlier import (or an adoption Rhino has caught up with) left.</summary>
        private class LinkedUids
        {
            public readonly List<int> Nodes = new List<int>();
            public readonly List<int> Lines = new List<int>();
            public readonly List<int> Domains = new List<int>();
            public readonly List<int> LoadPanels = new List<int>();
            /// <summary>Part ids (the custom parts collection addresses parts by id, not by index).</summary>
            public readonly List<int> Parts = new List<int>();

            public LinkedItems Counts()
            {
                return new LinkedItems { Nodes = Nodes.Count, Lines = Lines.Count, Domains = Domains.Count, LoadPanels = LoadPanels.Count, Parts = Parts.Count };
            }
        }

        /// <summary>What an earlier import of the file at <paramref name="jsonPath"/> left in the model (for the dialog).</summary>
        public static LinkedItems FindLinked(IAxisVMModel model, string jsonPath)
        {
            string key = LinkKey(jsonPath);
            if (model == null || key.Length == 0) return new LinkedItems();
            try { return Linked(model, key, null).Counts(); }
            catch { return new LinkedItems(); }
        }

        /// <summary>True when an entity carries the import mark, or is adopted and Rhino now knows what it became.</summary>
        private static bool Replaceable(AxisVMAttachments a, int index, string key, HashSet<string> rhinoKnows)
        {
            if (string.Equals(LinkState.Read(a, LinkState.Link, index), key, StringComparison.Ordinal)) return true;
            if (rhinoKnows == null) return false;
            List<string> owners;
            string adoptedKey = LinkState.AdoptedKey(LinkState.Read(a, LinkState.Adopted, index), out owners);
            if (!string.Equals(adoptedKey, key, StringComparison.Ordinal)) return false;
            foreach (string g in owners) if (rhinoKnows.Contains(g)) return true;
            return false;
        }

        private static LinkedUids Linked(IAxisVMModel model, string key, HashSet<string> rhinoKnows)
        {
            LinkedUids found = new LinkedUids();

            HashSet<int> lineIndices = new HashSet<int>();
            AxisVMAttachments la = TryGet(() => model.Lines.Attachments);
            int n = TryCount(() => model.Lines.Count);
            for (int i = 1; i <= n && la != null; i++)
            {
                if (!Replaceable(la, i, key, rhinoKnows)) continue;
                lineIndices.Add(i);
                found.Lines.Add(model.Lines.get_UID(i));
            }

            AxisVMAttachments na = TryGet(() => model.Nodes.Attachments);
            n = TryCount(() => model.Nodes.Count);
            for (int i = 1; i <= n && na != null; i++)
                if (Replaceable(na, i, key, rhinoKnows))
                    found.Nodes.Add(model.Nodes.get_UID(i));

            AxisVMAttachments da = TryGet(() => model.Domains.Attachments);
            n = TryCount(() => model.Domains.Count);
            for (int i = 1; i <= n && da != null; i++)
                if (Replaceable(da, i, key, rhinoKnows))
                    found.Domains.Add(model.Domains.get_UID(i));

            // Load panels cannot carry attachments: one whose contour runs only along marked lines is ours.
            n = TryCount(() => model.LoadPanels.Count);
            for (int i = 1; i <= n && lineIndices.Count > 0; i++)
            {
                try
                {
                    Array ids = null;
                    model.LoadPanels.Item[i].GetContourLineIDs(ref ids);
                    if (ids == null || ids.Length == 0) continue;
                    bool all = true;
                    foreach (object o in ids) if (!lineIndices.Contains(Convert.ToInt32(o))) { all = false; break; }
                    if (all) found.LoadPanels.Add(model.LoadPanels.get_UID(i));
                }
                catch { }
            }

            try
            {
                AxisVMAttachments pa = model.CustomParts.Attachments;
                IAxisVMCustomPartFolder root = model.CustomParts.RootFolder;
                int parts = root.PartCount;
                for (int i = 1; i <= parts; i++)
                {
                    int id = root.get_PartId(i);
                    if (string.Equals(LinkState.Read(pa, LinkState.Link, id), key, StringComparison.Ordinal)) found.Parts.Add(id);
                }
            }
            catch { }

            return found;
        }

        // ==================================================================
        // Removing it (before the build)
        // ==================================================================

        /// <summary>
        /// Deletes what an earlier import of this file created: load panels, domains, lines, parts, then
        /// the marked nodes nothing else uses any more. A marked node that a line drawn in AxisVM still
        /// uses is kept (and unmarked), so that line is not deleted with it.
        /// </summary>
        private void RemovePrevious()
        {
            string key = LinkKey(opt.SourcePath);
            if (key.Length == 0) return;

            LinkedUids old;
            try { old = Linked(model, key, opt.RhinoKnownGuids); }
            catch (Exception ex)
            {
                notes.Add("The earlier import could not be looked up (" + ex.Message + "); nothing was replaced.");
                return;
            }

            LinkedItems counts = old.Counts();
            if (counts.Total == 0)
            {
                notes.Add("Update: nothing from an earlier import of '" + key + "' was found in this model, so everything was imported as new.");
                return;
            }

            if (opt.DryRun)
            {
                stats.Replaced = counts;
                return;
            }

            // The end nodes of the lines about to go: those an adoption brought in carry no mark of
            // their own, and are deleted below too when nothing else uses them.
            HashSet<int> nodeUids = new HashSet<int>(old.Nodes);
            foreach (int uid in old.Lines)
            {
                try
                {
                    int[] ends = LinkState.LineNodes(model, model.Lines.get_IndexOfUID(uid));
                    if (ends == null) continue;
                    foreach (int e in ends) if (e > 0) nodeUids.Add(model.Nodes.get_UID(e));
                }
                catch { }
            }

            foreach (int uid in old.LoadPanels) DeleteByUid(i => model.LoadPanels.Delete(i), u => model.LoadPanels.get_IndexOfUID(u), uid);
            foreach (int uid in old.Domains) DeleteByUid(i => model.Domains.Delete(i), u => model.Domains.get_IndexOfUID(u), uid);
            foreach (int uid in old.Lines) DeleteByUid(i => model.Lines.Delete(i), u => model.Lines.get_IndexOfUID(u), uid);

            // Parts by descending id so the ids still to delete stay valid.
            old.Parts.Sort();
            for (int k = old.Parts.Count - 1; k >= 0; k--)
            {
                try { model.CustomParts.DeletePart(old.Parts[k]); } catch { }
            }

            HashSet<int> used = new HashSet<int>();
            int lines = SafeLineCount();
            for (int i = 1; i <= lines; i++)
            {
                int[] ends = LinkState.LineNodes(model, i);
                if (ends == null) continue;
                // UIDs: deleting nodes below shifts the indices of the rest.
                try { used.Add(model.Nodes.get_UID(ends[0])); used.Add(model.Nodes.get_UID(ends[1])); } catch { }
            }

            HashSet<int> markedNodes = new HashSet<int>(old.Nodes);
            int kept = 0;
            foreach (int uid in nodeUids)
            {
                int index;
                try { index = model.Nodes.get_IndexOfUID(uid); } catch { continue; }
                if (index <= 0) continue;
                if (used.Contains(uid))
                {
                    if (markedNodes.Contains(uid))
                    {
                        try { model.Nodes.Attachments.Delete(LinkAttachment, index); } catch { }
                        kept++;
                    }
                    continue;
                }
                try { model.Nodes.Delete(index); } catch { }
            }

            stats.Replaced = counts;
            if (kept > 0)
                notes.Add(string.Format("Update: {0} node(s) of the earlier import are also used by lines drawn in AxisVM and were kept.", kept));
        }

        private static void DeleteByUid(Func<int, int> delete, Func<int, int> indexOfUid, int uid)
        {
            try
            {
                int index = indexOfUid(uid);
                if (index > 0) delete(index);
            }
            catch { }
        }

        // ==================================================================
        // Marking this import (after the build)
        // ==================================================================

        /// <summary>Remembers what exists before the build, so MarkCreated can tell what the build added.</summary>
        private void CaptureBefore()
        {
            nodeUidsBefore = Uids(SafeNodeCount(), i => model.Nodes.get_UID(i));
            domainUidsBefore = Uids(TryCount(() => model.Domains.Count), i => model.Domains.get_UID(i));
            partUidsBefore = new HashSet<int>();
            try
            {
                IAxisVMCustomPartFolder root = model.CustomParts.RootFolder;
                for (int i = 1; i <= root.PartCount; i++) partUidsBefore.Add(root.get_PartUID(i));
            }
            catch { }
        }

        /// <summary>
        /// Marks everything this run created with RhinoLink, and stores the state of every entity that
        /// stands for a Rhino object (RhinoState) - the baseline "Send to Rhino" compares against.
        /// </summary>
        private void MarkCreated()
        {
            if (opt.DryRun) return;
            string key = LinkKey(opt.SourcePath);
            if (key.Length == 0) return;

            int marked = 0, failed = 0;
            Action<AxisVMAttachments, int> mark = (a, index) =>
            {
                if (LinkState.Write(a, LinkState.Link, index, key)) marked++; else failed++;
            };

            AxisVMAttachments la = TryGet(() => model.Lines.Attachments);
            int n = SafeLineCount();
            for (int i = 1; i <= n; i++)
            {
                if (preexistingLineUids.Contains(TryCount(() => model.Lines.get_UID(i)))) continue;
                mark(la, i);
                if (LinkState.Read(la, LinkState.Guid, i) != null) LinkState.Write(la, LinkState.State, i, LinkState.LineState(model, i));
            }

            AxisVMAttachments na = TryGet(() => model.Nodes.Attachments);
            n = SafeNodeCount();
            for (int i = 1; i <= n; i++)
            {
                if (nodeUidsBefore.Contains(TryCount(() => model.Nodes.get_UID(i)))) continue;
                mark(na, i);
                if (LinkState.Read(na, LinkState.Guid, i) != null) LinkState.Write(na, LinkState.State, i, LinkState.NodeState(model, i));
            }

            AxisVMAttachments da = TryGet(() => model.Domains.Attachments);
            n = TryCount(() => model.Domains.Count);
            for (int i = 1; i <= n; i++)
            {
                if (domainUidsBefore.Contains(TryCount(() => model.Domains.get_UID(i)))) continue;
                mark(da, i);
                LinkState.Write(da, LinkState.State, i, LinkState.DomainState(model, i));
            }

            try
            {
                AxisVMAttachments pa = model.CustomParts.Attachments;
                IAxisVMCustomPartFolder root = model.CustomParts.RootFolder;
                for (int i = 1; i <= root.PartCount; i++)
                    if (!partUidsBefore.Contains(root.get_PartUID(i))) mark(pa, root.get_PartId(i));
            }
            catch { }

            if (failed > 0)
                notes.Add(string.Format("{0} of {1} imported entities could not be marked as coming from '{2}'; a later update cannot replace those and may duplicate them.", failed, failed + marked, key));
        }

        private static HashSet<int> Uids(int count, Func<int, int> uid)
        {
            HashSet<int> set = new HashSet<int>();
            for (int i = 1; i <= count; i++)
            {
                try { set.Add(uid(i)); } catch { }
            }
            return set;
        }

        private static T TryGet<T>(Func<T> get) where T : class
        {
            try { return get(); } catch { return null; }
        }

        private static int TryCount(Func<int> get)
        {
            try { return get(); } catch { return 0; }
        }
    }
}
