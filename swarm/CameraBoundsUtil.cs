using UnityEngine;

public static class CameraBoundsUtil
{
    // Returns a Bounds on the ground plane (y = groundY) that covers the camera's viewport.
    public static Bounds GroundBoundsFromCamera(Camera cam, float groundY = 0f)
    {
        if (cam == null) return new Bounds(Vector3.zero, Vector3.zero);

        Plane ground = new Plane(Vector3.up, new Vector3(0f, groundY, 0f));

        // 4 viewport corners
        Vector2[] corners = new Vector2[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
        };

        bool any = false;
        Vector3 min = new Vector3(float.PositiveInfinity, groundY, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, groundY, float.NegativeInfinity);

        foreach (var uv in corners)
        {
            Ray r = cam.ViewportPointToRay(new Vector3(uv.x, uv.y, 0f));
            if (ground.Raycast(r, out float t))
            {
                Vector3 p = r.GetPoint(t);
                any = true;
                if (p.x < min.x) min.x = p.x;
                if (p.z < min.z) min.z = p.z;
                if (p.x > max.x) max.x = p.x;
                if (p.z > max.z) max.z = p.z;
            }
        }

        if (!any) return new Bounds(Vector3.zero, Vector3.zero);

        Vector3 center = new Vector3((min.x + max.x) * 0.5f, groundY, (min.z + max.z) * 0.5f);
        Vector3 size = new Vector3(Mathf.Max(0.01f, max.x - min.x), 0.01f, Mathf.Max(0.01f, max.z - min.z));
        return new Bounds(center, size);
    }

    public static Vector3 ClampToXZ(Bounds b, Vector3 p)
    {
        float x = Mathf.Clamp(p.x, b.min.x, b.max.x);
        float z = Mathf.Clamp(p.z, b.min.z, b.max.z);
        return new Vector3(x, p.y, z);
    }
}
