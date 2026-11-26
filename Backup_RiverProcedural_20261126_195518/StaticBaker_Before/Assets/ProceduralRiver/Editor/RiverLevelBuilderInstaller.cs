#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class RiverLevelBuilderInstaller
{
    private static readonly string[] LegacyLevelRoots =
    {
        "LD_Zone1_Easy",
        "LD_Zone2_Medium",
        "LD_Zone3_Hard",
        "LD_Zone4_Finale"
    };

    [MenuItem("Tools/River Procedural/Install in Open Gameplay Scene")]
    public static void InstallInOpenScene()
    {
        Scene activeScene = SceneManager.GetActiveScene();
        if (!activeScene.IsValid() || !activeScene.isLoaded)
        {
            EditorUtility.DisplayDialog("River Procedural", "Tidak ada gameplay scene yang sedang terbuka.", "OK");
            return;
        }

        RiverPath river = RiverPath.Main;
        if (river == null)
        {
            EditorUtility.DisplayDialog("River Procedural", "RiverPath valid tidak ditemukan. Pastikan scene memiliki RiverPath dengan SplineContainer minimal 2 knot.", "OK");
            return;
        }

        RiverLevelBuilder builder = river.GetComponent<RiverLevelBuilder>();
        if (builder == null)
        {
            Undo.AddComponent<RiverLevelBuilder>(river.gameObject);
            builder = river.GetComponent<RiverLevelBuilder>();
        }

        RiverFinishGoal finish = Object.FindFirstObjectByType<RiverFinishGoal>();
        BoatController boat = Object.FindFirstObjectByType<BoatController>();

        SerializedObject serializedBuilder = new SerializedObject(builder);
        SetReference(serializedBuilder, "river", river);
        SetReference(serializedBuilder, "finishGoal", finish);
        SetReference(serializedBuilder, "boat", boat);
        serializedBuilder.ApplyModifiedProperties();

        int removedGroups = RemoveLegacyLevelRoots(activeScene);
        EditorUtility.SetDirty(builder);
        EditorSceneManager.MarkSceneDirty(activeScene);

        List<string> missingTags = new List<string>();
        EnsureTag("Batu", missingTags);
        EnsureTag("Sampah", missingTags);

        string message = $"RiverLevelBuilder terpasang pada '{river.name}'.\n" +
                         $"FinishGoal: {(finish != null ? finish.name : "TIDAK DITEMUKAN")}\n" +
                         $"BoatController: {(boat != null ? boat.name : "TIDAK DITEMUKAN")}\n" +
                         $"Legacy level groups dihapus: {removedGroups}\n\n" +
                         "Simpan scene setelah memeriksa perubahan. Network prefab hookable trash diregistrasikan otomatis oleh RiverNetworkPrefabRegistrar saat runtime.";

        if (missingTags.Count > 0)
        {
            message += "\n\nTAG YANG BELUM ADA: " + string.Join(", ", missingTags) +
                       ". Tambahkan melalui Project Settings > Tags and Layers sebelum Play.";
        }

        EditorUtility.DisplayDialog("River Procedural Installed", message, "OK");
        Debug.Log("[RiverLevelBuilderInstaller] " + message);
    }

    private static void EnsureTag(string tagName, List<string> failedTags)
    {
        string[] tags = InternalEditorUtility.tags;
        if (System.Array.IndexOf(tags, tagName) >= 0) return;

        try
        {
            InternalEditorUtility.AddTag(tagName);
        }
        catch (System.Exception exception)
        {
            failedTags.Add(tagName);
            Debug.LogError($"[RiverLevelBuilderInstaller] Gagal menambahkan tag '{tagName}': {exception.Message}");
        }
    }

    private static void SetReference(SerializedObject serializedObject, string propertyName, Object value)
    {
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        if (property != null) property.objectReferenceValue = value;
    }

    private static int RemoveLegacyLevelRoots(Scene scene)
    {
        int removed = 0;
        GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
        for (int i = 0; i < allObjects.Length; i++)
        {
            GameObject go = allObjects[i];
            if (go == null || go.scene != scene) continue;

            bool isLegacy = false;
            for (int n = 0; n < LegacyLevelRoots.Length; n++)
            {
                if (go.name == LegacyLevelRoots[n])
                {
                    isLegacy = true;
                    break;
                }
            }

            if (!isLegacy) continue;
            Undo.DestroyObjectImmediate(go);
            removed++;
        }

        return removed;
    }
}
#endif
