using UnityEngine;
using UnityEngine.UI;

public class AudioSettingsUI : MonoBehaviour
{
    [Header("Referensi Slider (Kosongkan yang tidak dipakai)")]
    [SerializeField] private Slider masterSlider;
    [SerializeField] private Slider bgmSlider;
    [SerializeField] private Slider sfxSlider;

    private void OnEnable()
    {
        BindListeners();
        SyncSlidersFromManager();

        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.OnVolumeChanged += SyncSlidersFromManager;
        }
    }

    private void Start()
    {
        // Jaga-jaga jika AudioManager baru selesai Awake pada frame pertama
        SyncSlidersFromManager();
    }

    private void OnDisable()
    {
        UnbindListeners();

        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.OnVolumeChanged -= SyncSlidersFromManager;
        }

        PlayerPrefs.Save();
    }

    private void BindListeners()
    {
        if (masterSlider != null) masterSlider.onValueChanged.AddListener(HandleMasterChanged);
        if (bgmSlider != null) bgmSlider.onValueChanged.AddListener(HandleBgmChanged);
        if (sfxSlider != null) sfxSlider.onValueChanged.AddListener(HandleSfxChanged);
    }

    private void UnbindListeners()
    {
        if (masterSlider != null) masterSlider.onValueChanged.RemoveListener(HandleMasterChanged);
        if (bgmSlider != null) bgmSlider.onValueChanged.RemoveListener(HandleBgmChanged);
        if (sfxSlider != null) sfxSlider.onValueChanged.RemoveListener(HandleSfxChanged);
    }

    private void SyncSlidersFromManager()
    {
        if (AudioManager.Instance == null) return;

        if (masterSlider != null)
        {
            masterSlider.minValue = 0f;
            masterSlider.maxValue = 1f;
            masterSlider.SetValueWithoutNotify(AudioManager.Instance.MasterVolume);
        }

        if (bgmSlider != null)
        {
            bgmSlider.minValue = 0f;
            bgmSlider.maxValue = 1f;
            bgmSlider.SetValueWithoutNotify(AudioManager.Instance.BgmVolume);
        }

        if (sfxSlider != null)
        {
            sfxSlider.minValue = 0f;
            sfxSlider.maxValue = 1f;
            sfxSlider.SetValueWithoutNotify(AudioManager.Instance.SfxVolume);
        }
    }

    private void HandleMasterChanged(float value)
    {
        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.SetMasterVolume(value);
        }
    }

    private void HandleBgmChanged(float value)
    {
        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.SetBgmVolume(value);
        }
    }

    private void HandleSfxChanged(float value)
    {
        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.SetSfxVolume(value);
        }
    }
}