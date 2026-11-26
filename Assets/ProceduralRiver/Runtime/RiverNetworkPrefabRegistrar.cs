using UnityEngine;

/// <summary>
/// Kept as a compatibility type for scenes that still contain this component.
/// Static, scene-placed NetworkObjects are synchronized by NGO scene management;
/// this component must not dynamically register/add the same prefabs at runtime.
/// </summary>
[DisallowMultipleComponent]
public sealed class RiverNetworkPrefabRegistrar : MonoBehaviour
{
    // Intentionally empty. Static river layout no longer requires runtime prefab registration.
}
