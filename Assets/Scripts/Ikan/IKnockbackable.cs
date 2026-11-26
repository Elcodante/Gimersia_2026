using UnityEngine;

/// <summary>
/// Kontrak OOP untuk objek yang dapat dihempaskan oleh serangan ikan ke air.
/// Memisahkan sistem ikan dari detail internal PlayerMovement.
/// </summary>
public interface IKnockbackable
{
    bool CanBeKnockedBack { get; }
    Vector3 KnockbackPosition { get; }
    void ApplyFishKnockback(Vector3 knockbackVelocity);
}
