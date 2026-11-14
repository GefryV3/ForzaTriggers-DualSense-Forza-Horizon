#if SELFTEST
// Test bench / diagnostics (built only with /define:SELFTEST → ForzaTriggers.Diag.exe):
//   Diag.exe                — trigger scenarios: synthetic FH6 packets → the JSON that would go to DSX
//   Diag.exe wav <path>     — same scenarios + render the haptic signal to a WAV with per-scenario levels
//   Diag.exe dev            — play the haptics demo on a real DualSense (USB)
//   Diag.exe analyze <file> — statistics of a telemetry-*.bin recording
//   Diag.exe direct [sec]   — direct mode check: buttons, triggers, USB haptics
// Тестовый стенд (собирается только с /define:SELFTEST). Язык вывода — как в ForzaTriggers.ini.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ForzaTriggers
{
    static class SelfTest
    {
        static string T(string ru, string en) { return Loc.T(ru, en); }

        class Pkt
        {
            public byte[] D = new byte[324];
            public Pkt()
            {
                I(0, 1); F(8, 8000); F(12, 900); F(16, 3000); I(228, 6);
                for (int i = 0; i < 4; i++) { F(68 + 4 * i, 0.5f); F(100 + 4 * i, 30f); F(196 + 4 * i, 0.10f); }
                I(224, 1); B(319, 3); F(256, 20f);
            }
            public Pkt F(int o, float v) { BitConverter.GetBytes(v).CopyTo(D, o); return this; }
            public Pkt I(int o, int v) { BitConverter.GetBytes(v).CopyTo(D, o); return this; }
            public Pkt B(int o, int v) { D[o] = (byte)v; return this; }
            public Pkt Thr(float v) { return B(315, (int)(v * 255)); }
            public Pkt Brk(float v) { return B(316, (int)(v * 255)); }
            public Pkt Gear(int g) { return B(319, g); }
            public Pkt SlipAll(float fr, float rr) { F(84, fr); F(88, fr); F(92, rr); return F(96, rr); }
            public Pkt Comb(float l, float r) { F(180, l); F(188, l); F(184, r); return F(192, r); }
        }

        class Case { public string Name; public Func<int, Pkt> Make; public int Ms = 400; }

        static List<Case> Cases()
        {
            var c = new List<Case>();
            c.Add(new Case { Name = T("Меню (IsRaceOn=0)", "Menu (IsRaceOn=0)"), Make = k => new Pkt().I(0, 0) });
            c.Add(new Case { Name = T("Круиз 72 км/ч, газ 40%", "Cruise 72 km/h, 40% throttle"), Make = k => new Pkt().Thr(0.4f).F(28, 1.5f).F(16, 3500) });
            c.Add(new Case { Name = T("Разгон в пол, 0.8g, 6500 об", "Full throttle, 0.8g, 6500 rpm"), Make = k => new Pkt().Thr(1f).F(28, 7.8f).F(16, 6500) });
            c.Add(new Case { Name = T("Торможение 60%, 0.9g", "Braking 60%, 0.9g"), Make = k => new Pkt().Brk(0.6f).F(28, -8.8f).SlipAll(0.2f, 0.1f) });
            c.Add(new Case { Name = T("Торможение у предела (скольж. 0.48)", "Braking at the limit (slip 0.48)"), Make = k => new Pkt().Brk(0.6f).F(28, -10.8f).SlipAll(0.48f, 0.3f) });
            c.Add(new Case { Name = T("ABS: срыв передних 1.2", "ABS: front slip 1.2"), Make = k => new Pkt().Brk(1f).F(28, -9f).SlipAll(1.2f, 0.4f).Comb(1.5f, 1.5f) });
            c.Add(new Case { Name = T("Блокировка (колёса стоят)", "Lock-up (wheels stopped)"), Make = k => new Pkt().Brk(1f).F(28, -7f).SlipAll(3f, 3f).F(100, 0).F(104, 0).Comb(3f, 3f) });
            c.Add(new Case { Name = T("Пробуксовка RWD, задние 2.5", "RWD wheelspin, rear 2.5"), Make = k => new Pkt().Thr(1f).F(28, 3f).SlipAll(0.1f, 2.5f).Comb(0.3f, 2.6f) });
            c.Add(new Case { Name = T("Отсечка 7900/8000", "Rev limiter 7900/8000"), Make = k => new Pkt().Thr(1f).F(16, 7900f).F(28, 2f) });
            c.Add(new Case { Name = T("Поребрик слева, газ 60%", "Kerb on the left, 60% throttle"), Make = k => new Pkt().Thr(0.6f).I(116, 1).I(124, 1) });
            c.Add(new Case { Name = T("Гравий (surface rumble 0.6)", "Gravel (surface rumble 0.6)"), Make = k => new Pkt().Thr(0.5f).F(148, 0.6f).F(152, 0.6f).F(156, 0.6f).F(160, 0.6f) });
            c.Add(new Case { Name = T("Кочки: подвеска ходит 3 см/кадр", "Bumps: suspension moves 3 cm/frame"), Make = k => { var p = new Pkt().Thr(0.5f); float s = (k % 6 < 3) ? 0.10f : 0.16f; for (int i = 0; i < 4; i++) p.F(196 + 4 * i, s); return p; } });
            c.Add(new Case { Name = T("Удар слева (скачок AccX)", "Hit from the left (AccX spike)"), Make = k => new Pkt().Thr(0.3f).F(20, k == 5 ? 90f : 0f) });
            c.Add(new Case { Name = T("Нырок: тормоз в пол на 90 км/ч", "Dive: full brake at 90 km/h"), Make = k => k < 4 ? new Pkt().Thr(0.5f).F(256, 25f) : new Pkt().Brk(1f).F(256, 24f).F(28, -9f) });
            c.Add(new Case { Name = T("Старт с места в пол", "Standing launch, full throttle"), Make = k => k < 4 ? new Pkt().F(256, 0.2f).F(40, 0.2f) : new Pkt().Thr(1f).F(256, 0.5f).F(40, 0.5f).F(28, 6f) });
            c.Add(new Case { Name = T("Лобовой удар (AccZ −80)", "Frontal crash (AccZ −80)"), Make = k => new Pkt().Thr(0.3f).F(28, k == 5 ? -80f : 0f) });
            c.Add(new Case { Name = T("Удар в зад (AccZ +60)", "Rear hit (AccZ +60)"), Make = k => new Pkt().Thr(0.3f).F(28, k == 5 ? 60f : 0f) });
            c.Add(new Case { Name = T("V8 американский, 2500 об", "Cross-plane V8, 2500 rpm"), Make = k => new Pkt().Thr(0.6f).I(228, 8).F(8, 7000).F(16, 2500) });
            c.Add(new Case { Name = T("V12, 2500 об", "V12, 2500 rpm"), Make = k => new Pkt().Thr(0.6f).I(228, 12).F(8, 9000).F(16, 2500) });
            c.Add(new Case { Name = T("Поворот вправо 1g (вес влево)", "Right turn 1g (weight to the left)"), Make = k => new Pkt().Thr(0.7f).F(16, 5000).F(20, 9.8f) });
            c.Add(new Case { Name = T("Холостые 900 об, V8", "Idle 900 rpm, V8"), Make = k => new Pkt().F(16, 900).I(228, 8).F(256, 0.1f).F(40, 0.1f) });
            c.Add(new Case { Name = T("Переключения 3↔4 каждые 160 мс, газ", "Shifts 3↔4 every 160 ms, on throttle"), Make = k => new Pkt().Thr(1f).F(16, 6000).Gear((k / 10) % 2 == 0 ? 3 : 4).F(28, 4f).SlipAll(0.3f, 0.6f).Comb(0.8f, 1.1f), Ms = 700 });
            c.Add(new Case { Name = T("Сброс газа на 6500 об (хлопки)", "Lift-off at 6500 rpm (pops)"), Make = k => new Pkt().Thr(0f).F(16, 6500).F(28, -2f), Ms = 700 });
            c.Add(new Case { Name = T("В воздухе, газ в пол", "Airborne, full throttle"), Make = k => { var p = new Pkt().Thr(1f).SlipAll(0.1f, 3f); for (int i = 0; i < 4; i++) p.F(68 + 4 * i, 0f); return p; } });
            return c;
        }

        public static void Run(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Loc.Apply(Settings.Load().Language);
            string mode = args.Length > 0 ? args[0] : "triggers";
            if (mode == "dev") { Device(); return; }
            if (mode == "tones") { Tones(); return; }
            if (mode == "clean") { Clean(); return; }
            if (mode == "bisect") { Bisect(); return; }
            if (mode == "wake") { Wake(); return; }
            if (mode == "analyze")
            {
                if (args.Length < 2) { Console.WriteLine(T("Использование: Diag.exe analyze <telemetry-*.bin>", "Usage: Diag.exe analyze <telemetry-*.bin>")); return; }
                Analyze(args[1]); return;
            }
            if (mode == "direct")
            {
                int sec;
                DirectDiag(args.Length > 1 && int.TryParse(args[1], out sec) ? sec : 10); return;
            }
            if (mode == "dsxcheck") { Console.WriteLine(T("Проверка профиля DSX: ", "DSX profile check: ") + (DsxProfile.RumbleWarning() ?? T("ок, вибрация игры выключена", "ok, game rumble is off"))); return; }
            string wavPath = mode == "wav" && args.Length > 1 ? args[1] : null;

            var s = new Settings { ForzaPort = 5391, DsxPort = 6991 };
            var synth = new HapticSynth();
            var dsx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 6991));
            dsx.Client.ReceiveTimeout = 5;
            var eng = new Engine(s, synth);
            eng.Start();
            var game = new UdpClient();
            var to = new IPEndPoint(IPAddress.Loopback, 5391);

            // "виртуальная звуковая карта": рендерим хаптику в реальном времени, пока идут сценарии
            var wavL = new List<float>(); var wavR = new List<float>();
            bool rendering = wavPath != null;
            var renderSw = System.Diagnostics.Stopwatch.StartNew();
            long rendered = 0;
            var lbuf = new float[4800]; var rbuf = new float[4800];
            Action pump = () =>
            {
                if (!rendering) return;
                long want = renderSw.ElapsedMilliseconds * 48;
                while (rendered < want)
                {
                    int n = (int)Math.Min(480, want - rendered);
                    synth.Render(lbuf, rbuf, n);
                    for (int i = 0; i < n; i++) { wavL.Add(lbuf[i]); wavR.Add(rbuf[i]); }
                    rendered += n;
                }
            };

            Console.WriteLine(T("==================== DSX v3, курки + хаптика ====================", "==================== DSX v3, triggers + haptics ===================="));
            foreach (var c in Cases())
            {
                int startSample = wavL.Count;
                int firedBefore = synth.FiredCount;
                long end = Environment.TickCount + c.Ms;
                string last = null; int k = 0;
                while (Environment.TickCount < end)
                {
                    var d = c.Make(k++).D;
                    game.Send(d, d.Length, to);
                    Thread.Sleep(16);
                    pump();
                    last = Drain(dsx) ?? last;
                }
                Thread.Sleep(15); pump();
                last = Drain(dsx) ?? last;
                var st = eng.Stat;
                Console.WriteLine("{0,-38} L2: {1,-26} R2: {2}", c.Name, st.L.Label, st.R.Label);
                Console.WriteLine("    " + last);
                if (rendering)
                {
                    double sl = 0, sr = 0, hf = 0, all = 0; float pl = 0, pr = 0; int n = wavL.Count - startSample;
                    var h1 = Biquad.HighPass(250, 0.541, 48000); var h2 = Biquad.HighPass(250, 1.307, 48000);
                    for (int i = startSample; i < wavL.Count; i++)
                    {
                        sl += wavL[i] * wavL[i]; sr += wavR[i] * wavR[i]; pl = Math.Max(pl, Math.Abs(wavL[i])); pr = Math.Max(pr, Math.Abs(wavR[i]));
                        float h = h2.Run(h1.Run(wavL[i])); hf += h * h; all += wavL[i] * wavL[i];
                    }
                    Console.WriteLine(T("    доля энергии выше 250 Гц (слышимое): {0:0.00}%", "    energy share above 250 Hz (audible): {0:0.00}%"), all > 0 ? 100 * hf / all : 0);
                    Console.WriteLine(T("    хаптика: RMS L {0:0.000} R {1:0.000} · пик L {2:0.00} R {3:0.00} · ударов {4} (последний L {5:0.00} R {6:0.00})",
                                        "    haptics: RMS L {0:0.000} R {1:0.000} · peak L {2:0.00} R {3:0.00} · impulses {4} (last L {5:0.00} R {6:0.00})"),
                        Math.Sqrt(sl / Math.Max(1, n)), Math.Sqrt(sr / Math.Max(1, n)), pl, pr,
                        synth.FiredCount - firedBefore, synth.LastPanL, synth.LastPanR);
                }
            }
            eng.Stop(true);
            Thread.Sleep(30);
            Console.WriteLine(T("Выход: ", "On exit: ") + Drain(dsx));
            if (wavPath != null) { WriteWav(wavPath, wavL, wavR); Console.WriteLine("WAV: " + wavPath + " (" + (wavL.Count / 48000.0).ToString("0.0") + T(" с)", " s)")); }
        }

        static void Device()
        {
            var s = Settings.Load();
            var synth = new HapticSynth();
            var hout = new HapticOutput(synth);
            hout.Start();
            var eng = new Engine(new Settings { ForzaPort = 5392, DsxPort = 6992, HapMaster = s.HapMaster }, synth);
            eng.Start();
            for (int i = 0; i < 10 && !hout.Ok; i++) Thread.Sleep(100);
            Console.WriteLine(T("Состояние: ", "State: ") + hout.State);
            if (hout.Ok)
            {
                eng.StartTest(3);
                float maxL = 0, maxR = 0;
                for (int i = 0; i < 66; i++) { Thread.Sleep(100); maxL = Math.Max(maxL, synth.PeakL); maxR = Math.Max(maxR, synth.PeakR); }
                Console.WriteLine(T("Демо отыграно. Пиковый уровень на моторах: L {0:0.00} R {1:0.00}", "Demo played. Peak level on the motors: L {0:0.00} R {1:0.00}"), maxL, maxR);
                Console.WriteLine(T("Состояние после: ", "State after: ") + hout.State);
            }
            eng.Stop(false);
            hout.Stop();
        }

        // Мощные тоны на моторы: 4 частоты по 1.2 с (громкость почти максимальная), без мастер-громкости.
        static void Tones()
        {
            var synth = new HapticSynth { Master = 1f };
            var hout = new HapticOutput(synth);
            hout.Start();
            for (int i = 0; i < 20 && !hout.Ok; i++) Thread.Sleep(100);
            Console.WriteLine(T("Вывод: ", "Output: ") + hout.State);
            if (hout.Ok)
            {
                foreach (var f in new[] { 40f, 80f, 160f, 250f })
                {
                    synth.ToneFreq = f; synth.ToneAmp = 0.9f;
                    Thread.Sleep(1200);
                    synth.ToneAmp = 0f;
                    Thread.Sleep(300);
                    Console.WriteLine(T("  тон ", "  tone ") + f + T(" Гц отыгран, пик ", " Hz played, peak ") + synth.PeakL.ToString("0.00"));
                }
            }
            hout.Stop();
        }

        // Разбор записи телеметрии: какие значения FH6 реально отдаёт.
        static void Analyze(string path)
        {
            var list = new List<Telemetry>();
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                while (r.BaseStream.Position < r.BaseStream.Length)
                {
                    long ms = r.ReadInt64(); int len = r.ReadInt32(); var b = r.ReadBytes(len);
                    var t = ForzaParser.Parse(b, len);
                    if (t != null) { t.ReceivedMs = ms; list.Add(t); }
                }
            }
            var race = list.FindAll(x => x.RaceOn);
            Console.WriteLine(T("Пакетов: {0} (в гонке {1}), длина {2} байт, длительность {3:0.0} с", "Packets: {0} (racing {1}), length {2} bytes, duration {3:0.0} s"), list.Count, race.Count,
                list.Count > 0 ? list[0].Length : 0, list.Count > 1 ? (list[list.Count - 1].ReceivedMs - list[0].ReceivedMs) / 1000.0 : 0);
            if (race.Count < 2) return;
            var t0 = race[0];
            Console.WriteLine(T("Машина: цилиндров {0}, привод {1}, MaxRpm {2:0}", "Car: cylinders {0}, drivetrain {1}, MaxRpm {2:0}"), t0.Cylinders, t0.Drivetrain, t0.MaxRpm);
            Console.WriteLine(T("Скорость: макс {0:0} км/ч", "Speed: max {0:0} km/h"), Max(race, x => x.Speed * 3.6f));

            string[] w = { "FL", "FR", "RL", "RR" };
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                Console.WriteLine(T("SurfaceRumble {0}: {1}   | на поребрике {2} кадров | лужа макс {3:0.000}", "SurfaceRumble {0}: {1}   | on rumble strip {2} frames | puddle max {3:0.000}"),
                    w[i], Stats(race, x => x.SurfaceRumble[k]), race.FindAll(x => x.RumbleStrip[k]).Count, Max(race, x => x.Puddle[k]));
            }
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                var vel = new List<float>();
                for (int j = 1; j < race.Count; j++)
                {
                    float dt = Math.Max(0.004f, (race[j].ReceivedMs - race[j - 1].ReceivedMs) / 1000f);
                    vel.Add((race[j].SuspMeters[k] - race[j - 1].SuspMeters[k]) / dt);
                }
                Console.WriteLine(T("Скорость подвески {0} (м/с, + = сжатие): {1} | ход {2}", "Suspension velocity {0} (m/s, + = compression): {1} | travel {2}"), w[i], StatsL(vel), Stats(race, x => x.SuspMeters[k]));
            }
            var jerk = new List<float>();
            for (int j = 1; j < race.Count; j++)
            {
                float dx = race[j].AccX - race[j - 1].AccX, dz = race[j].AccZ - race[j - 1].AccZ;
                jerk.Add((float)Math.Sqrt(dx * dx + dz * dz));
            }
            Console.WriteLine(T("Скачок ускорения за кадр: ", "Acceleration jump per frame: ") + StatsL(jerk));
            Console.WriteLine(T("Вертикальное ускорение AccY: ", "Vertical acceleration AccY: ") + Stats(race, x => x.AccY));
            Console.WriteLine(T("CombinedSlip макс по колёсам: ", "CombinedSlip max over wheels: ") + Stats(race, x => Math.Max(Math.Max(Math.Abs(x.CombinedSlip[0]), Math.Abs(x.CombinedSlip[1])), Math.Max(Math.Abs(x.CombinedSlip[2]), Math.Abs(x.CombinedSlip[3])))));
            var gears = new List<string>(); int lastG = -1, changes = 0;
            foreach (var x in race) { if (x.Gear != lastG) { if (lastG >= 0) changes++; if (gears.Count < 30) gears.Add(x.Gear.ToString()); lastG = x.Gear; } }
            Console.WriteLine(T("Передачи: смен {0}, последовательность {1}", "Gears: {0} changes, sequence {1}"), changes, string.Join(">", gears.ToArray()));
            // как часто сработают детекторы с текущими порогами
            double minutes = Math.Max(1e-6, (race[race.Count - 1].ReceivedMs - race[0].ReceivedMs) / 60000.0);
            int curbFrames = race.FindAll(x => x.Speed > 2f && (Math.Abs(x.SurfaceRumble[0]) > 0.05f || Math.Abs(x.SurfaceRumble[1]) > 0.05f || Math.Abs(x.SurfaceRumble[2]) > 0.05f || Math.Abs(x.SurfaceRumble[3]) > 0.05f)).Count;
            int susp = 0, crash = 0; long[] cool = new long[4]; long crashCool = 0;
            var slipLv = new List<float>();
            for (int j = 1; j < race.Count; j++)
            {
                var a = race[j - 1]; var b = race[j]; long now = b.ReceivedMs;
                float dt = Math.Max(0.004f, (b.ReceivedMs - a.ReceivedMs) / 1000f);
                for (int i = 0; i < 4; i++) { float v = (b.SuspMeters[i] - a.SuspMeters[i]) / dt; if (v > 0.4f && now >= cool[i]) { susp++; cool[i] = now + 110; } }
                float dx = b.AccX - a.AccX, dz = b.AccZ - a.AccZ;
                if (Math.Sqrt(dx * dx + dz * dz) > 12 && now >= crashCool) { crash++; crashCool = now + 200; }
                float cs = Math.Max(Math.Max(Math.Abs(b.CombinedSlip[0]), Math.Abs(b.CombinedSlip[1])), Math.Max(Math.Abs(b.CombinedSlip[2]), Math.Abs(b.CombinedSlip[3])));
                slipLv.Add(cs <= 1.2f ? 0f : (float)Math.Min(1.0, Math.Log(cs / 1.2) / Math.Log(15.0 / 1.2)));
            }
            Console.WriteLine(T("Детекторы: поребрик/рифлёнка {0:0.0}% времени · удары подвески {1:0} в минуту · толчки/аварии {2} за запись",
                                "Detectors: kerb/rumble {0:0.0}% of the time · suspension hits {1:0} per minute · bumps/crashes {2} in the recording"),
                100.0 * curbFrames / race.Count, susp / minutes, crash);
            Console.WriteLine(T("Уровень скольжения (0..1): ", "Slip level (0..1): ") + StatsL(slipLv));
            int air = race.FindAll(x => x.SuspNorm[0] < 0.03f && x.SuspNorm[1] < 0.03f && x.SuspNorm[2] < 0.03f && x.SuspNorm[3] < 0.03f).Count;
            Console.WriteLine(T("Кадров в воздухе: ", "Airborne frames: ") + air);
        }

        static float Max(List<Telemetry> l, Func<Telemetry, float> f) { float m = float.MinValue; foreach (var x in l) m = Math.Max(m, f(x)); return m; }
        static string Stats(List<Telemetry> l, Func<Telemetry, float> f) { var v = new List<float>(); foreach (var x in l) v.Add(f(x)); return StatsL(v); }
        static string StatsL(List<float> v)
        {
            if (v.Count == 0) return "—";
            var s = new List<float>(v); s.Sort();
            Func<double, float> q = p => s[Math.Min(s.Count - 1, (int)(p * (s.Count - 1)))];
            return string.Format(T("мин {0:0.000} · p50 {1:0.000} · p95 {2:0.000} · p99 {3:0.000} · макс {4:0.000}", "min {0:0.000} · p50 {1:0.000} · p95 {2:0.000} · p99 {3:0.000} · max {4:0.000}"),
                                 s[0], q(0.5), q(0.95), q(0.99), s[s.Count - 1]);
        }

        // Гипотеза "аудиотракт спит при тишине в каналах 1–2".
        static void Wake()
        {
            var dc = new DirectController();
            dc.Start();
            for (int i = 0; i < 40 && !dc.Ready; i++) Thread.Sleep(100);
            Console.WriteLine(T("Состояние: ", "State: ") + dc.State);
            var synth = new HapticSynth { Master = 1f, Ceiling = 0.92f, MaxLift = 1f };
            var hout = new HapticOutput(synth) { PreferDsx = () => false, SpeakerTest = true };
            hout.Start();
            for (int i = 0; i < 20 && !hout.Ok; i++) Thread.Sleep(100);
            Console.WriteLine(T("Вывод: ", "Output: ") + hout.State);
            Thread.Sleep(500);
            synth.ToneFreq = 80; synth.ToneAmp = 0.8f; Thread.Sleep(2500); synth.ToneAmp = 0f;
            Console.WriteLine(T("Импульс 1 — каналы 1–2 в тишине", "Pulse 1 — channels 1–2 silent"));
            Thread.Sleep(1200);
            hout.SpeakerToneAmp = 0.5f; Thread.Sleep(1500); hout.SpeakerToneAmp = 0.003f;
            synth.ToneFreq = 80; synth.ToneAmp = 0.8f; Thread.Sleep(2500); synth.ToneAmp = 0f;
            Console.WriteLine(T("Импульс 2 — после писка, в каналах 1–2 едва слышный сигнал", "Pulse 2 — after the beep, a barely audible signal on channels 1–2"));
            hout.SpeakerToneAmp = 0f;
            Thread.Sleep(3000);
            synth.ToneFreq = 80; synth.ToneAmp = 0.8f; Thread.Sleep(2500); synth.ToneAmp = 0f;
            Console.WriteLine(T("Импульс 3 — каналы 1–2 снова в тишине (3 с паузы)", "Pulse 3 — channels 1–2 silent again (3 s pause)"));
            hout.Stop(); dc.Stop();
        }

        // Бисекция: какой выходной отчёт выключает аудио-хаптику. Каждый шаг — отчёт + импульс 80 Гц 2.5 с.
        static void Bisect()
        {
            var dc = new DirectController();
            dc.Start();
            for (int i = 0; i < 40 && !dc.Ready; i++) Thread.Sleep(100);
            Console.WriteLine(T("Состояние: ", "State: ") + dc.State);
            if (!dc.Ready) { dc.Stop(); return; }
            var synth = new HapticSynth { Master = 1f, Ceiling = 0.92f, MaxLift = 1f };
            var hout = new HapticOutput(synth) { PreferDsx = () => false };
            hout.Start();
            for (int i = 0; i < 20 && !hout.Ok; i++) Thread.Sleep(100);
            Console.WriteLine(T("Вывод: ", "Output: ") + hout.State);
            int L = dc.OutputLength;
            var steps = new List<KeyValuePair<string, Func<byte[]>>>();
            Func<byte[]> blank = () => { var o = new byte[L]; o[0] = 0x02; return o; };
            steps.Add(new KeyValuePair<string, Func<byte[]>>(T("1: без отчётов (как в чистом тесте)", "1: no reports (like the clean test)"), () => null));
            steps.Add(new KeyValuePair<string, Func<byte[]>>(T("2: пустой отчёт (все флаги 0)", "2: empty report (all flags 0)"), blank));
            steps.Add(new KeyValuePair<string, Func<byte[]>>(T("3: только курки (flag0=0x0C, Off)", "3: triggers only (flag0=0x0C, Off)"), () => { var o = blank(); o[1] = 0x0C; o[11] = 0x05; o[22] = 0x05; return o; }));
            steps.Add(new KeyValuePair<string, Func<byte[]>>(T("4: подсветка (flag1=0x04)", "4: light bar (flag1=0x04)"), () => { var o = blank(); o[2] = 0x04; o[45] = 0; o[46] = 80; o[47] = 255; return o; }));
            steps.Add(new KeyValuePair<string, Func<byte[]>>(T("5: энергосбережение flag1=0x02, байт10=0x00", "5: power save flag1=0x02, byte10=0x00"), () => { var o = blank(); o[2] = 0x02; o[10] = 0x00; return o; }));
            int k = 0;
            foreach (var s in steps)
            {
                k++;
                var rep = s.Value();
                if (rep != null) Console.WriteLine(T("  отчёт записан: ", "  report written: ") + dc.WriteRaw(rep));
                Thread.Sleep(300);
                synth.ToneFreq = 80; synth.ToneAmp = 0.8f; Thread.Sleep(2500); synth.ToneAmp = 0f;
                Console.WriteLine(T("Импульс ", "Pulse ") + k + " — " + s.Key);
                Thread.Sleep(1200);
            }
            hout.Stop();
            dc.Stop();
        }

        // Прямой режим: DualSense по USB → виртуальный Xbox; курки и хаптика напрямую.
        static void DirectDiag(int buttonSeconds)
        {
            var dc = new DirectController();
            dc.Start();
            for (int i = 0; i < 40 && !dc.Ready; i++) Thread.Sleep(100);
            Console.WriteLine(T("Состояние: ", "State: ") + dc.State);
            if (!dc.Ready) { dc.Stop(); return; }

            Console.WriteLine(T("Кнопки (", "Buttons (") + buttonSeconds + T(" с): жми что угодно", " s): press anything"));
            string last = null; int rep0 = dc.InputReports; var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < buttonSeconds * 1000)
            {
                var b = dc.LastButtons;
                if (b != last) { Console.WriteLine("  " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + T(" с: ", " s: ") + b); last = b; }
                Thread.Sleep(20);
            }
            Console.WriteLine(T("Входных отчётов: ", "Input reports: ") + (dc.InputReports - rep0) + T(" за ", " in ") + buttonSeconds + T(" с", " s"));

            Console.WriteLine(T("Курки: L2 — тугая педаль, R2 — вибрация 30 Гц (5 с)", "Triggers: L2 — stiff pedal, R2 — 30 Hz vibration (5 s)"));
            var zones = new[] { 0, 2, 3, 4, 5, 6, 7, 8, 8, 8 };
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            while (t0.ElapsedMilliseconds < 5000) { dc.SetOutput(Eff.Curve(zones, 1, 5, ""), Eff.Vibe(1, 6, 30, ""), new[] { 255, 60, 0 }); Thread.Sleep(50); }

            Console.WriteLine(T("Хаптика по USB: 4 тона", "USB haptics: 4 tones"));
            var synth = new HapticSynth { Master = 1f, Ceiling = 0.92f, MaxLift = 1f };
            var hout = new HapticOutput(synth) { PreferDsx = () => false };
            hout.Start();
            for (int i = 0; i < 20 && !hout.Ok; i++) Thread.Sleep(100);
            Console.WriteLine(T("  вывод: ", "  output: ") + hout.State);
            foreach (var f in new[] { 40f, 80f, 160f, 250f })
            {
                dc.SetOutput(Eff.Off(""), Eff.Off(""), null); // держим хаптику включённой
                synth.ToneFreq = f; synth.ToneAmp = 0.8f; Thread.Sleep(1500); synth.ToneAmp = 0f; Thread.Sleep(400);
                Console.WriteLine(T("  тон ", "  tone ") + f + T(" Гц", " Hz"));
            }
            hout.Stop();
            dc.Stop();
            Console.WriteLine(T("Готово, виртуальный геймпад отключён.", "Done, the virtual gamepad is unplugged."));
        }

        // Чистый тест без DSX: писк в динамик геймпада (каналы 1–2), затем тоны на моторы (каналы 3–4).
        static void Clean()
        {
            var synth = new HapticSynth { Master = 1f };
            var hout = new HapticOutput(synth) { PreferDsx = () => false, SpeakerTest = true };
            hout.Start();
            for (int i = 0; i < 20 && !hout.Ok; i++) Thread.Sleep(100);
            Console.WriteLine(T("Вывод: ", "Output: ") + hout.State);
            if (!hout.Ok) { hout.Stop(); return; }
            Console.WriteLine(T("  писк 600 Гц в динамик…", "  600 Hz beep on the speaker…"));
            hout.SpeakerToneAmp = 0.5f; Thread.Sleep(2000); hout.SpeakerToneAmp = 0f; Thread.Sleep(500);
            foreach (var f in new[] { 40f, 80f, 160f, 250f })
            {
                synth.ToneFreq = f; synth.ToneAmp = 0.9f; Thread.Sleep(1200); synth.ToneAmp = 0f; Thread.Sleep(300);
                Console.WriteLine(T("  тон ", "  tone ") + f + T(" Гц на моторы", " Hz on the motors"));
            }
            hout.Stop();
        }

        static string Drain(UdpClient c)
        {
            string last = null;
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                try { var b = c.Receive(ref ep); last = Encoding.UTF8.GetString(b); }
                catch (SocketException) { return last; }
            }
        }

        static void WriteWav(string path, List<float> l, List<float> r)
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int n = l.Count, data = n * 4;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(48000); w.Write(48000 * 4); w.Write((short)4); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
                for (int i = 0; i < n; i++) { w.Write((short)(l[i] * 32767)); w.Write((short)(r[i] * 32767)); }
            }
        }
    }
}
#endif
