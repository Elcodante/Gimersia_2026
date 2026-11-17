using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : NetworkBehaviour
{
    [Header("Pengaturan Gerak")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float rotationSpeed = 10f;

    private Rigidbody rb;
    private bool isFallingOff = false;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.interpolation = RigidbodyInterpolation.None;
    }

    public override void OnNetworkSpawn()
    {
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.None;

        BoatController boat = FindFirstObjectByType<BoatController>();

        // PENTING: Matikan tabrakan fisik antara kapsul pemain dan collider kapal
        // agar kapsul pemain yang kinematic tidak menahan/memutar kapal saat kapal bergerak.
        if (boat != null)
        {
            IgnoreCollisionWithBoat(boat);

            if (IsServer && transform.parent == null)
            {
                NetworkObject.TrySetParent(boat.NetworkObject, true);
            }
        }
    }

    private void IgnoreCollisionWithBoat(BoatController boat)
    {
        Collider[] playerColliders = GetComponentsInChildren<Collider>();
        Collider[] boatColliders = boat.GetComponentsInChildren<Collider>();

        foreach (Collider pCol in playerColliders)
        {
            foreach (Collider bCol in boatColliders)
            {
                Physics.IgnoreCollision(pCol, bCol, true);
            }
        }
    }

    private void Update()
    {
        if (!IsSpawned || !IsOwner || !Application.isFocused) return;

        HandleMovement();
        CheckIfStillOnBoat();
    }

    private void HandleMovement()
    {
        Vector2 inputVector = Vector2.zero;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) inputVector.y += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) inputVector.y -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) inputVector.x += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) inputVector.x -= 1f;
        }

        inputVector.Normalize();
        Vector3 worldMoveDir = new Vector3(inputVector.x, 0f, inputVector.y);

        if (worldMoveDir.magnitude >= 0.1f)
        {
            if (transform.parent != null)
            {
                // Ubah arah gerak layar (World) menjadi arah lokal kapal
                Vector3 localMoveDir = transform.parent.InverseTransformDirection(worldMoveDir);
                transform.localPosition += localMoveDir * moveSpeed * Time.deltaTime;
            }
            else
            {
                transform.position += worldMoveDir * moveSpeed * Time.deltaTime;
            }

            Quaternion targetRotation = Quaternion.LookRotation(worldMoveDir, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
        }
    }

    private void CheckIfStillOnBoat()
    {
        if (isFallingOff || transform.parent == null) return;

        if (!IsBoatBelow())
        {
            isFallingOff = true;
            FallOffBoatServerRpc();
        }
    }

    private bool IsBoatBelow()
    {
        // Physics.RaycastAll tetap mendeteksi collider kapal meskipun Physics.IgnoreCollision aktif
        RaycastHit[] hits = Physics.RaycastAll(transform.position + Vector3.up * 0.5f, Vector3.down, 3f);
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform))
                continue;

            if (hit.collider.GetComponentInParent<BoatController>() != null)
                return true;
        }
        return false;
    }

    [ServerRpc]
    private void FallOffBoatServerRpc()
    {
        NetworkObject.TryRemoveParent(true);
        EnableGravityClientRpc();
    }

    [ClientRpc]
    private void EnableGravityClientRpc()
    {
        rb.isKinematic = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
    }
}