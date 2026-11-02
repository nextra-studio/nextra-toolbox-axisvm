using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// The live link file shared with Rhino (AxisBridge AxLink): one JSON with a section per side.
    ///
    ///   { "link": 1,
    ///     "rhino":  { the full AxExport model, "sent", "basedOnAxisvm", "seenAxisvmGuids" },
    ///     "axisvm": { "sent", "basedOnRhino", "built", "unitSystem": "Meters",
    ///                 "points" / "lines" / "domains" (what changed in AxisVM), "deleted", "notes" } }
    ///
    /// Each side rewrites only its own section and keeps the other one as it found it. Every
    /// read-modify-write holds the file open exclusively, so Rhino and AxisVM saving at the same moment
    /// cannot write back a stale copy of each other's section.
    /// </summary>
    public static class LinkFile
    {
        public const string RhinoSection = "rhino";
        public const string AxisVmSection = "axisvm";

        public static bool IsLink(JsonValue root)
        {
            return root != null && root.Type == JsonValue.JsonType.Object && root["link"] != null;
        }

        /// <summary>The Rhino model inside a link file, or the root itself for a plain AxExport file.</summary>
        public static JsonValue RhinoPart(JsonValue root)
        {
            if (!IsLink(root)) return root;
            JsonValue r = root[RhinoSection];
            if (r == null || r.Type != JsonValue.JsonType.Object || r.Count == 0)
                throw new FormatException("This link file holds nothing from Rhino yet. In Rhino, run AxLink and press 'Send to AxisVM' first.");
            return r;
        }

        public static JsonValue Read(string path)
        {
            using (FileStream fs = Open(path, FileAccess.Read, FileShare.Read))
            using (StreamReader r = new StreamReader(fs, Encoding.UTF8, true))
                return JsonValue.Parse(r.ReadToEnd());
        }

        /// <summary>
        /// Reads the file (an empty or missing one starts a new link), lets <paramref name="change"/> edit
        /// the tree and writes it back, all while holding the file exclusively. A plain AxExport file is
        /// wrapped into the "rhino" section first.
        /// </summary>
        public static void Update(string path, Action<JsonValue> change)
        {
            using (FileStream fs = Open(path, FileAccess.ReadWrite, FileShare.None))
            {
                string text;
                StreamReader r = new StreamReader(fs, Encoding.UTF8, true);
                text = r.ReadToEnd();

                JsonValue root = string.IsNullOrWhiteSpace(text) ? null : JsonValue.Parse(text);
                if (root == null) root = JsonValue.Obj().Set("link", JsonValue.Num(1));
                else if (!IsLink(root)) root = JsonValue.Obj().Set("link", JsonValue.Num(1)).Set(RhinoSection, root);

                change(root);

                byte[] bytes = new UTF8Encoding(false).GetBytes(root.ToJson());
                fs.SetLength(0);
                fs.Position = 0;
                fs.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>Opens the file, retrying for a few seconds while the other side holds it.</summary>
        private static FileStream Open(string path, FileAccess access, FileShare share)
        {
            FileMode mode = access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate;
            for (int attempt = 0; ; attempt++)
            {
                try { return new FileStream(path, mode, access, share); }
                catch (IOException)
                {
                    if (attempt >= 25 || !File.Exists(path)) throw;
                    Thread.Sleep(200);
                }
            }
        }

        public static string Now()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        public static string Get(JsonValue section, string key)
        {
            JsonValue v = section == null ? null : section[key];
            return v == null ? "" : v.AsString("");
        }

        /// <summary>A stored UTC stamp as local time for the dialogs ("" stays "").</summary>
        public static string Local(string stamp)
        {
            DateTime t;
            if (string.IsNullOrEmpty(stamp) || !DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                return stamp ?? "";
            return t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>The guids the Rhino section knows: every object it holds plus every AxisVM object Rhino ever loaded.</summary>
        public static HashSet<string> RhinoKnownGuids(JsonValue root)
        {
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonValue r = root == null ? null : (IsLink(root) ? root[RhinoSection] : root);
            if (r == null) return known;
            foreach (string list in new[] { "points", "lines", "domains" })
            {
                JsonValue arr = r[list];
                for (int i = 0; arr != null && i < arr.Count; i++)
                {
                    string g = Get(arr.At(i), "guid");
                    if (g.Length > 0) known.Add(BaseGuid(g));
                }
            }
            JsonValue seen = r["seenAxisvmGuids"];
            for (int i = 0; seen != null && i < seen.Count; i++) known.Add(seen.At(i).AsString(""));
            return known;
        }

        /// <summary>A polysurface face id "guid_f2" -> "guid".</summary>
        public static string BaseGuid(string guid)
        {
            int i = guid == null ? -1 : guid.LastIndexOf("_f", StringComparison.Ordinal);
            return i > 0 ? guid.Substring(0, i) : guid;
        }

        /// <summary>One line for the dialogs: who sent what, and whether the other side has caught up.</summary>
        public class Status
        {
            public string RhinoSent = "", RhinoBasedOnAxisvm = "", AxisVmSent = "", AxisVmBasedOnRhino = "";
            public int AxisVmChanges;

            /// <summary>AxisVM sent changes that Rhino has not loaded yet.</summary>
            public bool AxisVmPending { get { return AxisVmSent.Length > 0 && AxisVmChanges > 0 && AxisVmSent != RhinoBasedOnAxisvm; } }

            /// <summary>Rhino sent a model AxisVM has not loaded yet.</summary>
            public bool RhinoNewer { get { return RhinoSent.Length > 0 && RhinoSent != AxisVmBasedOnRhino; } }
        }

        public static Status ReadStatus(JsonValue root)
        {
            Status s = new Status();
            if (!IsLink(root))
            {
                s.RhinoSent = root != null ? "(plain AxExport file)" : "";
                return s;
            }
            JsonValue r = root[RhinoSection], a = root[AxisVmSection];
            s.RhinoSent = Get(r, "sent");
            s.RhinoBasedOnAxisvm = Get(r, "basedOnAxisvm");
            s.AxisVmSent = Get(a, "sent");
            s.AxisVmBasedOnRhino = Get(a, "basedOnRhino");
            s.AxisVmChanges = CountChanged(a);
            return s;
        }

        public static int CountChanged(JsonValue axisvm)
        {
            if (axisvm == null) return 0;
            int n = 0;
            foreach (string list in new[] { "points", "lines", "domains" })
            {
                JsonValue arr = axisvm[list];
                for (int i = 0; arr != null && i < arr.Count; i++)
                {
                    JsonValue c = arr.At(i)["changed"];
                    if (c != null && c.Type == JsonValue.JsonType.Bool && c.BoolValue) n++;
                }
            }
            JsonValue del = axisvm["deleted"];
            JsonValue lib = axisvm["libraryNew"];
            return n + (del != null ? del.Count : 0) + (lib != null ? lib.AsInt(0) : 0);
        }
    }
}
