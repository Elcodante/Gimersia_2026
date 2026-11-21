using UnityEngine;
using UnityEngine.InputSystem;

public class InGameMenuUI : MonoBehaviour
{
    [Header("Referensi UI")]
    [Tooltip("Panel campuran Hotkeys & Pengaturan yang akan disembunyikan saat mulai.")]
    [SerializeField] private GameObject menuPanel;

    [Header("Pengaturan Perilaku")]
    [Tooltip("Jika aktif, pemain tidak bisa jalan WASD saat panel terbuka. Laso selalu diblokir saat menu terbuka.")]
    [SerializeField] private bool lockMovementWhenOpen = true;

    // Status global lokal agar PlayerMovement & LassoController tahu menu sedang terbuka
    public static bool IsMenuOpen { get; private set; }
    public static bool ShouldLockMovement { get; private set; }

    private void Awake()
    {
        SetMenuState(false);
    }

    private void OnDestroy()
    {
        // Reset variabel static agar tidak bocor saat pindah scene
        IsMenuOpen = false;
        ShouldLockMovement = false;
    }

    private void Update()
    {
        if (!Application.isFocused || Keyboard.current == null) return;

        bool escPressed = Keyboard.current.escapeKey.wasPressedThisFrame;
        bool tabPressed = Keyboard.current.tabKey.wasPressedThisFrame;

        if (escPressed || tabPressed)
        {
            ToggleMenu();
        }
    }

    public void ToggleMenu()
    {
        SetMenuState(!IsMenuOpen);
    }

    // Bisa dihubungkan langsung ke tombol "Tutup / Resume" di Inspector
    public void CloseMenu()
    {
        SetMenuState(false);
    }

    private void SetMenuState(bool open)
    {
        IsMenuOpen = open;
        ShouldLockMovement = open && lockMovementWhenOpen;

        if (menuPanel != null)
        {
            menuPanel.SetActive(open);
        }
    }
}