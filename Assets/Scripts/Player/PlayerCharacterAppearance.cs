using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Menyinkronkan pilihan varian karakter unik per pemain di multiplayer (via NetworkVariable)
/// dan menerapkan model/mesh/material/warna dari PlayerCharacterCatalog secara efisien (WebGL-friendly).
/// </summary>
[DisallowMultipleComponent]
public class PlayerCharacterAppearance : NetworkBehaviour
{
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int LegacyColorId = Shader.PropertyToID("_Color");

    [Header("Katalog Karakter")]
    [Tooltip("Seret asset PlayerCharacterCatalog di sini. Teman tim cukup mengisi daftar model di asset katalog tersebut.")]
    [SerializeField] private PlayerCharacterCatalog catalog;

    [Header("Visual Default pada Prefab Player")]
    [Tooltip("Transform badan default (otomatis dicari dari PlayerSwayAnimator jika kosong).")]
    [SerializeField] private Transform defaultBodyVisual;
    [Tooltip("Transform kepala default (otomatis dicari dari PlayerSwayAnimator jika kosong).")]
    [SerializeField] private Transform defaultHeadVisual;

    /// <summary>
    /// Indeks karakter yang ditetapkan Server (unik untuk setiap pemain dalam satu sesi).
    /// </summary>
    public NetworkVariable<int> CharacterIndex = new NetworkVariable<int>(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public int ActiveCharacterIndex => appliedIndex;
    public string ActiveCharacterName { get; private set; } = string.Empty;

    private PlayerSwayAnimator swayAnimator;
    private PlayerMovement playerMovement;
    private Transform modelRoot;
    private GameObject spawnedCustomModel;
    private MaterialPropertyBlock propertyBlock;

    private Mesh initialBodyMesh;
    private Mesh initialHeadMesh;
    private Material[] initialBodyMaterials;
    private Material[] initialHeadMaterials;
    private bool initialDefaultsCached;
    private int appliedIndex = -1;

    private void Awake()
    {
        swayAnimator = GetComponent<PlayerSwayAnimator>();
        playerMovement = GetComponent<PlayerMovement>();
        propertyBlock = new MaterialPropertyBlock();

        ResolveDefaultVisuals();
        CacheInitialDefaultState();
    }

    public override void OnNetworkSpawn()
    {
        CharacterIndex.OnValueChanged += HandleCharacterIndexChanged;

        // Fallback aman jika Player di-spawn tanpa melalui GameSpawner (mis. test langsung di editor)
        if (IsServer && CharacterIndex.Value < 0)
        {
            int fallbackSlot = (catalog != null && catalog.Count > 0)
                ? (int)(OwnerClientId % (ulong)catalog.Count)
                : (int)OwnerClientId;
            CharacterIndex.Value = fallbackSlot;
        }

        if (CharacterIndex.Value >= 0)
        {
            ApplyCharacter(CharacterIndex.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        CharacterIndex.OnValueChanged -= HandleCharacterIndexChanged;
    }

    /// <summary>
    /// Dipanggil oleh Server (misalnya dari GameSpawner) untuk memberikan slot karakter unik ke pemain ini.
    /// </summary>
    public void AssignCharacterIndex(int index)
    {
        if (!IsServer) return;
        CharacterIndex.Value = Mathf.Max(0, index);
    }

    private void HandleCharacterIndexChanged(int previousValue, int newValue)
    {
        if (newValue >= 0)
        {
            ApplyCharacter(newValue);
        }
    }

    private void ResolveDefaultVisuals()
    {
        if (swayAnimator != null)
        {
            if (defaultBodyVisual == null) defaultBodyVisual = swayAnimator.Body;
            if (defaultHeadVisual == null) defaultHeadVisual = swayAnimator.Head;
        }
    }

    private void CacheInitialDefaultState()
    {
        if (initialDefaultsCached) return;
        initialDefaultsCached = true;

        if (defaultBodyVisual != null)
        {
            var mf = defaultBodyVisual.GetComponent<MeshFilter>();
            var mr = defaultBodyVisual.GetComponent<MeshRenderer>();
            if (mf != null) initialBodyMesh = mf.sharedMesh;
            if (mr != null) initialBodyMaterials = mr.sharedMaterials;
        }

        if (defaultHeadVisual != null)
        {
            var mf = defaultHeadVisual.GetComponent<MeshFilter>();
            var mr = defaultHeadVisual.GetComponent<MeshRenderer>();
            if (mf != null) initialHeadMesh = mf.sharedMesh;
            if (mr != null) initialHeadMaterials = mr.sharedMaterials;
        }
    }

    private void ApplyCharacter(int index)
    {
        if (catalog == null || catalog.Count == 0) return;
        if (appliedIndex == index && spawnedCustomModel != null) return;

        PlayerCharacterCatalog.CharacterVariant variant = catalog.GetVariant(index);
        if (variant == null) return;

        appliedIndex = index;
        ActiveCharacterName = variant.characterName;

        ClearSpawnedCustomModel();

        if (variant.modelPrefab != null)
        {
            ApplyCustomPrefabVariant(variant);
        }
        else
        {
            ApplyDefaultVisualVariant(variant);
        }

        // Perbarui cache renderer & target animasi muncul (respawn) di PlayerMovement
        if (playerMovement != null)
        {
            playerMovement.RefreshVisualCache();
        }
    }

    private void ApplyCustomPrefabVariant(PlayerCharacterCatalog.CharacterVariant variant)
    {
        SetDefaultVisualsActive(false);

        if (modelRoot == null)
        {
            var rootGo = new GameObject("CharacterModelRoot");
            rootGo.layer = 0; // Default layer
            modelRoot = rootGo.transform;
            modelRoot.SetParent(transform, false);
        }

        spawnedCustomModel = Instantiate(variant.modelPrefab, modelRoot);
        spawnedCustomModel.name = string.IsNullOrWhiteSpace(variant.characterName)
            ? variant.modelPrefab.name
            : variant.characterName;

        Transform modelTr = spawnedCustomModel.transform;
        modelTr.localPosition = variant.modelLocalPosition;
        modelTr.localRotation = Quaternion.Euler(variant.modelLocalEuler);
        modelTr.localScale = variant.modelLocalScale;

        // Pastikan model visual tidak membawa Collider / Rigidbody yang dapat mengganggu fisika kapal/player
        StripPhysicsFromVisualModel(spawnedCustomModel);

        // Hubungkan kembali PlayerSwayAnimator ke child body/head (atau root model jika tidak ditentukan)
        if (swayAnimator != null)
        {
            Transform newBody = !string.IsNullOrWhiteSpace(variant.bodyChildName)
                ? FindChildRecursive(modelTr, variant.bodyChildName)
                : modelTr;
            Transform newHead = !string.IsNullOrWhiteSpace(variant.headChildName)
                ? FindChildRecursive(modelTr, variant.headChildName)
                : null;

            swayAnimator.RebindVisuals(newBody != null ? newBody : modelTr, newHead);
        }
    }

    private void ApplyDefaultVisualVariant(PlayerCharacterCatalog.CharacterVariant variant)
    {
        SetDefaultVisualsActive(true);
        RestoreInitialDefaultMeshesAndMaterials();

        bool hasCustomMaterial = false;

        if (defaultBodyVisual != null)
        {
            var mf = defaultBodyVisual.GetComponent<MeshFilter>();
            var mr = defaultBodyVisual.GetComponent<MeshRenderer>();
            if (mf != null && variant.bodyMeshOverride != null) mf.sharedMesh = variant.bodyMeshOverride;
            if (mr != null && variant.bodyMaterialOverride != null)
            {
                mr.sharedMaterial = variant.bodyMaterialOverride;
                hasCustomMaterial = true;
            }
        }

        if (defaultHeadVisual != null)
        {
            var mf = defaultHeadVisual.GetComponent<MeshFilter>();
            var mr = defaultHeadVisual.GetComponent<MeshRenderer>();
            if (mf != null && variant.headMeshOverride != null) mf.sharedMesh = variant.headMeshOverride;
            if (mr != null && variant.headMaterialOverride != null)
            {
                mr.sharedMaterial = variant.headMaterialOverride;
                hasCustomMaterial = true;
            }
        }

        // Pakai MaterialPropertyBlock agar warna pembeda tidak menduplikasi Material di memori (WebGL-friendly)
        if (variant.useFallbackTint && !hasCustomMaterial)
        {
            ApplyTintToTransform(defaultBodyVisual, variant.fallbackTintColor);
            ApplyTintToTransform(defaultHeadVisual, Color.Lerp(Color.white, variant.fallbackTintColor, 0.35f));
        }
        else
        {
            ClearTintOnTransform(defaultBodyVisual);
            ClearTintOnTransform(defaultHeadVisual);
        }

        if (swayAnimator != null)
        {
            swayAnimator.RebindVisuals(defaultBodyVisual, defaultHeadVisual);
        }
    }

    private void ApplyTintToTransform(Transform target, Color tint)
    {
        if (target == null) return;
        Renderer r = target.GetComponent<Renderer>();
        if (r == null) return;

        r.GetPropertyBlock(propertyBlock);
        propertyBlock.SetColor(BaseColorId, tint);
        propertyBlock.SetColor(LegacyColorId, tint);
        r.SetPropertyBlock(propertyBlock);
    }

    private void ClearTintOnTransform(Transform target)
    {
        if (target == null) return;
        Renderer r = target.GetComponent<Renderer>();
        if (r != null) r.SetPropertyBlock(null);
    }

    private void RestoreInitialDefaultMeshesAndMaterials()
    {
        if (!initialDefaultsCached) return;

        if (defaultBodyVisual != null)
        {
            var mf = defaultBodyVisual.GetComponent<MeshFilter>();
            var mr = defaultBodyVisual.GetComponent<MeshRenderer>();
            if (mf != null && initialBodyMesh != null) mf.sharedMesh = initialBodyMesh;
            if (mr != null && initialBodyMaterials != null && initialBodyMaterials.Length > 0)
                mr.sharedMaterials = initialBodyMaterials;
        }

        if (defaultHeadVisual != null)
        {
            var mf = defaultHeadVisual.GetComponent<MeshFilter>();
            var mr = defaultHeadVisual.GetComponent<MeshRenderer>();
            if (mf != null && initialHeadMesh != null) mf.sharedMesh = initialHeadMesh;
            if (mr != null && initialHeadMaterials != null && initialHeadMaterials.Length > 0)
                mr.sharedMaterials = initialHeadMaterials;
        }
    }

    private void SetDefaultVisualsActive(bool active)
    {
        if (defaultBodyVisual != null) defaultBodyVisual.gameObject.SetActive(active);
        if (defaultHeadVisual != null) defaultHeadVisual.gameObject.SetActive(active);
    }

    private void ClearSpawnedCustomModel()
    {
        if (spawnedCustomModel != null)
        {
            Destroy(spawnedCustomModel);
            spawnedCustomModel = null;
        }
    }

    private static void StripPhysicsFromVisualModel(GameObject root)
    {
        Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            colliders[i].enabled = false;
        }

        Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < bodies.Length; i++)
        {
            bodies[i].isKinematic = true;
            bodies[i].detectCollisions = false;
        }
    }

    private static Transform FindChildRecursive(Transform parent, string targetName)
    {
        if (parent == null || string.IsNullOrEmpty(targetName)) return null;
        if (parent.name == targetName) return parent;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform found = FindChildRecursive(parent.GetChild(i), targetName);
            if (found != null) return found;
        }
        return null;
    }
}

