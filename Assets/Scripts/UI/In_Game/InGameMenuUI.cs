using UnityEngine;
using UnityEngine.InputSystem;

public class InGameMenuUI : MonoBehaviour
{
    [Header("Referensi UI")]
    [SerializeField] private GameObject menuPanel;
    [SerializeField] private JournalMenuAnimator journalAnimator;

    [Header("Pengaturan Perilaku")]
    [SerializeField] private bool lockMovementWhenOpen = true;

    public static bool IsMenuOpen { get; private set; }
    public static bool ShouldLockMovement { get; private set; }

    private void Awake()
    {
        IsMenuOpen = false;
        ShouldLockMovement = false;

        if (journalAnimator != null)
        {
            journalAnimator.SetClosedImmediate();
        }
        else if (menuPanel != null)
        {
            menuPanel.SetActive(false);
        }
    }

    private void OnDestroy()
    {
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

    public void CloseMenu()
    {
        SetMenuState(false);
    }

    private void SetMenuState(bool open)
    {
        IsMenuOpen = open;
        ShouldLockMovement = open && lockMovementWhenOpen;

        if (journalAnimator != null)
        {
            if (open) journalAnimator.PlayOpen();
            else journalAnimator.PlayClose();
        }
        else if (menuPanel != null)
        {
            menuPanel.SetActive(open);
        }
    }
}