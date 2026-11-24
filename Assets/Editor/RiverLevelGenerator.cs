#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>
/// Generator level design sungai (batu hook, rintangan, sampah) sepanjang spline RiverPath.
///
/// Aturan:
///  - Batu yang prefab-nya ada kata "hook" = bisa di-hook. Batu tanpa "hook" = rintangan statis.
///  - Sampah 1-3 = bisa di-hook beberapa kali, lalu lepas dan hanyut mengikuti arus (TrashObstacle).
///  - Sampah AH 1-3 = anti hook, statis seperti batu.
///  - Jarak diukur dari titik awal kapal ke arah hulu (kapal ditarik melawan arus).
///  - Makin jauh dari kapal: hook makin jarang (jarak tarik makin dekat ke batas lasso),
///    lorong makin sempit, rintangan makin rapat, sampah penyumbat makin sering.
///  - Rute tarikan (hook ke hook) divalidasi supaya kapal (lingkaran radius BoatRadius) muat lewat.
///
/// Menu: Tools > Sungai > Generate Level Design / Hapus Level Design
/// </summary>
public static class RiverLevelGenerator
{
    public const string RootName = "LevelDesign_Sungai";
    public const string OldGroupName = "LevelDesign_Lama_Nonaktif";
    public const int DefaultSeed = 20261124;
    const string PrefabDir = "Assets/Prefabs/";

    // ---------------- ukuran & batas gameplay ----------------
    const float BoatRadius = 4.6f;      // kapal ~5.8 x 9.1 m, dianggap lingkaran (konservatif)
    const float LassoReach = 24f;       // lassoRange = 25, sisakan 1 m
    const float StopGap = 0.25f;        // kapal berhenti ~0.22 m dari permukaan batu target
    const float LutStep = 2f;
    const float WaterThreshold = 4.5f;  // tinggi terrain di bawah ini = air (air di y = 5)
    const float MaxBankLat = 15.5f;
    const float HarborLength = 40f;     // area awal kapal bebas rintangan
    const float EndPoolLength = 25f;    // kolam bebas di ujung sungai (mata air)

    static readonly float[] ZoneEdges = { 0f, 0.18f, 0.38f, 0.58f, 0.80f, 1.01f };
    static readonly string[] ZoneNames =
    {
        "Zona1_Pemanasan", "Zona2_Slalom", "Zona3_Tumpukan_Sampah", "Zona4_Lorong_Sempit", "Zona5_Menuju_Mata_Air"
    };

    // ---------------- kurva kesulitan (p = 0 di kapal, 1 di ujung sungai) ----------------
    static float Lerp(float a, float b, float t) { return a + (b - a) * Mathf.Clamp01(t); }
    static float PullDistance(float p) { return Lerp(16f, 23.3f, Mathf.Pow(p, 0.9f)); }   // jarak lempar lasso ke hook berikutnya
    static float LaneHalf(float p) { return Lerp(9f, 6f, Mathf.Pow(p, 0.8f)); }           // setengah lebar lorong
    static float Clearance(float p) { return Lerp(3f, 0.4f, Mathf.Pow(p, 0.9f)); }        // ruang lebih di luar badan kapal
    static float WallDensity(float p) { return Lerp(1.2f, 9f, Mathf.Pow(p, 1.1f)); }      // rintangan per 100 m
    static float TrashDensity(float p) { return p < 0.12f ? 0f : Lerp(0.4f, 1.8f, (p - 0.12f) / 0.88f); }
    static float DamSpacing(float p) { return Lerp(400f, 170f, (p - 0.38f) / 0.62f); }    // jarak antar tumpukan sampah

    static int ZoneOf(float p)
    {
        for (int i = 0; i < ZoneNames.Length; i++) if (p < ZoneEdges[i + 1]) return i;
        return ZoneNames.Length - 1;
    }

    // urutan: Batu, BatuDatar, BatuPohon, Sampah AH 1, AH 2, AH 3
    static readonly float[][] WallWeights =
    {
        new[] { 0.45f, 0.45f, 0.10f, 0.00f, 0.00f, 0.00f },
        new[] { 0.35f, 0.30f, 0.20f, 0.10f, 0.05f, 0.00f },
        new[] { 0.25f, 0.20f, 0.20f, 0.15f, 0.12f, 0.08f },
        new[] { 0.18f, 0.12f, 0.18f, 0.18f, 0.18f, 0.16f },
        new[] { 0.15f, 0.08f, 0.15f, 0.18f, 0.20f, 0.24f },
    };

    static float[] TrashWeights(float p)
    {
        if (p < 0.4f) return new[] { 0.55f, 0.45f, 0f };
        if (p < 0.7f) return new[] { 0.35f, 0.35f, 0.30f };
        return new[] { 0.20f, 0.30f, 0.50f };
    }

    // ---------------- tipe data ----------------
    class Frame { public Vector3 c; public Vector2 up, rt; public float wl, wr; }
    class Proto { public string name; public GameObject prefab; public float r; }
    class Item
    {
        public Proto proto; public Vector2 pos; public float r, scale, yaw, d;
        public int zone; public string kind; public int stopIndex;
    }

    static List<Frame> lut;
    static Terrain terr;

    // ---------------- util geometri ----------------
    static Frame FrameAt(float d)
    {
        float f = Mathf.Clamp(d / LutStep, 0f, lut.Count - 1.001f);
        int i = (int)f; float t = f - i;
        Frame a = lut[i], b = lut[i + 1];
        Vector2 u = Vector2.Lerp(a.up, b.up, t).normalized;
        return new Frame
        {
            c = Vector3.Lerp(a.c, b.c, t), up = u, rt = new Vector2(u.y, -u.x),
            wl = Mathf.Lerp(a.wl, b.wl, t), wr = Mathf.Lerp(a.wr, b.wr, t)
        };
    }

    static Vector2 World(float d, float lat)
    {
        Frame f = FrameAt(d);
        return new Vector2(f.c.x, f.c.z) + f.rt * lat;
    }

    static float LatLimit(float d, float r, int side)
    {
        Frame f = FrameAt(d);
        float w = side > 0 ? f.wr : f.wl;
        return Mathf.Min(MaxBankLat, w) - r - 0.6f;
    }

    static bool Wet(Vector2 p) { return terr.SampleHeight(new Vector3(p.x, 0f, p.y)) < WaterThreshold; }

    static bool OnWater(Vector2 p, float r)
    {
        if (!Wet(p)) return false;
        for (int k = 0; k < 4; k++)
        {
            float a = k * Mathf.PI * 0.5f;
            if (!Wet(p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r)) return false;
        }
        return true;
    }

    static bool SegmentWet(Vector2 a, Vector2 b, float r)
    {
        float len = (b - a).magnitude;
        int n = Mathf.Max(1, (int)(len / 3f));
        for (int i = 0; i <= n; i++) if (!OnWater(Vector2.Lerp(a, b, i / (float)n), r)) return false;
        return true;
    }

    static float DistSeg(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
        return (p - (a + ab * t)).magnitude;
    }

    static void ToDL(Vector2 p, out float d, out float lat)
    {
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < lut.Count; i++)
        {
            Vector3 c = lut[i].c;
            float dd = (new Vector2(c.x, c.z) - p).sqrMagnitude;
            if (dd < bd) { bd = dd; best = i; }
        }
        d = best * LutStep;
        Frame f = lut[best];
        lat = Vector2.Dot(p - new Vector2(f.c.x, f.c.z), f.rt);
    }

    // d = jarak dari titik awal kapal ke arah hulu (kebalikan arah aliran). Spline sudah dibalik: t = 1 adalah awal kapal.
    static List<Frame> BuildLut(SplineContainer sc)
    {
        const int M = 16000;
        var pos = new Vector3[M + 1];
        var up = new Vector2[M + 1];
        var cum = new float[M + 1];
        for (int i = 0; i <= M; i++)
        {
            float t = 1f - i / (float)M;
            pos[i] = sc.EvaluatePosition(t);
            Vector3 tg = sc.EvaluateTangent(t);
            var u = new Vector2(-tg.x, -tg.z);
            if (u.sqrMagnitude < 1e-8f) u = i > 0 ? up[i - 1] : Vector2.up;
            up[i] = u.normalized;
            cum[i] = i == 0 ? 0f : cum[i - 1] + Vector3.Distance(pos[i - 1], pos[i]);
        }

        float total = cum[M];
        int n = (int)(total / LutStep) + 1;
        var list = new List<Frame>(n);
        int j = 0;
        for (int k = 0; k < n; k++)
        {
            float d = k * LutStep;
            while (j < M - 1 && cum[j + 1] < d) j++;
            float seg = Mathf.Max(1e-4f, cum[j + 1] - cum[j]);
            float f = Mathf.Clamp01((d - cum[j]) / seg);
            var fr = new Frame { c = Vector3.Lerp(pos[j], pos[j + 1], f) };
            Vector2 u = Vector2.Lerp(up[j], up[j + 1], f).normalized;
            fr.up = u; fr.rt = new Vector2(u.y, -u.x);

            Vector2 c2 = new Vector2(fr.c.x, fr.c.z);
            if (!Wet(c2)) { fr.wl = 0f; fr.wr = 0f; }
            else
            {
                float r = 0f, l = 0f;
                for (float x = 0.5f; x <= 22f; x += 0.5f) { if (Wet(c2 + fr.rt * x)) r = x; else break; }
                for (float x = 0.5f; x <= 22f; x += 0.5f) { if (Wet(c2 - fr.rt * x)) l = x; else break; }
                fr.wr = r; fr.wl = l;
            }
            list.Add(fr);
        }
        return list;
    }

    static Proto LoadProto(string name)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabDir + name + ".prefab");
        if (prefab == null) throw new Exception("Prefab tidak ditemukan: " + PrefabDir + name + ".prefab");

        var tmp = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        tmp.transform.position = Vector3.zero;
        Physics.SyncTransforms();
        float rc = 0f, rr = 0f;
        foreach (var c in tmp.GetComponentsInChildren<Collider>())
        {
            Bounds b = c.bounds;
            rc = Mathf.Max(rc, 0.5f * Mathf.Max(b.size.x, b.size.z));
        }
        bool has = false; Bounds rb = new Bounds();
        foreach (var r in tmp.GetComponentsInChildren<Renderer>())
        {
            if (!has) { rb = r.bounds; has = true; } else rb.Encapsulate(r.bounds);
        }
        if (has) rr = 0.5f * Mathf.Max(rb.size.x, rb.size.z);
        UnityEngine.Object.DestroyImmediate(tmp);
        return new Proto { name = name, prefab = prefab, r = Mathf.Max(rc, rr * 0.95f) };
    }

    static Proto Pick(List<Proto> pool, float[] w, System.Random rng)
    {
        float sum = 0f;
        for (int i = 0; i < w.Length; i++) sum += w[i];
        float x = (float)rng.NextDouble() * sum;
        for (int i = 0; i < w.Length; i++) { x -= w[i]; if (x <= 0f) return pool[i]; }
        return pool[pool.Count - 1];
    }

    // ---------------- menu ----------------
    [MenuItem("Tools/Sungai/Generate Level Design")]
    public static void GenerateMenu()
    {
        Debug.Log(Generate(DefaultSeed));
    }

    [MenuItem("Tools/Sungai/Hapus Level Design")]
    public static void ClearMenu()
    {
        var old = GameObject.Find(RootName);
        if (old != null) Undo.DestroyObjectImmediate(old);
    }

    // ---------------- generator ----------------
    public static string Generate(int seed = DefaultSeed)
    {
        var rng = new System.Random(seed);
        Func<float, float, float> R = (a, b) => a + (float)rng.NextDouble() * (b - a);

        var rp = RiverPath.Main;
        if (rp == null) return "GAGAL: RiverPath tidak ditemukan.";
        var sc = rp.GetComponent<SplineContainer>();
        terr = Terrain.activeTerrain;
        var boatGo = GameObject.Find("Kapal");
        if (terr == null || boatGo == null) return "GAGAL: Terrain atau Kapal tidak ditemukan.";

        // hapus hasil generate sebelumnya
        var oldRoot = GameObject.Find(RootName);
        if (oldRoot != null) Undo.DestroyObjectImmediate(oldRoot);

        lut = BuildLut(sc);
        float waterY = lut[0].c.y;

        Vector2 boatXZ = new Vector2(boatGo.transform.position.x, boatGo.transform.position.z);
        float d0, boatLat;
        ToDL(boatXZ, out d0, out boatLat);

        float dEndWater = 0f;
        for (int i = 0; i < lut.Count; i++) if (lut[i].wl + lut[i].wr >= 22f) dEndWater = i * LutStep;
        float dEnd = dEndWater - EndPoolLength;
        float span = dEnd - d0;

        var hookProtos = new List<Proto> { LoadProto("Batu hook 1"), LoadProto("Batu hook 2"), LoadProto("Batu Hook 3") };
        var wallProtos = new List<Proto>
        {
            LoadProto("Batu"), LoadProto("BatuDatar"), LoadProto("BatuPohon"),
            LoadProto("Sampah AH 1"), LoadProto("Sampah AH 2"), LoadProto("Sampah AH 3")
        };
        var trashProtos = new List<Proto> { LoadProto("Sampah 1"), LoadProto("Sampah 2"), LoadProto("Sampah 3") };

        var items = new List<Item>();
        var hooks = new List<Item>();
        var stops = new List<Vector2> { boatXZ };
        int chainFails = 0; string chainWhy = "";

        // ===== 1. Rantai batu hook (rute tarikan kapal) =====
        float dRock = d0;
        int side = rng.Next(2) == 0 ? -1 : 1;
        while (dRock < dEnd - 6f)
        {
            float p = Mathf.Clamp01((dRock - d0) / span);
            float D = Mathf.Min(23.5f, PullDistance(p) + R(-0.9f, 0.7f));
            Proto pr = hookProtos[rng.Next(hookProtos.Count)];
            float scl = R(0.95f, 1.25f);
            float r = pr.r * scl;
            if (rng.NextDouble() < 0.75) side = -side;

            Vector2 cur = stops[stops.Count - 1];
            float dCur, latCur;
            ToDL(cur, out dCur, out latCur);

            var why = new int[12];
            bool ok = false;
            Vector2 bestC = Vector2.zero, bestStop = Vector2.zero;
            float bestD = 0f;

            for (int t = 0; t < 160 && !ok; t++)
            {
                int sd = t < 60 ? side : -side;
                float latAbs = LaneHalf(p) + r * R(0.15f, 0.85f);
                if (t >= 100) latAbs = LaneHalf(p) * R(0.3f, 0.9f);
                float Dt = D - (t / 40) * 0.6f;
                float lat = sd * latAbs;
                // simpangan lateral dibatasi supaya jarak lempar bisa dicapai sambil tetap maju ke hulu
                lat = Mathf.Clamp(lat, latCur - 0.72f * Dt, latCur + 0.72f * Dt);
                sd = lat >= 0f ? 1 : -1;

                float dd = dCur + Mathf.Sqrt(Mathf.Max(4f, Dt * Dt - (lat - latCur) * (lat - latCur)));
                Vector2 C = World(dd, lat);
                for (int it = 0; it < 6; it++)
                {
                    C = World(dd, lat);
                    dd += (Dt - (C - cur).magnitude) * 0.9f;
                }
                C = World(dd, lat);

                float lim = LatLimit(dd, r, sd);
                if (lim < 3f) { why[0]++; continue; }
                if (Mathf.Abs(lat) > lim) { lat = sd * lim; C = World(dd, lat); }

                float dist = (C - cur).magnitude;
                if (dist > LassoReach || dist < 8f) { why[1]++; continue; }
                if (dd <= dRock + 3f || dd > dEnd + 6f) { why[2]++; continue; }
                if (!OnWater(C, r * 0.9f)) { why[3]++; continue; }

                Vector2 u = (C - cur) / dist;
                Vector2 stop = C - u * (BoatRadius + r + StopGap);
                if ((stop - cur).magnitude < 4f) { why[4]++; continue; }
                if (!OnWater(stop, BoatRadius * 0.85f)) { why[5]++; continue; }
                if (!SegmentWet(cur, stop, BoatRadius * 0.8f)) { why[6]++; continue; }

                bool bad = false;
                for (int j = Mathf.Max(0, hooks.Count - 8); j < hooks.Count && !bad; j++)
                    if ((C - hooks[j].pos).magnitude < r + hooks[j].r + 1.5f) bad = true;
                if (bad) { why[7]++; continue; }

                // rute tidak boleh menabrak batu hook lain (kecuali batu sumber tempat kapal sedang berhenti)
                for (int j = 0; j < hooks.Count - 1 && !bad; j++)
                    if (DistSeg(hooks[j].pos, cur, stop) < BoatRadius + hooks[j].r + 0.3f) bad = true;
                if (bad) { why[8]++; continue; }

                // keluar dari batu sumber: tidak boleh langsung menabrak batu itu lagi
                if (hooks.Count > 0)
                {
                    Item src = hooks[hooks.Count - 1];
                    if (Vector2.Distance(src.pos, cur + u * 3f) < BoatRadius + src.r - 0.1f) { why[9]++; continue; }
                }

                ok = true; bestC = C; bestStop = stop; bestD = dd;
            }

            if (!ok) { chainFails++; chainWhy = "hook#" + hooks.Count + " d=" + dRock.ToString("F0") + " p=" + p.ToString("F2") + " D=" + D.ToString("F1") + " alasan=[" + string.Join(",", why) + "]"; break; }

            hooks.Add(new Item
            {
                proto = pr, pos = bestC, r = r, scale = scl, yaw = R(0f, 360f), d = bestD,
                kind = "hook", stopIndex = stops.Count
            });
            stops.Add(bestStop);
            dRock = bestD;
        }

        items.AddRange(hooks);

        // posisi rute di koordinat (d, lat) untuk bantu penempatan rintangan
        var sD = new List<float>(); var sL = new List<float>();
        for (int i = 0; i < stops.Count; i++)
        {
            float dd, ll; ToDL(stops[i], out dd, out ll);
            sD.Add(dd); sL.Add(ll);
        }
        Func<float, float> PathLatAt = dq =>
        {
            if (dq <= sD[0]) return sL[0];
            for (int i = 0; i < sD.Count - 1; i++)
                if (dq <= sD[i + 1]) return Mathf.Lerp(sL[i], sL[i + 1], Mathf.InverseLerp(sD[i], sD[i + 1], dq));
            return sL[sL.Count - 1];
        };

        Func<Vector2, float, float, bool> Free = (c, r, sep) =>
        {
            for (int i = 0; i < items.Count; i++)
                if ((items[i].pos - c).magnitude < r + items[i].r + sep) return false;
            return true;
        };

        Func<Vector2, float, float, bool> TubeOk = (c, r, clr) =>
        {
            float need = r + BoatRadius + clr;
            for (int i = 0; i < stops.Count - 1; i++)
                if (DistSeg(c, stops[i], stops[i + 1]) < need) return false;
            return true;
        };

        // ===== 2. Tumpukan sampah (bendungan) yang menutupi rute: harus di-hook & ditarik sampai lepas =====
        int damCount = 0;
        float nextDam = d0 + 0.38f * span;
        while (nextDam < dEnd - 60f)
        {
            float p = (nextDam - d0) / span;
            int seg = -1;
            for (int i = 0; i < stops.Count - 1; i++) if (sD[i] <= nextDam && nextDam <= sD[i + 1]) { seg = i; break; }
            if (seg >= 0)
            {
                Vector2 a = stops[seg], b = stops[seg + 1];
                Vector2 center = Vector2.Lerp(a, b, Mathf.InverseLerp(sD[seg], sD[seg + 1], nextDam));
                Vector2 dir = (b - a).normalized, nrm = new Vector2(-dir.y, dir.x);
                int n = 2 + (p > 0.6f ? 1 : 0) + (p > 0.85f ? 1 : 0);
                var pieces = new List<Proto>(); float total = 0f;
                for (int k = 0; k < n; k++)
                {
                    Proto tp = Pick(trashProtos, TrashWeights(p), rng);
                    pieces.Add(tp); total += tp.r * 2f + 0.4f;
                }
                float off = -total * 0.5f; int placedHere = 0;
                for (int k = 0; k < n; k++)
                {
                    float rr = pieces[k].r;
                    off += rr;
                    Vector2 C = center + nrm * off + dir * R(-1.5f, 1.5f);
                    off += rr + 0.4f;
                    if (!OnWater(C, rr * 0.8f) || !Free(C, rr, 0.5f)) continue;
                    bool nearStop = false;
                    for (int i = 0; i < stops.Count; i++) if ((stops[i] - C).magnitude < BoatRadius + rr + 1f) { nearStop = true; break; }
                    if (nearStop) continue;
                    float dd, ll; ToDL(C, out dd, out ll);
                    items.Add(new Item { proto = pieces[k], pos = C, r = rr, scale = 1f, yaw = R(0f, 360f), d = dd, kind = "dam" });
                    placedHere++;
                }
                if (placedHere > 0) damCount++;
            }
            nextDam += DamSpacing(p) * R(0.85f, 1.15f);
        }

        // ===== 3. Rintangan statis (batu tanpa hook + sampah AH) & sampah hook tambahan =====
        for (float binD = d0 + HarborLength; binD < dEnd; binD += 50f)
        {
            float pBin = Mathf.Clamp01((binD + 25f - d0) / span);
            int zone = ZoneOf(pBin);

            // --- rintangan statis ---
            float expected = WallDensity(pBin) * 0.5f;
            int count = (int)expected; if (rng.NextDouble() < expected - count) count++;
            for (int c = 0; c < count; c++)
                TryPlaceExtra(items, wallProtos, WallWeights[zone], false, binD, pBin, zone, rng, R, PathLatAt, Free, TubeOk);

            // --- sampah hook tambahan (opsional / jebakan) ---
            float expT = TrashDensity(pBin) * 0.5f;
            int cntT = (int)expT; if (rng.NextDouble() < expT - cntT) cntT++;
            for (int c = 0; c < cntT; c++)
                TryPlaceExtra(items, trashProtos, TrashWeights(pBin), true, binD, pBin, zone, rng, R, PathLatAt, Free, TubeOk);
        }

        // ===== 4. Validasi: rute kapal harus muat lewat =====
        float minClear = float.MaxValue; int violations = 0; float maxPull = 0f;
        for (int i = 0; i < stops.Count - 1; i++)
        {
            Item target = hooks[i];
            maxPull = Mathf.Max(maxPull, (target.pos - stops[i]).magnitude);
            foreach (var it in items)
            {
                if (it.kind == "dam" || it.kind == "trash") continue;       // sampah hook bisa dibersihkan
                if (it == target) continue;
                if (i > 0 && it == hooks[i - 1]) continue;                  // batu tempat kapal berhenti
                float clear = DistSeg(it.pos, stops[i], stops[i + 1]) - it.r - BoatRadius;
                if (clear < minClear) minClear = clear;
                if (clear < -0.05f) violations++;
            }
        }

        // ===== 5. Spawn ke scene =====
        Undo.SetCurrentGroupName("Generate Level Design Sungai");
        int undoGroup = Undo.GetCurrentGroup();

        var root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, "Level Design Sungai");
        var groups = new Dictionary<string, Transform>();
        var counters = new Dictionary<string, int>();

        foreach (var it in items)
        {
            float pIt = Mathf.Clamp01((it.d - d0) / span);
            it.zone = ZoneOf(pIt);
            string folder = it.kind == "hook" ? "Batu_Hook"
                          : it.kind == "rock" ? "Batu_Rintangan"
                          : it.kind == "ah" ? "Sampah_AH" : "Sampah_Hook";
            string key = it.zone + "/" + folder;
            Transform parent;
            if (!groups.TryGetValue(key, out parent))
            {
                Transform zt;
                if (!groups.TryGetValue("Z" + it.zone, out zt))
                {
                    var zg = new GameObject(ZoneNames[it.zone]);
                    zg.transform.SetParent(root.transform, false);
                    zt = zg.transform; groups["Z" + it.zone] = zt;
                }
                var fg = new GameObject(folder);
                fg.transform.SetParent(zt, false);
                parent = fg.transform; groups[key] = parent;
            }
            int idx; counters.TryGetValue(key, out idx); counters[key] = idx + 1;
            bool isTrash = it.kind == "trash" || it.kind == "dam";
            SpawnItem(it, parent, it.proto.name + " (" + idx + ")", waterY, isTrash);
        }

        Undo.CollapseUndoOperations(undoGroup);

        var obs = rp.GetComponent<RiverObstacleMap>();
        if (obs != null) obs.Bake();
        EditorSceneManager.MarkSceneDirty(root.scene);

        // ===== laporan =====
        var sb = new StringBuilder();
        sb.AppendLine("Level design sungai selesai (seed " + seed + ").");
        sb.AppendLine("Panjang rute: awal kapal d=" + d0.ToString("F0") + " m -> ujung d=" + dEnd.ToString("F0") + " m (" + span.ToString("F0") + " m)");
        if (chainFails > 0) sb.AppendLine("!! RANTAI GAGAL: " + chainWhy);
        sb.AppendLine("Batu hook: " + hooks.Count + " | tumpukan sampah: " + damCount + " | rantai gagal: " + chainFails);
        sb.AppendLine("Validasi rute: ruang bebas minimum di luar badan kapal = " + minClear.ToString("F2") + " m, pelanggaran = " + violations + ", jarak lempar maks = " + maxPull.ToString("F1") + " m");
        for (int z = 0; z < ZoneNames.Length; z++)
        {
            int nh = 0, nr = 0, na = 0, nt = 0; float sumPull = 0f;
            foreach (var it in items)
            {
                if (it.zone != z) continue;
                if (it.kind == "hook") { nh++; sumPull += (it.pos - stops[it.stopIndex - 1]).magnitude; }
                else if (it.kind == "rock") nr++;
                else if (it.kind == "ah") na++;
                else nt++;
            }
            sb.AppendLine("  " + ZoneNames[z] + ": hook " + nh + ", batu rintangan " + nr + ", sampah AH " + na + ", sampah hook " + nt
                + (nh > 0 ? ", rata-rata jarak lempar " + (sumPull / nh).ToString("F1") + " m" : ""));
        }
        return sb.ToString();
    }

    static void TryPlaceExtra(List<Item> items, List<Proto> pool, float[] weights, bool hookTrash, float binD, float p, int zone,
        System.Random rng, Func<float, float, float> R, Func<float, float> pathLatAt,
        Func<Vector2, float, float, bool> free, Func<Vector2, float, float, bool> tubeOk)
    {
        for (int tries = 0; tries < 50; tries++)
        {
            float d = binD + R(0f, 50f);
            Proto pr = Pick(pool, weights, rng);
            bool isAH = !hookTrash && pr.name.StartsWith("Sampah AH");
            float scl = hookTrash || isAH ? 1f : R(0.9f, 1.3f);
            float r = pr.r * scl;
            float clr = Clearance(p);

            int sd = rng.Next(2) == 0 ? -1 : 1;
            float lat;
            if (rng.NextDouble() < 0.2)
                lat = sd * R(0f, 14f);
            else
            {
                float mean = Lerp(3.5f, 1.2f, p);
                float ex = -mean * Mathf.Log(1f - (float)rng.NextDouble() * 0.999f);
                lat = pathLatAt(d) + sd * (r + BoatRadius + clr + ex);
            }

            int ls = lat >= 0f ? 1 : -1;
            float lim = LatLimit(d, r, ls);
            if (Mathf.Abs(lat) > lim) continue;

            Vector2 C = World(d, lat);
            if (!OnWater(C, r * 0.8f)) continue;
            if (!tubeOk(C, r, clr)) continue;
            if (!free(C, r, Lerp(1.2f, 0.3f, p))) continue;

            items.Add(new Item
            {
                proto = pr, pos = C, r = r, scale = scl, yaw = R(0f, 360f), d = d,
                kind = hookTrash ? "trash" : (isAH ? "ah" : "rock")
            });
            return;
        }
    }

    static void SpawnItem(Item it, Transform parent, string nm, float waterY, bool isTrash)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(it.proto.prefab, parent);
        go.name = nm;
        var t = go.transform;
        t.localScale = it.proto.prefab.transform.localScale * it.scale;
        t.rotation = Quaternion.Euler(0f, it.yaw, 0f) * it.proto.prefab.transform.rotation;

        if (isTrash || it.proto.name.StartsWith("Sampah"))
        {
            // sampah: tinggi pusat sama seperti contoh di scene (sedikit di bawah permukaan air)
            t.position = new Vector3(it.pos.x, waterY - 0.07f, it.pos.y);
        }
        else
        {
            t.position = new Vector3(it.pos.x, waterY, it.pos.y);
            bool has = false; Bounds b = new Bounds();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
            }
            if (has) t.position += Vector3.up * ((waterY - 0.25f) - b.min.y);
        }
        Undo.RegisterCreatedObjectUndo(go, "Level Design Sungai");
    }
}
#endif
