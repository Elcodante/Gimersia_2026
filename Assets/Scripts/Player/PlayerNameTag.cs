using TMPro;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Menampilkan nama pemain di atas kepala secara world-space (billboard selalu menghadap kamera)
/// meskipun kapal berputar atau pemain bergerak. Nama disinkronkan lewat NetworkPlayerData
/// sehingga semua pemain di sesi multiplayer dapat saling melihat nama masing-masing.
/// </summary>
[DefaultExecutionOrder(500)]
[RequireComponent(typeof(NetworkPlayerData))]
public class PlayerNameTag : MonoBehaviour
{
    [Header("Posisi & Tampilan")]
    [Tooltip("Ketinggian nama di atas titik pusat Player (sumbu Y dunia).")]
    [SerializeField] private float heightOffset = 2.0f;
    [SerializeField] private float fontSize = 4.2f;
    [SerializeField] private Color localPlayerColor = new Color(1f, 0.95f, 0.65f, 1f);
    [SerializeField] private Color otherPlayerColor = Color.white;
    [SerializeField] private Color outlineColor = new Color(0.1f, 0.08f, 0.06f, 1f);
    [SerializeField, Range(0f, 0.5f)] private float outlineWidth = 0.24f;

    [Header("Font (Opsional)")]
    [Tooltip("Kosong = memakai font default TextMeshPro.")]
    [SerializeField] private TMP_FontAsset fontAsset;

    private NetworkPlayerData playerData;
    private PlayerMovement playerMovement;
    private Transform nameTagRoot;
    private TextMeshPro tmpText;
    private MeshRenderer tmpRenderer;
    private Material runtimeMatInstance;
    private string lastDisplayedName = string.Empty;

    private void Awake()
    {
        playerData = GetComponent<NetworkPlayerData>();
        playerMovement = GetComponent<PlayerMovement>();
        CreateNameTagVisual();
    }

    private void OnEnable()
    {
        if (playerData != null)
        {
            playerData.PlayerName.OnValueChanged += HandleNameChanged;
        }
        NetworkPlayerData.OnPlayerListChanged += RefreshName;
    }

    private void Start()
    {
        RefreshName();
    }

    private void OnDisable()
    {
        if (playerData != null)
        {
            playerData.PlayerName.OnValueChanged -= HandleNameChanged;
        }
        NetworkPlayerData.OnPlayerListChanged -= RefreshName;
    }

    private void OnDestroy()
    {
        if (runtimeMatInstance != null)
        {
            Destroy(runtimeMatInstance);
            runtimeMatInstance = null;
        }
    }

    private void CreateNameTagVisual()
    {
        // Wajib sertakan typeof(RectTransform) sejak awal agar referensi Transform tidak hancur
        // ketika komponen TextMeshPro mengganti Transform menjadi RectTransform.
        GameObject go = new GameObject("NameTag", typeof(RectTransform));
        go.layer = 0; // Default layer agar selalu dirender kamera utama

        tmpText = go.AddComponent<TextMeshPro>();
        nameTagRoot = tmpText.rectTransform;
        nameTagRoot.SetParent(transform, false);
        nameTagRoot.localPosition = Vector3.up * heightOffset;
        nameTagRoot.localRotation = Quaternion.identity;
        nameTagRoot.localScale = Vector3.one;

        if (fontAsset != null)
        {
            tmpText.font = fontAsset;
        }

        tmpText.text = string.Empty;
        tmpText.fontSize = fontSize;
        tmpText.fontStyle = FontStyles.Bold;
        tmpText.alignment = TextAlignmentOptions.Center;
        tmpText.textWrappingMode = TextWrappingModes.NoWrap;
        tmpText.overflowMode = TextOverflowModes.Overflow;
        tmpText.color = otherPlayerColor;

        RectTransform rt = tmpText.rectTransform;
        rt.sizeDelta = new Vector2(10f, 1.5f);

        // Gunakan material instance lokal agar outline tidak mengubah material font global UI lain
        if (tmpText.fontSharedMaterial != null)
        {
            ShaderUtilities.GetShaderPropertyIDs();
            runtimeMatInstance = new Material(tmpText.fontSharedMaterial);
            runtimeMatInstance.EnableKeyword("OUTLINE_ON");
            runtimeMatInstance.SetColor(ShaderUtilities.ID_OutlineColor, outlineColor);
            runtimeMatInstance.SetFloat(ShaderUtilities.ID_OutlineWidth, outlineWidth);
            runtimeMatInstance.EnableKeyword("UNDERLAY_ON");
            runtimeMatInstance.SetColor(ShaderUtilities.ID_UnderlayColor, new Color(0f, 0f, 0f, 0.55f));
            runtimeMatInstance.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0f);
            runtimeMatInstance.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, -0.45f);
            runtimeMatInstance.SetFloat(ShaderUtilities.ID_UnderlayDilate, 0.25f);
            runtimeMatInstance.SetFloat(ShaderUtilities.ID_UnderlaySoftness, 0.35f);
            tmpText.fontMaterial = runtimeMatInstance;
        }

        tmpRenderer = tmpText.GetComponent<MeshRenderer>();
        if (tmpRenderer != null)
        {
            tmpRenderer.shadowCastingMode = ShadowCastingMode.Off;
            tmpRenderer.receiveShadows = false;
            tmpRenderer.sortingOrder = 50;
        }
    }

    private void HandleNameChanged(FixedString64Bytes previousValue, FixedString64Bytes newValue)
    {
        ApplyName(newValue.ToString());
    }

    private void RefreshName()
    {
        if (playerData == null) return;

        string currentName = playerData.PlayerName.Value.ToString();
        if (string.IsNullOrWhiteSpace(currentName) && playerData.IsSpawned)
        {
            if (playerData.IsOwner && !string.IsNullOrWhiteSpace(MainMenuController.LocalPlayerName))
            {
                currentName = MainMenuController.LocalPlayerName;
            }
            else
            {
                currentName = $"Player {playerData.OwnerClientId + 1}";
            }
        }

        ApplyName(currentName);
    }

    private void ApplyName(string displayName)
    {
        if (tmpText == null) return;

        bool isOwner = playerData != null && playerData.IsSpawned && playerData.IsOwner;
        tmpText.color = isOwner ? localPlayerColor : otherPlayerColor;

        if (displayName == lastDisplayedName) return;
        lastDisplayedName = displayName;
        tmpText.text = displayName;
    }

    private void LateUpdate()
    {
        if (nameTagRoot == null) return;

        // Pastikan nama tersinkron bila OnNetworkSpawn terjadi setelah Start
        if (playerData != null && playerData.IsSpawned)
        {
            string syncedName = playerData.PlayerName.Value.ToString();
            if (!string.IsNullOrEmpty(syncedName) && syncedName != lastDisplayedName)
            {
                ApplyName(syncedName);
            }
        }

        // Sembunyikan nama saat pemain sedang fase tak terlihat (respawn)
        bool shouldShow = playerMovement == null || !playerMovement.IsRespawning;
        if (tmpRenderer != null && tmpRenderer.enabled != shouldShow)
        {
            tmpRenderer.enabled = shouldShow;
        }

        // Posisikan selalu tegak lurus di atas kepala (sumbu Y dunia) & menghadap penuh ke kamera
        Vector3 worldPos = transform.position + Vector3.up * heightOffset;
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            nameTagRoot.SetPositionAndRotation(worldPos, mainCam.transform.rotation);
        }
        else
        {
            nameTagRoot.position = worldPos;
        }
    }
}
