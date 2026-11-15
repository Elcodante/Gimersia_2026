using UnityEngine;
using UnityEngine.Splines;

[ExecuteAlways, RequireComponent(typeof(RiverPath), typeof(MeshFilter), typeof(MeshRenderer))]
public class RiverMeshBuilder : MonoBehaviour
{
    public int lengthSegments = 200;   // makin banyak makin halus di tikungan
    public int widthSegments = 8;

    [ContextMenu("Rebuild")]
    public void Rebuild()
    {
        var river = GetComponent<RiverPath>();
        var spline = GetComponent<SplineContainer>().Spline;
        int n = lengthSegments + 1, m = widthSegments + 1;

        var verts = new Vector3[n * m];
        var uvs = new Vector2[n * m];
        var tris = new int[lengthSegments * widthSegments * 6];

        float dist = 0f; Vector3 prev = default;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)lengthSegments;
            Vector3 c = SplineUtility.EvaluatePosition(spline, t);
            Vector3 tan = SplineUtility.EvaluateTangent(spline, t);
            tan.y = 0f; tan.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, tan);

            if (i > 0) dist += Vector3.Distance(c, prev);
            prev = c;

            float half = river.WidthAt(t) * 0.5f;
            for (int j = 0; j < m; j++)
            {
                float u = j / (float)widthSegments;
                verts[i * m + j] = c + right * Mathf.Lerp(-half, half, u);
                uvs[i * m + j] = new Vector2(u, dist / river.uvTileLength);
            }
        }

        int k = 0;
        for (int i = 0; i < lengthSegments; i++)
            for (int j = 0; j < widthSegments; j++)
            {
                int a = i * m + j, b = a + 1, c2 = a + m, d = c2 + 1;
                tris[k++] = a; tris[k++] = c2; tris[k++] = b;
                tris[k++] = b; tris[k++] = c2; tris[k++] = d;
            }

        var mesh = new Mesh { name = "RiverMesh" };
        mesh.vertices = verts; mesh.uv = uvs; mesh.triangles = tris;
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        GetComponent<MeshFilter>().sharedMesh = mesh;
    }

    void OnValidate() { if (isActiveAndEnabled) Rebuild(); }
}