using System;
using UnityEngine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

/// <summary>
/// Drena la cola del StoryEventBus con un presupuesto por frame.
///
/// v2 PERF:
/// - En lugar de invocar el event (multicast) de golpe, invoca handler a handler.
/// - Permite time-slicing real dentro de un mismo evento, evitando spikes en "Next".
///
/// Nota:
/// - Mantiene compatibilidad: si no existe dispatcher, StoryEventBus invoca como siempre.
/// </summary>
[DefaultExecutionOrder(-8000)]
public sealed class StoryEventBusDispatcher : MonoBehaviour
{
    public static StoryEventBusDispatcher Instance { get; private set; }
    private static bool _duplicateWarned;

    [Header("Dispatch")]
    [Tooltip("Activa el drenado de la cola del StoryEventBus.")]
    [SerializeField] private bool enableDispatch = true;

    [Tooltip("Procesa eventos en LateUpdate (recomendado). Si lo desactivas, usa Update.")]
    [SerializeField] private bool dispatchInLateUpdate = true;

    [Tooltip("Máximo de eventos por frame (hard cap). En v2 esto limita los eventos que se INICIAN por frame (un evento puede continuar en frames posteriores).")]
    [SerializeField, Min(1)] private int maxEventsPerFrame = 1;

    [Tooltip("Hard cap de handlers por frame (además del presupuesto en ms).")]
    [SerializeField, Min(1)] private int maxHandlersPerFrame = 8;

    [Tooltip("Presupuesto de tiempo (ms) por frame. Se corta si se supera.")]
    [SerializeField, Min(0f)] private float maxMillisecondsPerFrame = 2.0f;

    [Tooltip("Si estÃ¡ activo, los eventos encolados en este mismo frame se empiezan a despachar en el frame siguiente. Reduce picos al pulsar Next.")]
    [SerializeField] private bool deferFreshEventsToNextFrame = true;

    [Min(0)]
    [Tooltip("Edad minima en frames para empezar a despachar un evento recien encolado. 1 = siguiente frame, 2 = dos frames despues.")]
    [SerializeField] private int freshEventFrameDelay = 1;

    [Header("Queue Coalescing (Opt-In)")]
    [Tooltip("Deduplica eventos iguales encolados en una ventana corta de frames. OFF por defecto para mantener comportamiento actual.")]
    [SerializeField] private bool coalesceQueuedEvents = true;

    [Min(0)]
    [Tooltip("Ventana de frames para deduplicar eventos en cola cuando coalesceQueuedEvents está activo.")]
    [SerializeField] private int coalesceWindowFrames = 1;

    [Header("Safety")]
    [Tooltip("Si hay trabajo pendiente, intenta invocar al menos 1 handler aunque el presupuesto sea 0.")]
    [SerializeField] private bool alwaysProcessAtLeastOne = true;

    [Header("Debug")]
    [SerializeField] private bool verboseLogs = false;

    [Header("Heavy Listener Throttling (Opt-In)")]
    [Tooltip("Limita cuántos listeners caros se ejecutan por frame. OFF por defecto para mantener comportamiento actual.")]
    [SerializeField] private bool limitHeavyHandlersPerFrame = false;

    [SerializeField, Min(1)] private int maxHeavyHandlersPerFrame = 1;

    [SerializeField, Min(0.05f)] private float heavyHandlerThresholdMs = 0.6f;

    [Tooltip("Handlers críticos que nunca deben throttlearse (match por contains sobre nombre completo Type.Method).")]
    [SerializeField] private string[] heavyHandlerBypassContains = { "StoryActionGraphRunner", "ActorService", "Npc", "NPC" };

    [Header("Narrative Pipeline")]
    [Tooltip("Si hay transición narrativa activa, espera a la fase C antes de drenar StoryEventBus.")]
    [SerializeField] private bool respectNarrativePipelinePhases = true;

    [Tooltip("Fail-open: si el pipeline queda activo sin alcanzar fase C, libera dispatch tras N frames para evitar bloqueo de listeners.")]
    [SerializeField] private bool failOpenIfPipelineStalls = true;

    [Tooltip("Frames máximos esperando fase C antes de fail-open.")]
    [SerializeField, Min(1)] private int maxFramesWaitingForNarrativeDispatchPhase = 12;

    // Estado del evento en curso (para continuar en el siguiente frame)
    private string _curEvent;
    private StoryEntry _curEntry;
    private Delegate[] _curHandlers;
    private int _curHandlerIndex;
    private Delegate[] _cachedHandlers;
    private int _cachedHandlersVersion = -1;
    private int _pipelinePhaseWaitFrames;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticsForPlayMode()
    {
        Instance = null;
        _duplicateWarned = false;
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private static readonly ProfilerMarker _pmDispatch = new ProfilerMarker("StoryEventBus.Dispatch");
    private static readonly ProfilerMarker _pmDispatchHandlers = new ProfilerMarker("StoryEventBus.Dispatch.Handlers");
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<StoryEventBusDispatcher>() != null)
            return;
#else
        if (FindObjectOfType<StoryEventBusDispatcher>() != null)
            return;
#endif

        var go = new GameObject("_StoryEventBusDispatcher");
        go.AddComponent<StoryEventBusDispatcher>();
        DontDestroyOnLoad(go);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            if (!_duplicateWarned)
            {
                _duplicateWarned = true;
                Debug.LogWarning("[StoryEventBusDispatcher] Duplicate instance detected. Destroying newest instance to keep singleton stable.");
            }

            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        StoryEventBus.InternalSetDispatcherPresent(true);
        ApplyBusQueueConfig();
    }

    private void OnValidate()
    {
        ApplyBusQueueConfig();
    }

    private void OnDestroy()
    {
        _pipelinePhaseWaitFrames = 0;
        if (Instance == this)
        {
            Instance = null;
            StoryEventBus.InternalSetDispatcherPresent(false);
        }
    }

    private void ApplyBusQueueConfig()
    {
        StoryEventBus.InternalConfigureQueue(coalesceQueuedEvents, coalesceWindowFrames);
    }

    private void Update()
    {
        if (!dispatchInLateUpdate)
            DrainQueue();
    }

    private void LateUpdate()
    {
        if (dispatchInLateUpdate)
            DrainQueue();
    }

    private void DrainQueue()
    {
        if (!enableDispatch)
            return;

        if (StoryEventBus.IsGated)
            return;

        if (StoryEventBus.QueuedCount == 0 && _curHandlers == null)
            return;

        if (respectNarrativePipelinePhases &&
            NarrativeTransitionPipeline.IsActive &&
            !NarrativeTransitionPipeline.IsPhaseReached(NarrativeTransitionPipeline.Phase.NarrativeEventDispatch))
        {
            _pipelinePhaseWaitFrames++;
            if (!failOpenIfPipelineStalls || _pipelinePhaseWaitFrames < Mathf.Max(1, maxFramesWaitingForNarrativeDispatchPhase))
                return;

            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("StoryEventBusDispatcher.PipelineFailOpen", $"waitFrames={_pipelinePhaseWaitFrames}");
            _pipelinePhaseWaitFrames = 0;
        }
        else
        {
            _pipelinePhaseWaitFrames = 0;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmDispatch.Auto())
        {
            DrainQueueInternal();
        }
#else
        DrainQueueInternal();
#endif
    }

    private void DrainQueueInternal()
    {
        float start = Time.realtimeSinceStartup;

        int startedEventsThisFrame = 0;
        int handlersThisFrame = 0;
        int heavyHandlersThisFrame = 0;

        // 1) Procesa handlers del evento en curso primero.
        if (_curHandlers != null)
        {
            ProcessCurrentEvent(ref handlersThisFrame, ref heavyHandlersThisFrame, start);
        }

        // 2) Si aún hay presupuesto, inicia eventos nuevos (hasta maxEventsPerFrame).
        while (_curHandlers == null && startedEventsThisFrame < maxEventsPerFrame)
        {
            if (StoryEventBus.IsGated)
                break;

            // Coste objetivo: evita acumular "Raise + handlers" en el frame del input/transicion.
            // El evento debe envejecer N frames antes de iniciar su dispatch.
            if (deferFreshEventsToNextFrame &&
                StoryEventBus.InternalTryPeekEnqueueFrame(out int queuedFrame) &&
                (queuedFrame + Mathf.Max(0, freshEventFrameDelay)) > Time.frameCount)
            {
                if (StoryTransitionTrace.Enabled)
                    StoryTransitionTrace.Mark("StoryEventBusDispatcher.WaitFreshEventAge", $"queuedFrame={queuedFrame} now={Time.frameCount}");
                break;
            }

            if (!StoryEventBus.InternalTryDequeue(out _curEvent, out _curEntry, out _))
                break;

            _curHandlers = GetHandlerSnapshot();
            _curHandlerIndex = 0;
            startedEventsThisFrame++;
            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.MarkEvent("StoryEventBusDispatcher.StartEvent", _curEvent, $"handlers={(_curHandlers != null ? _curHandlers.Length : 0)} queueRemaining={StoryEventBus.QueuedCount}");

            // Si no hay handlers, termina este evento inmediatamente.
            if (_curHandlers == null || _curHandlers.Length == 0)
            {
                if (StoryTransitionTrace.Enabled)
                    StoryTransitionTrace.MarkEvent("StoryEventBusDispatcher.EmptyHandlers", _curEvent);
                ClearCurrentEvent();
                continue;
            }

            ProcessCurrentEvent(ref handlersThisFrame, ref heavyHandlersThisFrame, start);

            // Si el evento no terminó por presupuesto, salimos y continuamos el próximo frame.
            if (_curHandlers != null)
                break;

            // Si se agotó presupuesto, salimos.
            if (IsBudgetExceeded(start) || handlersThisFrame >= maxHandlersPerFrame)
                break;
        }

        // 3) Si el presupuesto es 0 pero hay trabajo, opcionalmente procesa 1 handler.
        if (handlersThisFrame == 0 && alwaysProcessAtLeastOne)
        {
            if (_curHandlers == null)
            {
                if (!StoryEventBus.IsGated && StoryEventBus.InternalTryDequeue(out _curEvent, out _curEntry, out _))
                {
                    _curHandlers = GetHandlerSnapshot();
                    _curHandlerIndex = 0;
                }
            }

            if (_curHandlers != null)
            {
                if (StoryTransitionTrace.Enabled)
                    StoryTransitionTrace.MarkEvent("StoryEventBusDispatcher.ForceOneHandler", _curEvent);
                InvokeNextHandler(ref heavyHandlersThisFrame);
                handlersThisFrame++;

                if (_curHandlers != null && _curHandlerIndex >= _curHandlers.Length)
                    ClearCurrentEvent();
            }
        }

        if (verboseLogs && (handlersThisFrame > 0 || startedEventsThisFrame > 0))
        {
            Debug.Log($"[StoryEventBusDispatcher] startedEvents={startedEventsThisFrame} handlers={handlersThisFrame} remainingEvents={StoryEventBus.QueuedCount} inFlight={(_curHandlers != null)}");
        }
    }

    private void ProcessCurrentEvent(ref int handlersThisFrame, ref int heavyHandlersThisFrame, float startTime)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmDispatchHandlers.Auto())
        {
            ProcessCurrentEventInternal(ref handlersThisFrame, ref heavyHandlersThisFrame, startTime);
        }
#else
        ProcessCurrentEventInternal(ref handlersThisFrame, ref heavyHandlersThisFrame, startTime);
#endif
    }

    private void ProcessCurrentEventInternal(ref int handlersThisFrame, ref int heavyHandlersThisFrame, float startTime)
    {
        if (_curHandlers == null) return;

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.MarkEvent("StoryEventBusDispatcher.ProcessCurrentEvent", _curEvent, $"fromHandler={_curHandlerIndex} total={_curHandlers.Length}");

        while (_curHandlerIndex < _curHandlers.Length)
        {
            if (StoryEventBus.IsGated)
                break;

            if (handlersThisFrame >= maxHandlersPerFrame)
                break;

            if (IsBudgetExceeded(startTime))
                break;

            if (limitHeavyHandlersPerFrame && maxHeavyHandlersPerFrame > 0)
            {
                var candidate = _curHandlers[_curHandlerIndex];
                float estimatedMs = StoryEventBus.InternalGetHandlerEstimatedCostMs(candidate);
                if (estimatedMs >= heavyHandlerThresholdMs && !IsCriticalHandler(candidate))
                {
                    if (heavyHandlersThisFrame >= maxHeavyHandlersPerFrame)
                    {
                        if (verboseLogs)
                            Debug.Log($"[StoryEventBusDispatcher] Throttling heavy handler='{StoryEventBus.InternalGetHandlerName(candidate)}' est={estimatedMs:0.00}ms event='{_curEvent}'");
                        break;
                    }
                }
            }

            InvokeNextHandler(ref heavyHandlersThisFrame);
            handlersThisFrame++;
        }

        if (_curHandlers != null && _curHandlerIndex >= _curHandlers.Length)
        {
            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.MarkEvent("StoryEventBusDispatcher.EventCompleted", _curEvent);
            ClearCurrentEvent();
        }
    }

    private void InvokeNextHandler(ref int heavyHandlersThisFrame)
    {
        if (_curHandlers == null || _curHandlerIndex >= _curHandlers.Length)
            return;

        var d = _curHandlers[_curHandlerIndex++];
        try
        {
            if (limitHeavyHandlersPerFrame && maxHeavyHandlersPerFrame > 0)
            {
                float estimatedMs = StoryEventBus.InternalGetHandlerEstimatedCostMs(d);
                if (estimatedMs >= heavyHandlerThresholdMs && !IsCriticalHandler(d))
                    heavyHandlersThisFrame++;
            }

            StoryEventBus.InternalInvokeOne(d, _curEvent, _curEntry);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    private bool IsCriticalHandler(Delegate handler)
    {
        string handlerName = StoryEventBus.InternalGetHandlerName(handler);
        if (string.IsNullOrEmpty(handlerName) || heavyHandlerBypassContains == null || heavyHandlerBypassContains.Length == 0)
            return false;

        for (int i = 0; i < heavyHandlerBypassContains.Length; i++)
        {
            string token = heavyHandlerBypassContains[i];
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (handlerName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private bool IsBudgetExceeded(float startTime)
    {
        if (maxMillisecondsPerFrame <= 0f) return false;
        float elapsedMs = (Time.realtimeSinceStartup - startTime) * 1000f;
        return elapsedMs >= maxMillisecondsPerFrame;
    }

    private Delegate[] GetHandlerSnapshot()
    {
        int version = StoryEventBus.InternalHandlersVersion;
        if (_cachedHandlersVersion != version)
        {
            _cachedHandlers = StoryEventBus.InternalGetInvocationList();
            _cachedHandlersVersion = version;
        }

        return _cachedHandlers;
    }

    private void ClearCurrentEvent()
    {
        _curEvent = null;
        _curEntry = null;
        _curHandlers = null;
        _curHandlerIndex = 0;
    }
}
