#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Menu: Tools > River Game > Create Rope Material
/// Membuat Assets/Materials/Rope/RopeTexture.png dan Rope.mat, lalu tinggal drag ke slot Rope Material.
/// </summary>
public static class RopeMaterialCreator
{
    [MenuItem("Tools/River Game/Create Rope Material")]
    private static void Create()
    {
        const string dir = "Assets/Materials/Rope";
        if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
        if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder("Assets/Materials", "Rope");

        Texture2D generated = RopeMaterialFactory.CreateRopeTexture();
        byte[] png = generated.EncodeToPNG();
        Object.DestroyImmediate(generated);

        string texPath = dir + "/RopeTexture.png";
        File.WriteAllBytes(texPath, png);
        AssetDatabase.ImportAsset(texPath);

        var importer = (TextureImporter)AssetImporter.GetAtPath(texPath);
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.anisoLevel = 4;
        importer.sRGBTexture = true;
        importer.SaveAndReimport();

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
        Material mat = RopeMaterialFactory.CreateMaterial(tex);

        string matPath = dir + "/Rope.mat";
        AssetDatabase.DeleteAsset(matPath);
        AssetDatabase.CreateAsset(mat, matPath);
        AssetDatabase.SaveAssets();

        Selection.activeObject = mat;
        EditorGUIUtility.PingObject(mat);
        Debug.Log("[RopeMaterialCreator] Material tali dibuat di " + matPath + ". Drag ke 'Rope Material' di LassoController.");
    }
}
#endif