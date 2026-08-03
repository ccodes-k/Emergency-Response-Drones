using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(SphereCollider))]
public class FireSource : MonoBehaviour
{
    [Header("Intensity [0..1]")]
    [Range(0f, 1f)] public float baseIntensity = 0.25f;
    public float maxIntensity = 1f;

    [Header("Growth when unattended")]
    public float growRate = 0.06f;             // slow growth while ignored
    public float fastGrowRate = 0.18f;         // faster growth after ramp time
    public float unattendedRampSeconds = 5f;   // after this, use fastGrowRate
    [Tooltip("Multiply unattended growth (suppression unchanged).")]
    public float growthSpeed = 3f;             // speed-up for simulation

    [Header("Suppression (planar check)")]
    public int dronesNeededToSuppress = 4;
    public float suppressRatePerDrone = 0.12f;
    public float suppressionRadiusXZ = 12f;    // planar radius
    public float scanHeight = 40f;             // overlap sphere height cap
    public float extinguishThreshold = 0.02f;

    [Header("Concentric Ring Visuals (no prefab needed)")]
    public bool useConcentricRings = true;
    [Tooltip("How many rings at intensity = 0..1")]
    public int minRings = 1;
    public int maxRings = 10;
    [Tooltip("Ring radius range (world units) at intensity = 0..1")]
    public Vector2 ringRadiusRange = new Vector2(1.5f, 8f);
    [Tooltip("Ring line width range at intensity = 0..1")]
    public Vector2 ringWidthRange = new Vector2(0.03f, 0.15f);
    [Tooltip("Vertical offset so the rings float above the ground")]
    public float ringYOffset = 0.1f;
    [Tooltip("Segments used to draw each ring (higher = smoother circle)")]
    public int ringSegments = 64;
    [ColorUsage(true, true)] public Color ringColor = new Color(1f, 0.4f, 0f, 0.8f); // orange w/ alpha
    [Tooltip("Fade inner rings slightly for depth")]
    public bool fadeInnerRings = true;

    [Header("External Spraying")]
    public float externalSuppressionMultiplier = 1f;

    // === For DroneSensor / scanning logic ===
    [Header("Beacon (sensor)")]
    public float baseBeaconRadius = 18f;
    public float extraBeaconPerIntensity = 20f;

    // --- Runtime state ---
    float _intensity;
    float _unattendedTimer;
    float _externalSuppressionAccum;

    public int CurrentDronesInRange { get; private set; }
    public float ScoutRingRadius => Mathf.Max(0.8f * suppressionRadiusXZ, 3f);
    public float PayloadRingRadius => Mathf.Max(0.3f * suppressionRadiusXZ, 1.5f);

    // Required by DroneSensor
    public float Intensity => _intensity;
    public float BeaconRadius => baseBeaconRadius + extraBeaconPerIntensity * Mathf.Clamp01(_intensity);

    // Back-compat helpers expected by other scripts
    public int GetCurrentDronesInRange() => CurrentDronesInRange;
    public int GetDronesNeededToSuppress() => dronesNeededToSuppress;
    public float GetScoutRing() => ScoutRingRadius;
    public float GetPayloadRing() => PayloadRingRadius;
    public bool NeedsMorePayloadDrones() => CurrentDronesInRange < dronesNeededToSuppress;

    // --- Ring visuals ---
    readonly List<LineRenderer> _rings = new();
    Transform _ringRoot;

    void Awake()
    {
        _intensity = Mathf.Clamp01(baseIntensity);
        EnsureRingRoot();
        RebuildRings(forceAll: true);
        UpdateRingsVisual(); // apply initial visuals
    }

    void Update()
    {
        // how many payload drones are close enough on the XZ plane
        CurrentDronesInRange = CountPayloadDronesPlanar();

        if (CurrentDronesInRange >= dronesNeededToSuppress)
        {
            _unattendedTimer = 0f;
            _intensity -= suppressRatePerDrone * CurrentDronesInRange * Time.deltaTime;
        }
        else
        {
            _unattendedTimer += (CurrentDronesInRange == 0 ? Time.deltaTime : 0f);
            float rate = (_unattendedTimer >= unattendedRampSeconds) ? fastGrowRate : growRate;
            _intensity += rate * growthSpeed * Time.deltaTime;
        }

        // external spray suppression
        if (_externalSuppressionAccum > 0f)
        {
            _intensity -= externalSuppressionMultiplier * _externalSuppressionAccum * Time.deltaTime;
            _externalSuppressionAccum = 0f;
        }

        _intensity = Mathf.Clamp(_intensity, 0f, maxIntensity);

        // Concentric circles update
        if (useConcentricRings)
        {
            RebuildRings();       // ensure ring count matches intensity
            UpdateRingsVisual();  // size, width, color
        }

        // Extinguish if weak
        if (_intensity <= extinguishThreshold)
        {
            EventHub.FireExtinguished(this);
            Destroy(gameObject);
        }
    }

    // Called by PayLoadSprayer each frame while spraying
    public void AddExternalSuppression(float perSecond)
    {
        _externalSuppressionAccum += Mathf.Max(0f, perSecond);
    }

    // === Internals ===
    int CountPayloadDronesPlanar()
    {
        float radius = Mathf.Max(suppressionRadiusXZ, 1f);
        var hits = Physics.OverlapSphere(transform.position, Mathf.Max(radius, scanHeight));
        Vector2 p = new Vector2(transform.position.x, transform.position.z);
        int count = 0;

        foreach (var h in hits)
        {
            if (!h || !h.CompareTag("Drone")) continue;

            var ctrl = h.GetComponent<DroneController>();
            if (ctrl == null || !ctrl.isPayloadDrone) continue;

            Vector3 hp = h.transform.position;
            // planar distance check
            if (Vector2.Distance(p, new Vector2(hp.x, hp.z)) <= suppressionRadiusXZ)
                count++;
        }
        return count;
    }

    void EnsureRingRoot()
    {
        if (_ringRoot == null)
        {
            _ringRoot = new GameObject("FireRings").transform;
            _ringRoot.SetParent(transform, false);
            _ringRoot.localPosition = Vector3.zero;
        }
    }

    void RebuildRings(bool forceAll = false)
    {
        if (!useConcentricRings) return;
        EnsureRingRoot();

        int target = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Lerp(minRings, maxRings, Mathf.Clamp01(_intensity / Mathf.Max(0.0001f, maxIntensity)))),
            minRings, maxRings);

        // grow
        while (_rings.Count < target)
        {
            _rings.Add(CreateRing($"Ring_{_rings.Count}"));
        }
        // shrink
        while (_rings.Count > target)
        {
            var last = _rings[_rings.Count - 1];
            if (last) Destroy(last.gameObject);
            _rings.RemoveAt(_rings.Count - 1);
        }

        if (forceAll && _rings.Count == 0 && target > 0)
        {
            _rings.Add(CreateRing("Ring_0"));
        }
    }

    LineRenderer CreateRing(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_ringRoot, false);
        go.transform.localPosition = new Vector3(0f, ringYOffset, 0f);

        var lr = go.AddComponent<LineRenderer>();
        lr.loop = true;
        lr.useWorldSpace = false;
        lr.textureMode = LineTextureMode.Stretch;
        lr.material = new Material(Shader.Find("Sprites/Default")); // simple unlit
        lr.positionCount = Mathf.Max(3, ringSegments);
        // initialize circle points (unit circle for now; we scale radius later)
        SetCirclePositions(lr, 1f);
        return lr;
    }

    void UpdateRingsVisual()
    {
        if (!useConcentricRings) return;

        // Compute overall radius/width for current intensity
        float t = Mathf.Clamp01(_intensity / Mathf.Max(0.0001f, maxIntensity));
        float maxR = Mathf.Lerp(ringRadiusRange.x, ringRadiusRange.y, t);
        float baseWidth = Mathf.Lerp(ringWidthRange.x, ringWidthRange.y, t);

        int n = _rings.Count;
        for (int i = 0; i < n; i++)
        {
            var lr = _rings[i];
            if (!lr) continue;

            // inner -> outer radius distribution
            float u = (n <= 1) ? 1f : (float)(i + 1) / n; // 1/n .. 1
            float radius = Mathf.Lerp(ringRadiusRange.x, maxR, u);

            // set circle points
            SetCirclePositions(lr, radius);

            // width: thinner for inner rings, thicker outer
            float w = Mathf.Lerp(baseWidth * 0.6f, baseWidth, u);
            lr.startWidth = w;
            lr.endWidth = w;

            // color: fade inner rings slightly if enabled
            var col = ringColor;
            if (fadeInnerRings)
            {
                float a = Mathf.Lerp(0.35f, col.a, u);
                col.a = a;
            }
            lr.startColor = col;
            lr.endColor = col;
        }
    }

    void SetCirclePositions(LineRenderer lr, float radius)
    {
        int segs = Mathf.Max(3, ringSegments);
        if (lr.positionCount != segs) lr.positionCount = segs;

        float step = 2f * Mathf.PI / segs;
        for (int i = 0; i < segs; i++)
        {
            float ang = i * step;
            float x = Mathf.Cos(ang) * radius;
            float z = Mathf.Sin(ang) * radius;
            lr.SetPosition(i, new Vector3(x, 0f, z));
        }
    }

    // Optional gizmos so you can see scout/payload/beacon in Scene view
    void OnDrawGizmosSelected()
    {
        // payload ring
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, PayloadRingRadius);
        // scout ring
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, ScoutRingRadius);
        // beacon
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.35f);
        float i = Application.isPlaying ? _intensity : baseIntensity;
        float br = baseBeaconRadius + extraBeaconPerIntensity * Mathf.Clamp01(i);
        Gizmos.DrawWireSphere(transform.position, br);
    }
}
