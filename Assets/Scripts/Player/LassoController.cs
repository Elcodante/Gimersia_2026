using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(LineRenderer))]
public class LassoController : NetworkBehaviour
{
    [Header("Pengaturan Laso")]
    [SerializeField] private float lassoRange = 25f;

    private LineRenderer lineRenderer;
    private BoatController boatController;
    private PlayerMovement playerMovement;

    // NetworkVariable menyinkronkan data secara otomatis ke semua pemain
    private NetworkVariable<bool> isLassoActive = new NetworkVariable<bool>(false);
    private NetworkVariable<Vector3> lassoTargetPoint = new NetworkVariable<Vector3>();

    private void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        playerMovement = GetComponent<PlayerMovement>();
        lineRenderer.enabled = false;
    }

    private void Update()
    {
        // 1. UPDATE VISUAL TALI UNTUK SEMUA PEMAIN
        if (isLassoActive.Value)
        {
            lineRenderer.enabled = true;
            lineRenderer.SetPosition(0, transform.position);
            lineRenderer.SetPosition(1, lassoTargetPoint.Value);
        }
        else
        {
            lineRenderer.enabled = false;
        }

        // 2. INPUT HANYA UNTUK PEMILIK KARAKTER
        if (!IsOwner || !Application.isFocused) return;

        // SAAT DIKLIK: Tembak laso
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            TryShootLasso();
        }
        // SAAT DILEPAS: Putus laso
        else if (Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame)
        {
            // Cek apakah tombol dilepas saat laso sedang aktif
            if (isLassoActive.Value)
            {
                StopLassoServerRpc();
            }
        }
    }

    private void TryShootLasso()
    {
        if (Camera.main == null) return;

        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = Camera.main.ScreenPointToRay(mousePos);

        if (Physics.Raycast(ray, out RaycastHit hit, 100f))
        {
            if (hit.collider.CompareTag("Batu") && Vector3.Distance(transform.position, hit.point) <= lassoRange)
            {
                // Putar karakter menghadap batu
                Vector3 lookDir = hit.point - transform.position;
                lookDir.y = 0;
                transform.rotation = Quaternion.LookRotation(lookDir);

                // Beritahu server untuk mulai menarik SECARA TERUS MENERUS
                StartLassoServerRpc(hit.collider.transform.position);
            }
        }
    }

    [ServerRpc]
    private void StartLassoServerRpc(Vector3 target)
    {
        Debug.Log("[LASO SERVER] Telepon dari Client diterima, mulai menarik!");
        if (boatController == null)
        {
            // Cari di parent terlebih dahulu, jika tidak ada cari langsung di scene
            boatController = GetComponentInParent<BoatController>();
            if (boatController == null) boatController = FindFirstObjectByType<BoatController>();
        }

        // Aktifkan variabel jaringan (otomatis memperbarui visual di layar semua orang)
        isLassoActive.Value = true;
        lassoTargetPoint.Value = target;

        // Nyalakan mesin penarik perahu
        if (boatController != null) boatController.StartPull(target);
    }

    [ServerRpc]
    private void StopLassoServerRpc()
    {
        Debug.Log("[LASO SERVER] Perintah berhenti diterima.");
        isLassoActive.Value = false;
        if (boatController == null) boatController = FindFirstObjectByType<BoatController>();

        // Matikan mesin penarik perahu
        if (boatController != null) boatController.StopPull();
    }
}