using System;
using UnityEngine;

[Serializable]
public class PlacementPoint
{
    [Tooltip("Unique Placement ID (e.g. PLAYER_START_S0, NPC_MAESTRO_IDLE)")]
    public string id;

    [Tooltip("Transform used as position + rotation anchor")]
    public Transform anchor;
}