// SysBar - cienki pasek systemowy (CPU, RAM, siec, GPU, zegar) dokowany do krawedzi ekranu.
// Kompilacja: build.cmd (uzywa csc.exe z .NET Framework 4.x, nic nie trzeba instalowac).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace SysBar
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
            public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_ACTIVATE = 6, ABM_WINDOWPOSCHANGED = 9;
        public const uint ABE_TOP = 1, ABE_BOTTOM = 3;
        public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
        public const int WM_ACTIVATE = 0x0006, WM_WINDOWPOSCHANGED = 0x0047, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0;
        public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [DllImport("shell32.dll")]
        public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string msg);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
        [DllImport("kernel32.dll")]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")]
        public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint len);
        [DllImport("kernel32.dll")]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }

    class GpuInfo
    {
        public string Name;
        public double Util = double.NaN, MemUsedMb = double.NaN, MemTotalMb = double.NaN, Temp = double.NaN;
    }

    class Snapshot
    {
        public string[] CpuNames;
        public double[] Cpu;
        public ulong RamUsed, RamTotal;
        public double Down, Up;
        public List<GpuInfo> Gpus = new List<GpuInfo>();
    }

    // Zbiera pomiary w osobnym watku, zeby pasek nigdy sie nie przycinal.
    class Sampler
    {
        readonly Action<Snapshot> callback;
        readonly bool forceWddm;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Thread thread;

        string[] cpuNames = new string[0];
        PerformanceCounter[] cpuCounters = new PerformanceCounter[0];

        Process smi;
        IntPtr job = IntPtr.Zero;
        readonly SortedDictionary<int, GpuInfo> smiGpus = new SortedDictionary<int, GpuInfo>();

        // GPU bez nvidia-smi (AMD / Intel): liczniki wydajnosci "GPU Engine"
        PerformanceCounterCategory gpuCat;
        readonly Dictionary<long, string> wddmNames = new Dictionary<long, string>();
        Dictionary<string, CounterSample> gpuPrev = new Dictionary<string, CounterSample>();
        static readonly Regex engRx = new Regex(@"luid_0x([0-9A-Fa-f]+)_0x([0-9A-Fa-f]+)_phys_(\d+)_eng_(\d+)", RegexOptions.Compiled);

        public Sampler(Action<Snapshot> callback, bool forceWddm)
        {
            this.callback = callback;
            this.forceWddm = forceWddm;
        }

        public void Start()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop()
        {
            stop.Set();
            try { if (smi != null && !smi.HasExited) smi.Kill(); } catch { }
        }

        void Run()
        {
            try { InitCpu(); } catch { }
            bool haveSmi = false;
            if (!forceWddm) { try { haveSmi = StartSmi(); } catch { } }
            try { InitWddm(haveSmi); } catch { }

            Stopwatch sw = Stopwatch.StartNew();
            long lastRx = -1, lastTx = -1;
            double lastT = 0;

            while (!stop.WaitOne(1000))
            {
                Snapshot s = new Snapshot();
                s.CpuNames = cpuNames;
                s.Cpu = new double[cpuCounters.Length];
                for (int i = 0; i < cpuCounters.Length; i++)
                {
                    try { s.Cpu[i] = Math.Max(0, Math.Min(100, cpuCounters[i].NextValue())); }
                    catch { s.Cpu[i] = double.NaN; }
                }

                Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
                if (Native.GlobalMemoryStatusEx(ref m))
                {
                    s.RamTotal = m.ullTotalPhys;
                    s.RamUsed = m.ullTotalPhys - m.ullAvailPhys;
                }

                long rx, tx;
                ReadNet(out rx, out tx);
                double t = sw.Elapsed.TotalSeconds;
                if (lastRx >= 0 && t > lastT)
                {
                    s.Down = Math.Max(0, (rx - lastRx) / (t - lastT));
                    s.Up = Math.Max(0, (tx - lastTx) / (t - lastT));
                }
                lastRx = rx; lastTx = tx; lastT = t;

                lock (smiGpus)
                {
                    foreach (GpuInfo g in smiGpus.Values) s.Gpus.Add(g);
                }
                try { ReadWddm(s.Gpus); } catch { }

                callback(s);
            }
        }

        // ---------- CPU ----------

        static string ShortCpu(string n)
        {
            n = Regex.Replace(n, @"\((R|TM|tm|r)\)", "");
            n = Regex.Replace(n, @"@.*$", "");
            n = Regex.Replace(n, @"\b(\d+(st|nd|rd|th) Gen|Intel|AMD|CPU|Processor|\d+-Core)\b", "", RegexOptions.IgnoreCase);
            return Regex.Replace(n, @"\s+", " ").Trim();
        }

        void InitCpu()
        {
            List<string> names = new List<string>();
            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher("select Name from Win32_Processor"))
                    foreach (ManagementObject o in q.Get())
                        names.Add(ShortCpu(Convert.ToString(o["Name"])));
            }
            catch { }
            if (names.Count == 0) names.Add("");

            // Przy kilku procesorach licznik ma instancje "0,_Total", "1,_Total" (po jednej na gniazdo / wezel NUMA).
            PerformanceCounterCategory cat = new PerformanceCounterCategory("Processor Information");
            List<int> nodes = new List<int>();
            foreach (string inst in cat.GetInstanceNames())
            {
                Match mm = Regex.Match(inst, @"^(\d+),_Total$");
                if (mm.Success) nodes.Add(int.Parse(mm.Groups[1].Value));
            }
            nodes.Sort();

            string counter = cat.CounterExists("% Processor Utility") ? "% Processor Utility" : "% Processor Time";
            List<PerformanceCounter> list = new List<PerformanceCounter>();
            if (names.Count > 1 && nodes.Count == names.Count)
            {
                foreach (int n in nodes)
                    list.Add(new PerformanceCounter("Processor Information", counter, n + ",_Total", true));
                cpuNames = names.ToArray();
            }
            else
            {
                list.Add(new PerformanceCounter("Processor Information", counter, "_Total", true));
                cpuNames = new string[] { names.Count > 1 ? names.Count + "x " + names[0] : names[0] };
            }
            foreach (PerformanceCounter c in list) { try { c.NextValue(); } catch { } }
            cpuCounters = list.ToArray();
        }

        // ---------- siec ----------

        static readonly string[] netSkip = { "virtual", "hyper-v", "vmware", "vpn", "tap-", "wintun", "wireguard", "loopback", "bluetooth", "npcap" };

        static void ReadNet(out long rx, out long tx)
        {
            rx = 0; tx = 0;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string d = ni.Description.ToLowerInvariant();
                    bool skip = false;
                    foreach (string k in netSkip) if (d.Contains(k)) { skip = true; break; }
                    if (skip) continue;
                    IPInterfaceStatistics st = ni.GetIPStatistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
            }
            catch { }
        }

        // ---------- GPU: nvidia-smi ----------

        static string ShortGpu(string n)
        {
            n = Regex.Replace(n, @"\((R|TM|tm|r)\)", "");
            n = Regex.Replace(n, @"\b(NVIDIA|AMD|Intel)\b", "", RegexOptions.IgnoreCase);
            return Regex.Replace(n, @"\s+", " ").Trim();
        }

        static double Num(string s)
        {
            double v;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        bool StartSmi()
        {
            ProcessStartInfo psi = new ProcessStartInfo("nvidia-smi.exe",
                "--query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits -l 1");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;

            smi = new Process();
            smi.StartInfo = psi;
            smi.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                string[] p = e.Data.Split(',');
                int idx;
                if (p.Length < 6 || !int.TryParse(p[0].Trim(), out idx)) return;
                GpuInfo g = new GpuInfo();
                g.Name = ShortGpu(p[1]);
                g.Util = Num(p[2]);
                g.MemUsedMb = Num(p[3]);
                g.MemTotalMb = Num(p[4]);
                g.Temp = Num(p[5]);
                lock (smiGpus) smiGpus[idx] = g;
            };
            try { if (!smi.Start()) return false; }
            catch { smi = null; return false; }

            // Job object: nvidia-smi konczy sie razem z paskiem, nawet gdy pasek zostanie ubity.
            try
            {
                job = Native.CreateJobObject(IntPtr.Zero, null);
                Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
                Native.SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf(typeof(Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                Native.AssignProcessToJobObject(job, smi.Handle);
            }
            catch { }

            smi.BeginOutputReadLine();
            return true;
        }

        // ---------- GPU: liczniki Windows (karty inne niz NVIDIA) ----------

        void InitWddm(bool haveSmi)
        {
            using (RegistryKey dx = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX"))
            {
                if (dx == null) return;
                foreach (string sub in dx.GetSubKeyNames())
                {
                    using (RegistryKey k = dx.OpenSubKey(sub))
                    {
                        string desc = k.GetValue("Description") as string;
                        object luid = k.GetValue("AdapterLuid");
                        if (desc == null || luid == null) continue;
                        if (desc.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (haveSmi && desc.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        wddmNames[Convert.ToInt64(luid)] = ShortGpu(desc);
                    }
                }
            }
            if (wddmNames.Count > 0) gpuCat = new PerformanceCounterCategory("GPU Engine");
        }

        void ReadWddm(List<GpuInfo> output)
        {
            if (gpuCat == null) return;
            InstanceDataCollectionCollection all = gpuCat.ReadCategory();
            InstanceDataCollection data = all["Utilization Percentage"];
            if (data == null) return;

            Dictionary<string, CounterSample> cur = new Dictionary<string, CounterSample>();
            Dictionary<long, Dictionary<string, double>> perEngine = new Dictionary<long, Dictionary<string, double>>();
            foreach (DictionaryEntry e in data)
            {
                InstanceData d = (InstanceData)e.Value;
                cur[d.InstanceName] = d.Sample;
                Match mm = engRx.Match(d.InstanceName);
                if (!mm.Success) continue;
                long luid = (Convert.ToInt64(mm.Groups[1].Value, 16) << 32) | Convert.ToInt64(mm.Groups[2].Value, 16);
                if (!wddmNames.ContainsKey(luid)) continue;
                CounterSample prev;
                double v = gpuPrev.TryGetValue(d.InstanceName, out prev) ? CounterSample.Calculate(prev, d.Sample) : 0;
                Dictionary<string, double> eng;
                if (!perEngine.TryGetValue(luid, out eng)) { eng = new Dictionary<string, double>(); perEngine[luid] = eng; }
                string key = mm.Groups[3].Value + "_" + mm.Groups[4].Value;
                double acc;
                eng.TryGetValue(key, out acc);
                eng[key] = acc + v;
            }
            gpuPrev = cur;

            // Jak w Menedzerze zadan: obciazenie karty = najbardziej zajety silnik.
            List<long> luids = new List<long>(perEngine.Keys);
            luids.Sort();
            foreach (long luid in luids)
            {
                double max = 0;
                foreach (double v in perEngine[luid].Values) if (v > max) max = v;
                GpuInfo g = new GpuInfo();
                g.Name = wddmNames[luid];
                g.Util = Math.Min(100, max);
                output.Add(g);
            }
        }
    }

    // Jedna pozycja paska: etykieta, nazwa, miernik, wartosc, dodatkowy opis.
    class Cell
    {
        public readonly StackPanel Root = new StackPanel();
        readonly TextBlock value = new TextBlock();
        readonly TextBlock extra = new TextBlock();
        readonly Border fill = new Border();
        const double MeterWidth = 34;

        static readonly Brush Dim = Frozen("#9A9AA2");
        static readonly Brush Text = Frozen("#F2F2F4");
        static readonly Brush Track = Frozen("#3A3A42");
        static readonly Brush Low = Frozen("#5FCB6C");
        static readonly Brush Mid = Frozen("#F2C94C");
        static readonly Brush High = Frozen("#FF6B6B");

        public static Brush Frozen(string hex)
        {
            SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public Cell(string label, string name, bool meter, double valueWidth)
        {
            Root.Orientation = Orientation.Horizontal;
            Root.VerticalAlignment = VerticalAlignment.Center;
            Root.Margin = new Thickness(0, 0, 18, 0);

            TextBlock l = new TextBlock();
            l.Text = label;
            l.Foreground = Dim;
            l.FontWeight = FontWeights.SemiBold;
            l.VerticalAlignment = VerticalAlignment.Center;
            Root.Children.Add(l);

            if (!string.IsNullOrEmpty(name))
            {
                TextBlock n = new TextBlock();
                n.Text = name;
                n.Foreground = Text;
                n.Margin = new Thickness(6, 0, 0, 0);
                n.VerticalAlignment = VerticalAlignment.Center;
                Root.Children.Add(n);
            }

            if (meter)
            {
                Border track = new Border();
                track.Width = MeterWidth;
                track.Height = 5;
                track.CornerRadius = new CornerRadius(2.5);
                track.Background = Track;
                track.Margin = new Thickness(8, 1, 0, 0);
                track.VerticalAlignment = VerticalAlignment.Center;
                fill.HorizontalAlignment = HorizontalAlignment.Left;
                fill.CornerRadius = new CornerRadius(2.5);
                fill.Width = 0;
                track.Child = fill;
                Root.Children.Add(track);
            }

            value.Foreground = Text;
            value.FontWeight = FontWeights.SemiBold;
            value.MinWidth = valueWidth;
            value.TextAlignment = TextAlignment.Right;
            value.Margin = new Thickness(4, 0, 0, 0);
            value.VerticalAlignment = VerticalAlignment.Center;
            value.Text = "–";
            Root.Children.Add(value);

            extra.Foreground = Dim;
            extra.Margin = new Thickness(8, 0, 0, 0);
            extra.VerticalAlignment = VerticalAlignment.Center;
            extra.Visibility = Visibility.Collapsed;
            Root.Children.Add(extra);
        }

        public void SetPercent(double pct, string extraText)
        {
            if (double.IsNaN(pct))
            {
                value.Text = "–";
                fill.Width = 0;
            }
            else
            {
                value.Text = Math.Round(pct).ToString("0") + "%";
                fill.Width = MeterWidth * Math.Max(0, Math.Min(100, pct)) / 100.0;
                fill.Background = pct >= 85 ? High : pct >= 60 ? Mid : Low;
            }
            SetExtra(extraText);
        }

        public void SetText(string text)
        {
            value.Text = text;
        }

        void SetExtra(string text)
        {
            if (string.IsNullOrEmpty(text)) { extra.Visibility = Visibility.Collapsed; return; }
            extra.Text = text;
            extra.Visibility = Visibility.Visible;
        }
    }

    class BarWindow : Window
    {
        const double BarHeight = 30; // wysokosc paska w jednostkach niezaleznych od DPI
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly StackPanel items = new StackPanel();
        readonly TextBlock clock = new TextBlock();
        readonly TextBlock date = new TextBlock();
        readonly List<Cell> cpuCells = new List<Cell>();
        readonly List<Cell> gpuCells = new List<Cell>();
        Cell ramCell, downCell, upCell;
        string layoutKey;

        IntPtr hwnd;
        uint callbackMsg, taskbarCreatedMsg;
        bool registered, dockQueued;
        readonly string monitorName; // nazwa urzadzenia ekranu, do ktorego nalezy ten pasek

        public string MonitorName { get { return monitorName; } }

        public BarWindow(string monitorName)
        {
            this.monitorName = monitorName;

            Title = "SysBar";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0; Top = 0; Width = 800; Height = BarHeight;
            Background = Cell.Frozen("#1C1C20");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 12.5;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            DockPanel root = new DockPanel();
            root.Margin = new Thickness(12, 0, 12, 0);

            StackPanel right = new StackPanel();
            right.Orientation = Orientation.Horizontal;
            right.VerticalAlignment = VerticalAlignment.Center;
            date.Foreground = Cell.Frozen("#9A9AA2");
            date.Margin = new Thickness(0, 0, 10, 0);
            date.VerticalAlignment = VerticalAlignment.Center;
            clock.Foreground = Cell.Frozen("#F2F2F4");
            clock.FontWeight = FontWeights.SemiBold;
            clock.FontSize = 13.5;
            clock.MinWidth = 58;
            clock.TextAlignment = TextAlignment.Right;
            clock.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(date);
            right.Children.Add(clock);
            DockPanel.SetDock(right, Dock.Right);
            root.Children.Add(right);

            // Viewbox zmniejsza zawartosc, gdy pozycji jest wiecej niz miejsca na ekranie.
            items.Orientation = Orientation.Horizontal;
            Viewbox box = new Viewbox();
            box.StretchDirection = StretchDirection.DownOnly;
            box.HorizontalAlignment = HorizontalAlignment.Left;
            box.Margin = new Thickness(0, 0, 16, 0);
            box.Child = items;
            root.Children.Add(box);
            Content = root;

            BuildMenu();
            UpdateClock();
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(250);
            timer.Tick += delegate { UpdateClock(); };
            timer.Start();

            SourceInitialized += OnSourceInitialized;
            ContentRendered += delegate { DockBar(); };
            Closed += OnClosed;
        }

        // ---------- dane ----------

        void UpdateClock()
        {
            DateTime now = DateTime.Now;
            string t = now.ToString("HH:mm:ss");
            if (clock.Text != t)
            {
                clock.Text = t;
                date.Text = now.ToString("ddd, d MMM yyyy");
            }
        }

        static string Speed(double bytesPerSec)
        {
            double kb = bytesPerSec / 1024.0;
            if (kb < 1000) return kb.ToString("0") + " KB/s";
            double mb = kb / 1024.0;
            if (mb < 1000) return mb.ToString(mb < 100 ? "0.0" : "0") + " MB/s";
            return (mb / 1024.0).ToString("0.00") + " GB/s";
        }

        void Rebuild(Snapshot s)
        {
            items.Children.Clear();
            cpuCells.Clear();
            gpuCells.Clear();

            for (int i = 0; i < s.CpuNames.Length; i++)
            {
                Cell c = new Cell(s.CpuNames.Length > 1 ? "CPU" + i : "CPU", s.CpuNames[i], true, 34);
                cpuCells.Add(c);
                items.Children.Add(c.Root);
            }
            ramCell = new Cell("RAM", null, true, 34);
            items.Children.Add(ramCell.Root);
            downCell = new Cell("↓", null, false, 62);
            downCell.Root.Margin = new Thickness(0, 0, 10, 0);
            items.Children.Add(downCell.Root);
            upCell = new Cell("↑", null, false, 62);
            items.Children.Add(upCell.Root);
            for (int i = 0; i < s.Gpus.Count; i++)
            {
                Cell c = new Cell(s.Gpus.Count > 1 ? "GPU" + i : "GPU", s.Gpus[i].Name, true, 34);
                gpuCells.Add(c);
                items.Children.Add(c.Root);
            }
        }

        public void Apply(Snapshot s)
        {
            StringBuilder key = new StringBuilder();
            foreach (string n in s.CpuNames) key.Append(n).Append('|');
            key.Append('#');
            foreach (GpuInfo g in s.Gpus) key.Append(g.Name).Append('|');
            if (key.ToString() != layoutKey)
            {
                layoutKey = key.ToString();
                Rebuild(s);
            }

            for (int i = 0; i < cpuCells.Count; i++) cpuCells[i].SetPercent(s.Cpu[i], null);

            const double GB = 1024.0 * 1024.0 * 1024.0;
            double ramPct = s.RamTotal > 0 ? 100.0 * s.RamUsed / s.RamTotal : double.NaN;
            ramCell.SetPercent(ramPct, (s.RamUsed / GB).ToString("0.0") + " / " + (s.RamTotal / GB).ToString("0") + " GB");

            downCell.SetText(Speed(s.Down));
            upCell.SetText(Speed(s.Up));

            for (int i = 0; i < gpuCells.Count; i++)
            {
                GpuInfo g = s.Gpus[i];
                string extra = "";
                if (!double.IsNaN(g.MemUsedMb) && !double.IsNaN(g.MemTotalMb))
                    extra = (g.MemUsedMb / 1024.0).ToString("0.0") + " / " + (g.MemTotalMb / 1024.0).ToString("0") + " GB";
                if (!double.IsNaN(g.Temp))
                    extra += (extra.Length > 0 ? "  " : "") + g.Temp.ToString("0") + "°C";
                gpuCells[i].SetPercent(g.Util, extra);
            }
        }

        // ---------- ustawienia i menu ----------

        static string ExePath()
        {
            return Process.GetCurrentProcess().MainModule.FileName;
        }

        static bool AutostartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunPath))
                    return k != null && k.GetValue("SysBar") != null;
            }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunPath))
                {
                    if (on) k.SetValue("SysBar", "\"" + ExePath() + "\"");
                    else k.DeleteValue("SysBar", false);
                }
            }
            catch { }
        }

        void BuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            menu.Opened += delegate
            {
                menu.Items.Clear();

                MenuItem top = new MenuItem();
                top.Header = "Na górze ekranu";
                top.IsChecked = !Bars.Bottom;
                top.Click += delegate { Bars.SetBottom(false); };
                menu.Items.Add(top);

                MenuItem bot = new MenuItem();
                bot.Header = "Na dole ekranu";
                bot.IsChecked = Bars.Bottom;
                bot.Click += delegate { Bars.SetBottom(true); };
                menu.Items.Add(bot);

                menu.Items.Add(new Separator());

                MenuItem auto = new MenuItem();
                auto.Header = "Uruchamiaj z Windows";
                auto.IsChecked = AutostartEnabled();
                auto.Click += delegate { SetAutostart(!AutostartEnabled()); };
                menu.Items.Add(auto);

                MenuItem exit = new MenuItem();
                exit.Header = "Zamknij";
                exit.Click += delegate { Bars.Exit(); };
                menu.Items.Add(exit);
            };
            menu.Items.Add(new MenuItem()); // puste menu nie otwiera sie; pozycje powstaja w Opened
            ContextMenu = menu;
        }

        // ---------- dokowanie (AppBar) ----------

        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);

            callbackMsg = Native.RegisterWindowMessage("SysBarAppBarMessage");
            taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            Register();

            // Rezerwacja miejsca nalezy do Eksploratora. Jesli jeszcze nie wstal (start systemu)
            // albo zgubil rejestracje, ponawiamy az do skutku.
            DispatcherTimer retry = new DispatcherTimer();
            retry.Interval = TimeSpan.FromSeconds(3);
            retry.Tick += delegate { if (!registered) Register(); };
            retry.Start();
        }

        void Register()
        {
            Native.APPBARDATA d = NewData();
            Native.SHAppBarMessage(Native.ABM_REMOVE, ref d); // bez tego ponowna rejestracja tego samego okna jest odrzucana
            d.uCallbackMessage = callbackMsg;
            registered = Native.SHAppBarMessage(Native.ABM_NEW, ref d) != UIntPtr.Zero;
            if (registered) DockBar();
        }

        Native.APPBARDATA NewData()
        {
            Native.APPBARDATA d = new Native.APPBARDATA();
            d.cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA));
            d.hWnd = hwnd;
            return d;
        }

        System.Windows.Forms.Screen PickScreen()
        {
            foreach (System.Windows.Forms.Screen s in System.Windows.Forms.Screen.AllScreens)
                if (s.DeviceName == monitorName) return s;
            return null;
        }

        public void DockBar()
        {
            if (!registered) return;
            System.Windows.Forms.Screen screen = PickScreen();
            if (screen == null) return; // ekran odlaczony - Bars.Reconcile zamknie to okno
            System.Drawing.Rectangle b = screen.Bounds;
            bool bottom = Bars.Bottom;

            Native.POINT pt = new Native.POINT();
            pt.x = b.Left + b.Width / 2;
            pt.y = b.Top + b.Height / 2;
            uint dpiX = 96, dpiY = 96;
            try { Native.GetDpiForMonitor(Native.MonitorFromPoint(pt, 2), 0, out dpiX, out dpiY); } catch { }
            int h = (int)Math.Round(BarHeight * dpiY / 96.0);

            Native.APPBARDATA d = NewData();
            d.uEdge = bottom ? Native.ABE_BOTTOM : Native.ABE_TOP;
            d.rc.left = b.Left;
            d.rc.right = b.Right;
            d.rc.top = bottom ? b.Bottom - h : b.Top;
            d.rc.bottom = bottom ? b.Bottom : b.Top + h;
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref d);
            if (bottom) d.rc.top = d.rc.bottom - h; else d.rc.bottom = d.rc.top + h;
            Native.SHAppBarMessage(Native.ABM_SETPOS, ref d);

            Native.SetWindowPos(hwnd, IntPtr.Zero, d.rc.left, d.rc.top, d.rc.right - d.rc.left, d.rc.bottom - d.rc.top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        void QueueDock()
        {
            if (dockQueued) return;
            dockQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                dockQueued = false;
                DockBar();
            }));
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)taskbarCreatedMsg)
            {
                // Eksplorator uruchomil sie ponownie i zapomnial wszystkie paski - rejestrujemy sie od nowa.
                registered = false;
                Register();
            }
            else if (msg == (int)callbackMsg)
            {
                int code = wParam.ToInt32();
                if (code == Native.ABN_POSCHANGED) QueueDock();
                else if (code == Native.ABN_FULLSCREENAPP)
                {
                    // Aplikacja pelnoekranowa (gra, film): pasek schodzi pod spod.
                    bool full = lParam != IntPtr.Zero;
                    Topmost = !full;
                    if (full) Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                }
                handled = true;
            }
            else if (msg == Native.WM_DISPLAYCHANGE)
            {
                Bars.QueueReconcile();
            }
            else if (msg == Native.WM_DPICHANGED)
            {
                QueueDock();
            }
            else if (msg == Native.WM_ACTIVATE && registered)
            {
                Native.APPBARDATA d = NewData();
                Native.SHAppBarMessage(Native.ABM_ACTIVATE, ref d);
            }
            else if (msg == Native.WM_WINDOWPOSCHANGED && registered)
            {
                Native.APPBARDATA d = NewData();
                Native.SHAppBarMessage(Native.ABM_WINDOWPOSCHANGED, ref d);
            }
            return IntPtr.Zero;
        }

        void OnClosed(object sender, EventArgs e)
        {
            if (registered)
            {
                Native.APPBARDATA d = NewData();
                Native.SHAppBarMessage(Native.ABM_REMOVE, ref d);
                registered = false;
            }
        }
    }

    // Pilnuje, zeby na kazdym monitorze byl jeden pasek, i rozsyla do nich pomiary.
    static class Bars
    {
        const string RegPath = @"Software\SysBar";
        static readonly List<BarWindow> windows = new List<BarWindow>();
        static Sampler sampler;
        static Dispatcher dispatcher;
        static bool reconcileQueued, exiting;

        public static bool Bottom;

        public static void Start(bool forceWddm)
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegPath))
                    if (k != null) Bottom = Convert.ToInt32(k.GetValue("Bottom", 0)) != 0;
            }
            catch { }

            Reconcile();
            sampler = new Sampler(delegate(Snapshot s)
            {
                dispatcher.BeginInvoke(new Action<Snapshot>(ApplyAll), s);
            }, forceWddm);
            sampler.Start();
        }

        static void ApplyAll(Snapshot s)
        {
            foreach (BarWindow w in windows) w.Apply(s);
        }

        public static void SetBottom(bool bottom)
        {
            Bottom = bottom;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegPath))
                    k.SetValue("Bottom", bottom ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
            foreach (BarWindow w in windows) w.DockBar();
        }

        // Po podlaczeniu / odlaczeniu monitora: usun paski bez ekranu, dodaj brakujace, popraw pozycje.
        static void Reconcile()
        {
            if (exiting) return;
            System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
            HashSet<string> present = new HashSet<string>();
            foreach (System.Windows.Forms.Screen s in screens) present.Add(s.DeviceName);

            foreach (BarWindow w in windows.ToArray())
            {
                if (present.Contains(w.MonitorName)) continue;
                windows.Remove(w);
                w.Close();
            }
            foreach (System.Windows.Forms.Screen s in screens)
            {
                string name = s.DeviceName;
                if (windows.Exists(delegate(BarWindow w) { return w.MonitorName == name; })) continue;
                BarWindow bar = new BarWindow(name);
                windows.Add(bar);
                bar.Show();
            }
            foreach (BarWindow w in windows) w.DockBar();
        }

        public static void QueueReconcile()
        {
            if (reconcileQueued) return;
            reconcileQueued = true;
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                reconcileQueued = false;
                Reconcile();
            }));
        }

        public static void Exit()
        {
            exiting = true;
            foreach (BarWindow w in windows.ToArray()) w.Close();
            windows.Clear();
            if (sampler != null) sampler.Stop();
            Application.Current.Shutdown();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "SysBar_SingleInstance", out created))
            {
                if (!created) return;
                bool forceWddm = Array.IndexOf(args, "--wddm") >= 0; // pomin nvidia-smi, uzyj licznikow Windows
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Bars.Start(forceWddm);
                app.Run();
            }
        }
    }
}
