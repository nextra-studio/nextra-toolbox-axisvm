// Closes the modal dialogs a scratch AxisVM started by a live check pops up on the user's screen ("N invalid link
// elements deleted", "Save changes?", ...). Each one blocks the COM call that caused it until someone clicks it,
// which looks like a hang. Only safe answers are ever pressed: OK, No or Cancel - never Yes or Save.
//
// Usage: app = AxisVmDialogGuard.Launch() instead of Activator.CreateInstance. Launch REFUSES to start while any AxisVM is
// already open (a COM start would attach to it and the check would run inside the user's session), and the guard only ever
// touches the one process Launch created - never an AxisVM the user opens later - and logs each dialog it closed.
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

    /// <summary>The single AxisVM process this check created (0 until it is known). The guard acts on nothing else.</summary>
    private static int ownedPid;

    private static List<int> AxisVmPids()
    {
        List<int> pids = new List<int>();
        foreach (Process p in Process.GetProcesses())
        {
            try { if (p.ProcessName.IndexOf("axisvm", StringComparison.OrdinalIgnoreCase) >= 0) pids.Add(p.Id); } catch { }
        }
        return pids;
    }

    /// <summary>Starts a new AxisVM through COM, or throws when one is already open. See the file header.</summary>
    public static AxisVM.IAxisVMApplication Launch()
    {
        List<int> open = AxisVmPids();
        if (open.Count > 0)
            throw new InvalidOperationException("AxisVM is already open (process " + open[0] + "). Close it first: a check never shares or touches an AxisVM that is already running.");

        new Thread(Watch) { IsBackground = true, Name = "AxisVM dialog guard" }.Start();
        AxisVM.IAxisVMApplication app = (AxisVM.IAxisVMApplication)Activator.CreateInstance(Type.GetTypeFromProgID("AxisVM.AxisVMApplication"));

        // The process this created: the one AxisVM that exists now.
        for (int i = 0; i < 100 && ownedPid == 0; i++)
        {
            List<int> now = AxisVmPids();
            if (now.Count == 1) ownedPid = now[0];
            else if (now.Count > 1) throw new InvalidOperationException("More than one AxisVM started - cannot tell which one belongs to this check.");
            else Thread.Sleep(100);
        }
        if (ownedPid == 0) throw new InvalidOperationException("The AxisVM this check started could not be found.");
        return app;
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
                int pid = ownedPid;
                if (pid != 0)
                {
                    HashSet<int> pids = new HashSet<int> { pid };
                    EnumWindows((h, l) => { Inspect(h, pids); return true; }, IntPtr.Zero);
                }
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
