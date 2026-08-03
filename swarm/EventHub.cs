using System;
using UnityEngine;

public static class EventHub
{
    public static event Action<FireSource, DroneController, float> OnFireSpotted;
    public static event Action<FireSource> OnFireExtinguished;
    public static event Action<FireSource> OnGlobalRespondToFire;

    public static void FireSpotted(FireSource fire, DroneController who, float confidence)
        => OnFireSpotted?.Invoke(fire, who, confidence);

    public static void FireExtinguished(FireSource fire)
        => OnFireExtinguished?.Invoke(fire);

    public static void RespondToFire(FireSource fire)
        => OnGlobalRespondToFire?.Invoke(fire);
}
