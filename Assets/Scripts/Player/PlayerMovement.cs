using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : NetworkBehaviour
{
    [Header("Pengaturan Gerak")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float rotationSpeed = 10f;

    private Rigidbody rb;
    private Rigidbody parentBoatRb;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }
        else
        {
            // Di layar pemain lain, matikan fisika agar murni mengikuti posisi lokal di kapal
            rb.isKinematic = true;
        }
    }

    // Gunakan FixedUpdate karena kita berinteraksi langsung dengan Rigidbody kapal & pemain
    private void FixedUpdate()
    {
        if (!IsSpawned || !IsOwner) return;

        // Cari Rigidbody kapal jika kita sedang berada di atas kapal (sebagai child)
        if (transform.parent != null && parentBoatRb == null)
        {
            parentBoatRb = transform.parent.GetComponent<Rigidbody>();
        }

        HandleMovement();
    }

    private void HandleMovement()
    {
        Vector2 inputVector = Vector2.zero;

        // Hanya baca input jika jendela game sedang aktif/fokus
        if (Application.isFocused && Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) inputVector.y += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) inputVector.y -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) inputVector.x += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) inputVector.x -= 1f;
        }

        inputVector.Normalize();

        // 1. Hitung kecepatan jalan dari input pemain (WASD)
        Vector3 playerInputVelocity = new Vector3(inputVector.x, 0f, inputVector.y) * moveSpeed;

        // 2. Ambil kecepatan gerak kapal saat ini (jika pemain masih di atas kapal)
        Vector3 boatVelocity = Vector3.zero;
        if (parentBoatRb != null)
        {
            boatVelocity = parentBoatRb.linearVelocity;
        }

        // 3. GABUNGKAN kecepatan kapal + kecepatan jalan pemain!
        // Dengan cara ini, saat pemain diam (input = 0), kecepatan karakter = kecepatan kapal (ikut terbawa kapal).
        // Sumbu Y tetap menggunakan gravitasi asli karakter agar tetap menapak dan bisa jatuh ke sungai.
        rb.linearVelocity = new Vector3(
            boatVelocity.x + playerInputVelocity.x,
            rb.linearVelocity.y,
            boatVelocity.z + playerInputVelocity.z
        );

        // 4. Putar karakter menghadap arah jalan jika sedang menekan WASD
        if (playerInputVelocity.sqrMagnitude > 0.01f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(playerInputVelocity.normalized, Vector3.up);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, targetRotation, rotationSpeed * Time.fixedDeltaTime));
        }
    }
}