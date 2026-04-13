using System.Collections;
using UnityEngine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

public class ExperienceManager : MonoBehaviour
{
    public static ExperienceManager Instance { get; private set; }
    private static bool _duplicateWarned;

    [Header("Config")]
    public ExperienceConfig config;

    [Header("References")]
    public Transform xrRig;
    [Tooltip("Opcional. Si lo tienes, se mueve este; si no, se mueve xrRig.")]
    public Transform locomotorRoot;

    [Header("Optional Services")]
    public EnvironmentSceneLoader environmentLoader;
    public FadeManager fadeManager;
    public StoryManager storyManager;

    [Header("Startup")]
    public bool autoBootOnStart = true;
    public bool autoStartStoryAfterBoot = false;
    public Enums.PlacementContext startupContext = Enums.PlacementContext.Startup;

    [Header("Teleport")]
    public bool useFade = true;
    [Min(0f)] public float teleportFadeOut = 0.15f;
    [Min(0f)] public float teleportFadeIn = 0.10f;
    [Min(0.1f)] public float waitEnvironmentTimeout = 20f;
    [Min(0.1f)] public float waitAnchorTimeout = 3f;

    [Tooltip("Frames a esperar antes de empezar el teleport cuando se llama desde un handler del StoryEventBus (evita coincidir con Canvas/TMP).")]
    [Min(0)] public int deferFramesWhenCalledFromDispatch = 2;

    [Tooltip("Frames a esperar cuando NO viene del dispatch (back-compat: 1).")]
    [Min(0)] public int defaultPreTeleportDelayFrames = 1;

    [Header("Teleport Scheduling")]
    [Tooltip("Si está activo, evita solicitudes duplicadas de teleport dentro de una ventana corta de frames.")]
    public bool coalesceEquivalentTeleportRequests = true;

    [Tooltip("Frames de ventana para considerar duplicadas dos solicitudes equivalentes.")]
    [Min(0)] public int teleportDuplicateWindowFrames = 2;

    [Tooltip("Si está activo, cuando llega una nueva solicitud se mantiene solo la última pendiente (evita backlog durante transiciones narrativas).")]
    public bool keepOnlyLatestPendingTeleport = true;

    [Tooltip("Si está activo, cuando hay backlog en StoryEventBus se fuerza un pequeño defer para no solapar teleport con dispatch pesado.")]
    public bool deferWhenStoryEventBacklog = true;

    [Tooltip("Frames mínimos de defer cuando hay backlog de StoryEventBus.")]
    [Min(0)] public int deferFramesWhenStoryEventBacklog = 1;

    [Header("Teleport Runtime Optimization")]
    [Tooltip("Reutiliza el último anchor resuelto para evitar búsquedas repetidas cuando el placement no cambia.")]
    public bool reuseLastResolvedAnchor = true;

    [Tooltip("Si está activo y el player ya está prácticamente en el anchor, evita fade/move redundante.")]
    public bool skipRedundantTeleportWhenAlreadyAtTarget = true;

    [Min(0f)] public float redundantTeleportPositionEpsilon = 0.01f;
    [Min(0f)] public float redundantTeleportAngleEpsilon = 0.5f;

    [Header("Narrative Pipeline")]
    [Tooltip("Si hay transición narrativa activa, retrasa teleports hasta la fase F del pipeline.")]
    public bool respectNarrativePipelinePhases = true;

    [Header("Debug")]
    public bool verboseLogs = true;
    [Tooltip("Permite logs en device (Quest/Android). OJO: Debug.Log puede meter spikes.")]
    public bool allowDeviceLogs = false;

    private Coroutine _boot;
    private Coroutine _teleportWorker;

    private struct TeleportRequest
    {
        public string placementID;
        public string reason;
        public int preDelayFrames;
        public int frameRequested;
    }

    private readonly System.Collections.Generic.Queue<TeleportRequest> _teleportQueue =
        new System.Collections.Generic.Queue<TeleportRequest>(8);

    private string _activeTeleportPlacementId;
    private string _lastEnqueuedPlacementId;
    private int _lastEnqueueFrame = -9999;

    private string _cachedAnchorPlacementId;
    private Transform _cachedAnchorTransform;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // PERF NOTE:
    // Teleports durante transición pueden solaparse con UI/TMP y cargas de entorno.
    // Se marcan solo secciones no-coroutine para evitar Begin/End desbalanceados entre frames.
    private static readonly ProfilerMarker _pmPlaceByContext = new ProfilerMarker("ExperienceManager.PlacePlayerByContext");
    private static readonly ProfilerMarker _pmTryTeleport = new ProfilerMarker("ExperienceManager.TryTeleportPlayerToPlacementID");
    private static readonly ProfilerMarker _pmTeleportStart = new ProfilerMarker("ExperienceManager.TeleportRoutine.Start");
    private static readonly ProfilerMarker _pmQueueTeleport = new ProfilerMarker("ExperienceManager.Teleport.QueueRequest");
    private static readonly ProfilerMarker _pmProcessTeleportQueue = new ProfilerMarker("ExperienceManager.Teleport.ProcessQueue");
    private static readonly ProfilerMarker _pmCoalesceTeleport = new ProfilerMarker("ExperienceManager.Teleport.Coalesce");
    private static readonly ProfilerMarker _pmAnchorResolve = new ProfilerMarker("ExperienceManager.Teleport.ResolveAnchor");
    private static readonly ProfilerMarker _pmAnchorWait = new ProfilerMarker("ExperienceManager.TeleportRoutine.WaitAnchor");
    private static readonly ProfilerMarker _pmSetRig = new ProfilerMarker("ExperienceManager.TeleportRoutine.SetRig");
#endif

    private bool ShouldLog => verboseLogs && (Application.isEditor || allowDeviceLogs);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticsForPlayMode()
    {
        Instance = null;
        _duplicateWarned = false;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            if (!_duplicateWarned)
            {
                _duplicateWarned = true;
                Debug.LogWarning("[ExperienceManager] Duplicate instance detected. Destroying newest instance to keep singleton stable.");
            }

            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        if (fadeManager == null) fadeManager = FadeManager.Instance;

        ResolveOptionalReferences();
    }

    private void OnDestroy()
    {
        if (_teleportWorker != null)
        {
            StopCoroutine(_teleportWorker);
            _teleportWorker = null;
        }

        _teleportQueue.Clear();
        ClearAnchorCache();

        if (Instance == this)
            Instance = null;
    }

    private void ResolveOptionalReferences()
    {
        if (storyManager == null)
            storyManager = GetComponentInParent<StoryManager>(true);

        if (environmentLoader == null)
            environmentLoader = GetComponentInParent<EnvironmentSceneLoader>(true);
    }

    private void ResolveRuntimeReferencesIfNeeded()
    {
        if (storyManager == null)
        {
#if UNITY_2023_1_OR_NEWER
            storyManager = FindFirstObjectByType<StoryManager>();
#else
            storyManager = FindObjectOfType<StoryManager>();
#endif
        }

        if (environmentLoader == null)
        {
#if UNITY_2023_1_OR_NEWER
            environmentLoader = FindFirstObjectByType<EnvironmentSceneLoader>();
#else
            environmentLoader = FindObjectOfType<EnvironmentSceneLoader>();
#endif
        }
    }

    private void Start()
    {
        if (!autoBootOnStart) return;

        if (_boot != null) StopCoroutine(_boot);
        _boot = StartCoroutine(BootRoutine());
    }

    public void BootNow()
    {
        if (_boot != null) StopCoroutine(_boot);
        _boot = StartCoroutine(BootRoutine());
    }

    public void ResetState()
    {
        if (_boot != null)
        {
            StopCoroutine(_boot);
            _boot = null;
        }

        if (_teleportWorker != null)
        {
            StopCoroutine(_teleportWorker);
            _teleportWorker = null;
        }

        _teleportQueue.Clear();
        _activeTeleportPlacementId = null;
        _lastEnqueuedPlacementId = null;
        _lastEnqueueFrame = -9999;
        ClearAnchorCache();
    }

    private IEnumerator BootRoutine()
    {
        if (ShouldLog) Debug.Log($"[ExperienceManager] BOOT start (timeScale={Time.timeScale:0.00})");

        ApplyConfigBasics();

        // deja respirar a Awake/Start de otros managers
        yield return null;

        // Fallback seguro para referencias opcionales no enlazadas por inspector.
        ResolveRuntimeReferencesIfNeeded();

        // Esperar a que termine el swap del entorno (y por tanto cierre el gate)
        yield return WaitEnvironmentReady();

        // Teleport startup
        PlacePlayerByContext(startupContext);

        // Si quieres, arrancar historia DESPUÉS del entorno listo
        if (autoStartStoryAfterBoot && storyManager != null)
        {
            if (ShouldLog) Debug.Log("[ExperienceManager] Calling storyManager.StartStory()");
            storyManager.StartStory();
        }

        if (ShouldLog) Debug.Log("[ExperienceManager] BOOT end");
        _boot = null;
    }

    private void ApplyConfigBasics()
    {
        if (config == null)
        {
            if (ShouldLog) Debug.Log("[ExperienceManager] No config assigned.");
            return;
        }

        // Language
        if (config.startLanguage != Enums.Language.None)
        {
            var lm = LanguageManager.GetInstance();
            if (lm != null)
            {
                var sysLang = (config.startLanguage == Enums.Language.English)
                    ? SystemLanguage.English
                    : SystemLanguage.Spanish;

                lm.SetLanguage(sysLang);
                if (ShouldLog) Debug.Log($"[ExperienceManager] Language set to {sysLang}");
            }
            else
            {
                if (ShouldLog) Debug.Log("[ExperienceManager] WARN: LanguageManager not found.");
            }
        }

        // Master volume
        AudioListener.volume = Mathf.Clamp01(config.masterVolume);
        if (ShouldLog) Debug.Log($"[ExperienceManager] AudioListener.volume={AudioListener.volume:0.00}");
    }

    private IEnumerator WaitEnvironmentReady()
    {
        if (environmentLoader == null)
        {
            if (ShouldLog) Debug.Log("[ExperienceManager] No EnvironmentSceneLoader found -> skip wait.");
            yield break;
        }

        float start = Time.realtimeSinceStartup;

        while (environmentLoader.IsBusy)
        {
            if (Time.realtimeSinceStartup - start > waitEnvironmentTimeout)
            {
                if (ShouldLog) Debug.Log($"[ExperienceManager] ERROR: Environment wait TIMEOUT >{waitEnvironmentTimeout:0.0}s (IsBusy still true).");
                yield break;
            }
            yield return null;
        }

        if (ShouldLog) Debug.Log($"[ExperienceManager] Environment ready. Current='{environmentLoader.CurrentEnvironmentSceneName ?? "null"}'");
    }

    public bool PlacePlayerByContext(Enums.PlacementContext context)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmPlaceByContext.Auto())
        {
            return PlacePlayerByContextInternal(context);
        }
#else
        return PlacePlayerByContextInternal(context);
#endif
    }

    private bool PlacePlayerByContextInternal(Enums.PlacementContext context)
    {
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("ExperienceManager.PlacePlayerByContext", "context=" + context);
        if (config == null)
        {
            if (ShouldLog) Debug.Log("[ExperienceManager] PlacePlayerByContext failed: config null");
            return false;
        }

        if (!config.TryGetPlacementID(context, out var placementID))
        {
            if (ShouldLog) Debug.Log($"[ExperienceManager] PlacePlayerByContext failed: no placement for context {context}");
            return false;
        }

        int preDelay = Mathf.Max(0, defaultPreTeleportDelayFrames);
        preDelay = ApplyStoryBacklogDefer(preDelay);
        return EnqueueTeleportRequest(placementID, $"Context:{context}", preDelay);
    }

    public bool TryTeleportPlayerToPlacementID(string placementID)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmTryTeleport.Auto())
        {
            return TryTeleportPlayerToPlacementIDInternal(placementID);
        }
#else
        return TryTeleportPlayerToPlacementIDInternal(placementID);
#endif
    }

    private bool TryTeleportPlayerToPlacementIDInternal(string placementID)
    {
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("ExperienceManager.TryTeleport", "placement=" + placementID);
        if (config != null && !config.ContainsPlacementID(placementID))
        {
            if (ShouldLog) Debug.Log($"[ExperienceManager] Teleport rejected (not in whitelist): '{placementID}'");
            return false;
        }

        // Si nos llaman dentro del dispatch del StoryEventBus, difiere más frames.
        int preDelay = Mathf.Max(0, defaultPreTeleportDelayFrames);
        if (StoryEventBus.IsDispatching)
            preDelay = Mathf.Max(preDelay, deferFramesWhenCalledFromDispatch);

        preDelay = ApplyStoryBacklogDefer(preDelay);
        return EnqueueTeleportRequest(placementID, "Direct", preDelay);
    }

    // PERF NOTE:
    // Antes se lanzaba una coroutine por solicitud de teleport. En transiciones con listeners/eventos
    // esto podía generar solapes de fade, waits de anchor y SetPositionAndRotation en frames conflictivos.
    // Ahora se serializan requests en una cola única y se coalescen duplicados equivalentes.
    private bool EnqueueTeleportRequest(string placementID, string reason, int preDelayFrames)
    {
        if (string.IsNullOrEmpty(placementID))
            return false;

        preDelayFrames = Mathf.Max(0, preDelayFrames);
        int nowFrame = Time.frameCount;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmQueueTeleport.Auto())
        {
            if (!TryCoalesceTeleportRequest(placementID, nowFrame))
            {
                if (keepOnlyLatestPendingTeleport && _teleportQueue.Count > 0)
                    _teleportQueue.Clear();

                _teleportQueue.Enqueue(new TeleportRequest
                {
                    placementID = placementID,
                    reason = reason,
                    preDelayFrames = preDelayFrames,
                    frameRequested = nowFrame
                });
                if (StoryTransitionTrace.Enabled)
                    StoryTransitionTrace.Mark("ExperienceManager.TeleportEnqueued", $"placement={placementID} reason={reason} preDelay={preDelayFrames} queue={_teleportQueue.Count}");

                _lastEnqueuedPlacementId = placementID;
                _lastEnqueueFrame = nowFrame;
            }
        }
#else
        if (!TryCoalesceTeleportRequest(placementID, nowFrame))
        {
            if (keepOnlyLatestPendingTeleport && _teleportQueue.Count > 0)
                _teleportQueue.Clear();

            _teleportQueue.Enqueue(new TeleportRequest
            {
                placementID = placementID,
                reason = reason,
                preDelayFrames = preDelayFrames,
                frameRequested = nowFrame
            });

            _lastEnqueuedPlacementId = placementID;
            _lastEnqueueFrame = nowFrame;
        }
#endif

        if (_teleportWorker == null)
            _teleportWorker = StartCoroutine(ProcessTeleportQueueRoutine());

        return true;
    }

    private bool TryCoalesceTeleportRequest(string placementID, int nowFrame)
    {
        if (!coalesceEquivalentTeleportRequests)
            return false;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmCoalesceTeleport.Auto())
        {
            return TryCoalesceTeleportRequestInternal(placementID, nowFrame);
        }
#else
        return TryCoalesceTeleportRequestInternal(placementID, nowFrame);
#endif
    }

    private bool TryCoalesceTeleportRequestInternal(string placementID, int nowFrame)
    {
        int window = Mathf.Max(0, teleportDuplicateWindowFrames);

        if (!string.IsNullOrEmpty(_activeTeleportPlacementId) &&
            string.Equals(_activeTeleportPlacementId, placementID, System.StringComparison.OrdinalIgnoreCase))
        {
            LogPerf($"Coalesced request '{placementID}' (already active).");
            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("ExperienceManager.TeleportCoalesced", $"placement={placementID} reason=Active");
            return true;
        }

        if (!string.IsNullOrEmpty(_lastEnqueuedPlacementId) &&
            string.Equals(_lastEnqueuedPlacementId, placementID, System.StringComparison.OrdinalIgnoreCase) &&
            (nowFrame - _lastEnqueueFrame) <= window)
        {
            LogPerf($"Coalesced request '{placementID}' (duplicate in window).");
            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("ExperienceManager.TeleportCoalesced", $"placement={placementID} reason=DuplicateWindow");
            return true;
        }

        if (_teleportQueue.Count > 0)
        {
            TeleportRequest[] pending = _teleportQueue.ToArray();
            for (int i = pending.Length - 1; i >= 0; i--)
            {
                if (!string.Equals(pending[i].placementID, placementID, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                if ((nowFrame - pending[i].frameRequested) > window)
                    break;

                LogPerf($"Coalesced request '{placementID}' (already pending).");
                if (StoryTransitionTrace.Enabled)
                    StoryTransitionTrace.Mark("ExperienceManager.TeleportCoalesced", $"placement={placementID} reason=AlreadyPending");
                return true;
            }
        }

        return false;
    }

    private IEnumerator ProcessTeleportQueueRoutine()
    {
        while (_teleportQueue.Count > 0)
        {
            if (respectNarrativePipelinePhases &&
                NarrativeTransitionPipeline.IsActive &&
                !NarrativeTransitionPipeline.IsPhaseReached(NarrativeTransitionPipeline.Phase.TeleportAndPlacement))
            {
                yield return null;
                continue;
            }

            TeleportRequest request;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmProcessTeleportQueue.Auto())
            {
                request = _teleportQueue.Dequeue();
            }
#else
            request = _teleportQueue.Dequeue();
#endif

            _activeTeleportPlacementId = request.placementID;
            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("ExperienceManager.TeleportDequeued", $"placement={request.placementID} queueAfterDequeue={_teleportQueue.Count}");
            yield return TeleportToPlacementRoutineInternal(request.placementID, request.reason, request.preDelayFrames);
            _activeTeleportPlacementId = null;
        }

        _teleportWorker = null;
    }

    private int ApplyStoryBacklogDefer(int currentPreDelay)
    {
        int preDelay = Mathf.Max(0, currentPreDelay);

        if (!deferWhenStoryEventBacklog)
            return preDelay;

        if (StoryEventBus.QueuedCount <= 0)
            return preDelay;

        return Mathf.Max(preDelay, deferFramesWhenStoryEventBacklog);
    }

    private void ClearAnchorCache()
    {
        _cachedAnchorPlacementId = null;
        _cachedAnchorTransform = null;
    }

    private bool TryGetAnchorCached(string placementID, out Transform anchor)
    {
        anchor = null;

        if (string.IsNullOrEmpty(placementID))
            return false;

        if (reuseLastResolvedAnchor &&
            !string.IsNullOrEmpty(_cachedAnchorPlacementId) &&
            string.Equals(_cachedAnchorPlacementId, placementID, System.StringComparison.OrdinalIgnoreCase) &&
            _cachedAnchorTransform != null)
        {
            anchor = _cachedAnchorTransform;
            return true;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmAnchorResolve.Auto())
        {
            if (!ObjectPlacementManager.TryGetAnyAnchor(placementID, out anchor) || anchor == null)
                return false;
        }
#else
        if (!ObjectPlacementManager.TryGetAnyAnchor(placementID, out anchor) || anchor == null)
            return false;
#endif

        if (reuseLastResolvedAnchor)
        {
            _cachedAnchorPlacementId = placementID;
            _cachedAnchorTransform = anchor;
        }

        return true;
    }

    private void LogPerf(string msg)
    {
        if (!ShouldLog)
            return;

        Debug.Log("[ExperienceManager][Perf] " + msg, this);
    }

    private IEnumerator TeleportToPlacementRoutineInternal(string placementID, string reason, int preDelayFrames)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmTeleportStart.Auto())
        {
            // Marcador ligero para separar coste de scheduling frente a WaitAnchor/SetRig.
        }
#endif
        if (ShouldLog) Debug.Log($"[ExperienceManager] Teleport START id='{placementID}' reason='{reason}' preDelayFrames={preDelayFrames}");
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("ExperienceManager.TeleportRoutine.Begin", $"placement={placementID} reason={reason} preDelay={preDelayFrames}");

        // Espera N frames para separar de dispatch / Canvas / registros.
        int frames = Mathf.Max(0, preDelayFrames);
        while (frames-- > 0)
            yield return null;

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("ExperienceManager.TeleportRoutine.AfterPreDelay", "placement=" + placementID);

        // Espera a que exista el anchor (escenas aditivas pueden tardar 1-2 frames)
        Transform anchor = null;
        float start = Time.realtimeSinceStartup;

        while (anchor == null)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmAnchorWait.Auto())
            {
                if (TryGetAnchorCached(placementID, out anchor) && anchor != null)
                    break;
            }
#else
            if (TryGetAnchorCached(placementID, out anchor) && anchor != null)
                break;
#endif

            if (Time.realtimeSinceStartup - start > waitAnchorTimeout)
            {
                if (ShouldLog) Debug.Log($"[ExperienceManager] ERROR: Anchor TIMEOUT id='{placementID}' >{waitAnchorTimeout:0.0}s");
                if (StoryTransitionTrace.Enabled)
                    StoryTransitionTrace.Mark("ExperienceManager.TeleportRoutine.AnchorTimeout", "placement=" + placementID);
                yield break;
            }

            yield return null;
        }

        if (ShouldLog) Debug.Log($"[ExperienceManager] Anchor found '{placementID}' pos={anchor.position}");
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("ExperienceManager.TeleportRoutine.AnchorReady", "placement=" + placementID);

        // mueve rig (o locomotor root si existe)
        var root = locomotorRoot != null ? locomotorRoot : xrRig;
        if (root == null)
        {
            if (ShouldLog) Debug.Log("[ExperienceManager] ERROR: No xrRig/locomotorRoot assigned.");
            yield break;
        }

        // COSTE EVITADO:
        // Si el player ya está en el target, un teleport redundante sólo añade fade + trabajo de transform.
        // Lo saltamos para evitar spikes repetidos en transiciones con eventos duplicados.
        if (skipRedundantTeleportWhenAlreadyAtTarget)
        {
            float sqrDist = (root.position - anchor.position).sqrMagnitude;
            float distEps = Mathf.Max(0f, redundantTeleportPositionEpsilon);
            float angle = Quaternion.Angle(root.rotation, anchor.rotation);
            float angleEps = Mathf.Max(0f, redundantTeleportAngleEpsilon);

            if (sqrDist <= (distEps * distEps) && angle <= angleEps)
            {
                LogPerf($"Skipped redundant teleport '{placementID}' (already at target).");
                if (StoryTransitionTrace.Enabled)
                    StoryTransitionTrace.Mark("ExperienceManager.TeleportRoutine.SkippedRedundant", "placement=" + placementID);
                yield break;
            }
        }

        if (useFade && fadeManager != null)
        {
            yield return fadeManager.FadeOut();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmSetRig.Auto())
        {
            root.SetPositionAndRotation(anchor.position, anchor.rotation);
        }
#else
        root.SetPositionAndRotation(anchor.position, anchor.rotation);
#endif

        if (useFade && fadeManager != null)
        {
            yield return fadeManager.FadeIn();
        }

        if (ShouldLog) Debug.Log($"[ExperienceManager] Teleport END id='{placementID}'");
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("ExperienceManager.TeleportRoutine.End", "placement=" + placementID);
    }
}
