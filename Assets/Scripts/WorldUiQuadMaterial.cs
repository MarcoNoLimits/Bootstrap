using UnityEngine;

/// <summary>
/// World-space UI quads use a project shader so UWP/IL2CPP builds do not strip it (avoids magenta/purple quad).
/// </summary>
public static class WorldUiQuadMaterial
{
    public static Material Create(Texture texture)
    {
        // Prefer Resources material so IL2CPP includes the shader (runtime Shader.Find alone can strip).
        var template = Resources.Load<Material>("Materials/WorldUiQuad");
        if (template != null)
        {
            var mat = new Material(template);
            mat.mainTexture = texture;
            return mat;
        }

        Shader shader = Shader.Find("Bootstrap/WorldUIUnlit");
        if (shader == null)
            shader = Shader.Find("Unlit/Transparent");
        if (shader == null)
            shader = Shader.Find("Unlit/Texture");

        var fallback = new Material(shader);
        fallback.mainTexture = texture;
        return fallback;
    }
}
