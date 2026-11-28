using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Pemutar efek partikel air (cipratan) ber-pool untuk scene In_Game.
/// Dipakai oleh:
/// - FishProjectile        : riak saat ikan bersiap lompat, cipratan saat keluar air, cipratan saat nyemplung lagi.
/// - PlayerMovement        : cipratan saat pemain jatuh/nyemplung ke air.
/// - BoatImpactBroadcaster : cipratan air di sisi kapal saat kapal menyenggol/menabrak rintangan.
/// Jika prefab di Inspector dikosongkan atau objek WaterSplashVFX belum dipasang di scene,
/// sistem otomatis membuat efek partikel air prosedural yang di-pool (aman untuk WebGL/URP).
/// </summary>
[DisallowMultipleComponent]
public sealed class WaterSplashVFX : MonoBehaviour
{
    public static WaterSplashVFX Instance { get; private set; }

    [Header("Prefab Partikel (Opsional — kosong = otomatis pakai efek prosedural)")]
    [Tooltip("Cipratan besar ke atas saat ikan menembus permukaan air untuk melompat.")]
    [SerializeField] private ParticleSystem leapSplashPrefab;
    [Tooltip("Cipratan saat sesuatu (ikan / pemain) masuk ke air.")]
    [SerializeField] private ParticleSystem entrySplashPrefab;
    [Tooltip("Riak + gelembung kecil di permukaan air sebagai tanda ikan akan melompat.")]
    [SerializeField] private ParticleSystem ripplePrefab;
    [Tooltip("Cipratan air di sisi kapal saat menyenggol/menabrak batu, sampah, atau tepian.")]
    [SerializeField] private ParticleSystem hullImpactSplashPrefab;

    [Header("Pool")]
    [Tooltip("Jumlah instance per jenis efek yang di-prewarm dan didaur ulang bergantian.")]
    [SerializeField, Range(2, 16)] private int instancesPerType = 6;

    private Pool leapPool;
    private Pool entryPool;
    private Pool ripplePool;
    private Pool hullImpactPool;
    private bool poolsInitialized;

    private static Texture2D sharedDropletTexture;
    private static Texture2D sharedRingTexture;
    private static Material sharedDropletMaterial;
    private static Material sharedRingMaterial;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
        sharedDropletTexture = null;
        sharedRingTexture = null;
        sharedDropletMaterial = null;
        sharedRingMaterial = null;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        InitializePools();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>Memastikan pool sudah siap sejak awal match agar tidak ada hitch saat cipratan pertama.</summary>
    public static void Prewarm()
    {
        EnsureInstance();
    }

    /// <summary>Cipratan ikan menembus permukaan air saat melompat.</summary>
    public static void PlayLeapSplash(Vector3 position, float scale = 1f)
    {
        WaterSplashVFX vfx = EnsureInstance();
        vfx?.leapPool?.Play(position, Quaternion.identity, scale);
    }

    /// <summary>Cipratan saat ikan / pemain masuk ke air.</summary>
    public static void PlayEntrySplash(Vector3 position, float scale = 1f)
    {
        WaterSplashVFX vfx = EnsureInstance();
        vfx?.entryPool?.Play(position, Quaternion.identity, scale);
    }

    /// <summary>Riak kecil di permukaan air (telegraph sebelum ikan melompat).</summary>
    public static void PlayRipple(Vector3 position, float scale = 1f)
    {
        WaterSplashVFX vfx = EnsureInstance();
        vfx?.ripplePool?.Play(position, Quaternion.identity, scale);
    }

    /// <summary>Cipratan air di sisi kapal yang menabrak rintangan.</summary>
    public static void PlayBoatImpactSplash(Vector3 position, Vector3 outwardNormal, float scale = 1f)
    {
        WaterSplashVFX vfx = EnsureInstance();
        if (vfx == null || vfx.hullImpactPool == null) return;

        outwardNormal.y = 0f;
        Quaternion rot = outwardNormal.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(outwardNormal.normalized, Vector3.up)
            : Quaternion.identity;

        vfx.hullImpactPool.Play(position, rot, scale);
    }

    private static WaterSplashVFX EnsureInstance()
    {
        if (Instance != null)
        {
            Instance.InitializePools();
            return Instance;
        }

        WaterSplashVFX found = FindFirstObjectByType<WaterSplashVFX>();
        if (found != null)
        {
            Instance = found;
            Instance.InitializePools();
            return Instance;
        }

        GameObject go = new GameObject("WaterSplashVFX_Auto");
        Instance = go.AddComponent<WaterSplashVFX>();
        Instance.InitializePools();
        return Instance;
    }

    private void InitializePools()
    {
        if (poolsInitialized) return;
        poolsInitialized = true;

        int count = Mathf.Clamp(instancesPerType, 2, 16);

        bool customLeap = leapSplashPrefab != null;
        ParticleSystem leapTemplate = customLeap ? leapSplashPrefab : CreateProceduralLeapSplashTemplate();
        leapPool = new Pool(leapTemplate, count, transform, destroyTemplateAfterClone: !customLeap);

        bool customEntry = entrySplashPrefab != null;
        ParticleSystem entryTemplate = customEntry ? entrySplashPrefab : CreateProceduralEntrySplashTemplate();
        entryPool = new Pool(entryTemplate, count, transform, destroyTemplateAfterClone: !customEntry);

        bool customRipple = ripplePrefab != null;
        ParticleSystem rippleTemplate = customRipple ? ripplePrefab : CreateProceduralRippleTemplate();
        ripplePool = new Pool(rippleTemplate, count, transform, destroyTemplateAfterClone: !customRipple);

        bool customHull = hullImpactSplashPrefab != null;
        ParticleSystem hullTemplate = customHull ? hullImpactSplashPrefab : CreateProceduralHullImpactTemplate();
        hullImpactPool = new Pool(hullTemplate, count, transform, destroyTemplateAfterClone: !customHull);
    }

    // ------------------------------------------------------------------ POOL

    private sealed class Pool
    {
        private readonly ParticleSystem[] items;
        private int next;

        public Pool(ParticleSystem prefab, int count, Transform parent, bool destroyTemplateAfterClone)
        {
            if (prefab == null)
            {
                items = new ParticleSystem[0];
                return;
            }

            items = new ParticleSystem[count];
            for (int i = 0; i < count; i++)
            {
                ParticleSystem ps = Instantiate(prefab, parent);
                ps.gameObject.name = prefab.name + "_" + i;
                ps.gameObject.SetActive(true);
                DisablePlayOnAwakeRecursive(ps);
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                items[i] = ps;
            }

            if (destroyTemplateAfterClone)
            {
                Destroy(prefab.gameObject);
            }
        }

        public void Play(Vector3 position, Quaternion rotation, float scale)
        {
            if (items.Length == 0) return;

            ParticleSystem ps = items[next];
            next = (next + 1) % items.Length;
            if (ps == null) return;

            Transform t = ps.transform;
            t.SetPositionAndRotation(position, rotation);
            t.localScale = Vector3.one * Mathf.Max(0.05f, scale);

            ps.Clear(true);
            ps.Play(true);
        }

        private static void DisablePlayOnAwakeRecursive(ParticleSystem root)
        {
            ParticleSystem[] all = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < all.Length; i++)
            {
                ParticleSystem.MainModule main = all[i].main;
                main.playOnAwake = false;
            }
        }
    }

    // ------------------------------------------------------------------ PROCEDURAL FALLBACK VFX

    private static ParticleSystem CreateProceduralRippleTemplate()
    {
        GameObject root = new GameObject("ProceduralRipple");
        root.SetActive(false);

        ParticleSystem ringPs = root.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = ringPs.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 0.85f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.60f, 0.82f);
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(1.9f, 2.4f);
        main.startColor = new Color(0.88f, 0.98f, 1f, 0.80f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = 6;

        ParticleSystem.EmissionModule emission = ringPs.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[]
        {
            new ParticleSystem.Burst(0f, 1),
            new ParticleSystem.Burst(0.20f, 1)
        });

        ParticleSystem.ShapeModule shape = ringPs.shape;
        shape.enabled = false;

        ParticleSystem.SizeOverLifetimeModule sizeMod = ringPs.sizeOverLifetime;
        sizeMod.enabled = true;
        sizeMod.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.20f),
            new Keyframe(0.55f, 0.82f),
            new Keyframe(1f, 1.12f)));

        ParticleSystem.ColorOverLifetimeModule colMod = ringPs.colorOverLifetime;
        colMod.enabled = true;
        colMod.color = MakeAlphaGradient(0f, 0.85f, 0.15f, 0f);

        ConfigureRenderer(ringPs, GetRingMaterial(), ParticleSystemRenderMode.HorizontalBillboard);

        GameObject bubblesObj = new GameObject("RippleBubbles");
        bubblesObj.transform.SetParent(root.transform, false);
        ParticleSystem bubblesPs = bubblesObj.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule bMain = bubblesPs.main;
        bMain.playOnAwake = false;
        bMain.loop = false;
        bMain.duration = 0.7f;
        bMain.startLifetime = new ParticleSystem.MinMaxCurve(0.28f, 0.50f);
        bMain.startSpeed = new ParticleSystem.MinMaxCurve(1.1f, 2.3f);
        bMain.startSize = new ParticleSystem.MinMaxCurve(0.11f, 0.22f);
        bMain.gravityModifier = 0.95f;
        bMain.startColor = new ParticleSystem.MinMaxGradient(new Color(0.78f, 0.96f, 1f, 0.90f), Color.white);
        bMain.simulationSpace = ParticleSystemSimulationSpace.World;
        bMain.scalingMode = ParticleSystemScalingMode.Hierarchy;
        bMain.maxParticles = 12;

        ParticleSystem.EmissionModule bEmit = bubblesPs.emission;
        bEmit.enabled = true;
        bEmit.rateOverTime = 0f;
        bEmit.SetBursts(new[] { new ParticleSystem.Burst(0f, 7) });

        ParticleSystem.ShapeModule bShape = bubblesPs.shape;
        bShape.enabled = true;
        bShape.shapeType = ParticleSystemShapeType.Cone;
        bShape.angle = 26f;
        bShape.radius = 0.45f;
        bShape.rotation = new Vector3(-90f, 0f, 0f);

        ParticleSystem.ColorOverLifetimeModule bCol = bubblesPs.colorOverLifetime;
        bCol.enabled = true;
        bCol.color = MakeAlphaGradient(0.95f, 0.8f, 0.65f, 0f);

        ConfigureRenderer(bubblesPs, GetDropletMaterial(), ParticleSystemRenderMode.Billboard);

        return ringPs;
    }

    private static ParticleSystem CreateProceduralLeapSplashTemplate()
    {
        GameObject root = new GameObject("ProceduralLeapSplash");
        root.SetActive(false);

        ParticleSystem dropPs = root.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = dropPs.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 0.95f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.82f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(4.4f, 8.4f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.16f, 0.36f);
        main.gravityModifier = 1.65f;
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.72f, 0.95f, 1f, 0.95f), Color.white);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = 28;

        ParticleSystem.EmissionModule emit = dropPs.emission;
        emit.enabled = true;
        emit.rateOverTime = 0f;
        emit.SetBursts(new[] { new ParticleSystem.Burst(0f, 20) });

        ParticleSystem.ShapeModule shape = dropPs.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 30f;
        shape.radius = 0.35f;
        shape.rotation = new Vector3(-90f, 0f, 0f);

        ParticleSystem.SizeOverLifetimeModule sizeMod = dropPs.sizeOverLifetime;
        sizeMod.enabled = true;
        sizeMod.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.9f),
            new Keyframe(0.35f, 1f),
            new Keyframe(1f, 0.25f)));

        ParticleSystem.ColorOverLifetimeModule colMod = dropPs.colorOverLifetime;
        colMod.enabled = true;
        colMod.color = MakeAlphaGradient(1f, 0.9f, 0.65f, 0f);

        ConfigureRenderer(dropPs, GetDropletMaterial(), ParticleSystemRenderMode.Billboard);

        AddFoamChild(root.transform, "LeapFoam", burstCount: 7, coneAngle: 42f, minSpeed: 1.5f, maxSpeed: 3.0f, minSize: 0.48f, maxSize: 0.85f);
        AddRingChild(root.transform, "LeapRing", ringSize: 2.6f, twoBursts: false);

        return dropPs;
    }

    private static ParticleSystem CreateProceduralEntrySplashTemplate()
    {
        GameObject root = new GameObject("ProceduralEntrySplash");
        root.SetActive(false);

        ParticleSystem dropPs = root.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = dropPs.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 1.0f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.85f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(3.6f, 7.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.18f, 0.38f);
        main.gravityModifier = 1.7f;
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.74f, 0.96f, 1f, 0.95f), Color.white);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = 32;

        ParticleSystem.EmissionModule emit = dropPs.emission;
        emit.enabled = true;
        emit.rateOverTime = 0f;
        emit.SetBursts(new[] { new ParticleSystem.Burst(0f, 24) });

        ParticleSystem.ShapeModule shape = dropPs.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 48f;
        shape.radius = 0.45f;
        shape.rotation = new Vector3(-90f, 0f, 0f);

        ParticleSystem.SizeOverLifetimeModule sizeMod = dropPs.sizeOverLifetime;
        sizeMod.enabled = true;
        sizeMod.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.95f),
            new Keyframe(0.35f, 1f),
            new Keyframe(1f, 0.20f)));

        ParticleSystem.ColorOverLifetimeModule colMod = dropPs.colorOverLifetime;
        colMod.enabled = true;
        colMod.color = MakeAlphaGradient(1f, 0.9f, 0.65f, 0f);

        ConfigureRenderer(dropPs, GetDropletMaterial(), ParticleSystemRenderMode.Billboard);

        AddFoamChild(root.transform, "EntryFoam", burstCount: 8, coneAngle: 55f, minSpeed: 1.2f, maxSpeed: 2.6f, minSize: 0.52f, maxSize: 0.95f);
        AddRingChild(root.transform, "EntryRing", ringSize: 3.1f, twoBursts: true);

        return dropPs;
    }

    private static ParticleSystem CreateProceduralHullImpactTemplate()
    {
        GameObject root = new GameObject("ProceduralHullImpactSplash");
        root.SetActive(false);

        // Semburan air di sisi lambung kapal yang menabrak rintangan
        ParticleSystem dropPs = root.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = dropPs.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 0.85f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.68f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(2.8f, 6.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.14f, 0.30f);
        main.gravityModifier = 1.65f;
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.76f, 0.96f, 1f, 0.95f), Color.white);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = 24;

        ParticleSystem.EmissionModule emit = dropPs.emission;
        emit.enabled = true;
        emit.rateOverTime = 0f;
        emit.SetBursts(new[] { new ParticleSystem.Burst(0f, 18) });

        ParticleSystem.ShapeModule shape = dropPs.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 38f;
        shape.radius = 0.28f;
        shape.rotation = new Vector3(-76f, 0f, 0f); // Menyembur ke atas dan sedikit ke arah sisi kapal

        ParticleSystem.SizeOverLifetimeModule sizeMod = dropPs.sizeOverLifetime;
        sizeMod.enabled = true;
        sizeMod.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.90f),
            new Keyframe(0.35f, 1f),
            new Keyframe(1f, 0.20f)));

        ParticleSystem.ColorOverLifetimeModule colMod = dropPs.colorOverLifetime;
        colMod.enabled = true;
        colMod.color = MakeAlphaGradient(1f, 0.9f, 0.6f, 0f);

        ConfigureRenderer(dropPs, GetDropletMaterial(), ParticleSystemRenderMode.Billboard);

        AddFoamChild(root.transform, "HullImpactFoam", burstCount: 6, coneAngle: 48f, minSpeed: 0.9f, maxSpeed: 2.2f, minSize: 0.36f, maxSize: 0.68f);
        AddRingChild(root.transform, "HullImpactRing", ringSize: 2.2f, twoBursts: false);

        return dropPs;
    }

    private static void AddFoamChild(
        Transform parent,
        string childName,
        short burstCount,
        float coneAngle,
        float minSpeed,
        float maxSpeed,
        float minSize,
        float maxSize)
    {
        GameObject foamObj = new GameObject(childName);
        foamObj.transform.SetParent(parent, false);
        ParticleSystem ps = foamObj.AddComponent<ParticleSystem>();

        ParticleSystem.MainModule main = ps.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 0.65f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.28f, 0.50f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(minSpeed, maxSpeed);
        main.startSize = new ParticleSystem.MinMaxCurve(minSize, maxSize);
        main.gravityModifier = 0.35f;
        main.startColor = new Color(0.94f, 0.99f, 1f, 0.68f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = burstCount + 4;

        ParticleSystem.EmissionModule emit = ps.emission;
        emit.enabled = true;
        emit.rateOverTime = 0f;
        emit.SetBursts(new[] { new ParticleSystem.Burst(0f, burstCount) });

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = coneAngle;
        shape.radius = 0.40f;
        shape.rotation = new Vector3(-90f, 0f, 0f);

        ParticleSystem.SizeOverLifetimeModule sizeMod = ps.sizeOverLifetime;
        sizeMod.enabled = true;
        sizeMod.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.55f),
            new Keyframe(1f, 1.35f)));

        ParticleSystem.ColorOverLifetimeModule colMod = ps.colorOverLifetime;
        colMod.enabled = true;
        colMod.color = MakeAlphaGradient(0.85f, 0.65f, 0.3f, 0f);

        ConfigureRenderer(ps, GetDropletMaterial(), ParticleSystemRenderMode.Billboard);
    }

    private static void AddRingChild(Transform parent, string childName, float ringSize, bool twoBursts)
    {
        GameObject ringObj = new GameObject(childName);
        ringObj.transform.SetParent(parent, false);
        ParticleSystem ps = ringObj.AddComponent<ParticleSystem>();

        ParticleSystem.MainModule main = ps.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 0.85f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.55f, 0.78f);
        main.startSpeed = 0f;
        main.startSize = ringSize;
        main.startColor = new Color(0.92f, 0.99f, 1f, 0.82f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = 4;

        ParticleSystem.EmissionModule emit = ps.emission;
        emit.enabled = true;
        emit.rateOverTime = 0f;
        emit.SetBursts(twoBursts
            ? new[] { new ParticleSystem.Burst(0f, 1), new ParticleSystem.Burst(0.14f, 1) }
            : new[] { new ParticleSystem.Burst(0f, 1) });

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.enabled = false;

        ParticleSystem.SizeOverLifetimeModule sizeMod = ps.sizeOverLifetime;
        sizeMod.enabled = true;
        sizeMod.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.25f),
            new Keyframe(0.55f, 0.88f),
            new Keyframe(1f, 1.18f)));

        ParticleSystem.ColorOverLifetimeModule colMod = ps.colorOverLifetime;
        colMod.enabled = true;
        colMod.color = MakeAlphaGradient(0.9f, 0.75f, 0.25f, 0f);

        ConfigureRenderer(ps, GetRingMaterial(), ParticleSystemRenderMode.HorizontalBillboard);
    }

    private static void ConfigureRenderer(ParticleSystem ps, Material mat, ParticleSystemRenderMode mode)
    {
        ParticleSystemRenderer pr = ps.GetComponent<ParticleSystemRenderer>();
        if (pr == null) return;
        pr.sharedMaterial = mat;
        pr.renderMode = mode;
        pr.shadowCastingMode = ShadowCastingMode.Off;
        pr.receiveShadows = false;
    }

    private static ParticleSystem.MinMaxGradient MakeAlphaGradient(float a0, float a1, float midTime, float aEnd)
    {
        Gradient grad = new Gradient();
        grad.SetKeys(
            new[]
            {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f)
            },
            new[]
            {
                new GradientAlphaKey(a0, 0f),
                new GradientAlphaKey(a1, midTime),
                new GradientAlphaKey(aEnd, 1f)
            });
        return new ParticleSystem.MinMaxGradient(grad);
    }

    private static Material GetDropletMaterial()
    {
        if (sharedDropletMaterial == null)
        {
            if (sharedDropletTexture == null) sharedDropletTexture = CreateDropletTexture();
            sharedDropletMaterial = CreateParticleMaterial(sharedDropletTexture, "WaterSplashDropletMat");
        }
        return sharedDropletMaterial;
    }

    private static Material GetRingMaterial()
    {
        if (sharedRingMaterial == null)
        {
            if (sharedRingTexture == null) sharedRingTexture = CreateRingTexture();
            sharedRingMaterial = CreateParticleMaterial(sharedRingTexture, "WaterSplashRingMat");
        }
        return sharedRingMaterial;
    }

    private static Material CreateParticleMaterial(Texture2D tex, string matName)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        bool isUrpParticles = shader != null;

        if (shader == null) shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");

        Material mat = new Material(shader)
        {
            name = matName,
            hideFlags = HideFlags.HideAndDontSave
        };

        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);

        if (isUrpParticles)
        {
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)RenderQueue.Transparent;
        }

        return mat;
    }

    private static Texture2D CreateDropletTexture()
    {
        const int size = 64;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
        {
            name = "WaterDropletProceduralTex",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };

        Color32[] pixels = new Color32[size * size];
        float invHalf = 2f / size;

        for (int y = 0; y < size; y++)
        {
            float ny = (y + 0.5f) * invHalf - 1f;
            int row = y * size;
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) * invHalf - 1f;
                float r2 = nx * nx + ny * ny;
                float a = Mathf.Clamp01(1f - r2);
                a *= a;
                pixels[row + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(true, true);
        return tex;
    }

    private static Texture2D CreateRingTexture()
    {
        const int size = 64;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
        {
            name = "WaterRingProceduralTex",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };

        Color32[] pixels = new Color32[size * size];
        float invHalf = 2f / size;

        for (int y = 0; y < size; y++)
        {
            float ny = (y + 0.5f) * invHalf - 1f;
            int row = y * size;
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) * invHalf - 1f;
                float r = Mathf.Sqrt(nx * nx + ny * ny);

                float band = 1f - Mathf.Clamp01(Mathf.Abs(r - 0.72f) / 0.26f);
                float a = band * band * (3f - 2f * band);
                pixels[row + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(true, true);
        return tex;
    }
}