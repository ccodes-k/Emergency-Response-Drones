using System.Collections.Generic;
using UnityEngine;

public class FireSpawner : MonoBehaviour
{
    [Header("Fire")]
    public FireSource firePrefab;
    public Vector2 startIntensityRange = new Vector2(0.25f, 0.9f);
    public int maxConcurrentFires = 3;

    [Header("Timing")]
    public float minSpawnInterval = 4f;
    public float maxSpawnInterval = 10f;

    [Header("Spawn Placement")]
    public string buildingTag = "Building";
    public float fireHeightOffset = 0.2f;

    float _t;
    readonly List<Transform> _buildings = new();

    void Start()
    {
        _t = Random.Range(minSpawnInterval, maxSpawnInterval);
        var found = GameObject.FindGameObjectsWithTag(buildingTag);
        foreach (var go in found) _buildings.Add(go.transform);
        if (_buildings.Count == 0)
            Debug.LogWarning("[FireSpawner] No objects with tag 'Building' found.");
    }

    void Update()
    {
        if (_buildings.Count == 0) return;
        if (FindObjectsByType<FireSource>(FindObjectsSortMode.None).Length >= maxConcurrentFires) return;

        _t -= Time.deltaTime;
        if (_t > 0f) return;

        Transform b = _buildings[Random.Range(0, _buildings.Count)];
        Bounds bounds = new Bounds(b.position, Vector3.one);
        var rend = b.GetComponentInChildren<Renderer>();
        var col = b.GetComponentInChildren<Collider>();
        if (rend) bounds = rend.bounds; else if (col) bounds = col.bounds;

        float topY = bounds.max.y;
        Vector3 pos = new Vector3(bounds.center.x, topY + fireHeightOffset, bounds.center.z);

        if (firePrefab == null) { Debug.LogError("FireSpawner: firePrefab is not assigned."); return; }
        var fire = Instantiate(firePrefab, pos, Quaternion.identity);
        fire.baseIntensity = Random.Range(startIntensityRange.x, startIntensityRange.y);

        _t = Random.Range(minSpawnInterval, maxSpawnInterval);
    }
}
