using UnityEngine;
using UnityEngine.InputSystem; // New Input System

// Top-down / angled strategy camera with:
// - Zoom-to-mouse (keeps the ground point under cursor while zooming)
// - WASD panning
// - Right-mouse drag panning
// Works best with camera tilted ~30–60 degrees down toward groundY.
[RequireComponent(typeof(Camera))]
public class CameraPanZoomController : MonoBehaviour
{
    [Header("Ground")]
    public float groundY = 0f;               // height of your ground plane

    [Header("Pan")]
    public float panSpeed = 60f;             // WASD speed (world units/sec)
    public float dragPanSpeed = 1.2f;        // right-mouse drag sensitivity
    public float inertia = 6f;               // higher = snappier, lower = floaty

    [Header("Zoom")]
    public float minHeight = 12f;            // min camera height above ground
    public float maxHeight = 180f;           // max camera height above ground
    public float zoomSpeed = 2.0f;           // scroll sensitivity (units per tick)
    public float zoomToMouseStrength = 1.0f; // 0..1 (how strongly to pull toward mouse point)

    Camera cam;

    // velocity used for smooth damping
    Vector3 panVelocity;

    void Awake()
    {
        cam = GetComponent<Camera>();
    }

    void Update()
    {
        Vector3 wantedDelta = Vector3.zero;

        // --- 1) WASD pan on XZ plane ---
        Vector2 wasd = ReadWASD();
        if (wasd.sqrMagnitude > 1e-4f)
        {
            // Move in camera's XZ frame
            Vector3 right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            wantedDelta += (right * wasd.x + fwd * wasd.y) * panSpeed * Time.deltaTime;
        }

        // --- 2) Right-mouse drag pan ---
        if (Mouse.current != null && Mouse.current.rightButton.isPressed)
        {
            Vector2 delta = Mouse.current.delta.ReadValue(); // pixels this frame
            // Drag opposite to mouse motion; scale by height for consistent feel
            float h = Mathf.Max(1f, transform.position.y - groundY);
            Vector3 right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            Vector3 dragMove = (-right * delta.x - fwd * delta.y) * (dragPanSpeed * h * 0.001f);
            wantedDelta += dragMove;
        }

        // --- 3) Zoom to mouse ---
        float scrollY = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
        if (Mathf.Abs(scrollY) > 0.01f)
        {
            ZoomTowardMouse(scrollY);
        }

        // --- 4) Smoothly apply pan ---
        // Critically damped-ish smoothing
        if (inertia > 0f)
        {
            panVelocity = Vector3.Lerp(panVelocity, wantedDelta / Time.deltaTime, Time.deltaTime * inertia);
            transform.position += panVelocity * Time.deltaTime;
        }
        else
        {
            transform.position += wantedDelta;
        }

        // --- 5) Clamp height ---
        ClampHeight();

        // --- 6) Keep SwarmManager’s bounds up-to-date (optional safety) ---
        var mgr = SwarmManager.Instance;
        if (mgr != null) { mgr.targetCamera = cam; } // ensures it tracks this camera
    }

    void ZoomTowardMouse(float scrollY)
    {
        // ray from mouse to ground before zoom
        if (cam == null) return;

        Plane ground = new Plane(Vector3.up, new Vector3(0f, groundY, 0f));
        Ray rayBefore = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
        Vector3 hitBefore = transform.position;
        if (ground.Raycast(rayBefore, out float tBefore))
            hitBefore = rayBefore.GetPoint(tBefore);

        // target height change (positive scroll up = zoom in)
        float height = transform.position.y - groundY;
        float targetHeight = Mathf.Clamp(height - scrollY * zoomSpeed, minHeight, maxHeight);

        // compute how far we *should* be from the mouse-ground hit after zoom
        // keep the cursor's ground point relatively stable under the cursor:
        // scale the horizontal vector around hit point by the height ratio
        float ratio = Mathf.Max(0.01f, targetHeight / Mathf.Max(0.01f, height));

        Vector3 pos = transform.position;
        Vector3 flatFromHit = new Vector3(pos.x - hitBefore.x, 0f, pos.z - hitBefore.z);
        Vector3 newFlatFromHit = flatFromHit * ratio;

        // interpolate toward the exact “keep under cursor” position
        Vector3 desiredPos = new Vector3(
            hitBefore.x + newFlatFromHit.x,
            groundY + targetHeight,
            hitBefore.z + newFlatFromHit.z
        );

        // soften with zoomToMouseStrength (0 = plain vertical zoom, 1 = full focus)
        transform.position = Vector3.Lerp(
            new Vector3(pos.x, groundY + targetHeight, pos.z),
            desiredPos,
            Mathf.Clamp01(zoomToMouseStrength)
        );
    }

    void ClampHeight()
    {
        Vector3 p = transform.position;
        p.y = Mathf.Clamp(p.y, groundY + minHeight, groundY + maxHeight);
        transform.position = p;
    }

    static Vector2 ReadWASD()
    {
        if (Keyboard.current == null) return Vector2.zero;
        float x = 0f, y = 0f;
        if (Keyboard.current.aKey.isPressed) x -= 1f;
        if (Keyboard.current.dKey.isPressed) x += 1f;
        if (Keyboard.current.sKey.isPressed) y -= 1f;
        if (Keyboard.current.wKey.isPressed) y += 1f;
        Vector2 v = new Vector2(x, y);
        return v.sqrMagnitude > 1f ? v.normalized : v;
    }
}
