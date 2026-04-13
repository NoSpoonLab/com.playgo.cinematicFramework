using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

/// <summary>
/// Bus global de eventos de historia.
/// Lanza eventos del tipo "S1_010_Start", "S1_010_End", etc.
///
/// v2 PERF:
/// - Mantiene compatibilidad con el event público OnStoryEvent (+= / -=).
/// - Cuando existe StoryEventBusDispatcher, los Raise() se encolan.
/// - El dispatcher puede invocar handlers uno a uno con presupuesto por frame (time-slicing).
/// </summary>
public static class StoryEventBus
{
    // Backing field para poder leer invocation list sin romper la API de event.
    private static Action<string, StoryEntry> _onStoryEvent;
    private static int _handlersVersion;

    /// <summary>
    /// Suscripción a eventos.
    /// IMPORTANTE: mantenemos el "event" público para compatibilidad.
    /// </summary>
    public static event Action<string, StoryEntry> OnStoryEvent
    {
        add
        {
            _onStoryEvent += value;
            _handlersVersion++;
        }
        remove
        {
            _onStoryEvent -= value;
            _handlersVersion++;
        }
    }

    public static int GateCount => _gateCount;
    public static int QueuedCount => _queue.Count;

    /// <summary>
    /// True mientras se está ejecutando un handler (útil para que sistemas pesados se auto-dosifiquen).
    /// Nota: en v2 el dispatcher invoca handlers individualmente, así que este flag es granular.
    /// </summary>
    public static bool IsDispatching => _isDispatching;

    private struct Queued
    {
        public string name;
        public StoryEntry entry;
        public int enqueueFrame;
    }

    private static readonly Queue<Queued> _queue = new Queue<Queued>(128);
    private static readonly Dictionary<string, int> _lastQueuedFrameByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HandlerPerf> _handlerPerf = new Dictionary<string, HandlerPerf>(StringComparer.Ordinal);
    private static int _gateCount = 0;

    // Cuando hay un dispatcher en escena, el bus opera en modo deferred.
    // Si no existe dispatcher (tests/escenas antiguas), mantiene compatibilidad e invoca al vuelo.
    private static bool _hasDispatcher = false;
    private static bool _isDispatching = false;
    private static bool _coalesceQueuedEvents = false;
    private static int _coalesceWindowFrames = 1;

    private struct HandlerPerf
    {
        public int callCount;
        public float emaMs;
        public float maxMs;
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private static readonly ProfilerMarker _pmRaise = new ProfilerMarker("StoryEventBus.Raise");
    // PERF NOTE:
    // Estas rutas pueden concentrar CPU cuando muchos listeners reaccionan a Start/End/Feedback.
    private static readonly ProfilerMarker _pmEnqueue = new ProfilerMarker("StoryEventBus.Enqueue");
    private static readonly ProfilerMarker _pmFlushQueued = new ProfilerMarker("StoryEventBus.FlushQueued");
    private static readonly ProfilerMarker _pmInvokeAll = new ProfilerMarker("StoryEventBus.InvokeHandlers.All");
    private static readonly ProfilerMarker _pmInvokeOne = new ProfilerMarker("StoryEventBus.InvokeHandlers.One");
#endif

    /// <summary>True si el bus está en modo "gate" (eventos encolados).</summary>
    public static bool IsGated => _gateCount > 0;

    /// <summary>Empieza un gate (puede anidarse).</summary>
    public static void BeginGate() => _gateCount++;

    /// <summary>
    /// Termina un gate. Cuando el contador llega a 0, se hace flush de la cola.
    /// </summary>
    public static void EndGate()
    {
        if (_gateCount > 0) _gateCount--;

        // IMPORTANTE: no hacemos FlushQueued() aquí si hay dispatcher para no provocar un spike.
        // El dispatcher drenará la cola con presupuesto por frame.
        if (_gateCount == 0 && !_hasDispatcher)
            FlushQueued();
    }

    /// <summary>Vacía la cola y dispara eventos en el orden original.</summary>
    public static void FlushQueued(int maxToFlush = 2048)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Spike candidate: flush completo sin time-slicing cuando no hay dispatcher.
        // Coste esperado: CPU por dispatch acumulado.
        using var _scope = _pmFlushQueued.Auto();
#endif
        int guard = 0;

        while (_queue.Count > 0 && guard++ < maxToFlush)
        {
            var q = _queue.Dequeue();
            // Back-compat: invoca todo en el acto
            InternalInvokeAll(q.name, q.entry);
        }

        if (guard >= maxToFlush && _queue.Count > 0)
        {
            Debug.LogWarning($"[StoryEventBus] Flush guard hit. Remaining queued events: {_queue.Count}");
        }
    }

    /// <summary>Descarta eventos encolados (por si quieres abortar una transición).</summary>
    public static void ClearQueued()
    {
        _queue.Clear();
        _lastQueuedFrameByName.Clear();
    }

    public static void Raise(string eventName, StoryEntry entry)
    {
        if (string.IsNullOrEmpty(eventName))
            return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmRaise.Auto())
        {
            RaiseInternal(eventName, entry);
        }
#else
        RaiseInternal(eventName, entry);
#endif
    }

    private static void RaiseInternal(string eventName, StoryEntry entry)
    {
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.MarkEvent("StoryEventBus.Raise", eventName, $"queued={_queue.Count} gated={IsGated} hasDispatcher={_hasDispatcher}");

        // Con dispatcher presente, SIEMPRE encolamos para que el procesamiento sea budgeted.
        if (_hasDispatcher || IsGated)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmEnqueue.Auto())
#endif
            if (ShouldCoalesce(eventName))
                return;

            _queue.Enqueue(new Queued { name = eventName, entry = entry, enqueueFrame = Time.frameCount });
            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.MarkEvent("StoryEventBus.Enqueue", eventName, "queueAfter=" + _queue.Count);
            return;
        }

        // Back-compat: si no hay dispatcher, se invoca en el acto.
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.MarkEvent("StoryEventBus.InvokeImmediate", eventName);
        InternalInvokeAll(eventName, entry);
    }

    public static void RaiseStart(StoryEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.id))
            return;

        // Cacheado dentro de StoryEntry para evitar allocs repetidos
        Raise(entry.StartEventName, entry);
    }

    public static void RaiseEnd(StoryEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.id))
            return;

        // Cacheado dentro de StoryEntry para evitar allocs repetidos
        Raise(entry.EndEventName, entry);
    }

    // ---------------------------------------------------------------------
    // Dispatcher integration (internal)
    // ---------------------------------------------------------------------

    internal static void InternalSetDispatcherPresent(bool present) => _hasDispatcher = present;
    internal static int InternalHandlersVersion => _handlersVersion;
    internal static void InternalConfigureQueue(bool coalesceQueuedEvents, int coalesceWindowFrames)
    {
        _coalesceQueuedEvents = coalesceQueuedEvents;
        _coalesceWindowFrames = Mathf.Max(0, coalesceWindowFrames);
        if (!_coalesceQueuedEvents)
            _lastQueuedFrameByName.Clear();
    }

    internal static float InternalGetHandlerEstimatedCostMs(Delegate handler)
    {
        string key = GetHandlerKey(handler);
        if (string.IsNullOrEmpty(key))
            return 0f;

        return _handlerPerf.TryGetValue(key, out var perf) ? perf.emaMs : 0f;
    }

    internal static string InternalGetHandlerName(Delegate handler)
    {
        return GetHandlerKey(handler);
    }

    internal static bool InternalTryDequeue(out string eventName, out StoryEntry entry, out int enqueueFrame)
    {
        if (_queue.Count == 0)
        {
            eventName = null;
            entry = null;
            enqueueFrame = -1;
            return false;
        }

        var q = _queue.Dequeue();
        eventName = q.name;
        entry = q.entry;
        enqueueFrame = q.enqueueFrame;
        return true;
    }

    internal static bool InternalTryPeekEnqueueFrame(out int enqueueFrame)
    {
        if (_queue.Count == 0)
        {
            enqueueFrame = -1;
            return false;
        }

        enqueueFrame = _queue.Peek().enqueueFrame;
        return true;
    }

    /// <summary>
    /// Snapshot de los handlers actuales (alloc de Delegate[]). Usar solo en dispatcher.
    /// </summary>
    internal static Delegate[] InternalGetInvocationList()
    {
        return _onStoryEvent?.GetInvocationList();
    }

    /// <summary>
    /// Back-compat: invoca todos los handlers en el mismo hilo / frame.
    /// (Evitar usar esto desde el dispatcher si quieres time-slicing.)
    /// </summary>
    internal static void InternalInvoke(string eventName, StoryEntry entry)
    {
        InternalInvokeAll(eventName, entry);
    }

    internal static void InternalInvokeAll(string eventName, StoryEntry entry)
    {
        if (string.IsNullOrEmpty(eventName)) return;

        if (_onStoryEvent == null) return;

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.MarkEvent("StoryEventBus.InvokeAll.Begin", eventName);

        _isDispatching = true;
        try
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmInvokeAll.Auto())
            {
                _onStoryEvent.Invoke(eventName, entry);
            }
#else
            _onStoryEvent.Invoke(eventName, entry);
#endif
        }
        finally
        {
            _isDispatching = false;
        }

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.MarkEvent("StoryEventBus.InvokeAll.End", eventName);
    }

    /// <summary>
    /// Invoca UN handler (usado por el dispatcher para poder partir el trabajo en varios frames).
    /// </summary>
    internal static void InternalInvokeOne(Delegate handler, string eventName, StoryEntry entry)
    {
        if (handler == null || string.IsNullOrEmpty(eventName)) return;

        if (handler is not Action<string, StoryEntry> a) return;

        string handlerName = null;
        float started = Time.realtimeSinceStartup;
        if (StoryTransitionTrace.Enabled)
        {
            handlerName = a.Method != null
                ? $"{a.Method.DeclaringType?.Name}.{a.Method.Name}"
                : "UnknownHandler";
            StoryTransitionTrace.MarkEvent("StoryEventBus.InvokeOne.Begin", eventName, "handler=" + handlerName);
        }

        _isDispatching = true;
        try
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmInvokeOne.Auto())
            {
                a.Invoke(eventName, entry);
            }
#else
            a.Invoke(eventName, entry);
#endif
        }
        finally
        {
            _isDispatching = false;
        }

        float elapsedMs = (Time.realtimeSinceStartup - started) * 1000f;
        RecordHandlerPerf(handler, elapsedMs);

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.MarkEvent("StoryEventBus.InvokeOne.End", eventName, "handler=" + handlerName);
    }

    private static bool ShouldCoalesce(string eventName)
    {
        if (!_coalesceQueuedEvents || string.IsNullOrEmpty(eventName))
            return false;

        int now = Time.frameCount;
        if (_lastQueuedFrameByName.TryGetValue(eventName, out var lastFrame) &&
            (now - lastFrame) <= _coalesceWindowFrames)
        {
            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.MarkEvent("StoryEventBus.Coalesced", eventName, $"window={_coalesceWindowFrames}");
            return true;
        }

        _lastQueuedFrameByName[eventName] = now;
        return false;
    }

    private static string GetHandlerKey(Delegate handler)
    {
        if (handler?.Method == null)
            return string.Empty;

        return $"{handler.Method.DeclaringType?.FullName}.{handler.Method.Name}";
    }

    private static void RecordHandlerPerf(Delegate handler, float elapsedMs)
    {
        string key = GetHandlerKey(handler);
        if (string.IsNullOrEmpty(key))
            return;

        if (!_handlerPerf.TryGetValue(key, out var perf))
            perf = new HandlerPerf();

        perf.callCount++;
        perf.maxMs = Mathf.Max(perf.maxMs, elapsedMs);
        const float alpha = 0.2f;
        perf.emaMs = perf.callCount == 1 ? elapsedMs : Mathf.Lerp(perf.emaMs, elapsedMs, alpha);
        _handlerPerf[key] = perf;
    }
}
