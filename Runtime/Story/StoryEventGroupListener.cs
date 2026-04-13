using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

[Serializable]
public class StoryEventBinding
{
    [Tooltip("Nombre exacto del evento. Ej: S1_HORNO_010_Start")]
    public string eventName;

    [Tooltip("Acciones a ejecutar cuando se dispare este evento.")]
    public UnityEvent onEvent;
}

/// <summary>
/// Listener que puede manejar varios eventos de historia en una sola instancia.
/// Ideal para tener uno por acto (Acto 1, 2, 3).
/// </summary>
public class StoryEventGroupListener : MonoBehaviour
{
    [Header("Opcional: filtro por prefijo (scene/acto)")]
    [Tooltip("Si se rellena, solo se procesan eventos cuyo nombre empiece por este prefijo. Ej: 'S1_'")]
    public string eventPrefixFilter;

    [Header("Bindings de eventos")]
    public List<StoryEventBinding> bindings = new List<StoryEventBinding>();

    [Header("Performance")]
    [Tooltip("Si está activo, los UnityEvents se invocan fuera del dispatch del bus (en frames posteriores) para evitar spikes al pulsar Next.")]
    public bool invokeNextFrame = true;

    [Range(0, 3)]
    [Tooltip("Frames de retardo antes de invocar el UnityEvent (0 = mismo frame).")]
    public int framesDelay = 1;

    [Tooltip("Si estÃ¡ activo, en vez de lanzar una coroutine por binding, se encolan invocaciones y se drenan con presupuesto por frame.")]
    public bool coalesceDeferredInvokes = true;

    [Min(1)]
    [Tooltip("MÃ¡ximo de UnityEvents diferidos a invocar por frame.")]
    public int maxDeferredInvokesPerFrame = 4;

    [Header("Debug")]
    [Tooltip("Logs de diagnÃ³stico del drenado diferido (Editor por defecto).")]
    public bool verbosePerformanceLogs = false;

    private struct PendingInvoke
    {
        public UnityEvent unityEvent;
        public int targetFrame;
    }

    private readonly Queue<PendingInvoke> _pendingInvokes = new Queue<PendingInvoke>(16);
    private Coroutine _drainRoutine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // Spike candidate:
    // MÃºltiples bindings pueden disparar UnityEvents costosos (SetActive/animaciones) en el mismo frame.
    // Se perfila y se trocea por presupuesto cuando hay defer+coalescing.
    private static readonly ProfilerMarker _pmHandle = new ProfilerMarker("StoryEventGroupListener.Handle");
    private static readonly ProfilerMarker _pmInvokeNow = new ProfilerMarker("StoryEventGroupListener.InvokeNow");
    private static readonly ProfilerMarker _pmDrain = new ProfilerMarker("StoryEventGroupListener.DrainDeferred");
#endif

    private void OnEnable()
    {
        StoryEventBus.OnStoryEvent += HandleStoryEvent;
    }

    private void OnDisable()
    {
        StoryEventBus.OnStoryEvent -= HandleStoryEvent;

        if (_drainRoutine != null)
        {
            StopCoroutine(_drainRoutine);
            _drainRoutine = null;
        }

        _pendingInvokes.Clear();
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
        if (string.IsNullOrEmpty(raisedEventName))
            return;

        // Si hay prefijo de filtro (ej. "S1_"), ignoramos lo que no pertenece a este acto
        if (!string.IsNullOrEmpty(eventPrefixFilter) &&
            !raisedEventName.StartsWith(eventPrefixFilter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (bindings == null || bindings.Count == 0)
            return;

        for (int i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];
            if (b == null || string.IsNullOrEmpty(b.eventName))
                continue;

            if (!string.Equals(b.eventName, raisedEventName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.MarkEvent("StoryEventGroupListener.Match", raisedEventName, "listener=" + name);

            bool forceDeferByPipeline =
                NarrativeTransitionPipeline.IsActive &&
                !NarrativeTransitionPipeline.IsPhaseReached(NarrativeTransitionPipeline.Phase.NonCriticalActions);

            if (!forceDeferByPipeline && (!invokeNextFrame || framesDelay <= 0))
            {
                InvokeUnityEventNow(b.onEvent);
            }
            else
            {
                if (!coalesceDeferredInvokes)
                {
                    StartCoroutine(InvokeDeferred(b.onEvent));
                    continue;
                }

                EnqueueDeferredInvoke(b.onEvent);
            }
        }
    }

    private IEnumerator InvokeDeferred(UnityEvent evt)
    {
        for (int i = 0; i < framesDelay; i++)
            yield return null;

        InvokeUnityEventNow(evt);
    }

    private void EnqueueDeferredInvoke(UnityEvent evt)
    {
        if (evt == null)
            return;

        int delay = Mathf.Max(0, framesDelay);
        _pendingInvokes.Enqueue(new PendingInvoke
        {
            unityEvent = evt,
            targetFrame = Time.frameCount + delay
        });

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryEventGroupListener.EnqueueInvoke", $"listener={name} pending={_pendingInvokes.Count} delayFrames={delay}");

        if (_drainRoutine == null)
            _drainRoutine = StartCoroutine(DrainDeferredInvokesRoutine());
    }

    private IEnumerator DrainDeferredInvokesRoutine()
    {
        while (_pendingInvokes.Count > 0)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmDrain.Auto())
            {
                DrainDeferredInvokesOneFrame();
            }
#else
            DrainDeferredInvokesOneFrame();
#endif
            yield return null;
        }

        _drainRoutine = null;
    }

    private void DrainDeferredInvokesOneFrame()
    {
        int budget = Mathf.Max(1, maxDeferredInvokesPerFrame);
        int invoked = 0;
        int now = Time.frameCount;

        while (_pendingInvokes.Count > 0 && invoked < budget)
        {
            var next = _pendingInvokes.Peek();
            if (next.targetFrame > now)
                break;

            _pendingInvokes.Dequeue();
            InvokeUnityEventNow(next.unityEvent);
            invoked++;
        }

        if (verbosePerformanceLogs && invoked > 0)
        {
            Debug.Log($"[StoryEventGroupListener] Invoked deferred={invoked} pending={_pendingInvokes.Count}", this);
        }
    }

    private void InvokeUnityEventNow(UnityEvent evt)
    {
        if (evt == null)
            return;

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryEventGroupListener.Invoke", "listener=" + name);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmInvokeNow.Auto())
        {
            evt.Invoke();
        }
#else
        evt.Invoke();
#endif
    }
}
