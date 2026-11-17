using Unity.Netcode;
using UnityEngine;

public class BoatController : NetworkBehaviour
{
    private Rigidbody rb;
    private bool isPulled = false;
    private Vector3 pullTarget;

    [SerializeField] private float pullForceMultiplier = 15f;

    private void Start()
    {
        rb = GetComponent<Rigidbody>();
    }

    public void StartPull(Vector3 target)
    {
        if (!IsServer) return;
        pullTarget = target;
        isPulled = true;
    }

    public void StopPull()
    {
        if (!IsServer) return;
        isPulled = false;
    }

    private void FixedUpdate()
    {
        if (IsServer && isPulled)
        {
            Vector3 direction = (pullTarget - transform.position).normalized;
            direction.y = 0;

            Vector3 appliedForce = direction * pullForceMultiplier;
            rb.AddForce(appliedForce, ForceMode.Acceleration);

            // 1. TEKS PELACAK: Mencetak status tarikan ke tab Console
            Debug.Log($"[KAPAL DITARIK] Arah: {direction} | Total Gaya: {appliedForce.magnitude} | Kecepatan Gerak: {rb.linearVelocity.magnitude}");

            // 2. GARIS VISUAL: Menggambar panah merah di tab 'Scene' (bukan di tab Game)
            Debug.DrawRay(transform.position + Vector3.up, direction * 5f, Color.red);
        }
    }
}