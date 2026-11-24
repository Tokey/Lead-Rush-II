using System;
using System.Collections.Generic;
using UnityEngine;

namespace LR2.UI
{
    /// <summary>
    /// Procedural UI icons (user 11-19: lobby "should be larger with more icon"). Each icon is a signed-distance shape drawn
    /// once into a 96 px white-on-transparent texture and cached; tint it with Image.color. No image assets needed.
    /// </summary>
    public static class LR2Icons
    {
        private const int N = 96;
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        public static Sprite Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (Cache.TryGetValue(name, out var sp) && sp != null) return sp;
            Func<Vector2, float> f = Shape(name);
            if (f == null) return null;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { name = "Icon_" + name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[N * N];
            float pix = 2f / N;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    var p = new Vector2((x + 0.5f) / N * 2f - 1f, (y + 0.5f) / N * 2f - 1f);
                    float a = Mathf.Clamp01(0.5f - f(p) / pix);
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            sp = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            sp.name = "Icon_" + name;
            Cache[name] = sp;
            return sp;
        }

        // ------------------------------------------------------------------ distance helpers (p in [-1, 1], y up)
        private static float Circle(Vector2 p, float cx, float cy, float r) => (p - new Vector2(cx, cy)).magnitude - r;
        private static float Ring(Vector2 p, float cx, float cy, float r, float w) => Mathf.Abs((p - new Vector2(cx, cy)).magnitude - r) - w;

        private static float Box(Vector2 p, float cx, float cy, float hx, float hy, float round = 0f)
        {
            Vector2 d = new Vector2(Mathf.Abs(p.x - cx) - hx + round, Mathf.Abs(p.y - cy) - hy + round);
            return new Vector2(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f)).magnitude + Mathf.Min(Mathf.Max(d.x, d.y), 0f) - round;
        }

        private static float Seg(Vector2 p, float ax, float ay, float bx, float by, float r)
        {
            Vector2 a = new Vector2(ax, ay), ba = new Vector2(bx, by) - a, pa = p - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
            return (pa - ba * h).magnitude - r;
        }

        /// <summary>Convex polygon (counter-clockwise points).</summary>
        private static float Poly(Vector2 p, params Vector2[] v)
        {
            float d = float.MinValue;
            for (int i = 0; i < v.Length; i++)
            {
                Vector2 a = v[i], b = v[(i + 1) % v.Length];
                Vector2 e = (b - a).normalized;
                Vector2 n = new Vector2(e.y, -e.x);
                d = Mathf.Max(d, Vector2.Dot(p - a, n));
            }
            return d;
        }

        private static float U(params float[] d) { float m = d[0]; for (int i = 1; i < d.Length; i++) m = Mathf.Min(m, d[i]); return m; }
        private static float Cut(float a, float b) => Mathf.Max(a, -b);
        private static Vector2 V(float x, float y) => new Vector2(x, y);

        private static float Star(Vector2 p, int points, float rOut, float rIn)
        {
            float ang = Mathf.Atan2(p.y, p.x) + Mathf.PI / 2f;
            float seg = Mathf.PI * 2f / points;
            float a = Mathf.Repeat(ang, seg) / seg; // 0..1 across one point
            float r = Mathf.Lerp(rIn, rOut, 1f - Mathf.Abs(a - 0.5f) * 2f);
            return (p.magnitude - r) * 0.8f;
        }

        private static float Chevrons(Vector2 p, int n)
        {
            float d = float.MaxValue;
            float step = 0.42f, top = (n - 1) * step * 0.5f;
            for (int i = 0; i < n; i++)
            {
                float y = top - i * step;
                d = Mathf.Min(d, U(Seg(p, -0.62f, y - 0.05f, 0f, y + 0.25f, 0.1f), Seg(p, 0f, y + 0.25f, 0.62f, y - 0.05f, 0.1f)));
            }
            return d;
        }

        private static float Person(Vector2 p, float cx, float s)
        {
            Vector2 q = (p - new Vector2(cx, 0f)) / s;
            float head = Circle(q, 0f, 0.42f, 0.27f);
            float body = Cut(Box(q, 0f, -0.42f, 0.6f, 0.36f, 0.32f), Box(q, 0f, -0.95f, 1f, 0.17f));
            return U(head, body) * s;
        }

        private static float Arrow(Vector2 p, float ax, float ay, float bx, float by)
        {
            Vector2 a = V(ax, ay), b = V(bx, by), dir = (b - a).normalized, n = new Vector2(-dir.y, dir.x);
            float shaft = Seg(p, ax, ay, bx - dir.x * 0.25f, by - dir.y * 0.25f, 0.08f);
            Vector2 t0 = b, t1 = b - dir * 0.4f + n * 0.3f, t2 = b - dir * 0.4f - n * 0.3f;
            Vector2 e1 = t1 - t0, e2 = t2 - t0;
            float head = e1.x * e2.y - e1.y * e2.x > 0f ? Poly(p, t0, t1, t2) : Poly(p, t0, t2, t1); // counter-clockwise only
            return Mathf.Min(shaft, head);
        }

        // ------------------------------------------------------------------ the set
        private static Func<Vector2, float> Shape(string name)
        {
            switch (name)
            {
                case "player": return p => Person(p, 0f, 1f);
                case "bot": return p => U(Cut(Box(p, 0f, -0.12f, 0.62f, 0.5f, 0.18f), U(Circle(p, -0.25f, -0.06f, 0.13f), Circle(p, 0.25f, -0.06f, 0.13f), Box(p, 0f, -0.36f, 0.24f, 0.05f))),
                                         Seg(p, 0f, 0.38f, 0f, 0.66f, 0.06f), Circle(p, 0f, 0.74f, 0.11f), Box(p, -0.72f, -0.12f, 0.08f, 0.2f, 0.04f), Box(p, 0.72f, -0.12f, 0.08f, 0.2f, 0.04f));
                case "spectate": return p => U(Cut(Mathf.Max(Circle(p, 0f, -0.62f, 1.05f), Circle(p, 0f, 0.62f, 1.05f)), Circle(p, 0f, 0f, 0.36f)), Circle(p, 0f, 0f, 0.2f));
                case "plus": return p => U(Seg(p, -0.6f, 0f, 0.6f, 0f, 0.12f), Seg(p, 0f, -0.6f, 0f, 0.6f, 0.12f));
                case "trash": return p => U(Cut(Box(p, 0f, -0.2f, 0.46f, 0.56f, 0.06f), U(Seg(p, -0.18f, -0.5f, -0.18f, 0.12f, 0.05f), Seg(p, 0.18f, -0.5f, 0.18f, 0.12f, 0.05f))),
                                           Seg(p, -0.64f, 0.5f, 0.64f, 0.5f, 0.07f), Box(p, 0f, 0.66f, 0.2f, 0.08f, 0.04f));
                case "balance": return p => U(Arrow(p, -0.65f, 0.3f, 0.7f, 0.3f), Arrow(p, 0.65f, -0.3f, -0.7f, -0.3f));
                case "gear":
                    return p =>
                    {
                        float a = Mathf.Atan2(p.y, p.x);
                        float teeth = Mathf.Clamp(Mathf.Cos(a * 8f) * 3f, -1f, 1f) * 0.11f;
                        return Cut(p.magnitude - (0.6f + teeth), Circle(p, 0f, 0f, 0.24f)) * 0.9f;
                    };
                case "rules": return p => U(Circle(p, -0.6f, 0.45f, 0.1f), Circle(p, -0.6f, 0f, 0.1f), Circle(p, -0.6f, -0.45f, 0.1f),
                                           Seg(p, -0.3f, 0.45f, 0.65f, 0.45f, 0.08f), Seg(p, -0.3f, 0f, 0.65f, 0f, 0.08f), Seg(p, -0.3f, -0.45f, 0.4f, -0.45f, 0.08f));
                case "back": return p => Arrow(p, 0.65f, 0f, -0.7f, 0f);
                case "play": return p => Poly(p, V(-0.45f, -0.62f), V(0.68f, 0f), V(-0.45f, 0.62f));
                // B130: stepper arrows as icons (the "<" / ">" text glyphs did not render in the lobby's heading font)
                case "chev-left": return p => U(Seg(p, 0.2f, 0.58f, -0.3f, 0f, 0.12f), Seg(p, -0.3f, 0f, 0.2f, -0.58f, 0.12f));
                case "chev-right": return p => U(Seg(p, -0.2f, 0.58f, 0.3f, 0f, 0.12f), Seg(p, 0.3f, 0f, -0.2f, -0.58f, 0.12f));
                case "power": return p => U(Cut(Ring(p, 0f, -0.1f, 0.6f, 0.085f), Poly(p, V(-0.32f, 0.95f), V(0f, -0.1f), V(0.32f, 0.95f))),
                                            Seg(p, 0f, 0f, 0f, 0.82f, 0.085f));
                case "map": return p => Cut(U(Circle(p, 0f, 0.22f, 0.44f), Poly(p, V(-0.38f, 0.02f), V(0f, -0.82f), V(0.38f, 0.02f))), Circle(p, 0f, 0.22f, 0.17f));
                case "mode": return p => U(Ring(p, 0f, 0f, 0.55f, 0.07f), Seg(p, 0f, 0.3f, 0f, 0.82f, 0.07f), Seg(p, 0f, -0.3f, 0f, -0.82f, 0.07f),
                                          Seg(p, 0.3f, 0f, 0.82f, 0f, 0.07f), Seg(p, -0.3f, 0f, -0.82f, 0f, 0.07f), Circle(p, 0f, 0f, 0.08f));
                case "score": return p => Star(p, 5, 0.82f, 0.36f);
                case "clock": return p => U(Ring(p, 0f, 0f, 0.7f, 0.08f), Seg(p, 0f, 0f, 0f, 0.45f, 0.08f), Seg(p, 0f, 0f, 0.32f, -0.12f, 0.08f));
                case "respawn":
                    return p =>
                    {
                        float a = Mathf.Atan2(p.y, p.x) * Mathf.Rad2Deg;
                        float arc = Ring(p, 0f, 0f, 0.58f, 0.09f);
                        if (a > 45f && a < 100f) arc = 1f; // the gap where the arrow sits
                        float head = Poly(p, V(0.22f, 0.58f), V(0.62f, 0.2f), V(0.62f, 0.95f));
                        return Mathf.Min(arc, head);
                    };
                case "drone": return p => U(Circle(p, 0f, 0f, 0.26f), Seg(p, -0.62f, 0.42f, 0.62f, -0.42f, 0.07f), Seg(p, -0.62f, -0.42f, 0.62f, 0.42f, 0.07f),
                                           Ring(p, -0.62f, 0.42f, 0.2f, 0.06f), Ring(p, 0.62f, 0.42f, 0.2f, 0.06f), Ring(p, -0.62f, -0.42f, 0.2f, 0.06f), Ring(p, 0.62f, -0.42f, 0.2f, 0.06f));
                case "gun": return p => U(Box(p, 0.05f, 0.12f, 0.5f, 0.13f, 0.03f), Seg(p, 0.5f, 0.17f, 0.95f, 0.17f, 0.04f), Poly(p, V(-0.06f, -0.5f), V(0.1f, -0.5f), V(0.18f, 0f), V(0.02f, 0f)),
                                         Poly(p, V(-0.42f, -0.4f), V(-0.26f, -0.4f), V(-0.16f, 0f), V(-0.32f, 0f)), Poly(p, V(-0.95f, 0.2f), V(-0.95f, -0.12f), V(-0.45f, -0.02f), V(-0.45f, 0.24f)));
                case "tdm": return p => U(Person(p, -0.38f, 0.62f), Person(p, 0.38f, 0.62f));
                case "ffa": return p => Star(p, 8, 0.85f, 0.42f);
                case "ctf": return p => U(Seg(p, -0.5f, -0.85f, -0.5f, 0.82f, 0.07f), Poly(p, V(-0.45f, 0.8f), V(-0.45f, 0.08f), V(0.72f, 0.44f)));
                case "duel": return p => U(Seg(p, -0.62f, -0.62f, 0.62f, 0.62f, 0.08f), Seg(p, 0.62f, -0.62f, -0.62f, 0.62f, 0.08f),
                                          Seg(p, -0.62f, -0.3f, -0.3f, -0.62f, 0.08f), Seg(p, 0.62f, -0.3f, 0.3f, -0.62f, 0.08f));
                case "shield": return p => Mathf.Min(Box(p, 0f, 0.28f, 0.62f, 0.5f, 0.12f), Poly(p, V(-0.62f, 0.1f), V(0f, -0.9f), V(0.62f, 0.1f)));
                case "teams2": return p => U(Box(p, -0.42f, 0f, 0.3f, 0.6f, 0.1f), Box(p, 0.42f, 0f, 0.3f, 0.6f, 0.1f));
                case "teams3": return p => U(Circle(p, 0f, 0.48f, 0.3f), Circle(p, -0.46f, -0.32f, 0.3f), Circle(p, 0.46f, -0.32f, 0.3f));
                case "teams4": return p => U(Box(p, -0.38f, 0.38f, 0.26f, 0.26f, 0.07f), Box(p, 0.38f, 0.38f, 0.26f, 0.26f, 0.07f), Box(p, -0.38f, -0.38f, 0.26f, 0.26f, 0.07f), Box(p, 0.38f, -0.38f, 0.26f, 0.26f, 0.07f));
                case "rank1": return p => Chevrons(p, 1);
                case "rank2": return p => Chevrons(p, 2);
                case "rank3": return p => Chevrons(p, 3);
                case "rank4": return p => Chevrons(p, 4);
                case "adaptive":
                    return p =>
                    {
                        float d = Ring(p, 0f, 0f, 0.78f, 0.07f);
                        for (int i = 0; i < 12; i++)
                        {
                            float x0 = -0.5f + i / 12f, x1 = -0.5f + (i + 1) / 12f;
                            d = Mathf.Min(d, Seg(p, x0, 0.28f * Mathf.Sin(x0 * 2f * Mathf.PI * 1.1f), x1, 0.28f * Mathf.Sin(x1 * 2f * Mathf.PI * 1.1f), 0.08f));
                        }
                        return d;
                    };
                case "flask":
                    return p => U(
                        Poly(p, V(-0.56f, -0.78f), V(0.56f, -0.78f), V(0.18f, 0.05f), V(-0.18f, 0.05f)),
                        Box(p, 0f, 0.36f, 0.16f, 0.36f, 0.04f),
                        Seg(p, -0.26f, 0.72f, 0.26f, 0.72f, 0.07f));
                case "robot":
                    return p => U(
                        Cut(Box(p, 0f, -0.08f, 0.6f, 0.48f, 0.04f), U(Box(p, -0.24f, 0.02f, 0.14f, 0.08f), Box(p, 0.24f, 0.02f, 0.14f, 0.08f), Box(p, 0f, -0.3f, 0.3f, 0.06f))),
                        Seg(p, 0f, 0.4f, 0f, 0.76f, 0.07f), Circle(p, 0f, 0.8f, 0.1f));
                case "brain":
                    return p => U(Circle(p, -0.28f, 0.12f, 0.34f), Circle(p, 0.28f, 0.12f, 0.34f), Circle(p, -0.26f, -0.24f, 0.28f), Circle(p, 0.26f, -0.24f, 0.28f), Seg(p, 0f, -0.45f, 0f, 0.42f, 0.05f));
                case "copy":
                    return p => U(Cut(Box(p, -0.18f, 0.18f, 0.42f, 0.48f, 0.05f), Box(p, -0.18f, 0.18f, 0.28f, 0.34f, 0.02f)),
                                  Cut(Box(p, 0.18f, -0.18f, 0.42f, 0.48f, 0.05f), Box(p, 0.18f, -0.18f, 0.28f, 0.34f, 0.02f)));
                default: return null;
            }
        }
    }
}
