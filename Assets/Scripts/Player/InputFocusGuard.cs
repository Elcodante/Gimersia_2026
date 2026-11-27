using UnityEngine;

/// <summary>
/// Penjaga fokus input yang aman untuk WebGL/itch.io iframe maupun Unity Editor/MPPM.
/// Di Editor dan Standalone tetap memakai Application.isFocused agar input tidak bocor
/// antar jendela pengujian MPPM; di build WebGL, iframe browser terkadang melaporkan
/// isFocused == false setelah klik UI sehingga input tetap diizinkan.
/// </summary>
public static class InputFocusGuard
{
    public static bool CanAcceptInput
    {
        get
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return true;
#else
            return Application.isFocused;
#endif
        }
    }
}

