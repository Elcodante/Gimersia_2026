using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem; // Tambahkan namespace Input System baru

public class PlayerMovement : NetworkBehaviour
{
    [Header("Pengaturan Gerak")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float rotationSpeed = 10f;

    private void Update()
    {
        // Jangan jalankan apa pun jika objek ini belum resmi di-spawn di jaringan,
        // atau jika ini bukan milik pemain kita sendiri.
        if (!IsSpawned || !IsOwner) return;

        HandleMovement();
    }

    private void HandleMovement()
    {
        // Membaca keyboard (WASD atau Panah) secara manual lewat Input System baru
        Vector2 inputVector = Vector2.zero;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) inputVector.y += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) inputVector.y -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) inputVector.x += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) inputVector.x -= 1f;
        }

        // Normalkan vektor agar gerak diagonal tidak lebih cepat
        inputVector.Normalize();

        Vector3 moveDirection = new Vector3(inputVector.x, 0f, inputVector.y);

        if (moveDirection.magnitude >= 0.1f)
        {
            // Bergerak
            transform.Translate(moveDirection * moveSpeed * Time.deltaTime, Space.World);

            // Memutar menghadap depan
            Quaternion targetRotation = Quaternion.LookRotation(moveDirection);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
        }
    }
}