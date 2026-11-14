// Прямой режим (без DSX): приложение само
//  • читает DualSense по USB (HID) и подаёт его в виртуальный Xbox 360 через драйвер ViGEmBus,
//  • пишет эффекты курков и подсветку прямо в геймпад, держа хаптику ВКЛЮЧЁННОЙ (power_save_control = 0),
//  • хаптика идёт в USB-аудио геймпада (каналы 3–4, 16 бит, 48 кГц) — см. HapticOutput.
// Геймпад спрятан от игр драйвером HidHide; наше приложение должно быть в его списке разрешённых.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ForzaTriggers
{
    static class NativeIo
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public short Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public short[] Reserved;
            public short NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                         NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                         NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("setupapi.dll", SetLastError = true)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr ov);
        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
        [DllImport("hid.dll")] public static extern bool HidD_SetNumInputBuffers(SafeFileHandle h, int n);
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)] public static extern IntPtr AvSetMmThreadCharacteristics(string task, ref int index);
        [DllImport("avrt.dll")] public static extern bool AvSetMmThreadPriority(IntPtr h, int prio);
        [DllImport("avrt.dll")] public static extern bool AvRevertMmThreadCharacteristics(IntPtr h);

        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

        public static List<string> InterfacePaths(Guid g)
        {
            var res = new List<string>();
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x02 /*PRESENT*/ | 0x10 /*DEVICEINTERFACE*/);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return res;
            try
            {
                for (int i = 0; ; i++)
                {
                    var d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(d);
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                    int req; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    if (req <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero))
                            res.Add(Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4)));
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return res;
        }
    }

    // ------------------------------------------------------------ Виртуальный Xbox 360 (ViGEmBus, протокол из ViGEmClient)
    class ViGEmXbox : IDisposable
    {
        static readonly Guid BusGuid = new Guid("96E42B22-F5E9-42F8-B043-ED0F932F014F");
        // CTL_CODE(FILE_DEVICE_BUS_EXTENDER=0x2A, 0x801+n, METHOD_BUFFERED, FILE_WRITE_DATA)
        const uint IOCTL_PLUGIN = 0x2AA004, IOCTL_UNPLUG = 0x2AA008, IOCTL_CHECK_VERSION = 0x2AA00C,
                   IOCTL_WAIT_READY = 0x2AA010, IOCTL_XUSB_SUBMIT = 0x2AA808;

        SafeFileHandle h;
        uint serial;
        readonly byte[] report = new byte[20];
        readonly byte[] last = new byte[12];
        bool haveLast;

        public string Connect()
        {
            var paths = NativeIo.InterfacePaths(BusGuid);
            if (paths.Count == 0) return Loc.T("драйвер ViGEmBus не найден — установи его", "ViGEmBus driver not found — please install it");
            h = NativeIo.CreateFile(paths[0], NativeIo.GENERIC_READ | NativeIo.GENERIC_WRITE,
                                    NativeIo.FILE_SHARE_READ | NativeIo.FILE_SHARE_WRITE, IntPtr.Zero, NativeIo.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return Loc.T("не открыть ViGEmBus (ошибка ", "cannot open ViGEmBus (error ") + Marshal.GetLastWin32Error() + ")";
            int ret;
            var ver = new byte[8]; BitConverter.GetBytes(8).CopyTo(ver, 0); BitConverter.GetBytes(1).CopyTo(ver, 4);
            if (!NativeIo.DeviceIoControl(h, IOCTL_CHECK_VERSION, ver, 8, null, 0, out ret, IntPtr.Zero))
                return Loc.T("версия ViGEmBus не подходит (ошибка ", "unsupported ViGEmBus version (error ") + Marshal.GetLastWin32Error() + ")";
            for (uint s = 1; s <= 16; s++)
            {
                var plug = new byte[16];
                BitConverter.GetBytes(16).CopyTo(plug, 0); BitConverter.GetBytes(s).CopyTo(plug, 4);
                BitConverter.GetBytes(0).CopyTo(plug, 8);                    // Xbox360Wired
                BitConverter.GetBytes((ushort)0x045E).CopyTo(plug, 12);      // Microsoft
                BitConverter.GetBytes((ushort)0x028E).CopyTo(plug, 14);      // Xbox 360 Controller
                if (NativeIo.DeviceIoControl(h, IOCTL_PLUGIN, plug, 16, null, 0, out ret, IntPtr.Zero)) { serial = s; break; }
            }
            if (serial == 0) return Loc.T("ViGEmBus не дал подключить виртуальный геймпад (ошибка ", "ViGEmBus refused to plug in the virtual gamepad (error ") + Marshal.GetLastWin32Error() + ")";
            var wait = new byte[8]; BitConverter.GetBytes(8).CopyTo(wait, 0); BitConverter.GetBytes(serial).CopyTo(wait, 4);
            NativeIo.DeviceIoControl(h, IOCTL_WAIT_READY, wait, 8, null, 0, out ret, IntPtr.Zero); // на старых версиях может не быть — не критично
            BitConverter.GetBytes(20).CopyTo(report, 0); BitConverter.GetBytes(serial).CopyTo(report, 4);
            return null;
        }

        public bool Submit(ushort buttons, byte lt, byte rt, short lx, short ly, short rx, short ry)
        {
            if (h == null || h.IsInvalid || serial == 0) return false;
            BitConverter.GetBytes(buttons).CopyTo(report, 8);
            report[10] = lt; report[11] = rt;
            BitConverter.GetBytes(lx).CopyTo(report, 12); BitConverter.GetBytes(ly).CopyTo(report, 14);
            BitConverter.GetBytes(rx).CopyTo(report, 16); BitConverter.GetBytes(ry).CopyTo(report, 18);
            bool same = haveLast;
            for (int i = 0; i < 12 && same; i++) same = last[i] == report[8 + i];
            if (same) return true;
            Array.Copy(report, 8, last, 0, 12); haveLast = true;
            int ret;
            return NativeIo.DeviceIoControl(h, IOCTL_XUSB_SUBMIT, report, 20, null, 0, out ret, IntPtr.Zero);
        }

        public void Dispose()
        {
            try
            {
                if (h != null && !h.IsInvalid && serial != 0)
                {
                    var un = new byte[8]; BitConverter.GetBytes(8).CopyTo(un, 0); BitConverter.GetBytes(serial).CopyTo(un, 4);
                    int ret; NativeIo.DeviceIoControl(h, IOCTL_UNPLUG, un, 8, null, 0, out ret, IntPtr.Zero);
                }
            }
            catch { }
            if (h != null) h.Dispose();
            h = null; serial = 0;
        }
    }

    // ------------------------------------------------------------ Сырые эффекты курков (формат прошивки, по TriggerEffectGenerator)
    static class TriggerRaw
    {
        // 11 байт: [0] режим, [1..10] параметры.
        public static void Encode(Eff e, byte[] d, int o)
        {
            for (int i = 0; i < 11; i++) d[o + i] = 0;
            uint active = 0; ulong force = 0;
            switch (e.Kind)
            {
                case EffKind.Curve: // MultiplePositionFeedback, режим 0x21
                    if (e.Zones == null) goto default;
                    for (int z = 0; z < 10; z++)
                    {
                        int s = Util.Clamp(e.Zones[z], 0, 8);
                        if (s > 0) { active |= 1u << z; force |= (ulong)((s - 1) & 7) << (3 * z); }
                    }
                    if (active == 0) goto default;
                    d[o] = 0x21; Put16(d, o + 1, active); Put32(d, o + 3, (uint)force);
                    return;
                case EffKind.Vibe: // Vibration, режим 0x26: зоны от position, амплитуда, частота в байте 9
                    {
                        int pos = Util.Clamp(e.Pos, 0, 9), amp = Util.Clamp(e.Amp, 1, 8);
                        for (int z = pos; z < 10; z++) { active |= 1u << z; force |= (ulong)((amp - 1) & 7) << (3 * z); }
                        d[o] = 0x26; Put16(d, o + 1, active); Put32(d, o + 3, (uint)force); d[o + 9] = (byte)Util.Clamp(e.Freq, 1, 255);
                        return;
                    }
                case EffKind.Machine: // 0x27: маска начала/конца, силы A/B (по 3 бита), частота, период ×0.1 с
                    {
                        int st = Util.Clamp(e.Pos, 0, 8), en = Util.Clamp(Math.Max(e.End, st + 1), st + 1, 9);
                        d[o] = 0x27; Put16(d, o + 1, (1u << st) | (1u << en));
                        d[o + 3] = (byte)((e.Amp & 7) | ((e.AmpB & 7) << 3));
                        d[o + 4] = (byte)Util.Clamp(e.Freq, 1, 255); d[o + 5] = (byte)Util.Clamp(e.Period, 1, 255);
                        return;
                    }
                case EffKind.Gallop: // 0x23: маска начала/конца, вторая нога (биты 0-2), первая (3-5), частота
                    {
                        int st = Util.Clamp(e.Pos, 0, 8), en = Util.Clamp(Math.Max(e.End, st + 1), st + 1, 9);
                        int ff = Util.Clamp(e.Amp, 0, 6), sf = Util.Clamp(e.AmpB, ff + 1, 7);
                        d[o] = 0x23; Put16(d, o + 1, (1u << st) | (1u << en));
                        d[o + 3] = (byte)((sf & 7) | ((ff & 7) << 3)); d[o + 4] = (byte)Util.Clamp(e.Freq, 1, 255);
                        return;
                    }
                default:
                    d[o] = 0x05; // Off
                    return;
            }
        }
        static void Put16(byte[] d, int o, uint v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
        static void Put32(byte[] d, int o, uint v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }
    }

    // ------------------------------------------------------------ Геймпад напрямую
    public class DirectController
    {
        public volatile string State = Loc.T("запуск…", "starting…");
        public volatile bool Ready;
        public volatile int InputReports;
        public volatile string LastButtons = "";

        volatile bool running;
        Thread thread;
        SafeFileHandle rh, wh;
        int outLen = 48;
        readonly object wlock = new object();
        byte[] lastOut;
        long lastOutMs;
        bool lightbarSetupDone;
        readonly Stopwatch clock = Stopwatch.StartNew();
        ViGEmXbox xbox;

        public void Start()
        {
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "dualsense-direct", Priority = ThreadPriority.Highest };
            thread.Start();
        }

        public void Stop()
        {
            // курки — свободные, подсветка — как была
            try { SetOutput(Eff.Off(""), Eff.Off(""), null, 0x04, true); } catch { }  // курки свободны, один центральный диод
            running = false;
            CloseDevice();
            if (thread != null) thread.Join(1500);
            if (xbox != null) { xbox.Dispose(); xbox = null; }
            if (tmTimer != null) { tmTimer.Dispose(); tmTimer = null; }
        }

        void CloseDevice()
        {
            Ready = false;
            lock (wlock) { if (wh != null) { wh.Dispose(); wh = null; } }
            // Called from both the UI thread (Stop) and the reader thread: swap atomically so neither
            // thread can dereference a handle the other one has just nulled (that used to crash on exit).
            var r = Interlocked.Exchange(ref rh, null);
            if (r != null) r.Dispose();
        }

        void Run()
        {
            // Поток ввода — в MMCSS-класс "Games": планировщик не даёт его вытеснить нагрузкой от игры.
            IntPtr mm = IntPtr.Zero;
            try { int ti = 0; mm = NativeIo.AvSetMmThreadCharacteristics("Games", ref ti); if (mm != IntPtr.Zero) NativeIo.AvSetMmThreadPriority(mm, 2 /*HIGH*/); } catch { }
            try
            {
                while (running)
                {
                    try { Session(); }
                    catch (Exception ex) { State = Loc.T("ошибка: ", "error: ") + ex.Message; }
                    CloseDevice();
                    for (int i = 0; i < 15 && running; i++) Thread.Sleep(100);
                }
            }
            finally { try { if (mm != IntPtr.Zero) NativeIo.AvRevertMmThreadCharacteristics(mm); } catch { } }
        }

        static string FindDualSenseUsb(out string note)
        {
            note = null;
            string usb = null;
            Guid hid; NativeIo.HidD_GetHidGuid(out hid);
            foreach (var p in NativeIo.InterfacePaths(hid))
            {
                string lp = p.ToLowerInvariant();
                if (lp.Contains("vid_054c&pid_0ce6") || lp.Contains("vid_054c&pid_0df2")) usb = usb ?? p;
                if (lp.Contains("054c_pid&0ce6") || lp.Contains("054c_pid&0df2")) note = Loc.T("DualSense подключён по Bluetooth — для прямого режима нужен USB-кабель", "DualSense is connected via Bluetooth — direct mode needs a USB cable");
            }
            return usb;
        }

        void Session()
        {
            if (Process.GetProcessesByName("DSX").Length > 0) { State = Loc.T("запущен DSX — закрой его (в прямом режиме он не нужен)", "DSX is running — close it (not needed in direct mode)"); return; }

            string note;
            string path = FindDualSenseUsb(out note);
            if (path == null) { State = note ?? Loc.T("DualSense по USB не найден (или HidHide не разрешил приложению его видеть)", "No USB DualSense found (or HidHide is hiding it from this app)"); return; }

            var rd = NativeIo.CreateFile(path, NativeIo.GENERIC_READ | NativeIo.GENERIC_WRITE, NativeIo.FILE_SHARE_READ | NativeIo.FILE_SHARE_WRITE, IntPtr.Zero, NativeIo.OPEN_EXISTING, 0, IntPtr.Zero);
            if (rd.IsInvalid) { State = Loc.T("не открыть DualSense (ошибка ", "cannot open DualSense (error ") + Marshal.GetLastWin32Error() + Loc.T(") — разреши приложение в HidHide", ") — allow this app in HidHide"); rd.Dispose(); return; }
            rh = rd;
            // По умолчанию драйвер копит до 32 входных отчётов: если поток хоть раз отстал, дальше читаем
            // устаревшие данные (инпут-лаг). Минимальная очередь (2) — всегда почти свежий отчёт.
            NativeIo.HidD_SetNumInputBuffers(rd, 2);
            var w = NativeIo.CreateFile(path, NativeIo.GENERIC_READ | NativeIo.GENERIC_WRITE, NativeIo.FILE_SHARE_READ | NativeIo.FILE_SHARE_WRITE, IntPtr.Zero, NativeIo.OPEN_EXISTING, 0, IntPtr.Zero);
            if (w.IsInvalid) { State = Loc.T("не открыть DualSense на запись (ошибка ", "cannot open DualSense for writing (error ") + Marshal.GetLastWin32Error() + ")"; w.Dispose(); return; }
            lock (wlock) wh = w;

            int inLen = 64;
            IntPtr pp;
            if (NativeIo.HidD_GetPreparsedData(rd, out pp))
            {
                NativeIo.HIDP_CAPS caps;
                if (NativeIo.HidP_GetCaps(pp, out caps) == 0x110000 /*HIDP_STATUS_SUCCESS*/)
                { inLen = Math.Max(16, (int)caps.InputReportByteLength); outLen = Math.Max(48, (int)caps.OutputReportByteLength); }
                NativeIo.HidD_FreePreparsedData(pp);
            }

            if (xbox == null)
            {
                xbox = new ViGEmXbox();
                string err = xbox.Connect();
                if (err != null) { xbox.Dispose(); xbox = null; State = err; return; }
            }

            lightbarSetupDone = false; lastOut = null;
            Ready = true;
            State = note != null
                ? Loc.T("⚠ геймпад подключён ещё и по Bluetooth — хаптика по USB не пойдёт. Выключи Bluetooth и переподключи кабель",
                        "⚠ the gamepad is also paired via Bluetooth — USB haptics won't work. Turn Bluetooth off and replug the cable")
                : Loc.T("DualSense USB → виртуальный Xbox 360 · хаптика 16 бит", "DualSense USB → virtual Xbox 360 · 16-bit haptics");

            var buf = new byte[inLen];
            ushort lastBtn = 0xFFFF;
            StartTimingLog();
            long prevRead = -1;
            while (running)
            {
                int n;
                var h = rh;
                if (h == null || !NativeIo.ReadFile(h, buf, inLen, out n, IntPtr.Zero) || n < 11)
                {
                    if (running) State = Loc.T("DualSense отключён — жду переподключения", "DualSense disconnected — waiting for it to come back");
                    return;
                }
                long tRead = clock.ElapsedTicks;
                if (buf[0] != 0x01) continue;
                InputReports++;
                ushort btn; byte lt, rt; short lx, ly, rx, ry;
                MapToXbox(buf, out btn, out lt, out rt, out lx, out ly, out rx, out ry);
                xbox.Submit(btn, lt, rt, lx, ly, rx, ry);
                long tDone = clock.ElapsedTicks;
                // замер: интервал между отчётами, время передачи в ViGEm, отчёты, прочитанные "пачкой" (были в очереди)
                tmReports++;
                if (prevRead >= 0)
                {
                    long gap = tRead - prevRead;
                    if (gap > tmMaxGap) tmMaxGap = gap;
                    if (gap > Stopwatch.Frequency * 8 / 1000) tmGapsOver8++;
                    if (gap < Stopwatch.Frequency / 2000) tmBurst++;
                }
                prevRead = tRead;
                long sub = tDone - tRead;
                if (sub > tmMaxSubmit) tmMaxSubmit = sub;
                if (btn != lastBtn) { lastBtn = btn; LastButtons = DescribeButtons(btn); }
            }
        }

        // ---------- замер задержек ввода: раз в секунду строка в input-timing.log рядом с exe
        int tmReports, tmGapsOver8, tmBurst, tmWrites;
        long tmMaxGap, tmMaxSubmit, tmMaxWrite;
        Timer tmTimer;

        void StartTimingLog()
        {
            if (tmTimer != null) return;
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "input-timing.log");
            try { System.IO.File.WriteAllText(path, "# время | отчётов/с | макс. пауза мс | пауз>8мс | пачкой (<0.5мс) | макс. ViGEm мс | записей курков/с | макс. запись мс\r\n"); } catch { }
            double f = Stopwatch.Frequency / 1000.0;
            tmTimer = new Timer(_ =>
            {
                int rep = Interlocked.Exchange(ref tmReports, 0), g8 = Interlocked.Exchange(ref tmGapsOver8, 0),
                    bu = Interlocked.Exchange(ref tmBurst, 0), wr = Interlocked.Exchange(ref tmWrites, 0);
                long mg = Interlocked.Exchange(ref tmMaxGap, 0), ms = Interlocked.Exchange(ref tmMaxSubmit, 0), mw = Interlocked.Exchange(ref tmMaxWrite, 0);
                if (rep == 0 && wr == 0) return;
                string line = DateTime.Now.ToString("HH:mm:ss") + " | " + rep + " | " + (mg / f).ToString("F1") + " | " + g8 + " | " + bu
                            + " | " + (ms / f).ToString("F2") + " | " + wr + " | " + (mw / f).ToString("F2") + "\r\n";
                try
                {
                    var fi = new System.IO.FileInfo(path);
                    if (fi.Exists && fi.Length > 2000000) System.IO.File.WriteAllText(path, "");
                    System.IO.File.AppendAllText(path, line);
                }
                catch { }
            }, null, 1000, 1000);
        }

        // USB-отчёт DualSense 0x01: [1..4] стики, [5] L2, [6] R2, [8] крестовина+фигуры, [9] плечи/меню/стики, [10] PS/тачпад.
        static void MapToXbox(byte[] r, out ushort b, out byte lt, out byte rt, out short lx, out short ly, out short rx, out short ry)
        {
            lx = Axis(r[1], false); ly = Axis(r[2], true); rx = Axis(r[3], false); ry = Axis(r[4], true);
            lt = r[5]; rt = r[6];
            int b0 = r[8], b1 = r[9], b2 = r[10];
            int hat = b0 & 0x0F;
            b = 0;
            if (hat == 0 || hat == 1 || hat == 7) b |= 0x0001;  // вверх
            if (hat == 3 || hat == 4 || hat == 5) b |= 0x0002;  // вниз
            if (hat == 5 || hat == 6 || hat == 7) b |= 0x0004;  // влево
            if (hat == 1 || hat == 2 || hat == 3) b |= 0x0008;  // вправо
            if ((b0 & 0x10) != 0) b |= 0x4000;  // квадрат → X
            if ((b0 & 0x20) != 0) b |= 0x1000;  // крест → A
            if ((b0 & 0x40) != 0) b |= 0x2000;  // круг → B
            if ((b0 & 0x80) != 0) b |= 0x8000;  // треугольник → Y
            if ((b1 & 0x01) != 0) b |= 0x0100;  // L1 → LB
            if ((b1 & 0x02) != 0) b |= 0x0200;  // R1 → RB
            if ((b1 & 0x10) != 0) b |= 0x0020;  // Create → Back
            if ((b1 & 0x20) != 0) b |= 0x0010;  // Options → Start
            if ((b1 & 0x40) != 0) b |= 0x0040;  // L3
            if ((b1 & 0x80) != 0) b |= 0x0080;  // R3
            if ((b2 & 0x01) != 0) b |= 0x0400;  // PS → Guide
            if ((b2 & 0x02) != 0) b |= 0x0020;  // клик тачпада → Back (карта/меню в Forza)
        }

        static short Axis(byte v, bool invert)
        {
            int full = (v << 8) | v;            // 0..65535
            int s = invert ? 32767 - full : full - 32768;
            return (short)Math.Max(-32768, Math.Min(32767, s));
        }

        static string DescribeButtons(ushort b)
        {
            var parts = new List<string>();
            string[] names = { "↑", "↓", "←", "→", "Start", "Back", "L3", "R3", "LB", "RB", "Guide", "?", "A", "B", "X", "Y" };
            for (int i = 0; i < 16; i++) if ((b & (1 << i)) != 0) parts.Add(names[i]);
            return parts.Count == 0 ? "—" : string.Join(" ", parts.ToArray());
        }

        public int OutputLength { get { return outLen; } }

        // Для диагностики: записать произвольный выходной отчёт.
        public bool WriteRaw(byte[] o)
        {
            lock (wlock)
            {
                if (wh == null || wh.IsInvalid) return false;
                int written;
                bool ok = NativeIo.WriteFile(wh, o, o.Length, out written, IntPtr.Zero);
                lastOut = null;
                return ok;
            }
        }

        // Курки + (опционально) подсветка. Всегда держит хаптику включённой: power_save_control = 0.
        public void SetOutput(Eff left, Eff right, int[] rgb, int playerLeds = -1, bool force = false)
        {
            var o = new byte[outLen];
            o[0] = 0x02;
            o[1] = 0x04 | 0x08;                         // valid_flag0: правый и левый курок (без режима вибрации-эмуляции)
            o[2] = 0x02;                                // valid_flag1: POWER_SAVE_CONTROL_ENABLE
            o[10] = 0x00;                               // power_save_control: ничего не усыплять, хаптика активна
            TriggerRaw.Encode(right, o, 11);
            TriggerRaw.Encode(left, o, 22);
            if (rgb != null)
            {
                o[2] |= 0x04;                           // LIGHTBAR_CONTROL_ENABLE
                if (!lightbarSetupDone) { o[39] |= 0x02; o[42] = 0x02; }   // LIGHTBAR_SETUP: погасить заставку прошивки
                o[45] = (byte)rgb[0]; o[46] = (byte)rgb[1]; o[47] = (byte)rgb[2];
            }
            if (playerLeds >= 0)
            {
                o[2] |= 0x10;                           // PLAYER_INDICATOR_CONTROL_ENABLE
                o[44] = (byte)(playerLeds & 0x1F);      // 5 светодиодов под тачпадом
            }
            long now = clock.ElapsedMilliseconds;
            if (!force && lastOut != null && now - lastOutMs < 1000)
            {
                bool same = lastOut.Length == o.Length;
                for (int i = 0; i < o.Length && same; i++) same = lastOut[i] == o[i];
                if (same) return;
            }
            lock (wlock)
            {
                if (wh == null || wh.IsInvalid) return;
                int written;
                long w0 = clock.ElapsedTicks;
                bool okw = NativeIo.WriteFile(wh, o, o.Length, out written, IntPtr.Zero);
                long wt = clock.ElapsedTicks - w0;
                tmWrites++; if (wt > tmMaxWrite) tmMaxWrite = wt;
                if (okw)
                {
                    lastOut = o; lastOutMs = now;
                    if (rgb != null) lightbarSetupDone = true;
                }
            }
        }
    }
}
