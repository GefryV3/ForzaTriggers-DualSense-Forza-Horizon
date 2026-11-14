// Тактильный синтезатор для вибромоторов DualSense.
// Принципы (чтобы ощущалось, а не звучало):
//  • вся энергия в полосе 20–180 Гц: выше моторы уже не "толкают", а пищат как динамик;
//  • никакого широкополосного шума — он слышен как шипение/"песок". Шум только сильно отфильтрованный
//    (< 60 Гц) или как огибающая (модуляция амплитуды синуса) — так получается "зерно" без шипения;
//  • удары — короткие синусы с опусканием высоты тона (быстрый "панч" → низкий "тум");
//  • на выходе: ФВЧ 18 Гц (убрать постоянку) + ФНЧ Баттерворта 180 Гц 4-го порядка + мягкий ограничитель.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;

namespace ForzaTriggers
{
    // Параметры, которые движок обновляет ~60–120 раз в секунду; синтезатор плавно ведёт их по каждому сэмплу.
    public class HapticParams
    {
        public float EngFreq = 40, EngAmp;          // мотор: несущая (ощущаемая) частота, 30–120 Гц
        public float EngPulse = 8;                 // ритм вспышек (вспышки/4), 4–32 Гц: на низах "бубнит", к отсечке сливается
        public float EngLoad;                      // нагрузка 0..1 — глубже пульс под газом
        public float[] EngPattern;                 // характер мотора: сила каждой вспышки по кругу
        public float EngDepth = 0.6f, EngRough = 0.1f;
        public bool LimOn;                         // отсечка — мотор "захлёбывается"
        public float RoadL, RoadR, RoadCut = 25;   // текстура дороги по бортам; срез фильтра, Гц (≤ 60)
        public float CurbRateL, CurbRateR, CurbL, CurbR; // поребрики: частота ударов и сила по бортам
        public float SlipL, SlipR;                 // скольжение шин (зернистая вибрация)
        public float SplashL, SplashR;             // лужи
        public float AbsFreq = 16, AbsAmp;         // толчки ABS
        public float SpinAmp, SpinFreq = 40;       // пробуксовка
        public float EngPanL = 1, EngPanR = 1;     // перенос веса: мотор "тяжелее" на внешнем борту
        public float TurboAmp, TurboFreq = 100;    // турбина: нарастающий гул наддува
    }

    public struct Impulse
    {
        public float Freq, AmpL, AmpR, Decay, Noise; // Noise: доля низкочастотного "грохота" (0..1)
        public float Delay;                          // задержка старта, с (серии: клац коробки, клапан турбины)
    }

    // Биквад (прямая форма I) — для ФНЧ/ФВЧ.
    struct Biquad
    {
        float b0, b1, b2, a1, a2, x1, x2, y1, y2;
        public static Biquad LowPass(double fc, double q, int fs)
        {
            double w = 2 * Math.PI * fc / fs, al = Math.Sin(w) / (2 * q), c = Math.Cos(w), a0 = 1 + al;
            var b = new Biquad();
            b.b0 = (float)((1 - c) / 2 / a0); b.b1 = (float)((1 - c) / a0); b.b2 = b.b0;
            b.a1 = (float)(-2 * c / a0); b.a2 = (float)((1 - al) / a0);
            return b;
        }
        public static Biquad HighPass(double fc, double q, int fs)
        {
            double w = 2 * Math.PI * fc / fs, al = Math.Sin(w) / (2 * q), c = Math.Cos(w), a0 = 1 + al;
            var b = new Biquad();
            b.b0 = (float)((1 + c) / 2 / a0); b.b1 = (float)(-(1 + c) / a0); b.b2 = b.b0;
            b.a1 = (float)(-2 * c / a0); b.a2 = (float)((1 - al) / a0);
            return b;
        }
        // Полосовой (пик 0 дБ); меняет только коэффициенты — состояние фильтра сохраняется.
        public void SetBandPass(double fc, double q, int fs)
        {
            double w = 2 * Math.PI * fc / fs, al = Math.Sin(w) / (2 * q), c = Math.Cos(w), a0 = 1 + al;
            b0 = (float)(al / a0); b1 = 0; b2 = (float)(-al / a0);
            a1 = (float)(-2 * c / a0); a2 = (float)((1 - al) / a0);
        }
        public float Run(float x)
        {
            float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y;
        }
    }

    public class HapticSynth
    {
        public int SampleRate = 48000;
        public volatile HapticParams P = new HapticParams();
        public float Master = 0.8f;
        public volatile float PeakL, PeakR;
        readonly ConcurrentQueue<Impulse> queue = new ConcurrentQueue<Impulse>();

        class Active { public Impulse I; public float T; public double Ph; }
        readonly List<Active> active = new List<Active>();

        // Диагностический тон (для проверки "доходит ли сигнал до моторов")
        public volatile float ToneFreq, ToneAmp;
        double tonePh;

        public int FiredCount;
        public float LastPanL, LastPanR;
        public void Fire(Impulse i)
        {
            if (queue.Count < 32) queue.Enqueue(i);
            Interlocked.Increment(ref FiredCount); LastPanL = i.AmpL; LastPanR = i.AmpR;
        }

        // Потолок выхода. DSX умножает сигнал (haptics_gain = 2) и переводит в 8 бит для Bluetooth —
        // всё выше 0.5 у нас превращается у него в жёсткий клиппинг ("хрип"). Держим запас.
        public float Ceiling = 0.46f;
        float limG = 1f;
        // Компрессия "против лесенки": по Bluetooth DSX шлёт хаптику в 8 битах, тихий сигнал там
        // кодируется десятком уровней и превращается в хрип. Поднимаем тихое (до ×3), громкое не трогаем.
        public float TargetRms = 0.13f, MaxLift = 3f;
        float rmsS, agcG = 1f;
        float pulseGain = 1f;
        int patIdx;

        // сглаженные параметры
        float sEngF = 40, sEngP = 8, sEngL, sEngA, sGate = 1, sRoadL, sRoadR, sCut = 25, sCurbL, sCurbR, sCurbRL = 20, sCurbRR = 20,
              sSlipL, sSlipR, sSplL, sSplR, sAbsA, sAbsF = 16, sSpin, sSpinF = 40;
        // фазы
        double engPh, pulsePh, limPh, curbPhL, curbPhR, absPh, spinPh, scrubPh1, scrubPh2;
        float curbTL = 1, curbTR = 1, absT = 1;
        // фильтры шума (огибающие и "грохот")
        float rdL1, rdL2, rdR1, rdR2, grL, envL, envR, spEnv, splLp;
        // выходная цепочка: ФВЧ 18 Гц + ФНЧ 180 Гц (2 каскада = 4-й порядок)
        Biquad hpL, hpR, lpL1, lpL2, lpR1, lpR2;
        Biquad roadBpL, roadBpR;          // дорога: полосовой шум в ощущаемой зоне
        float roadEnvL, roadEnvR;         // медленная огибающая — отдельные неровности
        float sTurboA, sTurboF = 100, sPanL = 1, sPanR = 1;
        double turboPh, turboLfo;
        int filtRate;
        uint rng = 2463534242;

        float Noise()
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            return (rng / 2147483648f) - 1f;
        }

        void SetupFilters(int fs)
        {
            hpL = Biquad.HighPass(18, 0.707, fs); hpR = Biquad.HighPass(18, 0.707, fs);
            lpL1 = Biquad.LowPass(180, 0.541, fs); lpL2 = Biquad.LowPass(180, 1.307, fs); // Баттерворт 4-го порядка
            lpR1 = Biquad.LowPass(180, 0.541, fs); lpR2 = Biquad.LowPass(180, 1.307, fs);
            filtRate = fs;
        }

        public void Render(float[] outL, float[] outR, int n)
        {
            int fs = SampleRate;
            if (filtRate != fs) SetupFilters(fs);
            var p = P;
            Impulse ni;
            while (queue.TryDequeue(out ni))
            {
                if (active.Count >= 24)
                {
                    int worst = 0; float wv = float.MaxValue;
                    for (int k = 0; k < active.Count; k++)
                    {
                        float lvl = (float)Math.Exp(-active[k].T / active[k].I.Decay) * Math.Max(active[k].I.AmpL, active[k].I.AmpR);
                        if (lvl < wv) { wv = lvl; worst = k; }
                    }
                    active.RemoveAt(worst);
                }
                active.Add(new Active { I = ni, T = -Math.Max(0f, ni.Delay) });
            }

            float dt = 1f / fs;
            float kA = 1f - (float)Math.Exp(-1.0 / (fs * 0.030)); // амплитуды ~30 мс
            float kF = 1f - (float)Math.Exp(-1.0 / (fs * 0.050)); // частоты ~50 мс (без "плавания")
            float master = Master;
            float limAtk = 1f - (float)Math.Exp(-1.0 / (fs * 0.0008)); // ограничитель: атака ~1 мс
            float limRel = 1f - (float)Math.Exp(-1.0 / (fs * 0.150));  // отпускание ~150 мс
            float kRms = 1f - (float)Math.Exp(-1.0 / (fs * 0.300));    // уровень за ~300 мс
            float kAgc = 1f - (float)Math.Exp(-1.0 / (fs * 0.250));    // усиление меняется плавно, ~250 мс
            float pkL = 0, pkR = 0;
            const double TAU = Math.PI * 2;
            // центр полосы дороги — раз на блок (плавно, состояние фильтра не сбрасывается)
            sCut += (Clamp(p.RoadCut, 55f, 140f) - sCut) * 0.3f;
            roadBpL.SetBandPass(sCut, 0.9, fs); roadBpR.SetBandPass(sCut, 0.9, fs);
            float roadNorm = 0.55f / (0.577f * (float)Math.Sqrt(1.571 * sCut / 0.9 / (fs * 0.5)));

            for (int i = 0; i < n; i++)
            {
                sEngF += (Clamp(p.EngFreq, 30f, 120f) - sEngF) * kF; sEngA += (p.EngAmp - sEngA) * kA;
                sEngP += (Clamp(p.EngPulse, 4f, 32f) - sEngP) * kF; sEngL += (p.EngLoad - sEngL) * kA;
                sRoadL += (p.RoadL - sRoadL) * kA; sRoadR += (p.RoadR - sRoadR) * kA;
                sCurbL += (p.CurbL - sCurbL) * kA; sCurbR += (p.CurbR - sCurbR) * kA;
                sCurbRL += (Clamp(p.CurbRateL, 4f, 30f) - sCurbRL) * kF; sCurbRR += (Clamp(p.CurbRateR, 4f, 30f) - sCurbRR) * kF;
                sSlipL += (p.SlipL - sSlipL) * kA; sSlipR += (p.SlipR - sSlipR) * kA;
                sSplL += (p.SplashL - sSplL) * kA; sSplR += (p.SplashR - sSplR) * kA;
                sAbsA += (p.AbsAmp - sAbsA) * kA; sAbsF += (Clamp(p.AbsFreq, 4f, 30f) - sAbsF) * kF;
                sSpin += (p.SpinAmp - sSpin) * kA; sSpinF += (Clamp(p.SpinFreq, 30f, 90f) - sSpinF) * kF;

                // --- мотор: несущая в ощущаемой полосе, промодулированная ритмом вспышек.
                // На низах пульс глубокий (мотор "бубнит"), к отсечке ритм ускоряется и сливается в ровный гул.
                // Каждый такт чуть отличается по силе — так мотор ощущается живым, а не как генератор.
                engPh += sEngF / fs; if (engPh >= 1) engPh -= 1;
                pulsePh += sEngP / fs;
                if (pulsePh >= 1)
                {
                    pulsePh -= 1;
                    var pat = p.EngPattern;
                    float pw = pat != null && pat.Length > 0 ? pat[patIdx++ % pat.Length] : 1f;   // рисунок тактов мотора
                    pulseGain = pw * (1f + p.EngRough * Noise());
                }
                float ps = (float)Math.Sin(TAU * pulsePh);
                float pulse = ps > 0 ? ps * ps : 0f;                           // острый "толчок" на такт
                float depth = Math.Min(0.95f, (1f - Clamp((sEngP - 14f) / 18f, 0f, 1f)) * p.EngDepth * (0.75f + 0.45f * sEngL));
                float envE = (1f - depth) + depth * pulse * 2.1f * pulseGain;   // среднее ≈ 1
                float eng = (float)Math.Sin(TAU * engPh) * envE;
                float gateT = 1f;
                if (p.LimOn) { limPh += 11.0 / fs; if (limPh >= 1) limPh -= 1; gateT = limPh < 0.5 ? 1f : 0.15f; }
                sGate += (gateT - sGate) * 0.004f; // мягкая огибающая отсечки, без щелчков
                eng *= sEngA * sGate;
                // приоритет мотора: остальные "постоянные" слои слегка уступают, когда мотор под нагрузкой
                float duck = 1f - 0.45f * Clamp(sEngA * 4f, 0f, 1f) * (0.4f + 0.6f * sEngL);

                // --- дорога: полосовой шум 60–140 Гц (там моторы реально толкают) × медленная огибающая —
                // ощущается как отдельные неровности покрытия, а не ровный гул
                roadEnvL += (Math.Abs(Noise()) - roadEnvL) * 0.0018f;  // ~14 Гц
                roadEnvR += (Math.Abs(Noise()) - roadEnvR) * 0.0018f;
                float roadL = roadBpL.Run(Noise()) * roadNorm * (0.35f + 1.3f * roadEnvL) * sRoadL;
                float roadR = roadBpR.Run(Noise()) * roadNorm * (0.35f + 1.3f * roadEnvR) * sRoadR;

                // --- поребрики: на каждую "гребёнку" — плотный тум 70 Гц (~30 мс)
                curbPhL += sCurbRL / fs; if (curbPhL >= 1) { curbPhL -= 1; curbTL = 0; }
                curbPhR += sCurbRR / fs; if (curbPhR >= 1) { curbPhR -= 1; curbTR = 0; }
                float curbL = (float)(Math.Sin(TAU * 70 * curbTL) * Math.Exp(-curbTL / 0.030)) * sCurbL;
                float curbR = (float)(Math.Sin(TAU * 70 * curbTR) * Math.Exp(-curbTR / 0.030)) * sCurbR;
                curbTL += dt; curbTR += dt;

                // --- скольжение шин: два синуса (70 и 97 Гц), модулированные медленным шумом → "зерно" без шипения
                float cg = 0.0025f; // огибающая ~19 Гц
                envL += (Math.Abs(Noise()) - envL) * cg; envR += (Math.Abs(Noise()) - envR) * cg;
                scrubPh1 += 70.0 / fs; if (scrubPh1 >= 1) scrubPh1 -= 1;
                scrubPh2 += 97.0 / fs; if (scrubPh2 >= 1) scrubPh2 -= 1;
                float scrub = (float)(Math.Sin(TAU * scrubPh1) * 0.6 + Math.Sin(TAU * scrubPh2) * 0.4);
                float slipL = scrub * (0.35f + 1.3f * envL) * sSlipL;
                float slipR = scrub * (0.35f + 1.3f * envR) * sSlipR;

                // --- лужи: мягкие низкие всплески
                splLp += (Noise() - splLp) * 0.004f;
                float splL = splLp * 6f * sSplL, splR = splLp * 6f * sSplR;

                // --- ABS: толчок на каждый цикл клапанов (50 Гц, ~18 мс)
                absPh += sAbsF / fs; if (absPh >= 1) { absPh -= 1; absT = 0; }
                float absV = (float)(Math.Sin(TAU * 50 * absT) * Math.Exp(-absT / 0.018)) * sAbsA;
                absT += dt;

                // --- пробуксовка: синус 30–90 Гц с рваной огибающей
                spEnv += (Math.Abs(Noise()) - spEnv) * 0.004f;
                spinPh += sSpinF / fs; if (spinPh >= 1) spinPh -= 1;
                float spin = (float)Math.Sin(TAU * spinPh) * (0.4f + 1.2f * spEnv) * sSpin;

                // --- турбина: гладкий тон, растёт с наддувом, с лёгким "дыханием" крыльчатки
                sTurboA += (p.TurboAmp - sTurboA) * kA; sTurboF += (Clamp(p.TurboFreq, 70f, 170f) - sTurboF) * kF;
                turboPh += sTurboF / fs; if (turboPh >= 1) turboPh -= 1;
                turboLfo += 7.0 / fs; if (turboLfo >= 1) turboLfo -= 1;
                float turbo = (float)(Math.Sin(TAU * turboPh) * (0.85 + 0.15 * Math.Sin(TAU * turboLfo))) * sTurboA;

                sPanL += (p.EngPanL - sPanL) * kA; sPanR += (p.EngPanR - sPanR) * kA;
                float L = eng * sPanL + turbo + (roadL + slipL + splL) * duck + curbL + absV + spin;
                float R = eng * sPanR + turbo + (roadR + slipR + splR) * duck + curbR + absV + spin;

                // --- удары: синус с опусканием тона (×1.8 → ×1) + немного низкого "грохота"
                for (int k = active.Count - 1; k >= 0; k--)
                {
                    var a = active[k];
                    if (a.T < 0) { a.T += dt; continue; } // ещё не время
                    // атака 3 мс вместо мгновенной: без щелчка на фронте
                    float env = (float)Math.Exp(-a.T / a.I.Decay) * Math.Min(1f, a.T / 0.003f);
                    float f = a.I.Freq * (1f + 0.8f * (float)Math.Exp(-a.T / 0.018));
                    a.Ph += f / fs; if (a.Ph >= 1) a.Ph -= 1;
                    float v = (float)Math.Sin(TAU * a.Ph) * env;
                    if (a.I.Noise > 0)
                    {
                        grL += (Noise() - grL) * 0.006f; // ~45 Гц
                        v += grL * 5f * a.I.Noise * env;
                    }
                    L += v * a.I.AmpL; R += v * a.I.AmpR;
                    a.T += dt;
                    if (a.T > a.I.Decay * 4.5f) active.RemoveAt(k);
                }

                if (ToneAmp > 0)
                {
                    tonePh += ToneFreq / fs; if (tonePh >= 1) tonePh -= 1;
                    float tone = (float)Math.Sin(TAU * tonePh) * ToneAmp;
                    L += tone; R += tone;
                }

                // --- выходная цепочка: фильтры → ограничитель громкости (плавно меняет усиление,
                // форму волны не искажает — значит, никаких новых гармоник и "хрипа") → страховочный потолок
                L = lpL2.Run(lpL1.Run(hpL.Run(L))) * master;
                R = lpR2.Run(lpR1.Run(hpR.Run(R))) * master;
                // A single NaN/Inf (e.g. a garbage telemetry value) would latch inside the IIR filter state
                // forever and silence the motors until restart — reset the whole output chain instead.
                if (float.IsNaN(L) || float.IsInfinity(L) || float.IsNaN(R) || float.IsInfinity(R))
                {
                    SetupFilters(fs); rmsS = 0; agcG = 1f; limG = 1f; active.Clear();
                    sEngA = sSlipL = sSlipR = sAbsA = sSpin = sTurboA = 0; sPanL = sPanR = 1;
                    L = R = 0;
                }
                rmsS += ((L * L + R * R) * 0.5f - rmsS) * kRms;
                float rms = (float)Math.Sqrt(rmsS);
                float gT = rms > 0.004f ? Clamp((float)Math.Sqrt(TargetRms / rms), 1f, MaxLift) : agcG; // 2:1, тишину не "раздуваем"
                agcG += (gT - agcG) * kAgc;
                L *= agcG; R *= agcG;
                float pkNow = Math.Max(Math.Abs(L), Math.Abs(R));
                float ceil = Ceiling;
                float target = pkNow * limG > ceil ? ceil / Math.Max(1e-6f, pkNow) : 1f;
                if (target < limG) limG += (target - limG) * limAtk; else limG += (target - limG) * limRel;
                L *= limG; R *= limG;
                L = Clamp(L, -ceil, ceil); R = Clamp(R, -ceil, ceil); // ×2 в DSX → максимум 0.92, без клиппинга
                outL[i] = L; outR[i] = R;
                if (L > pkL) pkL = L; else if (-L > pkL) pkL = -L;
                if (R > pkR) pkR = R; else if (-R > pkR) pkR = -R;
            }
            PeakL = Math.Max(pkL, PeakL * 0.85f);
            PeakR = Math.Max(pkR, PeakR * 0.85f);
        }

        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

        static float SoftClip(float x)
        {
            if (x > 3f) return 0.98f; if (x < -3f) return -0.98f;
            float x2 = x * x;
            return 0.98f * x * (27f + x2) / (27f + 9f * x2); // аппроксимация tanh
        }
    }
}
