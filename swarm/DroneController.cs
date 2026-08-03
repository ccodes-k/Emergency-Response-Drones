using UnityEngine;

[RequireComponent(typeof(SteeringAgent), typeof(Rigidbody))]
public class DroneController : MonoBehaviour
{
    public enum DroneRole { Scout, Payload }
    public enum DroneState { Idle, Patrol, Responding, Delivering, Returning }

    [Header("Role & State")]
    public DroneRole role = DroneRole.Scout;
    public DroneState state = DroneState.Idle;

    // orbit slot distribution
    int _orbitSlot = -1;
    int _orbitTotal = 1;

    [Header("Patrol Altitude Strategy")]
    public bool followBuildingHeights = true;
    public float roofScanRadius = 80f;
    public float roofOffset = 10f;
    public float minPatrolAlt = 12f;
    public float maxPatrolAlt = 150f;

    [Header("Fire Response Altitude")]
    public float attackOffset = 6f;

    [Header("Payload/Fire")]
    public bool isPayloadDrone = false;                 // TRUE on Payload_Drone prefab
    public FireSource currentFire { get; set; }         // <-- made setter public

    [Header("Orbit (payload)")]
    public float orbitSpeedDeg = 45f;
    public float orbitTighten = 0.7f;                   // 0..1 of PayloadRing

    [Header("Orbit Safety")]
    public float minOrbitClearance = 2f;
    public LayerMask orbitObstacleMask;                 // set to Obstacles

    [Header("Patrol")]
    public Vector3 home;
    public float patrolRadius = 120f;
    public Vector2 patrolRadiusRange = new Vector2(80f, 220f);

    SteeringAgent _steer;
    Rigidbody _rb;
    Vector3? _target;
    float _orbitAngle;

    void Awake()
    {
        _steer = GetComponent<SteeringAgent>();
        _rb = GetComponent<Rigidbody>();
        _rb.useGravity = false;
    }

    void OnEnable()
    {
        EventHub.OnGlobalRespondToFire += OnGlobalRespond;
    }

    void OnDisable()
    {
        EventHub.OnGlobalRespondToFire -= OnGlobalRespond;
    }

    // ---------- Spawning ----------
    public void SpawnAt(Vector3 pos)
    {
        transform.position = pos;
        home = pos;
        patrolRadius = Random.Range(patrolRadiusRange.x, patrolRadiusRange.y);
        state = DroneState.Patrol;
        PickNewPatrolPoint();
    }

    public void SpawnAndIdleAt(Vector3 pos)
    {
        transform.position = pos;
        home = pos;
        state = DroneState.Idle;
        _steer.desiredAltitude = ComputePatrolAltitude();
        _target = home + Vector3.up * _steer.desiredAltitude;
    }

    public void ScatterLaunch(Vector3 stationPos, Vector3 outwardDir, float ringRadius = 70f, float initialSpeed = 22f)
    {
        Vector3 dir = new Vector3(outwardDir.x, 0f, outwardDir.z);
        if (dir.sqrMagnitude < 0.001f) dir = Random.insideUnitSphere;
        dir.y = 0f; dir.Normalize();

        Vector3 p = stationPos + dir * ringRadius;

        var mgr = SwarmManager.Instance;
        if (mgr != null && mgr.limitToCamera && mgr.cameraGroundBounds.size.x > 0f)
            p = CameraBoundsUtil.ClampToXZ(mgr.cameraGroundBounds, p);

        _steer.desiredAltitude = ComputePatrolAltitude();
        _target = p + Vector3.up * _steer.desiredAltitude;

        state = DroneState.Patrol;
        _rb.linearVelocity = dir * initialSpeed;
    }

    // ---------- Global Fire Broadcast Handler ----------
    void OnGlobalRespond(FireSource fire)
    {
        // If I'm already working on this fire, ignore
        if (currentFire != null && currentFire == fire)
            return;

        // PAYLOAD DRONES decide if they are needed
        if (isPayloadDrone || role == DroneRole.Payload)
        {
            bool needsMe = fire.NeedsMorePayloadDrones();

            if (needsMe)
            {
                SetOrbitTarget(fire); // assigns Delivering etc.
                currentFire = fire;
                state = DroneState.Delivering;
            }
            else
            {
                // Not needed - resume/continue patrol instead of piling up
                if (state != DroneState.Patrol && state != DroneState.Idle)
                    ReturnToPatrol();
            }

            return;
        }

        // SCOUT DRONES move to observe
        if (role == DroneRole.Scout)
        {
            float alt = Mathf.Clamp(
                fire.transform.position.y + attackOffset,
                minPatrolAlt,
                maxPatrolAlt
            );
            _steer.desiredAltitude = alt;

            _target = new Vector3(
                fire.transform.position.x,
                alt,
                fire.transform.position.z
            );

            currentFire = fire;
            state = DroneState.Responding;
            return;
        }

        // fallback for any future roles
        _target = fire.transform.position;
        currentFire = fire;
        state = DroneState.Responding;
    }

    // ---------- Orbit Target Assign ----------
    public void SetOrbitTarget(FireSource fire, int slotIndex, int totalSlots)
    {
        if (fire == null)
        {
            ReturnAndIdle();
            return;
        }

        currentFire = fire;
        _orbitAngle = Random.Range(0f, 360f);
        state = DroneState.Delivering;

        _orbitTotal = Mathf.Max(1, totalSlots);
        _orbitSlot = Mathf.Clamp(slotIndex, 0, _orbitTotal - 1);
    }

    // Backwards-compatible wrapper
    public void SetOrbitTarget(FireSource fire)
    {
        SetOrbitTarget(fire, 0, 1);
    }

    // ---------- Loop ----------
    void Update()
    {
        // Continuously adapt patrol altitude while patrolling
        if (state == DroneState.Patrol)
            _steer.desiredAltitude = ComputePatrolAltitude();

        switch (state)
        {
            case DroneState.Idle:
                _target = new Vector3(home.x, _steer.desiredAltitude, home.z);
                break;

            case DroneState.Patrol:
                if (!_target.HasValue ||
                    (transform.position - _target.Value).sqrMagnitude < 100f)
                {
                    PickNewPatrolPoint();
                }
                break;

            case DroneState.Responding:
                // Target set when responding. We just keep heading there / hovering.
                break;

            case DroneState.Delivering:
                UpdateOrbit();
                break;

            case DroneState.Returning:
                _target = new Vector3(home.x, _steer.desiredAltitude, home.z);
                if ((transform.position - _target.Value).sqrMagnitude < 36f)
                {
                    state = DroneState.Idle;
                }
                break;
        }

        // If fire got destroyed, snap back to patrol
        if ((state == DroneState.Delivering || state == DroneState.Responding) && currentFire == null)
        {
            ReturnToPatrol();
        }

        _steer.TickSteering(_target, this);
    }

    void UpdateOrbit()
    {
        if (currentFire == null)
        {
            ReturnAndIdle();
            return;
        }

        Vector3 c = currentFire.transform.position;
        float baseRing = Mathf.Max(1.5f, currentFire.GetPayloadRing() * orbitTighten);

        _orbitAngle += orbitSpeedDeg * Time.deltaTime;
        float slotAngleOffset = (_orbitTotal > 1) ? (360f / _orbitTotal) * _orbitSlot : 0f;
        float absoluteAngle = _orbitAngle + slotAngleOffset;
        Vector3 dir = (Quaternion.Euler(0f, absoluteAngle, 0f) * Vector3.forward).normalized;

        // obstacle-aware ring radius
        float safeRing = baseRing;
        float probeLen = baseRing + minOrbitClearance + 4f;
        if (Physics.SphereCast(c, 0.6f, dir, out RaycastHit hit, probeLen, orbitObstacleMask, QueryTriggerInteraction.Ignore))
        {
            safeRing = Mathf.Max(1f, hit.distance - minOrbitClearance);
        }

        Vector3 offset = dir * safeRing;

        // clamp to camera bounds if enabled
        var mgr = SwarmManager.Instance;
        Vector3 p = new Vector3(c.x + offset.x, 0f, c.z + offset.z);
        if (mgr != null && mgr.limitToCamera && mgr.cameraGroundBounds.size.x > 0f)
        {
            p = CameraBoundsUtil.ClampToXZ(mgr.cameraGroundBounds, p);
        }

        // local drone separation so we don't overlap
        Collider[] near = Physics.OverlapSphere(
            new Vector3(p.x, transform.position.y, p.z),
            1.2f,
            ~0,
            QueryTriggerInteraction.Ignore
        );

        foreach (var col in near)
        {
            if (!col || !col.CompareTag("Drone")) continue;
            if (col.gameObject == gameObject) continue;

            Vector3 away = transform.position - col.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude > 0.001f)
                p += away.normalized * 0.6f;
        }

        float alt = Mathf.Clamp(c.y + attackOffset, minPatrolAlt, maxPatrolAlt);
        _steer.desiredAltitude = alt;
        _target = new Vector3(p.x, alt, p.z);
    }

    // ---------- Helpers ----------
    float ComputePatrolAltitude()
    {
        if (!followBuildingHeights)
            return Random.Range(minPatrolAlt, maxPatrolAlt);

        float tallest = 0f;
        var hits = Physics.OverlapSphere(transform.position, roofScanRadius);
        foreach (var h in hits)
        {
            if (!h || !h.CompareTag("Building")) continue;

            float top = 0f;
            var rend = h.GetComponentInChildren<Renderer>();
            var col = h.GetComponentInChildren<Collider>();
            if (rend) top = rend.bounds.max.y;
            else if (col) top = col.bounds.max.y;

            if (top > tallest)
                tallest = top;
        }

        if (tallest <= 0f)
            return Mathf.Clamp(transform.position.y, minPatrolAlt, maxPatrolAlt);

        return Mathf.Clamp(tallest + roofOffset, minPatrolAlt, maxPatrolAlt);
    }

    void PickNewPatrolPoint()
    {
        Vector3 p;
        var mgr = SwarmManager.Instance;
        if (mgr != null && mgr.limitToCamera && mgr.cameraGroundBounds.size.x > 0f)
        {
            var b = mgr.cameraGroundBounds;
            p = new Vector3(
                Random.Range(b.min.x, b.max.x),
                mgr.groundY,
                Random.Range(b.min.z, b.max.z)
            );
        }
        else
        {
            Vector2 circle = Random.insideUnitCircle * patrolRadius;
            p = home + new Vector3(circle.x, 0f, circle.y);
        }

        _target = new Vector3(p.x, _steer.desiredAltitude, p.z);
    }

    public void AssignTarget(Vector3 worldPos)
    {
        var mgr = SwarmManager.Instance;
        Vector3 p = worldPos;
        if (mgr != null && mgr.limitToCamera && mgr.cameraGroundBounds.size.x > 0f)
            p = CameraBoundsUtil.ClampToXZ(mgr.cameraGroundBounds, worldPos);

        _target = new Vector3(p.x, _steer.desiredAltitude, p.z);
    }

    public void ReturnToPatrol()
    {
        currentFire = null;
        if (state != DroneState.Patrol)
        {
            state = DroneState.Patrol;
            _steer.desiredAltitude = ComputePatrolAltitude();
            PickNewPatrolPoint();
        }
    }

    public void ReturnAndIdle()
    {
        currentFire = null;
        state = DroneState.Returning;
    }

    // Used by SwarmManager to filter available payloads if needed
    public bool IsBusy()
    {
        return !(state == DroneState.Idle || state == DroneState.Patrol);
    }
}
