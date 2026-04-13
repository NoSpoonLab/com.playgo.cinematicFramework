using System;
using UnityEngine;

[Serializable]
public class PlayerPlacement
{
    [Tooltip("Logical context (Startup, Scene, Debug...)")]
    public Enums.PlacementContext context;

    [Tooltip("Placement Point ID (e.g. PLAYER_START_S0)")]
    public string placementPointID;
}