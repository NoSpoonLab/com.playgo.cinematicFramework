using System.Collections;
using UnityEngine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

/// <summary>
/// Escucha eventos "*_Start" y hace teleport al placement del entry.
///
/// v2 PERF:
/// - Elimina Debug.Log por defecto (en device puede costar decenas de ms).
/// - Difunde el teleport a +2 frames para que no coincida con Canvas/TMP rebuild.
/// - Si el teleport se dispara desde un handler del StoryEventBus, ExperienceManager también aplicará un delay extra.
/// </summary>
public class StoryEntryPlacementListener : MonoBehaviour
{
    [Header("Teleport")]
    [Tooltip("Frames mínimos a esperar antes de pedir el teleport.")]
    [Min(0)] public int minDelayFrames = 2;

    [Tooltip("Si está activo, añade 1 frame extra para separar aún más de Canvas/TMP.")]
    public bool extraSafetyFrame = true;
    [Tooltip("Evita programar teleports duplicados mientras ya hay uno diferido pendiente.")]
    public bool coalesceDeferredRequests = true;
    [Tooltip("Si llega otro placement antes de ejecutar el pendiente, sustituye el anterior para evitar trabajo inutil.")]
    public bool replacePendingWhenNewPlacementArrives = true;

    [Header("Debug")]
    public bool verboseLogs = false;
    public bool allowDeviceLogs = false;

    private Coroutine _pendingTeleportRoutine;
    private string _pendingPlacementId;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // PERF NOTE:
    // Este listener puede disparar teleports en cada *_Start; medimos schedule vs defer real.
    private static readonly ProfilerMarker _pmHandle = new ProfilerMarker("StoryEntryPlacementListener.Handle");
    private static readonly ProfilerMarker _pmScheduleTeleport = new ProfilerMarker("StoryEntryPlacementListener.ScheduleTeleport");
    private static readonly ProfilerMarker _pmCoalesce = new ProfilerMarker("StoryEntryPlacementListener.Coalesce");
#endif

    private void OnEnable()
    {
        StoryEventBus.OnStoryEvent += HandleStoryEvent;
    }

    private void OnDisable()
    {
        StoryEventBus.OnStoryEvent -= HandleStoryEvent;

        if (_pendingTeleportRoutine != null)
        {
            StopCoroutine(_pendingTeleportRoutine);
            _pendingTeleportRoutine = null;
        }

        _pendingPlacementId = null;
    }

    private void HandleStoryEvent(string eventName, StoryEntry entry)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmHandle.Auto())
        {
            HandleStoryEventInternal(eventName, entry);
        }
#else
        HandleStoryEventInternal(eventName, entry);
#endif
    }

    private void HandleStoryEventInternal(string eventName, StoryEntry entry)
    {
        if (entry == null) return;
        if (string.IsNullOrEmpty(eventName)) return;
        if (!eventName.EndsWith("_Start")) return;

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.MarkEvent("StoryEntryPlacementListener.HandleStart", eventName, "entry=" + entry.id);

        if (string.IsNullOrEmpty(entry.id))
        {
            Log("entry.id empty (skip)");
            return;
        }

        if (ExperienceManager.Instance == null)
        {
            Log("ExperienceManager.Instance is NULL");
            return;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmScheduleTeleport.Auto())
        {
            ScheduleDeferredTeleport(entry.id);
        }
#else
        ScheduleDeferredTeleport(entry.id);
#endif
    }

    private void ScheduleDeferredTeleport(string placementId)
    {
        if (string.IsNullOrEmpty(placementId))
            return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmCoalesce.Auto())
        {
            ScheduleDeferredTeleportInternal(placementId);
        }
#else
        ScheduleDeferredTeleportInternal(placementId);
#endif
    }

    private void ScheduleDeferredTeleportInternal(string placementId)
    {
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryEntryPlacementListener.ScheduleDeferredTeleport", $"placement={placementId} pending={(_pendingTeleportRoutine != null)}");
        // COSTE EVITADO:
        // Sin coalescing, varios *_Start seguidos pueden acumular coroutines y duplicar requests de teleport.
        if (coalesceDeferredRequests && _pendingTeleportRoutine != null)
        {
            if (string.Equals(_pendingPlacementId, placementId, System.StringComparison.OrdinalIgnoreCase))
            {
                Log("Coalesced duplicate deferred placement: " + placementId);
                return;
            }

            if (replacePendingWhenNewPlacementArrives)
            {
                StopCoroutine(_pendingTeleportRoutine);
                _pendingTeleportRoutine = null;
                Log("Replacing deferred placement '" + _pendingPlacementId + "' -> '" + placementId + "'.");
            }
        }

        _pendingPlacementId = placementId;
        _pendingTeleportRoutine = StartCoroutine(TeleportDeferred(placementId));
    }

    private IEnumerator TeleportDeferred(string placementId)
    {
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryEntryPlacementListener.TeleportDeferred.Begin", "placement=" + placementId);
        int frames = Mathf.Max(0, minDelayFrames);
        while (frames-- > 0)
            yield return null;

        if (extraSafetyFrame)
            yield return null;

        if (ExperienceManager.Instance == null)
        {
            _pendingTeleportRoutine = null;
            _pendingPlacementId = null;
            yield break;
        }

        ExperienceManager.Instance.TryTeleportPlayerToPlacementID(placementId);
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryEntryPlacementListener.TeleportDeferred.Requested", "placement=" + placementId);
        _pendingTeleportRoutine = null;
        _pendingPlacementId = null;
    }

    private void Log(string msg)
    {
        if (!verboseLogs) return;
        if (!Application.isEditor && !allowDeviceLogs) return;
        Debug.Log("[StoryEntryPlacementListener] " + msg, this);
    }
}
