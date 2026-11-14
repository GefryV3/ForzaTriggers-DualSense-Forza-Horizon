// Встроенные характеры моторов для хаптики. FH6 не сообщает модель мотора, но сообщает число цилиндров
// и максимальные обороты — по ним узнаётся тип: американский V8 бурчит неровно, V8 с плоским валом визжит,
// V12 гладкий, рядная "четвёрка" — грубая на низах и т.д.
//
// Pattern — относительная сила каждой вспышки по кругу (повторяется). Неровный рисунок = узнаваемое "бурчание".
// Depth   — глубина пульса на низах (0 — ровный гул, 1 — отдельные удары).
// Rough   — случайное отличие тактов друг от друга (живость).
// FreqOffset/FreqSpan — несущая частота: 34 + offset + span × (обороты/макс)^0.9 Гц.

namespace ForzaTriggers
{
    public class EngineCharacter
    {
        public string NameRu, NameEn;
        public string Name { get { return Loc.T(NameRu, NameEn); } }
        public float[] Pattern;
        public float Depth, Rough, FreqOffset, FreqSpan, AmpMul = 1f;
    }

    public static class EngineCharacters
    {
        static EngineCharacter C(string n, string en, float[] p, float depth, float rough, float off, float span, float amp)
        { return new EngineCharacter { NameRu = n, NameEn = en, Pattern = p, Depth = depth, Rough = rough, FreqOffset = off, FreqSpan = span, AmpMul = amp }; }

        static readonly EngineCharacter Electric   = C("Электро — ровный гул", "Electric — smooth hum", new[] { 1f }, 0f, 0f, 14f, 70f, 0.7f);
        static readonly EngineCharacter Rotary     = C("Роторный — гладкий визг", "Rotary — smooth scream", new[] { 1f, 0.95f }, 0.35f, 0.03f, 8f, 85f, 0.95f);
        static readonly EngineCharacter Twin       = C("Двухцилиндровый — тяжёлые удары", "Twin — heavy thumps", new[] { 1f, 0.3f }, 0.9f, 0.15f, -8f, 55f, 1.05f);
        static readonly EngineCharacter Three      = C("3 цилиндра — хромает", "Inline-3 — limping", new[] { 1f, 0.7f, 0.85f }, 0.8f, 0.15f, -6f, 60f, 1f);
        static readonly EngineCharacter I4         = C("Рядная 4 — грубая на низах", "Inline-4 — rough down low", new[] { 1f, 0.75f, 0.95f, 0.7f }, 0.75f, 0.14f, -4f, 65f, 1f);
        static readonly EngineCharacter I4High     = C("Рядная 4 высокооборотная — звенит", "High-rev I4 — ringing", new[] { 1f, 0.85f, 1f, 0.85f }, 0.55f, 0.06f, 6f, 80f, 0.95f);
        static readonly EngineCharacter Five       = C("5 цилиндров — воет с перекатом", "Inline-5 — warbling howl", new[] { 1f, 0.65f, 0.9f, 0.8f, 0.6f }, 0.7f, 0.08f, 0f, 70f, 1f);
        static readonly EngineCharacter Six        = C("6 цилиндров — ровный", "Six — smooth", new[] { 1f, 0.85f, 0.95f, 0.85f, 1f, 0.8f }, 0.55f, 0.08f, 0f, 70f, 1f);
        static readonly EngineCharacter SixHigh    = C("6 цилиндров высокооборотный — поёт", "High-rev six — singing", new[] { 1f, 0.92f, 1f, 0.92f, 1f, 0.92f }, 0.45f, 0.04f, 6f, 80f, 0.95f);
        static readonly EngineCharacter V8Cross    = C("V8 американский — бурчит", "Cross-plane V8 — burbling", new[] { 1f, 0.45f, 0.95f, 0.7f, 1f, 0.5f, 0.85f, 0.75f }, 0.9f, 0.14f, -8f, 62f, 1.1f);
        static readonly EngineCharacter V8Flat     = C("V8 плоский вал — визжит", "Flat-plane V8 — shrieking", new[] { 1f, 0.85f, 1f, 0.85f }, 0.45f, 0.05f, 8f, 85f, 1f);
        static readonly EngineCharacter V10        = C("V10 — плотный вой", "V10 — dense wail", new[] { 1f, 0.9f, 0.95f, 0.86f, 1f, 0.9f, 0.95f, 0.88f, 1f, 0.92f }, 0.4f, 0.04f, 10f, 85f, 1f);
        static readonly EngineCharacter V12        = C("V12 / W16 — шёлк", "V12 / W16 — silk", new[] { 1f, 0.97f }, 0.22f, 0.02f, 12f, 85f, 0.9f);

        public static EngineCharacter For(int cylinders, float maxRpm)
        {
            if (cylinders <= 0) return Electric;
            if (cylinders <= 2) return maxRpm >= 8500 ? Rotary : Twin;
            if (cylinders == 3) return Three;
            if (cylinders == 4) return maxRpm >= 8000 ? I4High : I4;
            if (cylinders == 5) return Five;
            if (cylinders == 6 || cylinders == 7) return maxRpm >= 8000 ? SixHigh : Six;
            if (cylinders == 8 || cylinders == 9) return maxRpm < 7600 ? V8Cross : V8Flat;
            if (cylinders <= 11) return V10;
            return V12;
        }
    }
}
