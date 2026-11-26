using UnityEngine;

/// <summary>
/// Marks an obstacle as a valid or invalid Lasso target.
/// Put this component on the prefab root; child colliders are resolved through GetComponentInParent.
/// </summary>
[DisallowMultipleComponent]
public sealed class HookableTarget : MonoBehaviour
{
    [SerializeField] private bool canBeHooked = true;

    public bool CanBeHooked => canBeHooked;

    public void Configure(bool value)
    {
        canBeHooked = value;
    }
}
