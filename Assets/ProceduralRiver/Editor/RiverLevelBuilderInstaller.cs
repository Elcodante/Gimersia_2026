#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>Compatibility menu entry. Runtime generation has been superseded by the static baker.</summary>
public static class RiverLevelBuilderInstaller
{
    [MenuItem("Tools/River Procedural/Install in Open Gameplay Scene")]
    public static void ShowStaticWorkflowNotice()
    {
        EditorUtility.DisplayDialog(
            "Runtime Level Builder Deprecated",
            "Runtime generation has been replaced by static scene baking. Use Tools > River Level Baker > Open Static Level Baker.",
            "OK");
    }
}
#endif
