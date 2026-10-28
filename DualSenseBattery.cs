// DualSense Battery – icône de zone de notification affichant la batterie
// d'une manette PS5 (DualSense / DualSense Edge) connectée en USB ou Bluetooth.
// Compilable avec le csc.exe fourni par Windows (.NET Framework 4.x, C# 5).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DualSenseBattery
{
    enum Connection { Usb, Bluetooth }

    class ControllerState
    {
        public string Path;
        public Connection Connection;
        public bool Edge;
        public int Percent = -1;   // -1 = inconnu
        public bool Charging;
        public bool Full;
        public string Error;
        public byte RawStatus;
        public uint SensorTimestamp;
        public string LightResult;

        public string Name { get { return Edge ? "DualSense Edge" : "DualSense"; } }
        public string Link { get { return Connection == Connection.Usb ? "USB" : "Bluetooth"; } }

        public string Status
        {
            get
            {
                if (Percent < 0) return Error ?? "batterie inconnue";
                string s = Percent + " %";
                if (Full) s += " – chargée";
                else if (Charging) s += " – en charge";
                if (Error != null) s += " – " + Error;
                return s;
            }
        }

        public string Describe() { return Name + " (" + Link + ") : " + Status; }
    }

    static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ_WRITE = 3, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
        [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        public static List<string> EnumerateHidPaths()
        {
            var paths = new List<string>();
            Guid guid;
            HidD_GetHidGuid(out guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return paths;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(data);
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
                {
                    int required;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W : 8 en 64 bits, 6 en 32 bits
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, IntPtr.Zero))
                            paths.Add(Marshal.PtrToStringUni(detail + 4));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        public static SafeFileHandle Open(string path, uint access, bool overlapped)
        {
            return CreateFile(path, access, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING,
                overlapped ? FILE_FLAG_OVERLAPPED : 0, IntPtr.Zero);
        }

        public static bool GetCaps(SafeFileHandle h, out HIDP_CAPS caps)
        {
            caps = new HIDP_CAPS();
            IntPtr data;
            if (!HidD_GetPreparsedData(h, out data)) return false;
            try { return HidP_GetCaps(data, out caps) == 0x00110000; } // HIDP_STATUS_SUCCESS
            finally { HidD_FreePreparsedData(data); }
        }
    }

    static class DualSense
    {
        const ushort SonyVid = 0x054C, DualSensePid = 0x0CE6, DualSenseEdgePid = 0x0DF2;
        const string BluetoothHidGuid = "{00001124-0000-1000-8000-00805f9b34fb}";

        // Dernière couleur envoyée à chaque manette : on ne renvoie la lumière qu'à la
        // connexion ou quand la tranche de batterie change, pour laisser la main aux jeux.
        static readonly Dictionary<string, Color> sentLight = new Dictionary<string, Color>();

        public static List<ControllerState> Scan(bool driveLightbar)
        {
            var result = new List<ControllerState>();
            foreach (string path in Native.EnumerateHidPaths())
            {
                var attr = new Native.HIDD_ATTRIBUTES();
                attr.Size = Marshal.SizeOf(attr);
                using (var h = Native.Open(path, 0, false))
                {
                    if (h.IsInvalid || !Native.HidD_GetAttributes(h, ref attr)) continue;
                }
                if (attr.VendorID != SonyVid || (attr.ProductID != DualSensePid && attr.ProductID != DualSenseEdgePid)) continue;

                var st = new ControllerState { Path = path, Edge = attr.ProductID == DualSenseEdgePid };
                st.Connection = path.IndexOf(BluetoothHidGuid, StringComparison.OrdinalIgnoreCase) >= 0
                    ? Connection.Bluetooth : Connection.Usb;
                try { ReadBattery(st, driveLightbar); }
                catch (Exception ex) { st.Error = "lecture impossible (" + ex.GetType().Name + ")"; }
                result.Add(st);
            }
            foreach (string gone in sentLight.Keys.Where(p => !result.Any(s => s.Path == p)).ToList())
                sentLight.Remove(gone);
            return result;
        }

        static void ReadBattery(ControllerState st, bool driveLightbar)
        {
            bool writable = true;
            var h = Native.Open(st.Path, Native.GENERIC_READ | Native.GENERIC_WRITE, true);
            if (h.IsInvalid) { h.Dispose(); writable = false; h = Native.Open(st.Path, Native.GENERIC_READ, true); }
            if (h.IsInvalid) { h.Dispose(); st.Error = "accès refusé (utilisée en exclusif ?)"; return; }

            using (h)
            {
                Native.HIDP_CAPS caps;
                if (!Native.GetCaps(h, out caps) || caps.InputReportByteLength == 0) { st.Error = "infos HID illisibles"; return; }
                if (caps.InputReportByteLength > 64) st.Connection = Connection.Bluetooth;

                // En Bluetooth, la manette n'envoie que le rapport "simple" 0x01 (sans batterie)
                // tant qu'on n'a pas lu le rapport de calibration 0x05 : cette lecture la fait
                // passer en rapport étendu 0x31, qui contient l'état de la batterie.
                if (st.Connection == Connection.Bluetooth && caps.FeatureReportByteLength > 0)
                {
                    var feature = new byte[caps.FeatureReportByteLength];
                    feature[0] = 0x05;
                    Native.HidD_GetFeature(h, feature, feature.Length);
                }

                using (var fs = new FileStream(h, writable ? FileAccess.ReadWrite : FileAccess.Read, 1, true))
                {
                    var buf = new byte[caps.InputReportByteLength];
                    int deadline = Environment.TickCount + 1500;
                    while (true)
                    {
                        int remaining = deadline - Environment.TickCount;
                        if (remaining <= 0) break;
                        var read = fs.ReadAsync(buf, 0, buf.Length);
                        if (!read.Wait(remaining)) { Native.CancelIoEx(h, IntPtr.Zero); break; }
                        if (Parse(buf, read.Result, st))
                        {
                            if (driveLightbar && writable) UpdateLightbar(fs, st, caps.OutputReportByteLength);
                            return;
                        }
                    }
                }
                st.Error = "pas de données de batterie reçues";
            }
        }

        static Color? LightbarColor(ControllerState st)
        {
            if (st.Percent < 0) return null;
            if (st.Charging || st.Full) return Color.FromArgb(0, 70, 255);   // bleu : en charge
            if (st.Percent <= 20) return Color.FromArgb(255, 0, 0);          // rouge
            if (st.Percent <= 50) return Color.FromArgb(255, 90, 0);         // orange
            return Color.FromArgb(0, 255, 30);                                // vert
        }

        static void UpdateLightbar(FileStream fs, ControllerState st, int outputLength)
        {
            Color? wanted = LightbarColor(st);
            Color previous;
            bool known = sentLight.TryGetValue(st.Path, out previous);
            if (!wanted.HasValue || (known && previous == wanted.Value)) { st.LightResult = "inchangée"; return; }
            try
            {
                if (!known)
                {
                    // Libère les LED et coupe l'animation par défaut du firmware avant de
                    // prendre la main sur la barre lumineuse et les voyants joueur.
                    WriteOutput(fs, st.Connection, outputLength, delegate(byte[] r, int o)
                    {
                        r[o + 1] = 0x08;    // valid_flag1 : libération des LED
                        r[o + 38] = 0x02;   // valid_flag2 : réglage de la barre lumineuse
                        r[o + 41] = 0x02;   // lightbar_setup : extinction en fondu
                    });
                    Thread.Sleep(50);
                }
                Color c = wanted.Value;
                WriteOutput(fs, st.Connection, outputLength, delegate(byte[] r, int o)
                {
                    r[o + 1] = 0x04 | 0x10; // valid_flag1 : couleur de la barre lumineuse + voyants joueur
                    r[o + 43] = 0x04;       // voyant joueur central allumé (joueur 1), il cesse de clignoter
                    r[o + 44] = c.R;
                    r[o + 45] = c.G;
                    r[o + 46] = c.B;
                });
                sentLight[st.Path] = c;
                st.LightResult = "réglée en " + c.R + "," + c.G + "," + c.B;
            }
            catch (Exception ex) { st.LightResult = "échec (" + ex.Message + ")"; }
        }

        // Rapport de sortie DualSense : 0x02 en USB ; 0x31 en Bluetooth, avec octet 0x02,
        // commandes dès l'octet 2 et CRC32 final (calculé avec l'octet d'en-tête 0xA2),
        // comme le fait SDL. Windows impose de compléter jusqu'à OutputReportByteLength.
        static void WriteOutput(FileStream fs, Connection connection, int length, Action<byte[], int> fill)
        {
            byte[] r;
            if (connection == Connection.Usb)
            {
                r = new byte[Math.Max(length, 48)];
                r[0] = 0x02;
                fill(r, 1);
            }
            else
            {
                r = new byte[Math.Max(length, 78)];
                r[0] = 0x31;
                r[1] = 0x02;
                fill(r, 2);
                BitConverter.GetBytes(Crc32(0xA2, r, 74)).CopyTo(r, 74);
            }
            fs.Write(r, 0, r.Length);
        }

        static uint Crc32(byte seed, byte[] data, int count)
        {
            uint crc = Crc32Step(0xFFFFFFFF, seed);
            for (int i = 0; i < count; i++) crc = Crc32Step(crc, data[i]);
            return ~crc;
        }

        static uint Crc32Step(uint crc, byte b)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            return crc;
        }

        static bool Parse(byte[] r, int length, ControllerState st)
        {
            int index;
            if (r[0] == 0x01 && length >= 64 && st.Connection == Connection.Usb) index = 53;
            else if (r[0] == 0x31 && length >= 55) index = 54;
            else return false;

            st.RawStatus = r[index];
            st.SensorTimestamp = BitConverter.ToUInt32(r, index - 25);  // horloge interne de la manette
            int level = r[index] & 0x0F;      // 0..10, par paliers de 10 %
            int status = r[index] >> 4;
            st.Percent = Math.Min(level * 10, 100);
            switch (status)
            {
                case 0x0: break;                                              // sur batterie
                case 0x1: st.Charging = true; break;
                case 0x2: st.Full = true; st.Percent = 100; break;
                case 0xA: case 0xB: st.Error = "charge suspendue (température/tension)"; break;
                default: st.Error = "erreur de charge"; break;
            }
            return true;
        }
    }

    class TrayContext : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "DualSenseBattery";
        const int PollMs = 5000;

        readonly Control invoker = new Control();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem startupItem;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly HashSet<string> connected = new HashSet<string>();
        readonly HashSet<string> lowWarned = new HashSet<string>();
        IntPtr currentHIcon = IntPtr.Zero;
        string currentIconKey;
        volatile bool stopping;

        public TrayContext()
        {
            invoker.CreateControl();

            var menu = new ContextMenuStrip();
            var title = new ToolStripMenuItem("Batterie manette PS5") { Enabled = false };
            var refresh = new ToolStripMenuItem("Actualiser maintenant", null, delegate { wake.Set(); });
            startupItem = new ToolStripMenuItem("Lancer au démarrage de Windows", null, delegate { ToggleStartup(); });
            startupItem.Checked = IsStartupEnabled();
            var quit = new ToolStripMenuItem("Quitter", null, delegate { Quit(); });
            menu.Items.AddRange(new ToolStripItem[] { title, new ToolStripSeparator(), refresh, startupItem, new ToolStripSeparator(), quit });

            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowDetails(); };
            Apply(new List<ControllerState>(), true);
            tray.Visible = true;

            var worker = new Thread(Loop) { IsBackground = true, Name = "DualSense poll" };
            worker.Start();
        }

        void Loop()
        {
            while (!stopping)
            {
                List<ControllerState> states;
                try { states = DualSense.Scan(true); }
                catch { states = new List<ControllerState>(); }
                if (stopping) break;
                try { invoker.BeginInvoke((Action)delegate { Apply(states, false); }); }
                catch (InvalidOperationException) { break; }
                wake.WaitOne(PollMs);
            }
        }

        List<ControllerState> last = new List<ControllerState>();

        void Apply(List<ControllerState> states, bool initial)
        {
            if (stopping) return;
            last = states;

            // Notifications de connexion / batterie faible
            var paths = new HashSet<string>(states.Select(s => s.Path));
            foreach (var st in states)
            {
                if (!connected.Contains(st.Path) && !initial)
                    tray.ShowBalloonTip(4000, "Manette PS5 connectée", st.Name + " (" + st.Link + ") : " + st.Status, ToolTipIcon.Info);
                bool low = st.Percent >= 0 && st.Percent <= 20 && !st.Charging && !st.Full;
                if (low && !lowWarned.Contains(st.Path))
                {
                    lowWarned.Add(st.Path);
                    tray.ShowBalloonTip(6000, "Batterie faible", st.Name + " : " + st.Percent + " %. Pense à la recharger.", ToolTipIcon.Warning);
                }
                else if (!low) lowWarned.Remove(st.Path);
            }
            connected.Clear();
            connected.UnionWith(paths);
            lowWarned.IntersectWith(paths);

            // L'icône montre la manette la plus déchargée
            var shown = states.OrderBy(s => s.Percent < 0 ? int.MaxValue : s.Percent).FirstOrDefault();
            SetIcon(shown);

            string tip = states.Count == 0
                ? "Aucune manette PS5 connectée"
                : string.Join("\n", states.Select(s => s.Name + " (" + s.Link + ") : " + s.Status));
            tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
        }

        void ShowDetails()
        {
            string text = last.Count == 0
                ? "Aucune manette PS5 connectée."
                : string.Join("\n", last.Select(s => s.Describe()));
            tray.ShowBalloonTip(4000, "Batterie manette PS5", text, ToolTipIcon.None);
        }

        void SetIcon(ControllerState st)
        {
            string text; Color bg;
            if (st == null) { text = ""; bg = Color.FromArgb(125, 125, 125); }
            else if (st.Percent < 0) { text = "?"; bg = Color.FromArgb(125, 125, 125); }
            else
            {
                text = st.Percent.ToString();
                if (st.Charging || st.Full) bg = Color.FromArgb(0, 110, 210);        // bleu : en charge
                else if (st.Percent <= 20) bg = Color.FromArgb(205, 35, 35);         // rouge
                else if (st.Percent <= 50) bg = Color.FromArgb(225, 130, 0);         // orange
                else bg = Color.FromArgb(25, 145, 60);                               // vert
            }

            string key = text + "|" + bg.ToArgb();
            if (key == currentIconKey) return;
            currentIconKey = key;

            IntPtr hIcon;
            Icon icon = Render(text, bg, out hIcon);
            Icon old = tray.Icon;
            IntPtr oldHandle = currentHIcon;
            tray.Icon = icon;
            currentHIcon = hIcon;
            if (old != null) old.Dispose();
            if (oldHandle != IntPtr.Zero) Native.DestroyIcon(oldHandle);
        }

        static Icon Render(string text, Color bg, out IntPtr hIcon)
        {
            int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = IconRenderer.Draw(text, bg, s))
            {
                hIcon = bmp.GetHicon();
                return Icon.FromHandle(hIcon);
            }
        }

        static bool IsStartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunValue) != null;
        }

        void ToggleStartup()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (IsStartupEnabled()) k.DeleteValue(RunValue, false);
                else k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
            }
            startupItem.Checked = IsStartupEnabled();
        }

        void Quit()
        {
            stopping = true;
            wake.Set();
            tray.Visible = false;
            tray.Dispose();
            if (currentHIcon != IntPtr.Zero) Native.DestroyIcon(currentHIcon);
            ExitThread();
        }
    }

    // Dessine une silhouette de manette PS5 colorée selon le niveau, avec le pourcentage
    // en blanc cerné de noir par-dessus pour qu'il reste lisible même à 16 px.
    public static class IconRenderer
    {
        public static Bitmap Draw(string text, Color fill, int s)
        {
            var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Formes en coordonnées 0..1, puis mises à l'échelle de l'icône
                var toPixels = new Matrix();
                toPixels.Scale(s, s);
                var shapes = new List<GraphicsPath>
                {
                    RoundedRect(0.10f, 0.13f, 0.27f, 0.16f, 0.07f),   // gâchette L1
                    RoundedRect(0.63f, 0.13f, 0.27f, 0.16f, 0.07f),   // gâchette R1
                    RoundedRect(0.02f, 0.20f, 0.96f, 0.46f, 0.17f),   // corps
                    Grip(0.21f, 15f),                                  // poignée gauche
                    Grip(0.79f, -15f),                                 // poignée droite
                };
                foreach (var p in shapes) p.Transform(toPixels);

                Color outline = Color.FromArgb(230, fill.R / 3, fill.G / 3, fill.B / 3);
                using (var pen = new Pen(outline, Math.Max(1f, s / 16f) * 2) { LineJoin = LineJoin.Round })
                using (var brush = new SolidBrush(fill))
                {
                    // Contours d'abord (épaisseur doublée, la moitié intérieure est recouverte
                    // par le remplissage) : seul le contour extérieur de l'union reste visible.
                    foreach (var p in shapes) g.DrawPath(pen, p);
                    foreach (var p in shapes) g.FillPath(brush, p);
                }
                foreach (var p in shapes) p.Dispose();

                if (!string.IsNullOrEmpty(text))
                {
                    float em = s * (text.Length >= 3 ? 0.50f : 0.66f);
                    using (var family = new FontFamily("Segoe UI"))
                    using (var textPath = new GraphicsPath())
                    {
                        textPath.AddString(text, family, (int)FontStyle.Bold, em, PointF.Empty, StringFormat.GenericTypographic);
                        RectangleF b = textPath.GetBounds();
                        var center = new Matrix();
                        center.Translate(s * 0.5f - b.X - b.Width / 2, s * 0.47f - b.Y - b.Height / 2);
                        textPath.Transform(center);

                        using (var halo = new Pen(Color.FromArgb(235, 0, 0, 0), Math.Max(1.6f, s / 9f)) { LineJoin = LineJoin.Round })
                            g.DrawPath(halo, textPath);
                        g.FillPath(Brushes.White, textPath);
                    }
                }
            }
            return bmp;
        }

        static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static GraphicsPath Grip(float cx, float angle)
        {
            const float cy = 0.67f, w = 0.33f, h = 0.54f;
            var p = new GraphicsPath();
            p.AddEllipse(cx - w / 2, cy - h / 2, w, h);
            var m = new Matrix();
            m.RotateAt(angle, new PointF(cx, cy));
            p.Transform(m);
            return p;
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Mode diagnostic : DualSenseBattery.exe --dump [--light] | more
            if (args.Length > 0 && args[0] == "--dump")
            {
                try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
                var states = DualSense.Scan(args.Contains("--light"));
                if (states.Count == 0) Console.WriteLine("Aucune manette PS5 détectée.");
                foreach (var st in states)
                    Console.WriteLine(st.Describe() + "   [octet brut 0x" + st.RawStatus.ToString("X2")
                        + ", horloge manette " + st.SensorTimestamp + "]"
                        + (st.LightResult != null ? "   lumière : " + st.LightResult : ""));
                return 0;
            }

            bool created;
            using (new Mutex(true, "DualSenseBatteryTray_SingleInstance", out created))
            {
                if (!created) return 0;
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
            return 0;
        }
    }
}
