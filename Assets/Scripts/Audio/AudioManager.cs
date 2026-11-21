using System;
using System.Collections.Generic;
using UnityEngine;

public class AudioManager : MonoBehaviour
{
    [Serializable]
    public class SoundEntry
    {
        public string id;
        public AudioClip clip;
        [Range(0f, 1f)] public float baseVolume = 1f;
        [Range(0.5f, 1.5f)] public float pitch = 1f;
    }

    public static AudioManager Instance { get; private set; }

    [Header("Audio Sources (Opsional - otomatis dibuat jika kosong)")]
    [SerializeField] private AudioSource bgmSource;
    [SerializeField] private AudioSource sfxSource;

    [Header("Daftar Audio (Array dengan ID)")]
    [SerializeField] private SoundEntry[] bgmList;
    [SerializeField] private SoundEntry[] sfxList;

    [Header("BGM Awal (Opsional)")]
    [SerializeField] private string defaultBgmId;

    private const string MASTER_KEY = "Audio_MasterVolume";
    private const string BGM_KEY = "Audio_BgmVolume";
    private const string SFX_KEY = "Audio_SfxVolume";

    private Dictionary<string, SoundEntry> bgmLookup;
    private Dictionary<string, SoundEntry> sfxLookup;

    private string currentBgmId = string.Empty;
    private float currentBgmBaseVolume = 1f;

    public float MasterVolume { get; private set; } = 1f;
    public float BgmVolume { get; private set; } = 0.8f;
    public float SfxVolume { get; private set; } = 1f;

    // Event untuk memberi tahu semua UI slider yang aktif saat volume berubah
    public event Action OnVolumeChanged;

    private void Awake()
    {
        // Singleton Guard: pastikan hanya ada 1 AudioManager di seluruh scene
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        SetupAudioSources();
        BuildLookups();
        LoadSavedVolumes();
    }

    private void Start()
    {
        if (!string.IsNullOrWhiteSpace(defaultBgmId))
        {
            PlayBGM(defaultBgmId);
        }
    }

    private void SetupAudioSources()
    {
        if (bgmSource == null)
        {
            bgmSource = gameObject.AddComponent<AudioSource>();
        }
        bgmSource.loop = true;
        bgmSource.playOnAwake = false;
        bgmSource.spatialBlend = 0f; // 2D Audio

        if (sfxSource == null)
        {
            sfxSource = gameObject.AddComponent<AudioSource>();
        }
        sfxSource.loop = false;
        sfxSource.playOnAwake = false;
        sfxSource.spatialBlend = 0f; // 2D Audio
    }

    private void BuildLookups()
    {
        // StringComparer.OrdinalIgnoreCase agar pencarian ID tidak sensitif huruf besar/kecil
        bgmLookup = new Dictionary<string, SoundEntry>(StringComparer.OrdinalIgnoreCase);
        if (bgmList != null)
        {
            foreach (SoundEntry entry in bgmList)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.id) || entry.clip == null) continue;
                bgmLookup[entry.id.Trim()] = entry;
            }
        }

        sfxLookup = new Dictionary<string, SoundEntry>(StringComparer.OrdinalIgnoreCase);
        if (sfxList != null)
        {
            foreach (SoundEntry entry in sfxList)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.id) || entry.clip == null) continue;
                sfxLookup[entry.id.Trim()] = entry;
            }
        }
    }

    private void LoadSavedVolumes()
    {
        MasterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(MASTER_KEY, 1f));
        BgmVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(BGM_KEY, 0.8f));
        SfxVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(SFX_KEY, 1f));

        ApplyBgmVolume();
    }

    // --- KONTROL VOLUME DARI SLIDER ---

    public void SetMasterVolume(float value)
    {
        MasterVolume = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(MASTER_KEY, MasterVolume);
        ApplyBgmVolume();
        OnVolumeChanged?.Invoke();
    }

    public void SetBgmVolume(float value)
    {
        BgmVolume = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(BGM_KEY, BgmVolume);
        ApplyBgmVolume();
        OnVolumeChanged?.Invoke();
    }

    public void SetSfxVolume(float value)
    {
        SfxVolume = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(SFX_KEY, SfxVolume);
        OnVolumeChanged?.Invoke();
    }

    private void ApplyBgmVolume()
    {
        if (bgmSource != null)
        {
            bgmSource.volume = currentBgmBaseVolume * BgmVolume * MasterVolume;
        }
    }

    // --- PEMANGGILAN AUDIO BERBASIS ID ---

    public void PlayBGM(string id, bool restartIfSame = false)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        string key = id.Trim();
        if (!bgmLookup.TryGetValue(key, out SoundEntry entry))
        {
            Debug.LogWarning($"[AudioManager] BGM dengan ID '{id}' tidak ditemukan di bgmList.");
            return;
        }

        // Hindari BGM terulang dari awal jika lagu yang sama sudah berputar saat pindah scene
        if (!restartIfSame && currentBgmId.Equals(key, StringComparison.OrdinalIgnoreCase) && bgmSource.isPlaying)
        {
            return;
        }

        currentBgmId = key;
        currentBgmBaseVolume = entry.baseVolume;

        bgmSource.clip = entry.clip;
        bgmSource.pitch = entry.pitch;
        ApplyBgmVolume();
        bgmSource.Play();
    }

    public void StopBGM()
    {
        if (bgmSource != null)
        {
            bgmSource.Stop();
        }
        currentBgmId = string.Empty;
    }

    public void PlaySFX(string id, float randomPitchRange = 0f)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        string key = id.Trim();
        if (!sfxLookup.TryGetValue(key, out SoundEntry entry))
        {
            Debug.LogWarning($"[AudioManager] SFX dengan ID '{id}' tidak ditemukan di sfxList.");
            return;
        }

        float finalVolume = entry.baseVolume * SfxVolume * MasterVolume;
        if (finalVolume <= 0.001f) return;

        sfxSource.pitch = entry.pitch + UnityEngine.Random.Range(-randomPitchRange, randomPitchRange);
        sfxSource.PlayOneShot(entry.clip, finalVolume);
    }

    private void OnApplicationQuit()
    {
        PlayerPrefs.Save();
    }
}