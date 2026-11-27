using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ReturnToMenuController : MonoBehaviour
{
    [Header("Pengaturan Scene")]
    [SerializeField] private string mainMenuSceneName = "Main_Menu";

    [Header("Pembersihan NetworkManager")]
    [Tooltip("Centang jika objek NetworkManager dibuat di dalam scene Main_Menu agar tidak bentrok/duplikat saat kembali.")]
    [SerializeField] private bool destroyNetworkManagerOnLeave = true;

    private bool isLeaving = false;

    private void OnEnable()
    {
        if (NetworkManager.Singleton != null)
        {
            // Terpanggil otomatis jika koneksi terputus (misalnya Host menutup sesi)
            NetworkManager.Singleton.OnClientStopped += HandleClientStopped;
        }
    }

    private void OnDisable()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientStopped -= HandleClientStopped;
        }
    }

    /// <summary>
    /// Hubungkan fungsi ini ke event OnClick() pada tombol "Kembali ke Main Menu" di panel Settings.
    /// </summary>
    public void ReturnToMainMenu()
    {
        if (isLeaving) return;
        StartCoroutine(LeaveSessionAndLoadMenuRoutine());
    }

    private void HandleClientStopped(bool wasHost)
    {
        // Jika bukan pemain ini yang menekan tombol keluar (berarti Host keluar / terputus dari server),
        // arahkan pemain ini kembali ke Main Menu dengan rapi.
        if (!isLeaving)
        {
            StartCoroutine(LeaveSessionAndLoadMenuRoutine());
        }
    }

    private IEnumerator LeaveSessionAndLoadMenuRoutine()
    {
        isLeaving = true;

        // Pastikan kursor tetap bebas dan tidak terkunci saat masuk Main Menu
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // 1. Jika sedang dalam sesi Multiplayer (Host maupun Client), hentikan jaringan secara lokal
        if (NetworkManager.Singleton != null)
        {
            // Lepas listener agar tidak memicu callback ganda saat Shutdown() dipanggil
            NetworkManager.Singleton.OnClientStopped -= HandleClientStopped;

            if (NetworkManager.Singleton.IsListening)
            {
                // Jangan gunakan NetworkManager.SceneManager.LoadScene di sini!
                // Shutdown() hanya memutus pemain ini (jika Client) dan membiarkan pemain lain tetap bermain.
                NetworkManager.Singleton.Shutdown();
            }

            // Tunggu sampai proses Shutdown selesai total agar transport/WebSocket tertutup bersih
            yield return new WaitUntil(() =>
                NetworkManager.Singleton == null || !NetworkManager.Singleton.ShutdownInProgress
            );

            // Hancurkan NetworkManager lama agar saat kembali ke Main_Menu,
            // NetworkManager baru di Main_Menu bisa bekerja tanpa bentrok referensi UI.
            if (destroyNetworkManagerOnLeave && NetworkManager.Singleton != null)
            {
                Destroy(NetworkManager.Singleton.gameObject);
                yield return null;
            }
        }

        // Bersihkan daftar pemain statis agar tidak menyisakan referensi stale antar sesi di tab browser yang sama
        NetworkPlayerData.ConnectedPlayers.Clear();

        // 2. Pindah ke Main Menu secara lokal (hanya untuk pemain ini)
        if (SceneFadeTransition.Instance != null)
        {
            SceneFadeTransition.Instance.TransitionToScene(mainMenuSceneName);
        }
        else
        {
            SceneManager.LoadScene(mainMenuSceneName);
        }
    }
}