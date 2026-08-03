using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class SwarmManager : MonoBehaviour
{
    // === Singleton so other scripts can reach bounds, etc. ===
    public static SwarmManager Instance { get; private set; }

    [Header("Prefabs")]
    public GameObject scoutDronePrefab;    // assign Drone.prefab
    public GameObject payloadDronePrefab;  // assign Payload_Drone.prefab

    [Header("Stations")]
    public List<Transform> stations = new List<Transform>();

    [Header("Fleet per station")]
    public int scoutsPerStation = 10;
    public int payloadPerStation = 10;

    [Header("Camera Limit")]
    public bool limitToCamera = true;
    public Camera targetCamera;
    public float groundY = 0f;
    [HideInInspector] public Bounds cameraGroundBounds;

    [Header("Dispatch")]
    [Tooltip("Max payload responders PER FIRE you try to assign directly (local launch boost).")]
    public int payloadsPerFire = 4;

    [Header("Behavior tweaks (tuning)")]
    [Tooltip("If false, payload drones will not auto-launch to fires (they stay at base).")]
    public bool payloadAutoLaunch = true;

    [Tooltip("Number of scouts to explicitly dispatch to a fire (place them around the building).")]
    public int scoutsToDispatchPerFire = 6;

    [Tooltip("Multiplier applied to scout SteeringAgent.maxSpeed on spawn.")]
    public float scoutSpeedMultiplier = 1.5f;

    // Internal tracking lists
    readonly List<DroneController> scouts = new();
    readonly List<DroneController> payloads = new();

    // optional bookkeeping of who was assigned to which fire
    readonly Dictionary<FireSource, List<DroneController>> _payloadAssignments = new();

    // ---------- LIFECYCLE ----------
    void Awake()
    {
        Instance = this;

        EventHub.OnFireSpotted += OnFireSpotted;
        EventHub.OnFireExtinguished += OnFireExtinguished;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;

        EventHub.OnFireSpotted -= OnFireSpotted;
        EventHub.OnFireExtinguished -= OnFireExtinguished;
    }

    void Start()
    {
        // Spawn fleets at each station
        foreach (var st in stations)
        {
            var pos = st.position + Vector3.up * 1.5f;

            // Spawn scouts (scatter them out)
            for (int i = 0; i < scoutsPerStation; i++)
            {
                var go = Instantiate(scoutDronePrefab, pos, Quaternion.identity);
                var d = go.GetComponent<DroneController>();
                d.role = DroneController.DroneRole.Scout;
                d.isPayloadDrone = false;
                d.SpawnAt(pos);

                var steer = go.GetComponent<SteeringAgent>();
                if (steer != null)
                {
                    steer.maxSpeed *= scoutSpeedMultiplier;
                    steer.maxForce *= scoutSpeedMultiplier;
                }

                float ang = (360f / Mathf.Max(1, scoutsPerStation)) * i + Random.Range(-8f, 8f);
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * Vector3.forward;
                d.ScatterLaunch(st.position, dir, 70f, 22f);

                scouts.Add(d);
            }

            // Spawn payload drones (idle on pad)
            for (int i = 0; i < payloadPerStation; i++)
            {
                var go = Instantiate(payloadDronePrefab, pos, Quaternion.identity);
                var d = go.GetComponent<DroneController>();
                d.role = DroneController.DroneRole.Payload;
                d.isPayloadDrone = true;
                d.SpawnAndIdleAt(pos);
                payloads.Add(d);
            }
        }
    }

    void Update()
    {
        // update cameraGroundBounds so drones can clamp movement to visible area
        if (limitToCamera && targetCamera != null)
            cameraGroundBounds = CameraBoundsUtil.GroundBoundsFromCamera(targetCamera, groundY);
    }

    // ---------- FIRE SPOTTED ----------
    void OnFireSpotted(FireSource fire, DroneController who, float estimate)
    {
        if (fire == null) return;

        // 1. PAYLOAD AUTOLAUNCH (closest payload drones take off)
        if (payloadAutoLaunch)
        {
            var candidates = payloads
                .Where(p => p != null &&
                            p.state != DroneController.DroneState.Delivering &&
                            p.state != DroneController.DroneState.Responding)
                .OrderBy(p => (p.transform.position - fire.transform.position).sqrMagnitude)
                .ToList();

            if (!_payloadAssignments.ContainsKey(fire))
                _payloadAssignments[fire] = new List<DroneController>();
            var assignedList = _payloadAssignments[fire];

            int toTake = Mathf.Min(payloadsPerFire - assignedList.Count, candidates.Count);
            for (int i = 0; i < toTake; i++)
            {
                var p = candidates[i];
                assignedList.Add(p);

                // Assign orbit target. We KEEP slotting so they spread a little.
                int slotIndex = assignedList.Count - 1;
                p.SetOrbitTarget(fire, slotIndex, payloadsPerFire);
                // NOTE: we do NOT force their currentFire or state here, because
                // SetOrbitTarget already changes state to Delivering.
            }
        }

        // 2. SEND A SCOUT RING (visual perimeter around fire/building)
        var scoutTeam = scouts
            .Where(s => s != null && s.state == DroneController.DroneState.Patrol)
            .OrderBy(s => (s.transform.position - fire.transform.position).sqrMagnitude)
            .Take(scoutsToDispatchPerFire)
            .ToList();

        float baseRadius = 12f;
        for (int i = 0; i < scoutTeam.Count; i++)
        {
            var s = scoutTeam[i];
            float angle = (360f / Mathf.Max(1, scoutTeam.Count)) * i + Random.Range(-12f, 12f);
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            Vector3 target = fire.transform.position + dir * baseRadius;

            s.AssignTarget(target);
            s.state = DroneController.DroneState.Responding;

            // force scouts to hover above the roofline
            float alt = Mathf.Clamp(
                fire.transform.position.y + s.attackOffset,
                s.minPatrolAlt,
                s.maxPatrolAlt
            );
            var steer = s.GetComponent<SteeringAgent>();
            if (steer != null)
                steer.desiredAltitude = alt;
        }

        // 3. GLOBAL BROADCAST  <-- this is the swarm trigger
        EventHub.RespondToFire(fire);
    }

    // ---------- FIRE EXTINGUISHED ----------
    void OnFireExtinguished(FireSource fire)
    {
        if (_payloadAssignments.ContainsKey(fire))
            _payloadAssignments.Remove(fire);

        // Gently tell anyone mid-response to chill (optional)
        foreach (var p in payloads)
        {
            if (p != null && p.currentFire == fire)
                p.ReturnAndIdle();
        }

        foreach (var s in scouts)
        {
            if (s != null && s.state == DroneController.DroneState.Responding)
                s.ReturnToPatrol();
        }
    }
}
