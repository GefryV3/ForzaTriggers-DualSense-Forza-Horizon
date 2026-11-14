// Хаптика DualSense: вибромоторы (voice coil) — это каналы 3 и 4 (тыловые L/R) USB-аудиоустройства геймпада.
// Здесь: вывод через WASAPI (без зависимостей) + синтезатор тактильных сигналов из телеметрии.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace ForzaTriggers
{
    // ============================================================ WASAPI (COM interop)
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore props);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit)]
    struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(16)] public IntPtr p2;
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, int flags);
    }

    // Поток вывода на вибромоторы DualSense. Сам находит устройство и переподключается при выдёргивании кабеля.
    public class HapticOutput
    {
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

        static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        static readonly Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        static readonly Guid SUBTYPE_FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71");
        static readonly Guid SUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00aa00389b71");

        public readonly HapticSynth Synth;
        public volatile string State = Loc.T("запуск…", "starting…");
        public volatile bool Ok;
        volatile bool running;
        Thread thread;

        public HapticOutput(HapticSynth synth) { Synth = synth; }

        public void Start()
        {
            running = true;
            thread = new Thread(Run) { IsBackground = true, Name = "haptics", Priority = ThreadPriority.Highest };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null) thread.Join(1000);
        }

        void Run()
        {
            while (running)
            {
                try { Session(); }
                catch (Exception ex) { Ok = false; State = Loc.T("ошибка: ", "error: ") + ex.Message; }
                for (int i = 0; i < 20 && running; i++) Thread.Sleep(100); // повтор через 2 с
            }
        }

        static IMMDevice FindDualSense(out string name)
        {
            name = null;
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDeviceCollection col;
            if (en.EnumAudioEndpoints(0 /*render*/, 1 /*active*/, out col) != 0) return null;
            uint n; col.GetCount(out n);
            var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 }; // FriendlyName
            for (uint i = 0; i < n; i++)
            {
                IMMDevice d; if (col.Item(i, out d) != 0) continue;
                IPropertyStore ps; if (d.OpenPropertyStore(0, out ps) != 0) continue;
                PropVariant pv;
                if (ps.GetValue(ref key, out pv) == 0)
                {
                    string fn = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null;
                    PropVariantClear(ref pv);
                    if (fn != null && (fn.IndexOf("DualSense", StringComparison.OrdinalIgnoreCase) >= 0 || fn.IndexOf("Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0))
                    { name = fn; return d; }
                }
            }
            return null;
        }

        // Маршрут: через DSX (пишем стерео в выход, который DSX перехватывает) или напрямую в каналы 3/4 геймпада.
        public Func<bool> PreferDsx = () => true;
        public bool SpeakerTest;               // тест: разрешить писк в динамик (каналы 1–2) при прямом выводе
        public volatile float SpeakerToneAmp;
        double spkPh;

        static string NameOf(IMMDevice d)
        {
            try
            {
                var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                IPropertyStore ps; if (d.OpenPropertyStore(0, out ps) != 0) return "?";
                PropVariant pv;
                if (ps.GetValue(ref key, out pv) != 0) return "?";
                string fn = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : "?";
                PropVariantClear(ref pv);
                return fn;
            }
            catch { return "?"; }
        }

        string RouteKey() { return PreferDsx() ? (DsxProfile.CaptureDeviceId() ?? "direct") : "direct"; }

        void Session()
        {
            string devName;
            string route = RouteKey();
            bool viaDsx = route != "direct";
            IMMDevice dev = null;
            if (viaDsx)
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (en.GetDevice(route, out dev) != 0) dev = null;
                if (dev == null) { Ok = false; State = Loc.T("выход для DSX не найден: ", "output captured by DSX not found: ") + route; return; }
                devName = NameOf(dev);
            }
            else dev = FindDualSense(out devName);
            if (dev == null) { Ok = false; State = Loc.T("нет USB-аудио DualSense (нужен провод)", "no DualSense USB audio (a cable is required)"); return; }

            object o; var iid = IID_IAudioClient;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) throw new Exception("Activate");
            var client = (IAudioClient)o;

            IntPtr mix; client.GetMixFormat(out mix);
            int channels = Marshal.ReadInt16(mix, 2);
            int rate = Marshal.ReadInt32(mix, 4);
            int bits = Marshal.ReadInt16(mix, 14);
            int blockAlign = Marshal.ReadInt16(mix, 12);
            int tag = Marshal.ReadInt16(mix, 0) & 0xFFFF;
            bool isFloat = tag == 3;
            if (tag == 0xFFFE)
            {
                var sub = new byte[16]; Marshal.Copy(new IntPtr(mix.ToInt64() + 24), sub, 0, 16);
                isFloat = new Guid(sub) == SUBTYPE_FLOAT;
            }

            IntPtr fmt = mix;
            bool exclusive = false;
            IntPtr ownFmt = IntPtr.Zero;
            int hr;
            int chL = viaDsx ? 0 : 2, chR = viaDsx ? 1 : 3;
            if (viaDsx && channels < 2) { Marshal.FreeCoTaskMem(mix); Marshal.ReleaseComObject(client); throw new Exception(Loc.T("у выхода ", "output ") + devName + Loc.T(" один канал", " is mono")); }
            if (viaDsx || channels >= 4)
            {
                hr = client.Initialize(0, 0, 300000, 0, fmt, IntPtr.Zero); // общий режим, буфер 30 мс
                if (hr != 0) { Marshal.FreeCoTaskMem(mix); Marshal.ReleaseComObject(client); throw new Exception("Initialize 0x" + hr.ToString("X8")); }
            }
            else
            {
                // В Windows устройство настроено как стерео — открываем 4 канала монопольно.
                Marshal.FreeCoTaskMem(mix); mix = IntPtr.Zero;
                ownFmt = Marshal.AllocCoTaskMem(40);
                WriteExtensible(ownFmt, 4, 48000, 16, 0x33, SUBTYPE_PCM);
                channels = 4; rate = 48000; bits = 16; blockAlign = 8; isFloat = false; exclusive = true; fmt = ownFmt;
                long defP, minP; client.GetDevicePeriod(out defP, out minP);
                long per = Math.Max(defP, 100000);
                hr = client.Initialize(1, 0, per, per, fmt, IntPtr.Zero);
                if (hr != 0)
                {
                    Marshal.FreeCoTaskMem(ownFmt);
                    Marshal.ReleaseComObject(client);
                    throw new Exception(Loc.T("в Windows DualSense настроен как стерео. Звук → DualSense → Настроить → Квадрофония (код 0x",
                                              "DualSense is set to stereo in Windows. Sound → DualSense → Configure → Quadraphonic (code 0x") + hr.ToString("X8") + ")");
                }
            }
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);

            uint bufFrames; client.GetBufferSize(out bufFrames);
            object rco; var rid = IID_IAudioRenderClient;
            client.GetService(ref rid, out rco);
            var render = (IAudioRenderClient)rco;

            Synth.SampleRate = rate;
            var l = new float[bufFrames]; var r = new float[bufFrames];
            var fbuf = new float[bufFrames * channels];
            var sbuf = new short[bufFrames * channels];
            var ibuf = new int[bufFrames * channels];

            client.Start();
            Ok = true;
            State = viaDsx ? Loc.T("через DSX → ", "via DSX → ") + devName
                           : devName + Loc.T(" · напрямую · ", " · direct · ") + channels + Loc.T(" кан", " ch") + (exclusive ? Loc.T(" · монопольно", " · exclusive") : "");
            int routeCheck = 0;
            try
            {
                while (running)
                {
                    if (++routeCheck >= 250) { routeCheck = 0; if (RouteKey() != route) break; } // маршрут сменился — переподключаемся
                    uint pad;
                    hr = client.GetCurrentPadding(out pad);
                    if (hr != 0) throw new Exception(Loc.T("устройство отключено", "device disconnected"));
                    int avail = (int)(bufFrames - pad);
                    if (avail >= 64)
                    {
                        IntPtr data;
                        hr = render.GetBuffer((uint)avail, out data);
                        if (hr != 0) throw new Exception("GetBuffer 0x" + hr.ToString("X8"));
                        Synth.Render(l, r, avail);
                        int total = avail * channels;
                        if (isFloat && bits == 32)
                        {
                            Array.Clear(fbuf, 0, total);
                            for (int i = 0; i < avail; i++) { fbuf[i * channels + chL] = l[i]; fbuf[i * channels + chR] = r[i]; }
                            float sa = SpeakerToneAmp;
                            if (SpeakerTest && !viaDsx && sa > 0)
                                for (int i = 0; i < avail; i++)
                                {
                                    spkPh += 600.0 / rate; if (spkPh >= 1) spkPh -= 1;
                                    float v = (float)Math.Sin(spkPh * Math.PI * 2) * sa;
                                    fbuf[i * channels] = v; fbuf[i * channels + 1] = v;
                                }
                            Marshal.Copy(fbuf, 0, data, total);
                        }
                        else if (bits == 16)
                        {
                            Array.Clear(sbuf, 0, total);
                            for (int i = 0; i < avail; i++) { sbuf[i * channels + chL] = (short)(l[i] * 32767); sbuf[i * channels + chR] = (short)(r[i] * 32767); }
                            Marshal.Copy(sbuf, 0, data, total);
                        }
                        else if (blockAlign == channels * 4)
                        {
                            Array.Clear(ibuf, 0, total);
                            for (int i = 0; i < avail; i++) { ibuf[i * channels + chL] = (int)(l[i] * 2147483000f); ibuf[i * channels + chR] = (int)(r[i] * 2147483000f); }
                            Marshal.Copy(ibuf, 0, data, total);
                        }
                        else
                        {
                            render.ReleaseBuffer((uint)avail, 2 /*silent*/);
                            throw new Exception(Loc.T("неподдерживаемый формат ", "unsupported format ") + bits + Loc.T(" бит", " bit"));
                        }
                        render.ReleaseBuffer((uint)avail, 0);
                    }
                    Thread.Sleep(exclusive ? 2 : 4);
                }
            }
            finally
            {
                Ok = false;
                try { client.Stop(); } catch { }
                if (ownFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(ownFmt);
                Marshal.ReleaseComObject(render);
                Marshal.ReleaseComObject(client);
            }
        }

        static void WriteExtensible(IntPtr p, int ch, int rate, int bits, int mask, Guid sub)
        {
            int block = ch * bits / 8;
            Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));
            Marshal.WriteInt16(p, 2, (short)ch);
            Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * block);
            Marshal.WriteInt16(p, 12, (short)block);
            Marshal.WriteInt16(p, 14, (short)bits);
            Marshal.WriteInt16(p, 16, 22);
            Marshal.WriteInt16(p, 18, (short)bits);
            Marshal.WriteInt32(p, 20, mask);
            Marshal.Copy(sub.ToByteArray(), 0, new IntPtr(p.ToInt64() + 24), 16);
        }
    }

    // ============================================================ ПРОВЕРКА ПРОФИЛЯ DSX
    // Когда игра шлёт обычную вибрацию, DSX ставит в отчёте геймпаду флаги 0x03 ("вибрация вместо хаптики")
    // и геймпад игнорирует звуковые каналы моторов. FH6 вибрирует почти постоянно → наша хаптика глохнет.
    // Поэтому проверяем профиль DSX: если "rumble_isenabled": true — предупреждаем.
    // DSX 3.2 держит аудио-хаптику геймпада в энергосбережении (байт power_save_control = 0x0F),
    // пока сам не стримит хаптику. Рабочая схема: DSX в режиме "Audio to Haptics" с источником
    // SystemAudio перехватывает (loopback) отдельный выход Windows, а мы пишем хаптику в этот выход.
    public static class DsxProfile
    {
        static string dir;
        static long lastRead = -100000;
        static string cachedCapture, cachedWarn;
        static readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        static readonly System.Text.RegularExpressions.Regex RumbleOnV1 =
            new System.Text.RegularExpressions.Regex("\"rumble_isenabled\"\\s*:\\s*true");
        static readonly System.Text.RegularExpressions.Regex RumbleOnV2 =
            new System.Text.RegularExpressions.Regex("\"rumble\"\\s*:\\s*\\{\\s*\"enabled\"\\s*:\\s*true");
        static readonly System.Text.RegularExpressions.Regex AudioBlock =
            new System.Text.RegularExpressions.Regex("\"audio\"\\s*:\\s*\\{[^}]*?\"source\"\\s*:\\s*\"(?<src>\\w+)\"[^}]*?\"capture_device_id\"\\s*:\\s*\"(?<id>[^\"]*)\"");

        // ID выхода Windows, который DSX перехватывает для хаптики (null — DSX не настроен на SystemAudio).
        public static string CaptureDeviceId() { Refresh(); return cachedCapture; }

        public static string RumbleWarning() { Refresh(); return cachedWarn; }

        // Called from both the UI thread and the haptics thread.
        static readonly object refreshLock = new object();

        // Forget the cache (e.g. after the UI language changed) so the next call re-reads the profile.
        public static void Invalidate() { lock (refreshLock) lastRead = -100000; }

        static void Refresh()
        {
            lock (refreshLock) RefreshLocked();
        }

        static void RefreshLocked()
        {
            if (sw.ElapsedMilliseconds - lastRead < 3000) return;
            lastRead = sw.ElapsedMilliseconds;
            string cap = null, warn = null;
            bool rumbleWarn = false;
            try
            {
                if (dir == null || !System.IO.Directory.Exists(dir)) dir = FindDir();
                if (dir != null)
                {
                    foreach (var f in System.IO.Directory.GetFiles(dir, "*.dsx"))
                    {
                        if (f.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        string txt;
                        using (var fs = new System.IO.FileStream(f, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                        using (var sr = new System.IO.StreamReader(fs)) txt = sr.ReadToEnd();
                        string prof = System.IO.Path.GetFileNameWithoutExtension(f);
                        int v2 = txt.IndexOf("\"controller_haptics\": {", StringComparison.Ordinal);
                        var m = v2 >= 0 ? AudioBlock.Match(txt, v2) : System.Text.RegularExpressions.Match.Empty;
                        if (m.Success && m.Groups["src"].Value == "SystemAudio" && m.Groups["id"].Value.Length > 0) cap = m.Groups["id"].Value;
                        else if (warn == null) warn = Loc.T("DSX («" + prof + "»): включи Haptics → Audio → System Audio, иначе DSX держит моторы выключенными",
                                                            "DSX (\"" + prof + "\"): set Haptics → Audio → System Audio, otherwise DSX keeps the motors off");
                        if (warn == null && (RumbleOnV1.IsMatch(txt) || (v2 >= 0 && RumbleOnV2.IsMatch(txt, v2))))
                        {
                            warn = Loc.T("в DSX включена вибрация игры («" + prof + "») — она глушит хаптику. DSX → Haptics & Rumble → выключи Rumble",
                                         "game rumble is enabled in DSX (\"" + prof + "\") — it mutes the haptics. DSX → Haptics & Rumble → turn Rumble off");
                            rumbleWarn = true;
                        }
                    }
                }
            }
            catch { }
            cachedCapture = cap; cachedWarn = cap != null ? (rumbleWarn ? warn : null) : warn;
        }

        static string FindDir()
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("DSX"))
            {
                try
                {
                    var d = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(p.MainModule.FileName), "DSX_Savefile", "Configuration Files", "Controller Profiles");
                    if (System.IO.Directory.Exists(d)) return d;
                }
                catch { }
            }
            return null;
        }
    }

}
