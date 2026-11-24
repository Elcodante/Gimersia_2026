using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Katalog ScriptableObject daftar variasi karakter pemain.
/// Dirancang agar tim artist/developer dapat dengan mudah menambahkan aset karakter 3D
/// (baik berupa Prefab karakter utuh, maupun Mesh + Material) tanpa menyentuh kode gameplay/multiplayer.
/// </summary>
[CreateAssetMenu(fileName = "PlayerCharacterCatalog", menuName = "Gimersia/Player Character Catalog")]
public class PlayerCharacterCatalog : ScriptableObject
{
    [Serializable]
    public class CharacterVariant
    {
        [Header("Identitas Karakter")]
        public string characterName = "Karakter";

        [Header("Opsi 1: Prefab Model 3D Utuh (Prioritas Utama)")]
        [Tooltip("Masukkan Prefab model 3D karakter di sini. Jika diisi, model default (Tubuh/Kepala Cowok) otomatis disembunyikan.")]
        public GameObject modelPrefab;
        public Vector3 modelLocalPosition = Vector3.zero;
        public Vector3 modelLocalEuler = Vector3.zero;
        public Vector3 modelLocalScale = Vector3.one;

        [Tooltip("Opsional: Nama child bagian badan di dalam modelPrefab agar animasi miring (PlayerSwayAnimator) tetap aktif. Kosong = seluruh model ikut miring.")]
        public string bodyChildName = "";
        [Tooltip("Opsional: Nama child bagian kepala di dalam modelPrefab untuk delay sway kepala.")]
        public string headChildName = "";

        [Header("Opsi 2: Ganti Mesh / Material pada Model Default (Opsional)")]
        [Tooltip("Dipakai hanya jika Model Prefab kosong.")]
        public Mesh bodyMeshOverride;
        public Mesh headMeshOverride;
        public Material bodyMaterialOverride;
        public Material headMaterialOverride;

        [Header("Opsi 3: Warna Pembeda Otomatis (Fallback sebelum Aset Masuk)")]
        [Tooltip("Jika Model Prefab & Material Override kosong, warna ini diterapkan lewat MaterialPropertyBlock (ringan & aman untuk WebGL).")]
        public bool useFallbackTint = true;
        public Color fallbackTintColor = Color.white;
    }

    [SerializeField]
    private List<CharacterVariant> variants = new List<CharacterVariant>
    {
        new CharacterVariant
        {
            characterName = "Petualang Biru",
            useFallbackTint = true,
            fallbackTintColor = new Color(0.24f, 0.58f, 0.92f, 1f)
        },
        new CharacterVariant
        {
            characterName = "Petualang Merah",
            useFallbackTint = true,
            fallbackTintColor = new Color(0.92f, 0.32f, 0.28f, 1f)
        },
        new CharacterVariant
        {
            characterName = "Petualang Hijau",
            useFallbackTint = true,
            fallbackTintColor = new Color(0.26f, 0.78f, 0.44f, 1f)
        },
        new CharacterVariant
        {
            characterName = "Petualang Emas",
            useFallbackTint = true,
            fallbackTintColor = new Color(0.95f, 0.74f, 0.20f, 1f)
        }
    };

    public int Count => variants != null ? variants.Count : 0;

    public CharacterVariant GetVariant(int index)
    {
        if (variants == null || variants.Count == 0) return null;
        int safeIndex = ((index % variants.Count) + variants.Count) % variants.Count;
        return variants[safeIndex];
    }
}

