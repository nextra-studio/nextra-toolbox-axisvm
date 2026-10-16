// Closes the modal dialogs a scratch AxisVM started by a live check pops up on the user's screen ("N invalid link
// elements deleted", "Save changes?", ...). Each one blocks the COM call that caused it until someone clicks it,
// which looks like a hang. Only safe answers are ever pressed: OK, No or Cancel - never Yes or Save.
//
// Usage: app = AxisVmDialogGuard.Launch() instead of Activator.CreateInstance. Launch starts its OWN AxisVM like a double
// click and connects to exactly that process through the NextraAxisVM add-on ("!NextraAxisVM.Host.<pid>"); it never attaches
// to an AxisVM that is already open (the user's, or another agent's). The guard only touches that process and the processes
// it starts itself, and logs each dialog it closed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class AxisVmDialogGuard
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    private const int BM_CLICK = 0x00F5;

    /// <summary>Button captions pressed, best first.</summary>
    private static readonly string[] Safe = { "No", "OK", "Cancel" };

    /// <summary>
    /// The AxisVM process this check started, plus any process it starts itself (AxisVM restarts itself in a child process
    /// when its first license key is busy). The guard acts on nothing else - other AxisVMs on this machine (the user's,
    /// another agent's) are never touched, whenever they were started.
    /// </summary>
    private static readonly HashSet<int> owned = new HashSet<int>();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize; public uint cntUsage; public uint th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID;
        public uint cntThreads; public uint th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    /// <summary>Adds every running process whose parent is already owned (children of our AxisVM).</summary>
    private static void AdoptChildren()
    {
        IntPtr snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return;
        try
        {
            List<KeyValuePair<int, int>> pairs = new List<KeyValuePair<int, int>>();
            PROCESSENTRY32 e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
            if (Process32First(snap, ref e))
                do pairs.Add(new KeyValuePair<int, int>((int)e.th32ProcessID, (int)e.th32ParentProcessID)); while (Process32Next(snap, ref e));
            lock (owned)
            {
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (KeyValuePair<int, int> kv in pairs)
                        if (owned.Contains(kv.Value) && owned.Add(kv.Key)) grew = true;
                }
            }
        }
        finally { CloseHandle(snap); }
    }

    private static List<int> AxisVmPids()
    {
        List<int> pids = new List<int>();
        foreach (Process p in Process.GetProcesses())
        {
            try { if (p.ProcessName.IndexOf("axisvm", StringComparison.OrdinalIgnoreCase) >= 0) pids.Add(p.Id); } catch { }
        }
        return pids;
    }

    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out System.Runtime.InteropServices.ComTypes.IRunningObjectTable rot);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out System.Runtime.InteropServices.ComTypes.IBindCtx ctx);

    /// <summary>
    /// Starts AxisVM the way a double click does - so it opens with the user's last license key (a COM start tries the first
    /// key in the list, which may be in use, and then restarts itself in a second process the COM proxy cannot follow) -
    /// and connects through the NextraAxisVM add-on's "!NextraAxisVM.Host.&lt;pid&gt;" entry in the running object table.
    /// Throws when an AxisVM is already open. See the file header.
    /// </summary>
    public static AxisVM.IAxisVMApplication Launch()
    {
        string dir = Environment.GetEnvironmentVariable("AXISVM_DIR");
        if (string.IsNullOrEmpty(dir)) dir = @"C:\AXISVM X8";
        string exe = System.IO.Path.Combine(dir, "AxisVM_x64.exe");

        new Thread(Watch) { IsBackground = true, Name = "AxisVM dialog guard" }.Start();
        Process started = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true });
        lock (owned) owned.Add(started.Id);

        DateTime end = DateTime.Now.AddSeconds(180);
        while (DateTime.Now < end)
        {
            List<int> pids;
            lock (owned) pids = new List<int>(owned);
            foreach (int pid in pids)
            {
                object host = FromRot("!NextraAxisVM.Host." + pid);
                if (host == null) continue;
                try
                {
                    AxisVM.IAxisVMApplication app = (AxisVM.IAxisVMApplication)host.GetType().InvokeMember(
                        "GetApplication", System.Reflection.BindingFlags.InvokeMethod, null, host, null);
                    if (app != null && app.Loaded == AxisVM.ELongBoolean.lbTrue) return app;
                }
                catch { }
            }
            Thread.Sleep(500);
        }
        throw new InvalidOperationException("AxisVM did not finish starting, or the NextraAxisVM add-on is not loaded (no '!NextraAxisVM.Host.<pid>' in the running object table).");
    }

    /// <summary>Waits (up to <paramref name="seconds"/>) until the AxisVM processes this check started have exited.</summary>
    public static void WaitClosed(int seconds)
    {
        DateTime end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            bool alive = false;
            List<int> pids;
            lock (owned) pids = new List<int>(owned);
            foreach (int pid in pids)
            {
                try { if (!Process.GetProcessById(pid).HasExited) alive = true; } catch { }
            }
            if (!alive) return;
            Thread.Sleep(500);
        }
    }

    private static object FromRot(string name)
    {
        try
        {
            System.Runtime.InteropServices.ComTypes.IRunningObjectTable rot;
            System.Runtime.InteropServices.ComTypes.IBindCtx ctx;
            if (GetRunningObjectTable(0, out rot) != 0 || CreateBindCtx(0, out ctx) != 0) return null;
            System.Runtime.InteropServices.ComTypes.IEnumMoniker monikers;
            rot.EnumRunning(out monikers);
            System.Runtime.InteropServices.ComTypes.IMoniker[] one = new System.Runtime.InteropServices.ComTypes.IMoniker[1];
            while (monikers.Next(1, one, IntPtr.Zero) == 0)
            {
                string display;
                one[0].GetDisplayName(ctx, null, out display);
                if (!string.Equals(display, name, StringComparison.OrdinalIgnoreCase)) continue;
                object found;
                rot.GetObject(one[0], out found);
                return found;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// A new, empty model in the AxisVM the check started. A fresh AxisVM can open the user's last project (it did once: a real
    /// Box folder model), so a check must never take model 1 for granted: it creates its own and picks an empty one, and
    /// refuses to continue when it cannot find one.
    /// </summary>
    public static AxisVM.IAxisVMModel FreshModel(AxisVM.IAxisVMApplication app)
    {
        AxisVM.IAxisVMModels models = app.Models;
        int created = models.New();   // the index of the new model
        if (created >= 1 && created <= models.Count)
        {
            AxisVM.IAxisVMModel fresh = models.Item[created];
            try { if (fresh.Nodes.Count == 0 && fresh.Lines.Count == 0 && fresh.Domains.Count == 0) return fresh; } catch { }
        }
        for (int i = models.Count; i >= 1; i--)
        {
            AxisVM.IAxisVMModel m = models.Item[i];
            try { if (m.Nodes.Count == 0 && m.Lines.Count == 0 && m.Domains.Count == 0) return m; } catch { }
        }
        throw new InvalidOperationException("No empty model could be created in the scratch AxisVM - refusing to run on an existing model.");
    }

    private static void Watch()
    {
        while (true)
        {
            try
            {
                AdoptChildren();

                HashSet<int> pids;
                lock (owned) pids = new HashSet<int>(owned);
                if (pids.Count > 0) EnumWindows((h, l) => { Inspect(h, pids); return true; }, IntPtr.Zero);
            }
            catch { }
            Thread.Sleep(400);
        }
    }

    private static string Text(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static void Inspect(IntPtr window, HashSet<int> pids)
    {
        int pid;
        GetWindowThreadProcessId(window, out pid);
        if (!pids.Contains(pid) || !IsWindowVisible(window)) return;

        // Buttons of this window by caption ("&No" -> "No").
        Dictionary<string, IntPtr> buttons = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        List<string> texts = new List<string>();
        EnumChildWindows(window, (c, l) =>
        {
            StringBuilder cls = new StringBuilder(64);
            GetClassName(c, cls, cls.Capacity);
            string t = Text(c);
            if (cls.ToString().IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 && IsWindowVisible(c) && IsWindowEnabled(c))
            {
                string caption = t.Replace("&", "").Trim();
                if (caption.Length > 0 && !buttons.ContainsKey(caption)) buttons[caption] = c;
            }
            else if (t.Trim().Length > 0) texts.Add(t.Trim());
            return true;
        }, IntPtr.Zero);

        // A dialog is a window whose buttons are only answers; the main window has none of these alone.
        if (buttons.Count == 0 || buttons.Count > 5) return;
        foreach (string want in Safe)
        {
            IntPtr b;
            if (!buttons.TryGetValue(want, out b)) continue;
            Console.WriteLine("      (closed AxisVM dialog '" + Text(window) + "': " + string.Join(" / ", texts) + " -> " + want + ")");
            PostMessage(b, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return;
        }
    }
}
