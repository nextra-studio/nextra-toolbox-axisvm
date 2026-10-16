// Closes the modal dialogs a scratch AxisVM started by a live check pops up on the user's screen ("N invalid link
// elements deleted", "Save changes?", ...). Each one blocks the COM call that caused it until someone clicks it,
// which looks like a hang. Only safe answers are ever pressed: OK, No or Cancel - never Yes or Save.
//
// Usage: AxisVmDialogGuard.Start() right after Activator.CreateInstance; it watches every AxisVM process started
// after that moment (never one that was already open) and logs each dialog it closed.
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

    private static DateTime since;

    public static void Start()
    {
        since = DateTime.Now.AddSeconds(-5);
        new Thread(Watch) { IsBackground = true, Name = "AxisVM dialog guard" }.Start();
    }

    private static void Watch()
    {
        while (true)
        {
            try
            {
                HashSet<int> pids = new HashSet<int>();
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.ProcessName.IndexOf("axisvm", StringComparison.OrdinalIgnoreCase) >= 0 && p.StartTime >= since) pids.Add(p.Id);
                    }
                    catch { }
                }
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
