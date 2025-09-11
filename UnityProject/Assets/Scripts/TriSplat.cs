using UnityEngine;

public struct TriSplat
{
    public Vector3 v0, v1, v2;      // 36 bytes
    public Vector2 uv0, uv1, uv2;   // 24 bytes
    public Vector4 col0, col1, col2;// 48 bytes
    public int matID;               // 4 bytes (which material this triangle came from)
}
