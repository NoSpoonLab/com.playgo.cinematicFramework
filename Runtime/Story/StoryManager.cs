using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

/// <summary>
/// Gestiona la narrativa (StoryEntries) por "escena lógica" (sceneId del guion).
/// Todo sucede en UNA escena Unity; el "sceneId" es del storyboard/guion.
///
/// Objetivos:
/// - Mantener en memoria activa SOLO las entradas del sceneId actual (_sceneEntries).
/// - Al terminar una escena lógica, intentar cargar automáticamente la siguiente (si existe) sin parpadeos de UI.
/// </summary>
public class StoryManager : MonoBehaviour
{
    [Header("Story Data")]
    [Tooltip("Base de datos Story con todas las entradas del proyecto.")]
    public StoryDatabase storyDatabase;

    [Tooltip("ID lógico de la escena actual (columna 'scene' en STORY.csv), ej. S1_HORNO.")]
    public string currentSceneId;

    [Header("Auto Flow (opcional)")]
    [Tooltip("Si está activo, al terminar una escena lógica se intentará cargar la siguiente (mismo Unity Scene).")]
    public bool autoAdvanceToNextScene = true;

    [Tooltip("Si no se asigna, se buscará en la escena.")]
    public StoryboardFlowController flowController;

    [Header("Estado (solo lectura)")]
    [SerializeField] private int currentIndex = -1;
    [SerializeField] private StoryEntry currentEntry;

    [Tooltip("Entradas activas del sceneId actual (solo este capítulo).")]
    [SerializeField] private List<StoryEntry> _sceneEntries = new List<StoryEntry>(256);

    [Header("Runtime")]
    [SerializeField] private bool isPrepared = false;
    [SerializeField] private bool isStarted = false;

    [Header("Performance")]
    [Tooltip("Si está activo, NextEntry se ejecuta en coroutine y puede repartir pasos entre frames.")]
    public bool deferNextEntryWork = true;

    [Tooltip("Frames a esperar entre cerrar entrada (End) y abrir la siguiente (Start). 0 = mismo frame.")]
    [Range(0, 3)]
    public int yieldFramesBetweenEndAndStart = 1;

    [Tooltip("Si está activo, los Raise(Start/End) se encolan y se despachan budgeted por StoryEventBusDispatcher.")]
    public bool useEventBusDispatcher = true;

    [Tooltip("Si está activo, OnEntryChanged se dispara de forma diferida (siguiente frame) para separar UI/TMP del dispatch de StoryEvents.")]
    public bool deferEntryChangedToNextFrame = true;

    [Tooltip("Frames a esperar tras fijar la nueva entrada (y hacer RaiseStart) antes de disparar OnEntryChanged. 0 = mismo frame.")]
    [Range(0, 3)]
    public int yieldFramesAfterStartBeforeEntryChanged = 1;

    [Header("Narrative Pipeline")]
    [Tooltip("Activa pipeline explícito por fases para repartir transiciones narrativas en varios frames.")]
    [SerializeField] private bool useNarrativeTransitionPipeline = true;

    [Tooltip("Frames entre fase A (resolver entry) y fase B (refresh visual/UI).")]
    [Range(0, 4)] [SerializeField] private int pipelineFramesAfterResolve = 0;

    [Tooltip("Frames entre fase B (UI) y fase C (dispatch de eventos narrativos).")]
    [Range(0, 4)] [SerializeField] private int pipelineFramesAfterUi = 1;

    [Tooltip("Frames entre fase C (event dispatch) y fase D (acciones no críticas).")]
    [Range(0, 4)] [SerializeField] private int pipelineFramesAfterEvents = 1;

    [Tooltip("Frames entre fase D (acciones) y fase E (cambios pesados de objetos).")]
    [Range(0, 4)] [SerializeField] private int pipelineFramesAfterActions = 1;

    [Tooltip("Frames entre fase E (SceneObjectService) y fase F (teleport/placement final).")]
    [Range(0, 4)] [SerializeField] private int pipelineFramesAfterHeavyObjects = 1;

    [Tooltip("Logs del avance de fases del NarrativeTransitionPipeline.")]
    [SerializeField] private bool verboseNarrativePipelineLogs = false;

    [Header("Input Guard")]
    [Tooltip("Evita que pulsaciones/rebotes de Next en ráfaga encolen muchos avances seguidos.")]
    public bool coalesceRapidNextRequests = true;

    [Tooltip("Tiempo mínimo (segundos, unscaled) entre aceptaciones de NextEntry.")]
    [Range(0f, 1f)]
    public float minSecondsBetweenNextRequests = 0.2f;

    [Header("Debug")]
    [Tooltip("Activa logs de StoryEventBus emitidos por StoryManager (Start/End/Feedback).")]
    public bool debugStoryEvents = false;

    [Tooltip("Añade contexto extendido (tipo de entrada, índice y escena lógica).")]
    public bool verboseStoryEvents = false;

    [Tooltip("Activa una traza transversal por transicion narrativa (pipeline completo).")]
    [SerializeField] private bool enableTransitionTrace = false;

    [Tooltip("Logs compactos de StoryTransitionTrace para mapear cascadas por frame.")]
    [SerializeField] private bool verboseTransitionTraceLogs = false;

    /// <summary>Se dispara cada vez que cambia la entrada actual.</summary>
    public event Action OnEntryChanged;

    /// <summary>Se dispara cuando se evalúa una respuesta.</summary>
    public event Action<bool, StoryEntry> OnAnswerEvaluated;

    /// <summary>Se dispara cuando la historia empieza realmente.</summary>
    public event Action OnStoryStarted;

    /// <summary>
    /// Se dispara justo antes de cambiar de escena lógica (sceneId del guion).
    /// Útil para activar/desactivar contenido.
    /// </summary>
    public event Action<string, string> OnLogicalSceneWillChange;

    /// <summary>Se dispara después de cambiar de escena lógica (sceneId del guion).</summary>
    public event Action<string> OnLogicalSceneChanged;

    // ---------------------------------------------------------------------
    // UNITY
    // ---------------------------------------------------------------------

    private void Awake()
    {
        StoryTransitionTrace.Configure(enableTransitionTrace, verboseTransitionTraceLogs);
        NarrativeTransitionPipeline.Configure(verboseNarrativePipelineLogs);
        Prepare();
    }

    private void OnDestroy()
    {
        if (NarrativeTransitionPipeline.IsActive)
            NarrativeTransitionPipeline.End("StoryManager.OnDestroy");
    }

    // ---------------------------------------------------------------------
    // PREPARACIÓN
    // ---------------------------------------------------------------------

    private void Prepare()
    {
        if (isPrepared)
            return;

        if (storyDatabase == null)
        {
            Debug.LogError("[StoryManager] No hay StoryDatabase asignado.");
            return;
        }

        // Encontrar FlowController si no está asignado
        if (flowController == null)
        {
#if UNITY_2023_1_OR_NEWER
            flowController = FindFirstObjectByType<StoryboardFlowController>();
#else
            flowController = FindObjectOfType<StoryboardFlowController>();
#endif
        }

        // Si no viene currentSceneId, usamos el primero del FlowController o el primero detectado.
        if (string.IsNullOrEmpty(currentSceneId))
        {
            string first = null;

            if (flowController != null)
                first = flowController.GetFirstSceneIdFromDatabase(storyDatabase);

            if (string.IsNullOrEmpty(first))
                first = GetFirstSceneIdFallback();

            currentSceneId = first;
            Debug.Log($"[StoryManager] currentSceneId vacío. Usando '{currentSceneId}'.");
        }

        LoadScene(currentSceneId);

        isPrepared = true;
        Debug.Log("[StoryManager] Preparado. Esperando StartStory().");
    }

    private string GetFirstSceneIdFallback()
    {
        if (storyDatabase == null || storyDatabase.entries == null)
            return null;

        for (int i = 0; i < storyDatabase.entries.Count; i++)
        {
            var e = storyDatabase.entries[i];
            if (e == null) continue;
            if (string.IsNullOrEmpty(e.scene)) continue;
            return e.scene;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // ARRANQUE EXPLÍCITO
    // ---------------------------------------------------------------------

    public void StartStory()
    {
        if (!isPrepared)
        {
            Debug.LogWarning("[StoryManager] StartStory llamado sin Prepare.");
            return;
        }

        if (isStarted)
        {
            Debug.LogWarning("[StoryManager] La historia ya está iniciada.");
            return;
        }

        isStarted = true;

        // Notificar escena lógica actual (para que el loader active contenido ANTES del primer entry)
        OnLogicalSceneChanged?.Invoke(currentSceneId);

        OnStoryStarted?.Invoke();
        NextEntry();
    }

    // ---------------------------------------------------------------------
    // ESCENA LÓGICA
    // ---------------------------------------------------------------------

    public void LoadScene(string sceneId)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Spike candidate: recarga de entradas y ordenado al cambiar escena lógica.
        // Coste esperado: CPU (filtro + sort).
        using var _scope = _pmLoadScene.Auto();
#endif
        StoryTransitionTrace.Mark("StoryManager.LoadScene.Begin", "scene=" + sceneId);
        if (string.IsNullOrEmpty(sceneId))
        {
            Debug.LogError("[StoryManager] LoadScene con sceneId vacío.");
            _sceneEntries.Clear();
            currentIndex = -1;
            currentEntry = null;
            return;
        }

        currentSceneId = sceneId;

        // Reutilizamos la lista para NO retener entradas de otros capítulos
        _sceneEntries.Clear();

        var all = storyDatabase.entries;
        if (all != null)
        {
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                if (e == null) continue;
                if (!string.Equals(e.scene, sceneId, StringComparison.OrdinalIgnoreCase)) continue;
                _sceneEntries.Add(e);
            }
        }

        // Ordenar por "order" (sin LINQ)
        _sceneEntries.Sort((a, b) =>
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            return a.order.CompareTo(b.order);
        });

        currentIndex = -1;
        currentEntry = null;
        StoryTransitionTrace.SetCurrentEntry(null);

        Debug.Log($"[StoryManager] {_sceneEntries.Count} entradas activas para '{sceneId}'.");
        StoryTransitionTrace.Mark("StoryManager.LoadScene.End", $"scene={sceneId} entries={_sceneEntries.Count}");
    }

    // ---------------------------------------------------------------------
    // NAVEGACIÓN
    // ---------------------------------------------------------------------

    private Coroutine _nextRoutine;
    private int _pendingNextRequests = 0;
    private float _nextRequestEarliestTime = 0f;

    private Coroutine _notifyEntryChangedRoutine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private static readonly ProfilerMarker _pmNextEntry = new ProfilerMarker("StoryManager.NextEntry");
    // PERF NOTE:
    // Separar coste de transición lógica (carga de chapter y avance) del coste en listeners/UI.
    private static readonly ProfilerMarker _pmLoadScene = new ProfilerMarker("StoryManager.LoadScene");
    private static readonly ProfilerMarker _pmAutoAdvanceScene = new ProfilerMarker("StoryManager.TryAutoAdvanceToNextLogicalScene");
    private static readonly ProfilerMarker _pmNotifyEntryChanged = new ProfilerMarker("StoryManager.NotifyEntryChanged");
    private static readonly ProfilerMarker _pmPipelinePhaseA = new ProfilerMarker("StoryManager.Pipeline.PhaseA.Resolve");
    private static readonly ProfilerMarker _pmPipelinePhaseB = new ProfilerMarker("StoryManager.Pipeline.PhaseB.UI");
    private static readonly ProfilerMarker _pmPipelinePhaseC = new ProfilerMarker("StoryManager.Pipeline.PhaseC.Events");
    private static readonly ProfilerMarker _pmPipelinePhaseD = new ProfilerMarker("StoryManager.Pipeline.PhaseD.Actions");
    private static readonly ProfilerMarker _pmPipelinePhaseE = new ProfilerMarker("StoryManager.Pipeline.PhaseE.SceneObjects");
    private static readonly ProfilerMarker _pmPipelinePhaseF = new ProfilerMarker("StoryManager.Pipeline.PhaseF.Teleport");
#endif

    public void NextEntry()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmNextEntry.Auto())
        {
            NextEntryInternal();
        }
#else
        NextEntryInternal();
#endif
    }

    private void NextEntryInternal()
    {
        StoryTransitionTrace.Begin("StoryManager.NextEntry", currentEntry != null ? currentEntry.id : null, $"scene={currentSceneId} idx={currentIndex}");

        if (!isStarted)
        {
            StoryTransitionTrace.Mark("StoryManager.NextEntry.Rejected", "reason=NotStarted");
            Debug.LogWarning("[StoryManager] NextEntry antes de StartStory().");
            return;
        }

        if (coalesceRapidNextRequests && minSecondsBetweenNextRequests > 0f)
        {
            float now = Time.unscaledTime;
            if (now < _nextRequestEarliestTime)
            {
                StoryTransitionTrace.Mark("StoryManager.NextEntry.Rejected", "reason=Cooldown");
                return;
            }

            _nextRequestEarliestTime = now + minSecondsBetweenNextRequests;
        }

        // Si no quieres dispatcher, mantenemos el comportamiento anterior (aunque sea más pesado).
        // Ojo: el dispatcher se activa automáticamente con StoryEventBusDispatcher en escena.
        if (!deferNextEntryWork)
        {
            StoryTransitionTrace.Mark("StoryManager.NextEntry.Path", "mode=Immediate");
            NextEntryImmediate();
            return;
        }

        if (_pendingNextRequests < 1)
            _pendingNextRequests++;

        string mode = useNarrativeTransitionPipeline ? "Pipeline" : "DeferredLegacy";
        StoryTransitionTrace.Mark("StoryManager.NextEntry.Path", $"mode={mode} pending={_pendingNextRequests}");

        if (_nextRoutine == null)
            _nextRoutine = StartCoroutine(useNarrativeTransitionPipeline ? NextEntryPipelineRoutine() : NextEntryRoutine());
    }

    // Ruta antigua (sin yields): útil para comparar.
    private void NextEntryImmediate()
    {
        StoryTransitionTrace.Mark("StoryManager.NextEntryImmediate.Begin");

        // Cerrar la entrada anterior
        if (currentEntry != null && !string.IsNullOrEmpty(currentEntry.id))
        {
            StoryTransitionTrace.Mark("StoryManager.NextEntryImmediate.RaiseEnd", "entry=" + currentEntry.id);
            RaiseEndWithDebug(currentEntry, "NextEntryImmediate");
        }

        currentIndex++;

        // ¿Fin de escena lógica?
        if (_sceneEntries == null || currentIndex >= _sceneEntries.Count)
        {
            // Intento de auto-avance (sin pasar por entry = null para no "parpadear" UI)
            if (autoAdvanceToNextScene && flowController != null)
            {
                if (TryAutoAdvanceToNextLogicalScene())
                    return;
            }

            // Fin real (no hay siguiente)
            currentEntry = null;
            StoryTransitionTrace.SetCurrentEntry(null);
            ScheduleNotifyEntryChanged();
            Debug.Log($"[StoryManager] Fin de escena '{currentSceneId}'.");
            StoryTransitionTrace.End("StoryManager.NextEntryImmediate.End", "result=EndOfStoryOrScene");
            return;
        }

        // Entrada normal
        currentEntry = _sceneEntries[currentIndex];
        StoryTransitionTrace.SetCurrentEntry(currentEntry != null ? currentEntry.id : null);

        if (!string.IsNullOrEmpty(currentEntry.id))
        {
            StoryTransitionTrace.Mark("StoryManager.NextEntryImmediate.RaiseStart", "entry=" + currentEntry.id);
            RaiseStartWithDebug(currentEntry, "NextEntryImmediate");
        }

        ScheduleNotifyEntryChanged();
        StoryTransitionTrace.End("StoryManager.NextEntryImmediate.End", "result=EntryReady");
    }

    // Pipeline agresivo por fases:
    // A Resolve -> B UI -> C Events -> D Actions -> E SceneObjects -> F Teleport.
    private System.Collections.IEnumerator NextEntryPipelineRoutine()
    {
        while (_pendingNextRequests > 0)
        {
            _pendingNextRequests--;

            NarrativeTransitionPipeline.Begin("StoryManager.NextEntry", $"scene={currentSceneId} idx={currentIndex}");

            StoryEntry previousEntry;
            StoryEntry nextEntry;

            NarrativeTransitionPipeline.EnterPhase(NarrativeTransitionPipeline.Phase.ResolveEntry);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmPipelinePhaseA.Auto())
#endif
            {
                ResolveNextEntryForPipeline(out previousEntry, out nextEntry);
            }

            yield return YieldPipelineFrames(pipelineFramesAfterResolve);

            NarrativeTransitionPipeline.EnterPhase(NarrativeTransitionPipeline.Phase.VisualRefresh);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmPipelinePhaseB.Auto())
#endif
            {
                if (_notifyEntryChangedRoutine != null)
                {
                    StopCoroutine(_notifyEntryChangedRoutine);
                    _notifyEntryChangedRoutine = null;
                }

                // Fase B: coste visual/UI explícitamente separado de eventos.
                NotifyEntryChanged();
            }

            yield return YieldPipelineFrames(pipelineFramesAfterUi);

            NarrativeTransitionPipeline.EnterPhase(NarrativeTransitionPipeline.Phase.NarrativeEventDispatch);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmPipelinePhaseC.Auto())
#endif
            {
                // Fase C: emisión narrativa. El dispatcher trocea handlers por presupuesto.
                if (previousEntry != null && !string.IsNullOrEmpty(previousEntry.id))
                    RaiseEndWithDebug(previousEntry, "NextEntryPipeline.PhaseC");

                if (nextEntry != null && !string.IsNullOrEmpty(nextEntry.id))
                    RaiseStartWithDebug(nextEntry, "NextEntryPipeline.PhaseC");
            }

            yield return YieldPipelineFrames(pipelineFramesAfterEvents);

            NarrativeTransitionPipeline.EnterPhase(NarrativeTransitionPipeline.Phase.NonCriticalActions);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmPipelinePhaseD.Auto())
#endif
            {
                // Fase D: gate explícito para GraphRunner/listeners no críticos.
            }

            yield return YieldPipelineFrames(pipelineFramesAfterActions);

            NarrativeTransitionPipeline.EnterPhase(NarrativeTransitionPipeline.Phase.HeavyObjectChanges);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmPipelinePhaseE.Auto())
#endif
            {
                // Fase E: SceneObjectService y activaciones pesadas quedan permitidas desde aquí.
            }

            yield return YieldPipelineFrames(pipelineFramesAfterHeavyObjects);

            NarrativeTransitionPipeline.EnterPhase(NarrativeTransitionPipeline.Phase.TeleportAndPlacement);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmPipelinePhaseF.Auto())
#endif
            {
                // Fase F: placement/teleport/side effects finales.
            }

            NarrativeTransitionPipeline.End("completed");
        }

        _nextRoutine = null;
    }

    private static System.Collections.IEnumerator YieldPipelineFrames(int frames)
    {
        int f = Mathf.Max(0, frames);
        for (int i = 0; i < f; i++)
            yield return null;
    }

    private void ResolveNextEntryForPipeline(out StoryEntry previousEntry, out StoryEntry nextEntry)
    {
        previousEntry = currentEntry;
        nextEntry = null;

        currentIndex++;

        if (_sceneEntries == null || currentIndex >= _sceneEntries.Count)
        {
            bool advanced = false;
            if (autoAdvanceToNextScene && flowController != null)
                advanced = TryAutoAdvanceToNextLogicalScene(emitStartAndNotify: false);

            if (!advanced)
            {
                currentEntry = null;
                StoryTransitionTrace.SetCurrentEntry(null);
                return;
            }
        }
        else
        {
            currentEntry = _sceneEntries[currentIndex];
        }

        nextEntry = currentEntry;
        StoryTransitionTrace.SetCurrentEntry(nextEntry != null ? nextEntry.id : null);
    }

    private System.Collections.IEnumerator NextEntryRoutine()
    {
        StoryTransitionTrace.Mark("StoryManager.NextEntryRoutine.Begin", "pending=" + _pendingNextRequests);
        // Cola simple: si el usuario pulsa Next varias veces rápido, procesamos en orden.
        while (_pendingNextRequests > 0)
        {
            _pendingNextRequests--;
            yield return NextEntryRoutineStep();
        }

        _nextRoutine = null;
        StoryTransitionTrace.Mark("StoryManager.NextEntryRoutine.End");
    }

    private System.Collections.IEnumerator NextEntryRoutineStep()
    {
        StoryTransitionTrace.Mark("StoryManager.NextEntryRoutineStep.Begin", $"idx={currentIndex} scene={currentSceneId}");
        // 1) End de la entrada anterior (se encola, no debe disparar handlers pesados en el mismo frame).
        if (currentEntry != null && !string.IsNullOrEmpty(currentEntry.id))
        {
            StoryTransitionTrace.Mark("StoryManager.NextEntryRoutineStep.RaiseEnd", "entry=" + currentEntry.id);
            RaiseEndWithDebug(currentEntry, "NextEntryRoutineStep");
        }

        // 2) Espera opcional para separar End y Start.
        for (int i = 0; i < yieldFramesBetweenEndAndStart; i++)
            yield return null;

        StoryTransitionTrace.Mark("StoryManager.NextEntryRoutineStep.AfterEndDelay", "frames=" + yieldFramesBetweenEndAndStart);

        // 3) Avanzar índice.
        currentIndex++;

        // 4) ¿Fin de escena lógica?
        if (_sceneEntries == null || currentIndex >= _sceneEntries.Count)
        {
            // Intento de auto-avance (sin pasar por entry = null para no "parpadear" UI)
            if (autoAdvanceToNextScene && flowController != null)
            {
                if (TryAutoAdvanceToNextLogicalScene())
                {
                    ScheduleNotifyEntryChanged();
                    StoryTransitionTrace.End("StoryManager.NextEntryRoutineStep.End", "result=AutoAdvanceScene");
                    yield break;
                }
            }

            // Fin real (no hay siguiente)
            currentEntry = null;
            StoryTransitionTrace.SetCurrentEntry(null);
            ScheduleNotifyEntryChanged();
            Debug.Log($"[StoryManager] Fin de escena '{currentSceneId}'.");
            StoryTransitionTrace.End("StoryManager.NextEntryRoutineStep.End", "result=EndOfStoryOrScene");
            yield break;
        }

        // 5) Entrada normal
        currentEntry = _sceneEntries[currentIndex];
        StoryTransitionTrace.SetCurrentEntry(currentEntry != null ? currentEntry.id : null);

        if (currentEntry != null && !string.IsNullOrEmpty(currentEntry.id))
        {
            StoryTransitionTrace.Mark("StoryManager.NextEntryRoutineStep.RaiseStart", "entry=" + currentEntry.id);
            RaiseStartWithDebug(currentEntry, "NextEntryRoutineStep");
        }

        // 6) UI / listeners de OnEntryChanged siguen siendo inmediatos.
        ScheduleNotifyEntryChanged();
        StoryTransitionTrace.End("StoryManager.NextEntryRoutineStep.End", "result=EntryReady");
    }

    private bool TryAutoAdvanceToNextLogicalScene(bool emitStartAndNotify = true)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using var _scope = _pmAutoAdvanceScene.Auto();
#endif
        if (flowController == null)
            return false;

        StoryTransitionTrace.Mark("StoryManager.AutoAdvance.Try", "from=" + currentSceneId);

        // Pedir siguiente sceneId
        if (!flowController.TryGetNextSceneId(currentSceneId, storyDatabase, out var nextSceneId))
            return false;

        if (string.IsNullOrEmpty(nextSceneId) || string.Equals(nextSceneId, currentSceneId, StringComparison.OrdinalIgnoreCase))
            return false;

        string from = currentSceneId;
        string to = nextSceneId;
        StoryTransitionTrace.Mark("StoryManager.AutoAdvance.SceneChange", $"from={from} to={to}");

        OnLogicalSceneWillChange?.Invoke(from, to);

        LoadScene(to);

        OnLogicalSceneChanged?.Invoke(to);

        // Si el siguiente capítulo no tiene entradas, intentamos saltar al siguiente (evita quedarse "muerto")
        // Máximo de saltos: 8 para evitar bucles por datos corruptos.
        int safety = 8;
        while (safety-- > 0 && (_sceneEntries == null || _sceneEntries.Count == 0))
        {
            Debug.LogWarning($"[StoryManager] La escena lógica '{currentSceneId}' no tiene entradas. Saltando a la siguiente si existe...");
            if (!flowController.TryGetNextSceneId(currentSceneId, storyDatabase, out var next))
                break;

            OnLogicalSceneWillChange?.Invoke(currentSceneId, next);
            LoadScene(next);
            OnLogicalSceneChanged?.Invoke(next);
        }

        // Arrancar primera entrada del nuevo capítulo (sin llamar NextEntry para evitar recursión)
        currentIndex = 0;

        if (_sceneEntries == null || _sceneEntries.Count == 0)
        {
            currentEntry = null;
            StoryTransitionTrace.SetCurrentEntry(null);
            if (emitStartAndNotify)
                ScheduleNotifyEntryChanged();
            return true;
        }

        currentEntry = _sceneEntries[currentIndex];
        StoryTransitionTrace.SetCurrentEntry(currentEntry != null ? currentEntry.id : null);

        if (emitStartAndNotify && !string.IsNullOrEmpty(currentEntry.id))
        {
            StoryTransitionTrace.Mark("StoryManager.AutoAdvance.RaiseStart", "entry=" + currentEntry.id);
            RaiseStartWithDebug(currentEntry, "TryAutoAdvanceToNextLogicalScene");
        }

        if (emitStartAndNotify)
            ScheduleNotifyEntryChanged();
        return true;
    }

    public bool JumpToEntryById(string targetId)
    {
        if (!isStarted || _sceneEntries == null)
            return false;

        int index = _sceneEntries.FindIndex(e => e != null && e.id == targetId);
        if (index < 0)
            return false;

        if (currentEntry != null)
        {
            RaiseEndWithDebug(currentEntry, "JumpToEntryById");
        }

        currentIndex = index;
        currentEntry = _sceneEntries[currentIndex];

        RaiseStartWithDebug(currentEntry, "JumpToEntryById");
        ScheduleNotifyEntryChanged();
        return true;
    }

    public bool TryJumpToPreviousEntry()
    {
        if (!isStarted || currentIndex <= 0)
            return false;

        RaiseEndWithDebug(currentEntry, "TryJumpToPreviousEntry");

        currentIndex--;
        currentEntry = _sceneEntries[currentIndex];

        RaiseStartWithDebug(currentEntry, "TryJumpToPreviousEntry");
        ScheduleNotifyEntryChanged();
        return true;
    }

    // ---------------------------------------------------------------------
    // RESPUESTAS
    // ---------------------------------------------------------------------

    public void EvaluateCurrentAnswer(Enums.StoryAnswerOption answer)
    {
        if (currentEntry == null || currentEntry.type != Enums.StoryEntryType.QUESTION)
            return;

        bool isCorrect = currentEntry.correct == answer;
        
        // Eventos para VO: <id>_FB_OK / <id>_FB_KO
        if (!string.IsNullOrEmpty(currentEntry.id))
        {
            string fbVoiceId = VoiceIdUtil.ForFeedback(currentEntry, isCorrect);
            if (!string.IsNullOrEmpty(fbVoiceId))
                RaiseFeedbackWithDebug(fbVoiceId, currentEntry, isCorrect, "EvaluateCurrentAnswer");

            // Evento semántico adicional para graph/animaciones (no sustituye al evento VO).
            string fbSemanticId = StoryQuestionEventNames.ForFeedback(currentEntry, isCorrect);
            if (!string.IsNullOrEmpty(fbSemanticId))
                RaiseFeedbackWithDebug(fbSemanticId, currentEntry, isCorrect, "EvaluateCurrentAnswer.Semantic");
        }
        
        OnAnswerEvaluated?.Invoke(isCorrect, currentEntry);
    }

    // ---------------------------------------------------------------------
    // HELPERS
    // ---------------------------------------------------------------------

    private void ScheduleNotifyEntryChanged()
    {
        if (!deferEntryChangedToNextFrame || yieldFramesAfterStartBeforeEntryChanged <= 0)
        {
            StoryTransitionTrace.Mark("StoryManager.ScheduleEntryChanged", "mode=Immediate");
            NotifyEntryChanged();
            return;
        }

        // Cancelar notificación previa para evitar múltiples RefreshUI seguidos cuando el usuario spamea Next.
        if (_notifyEntryChangedRoutine != null)
            StopCoroutine(_notifyEntryChangedRoutine);

        StoryTransitionTrace.Mark("StoryManager.ScheduleEntryChanged", "mode=Deferred frames=" + yieldFramesAfterStartBeforeEntryChanged);
        _notifyEntryChangedRoutine = StartCoroutine(NotifyEntryChangedDeferredRoutine(yieldFramesAfterStartBeforeEntryChanged));
    }

    private System.Collections.IEnumerator NotifyEntryChangedDeferredRoutine(int framesDelay)
    {
        for (int i = 0; i < framesDelay; i++)
            yield return null;

        NotifyEntryChanged();
        _notifyEntryChangedRoutine = null;
    }

    private void NotifyEntryChanged()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using var _scope = _pmNotifyEntryChanged.Auto();
#endif
        StoryTransitionTrace.Mark("StoryManager.NotifyEntryChanged", $"idx={currentIndex} scene={currentSceneId}");
        OnEntryChanged?.Invoke();
    }

    private void RaiseStartWithDebug(StoryEntry entry, string source)
    {
        if (entry == null || string.IsNullOrEmpty(entry.id))
            return;

        if (debugStoryEvents)
            LogStoryEvent("Start", entry.StartEventName, entry, source, null);

        StoryEventBus.RaiseStart(entry);
    }

    private void RaiseEndWithDebug(StoryEntry entry, string source)
    {
        if (entry == null || string.IsNullOrEmpty(entry.id))
            return;

        if (debugStoryEvents)
            LogStoryEvent("End", entry.EndEventName, entry, source, null);

        StoryEventBus.RaiseEnd(entry);
    }

    private void RaiseFeedbackWithDebug(string eventName, StoryEntry entry, bool isCorrect, string source)
    {
        if (string.IsNullOrEmpty(eventName))
            return;

        if (debugStoryEvents)
            LogStoryEvent("Feedback", eventName, entry, source, isCorrect);

        StoryEventBus.Raise(eventName, entry);
    }

    private void LogStoryEvent(string kind, string eventName, StoryEntry entry, string source, bool? isCorrect)
    {
        if (!debugStoryEvents)
            return;

        if (!verboseStoryEvents)
        {
            Debug.Log($"[StoryManager][Event] {kind} -> '{eventName}' ({source})", this);
            return;
        }

        string entryId = entry != null ? entry.id : "<null>";
        string entryType = entry != null ? entry.type.ToString() : "<null>";
        string correctness = isCorrect.HasValue ? $" isCorrect={isCorrect.Value}" : string.Empty;

        Debug.Log(
            $"[StoryManager][Event] {kind} -> '{eventName}' ({source}) id={entryId} type={entryType} scene={currentSceneId} index={currentIndex}{correctness}",
            this);
    }

    public StoryEntry GetCurrentEntry()
    {
        return currentEntry;
    }
}
