using UnityEngine;

[RequireComponent(typeof(SphereCollider))]
public class DroneSensor : MonoBehaviour
{
    [Header("Sensor Settings")]
    public float baseRange = 18f;
    public float intensityRangeBoost = 20f;
    public float reportCooldown = 2f;

    float _cooldown;
    SphereCollider _col;

    void Awake()
    {
        _col = GetComponent<SphereCollider>();
        _col.isTrigger = true;
        _col.radius = baseRange;
    }

    void OnValidate()
    {
        var sc = GetComponent<SphereCollider>();
        if (sc != null) { sc.isTrigger = true; if (sc.radius < baseRange) sc.radius = baseRange; }
    }

    void Update() { if (_cooldown > 0f) _cooldown -= Time.deltaTime; }

    void OnTriggerStay(Collider other)
    {
        if (_cooldown > 0f) return;
        if (!other.CompareTag("Fire")) return;

        var fire = other.GetComponent<FireSource>();
        var drone = GetComponent<DroneController>();
        if (!fire || !drone) return;

        float beacon = fire.BeaconRadius;
        float planar = Vector2.Distance(
            new Vector2(transform.position.x, transform.position.z),
            new Vector2(fire.transform.position.x, fire.transform.position.z)
        );
        if (planar > beacon) return;

        float proximity = 1f - Mathf.Clamp01(planar / beacon);
        float estimate = Mathf.Clamp01(fire.Intensity * (0.6f + 0.4f * proximity));

        EventHub.FireSpotted(fire, drone, estimate);
        _cooldown = reportCooldown;
    }
}
