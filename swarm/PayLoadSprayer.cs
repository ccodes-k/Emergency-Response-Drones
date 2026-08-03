using UnityEngine;

[RequireComponent(typeof(DroneController))]
[RequireComponent(typeof(LineRenderer))]
public class PayLoadSprayer : MonoBehaviour
{
    public Transform nozzle;                 // optional: front of the drone
    public float sprayRange = 20f;
    public float suppressionPerSecond = 0.6f;
    public float aimAngle = 25f;
    public LayerMask obstacleMask;           // set to Obstacles

    DroneController ctrl;
    LineRenderer lr;

    void Awake()
    {
        ctrl = GetComponent<DroneController>();
        lr = GetComponent<LineRenderer>();
        lr.positionCount = 2;
        lr.enabled = false;
    }

    void LateUpdate()
    {
        bool spray = false;
        Vector3 origin = nozzle ? nozzle.position : transform.position;

        if (ctrl.isPayloadDrone && ctrl.state == DroneController.DroneState.Delivering && ctrl.currentFire != null)
        {
            var fire = ctrl.currentFire;
            Vector3 tgt = fire.transform.position;
            tgt.y = Mathf.Clamp(tgt.y + ctrl.attackOffset, ctrl.minPatrolAlt, ctrl.maxPatrolAlt);

            Vector3 dir = tgt - origin;
            float dist = dir.magnitude;

            if (dist <= sprayRange && Vector3.Angle(transform.forward, dir) <= aimAngle)
            {
                if (!Physics.Raycast(origin, dir.normalized, dist, obstacleMask))
                {
                    fire.AddExternalSuppression(suppressionPerSecond);
                    lr.SetPosition(0, origin);
                    lr.SetPosition(1, tgt);
                    spray = true;
                }
            }
        }
        lr.enabled = spray;
    }
}
