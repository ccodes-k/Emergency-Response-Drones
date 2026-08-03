using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class SteeringAgent : MonoBehaviour
{
    public float maxSpeed = 14f;
    public float maxForce = 28f;
    public float desiredAltitude = 12f;
    public float altitudeForce = 8f;

    public float separationRadius = 3f;
    public float neighborRadius = 8f;
    public float separationWeight = 1.5f;
    public float alignmentWeight = 0.6f;
    public float cohesionWeight = 0.5f;
    public float wanderStrength = 2f;

    [Header("Cruise")]
    public float minCruiseSpeed = 3f;

    [Header("Camera Bounds")]
    public bool enforceCameraBounds = true;
    public float boundaryBuffer = 5f;
    public float boundaryForce = 40f;
    public float boundaryFriction = 3f;

    [Header("Obstacle Avoidance")]
    public LayerMask obstacleMask;      // set to Obstacles layer
    public float avoidSphereRadius = 1.0f;
    public float avoidRange = 12f;
    public float avoidForce = 60f;
    public float sideWhiskerAngle = 25f;
    public float sideWhiskerFactor = 0.7f;

    Rigidbody rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
    }

    public void TickSteering(Vector3? target, DroneController self)
    {
        Vector3 force = Vector3.zero;

        // Altitude hold
        float altitudeError = desiredAltitude - transform.position.y;
        force += Vector3.up * altitudeError * altitudeForce;

        // Flocking
        var neighbors = Physics.OverlapSphere(transform.position, neighborRadius);
        Vector3 sep = Vector3.zero, ali = Vector3.zero, coh = Vector3.zero;
        int count = 0;
        foreach (var col in neighbors)
        {
            if (!col || col.attachedRigidbody == null || col.attachedRigidbody == rb) continue;
            if (!col.CompareTag("Drone")) continue;
            count++;
            Vector3 toMe = transform.position - col.transform.position;
            float d = toMe.magnitude;
            if (d < 0.001f) continue;
            if (d < separationRadius) sep += toMe.normalized / Mathf.Max(d, 0.2f);
            ali += col.attachedRigidbody.linearVelocity;
            coh += col.transform.position;
        }
        if (count > 0)
        {
            ali = ali.normalized;
            coh = ((coh / count) - transform.position).normalized;
        }
        force += separationWeight * sep + alignmentWeight * ali + cohesionWeight * coh;

        // Wander
        force += Random.insideUnitSphere * wanderStrength;

        // Goal seeking
        if (target.HasValue)
        {
            Vector3 desired = (target.Value - transform.position);
            float dist = desired.magnitude;
            desired = desired.normalized * Mathf.Lerp(maxSpeed * 0.5f, maxSpeed, Mathf.Clamp01(dist / 10f));
            Vector3 seekForce = desired - rb.linearVelocity;
            force += Vector3.ClampMagnitude(seekForce, maxForce);
        }

        // Obstacle avoidance
        Vector3 fwd = transform.forward;
        Vector3 pos = transform.position;

        if (Physics.SphereCast(pos, avoidSphereRadius, fwd, out RaycastHit hit, avoidRange, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 away = Vector3.ProjectOnPlane(pos - hit.point, Vector3.up).normalized;
            if (away.sqrMagnitude < 1e-3f) away = Vector3.Cross(Vector3.up, fwd);
            force += away * avoidForce;
        }
        else
        {
            Vector3 leftDir = Quaternion.Euler(0f, -sideWhiskerAngle, 0f) * fwd;
            Vector3 rightDir = Quaternion.Euler(0f, sideWhiskerAngle, 0f) * fwd;
            if (Physics.Raycast(pos, leftDir, out hit, avoidRange * sideWhiskerFactor, obstacleMask))
                force += (Vector3.Cross(Vector3.up, leftDir)).normalized * avoidForce * 0.7f;
            if (Physics.Raycast(pos, rightDir, out hit, avoidRange * sideWhiskerFactor, obstacleMask))
                force += (Vector3.Cross(rightDir, Vector3.up)).normalized * avoidForce * 0.7f;
        }

        // Keep moving
        Vector3 horiz = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        if (horiz.magnitude < minCruiseSpeed && target.HasValue)
        {
            Vector3 dir = (target.Value - transform.position).normalized;
            force += dir * (minCruiseSpeed * 0.8f);
        }

        // Bounds pushback
        if (enforceCameraBounds && SwarmManager.Instance != null && SwarmManager.Instance.limitToCamera)
        {
            var b = SwarmManager.Instance.cameraGroundBounds;
            if (b.size.x > 0f)
            {
                Vector3 p = transform.position;
                float xMin = b.min.x + boundaryBuffer, xMax = b.max.x - boundaryBuffer;
                float zMin = b.min.z + boundaryBuffer, zMax = b.max.z - boundaryBuffer;
                float fx = 0f, fz = 0f;
                if (p.x < xMin) fx = (xMin - p.x); else if (p.x > xMax) fx = (xMax - p.x);
                if (p.z < zMin) fz = (zMin - p.z); else if (p.z > zMax) fz = (zMax - p.z);
                if (fx != 0f || fz != 0f)
                {
                    force += new Vector3(fx, 0f, fz) * boundaryForce;
                    Vector3 v = rb.linearVelocity;
                    if (p.x < xMin || p.x > xMax) v.x *= Mathf.Clamp01(1f - boundaryFriction * Time.deltaTime);
                    if (p.z < zMin || p.z > zMax) v.z *= Mathf.Clamp01(1f - boundaryFriction * Time.deltaTime);
                    rb.linearVelocity = v;
                }
            }
        }

        // Apply & face velocity
        force = Vector3.ClampMagnitude(force, maxForce);
        rb.AddForce(force, ForceMode.Acceleration);

        Vector3 vel = rb.linearVelocity;
        if (vel.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(new Vector3(vel.x, 0f, vel.z)),
                10f * Time.deltaTime
            );
    }
}
