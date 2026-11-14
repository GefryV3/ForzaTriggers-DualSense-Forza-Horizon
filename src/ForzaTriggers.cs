// ForzaTriggers 2 — adaptive triggers + haptics for the PS5 DualSense in Forza Horizon 6 / 5 / 4 and Forza Motorsport.
// Reads Forza "Data Out" telemetry (UDP), simulates pedal physics and drives the triggers either through DSX
// (UDP JSON) or directly over USB (own virtual Xbox pad via ViGEmBus); in parallel it synthesises tactile
// signals for the controller's voice-coil motors (USB audio, channels 3/4).
// Builds with the csc.exe that ships with .NET Framework 4 (C# 5), no external dependencies.
// ---
// ForzaTriggers 2 — адаптивные курки + хаптика DualSense для Forza Horizon 6 через DSX или напрямую по USB.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ForzaTriggers
{
    // ============================================================ ТЕЛЕМЕТРИЯ
    public class Telemetry
    {
        public bool RaceOn;
        public float MaxRpm, IdleRpm, Rpm;
        public float AccX, AccY, AccZ;          // локальные оси машины, м/с² (X — вправо, Z — вперёд)
        public float VelX, VelY, VelZ;
        public float[] SuspNorm = new float[4];  // 0 = полностью разжата (колесо в воздухе), 1 = сжата
        public float[] SlipRatio = new float[4]; // 0 = сцепление, |x|>1 = срыв
        public float[] WheelSpeed = new float[4];// рад/с
        public bool[] RumbleStrip = new bool[4];
        public float[] Puddle = new float[4];
        public float[] SurfaceRumble = new float[4];
        public float[] CombinedSlip = new float[4];
        public float[] SuspMeters = new float[4];
        public int Drivetrain;                   // 0 FWD, 1 RWD, 2 AWD
        public int Cylinders;
        public float Boost;                      // наддув, psi (у атмосферников ≤ 0)
        public bool HasDash;
        public float Speed;                      // м/с
        public float Throttle, Brake, Clutch, Handbrake; // 0..1
        public int Gear;
        public int Length;
        public long ReceivedMs;
    }

    public static class ForzaParser
    {
        // NaN/Inf from a malformed packet would propagate into the physics and the haptic filters — treat as 0.
        static float F(byte[] d, int o) { float v = BitConverter.ToSingle(d, o); return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v; }
        static int I(byte[] d, int o) { return BitConverter.ToInt32(d, o); }

        // Порядок колёс: FL, FR, RL, RR.
        public static Telemetry Parse(byte[] d, int len)
        {
            if (d == null || len < 232) return null;
            var t = new Telemetry();
            t.Length = len;
            t.RaceOn = I(d, 0) != 0;
            t.MaxRpm = F(d, 8); t.IdleRpm = F(d, 12); t.Rpm = F(d, 16);
            t.AccX = F(d, 20); t.AccY = F(d, 24); t.AccZ = F(d, 28);
            t.VelX = F(d, 32); t.VelY = F(d, 36); t.VelZ = F(d, 40);
            for (int i = 0; i < 4; i++)
            {
                t.SuspNorm[i] = F(d, 68 + 4 * i);
                t.SlipRatio[i] = F(d, 84 + 4 * i);
                t.WheelSpeed[i] = F(d, 100 + 4 * i);
                t.RumbleStrip[i] = I(d, 116 + 4 * i) != 0;
                t.Puddle[i] = F(d, 132 + 4 * i);
                t.SurfaceRumble[i] = F(d, 148 + 4 * i);
                t.CombinedSlip[i] = F(d, 180 + 4 * i);
                t.SuspMeters[i] = F(d, 196 + 4 * i);
            }
            t.Drivetrain = I(d, 224);
            t.Cylinders = I(d, 228);

            // Где начинается "Dash"-часть:
            //  FM7 Dash (311) и FM2023 (331) — сразу после Sled (232).
            //  FH4 / FH5 / FH6 (324) — после 12 доп. байт (в FH6: CarGroup, SmashableVelDiff, SmashableMass).
            int dash = -1;
            if (len == 311 || len == 331) dash = 232;
            else if (len >= 323) dash = 244;

            float velMag = (float)Math.Sqrt(t.VelX * t.VelX + t.VelY * t.VelY + t.VelZ * t.VelZ);
            if (dash >= 0 && len >= dash + 79)
            {
                t.HasDash = true;
                t.Speed = F(d, dash + 12);
                t.Boost = F(d, dash + 40);
                t.Throttle = d[dash + 71] / 255f;
                t.Brake = d[dash + 72] / 255f;
                t.Clutch = d[dash + 73] / 255f;
                t.Handbrake = d[dash + 74] / 255f;
                t.Gear = d[dash + 75];
                if (float.IsNaN(t.Speed) || t.Speed < 0 || t.Speed > 250) t.Speed = velMag;
            }
            else
            {
                t.Speed = velMag;
            }
            return t;
        }
    }

    // ============================================================ НАСТРОЙКИ
    public class Settings
    {
        public int ForzaPort = 5300;
        public int DsxPort = 6969;
        public int ControllerIndex = 0;
        public bool Enabled = true;
        public bool AdvancedModes = true;   // режимы DSX v3 (многозонная педаль, насос ABS, вибрация в Гц)
        public int MasterStrength = 100;    // %
        public int Smoothing = 35;          // 0..100
        public bool LightBar = false;       // световая полоса = шифт-лайт

        // Тормоз L2
        public int BrakeFreeTravel = 1;     // зон люфта 0..4 (каждая ~10% хода)
        public int BrakeMinForce = 2;       // 1..8, жёсткость в точке схватывания
        public int BrakeMaxForce = 8;       // 1..8, жёсткость в конце хода
        public int BrakeProgression = 170;  // ×0.01 — экспонента кривой (1.0 линейно, 2+ "стекленеет" в конце)
        public int BrakeDecelWeight = 35;   // % — педаль наливается от реального замедления
        public bool BrakeLimitFeel = true;  // "стена" перед блокировкой
        public bool AbsEnabled = true;
        public int AbsStyle = 1;            // 0 пульс, 1 насос, 2 стук
        public int AbsThreshold = 55;       // порог скольжения ×100
        public int AbsStrength = 80;        // %
        public int AbsFrequency = 16;       // Гц
        public bool LockupEnabled = true;

        // Газ R2
        public int ThrottleMinForce = 1;
        public int ThrottleMaxForce = 3;
        public int ThrottleLoadWeight = 30; // % — тяжелеет при разгоне
        public bool ThrottleKickdown = false;
        public bool WheelspinEnabled = true;
        public int WheelspinThreshold = 60; // ×100
        public int WheelspinStrength = 80;  // %
        public bool LimiterEnabled = true;
        public bool ShiftKickEnabled = true;
        public int ShiftKickStrength = 70;  // %
        public bool CurbEnabled = true;
        public int CurbStrength = 40;       // %

        // Хаптика (вибромоторы)
        public bool HapticEnabled = true;
        public bool HapticViaDsx = true;    // через движок хаптики DSX (иначе DSX держит моторы выключенными)
        public bool DirectMode = false;     // без DSX: свой виртуальный Xbox (ViGEmBus), курки и хаптика по USB напрямую
        public int HapMaster = 80;
        public int HapEngine = 35;
        public int HapRoad = 60;
        public int HapCurb = 80;
        public int HapImpact = 85;
        public int HapSlip = 50;
        public int HapShift = 70;
        public int HapAbs = 50;
        public int HapTurbo = 0;            // турбина — убрана (не ощущалась); поле оставлено для старых ini
        public int HapDive = 80;            // "нырок" при резком торможении и рывок на старте
        public int HapWeight = 60;          // перенос веса в поворотах (панорама мотора)
        public bool ShiftLeds = true;       // шифт-лайт на 5 индикаторах игрока (прямой режим)
        public int Language = -1;           // -1 — как в Windows, 0 — русский, 1 — English

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ForzaTriggers.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var raw in File.ReadAllLines(FilePath))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var f = typeof(Settings).GetField(line.Substring(0, eq).Trim());
                        if (f == null) continue;
                        var v = line.Substring(eq + 1).Trim();
                        if (f.FieldType == typeof(int)) { int iv; if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv)) f.SetValue(s, iv); }
                        else if (f.FieldType == typeof(bool)) { bool bv; if (bool.TryParse(v, out bv)) f.SetValue(s, bv); }
                    }
                }
                else
                {
                    var portFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSX", "DSX_UDP_PortNumber.txt");
                    if (File.Exists(portFile))
                    {
                        int p;
                        if (int.TryParse(File.ReadAllText(portFile).Trim(), out p) && p > 0 && p < 65536) s.DsxPort = p;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# ForzaTriggers settings");
                foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    sb.AppendLine(f.Name + "=" + Convert.ToString(f.GetValue(this), CultureInfo.InvariantCulture));
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public void CopyFrom(Settings o)
        {
            foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                f.SetValue(this, f.GetValue(o));
        }
    }

    public static class Presets
    {
        public static string[] Names
        {
            get
            {
                return new[] { Loc.T("Реализм", "Realistic"), Loc.T("Мягкий", "Soft"),
                               Loc.T("Хардкор", "Hardcore"), Loc.T("Дрифт", "Drift") };
            }
        }

        public static void Apply(Settings s, int idx)
        {
            var d = new Settings();
            d.ForzaPort = s.ForzaPort; d.DsxPort = s.DsxPort; d.ControllerIndex = s.ControllerIndex;
            d.AdvancedModes = s.AdvancedModes; d.LightBar = s.LightBar; d.Enabled = s.Enabled; d.HapticEnabled = s.HapticEnabled;
            d.DirectMode = s.DirectMode; d.HapticViaDsx = s.HapticViaDsx; d.ShiftLeds = s.ShiftLeds; // режим работы пресеты не трогают
            d.Language = s.Language;
            switch (idx)
            {
                case 1: // Мягкий
                    d.MasterStrength = 80; d.BrakeMinForce = 1; d.BrakeMaxForce = 5; d.BrakeProgression = 130; d.BrakeDecelWeight = 25;
                    d.AbsStrength = 50; d.AbsStyle = 0; d.WheelspinStrength = 50; d.ThrottleMaxForce = 2; d.ShiftKickStrength = 40;
                    d.CurbStrength = 25; d.Smoothing = 55; d.HapMaster = 55; d.HapEngine = 25; d.HapImpact = 60;
                    break;
                case 2: // Хардкор
                    d.BrakeMinForce = 3; d.BrakeMaxForce = 8; d.BrakeProgression = 230; d.BrakeDecelWeight = 50; d.AbsStrength = 100;
                    d.AbsThreshold = 45; d.WheelspinThreshold = 45; d.WheelspinStrength = 100;
                    d.ThrottleMinForce = 2; d.ThrottleMaxForce = 4; d.ShiftKickStrength = 90; d.CurbStrength = 60;
                    d.Smoothing = 20; d.HapMaster = 100; d.HapEngine = 45; d.HapRoad = 75; d.HapImpact = 100; d.HapSlip = 65;
                    break;
                case 3: // Дрифт
                    d.WheelspinThreshold = 130; d.WheelspinStrength = 55; d.ThrottleMinForce = 1; d.ThrottleMaxForce = 2;
                    d.BrakeMaxForce = 6; d.ShiftKickStrength = 60; d.HapSlip = 70; d.HapEngine = 45;
                    break;
            }
            s.CopyFrom(d);
        }
    }

    // ============================================================ ЭФФЕКТЫ КУРКОВ
    public enum EffKind { Off, Curve, Vibe, Machine, Gallop }

    public struct Eff
    {
        public EffKind Kind;
        public int[] Zones;          // Curve: сила 0..8 на каждую из 10 зон хода
        public int LegacyStart, LegacyForce;
        public int Pos, End, Amp, AmpB, Freq, Period; // вибро-эффекты
        public string Label;

        public static Eff Off(string label) { var e = new Eff(); e.Kind = EffKind.Off; e.Label = label; return e; }

        public static Eff Curve(int[] zones, int legacyStart, int legacyForce, string label)
        {
            var e = new Eff(); e.Kind = EffKind.Curve; e.Zones = zones;
            e.LegacyStart = Util.Clamp(legacyStart, 0, 9); e.LegacyForce = Util.Clamp(legacyForce, 0, 8); e.Label = label; return e;
        }

        public static Eff Vibe(int pos, int amp, int freq, string label)
        {
            var e = new Eff(); e.Kind = EffKind.Vibe;
            e.Pos = Util.Clamp(pos, 0, 9); e.End = 9; e.Amp = Util.Clamp(amp, 1, 8); e.Freq = Util.Clamp(freq, 1, 255);
            e.Label = label; return e;
        }

        // Machine: вибрация, переключающаяся между силами A и B каждые period×0.1 с.
        public static Eff Machine(int start, int ampA, int ampB, int freq, int period, string label)
        {
            var e = new Eff(); e.Kind = EffKind.Machine;
            e.Pos = Util.Clamp(start, 0, 8); e.End = 9; e.Amp = Util.Clamp(ampA, 0, 7); e.AmpB = Util.Clamp(ampB, 0, 7);
            e.Freq = Util.Clamp(freq, 1, 255); e.Period = Util.Clamp(period, 1, 255); e.Label = label; return e;
        }

        // Galloping: ритмичный двойной стук ("да-дум") с частотой freq.
        public static Eff Gallop(int start, int freq, string label)
        {
            var e = new Eff(); e.Kind = EffKind.Gallop;
            e.Pos = Util.Clamp(start, 0, 8); e.End = 9; e.Amp = 2; e.AmpB = 5; e.Freq = Util.Clamp(freq, 1, 255); e.Label = label; return e;
        }
    }

    public static class Util
    {
        public static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        public static int RoundI(float v) { return (int)Math.Round(v, MidpointRounding.AwayFromZero); }
    }

    // Сглаживание + гистерезис, чтобы целочисленная сила не "дрожала" на границе.
    class Smoother
    {
        float f = -1; int cur;
        public int Step(float target, float a)
        {
            if (f < 0) f = target; else f += (target - f) * a;
            if (Math.Abs(f - cur) > 0.6f) cur = Util.RoundI(f);
            return cur;
        }
        public void Reset() { f = -1; cur = 0; }
    }

    public class Status
    {
        public string RxError;
        public int Pps, Len;
        public bool Fresh, RaceOn, Test;
        public float Kmh, Rpm, MaxRpm, Thr, Brk, BrakeSlip, DrivenSlip;
        public int Gear;
        public Eff L, R;
        public int SendPs, DsxPort;
        public string Direct;               // статус прямого режима (null — режим DSX)
        public bool DirectReady;
        public string DirectButtons;
        public string EngineName;
        public bool DsxReplies;
        public int Battery = -1;
    }

    // Что произошло с машиной в этом кадре — нужно и куркам, и хаптике.
    class Frame
    {
        public bool Abs, Lockup, Spin, Limiter, Shift, ShiftUp, Airborne, Dive, Launch;
        public float AbsLevel, SpinLevel;
    }

    // ============================================================ ДВИЖОК
    public class Engine
    {
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint p);

        public readonly Settings S;
        public readonly HapticSynth Synth; // может быть null
        public volatile Status Stat = new Status();
        public volatile int TestMode; // 0 нет, 1 L2, 2 R2, 3 хаптика
        public DirectController Direct;     // не null — прямой режим: курки пишем в геймпад сами, DSX не используется

        volatile Telemetry latest;
        UdpClient rx, tx;
        IPEndPoint dsxEp;
        Thread rxThread, loopThread, dsxRxThread;
        volatile bool running;
        string rxError;
        int pktCount, sendCount;
        long testStartMs, dsxReplyMs = -100000;
        volatile int battery = -1;
        readonly Stopwatch clock = Stopwatch.StartNew();

        // состояние физики курков
        readonly Smoother[] bz = MakeSm(10), tz = MakeSm(10);
        float absLevel, spinLevel;
        long absHold, lockHold, spinHold, kickUntil;
        int lastGear = -1;
        string lastJson = "";
        long lastSendMs;
        bool lightWasOn;

        // состояние хаптики
        Telemetry hapPrev;
        readonly long[] suspCool = new long[4];
        long crashCool, engCutUntil, airSince = -1;
        bool kickUp;
        float prevThrottle;
        readonly Random rnd = new Random();
        // нырок и старт
        long brakeLowMs, throttleLowMs, diveCool, launchCool, launchUntil;

        public Engine(Settings s, HapticSynth synth) { S = s; Synth = synth; }
        public long NowMs { get { return clock.ElapsedMilliseconds; } }

        static Smoother[] MakeSm(int n) { var a = new Smoother[n]; for (int i = 0; i < n; i++) a[i] = new Smoother(); return a; }

        public void Start()
        {
            running = true;
            rxError = null;
            try
            {
                rx = new UdpClient();
                rx.Client.Bind(new IPEndPoint(IPAddress.Any, S.ForzaPort));
                rx.Client.ReceiveTimeout = 300;
                NoConnReset(rx);
            }
            catch (Exception ex)
            {
                rxError = Loc.T("Порт " + S.ForzaPort + " занят (закрой SimHub/ForzaDSX/другой экземпляр): ",
                                "Port " + S.ForzaPort + " is busy (close SimHub/ForzaDSX/another instance): ") + ex.Message;
                rx = null;
            }
            tx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            tx.Client.ReceiveTimeout = 300;
            NoConnReset(tx);
            dsxEp = new IPEndPoint(IPAddress.Loopback, S.DsxPort);
            lastJson = "";

            rxThread = new Thread(RxLoop) { IsBackground = true, Name = "forza-rx" };
            loopThread = new Thread(MainLoop) { IsBackground = true, Name = "engine", Priority = ThreadPriority.AboveNormal };
            dsxRxThread = new Thread(DsxRxLoop) { IsBackground = true, Name = "dsx-rx" };
            rxThread.Start(); loopThread.Start(); dsxRxThread.Start();
        }

        public void Stop(bool resetController)
        {
            running = false;
            if (loopThread != null) loopThread.Join(500);
            if (Synth != null) Synth.P = new HapticParams();
            if (resetController && tx != null)
                Send("{\"instructions\":[{\"type\":7,\"parameters\":[" + S.ControllerIndex + "]}]}");
            try { if (rx != null) rx.Close(); } catch { }
            try { if (tx != null) tx.Close(); } catch { }
            if (rxThread != null) rxThread.Join(500);
            if (dsxRxThread != null) dsxRxThread.Join(500);
            rx = null; tx = null;
        }

        static void NoConnReset(UdpClient c)
        {
            try { c.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch { }
        }

        public void StartTest(int which) { testStartMs = NowMs; TestMode = which; }

        // ---------- запись телеметрии для настройки по реальным данным
        // recUntil: 0 — не пишем; 1 — нажата «стоп», ждём сохранения; иначе — время авто-остановки.
        // Список rec трогает только поток приёма; UI лишь меняет recUntil.
        long recUntil;
        readonly List<KeyValuePair<long, byte[]>> rec = new List<KeyValuePair<long, byte[]>>();
        public volatile string RecStatus;
        public volatile bool RecFailed;
        long recStart;
        public bool Recording { get { return Interlocked.Read(ref recUntil) > 0; } }
        public long RecElapsedMs { get { return Recording ? NowMs - recStart : 0; } }

        // ▶ — начать запись (страховочный предел 10 минут)
        public void StartRecording()
        {
            if (Recording) return;
            if (rx == null)
            {
                RecFailed = true;
                RecStatus = Loc.T("Нечего записывать: порт Forza не открыт", "Nothing to record: the Forza port is not open");
                return;
            }
            recStart = NowMs;
            RecStatus = null; RecFailed = false;
            Interlocked.Exchange(ref recUntil, NowMs + 10 * 60 * 1000L);
        }

        // ⏸ — остановить и сохранить (сохранение делает поток приёма не позже чем через 300 мс,
        // даже если игра перестала слать пакеты — раньше в этом случае запись висела вечно).
        public void StopRecording() { if (Recording) Interlocked.Exchange(ref recUntil, 1); }

        void SaveRecording()
        {
            try
            {
                if (rec.Count == 0)
                {
                    RecFailed = true;
                    RecStatus = Loc.T("Пусто: от игры не пришло ни одного пакета", "Empty: no packets arrived from the game");
                }
                else
                {
                    string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "telemetry-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin");
                    using (var w = new System.IO.BinaryWriter(System.IO.File.Create(path)))
                        foreach (var kv in rec) { w.Write(kv.Key); w.Write(kv.Value.Length); w.Write(kv.Value); }
                    RecFailed = false;
                    RecStatus = Loc.T("Сохранено: ", "Saved: ") + rec.Count + Loc.T(" пакетов → ", " packets → ") + System.IO.Path.GetFileName(path);
                }
            }
            catch (Exception ex) { RecFailed = true; RecStatus = Loc.T("Ошибка записи: ", "Recording error: ") + ex.Message; }
            rec.Clear();
            Interlocked.Exchange(ref recUntil, 0);
        }

        void RecordTick(byte[] buf, int n)
        {
            long until = Interlocked.Read(ref recUntil);
            if (until == 0) { if (rec.Count > 0) rec.Clear(); return; }
            long now = NowMs;
            if (until > 1 && now < until)
            {
                if (buf != null) { var copy = new byte[n]; Array.Copy(buf, copy, n); rec.Add(new KeyValuePair<long, byte[]>(now, copy)); }
            }
            else SaveRecording();
        }

        void RxLoop()
        {
            var buf = new byte[2048];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (running && rx != null)
            {
                try
                {
                    int n = rx.Client.ReceiveFrom(buf, ref any);
                    var t = ForzaParser.Parse(buf, n);
                    if (t != null) { t.ReceivedMs = NowMs; latest = t; Interlocked.Increment(ref pktCount); }
                    RecordTick(buf, n);
                }
                catch (SocketException) { RecordTick(null, 0); } // таймаут 300 мс — даём записи остановиться без пакетов
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        void DsxRxLoop()
        {
            var buf = new byte[8192];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (running)
            {
                try
                {
                    var c = tx;
                    if (c == null) break;
                    int n = c.Client.ReceiveFrom(buf, ref any);
                    if (n > 0)
                    {
                        dsxReplyMs = NowMs;
                        var s = Encoding.UTF8.GetString(buf, 0, n);
                        int i = s.IndexOf("\"BatteryLevel\"", StringComparison.OrdinalIgnoreCase);
                        if (i >= 0)
                        {
                            int k = s.IndexOf(':', i) + 1, v = 0; bool got = false;
                            while (k < s.Length && s[k] == ' ') k++;
                            while (k < s.Length && char.IsDigit(s[k])) { v = v * 10 + (s[k] - '0'); k++; got = true; }
                            if (got) battery = v;
                        }
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        void MainLoop()
        {
            timeBeginPeriod(1);
            try
            {
                long prev = NowMs, secStart = NowMs;
                int ppsShow = 0, spsShow = 0;
                while (running)
                {
                    long now = NowMs;
                    float dt = Math.Max(0.001f, Math.Min(0.1f, (now - prev) / 1000f));
                    prev = now;
                    if (now - secStart >= 1000)
                    {
                        ppsShow = Interlocked.Exchange(ref pktCount, 0);
                        spsShow = Interlocked.Exchange(ref sendCount, 0);
                        secStart = now;
                    }
                    try { Tick(now, dt, ppsShow, spsShow); } catch { }
                    Thread.Sleep(6);
                }
            }
            finally { timeEndPeriod(1); }
        }

        float Alpha(float dt)
        {
            float tau = 0.010f + S.Smoothing / 100f * 0.12f;
            return 1f - (float)Math.Exp(-dt / tau);
        }

        void ResetPhysics()
        {
            foreach (var s in bz) s.Reset();
            foreach (var s in tz) s.Reset();
            absLevel = spinLevel = 0; absHold = lockHold = spinHold = kickUntil = 0; lastGear = -1; hapPrev = null;
        }

        void Tick(long now, float dt, int pps, int sps)
        {
            var t = latest;
            bool fresh = t != null && now - t.ReceivedMs < 1000;
            Eff L, R;
            int[] rgb = null;
            int test = TestMode;

            if (test != 0)
            {
                float ph = (now - testStartMs) / 1000f;
                if (ph > 6.5f) { TestMode = 0; test = 0; }
            }

            if (test == 1 || test == 2)
            {
                ComputeTest(test, (now - testStartMs) / 1000f, out L, out R);
                if (Synth != null) Synth.P = new HapticParams();
            }
            else if (test == 3)
            {
                string ht = Loc.T("Тест хаптики", "Haptics test");
                L = Eff.Off(ht); R = Eff.Off(ht);
                HapticTest((now - testStartMs) / 1000f);
            }
            else if (!S.Enabled) { string m = Loc.T("Выключено", "Disabled"); L = Eff.Off(m); R = Eff.Off(m); ResetPhysics(); SilenceHaptics(); }
            else if (!fresh) { string m = Loc.T("Нет данных от игры", "No data from the game"); L = Eff.Off(m); R = Eff.Off(m); ResetPhysics(); SilenceHaptics(); }
            else if (!t.RaceOn) { string m = Loc.T("Меню / пауза", "Menu / paused"); L = Eff.Off(m); R = Eff.Off(m); ResetPhysics(); SilenceHaptics(); }
            else
            {
                var fr = new Frame();
                ComputeRace(t, now, dt, fr, out L, out R);
                UpdateHaptics(t, now, fr);
                if (S.LightBar) rgb = ShiftLight(t, now);
            }

            int idx = S.ControllerIndex;
            if (Direct != null)
            {
                int leds = -1;
                if (S.ShiftLeds)
                {
                    leds = 0x04; // вне гонки — один центральный, как обычный "игрок 1"
                    if (fresh && t.RaceOn && t.MaxRpm > 500 && test == 0)
                    {
                        float rfl = t.Rpm / t.MaxRpm;
                        if (rfl >= 0.97f) leds = (now / 70) % 2 == 0 ? 0x1F : 0x00;   // отсечка — мигают все
                        else
                        {
                            leds = 0;
                            float[] th = { 0.62f, 0.72f, 0.82f, 0.90f, 0.95f };
                            for (int i = 0; i < 5; i++) if (rfl >= th[i]) leds |= 1 << i;
                        }
                    }
                }
                Direct.SetOutput(L, R, S.LightBar ? rgb : null, leds);
                PublishStatus(now, t, fresh, test, L, R, pps, sps);
                return;
            }
            var sb = new StringBuilder(220);
            sb.Append("{\"instructions\":[");
            sb.Append(EncodeTrigger(idx, 1, L)).Append(',').Append(EncodeTrigger(idx, 2, R));
            if (rgb != null) sb.Append(",{\"type\":2,\"parameters\":[").Append(idx).Append(',').Append(rgb[0]).Append(',').Append(rgb[1]).Append(',').Append(rgb[2]).Append("]}");
            sb.Append("]}");
            string json = sb.ToString();

            if (lightWasOn && !S.LightBar)
            {
                Send("{\"instructions\":[{\"type\":7,\"parameters\":[" + idx + "]}]}");
                lastJson = "";
            }
            lightWasOn = S.LightBar;

            if ((json != lastJson && now - lastSendMs >= 10) || now - lastSendMs > 500)
            {
                if (Send(json)) Interlocked.Increment(ref sendCount);
                lastJson = json; lastSendMs = now;
            }
            PublishStatus(now, t, fresh, test, L, R, pps, sps);
        }

        void PublishStatus(long now, Telemetry t, bool fresh, int test, Eff L, Eff R, int pps, int sps)
        {
            var st = new Status();
            if (Direct != null) { st.Direct = Direct.State; st.DirectReady = Direct.Ready; st.DirectButtons = Direct.LastButtons; }
            st.RxError = rxError; st.Pps = pps; st.SendPs = sps; st.Fresh = fresh; st.Test = test != 0;
            st.DsxPort = S.DsxPort; st.DsxReplies = now - dsxReplyMs < 3000; st.Battery = battery;
            st.L = L; st.R = R;
            if (t != null)
            {
                st.Len = t.Length; st.RaceOn = t.RaceOn; st.Kmh = t.Speed * 3.6f; st.Rpm = t.Rpm; st.MaxRpm = t.MaxRpm;
                st.Gear = t.Gear; st.Thr = t.Throttle; st.Brk = t.Brake;
                st.EngineName = EngineCharacters.For(t.Cylinders, t.MaxRpm).Name;
                st.BrakeSlip = BrakeSlip(t); st.DrivenSlip = DrivenSlip(t);
            }
            Stat = st;
        }

        bool Send(string json)
        {
            try
            {
                var c = tx;
                if (c == null) return false;
                var b = Encoding.UTF8.GetBytes(json);
                c.Send(b, b.Length, dsxEp);
                return true;
            }
            catch { return false; }
        }

        // Режимы UDP в DSX 3.2 (enum TriggerType_v2_Legacy, прочитан из DSX.dll); параметры после режима
        // DSX кладёт по порядку в param0..param10 и передаёт в TriggerEffectGenerator:
        //   0  NORMAL
        //   12 CUSTOM_TRIGGER_VALUE  [mode, ...]          (VIB_RESISTANCE=9: freq, amp, start)
        //   13 RESISTANCE            [start, force]
        //   15 GALLOPING             [start, end, firstFoot 0-6, secondFoot ff+1..7, freq]
        //   18 MACHINE               [start, end, ampA 0-7, ampB 0-7, freq, period(×0.1 c)]
        //   21 FEEDBACK              [position, strength]
        //   23 VIBRATION             [position, amplitude, frequency]
        //   25 MULTIPLE_POSITION_FEEDBACK [10 сил по зонам]
        //   (26 MULTIPLE_POSITION_VIBRATION в DSX 3.2 берёт частоту из профиля, а не из пакета — не используем)
        string EncodeTrigger(int idx, int trig, Eff e)
        {
            string p;
            bool adv = S.AdvancedModes;
            string pre = idx + "," + trig + ",";
            switch (e.Kind)
            {
                case EffKind.Curve:
                    if (adv)
                    {
                        var sb = new StringBuilder(pre).Append("25");
                        bool any = false;
                        for (int i = 0; i < 10; i++) { int z = Util.Clamp(e.Zones[i], 0, 8); any |= z > 0; sb.Append(',').Append(z); }
                        p = any ? sb.ToString() : pre + "0,0,0";
                    }
                    else p = pre + "13," + e.LegacyStart + "," + e.LegacyForce;
                    break;
                case EffKind.Vibe:
                    p = adv ? pre + "23," + e.Pos + "," + e.Amp + "," + e.Freq : LegacyVibe(pre, e.Freq, e.Amp, e.Pos);
                    break;
                case EffKind.Machine:
                    p = adv ? pre + "18," + e.Pos + "," + e.End + "," + e.Amp + "," + e.AmpB + "," + e.Freq + "," + e.Period
                            : LegacyVibe(pre, e.Freq, Math.Max(e.Amp, e.AmpB) + 1, e.Pos);
                    break;
                case EffKind.Gallop:
                    p = adv ? pre + "15," + e.Pos + "," + e.End + "," + e.Amp + "," + e.AmpB + "," + e.Freq
                            : LegacyVibe(pre, Math.Max(4, e.Freq * 2), 6, e.Pos);
                    break;
                default:
                    p = pre + "0,0,0";
                    break;
            }
            return "{\"type\":1,\"parameters\":[" + p + "]}";
        }

        static string LegacyVibe(string pre, int freq, int amp8, int pos)
        {
            return pre + "12,9," + freq + "," + Util.Clamp(amp8 * 255 / 8, 0, 255) + "," + (pos * 20) + ",0,0,0,0";
        }

        static float BrakeSlip(Telemetry t)
        {
            float fs = Math.Max(Math.Abs(t.SlipRatio[0]), Math.Abs(t.SlipRatio[1]));
            float rs = Math.Max(Math.Abs(t.SlipRatio[2]), Math.Abs(t.SlipRatio[3]));
            return Math.Max(fs, 0.85f * rs);
        }

        static float DrivenSlip(Telemetry t)
        {
            float f = Math.Max(Math.Abs(t.SlipRatio[0]), Math.Abs(t.SlipRatio[1]));
            float r = Math.Max(Math.Abs(t.SlipRatio[2]), Math.Abs(t.SlipRatio[3]));
            if (t.Drivetrain == 0) return f;
            if (t.Drivetrain == 1) return r;
            return Math.Max(f, r);
        }

        // ------------------------------------------------------------ ФИЗИКА КУРКОВ
        void ComputeRace(Telemetry t, long now, float dt, Frame fr, out Eff L, out Eff R)
        {
            float master = S.MasterStrength / 100f;
            float a = Alpha(dt);
            float speed = t.Speed;
            bool airborne = t.SuspNorm[0] < 0.03f && t.SuspNorm[1] < 0.03f && t.SuspNorm[2] < 0.03f && t.SuspNorm[3] < 0.03f;
            fr.Airborne = airborne;
            bool curb = false; // поребрики убраны по просьбе (в FH6 они приходят только как SurfaceRumble 0/0.6 и мешают)
            float curbFreq = Math.Max(12f, Math.Min(60f, speed * 1.1f));
            int curbAmp = Util.RoundI(4f * S.CurbStrength / 100f * master);

            // ===== ТОРМОЗ (L2) =====
            // Кривая настоящей педали по 10 зонам: люфт → точка схватывания → прогрессивное "стекленение"
            // (вакуумный усилитель исчерпан, давление в контуре растёт). Чем сильнее реальное замедление,
            // тем плотнее вся кривая. У предела сцепления сразу за пальцем вырастает "стена".
            float b = t.Brake;
            int ft = Util.Clamp(S.BrakeFreeTravel, 0, 4);
            int bMin = Util.Clamp(S.BrakeMinForce, 1, 8);
            int bMax = Math.Max(bMin, Util.Clamp(S.BrakeMaxForce, 1, 8));
            float dw = S.BrakeDecelWeight / 100f;
            float decelN = Util.Clamp01(Math.Max(0f, -t.AccZ) / 9.81f / 1.2f);
            float bEnd = bMin + (bMax - bMin) * ((1f - dw) + dw * decelN);
            float gamma = Math.Max(0.5f, S.BrakeProgression / 100f);

            float bslip = BrakeSlip(t);
            float absTh = S.AbsThreshold / 100f;
            float limitProx = S.BrakeLimitFeel && b > 0.1f && speed > 3f ? Util.Clamp01((bslip / absTh - 0.6f) / 0.4f) : 0f;
            int pz = Util.Clamp((int)(b * 10f), 0, 9);

            var bZones = new int[10];
            for (int z = 0; z < 10; z++)
            {
                float target = 0f;
                if (z >= ft)
                {
                    float x = (z - ft + 1f) / (10f - ft);
                    target = bMin + (bEnd - bMin) * (float)Math.Pow(x, gamma);
                    if (limitProx > 0 && z > pz && z <= pz + 2) target += limitProx * 4f;
                    target = Math.Max(1f, target * master);
                }
                bZones[z] = Util.Clamp(bz[z].Step(target, a), 0, 8);
                if (z >= ft && bZones[z] < 1) bZones[z] = 1;
            }
            int bLegacy = bZones[Math.Max(ft, pz)];

            bool locked = S.LockupEnabled && b > 0.3f && speed > 4f && !airborne
                          && Math.Abs(t.WheelSpeed[0]) < 0.8f && Math.Abs(t.WheelSpeed[1]) < 0.8f;
            if (locked) lockHold = now + 120;
            if (S.AbsEnabled && b > 0.08f && speed > 2.5f && !airborne && bslip > absTh)
            {
                float target = Util.Clamp01((bslip - absTh) / 1.2f);
                if (now >= absHold) absLevel = target; else absLevel += (target - absLevel) * 0.25f;
                absHold = now + 90;
            }
            fr.Abs = now < absHold; fr.AbsLevel = absLevel; fr.Lockup = now < lockHold;

            float absStr = S.AbsStrength / 100f * master;
            int absAmp = Util.RoundI((3f + 5f * absLevel) * absStr);
            int lockAmp = Util.RoundI(6f * absStr);

            if (master <= 0.04f) L = Eff.Off(Loc.T("Сила 0%", "Strength 0%"));
            else if (fr.Lockup && lockAmp >= 1) L = Eff.Machine(ft, Math.Min(7, lockAmp), 0, 10, 1, Loc.T("БЛОКИРОВКА колёс", "WHEEL LOCK-UP"));
            else if (fr.Abs && absAmp >= 1) L = AbsEffect(ft, absAmp);
            else if (curb && b > 0.1f && curbAmp >= 1) L = Eff.Vibe(ft, curbAmp, Util.RoundI(curbFreq / 2) * 2, Loc.T("Поребрик", "Kerb"));
            else L = Eff.Curve(bZones, ft, bLegacy, (limitProx > 0.3f ? Loc.T("У предела · ", "At the limit · ") : Loc.T("Педаль ", "Pedal ")) + bZones[ft] + "→" + bZones[9]);

            // ===== ГАЗ (R2) =====
            // Лёгкая пружина, тяжелеет при разгоне (тебя вжимает в кресло); опционально — щелчок кикдауна у пола.
            float th = t.Throttle;
            int tMin = Util.Clamp(S.ThrottleMinForce, 1, 8);
            int tMax = Math.Max(tMin, Util.Clamp(S.ThrottleMaxForce, 1, 8));
            float lw = S.ThrottleLoadWeight / 100f;
            float loadN = Util.Clamp01(Math.Max(0f, t.AccZ) / 9.81f / 0.8f);
            float tEnd = airborne ? tMin : tMin + (tMax - tMin) * ((1f - lw) + lw * loadN);
            var tZones = new int[10];
            for (int z = 0; z < 10; z++)
            {
                float x = (z + 1f) / 10f;
                float target = tMin + (tEnd - tMin) * x;
                if (S.ThrottleKickdown && z == 8) target = Math.Min(8f, target + 4f);
                tZones[z] = Util.Clamp(tz[z].Step(Math.Max(1f, target * master), a), 1, 8);
            }
            int tpz = Util.Clamp((int)(th * 10f), 0, 9);

            // "Нырок": тормоз с нуля почти в пол за ~0.2 с на скорости.
            if (b < 0.25f) brakeLowMs = now;
            if (b > 0.6f && now - brakeLowMs < 220 && speed > 8f && now >= diveCool) { fr.Dive = true; diveCool = now + 900; }
            // Старт: газ с нуля в пол за ~0.3 с, стоя на месте.
            if (th < 0.3f) throttleLowMs = now;
            if (th > 0.85f && now - throttleLowMs < 300 && speed < 1.5f && now >= launchCool)
            { fr.Launch = true; launchCool = now + 1500; launchUntil = now + 220; }

            int g = t.Gear;
            bool validGear = g >= 0 && g <= 10;
            if (validGear)
            {
                if (lastGear >= 0 && g != lastGear && speed > 1f)
                {
                    fr.Shift = true;
                    fr.ShiftUp = g > lastGear && lastGear != 0;    // 0 = задняя
                    if (S.ShiftKickEnabled) { kickUntil = now + (fr.ShiftUp ? 110 : 160); kickUp = fr.ShiftUp; }
                }
                lastGear = g;
            }

            float dslip = DrivenSlip(t);
            float spinTh = S.WheelspinThreshold / 100f;
            if (S.WheelspinEnabled && th > 0.15f && dslip > spinTh && !airborne && validGear)
            {
                float target = Util.Clamp01((dslip - spinTh) / 2f);
                if (now >= spinHold) spinLevel = target; else spinLevel += (target - spinLevel) * 0.3f;
                spinHold = now + 70;
            }
            fr.Spin = now < spinHold; fr.SpinLevel = spinLevel;
            bool limiter = S.LimiterEnabled && t.MaxRpm > 500 && t.Rpm >= t.MaxRpm * 0.97f && th > 0.5f;
            fr.Limiter = t.MaxRpm > 500 && t.Rpm >= t.MaxRpm * 0.97f && th > 0.5f;

            int kickAmp = Util.RoundI((5f + 3f * S.ShiftKickStrength / 100f) * master);
            int spinAmp = Util.RoundI((3f + 5f * spinLevel) * S.WheelspinStrength / 100f * master);
            int spinFreq = Util.RoundI((26f + 44f * spinLevel + Math.Min(20f, speed * 0.3f)) / 2f) * 2;
            int limAmp = Util.RoundI(4f * master);

            if (master <= 0.04f) R = Eff.Off(Loc.T("Сила 0%", "Strength 0%"));
            // Толчок переключения. Вверх — один жёсткий удар курка (9 Гц ≈ одна "лопата" за 110 мс);
            // вниз — два удара (18 Гц, 160 мс), как перегазовка. Низкие частоты = удар, а не дребезг.
            else if (now < kickUntil && kickAmp >= 1)
                R = kickUp ? Eff.Vibe(0, Math.Min(8, kickAmp), 9, Loc.T("Переключение ↑", "Upshift ↑")) : Eff.Vibe(0, Math.Min(8, kickAmp), 18, Loc.T("Переключение ↓", "Downshift ↓"));
            // Старт: курок дёргает — сцепление схватило.
            else if (now < launchUntil && S.HapDive > 0) R = Eff.Vibe(1, Util.Clamp(Util.RoundI(8f * master), 1, 8), 22, Loc.T("Старт", "Launch"));
            else if (fr.Spin && spinAmp >= 1) R = Eff.Vibe(1, spinAmp, spinFreq, Loc.T("ПРОБУКСОВКА", "WHEELSPIN"));
            else if (limiter && limAmp >= 1) R = Eff.Machine(2, Math.Min(7, limAmp + 1), 0, 45, 1, Loc.T("Отсечка", "Rev limiter"));
            else if (curb && th > 0.1f && curbAmp >= 1) R = Eff.Vibe(1, curbAmp, Util.RoundI(curbFreq / 2) * 2, Loc.T("Поребрик", "Kerb"));
            else R = Eff.Curve(tZones, 0, tZones[tpz], (airborne ? Loc.T("В воздухе ", "Airborne ") : Loc.T("Педаль ", "Pedal ")) + tZones[0] + "→" + tZones[9]);
        }

        static float SlipLevel(float cs) { return cs <= 1.2f ? 0f : Util.Clamp01((float)(Math.Log(cs / 1.2) / Math.Log(30.0 / 1.2))); }

        static bool OnCurb(Telemetry t, int w) { return t.RumbleStrip[w] || Math.Abs(t.SurfaceRumble[w]) > 0.05f; }

        static int[] Fill10(int v) { var z = new int[10]; for (int i = 0; i < 10; i++) z[i] = v; return z; }

        Eff AbsEffect(int start, int amp8)
        {
            string lbl = "ABS " + S.AbsFrequency + Loc.T(" Гц", " Hz");
            switch (S.AbsStyle)
            {
                case 0: return Eff.Vibe(start, amp8, S.AbsFrequency, lbl + Loc.T(" · пульс", " · pulse"));
                case 2: return Eff.Gallop(start, Math.Max(2, S.AbsFrequency / 2), lbl + Loc.T(" · стук", " · knock"));
                default:
                    int a7 = Math.Min(7, amp8);
                    return Eff.Machine(start, a7, Math.Max(0, a7 - 4), S.AbsFrequency, 1, lbl + Loc.T(" · насос", " · pump"));
            }
        }

        // ------------------------------------------------------------ ХАПТИКА
        void SilenceHaptics() { if (Synth != null) Synth.P = new HapticParams(); }

        void UpdateHaptics(Telemetry t, long now, Frame fr)
        {
            if (Synth == null) return;
            Synth.Master = S.HapMaster / 100f;
            // По USB напрямую — 16 бит и без удвоения в DSX: можно почти весь диапазон и почти без компрессии.
            Synth.Ceiling = S.DirectMode ? 0.92f : 0.46f;
            Synth.MaxLift = S.DirectMode ? 1.5f : 3f;
            Synth.TargetRms = S.DirectMode ? 0.22f : 0.13f;
            if (!S.HapticEnabled) { Synth.P = new HapticParams(); hapPrev = null; return; }

            float gE = S.HapEngine / 100f, gI = S.HapImpact / 100f,
                  gS = S.HapSlip / 100f, gSh = S.HapShift / 100f, gA = S.HapAbs / 100f;
            var p = new HapticParams();
            float spd = t.Speed;

            // Мотор. Реальная частота вспышек (до 400 Гц) моторами не ощущается — она пищит. Поэтому:
            //  • несущая 34–120 Гц растёт с оборотами (это "высота" мотора);
            //  • ритм = частота вспышек / 4 — на холостых 4-цилиндровый "бубнит" ~7 Гц, V8 — ~13 Гц,
            //    к отсечке ритм уходит за 30 Гц и сливается в ровный гул (как у настоящего мотора);
            //  • под газом пульс глубже и сильнее, на сбросе — тише; при переключении — провал (отсечка зажигания).
            int cyl = t.Cylinders >= 1 && t.Cylinders <= 16 ? t.Cylinders : 6;
            float rpm = Math.Max(0f, t.Rpm);
            float rf = t.MaxRpm > 500 ? Util.Clamp01(rpm / t.MaxRpm) : 0.3f;
            float cylShift = (Math.Min(12, cyl) - 4) * 1.5f;
            float th = t.Throttle;
            if (fr.Shift) engCutUntil = now + (fr.ShiftUp ? 95 : 60);
            float engAmp = gE * (0.10f + 0.34f * th + 0.14f * rf);
            if (th < 0.08f) engAmp *= 0.6f;
            if (now < engCutUntil) engAmp *= 0.05f;   // провал при переключении — отчётливый "разрыв" тяги
            // Характер мотора по типу (цилиндры + максимальные обороты) — встроенные паттерны.
            var ch = EngineCharacters.For(t.Cylinders, t.MaxRpm);
            p.EngFreq = 34f + ch.FreqOffset + ch.FreqSpan * (float)Math.Pow(rf, 0.9);
            p.EngPulse = t.Cylinders <= 0 ? 30f : rpm / 60f * cyl / 2f / 4f;
            p.EngLoad = th;
            p.EngAmp = engAmp * ch.AmpMul;
            p.EngPattern = ch.Pattern; p.EngDepth = ch.Depth; p.EngRough = ch.Rough;
            p.LimOn = fr.Limiter;

            // Дорога, покрытие, поребрики и лужи убраны: в FH6 это был лишь фоновый шум, мешавший мотору.
            bool moving = spd > 0.4f && !fr.Airborne;

            // Перенос веса: в повороте мотор "тяжелее" на внешнем борту (боковая перегрузка AccX: + = вправо,
            // значит, кузов давит влево). До ±85% при 1.2g; корень — уже лёгкий поворот заметен.
            float latG = Math.Max(-1.2f, Math.Min(1.2f, t.AccX / 9.81f)) / 1.2f;
            float wShift = 0.85f * S.HapWeight / 100f * Math.Sign(latG) * (float)Math.Sqrt(Math.Abs(latG));
            p.EngPanL = Math.Max(0.1f, 1f + wShift); p.EngPanR = Math.Max(0.1f, 1f - wShift);

            // Скольжение шин по бортам (combined slip > ~1 = резина срывается).
            float csL = Math.Max(Math.Abs(t.CombinedSlip[0]), Math.Abs(t.CombinedSlip[2]));
            float csR = Math.Max(Math.Abs(t.CombinedSlip[1]), Math.Abs(t.CombinedSlip[3]));
            // В FH6 CombinedSlip: медиана ~0.8, занос 2–5, дрифт 10–45. Логарифмическая шкала 1…15 —
            // различаются и лёгкое скольжение в повороте, и полный срыв.
            p.SlipL = moving ? gS * 0.55f * SlipLevel(csL) : 0f;
            p.SlipR = moving ? gS * 0.55f * SlipLevel(csR) : 0f;

            p.AbsFreq = S.AbsFrequency;
            p.AbsAmp = fr.Abs ? gA * 0.45f * (0.6f + 0.4f * fr.AbsLevel) : (fr.Lockup ? gA * 0.25f : 0f);
            p.SpinAmp = fr.Spin ? gS * 0.35f * (0.4f + 0.6f * fr.SpinLevel) : 0f;
            p.SpinFreq = 35f + 40f * fr.SpinLevel;
            Synth.P = p;

            // --- удары: только на новом пакете телеметрии
            var prev = hapPrev;
            if (prev != null && t.ReceivedMs != prev.ReceivedMs)
            {
                float pdt = Math.Max(0.004f, Math.Min(0.05f, (t.ReceivedMs - prev.ReceivedMs) / 1000f));
                // Подвеска: быстрое сжатие = удар (яма, кочка, приземление после прыжка).
                for (int i = 0; i < 4; i++)
                {
                    float v = (t.SuspMeters[i] - prev.SuspMeters[i]) / pdt;
                    // в FH6: p95 0.17 м/с, p99 0.7, приземления 5–8
                    if (v > 0.4f && now >= suspCool[i] && gI > 0)
                    {
                        float amp = gI * 1.5f * Util.Clamp01((float)Math.Sqrt((v - 0.4f) / 4f)); // мелочь — тихо, приземления — в полную
                        bool left = (i % 2) == 0;
                        Synth.Fire(new Impulse
                        {
                            Freq = i < 2 ? 62f : 54f, Decay = 0.05f + 0.05f * amp, Noise = 0.25f,
                            AmpL = left ? amp : amp * 0.3f, AmpR = left ? amp * 0.3f : amp
                        });
                        suspCool[i] = now + 110;
                    }
                }
                // Столкновение: резкий скачок горизонтального ускорения.
                float dax = t.AccX - prev.AccX, daz = t.AccZ - prev.AccZ;
                float jerk = (float)Math.Sqrt(dax * dax + daz * daz);
                // в FH6: обычная езда p99 ≈ 3, касания 10–30, лобовые до ~200
                if (jerk > 12f && now >= crashCool && gI > 0)
                {
                    float amp = gI * Util.Clamp01((float)Math.Sqrt((jerk - 12f) / 120f) + 0.15f) * 1.3f;
                    float side = Math.Abs(dax) / jerk;          // доля бокового удара
                    float pan = Util.Clamp01(0.5f + dax / 120f); // толкнуло вправо → удар слева
                    if (side > 0.65f)
                    {
                        // В борт — с той стороны, откуда прилетело.
                        Synth.Fire(new Impulse { Freq = 55f, Decay = 0.10f, Noise = 0.5f, AmpL = amp * (0.25f + 1.1f * pan), AmpR = amp * (1.35f - 1.1f * pan) });
                    }
                    else if (daz < 0)
                    {
                        // Лобовой (машину резко остановило): резкий короткий удар + сухой "треск" сверху.
                        Synth.Fire(new Impulse { Freq = 72f, Decay = 0.055f, Noise = 0.35f, AmpL = amp * 1.25f, AmpR = amp * 1.25f });
                        Synth.Fire(new Impulse { Freq = 110f, Decay = 0.02f, Noise = 0f, AmpL = amp * 0.6f, AmpR = amp * 0.6f, Delay = 0.01f });
                    }
                    else
                    {
                        // В зад (машину толкнуло вперёд): глухой длинный толчок.
                        Synth.Fire(new Impulse { Freq = 36f, Decay = 0.22f, Noise = 0.9f, AmpL = amp * 1.15f, AmpR = amp * 1.15f });
                    }
                    crashCool = now + 200;
                }
            }

            // "Нырок": резко ударил по тормозам на скорости — кузов клюёт носом: тяжёлый толчок + отскок.
            float gD = S.HapDive / 100f;
            if (fr.Dive && gD > 0)
            {
                float a = gD * (0.9f + 0.7f * Util.Clamp01(t.Speed / 40f));
                Synth.Fire(new Impulse { Freq = 42f, Decay = 0.15f, Noise = 0.7f, AmpL = a, AmpR = a });
                Synth.Fire(new Impulse { Freq = 58f, Decay = 0.06f, Noise = 0f, AmpL = a * 0.5f, AmpR = a * 0.5f, Delay = 0.14f });
            }
            // Старт с места в пол: удар сцепления — машину дёрнуло.
            if (fr.Launch && gD > 0)
            {
                Synth.Fire(new Impulse { Freq = 38f, Decay = 0.18f, Noise = 0.8f, AmpL = gD * 1.6f, AmpR = gD * 1.6f });
                Synth.Fire(new Impulse { Freq = 65f, Decay = 0.05f, Noise = 0f, AmpL = gD * 0.7f, AmpR = gD * 0.7f, Delay = 0.02f });
            }

            // Переключения. Вверх — жёсткий "клац": удар 75 Гц + отдача 48 Гц через 35 мс (сцепление схватило).
            // Вниз — два толчка с перегазовкой между ними.
            if (fr.Shift && gSh > 0)
            {
                if (fr.ShiftUp)
                {
                    Synth.Fire(new Impulse { Freq = 75f, Decay = 0.045f, Noise = 0.3f, AmpL = gSh * 1.6f, AmpR = gSh * 1.6f });
                    Synth.Fire(new Impulse { Freq = 48f, Decay = 0.07f, Noise = 0f, AmpL = gSh * 1.0f, AmpR = gSh * 1.0f, Delay = 0.035f });
                }
                else
                {
                    Synth.Fire(new Impulse { Freq = 60f, Decay = 0.04f, Noise = 0.2f, AmpL = gSh * 1.2f, AmpR = gSh * 1.2f });
                    Synth.Fire(new Impulse { Freq = 90f, Decay = 0.05f, Noise = 0f, AmpL = gSh * 0.7f, AmpR = gSh * 0.7f, Delay = 0.06f });   // перегазовка
                    Synth.Fire(new Impulse { Freq = 55f, Decay = 0.05f, Noise = 0.2f, AmpL = gSh * 1.1f, AmpR = gSh * 1.1f, Delay = 0.13f });
                }
            }

            // Приземление: чем дольше машина была в воздухе, тем мощнее удар (сверх обычного удара подвески).
            if (fr.Airborne) { if (airSince < 0) airSince = now; }
            else if (airSince >= 0)
            {
                float air = (now - airSince) / 1000f;
                airSince = -1;
                if (air > 0.15f && gI > 0)
                {
                    float a = gI * (0.6f + 1.2f * Util.Clamp01(air / 1.2f));
                    Synth.Fire(new Impulse { Freq = 52f, Decay = 0.09f + 0.12f * Util.Clamp01(air / 1.2f), Noise = 0.7f, AmpL = a, AmpR = a });
                }
            }

            prevThrottle = t.Throttle;
            hapPrev = t;
        }

        // Демонстрация хаптики без игры: разгон с турбиной → переключение вверх → поворот влево/вправо →
        // сброс газа (клапан) → переключение вниз → удар слева/справа → приземление.
        int demoStep;
        void HapticTest(float ph)
        {
            if (Synth == null) return;
            Synth.Master = S.HapMaster / 100f;
            float gE = Math.Max(0.4f, S.HapEngine / 100f), gI = Math.Max(0.5f, S.HapImpact / 100f),
                  gSh = Math.Max(0.5f, S.HapShift / 100f), gP = Math.Max(0.5f, S.HapDive / 100f), gW = Math.Max(0.5f, S.HapWeight / 100f);
            if (ph < 0.05f) demoStep = 0;
            var p = new HapticParams();
            float k = Math.Min(1f, ph / 1.6f);
            float rf = ph < 1.6f ? 0.15f + 0.8f * k : (ph < 3.0f ? 0.6f : 0.4f);
            float thr = ph < 3.6f ? 1f : 0f;
            p.EngFreq = 40f + 70f * (float)Math.Pow(rf, 0.9); p.EngPulse = (800f + 7000f * rf) / 60f * 6f / 2f / 4f;
            p.EngLoad = thr; p.EngAmp = gE * (0.10f + 0.34f * thr + 0.14f * rf) * (thr < 0.1f ? 0.6f : 1f);
            if (ph >= 2.0f && ph < 3.4f)
            {
                float lat = ph < 2.7f ? -1f : 1f;                    // поворот влево, потом вправо
                p.EngPanL = Math.Max(0.1f, 1f + 0.85f * gW * lat); p.EngPanR = Math.Max(0.1f, 1f - 0.85f * gW * lat);
            }
            if (ph >= 1.6f && ph < 1.7f) p.EngAmp *= 0.05f;          // провал при переключении вверх
            Fire(1.6f, ph, () => {
                Synth.Fire(new Impulse { Freq = 75f, Decay = 0.045f, Noise = 0.3f, AmpL = gSh * 1.6f, AmpR = gSh * 1.6f });
                Synth.Fire(new Impulse { Freq = 48f, Decay = 0.07f, AmpL = gSh, AmpR = gSh, Delay = 0.035f });
            });
            Fire(3.6f, ph, () => {                       // нырок при торможении
                Synth.Fire(new Impulse { Freq = 42f, Decay = 0.15f, Noise = 0.7f, AmpL = gP * 1.4f, AmpR = gP * 1.4f });
                Synth.Fire(new Impulse { Freq = 58f, Decay = 0.06f, AmpL = gP * 0.7f, AmpR = gP * 0.7f, Delay = 0.14f });
            });
            Fire(4.3f, ph, () => {
                Synth.Fire(new Impulse { Freq = 60f, Decay = 0.04f, Noise = 0.2f, AmpL = gSh * 1.2f, AmpR = gSh * 1.2f });
                Synth.Fire(new Impulse { Freq = 90f, Decay = 0.05f, AmpL = gSh * 0.7f, AmpR = gSh * 0.7f, Delay = 0.06f });
                Synth.Fire(new Impulse { Freq = 55f, Decay = 0.05f, Noise = 0.2f, AmpL = gSh * 1.1f, AmpR = gSh * 1.1f, Delay = 0.13f });
            });
            Fire(4.9f, ph, () => {                       // лобовой: резко и коротко
                Synth.Fire(new Impulse { Freq = 72f, Decay = 0.055f, Noise = 0.35f, AmpL = gI * 1.5f, AmpR = gI * 1.5f });
                Synth.Fire(new Impulse { Freq = 110f, Decay = 0.02f, AmpL = gI * 0.7f, AmpR = gI * 0.7f, Delay = 0.01f });
            });
            Fire(5.3f, ph, () => Synth.Fire(new Impulse { Freq = 36f, Decay = 0.22f, Noise = 0.9f, AmpL = gI * 1.4f, AmpR = gI * 1.4f })); // в зад
            Fire(5.75f, ph, () => Synth.Fire(new Impulse { Freq = 55f, Decay = 0.10f, Noise = 0.5f, AmpL = gI * 1.4f, AmpR = gI * 0.25f })); // в левый борт
            Fire(6.1f, ph, () => Synth.Fire(new Impulse { Freq = 52f, Decay = 0.2f, Noise = 0.7f, AmpL = gI * 1.8f, AmpR = gI * 1.8f }));   // приземление
            Synth.P = p;
        }

        // однократное событие демо в момент at (секунды)
        void Fire(float at, float ph, Action a)
        {
            int id = (int)(at * 10);
            if (ph >= at && (demoStep & (1 << (id % 31))) == 0) { demoStep |= 1 << (id % 31); a(); }
        }

        int[] ShiftLight(Telemetry t, long now)
        {
            if (t.MaxRpm < 500) return new[] { 0, 40, 0 };
            float f = t.Rpm / t.MaxRpm;
            if (f >= 0.97f) return ((now / 60) % 2 == 0) ? new[] { 255, 0, 0 } : new[] { 0, 0, 255 };
            float k = Util.Clamp01((f - 0.55f) / 0.42f);
            k = (float)Math.Round(k * 12) / 12f;
            return new[] { Util.RoundI(255 * Math.Min(1f, k * 2f)), Util.RoundI(255 * Math.Min(1f, (1f - k) * 2f)), 0 };
        }

        // ------------------------------------------------------------ ТЕСТ КУРКОВ БЕЗ ИГРЫ
        void ComputeTest(int which, float ph, out Eff L, out Eff R)
        {
            L = Eff.Off("—"); R = Eff.Off("—");
            if (which == 1)
            {
                int ft = Util.Clamp(S.BrakeFreeTravel, 0, 4);
                int bMin = Util.Clamp(S.BrakeMinForce, 1, 8), bMax = Math.Max(bMin, Util.Clamp(S.BrakeMaxForce, 1, 8));
                float gamma = Math.Max(0.5f, S.BrakeProgression / 100f);
                var zones = new int[10];
                for (int z = 0; z < 10; z++)
                    zones[z] = z < ft ? 0 : Util.Clamp(Util.RoundI(bMin + (bMax - bMin) * (float)Math.Pow((z - ft + 1f) / (10f - ft), gamma)), 1, 8);
                int amp = Math.Max(1, Util.RoundI(7f * S.AbsStrength / 100f));
                if (ph < 2.2f) L = Eff.Curve(zones, ft, zones[Math.Min(9, ft + 4)], Loc.T("Тест: педаль — дави до упора", "Test: pedal — press all the way"));
                else if (ph < 4.4f) { L = AbsEffect(ft, amp); L.Label = Loc.T("Тест: ", "Test: ") + L.Label; }
                else L = Eff.Machine(ft, Math.Min(7, amp), 0, 10, 1, Loc.T("Тест: блокировка", "Test: lock-up"));
            }
            else
            {
                int tMin = Util.Clamp(S.ThrottleMinForce, 1, 8), tMax = Math.Max(tMin, Util.Clamp(S.ThrottleMaxForce, 1, 8));
                var zones = new int[10];
                for (int z = 0; z < 10; z++) zones[z] = Util.Clamp(Util.RoundI(tMin + (tMax - tMin) * (z + 1f) / 10f + (S.ThrottleKickdown && z == 8 ? 4 : 0)), 1, 8);
                if (ph < 1.8f) R = Eff.Curve(zones, 0, tMin, Loc.T("Тест: педаль газа", "Test: throttle pedal"));
                else if (ph < 3.4f)
                {
                    float lv = (ph - 1.8f) / 1.6f;
                    R = Eff.Vibe(1, Math.Max(1, Util.RoundI((3 + 5 * lv) * S.WheelspinStrength / 100f)), Util.RoundI((26 + 44 * lv) / 2) * 2, Loc.T("Тест: пробуксовка", "Test: wheelspin"));
                }
                else if (ph < 4.6f) R = Eff.Machine(2, 5, 0, 45, 1, Loc.T("Тест: отсечка", "Test: rev limiter"));
                else
                {
                    float k = (ph - 4.6f) % 0.5f;
                    int kick = Util.Clamp(Util.RoundI(3f + 5f * S.ShiftKickStrength / 100f), 1, 8);
                    string sl = Loc.T("Тест: переключения", "Test: gear shifts");
                    R = k < 0.12f ? Eff.Vibe(0, kick, 14, sl) : Eff.Curve(zones, 0, tMin, sl);
                }
            }
        }
    }

    // Интерфейс (Theme, LivePanel, MainForm) — в Ui.cs.

    static class Program
    {
        public const string Version = "2.1";
        public const string RepoUrl = "https://github.com/GefryV3/ForzaTriggers-DualSense-Forza-Horizon";

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

#if SELFTEST
        static void Main(string[] args) { SelfTest.Run(args); }
#else
        [STAThread]
        static void Main()
        {
            bool created;
#if UIPREVIEW
            const string mutexName = "ForzaTriggers_single_instance_preview";
#else
            const string mutexName = "ForzaTriggers_single_instance";
#endif
            using (var m = new Mutex(true, mutexName, out created))
            {
                if (!created)
                {
                    Loc.Apply(Settings.Load().Language);
                    MessageBox.Show(Loc.T("ForzaTriggers уже запущен.", "ForzaTriggers is already running."), "ForzaTriggers", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
#endif
    }
}
