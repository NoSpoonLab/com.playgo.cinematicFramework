using System.Collections;
using UnityEngine;
using UnityEngine.Events;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

/// <summary>
/// Escucha eventos del StoryEventBus y dispara UnityEvents
/// cuando el nombre de evento coincide.
/// </summary>
public class StoryEventListener : MonoBehaviour
{
    [Header("Story Event Listener")]
    [Tooltip("Nombre exacto del evento al que escuchar (ej. S1_010_Start).")]
    public string eventName;

    [Tooltip("Acciones a ejecutar cuando se dispare el evento indicado.")]
    public UnityEvent onEventTriggered;

    [Header("Performance")]
    [Tooltip("Si está activo, el UnityEvent se invoca en el siguiente frame (fuera del dispatch del bus) para evitar spikes al pulsar Next.")]
    public bool invokeNextFrame = true;

    [Range(0, 3)]
    [Tooltip("Frames de retardo antes de invocar el UnityEvent (0 = mismo frame).")]
    public int framesDelay = 1;

    private Coroutine _invokeRoutine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // Spike candidate:
    // UnityEvent puede activar animaciones/objetos y concentrar coste cuando llega un burst de eventos.
    private static readonly ProfilerMarker _pmHandle = new ProfilerMarker("StoryEventListener.Handle");
    private static readonly ProfilerMarker _pmInvoke = new ProfilerMarker("StoryEventListener.Invoke");
#endif

    private void OnEnable()
    {
        StoryEventBus.OnStoryEvent += HandleStoryEvent;
    }

    private void OnDisable()
    {
        StoryEventBus.OnStoryEvent -= HandleStoryEvent;

        if (_invokeRoutine != null)
        {
            StopCoroutine(_invokeRoutine);
            _invokeRoutine = null;
        }
    }

    private void HandleStoryEvent(string raisedEventName, StoryEntry entry)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmHandle.Auto())
        {
            HandleStoryEventInternal(raisedEventName, entry);
        }
#else
        HandleStoryEventInternal(raisedEventName, entry);
#endif
    }

    private void HandleStoryEventInternal(string raisedEventName, StoryEntry entry)
    {
        if (string.IsNullOrEmpty(eventName))
            return;

        if (!string.Equals(raisedEventName, eventName, System.StringComparison.OrdinalIgnoreCase))
            return;

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.MarkEvent("StoryEventListener.Match", raisedEventName, "listener=" + name);

        bool forceDeferByPipeline =
            NarrativeTransitionPipeline.IsActive &&
            !NarrativeTransitionPipeline.IsPhaseReached(NarrativeTransitionPipeline.Phase.NonCriticalActions);

        if (!forceDeferByPipeline && (!invokeNextFrame || framesDelay <= 0))
        {
            InvokeNow();
            return;
        }

        // Evita múltiples invocaciones acumuladas si llegan varios eventos iguales seguidos.
        if (_invokeRoutine != null)
            StopCoroutine(_invokeRoutine);

        _invokeRoutine = StartCoroutine(InvokeDeferred());
    }

    private IEnumerator InvokeDeferred()
    {
        for (int i = 0; i < framesDelay; i++)
            yield return null;

        InvokeNow();
        _invokeRoutine = null;
    }

    private void InvokeNow()
    {
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryEventListener.Invoke", "listener=" + name + " event=" + eventName);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmInvoke.Auto())
        {
            onEventTriggered?.Invoke();
        }
#else
        onEventTriggered?.Invoke();
#endif
    }
}
