using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

namespace PlayGo.SceneObjects
{
    [DefaultExecutionOrder(-9000)]
    [DisallowMultipleComponent]
    public sealed class SceneObjectService : MonoBehaviour
    {
        public static SceneObjectService Instance { get; private set; }

        [Header("Scan")]
        [Tooltip("Si lo dejas vacío, escanea toda la escena. Si rellenas, solo escanea dentro de esos roots.")]
        [SerializeField] private List<Transform> scanRoots = new();

        [Tooltip("Re-escanea en Start por si algo se instancia en Awake.")]
        [SerializeField] private bool rescanOnStart = true;

        [Header("Auto Refresh (robustez)")]
        [Tooltip("Si se cargan/descargan escenas aditivas, marca el lookup como sucio para reconstruirlo.")]
        [SerializeField] private bool rebuildOnSceneChanges = true;

        [Tooltip("Agrupa múltiples invalidaciones de rebuild durante la misma transición y ejecuta una sola reconstrucción.")]
        [SerializeField] private bool coalesceRebuildRequests = true;

        [Tooltip("Si está activo, al completar un rebuild también aplica defaults una única vez para ese lookup.")]
        [SerializeField] private bool autoApplyDefaultsOnRebuild = false;

        [Tooltip("Logs de rendimiento/diagnóstico del scheduler de rebuild y de la cola diferida.")]
        [SerializeField] private bool verbosePerformanceLogs = false;

        [Header("Incremental Rebuild Pipeline")]
        [Tooltip("Si está activo, los rebuilds no forzados se ejecutan por fases y con presupuesto por frame.")]
        [SerializeField] private bool useIncrementalRebuildPipeline = true;

        [Tooltip("Presupuesto (ms) por frame para descubrimiento/indexado durante rebuild incremental.")]
        [SerializeField, Min(0.1f)] private float rebuildBudgetMsPerFrame = 1.5f;

        [Tooltip("Lote de SceneObjectId por tick durante indexado incremental.")]
        [SerializeField, Min(8)] private int rebuildIndexBatchSize = 64;

        [Tooltip("Lote de nodos para construir tabla de descendientes en rebuild incremental.")]
        [SerializeField, Min(8)] private int rebuildDescendantsBatchSize = 64;

        [Tooltip("Si está activo, ApplyDefaults se trocea por frame cuando no es forzado.")]
        [SerializeField] private bool useIncrementalApplyDefaults = true;

        [Tooltip("Presupuesto (ms) por frame para ApplyDefaults incremental.")]
        [SerializeField, Min(0.1f)] private float applyDefaultsBudgetMsPerFrame = 1.0f;

        [Tooltip("Lote de SceneObjectId por tick para ApplyDefaults incremental.")]
        [SerializeField, Min(8)] private int applyDefaultsBatchSize = 64;

        [Tooltip("Si un id no se encuentra, intenta reconstruir el lookup 1 vez y reintentar (útil cuando aparecen objetos por Addressables o instanciación tardía).")]
        [SerializeField] private bool rebuildOnMissingId = true;

        [Tooltip("Log breve cuando se reconstruye el lookup (cuántos ids ha registrado).")]
        [SerializeField] private bool logLookupBuildSummary = false;

        [Tooltip("Si el escaneo normal no encuentra SceneObjectId, intenta un fallback con Resources.FindObjectsOfTypeAll filtrado por escenas cargadas.")]
        [SerializeField] private bool useResourcesFallbackWhenEmpty = true;

        [Header("Parent/Child Independence")]
        [Tooltip("Si activas un objeto que tiene descendientes registrados, re-aplica estado deseado de esos descendientes para que sean independientes.")]
        [SerializeField] private bool preserveRegisteredChildrenStates = true;

        [Header("State Enforcement (FIX)")]
        [Tooltip("Impone en LateUpdate el estado deseado de los objetos 'enforced'. Evita que otros scripts los activen por su cuenta.")]
        [SerializeField] private bool enforceDesiredStates = false;

        [Tooltip("Intervalo entre pasadas de enforce en segundos. 0 = cada frame. Subirlo reduce coste CPU.")]
        [SerializeField, Min(0f)] private float enforceIntervalSeconds = 0.05f;

        [Tooltip("Máximo de IDs enforced que se validan por pasada. 0 = validar todos.")]
        [SerializeField, Min(0)] private int maxEnforcedChecksPerTick = 16;

        [Tooltip("Solo se imponen los que tengan SceneObjectId.applyDefaultOnStart = true.")]
        [SerializeField] private bool enforceOnlyApplyDefaultOnStart = true;

        [Tooltip("Si está activo, loguea cuando detecta cambios externos y los corrige (útil para cazar al culpable).")]
        [SerializeField] private bool logExternalChanges = false;

        [Header("Performance / Spike Control")]
        [Tooltip("Si está activo, durante el dispatch de StoryEventBus se difiere SetActive para trocearlo en varios frames.")]
        [SerializeField] private bool deferActivationDuringStoryDispatch = true;

        [Tooltip("Si está activo, TODOS los SetActive se encolan y se aplican en Update/LateUpdate (last-write-wins por id). Reduce parpadeos por órdenes rápidas/contradictorias.")]
        [SerializeField] private bool deferAllSetActiveRequests = false;

        [Tooltip("Máximo de SetActive reales por frame (cuando hay cola).")]
        [SerializeField, Min(1)] private int maxSetActivesPerFrame = 20;

        [Tooltip("Presupuesto de tiempo (ms) por frame para aplicar SetActive reales.")]
        [SerializeField, Min(0f)] private float maxSetActiveMsPerFrame = 1.5f;

        [Tooltip("Límite adicional de activaciones reales por frame en la cola diferida. 0 = sin límite extra.")]
        [SerializeField, Min(0)] private int maxOperationsPerFrame = 0;

        [Tooltip("Si está activo, aplica la cola en LateUpdate (recomendado para no mezclar con Update de Oculus Interaction).")]
        [SerializeField] private bool processDeferredInLateUpdate = true;

        [Header("Narrative Pipeline")]
        [Tooltip("Si hay transición narrativa activa, retrasa rebuilds/SetActive diferidos hasta fase E.")]
        [SerializeField] private bool respectNarrativePipelinePhases = true;

        [Header("Missing ID Recovery")]
        [Tooltip("Cuando SetActive no encuentra un id, reintenta durante varios frames para cubrir cargas tardías.")]
        [SerializeField] private bool retryMissingSetActive = false;

        [Tooltip("Cantidad máxima de frames de reintento por id faltante.")]
        [SerializeField, Min(1)] private int missingSetActiveRetryFrames = 30;

        [Header("Diagnostics")]
        [Tooltip("Logs opcionales de picos en métodos calientes del servicio (apagado por defecto).")]
        [SerializeField] private bool logProfilingSpikes = false;

        [Tooltip("Umbral en ms para loggear un pico cuando logProfilingSpikes está activo.")]
        [SerializeField, Min(0f)] private float profilingSpikeLogMs = 2.5f;

        private Dictionary<string, GameObject> _map = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _canonicalToId = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SceneObjectId> _idComps = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _desiredActive = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<string>> _groups = new(StringComparer.OrdinalIgnoreCase);
        private List<SceneObjectId> _all = new(512);

        // Descendientes registrados por id (para re-aplicar estados sin recorrer _all entero).
        private Dictionary<string, List<string>> _descendantsById = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<Transform, string> _transformToId = new();

        private struct PendingSetActive
        {
            public string id;
        }

        private readonly Queue<PendingSetActive> _pending = new(256);
        private readonly HashSet<string> _pendingIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _missingDesired = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missingRetryRunning = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missingWarned = new(StringComparer.OrdinalIgnoreCase);

        // IDs que se “imponen” siempre
        private List<string> _enforcedIds = new(256);

        private bool _lookupDirty;
        private bool _pendingApplyDefaultsAfterRebuild;
        private int _pendingRebuildRequests;
        private int _lastLookupBuildFrame = -1;
        private int _lastMissingIdRebuildFrame = -1;
        private int _lookupVersion = 0;
        private int _lastAppliedDefaultsLookupVersion = -1;
        private int _lastEmptyScanWarningFrame = -1;
        private int _lastDeferredOpsFrame = -1;
        private int _deferredOpsThisFrame = 0;
        private float _nextEnforceTime;
        private int _enforceCursor;
        private Coroutine _rebuildRoutine;
        private bool _rebuildInProgress;
        private Coroutine _applyDefaultsRoutine;
        private bool _pendingIncrementalApplyDefaults;
        private readonly List<SceneObjectId> _scanBuffer = new(512);
        private readonly List<SceneObjectId> _rootScanBuffer = new(256);
        private readonly List<GameObject> _rootObjectsBuffer = new(128);
        private readonly List<string> _missingResolveBuffer = new(64);
        private static readonly Regex UnityCloneSuffixRegex = new Regex(@"\s\(\d+\)$", RegexOptions.Compiled);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // PERF NOTE:
        // Estos métodos son candidatos de spike por scene traversal, reconstrucción de índices y activaciones en lote.
        private static readonly ProfilerMarker _pmBuildLookup = new ProfilerMarker("SceneObjectService.BuildLookup");
        private static readonly ProfilerMarker _pmFindAllSceneObjectIds = new ProfilerMarker("SceneObjectService.FindAllSceneObjectIds");
        private static readonly ProfilerMarker _pmBuildDescendantTable = new ProfilerMarker("SceneObjectService.BuildDescendantTable");
        private static readonly ProfilerMarker _pmApplyDefaults = new ProfilerMarker("SceneObjectService.ApplyDefaults");
        private static readonly ProfilerMarker _pmApplyDefaultsIncremental = new ProfilerMarker("SceneObjectService.ApplyDefaultsIncremental");
        private static readonly ProfilerMarker _pmIncrementalScanChunk = new ProfilerMarker("SceneObjectService.BuildLookupIncremental.ScanChunk");
        private static readonly ProfilerMarker _pmIncrementalIndexChunk = new ProfilerMarker("SceneObjectService.BuildLookupIncremental.IndexChunk");
        private static readonly ProfilerMarker _pmIncrementalDescendantsChunk = new ProfilerMarker("SceneObjectService.BuildLookupIncremental.DescendantsChunk");
        private static readonly ProfilerMarker _pmProcessDeferredSetActive = new ProfilerMarker("SceneObjectService.ProcessDeferredSetActive");
        private static readonly ProfilerMarker _pmReapplyDescendants = new ProfilerMarker("SceneObjectService.ReapplyDescendants");
#endif

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void LogSpikeIfNeeded(string scope, float startedAtRealtime)
        {
            if (!logProfilingSpikes) return;
            float elapsedMs = (Time.realtimeSinceStartup - startedAtRealtime) * 1000f;
            if (elapsedMs < profilingSpikeLogMs) return;
            Debug.Log($"[SceneObjectService][Spike] {scope} took {elapsedMs:0.00} ms");
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void LogPerfVerbose(string message)
        {
            if (!verbosePerformanceLogs) return;
            Debug.Log("[SceneObjectService][Perf] " + message);
        }

        private bool CanProcessAnotherDeferredSetActiveThisFrame()
        {
            if (maxOperationsPerFrame <= 0)
                return true;

            int frame = Time.frameCount;
            if (_lastDeferredOpsFrame != frame)
            {
                _lastDeferredOpsFrame = frame;
                _deferredOpsThisFrame = 0;
            }

            if (_deferredOpsThisFrame >= maxOperationsPerFrame)
                return false;

            _deferredOpsThisFrame++;
            return true;
        }

        private sealed class RebuildSnapshot
        {
            public readonly Dictionary<string, GameObject> map = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> canonicalToId = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, SceneObjectId> idComps = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, bool> desiredActive = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<string>> groups = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<SceneObjectId> all = new(512);
            public readonly Dictionary<string, List<string>> descendantsById = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<Transform, string> transformToId = new();
            public readonly List<string> enforcedIds = new(256);
        }

        // Evita reconstrucciones duplicadas en una misma transición (sceneLoaded + llamadas manuales).
        private void RequestLookupRebuild(bool applyDefaultsAfterRebuild, bool immediate, string reason)
        {
            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("SceneObjectService.RequestLookupRebuild", $"reason={reason} immediate={immediate} applyDefaults={applyDefaultsAfterRebuild}");
            _lookupDirty = true;
            _pendingRebuildRequests++;
            _pendingApplyDefaultsAfterRebuild |= applyDefaultsAfterRebuild;

            if (verbosePerformanceLogs)
                LogPerfVerbose($"Rebuild requested reason={reason} immediate={immediate} pending={_pendingRebuildRequests} applyDefaults={_pendingApplyDefaultsAfterRebuild}");

            if (!Application.isPlaying || immediate)
            {
                ProcessPendingLookupRebuild(force: true, reason: reason);
            }
        }

        private void ProcessPendingLookupRebuild(bool force, string reason)
        {
            if (!_lookupDirty) return;
            if (!force && coalesceRebuildRequests && Time.frameCount == _lastLookupBuildFrame) return;
            if (!force &&
                respectNarrativePipelinePhases &&
                NarrativeTransitionPipeline.IsActive &&
                !NarrativeTransitionPipeline.IsPhaseReached(NarrativeTransitionPipeline.Phase.HeavyObjectChanges))
            {
                return;
            }

            if (!force && Application.isPlaying && useIncrementalRebuildPipeline)
            {
                if (_rebuildInProgress)
                    return;

                _rebuildRoutine = StartCoroutine(BuildLookupIncrementalRoutine(reason));
                return;
            }

            if (_rebuildRoutine != null)
            {
                StopCoroutine(_rebuildRoutine);
                _rebuildRoutine = null;
                _rebuildInProgress = false;
            }

            bool rebuilt = BuildLookupInternal(preserveDesiredStates: true);
            if (!rebuilt)
                return;

            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("SceneObjectService.ProcessLookupRebuild", $"reason={reason} pendingRequests={_pendingRebuildRequests}");

            _lookupDirty = false;
            _pendingRebuildRequests = 0;

            if (autoApplyDefaultsOnRebuild || _pendingApplyDefaultsAfterRebuild)
                ApplyDefaultsIfNeeded(force: false);

            _pendingApplyDefaultsAfterRebuild = false;

            if (verbosePerformanceLogs)
                LogPerfVerbose($"Rebuild processed reason={reason} lookupVersion={_lookupVersion}");
        }

        private void ApplyDefaultsIfNeeded(bool force)
        {
            if (!force && _lastAppliedDefaultsLookupVersion == _lookupVersion)
                return;

            if (force && _applyDefaultsRoutine != null)
            {
                StopCoroutine(_applyDefaultsRoutine);
                _applyDefaultsRoutine = null;
                _pendingIncrementalApplyDefaults = false;
            }

            if (!force && Application.isPlaying && useIncrementalApplyDefaults)
            {
                _pendingIncrementalApplyDefaults = true;
                if (_applyDefaultsRoutine == null)
                    _applyDefaultsRoutine = StartCoroutine(ApplyDefaultsIncrementalRoutine());
                return;
            }

            ApplyDefaults();
            _lastAppliedDefaultsLookupVersion = _lookupVersion;
        }

        private IEnumerator ApplyDefaultsIncrementalRoutine()
        {
            while (_pendingIncrementalApplyDefaults)
            {
                _pendingIncrementalApplyDefaults = false;

                while (!CanRunHeavyPipelineWork() || _rebuildInProgress)
                    yield return null;

                int changedCount = 0;
                int i = 0;
                int count = _all.Count;

                while (i < count)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    using (_pmApplyDefaultsIncremental.Auto())
#endif
                    {
                        float tickStart = Time.realtimeSinceStartup;
                        int batch = 0;

                        while (i < count)
                        {
                            var so = _all[i++];
                            if (so == null) continue;

                            var id = (so.Id ?? string.Empty).Trim();
                            if (string.IsNullOrWhiteSpace(id)) continue;
                            if (!so.applyDefaultOnStart) continue;

                            _desiredActive[id] = so.defaultActive;

                            var go = so.gameObject;
                            if (go != null && go.activeSelf != so.defaultActive)
                            {
                                go.SetActive(so.defaultActive);
                                changedCount++;
                            }

                            if (++batch >= applyDefaultsBatchSize || IsApplyDefaultsBudgetExceeded(tickStart))
                                break;
                        }
                    }

                    if (i < count)
                        yield return null;
                }

                _lastAppliedDefaultsLookupVersion = _lookupVersion;

                if (StoryTransitionTrace.Enabled)
                    StoryTransitionTrace.Mark("SceneObjectService.ApplyDefaults", $"changed={changedCount} total={_all.Count} incremental=true");
            }

            _applyDefaultsRoutine = null;
        }

        private bool IsApplyDefaultsBudgetExceeded(float frameStartRealtime)
        {
            float budgetMs = Mathf.Max(0.1f, applyDefaultsBudgetMsPerFrame);
            float elapsedMs = (Time.realtimeSinceStartup - frameStartRealtime) * 1000f;
            return elapsedMs >= budgetMs;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            if (rebuildOnSceneChanges)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                SceneManager.activeSceneChanged += OnActiveSceneChanged;
            }

            RequestLookupRebuild(applyDefaultsAfterRebuild: false, immediate: true, reason: "Awake");
        }

        private void Start()
        {
            if (rescanOnStart)
                RequestLookupRebuild(applyDefaultsAfterRebuild: false, immediate: true, reason: "Start.Rescan");

            ApplyDefaultsIfNeeded(force: false);
        }

        private void OnDestroy()
        {
            if (_rebuildRoutine != null)
            {
                StopCoroutine(_rebuildRoutine);
                _rebuildRoutine = null;
                _rebuildInProgress = false;
            }

            if (_applyDefaultsRoutine != null)
            {
                StopCoroutine(_applyDefaultsRoutine);
                _applyDefaultsRoutine = null;
                _pendingIncrementalApplyDefaults = false;
            }

            if (Instance == this)
            {
                if (rebuildOnSceneChanges)
                {
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                    SceneManager.sceneUnloaded -= OnSceneUnloaded;
                    SceneManager.activeSceneChanged -= OnActiveSceneChanged;
                }

                Instance = null;
            }
        }

        private void LateUpdate()
        {
            // Si hay cambios de escenas, reconstruimos en un punto estable del frame.
            ProcessPendingLookupRebuild(force: false, reason: "LateUpdate");

            if (_map.Count == 0) return;

            if (processDeferredInLateUpdate)
                ProcessDeferredActivationBudgeted();

            if (!enforceDesiredStates) return;
            if (_rebuildInProgress) return;

            if (enforceIntervalSeconds > 0f)
            {
                float now = Time.unscaledTime;
                if (now < _nextEnforceTime)
                    return;

                _nextEnforceTime = now + enforceIntervalSeconds;
            }

            // Impone estados de forma continua (throttled para evitar coste por frame)
            EnforceDesiredStates();
        }

        private void Update()
        {
            if (!processDeferredInLateUpdate)
                ProcessPendingLookupRebuild(force: false, reason: "Update");

            if (_map.Count == 0) return;

            if (!processDeferredInLateUpdate)
                ProcessDeferredActivationBudgeted();
        }

        public void ApplyDefaultsNow()
        {
            ApplyDefaultsIfNeeded(force: true);
        }

        [ContextMenu("Rebuild Lookup Now")]
        public void BuildLookup()
        {
            RequestLookupRebuild(applyDefaultsAfterRebuild: false, immediate: true, reason: "ManualBuildLookup");
        }

        // API de compatibilidad para callers que actualmente hacen BuildLookup() + ApplyDefaultsNow().
        public void RequestRebuild(bool applyDefaultsAfterRebuild = false, bool immediate = false)
        {
            RequestLookupRebuild(applyDefaultsAfterRebuild, immediate, "ExternalRequest");
        }

        private bool BuildLookupInternal(bool preserveDesiredStates)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Spike candidate: reconstrucción completa (diccionarios + scan + tabla de descendientes).
            // Coste esperado: CPU + scene traversal.
            float spikeStart = Time.realtimeSinceStartup;
            using var _scope = _pmBuildLookup.Auto();
#endif
            _lastLookupBuildFrame = Time.frameCount;

            Dictionary<string, bool> prevDesired = null;
            if (preserveDesiredStates && _desiredActive.Count > 0)
                prevDesired = new Dictionary<string, bool>(_desiredActive, StringComparer.OrdinalIgnoreCase);

            var ids = FindAllSceneObjectIds();
            if (Application.isPlaying && ids.Count == 0 && _map.Count > 0)
            {
                // Evita perder el registro por escaneos transitorios vacios (cargas aditivas / race de frame).
                if (StoryTransitionTrace.Enabled)
                    StoryTransitionTrace.Mark("SceneObjectService.BuildLookup.Skipped", "reason=TransientEmptyScan");
                return false;
            }

            _map.Clear();
            _canonicalToId.Clear();
            _idComps.Clear();
            _groups.Clear();
            _all.Clear();
            _enforcedIds.Clear();
            _descendantsById.Clear();
            _transformToId.Clear();
            _desiredActive.Clear();
            _enforceCursor = 0;

            for (int i = 0; i < ids.Count; i++)
            {
                var so = ids[i];
                if (so == null) continue;

                var id = (so.Id ?? "").Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    Debug.LogWarning($"[SceneObjectService] SceneObjectId sin id en '{GetPath(so.transform)}'.", so);
                    continue;
                }

                if (_map.ContainsKey(id))
                {
                    Debug.LogWarning($"[SceneObjectService] ID duplicado '{id}'. Mantengo el primero, ignoro '{GetPath(so.transform)}'.", so);
                    continue;
                }

                _all.Add(so);
                _map.Add(id, so.gameObject);
                AddCanonicalAlias(id);
                _idComps.Add(id, so);

                if (so.transform != null)
                    _transformToId[so.transform] = id;

                // Estado deseado: si ya existía, lo preservamos.
                bool want = so.gameObject.activeSelf;
                if (prevDesired != null && prevDesired.TryGetValue(id, out var prevWant))
                    want = prevWant;
                _desiredActive[id] = want;

                // Enforced list (por defecto, solo los que optan con applyDefaultOnStart)
                if (!enforceOnlyApplyDefaultOnStart || so.applyDefaultOnStart)
                    _enforcedIds.Add(id);

                var g = (so.Group ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(g))
                {
                    if (!_groups.TryGetValue(g, out var list))
                    {
                        list = new List<string>(16);
                        _groups.Add(g, list);
                    }
                    list.Add(id);
                }
            }

            // Construir tabla de descendientes registrados por id (O(N*depth)).
            BuildDescendantTable();

            if (logLookupBuildSummary)
                Debug.Log($"[SceneObjectService] Lookup rebuilt. Registered IDs: {_map.Count}");

            // Marca versión para evitar ApplyDefaults redundante sobre el mismo lookup.
            _lookupVersion++;
            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("SceneObjectService.BuildLookup.End", $"registered={_map.Count} all={_all.Count} lookupVersion={_lookupVersion}");
            ReconcileMissingDesiredAfterRebuild();
            // Si reconstruimos de forma manual/inmediata limpiamos invalidación pendiente para evitar doble rebuild.
            _lookupDirty = false;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            LogSpikeIfNeeded("BuildLookupInternal", spikeStart);
#endif
            return true;
        }

        // COSTE ORIGINAL:
        // BuildLookup monolítico hacía scan + index + descendientes en el mismo frame de transición.
        // CAMBIO:
        // Esta rutina divide el trabajo en fases con presupuesto por frame y commit atómico al final.
        private IEnumerator BuildLookupIncrementalRoutine(string reason)
        {
            _rebuildInProgress = true;

            while (_lookupDirty || _pendingRebuildRequests > 0)
            {
                while (!CanRunHeavyPipelineWork())
                    yield return null;

                bool shouldReapplyDefaults = false;
                if (_applyDefaultsRoutine != null)
                {
                    StopCoroutine(_applyDefaultsRoutine);
                    _applyDefaultsRoutine = null;
                    _pendingIncrementalApplyDefaults = false;
                    shouldReapplyDefaults = true;
                }

                bool applyDefaultsAfterRebuild = autoApplyDefaultsOnRebuild || _pendingApplyDefaultsAfterRebuild || shouldReapplyDefaults;
                _lookupDirty = false;
                _pendingRebuildRequests = 0;
                _pendingApplyDefaultsAfterRebuild = false;

                Dictionary<string, bool> prevDesired = null;
                if (_desiredActive.Count > 0)
                    prevDesired = new Dictionary<string, bool>(_desiredActive, StringComparer.OrdinalIgnoreCase);

                _scanBuffer.Clear();
                yield return ScanSceneObjectIdsIncremental(_scanBuffer);

                if (Application.isPlaying && _scanBuffer.Count == 0 && _map.Count > 0)
                {
                    if (verbosePerformanceLogs)
                        LogPerfVerbose("Incremental rebuild skipped by transient empty scan.");

                    if (!_lookupDirty && _pendingRebuildRequests <= 0)
                        break;

                    continue;
                }

                var snapshot = new RebuildSnapshot();
                yield return BuildSnapshotIncremental(_scanBuffer, prevDesired, snapshot);
                yield return BuildDescendantTableIncremental(snapshot);

                CommitSnapshot(snapshot);
                _lookupVersion++;
                _lastLookupBuildFrame = Time.frameCount;
                ReconcileMissingDesiredAfterRebuild();

                if (logLookupBuildSummary)
                    Debug.Log($"[SceneObjectService] Incremental lookup rebuilt. Registered IDs: {_map.Count}");

                if (applyDefaultsAfterRebuild)
                    ApplyDefaultsIfNeeded(force: false);
            }

            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("SceneObjectService.ProcessLookupRebuild", $"reason={reason} incremental=true lookupVersion={_lookupVersion}");

            _rebuildInProgress = false;
            _rebuildRoutine = null;
        }

        private IEnumerator ScanSceneObjectIdsIncremental(List<SceneObjectId> result)
        {
            if (result == null)
                yield break;

            float frameStart = Time.realtimeSinceStartup;
            int batch = 0;

            if (scanRoots != null && scanRoots.Count > 0)
            {
                bool anyValidRoot = false;
                for (int r = 0; r < scanRoots.Count; r++)
                {
                    var root = scanRoots[r];
                    if (root == null) continue;

                    anyValidRoot = true;
                    _rootScanBuffer.Clear();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    using (_pmIncrementalScanChunk.Auto())
#endif
                    root.GetComponentsInChildren(true, _rootScanBuffer);
                    for (int i = 0; i < _rootScanBuffer.Count; i++)
                        result.Add(_rootScanBuffer[i]);

                    if (++batch >= rebuildIndexBatchSize || IsRebuildBudgetExceeded(frameStart))
                    {
                        batch = 0;
                        frameStart = Time.realtimeSinceStartup;
                        yield return null;
                    }
                }

                if (anyValidRoot && result.Count > 0)
                    yield break;
            }

            int sceneCount = SceneManager.sceneCount;
            for (int si = 0; si < sceneCount; si++)
            {
                var sc = SceneManager.GetSceneAt(si);
                if (!sc.IsValid() || !sc.isLoaded) continue;

                _rootObjectsBuffer.Clear();
#if UNITY_2021_2_OR_NEWER
                sc.GetRootGameObjects(_rootObjectsBuffer);
#else
                _rootObjectsBuffer.AddRange(sc.GetRootGameObjects());
#endif
                for (int ri = 0; ri < _rootObjectsBuffer.Count; ri++)
                {
                    var root = _rootObjectsBuffer[ri];
                    if (root == null) continue;

                    _rootScanBuffer.Clear();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    using (_pmIncrementalScanChunk.Auto())
#endif
                    root.GetComponentsInChildren(true, _rootScanBuffer);
                    for (int i = 0; i < _rootScanBuffer.Count; i++)
                        result.Add(_rootScanBuffer[i]);

                    if (++batch >= rebuildIndexBatchSize || IsRebuildBudgetExceeded(frameStart))
                    {
                        batch = 0;
                        frameStart = Time.realtimeSinceStartup;
                        yield return null;
                    }
                }
            }

            if (result.Count == 0 && useResourcesFallbackWhenEmpty)
                TryAppendFromResourcesFindAll(result);
        }

        private IEnumerator BuildSnapshotIncremental(
            List<SceneObjectId> ids,
            Dictionary<string, bool> prevDesired,
            RebuildSnapshot snapshot)
        {
            if (ids == null || snapshot == null)
                yield break;

            float frameStart = Time.realtimeSinceStartup;
            int batch = 0;

            for (int i = 0; i < ids.Count; i++)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                using (_pmIncrementalIndexChunk.Auto())
#endif
                {
                var so = ids[i];
                if (so == null) continue;

                var id = (so.Id ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (snapshot.map.ContainsKey(id))
                    continue;

                snapshot.all.Add(so);
                snapshot.map.Add(id, so.gameObject);
                AddCanonicalAliasTo(snapshot.canonicalToId, id);
                snapshot.idComps.Add(id, so);

                if (so.transform != null)
                    snapshot.transformToId[so.transform] = id;

                bool want = so.gameObject != null && so.gameObject.activeSelf;
                if (prevDesired != null && prevDesired.TryGetValue(id, out var prevWant))
                    want = prevWant;
                snapshot.desiredActive[id] = want;

                if (!enforceOnlyApplyDefaultOnStart || so.applyDefaultOnStart)
                    snapshot.enforcedIds.Add(id);

                var g = (so.Group ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(g))
                {
                    if (!snapshot.groups.TryGetValue(g, out var list))
                    {
                        list = new List<string>(16);
                        snapshot.groups.Add(g, list);
                    }
                    list.Add(id);
                }
                }

                if (++batch >= rebuildIndexBatchSize || IsRebuildBudgetExceeded(frameStart))
                {
                    batch = 0;
                    frameStart = Time.realtimeSinceStartup;
                    yield return null;
                }
            }
        }

        private IEnumerator BuildDescendantTableIncremental(RebuildSnapshot snapshot)
        {
            if (snapshot == null)
                yield break;

            float frameStart = Time.realtimeSinceStartup;
            int batch = 0;

            for (int i = 0; i < snapshot.all.Count; i++)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                using (_pmIncrementalDescendantsChunk.Auto())
#endif
                {
                var so = snapshot.all[i];
                if (so == null) continue;

                var childId = (so.Id ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(childId)) continue;

                var t = so.transform;
                if (t == null) continue;

                var p = t.parent;
                while (p != null)
                {
                    if (snapshot.transformToId.TryGetValue(p, out var parentId) && !string.IsNullOrWhiteSpace(parentId))
                    {
                        if (!snapshot.descendantsById.TryGetValue(parentId, out var list))
                        {
                            list = new List<string>(8);
                            snapshot.descendantsById.Add(parentId, list);
                        }

                        list.Add(childId);
                    }

                    p = p.parent;
                }
                }

                if (++batch >= rebuildDescendantsBatchSize || IsRebuildBudgetExceeded(frameStart))
                {
                    batch = 0;
                    frameStart = Time.realtimeSinceStartup;
                    yield return null;
                }
            }
        }

        private void CommitSnapshot(RebuildSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            _map = snapshot.map;
            _canonicalToId = snapshot.canonicalToId;
            _idComps = snapshot.idComps;
            _desiredActive = snapshot.desiredActive;
            _groups = snapshot.groups;
            _all = snapshot.all;
            _descendantsById = snapshot.descendantsById;
            _transformToId = snapshot.transformToId;
            _enforcedIds = snapshot.enforcedIds;
            _enforceCursor = 0;
        }

        private bool IsRebuildBudgetExceeded(float frameStartRealtime)
        {
            float budgetMs = Mathf.Max(0.1f, rebuildBudgetMsPerFrame);
            float elapsedMs = (Time.realtimeSinceStartup - frameStartRealtime) * 1000f;
            return elapsedMs >= budgetMs;
        }

        private static void AddCanonicalAliasTo(Dictionary<string, string> canonicalToId, string id)
        {
            if (canonicalToId == null || string.IsNullOrWhiteSpace(id))
                return;

            string canonical = CanonicalizeId(id);
            if (string.IsNullOrWhiteSpace(canonical))
                return;

            if (canonicalToId.ContainsKey(canonical))
                return;

            canonicalToId.Add(canonical, id);
        }

        private void ReconcileMissingDesiredAfterRebuild()
        {
            if (_missingDesired.Count == 0)
                return;

            _missingResolveBuffer.Clear();
            foreach (var kv in _missingDesired)
                _missingResolveBuffer.Add(kv.Key);

            for (int i = 0; i < _missingResolveBuffer.Count; i++)
            {
                string requestedId = _missingResolveBuffer[i];
                if (!_missingDesired.TryGetValue(requestedId, out var want))
                    continue;

                if (!TryGetDirectOrCanonical(NormalizeIdInput(requestedId), out var resolvedId, out var go))
                    continue;

                _desiredActive[resolvedId] = want;
                if (ShouldDeferNow())
                    EnqueueSetActive(resolvedId);
                else if (go != null && go.activeSelf != want)
                    go.SetActive(want);

                _missingDesired.Remove(requestedId);
                _missingWarned.Remove(requestedId);
            }
        }

        private void BuildDescendantTable()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var _scope = _pmBuildDescendantTable.Auto();
#endif
            _descendantsById.Clear();

            for (int i = 0; i < _all.Count; i++)
            {
                var so = _all[i];
                if (so == null) continue;

                var childId = (so.Id ?? "").Trim();
                if (string.IsNullOrWhiteSpace(childId)) continue;

                var t = so.transform;
                if (t == null) continue;

                var p = t.parent;
                while (p != null)
                {
                    if (_transformToId.TryGetValue(p, out var parentId) && !string.IsNullOrWhiteSpace(parentId))
                    {
                        if (!_descendantsById.TryGetValue(parentId, out var list))
                        {
                            list = new List<string>(8);
                            _descendantsById.Add(parentId, list);
                        }

                        list.Add(childId);
                    }

                    p = p.parent;
                }
            }
        }

        private void ApplyDefaults()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Spike candidate: ráfaga de SetActive al inicio con posibles side-effects en cascada.
            // Coste esperado: activación de objetos.
            float spikeStart = Time.realtimeSinceStartup;
            using var _scope = _pmApplyDefaults.Auto();
#endif
            int changedCount = 0;

            // Aplica defaults leyendo SceneObjectId de cada GO registrado.
            for (int i = 0; i < _all.Count; i++)
            {
                var so = _all[i];
                if (so == null) continue;

                var id = (so.Id ?? "").Trim();
                if (string.IsNullOrWhiteSpace(id)) continue;

                if (!so.applyDefaultOnStart) continue;

                _desiredActive[id] = so.defaultActive;

                var go = so.gameObject;
                if (go != null && go.activeSelf != so.defaultActive)
                {
                    go.SetActive(so.defaultActive);
                    changedCount++;
                }
            }

            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("SceneObjectService.ApplyDefaults", $"changed={changedCount} total={_all.Count}");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            LogSpikeIfNeeded("ApplyDefaults", spikeStart);
#endif
        }

        private void EnforceDesiredStates()
        {
            // Si enforceOnlyApplyDefaultOnStart=true, solo imponemos esos ids
            int enforcedCount = _enforcedIds.Count;
            if (enforcedCount == 0) return;
            if (_enforceCursor >= enforcedCount) _enforceCursor = 0;

            int checksThisTick = enforcedCount;
            if (maxEnforcedChecksPerTick > 0 && maxEnforcedChecksPerTick < enforcedCount)
                checksThisTick = maxEnforcedChecksPerTick;

            for (int i = 0; i < checksThisTick; i++)
            {
                if (_enforceCursor >= enforcedCount)
                    _enforceCursor = 0;

                var id = _enforcedIds[_enforceCursor++];
                if (string.IsNullOrWhiteSpace(id)) continue;

                if (!_map.TryGetValue(id, out var go) || go == null) continue;
                if (!_desiredActive.TryGetValue(id, out var want)) continue;

                // Si está pendiente de aplicarse de forma diferida, NO lo forzamos este frame.
                if (_pendingIds.Contains(id))
                    continue;

                if (go.activeSelf != want)
                {
                    if (logExternalChanges)
                        Debug.LogWarning($"[SceneObjectService] External change detected. Forcing '{id}' -> {want}", go);

                    go.SetActive(want);
                }
            }
        }

        public bool TryGet(string id, out GameObject go)
        {
            return TryResolveId(id, out _, out go);
        }

        private bool TryResolveId(string id, out string resolvedId, out GameObject go)
        {
            resolvedId = null;
            go = null;
            if (string.IsNullOrWhiteSpace(id)) return false;
            id = NormalizeIdInput(id);

            if (_map.Count == 0)
            {
                if (_rebuildInProgress)
                    return false;

                BuildLookupInternal(preserveDesiredStates: true);
            }

            if (TryGetDirectOrCanonical(id, out resolvedId, out go))
                return true;

            // Auto-heal: si el id no está, puede ser porque se cargó una escena aditiva o se instanció tarde.
            // Permitimos 1 rebuild por frame, incluso si ya hubo build este frame (caso Awake/Start race).
            if (Application.isPlaying &&
                rebuildOnMissingId &&
                Time.frameCount != _lastMissingIdRebuildFrame &&
                Time.frameCount != _lastLookupBuildFrame)
            {
                _lastMissingIdRebuildFrame = Time.frameCount;
                if (useIncrementalRebuildPipeline)
                {
                    RequestLookupRebuild(applyDefaultsAfterRebuild: false, immediate: false, reason: "MissingId");
                    return false;
                }

                BuildLookupInternal(preserveDesiredStates: true);
                return TryGetDirectOrCanonical(id, out resolvedId, out go);
            }

            return false;
        }

        public bool SetActive(string id, bool active)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            id = NormalizeIdInput(id);

            if (!TryResolveId(id, out var resolvedId, out var go))
            {
                if (_rebuildInProgress)
                {
                    _missingDesired[id] = active;
                    if (retryMissingSetActive)
                        StartMissingRetry(id);

                    if (verbosePerformanceLogs)
                        LogPerfVerbose($"Deferred SetActive while rebuild in progress id={id} active={active}");

                    return true;
                }

                if (Application.isPlaying && retryMissingSetActive)
                {
                    _missingDesired[id] = active;
                    StartMissingRetry(id);
                    if (_missingWarned.Add(id))
                        Debug.LogWarning($"[SceneObjectService] Object id '{id}' not found. Queued retry.");
                    return true;
                }

                Debug.LogWarning($"[SceneObjectService] Object id '{id}' not found.");
                if (StoryTransitionTrace.Enabled)
                    StoryTransitionTrace.Mark("SceneObjectService.SetActive.Missing", $"id={id} active={active}");
                return false;
            }

            if (_map.Count == 0) BuildLookup();

            // Guardamos el estado deseado SIEMPRE
            _desiredActive[resolvedId] = active;
            _missingDesired.Remove(id);
            _missingWarned.Remove(id);

            // Si estamos dentro del dispatch de StoryEventBus, difiere el SetActive real.
            if (ShouldDeferNow())
            {
                EnqueueSetActive(resolvedId);
                if (StoryTransitionTrace.Enabled)
                    StoryTransitionTrace.Mark("SceneObjectService.SetActive.Deferred", $"id={resolvedId} active={active} pending={_pending.Count}");
                return true;
            }

            if (go.activeSelf != active)
                go.SetActive(active);

            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("SceneObjectService.SetActive.Immediate", $"id={resolvedId} active={active}");

            // Si activamos un padre, re-aplicamos estados deseados a hijos registrados (independientes)
            if (preserveRegisteredChildrenStates && active)
                ReapplyDesiredToRegisteredDescendantsById(resolvedId);

            return true;
        }

        private void StartMissingRetry(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (!_missingRetryRunning.Add(id)) return;
            StartCoroutine(RetryMissingSetActive(id));
        }

        private IEnumerator RetryMissingSetActive(string id)
        {
            int maxFrames = Mathf.Max(1, missingSetActiveRetryFrames);

            for (int i = 0; i < maxFrames; i++)
            {
                yield return null;

                if (!_missingDesired.TryGetValue(id, out var want))
                {
                    _missingRetryRunning.Remove(id);
                    yield break;
                }

                if (Time.frameCount != _lastLookupBuildFrame)
                {
                    if (useIncrementalRebuildPipeline)
                        RequestLookupRebuild(applyDefaultsAfterRebuild: false, immediate: false, reason: "MissingRetry");
                    else
                        BuildLookupInternal(preserveDesiredStates: true);
                }

                _lastMissingIdRebuildFrame = Time.frameCount;

                if (!TryResolveId(id, out var resolvedId, out var go))
                    continue;

                _desiredActive[resolvedId] = want;

                if (ShouldDeferNow())
                    EnqueueSetActive(resolvedId);
                else if (go.activeSelf != want)
                    go.SetActive(want);

                if (preserveRegisteredChildrenStates && want)
                    ReapplyDesiredToRegisteredDescendantsById(resolvedId);

                _missingDesired.Remove(id);
                _missingWarned.Remove(id);
                _missingRetryRunning.Remove(id);
                yield break;
            }

            if (_missingDesired.ContainsKey(id))
            {
                Debug.LogWarning($"[SceneObjectService] Object id '{id}' still missing after {maxFrames} retry frames.");
                LogMissingIdDiagnostics(id);
            }

            _missingRetryRunning.Remove(id);
        }

        private bool TryGetDirectOrCanonical(string id, out string resolvedId, out GameObject go)
        {
            resolvedId = null;
            go = null;
            if (string.IsNullOrWhiteSpace(id)) return false;

            if (_map.TryGetValue(id, out go) && go != null)
            {
                resolvedId = id;
                return true;
            }

            string canonical = CanonicalizeId(id);
            if (string.IsNullOrWhiteSpace(canonical)) return false;
            if (!_canonicalToId.TryGetValue(canonical, out var canonicalId)) return false;
            if (string.IsNullOrWhiteSpace(canonicalId)) return false;
            if (!_map.TryGetValue(canonicalId, out go) || go == null) return false;

            resolvedId = canonicalId;
            return true;
        }

        private void AddCanonicalAlias(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            string canonical = CanonicalizeId(id);
            if (string.IsNullOrWhiteSpace(canonical)) return;
            if (_canonicalToId.ContainsKey(canonical)) return;
            _canonicalToId.Add(canonical, id);
        }

        private static string NormalizeIdInput(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return string.Empty;
            id = id.Trim();
            return id.Replace('\\', '/');
        }

        private static string CanonicalizeId(string id)
        {
            id = NormalizeIdInput(id);
            if (string.IsNullOrWhiteSpace(id)) return string.Empty;

            string[] parts = id.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i]?.Trim() ?? string.Empty;
                part = UnityCloneSuffixRegex.Replace(part, string.Empty);
                parts[i] = part;
            }

            return string.Join("/", parts);
        }

        private void LogMissingIdDiagnostics(string requestedId)
        {
            var sb = new StringBuilder(512);
            string canonical = CanonicalizeId(requestedId);

            sb.Append("[SceneObjectService] Missing ID diagnostics");
            sb.Append(" | requested='").Append(requestedId).Append("'");
            sb.Append(" | canonical='").Append(canonical).Append("'");
            sb.Append(" | registered=").Append(_map.Count);
            sb.Append(" | loadedScenes=");

            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (i > 0) sb.Append(",");
                sb.Append(sc.name).Append(sc.isLoaded ? "(L)" : "(U)");
            }

            string leaf = string.Empty;
            if (!string.IsNullOrWhiteSpace(canonical))
            {
                int lastSlash = canonical.LastIndexOf('/');
                leaf = lastSlash >= 0 && lastSlash < canonical.Length - 1
                    ? canonical.Substring(lastSlash + 1)
                    : canonical;
            }

            if (!string.IsNullOrWhiteSpace(leaf))
            {
                int matches = 0;
                sb.Append(" | leafMatches=");
                foreach (var key in _map.Keys)
                {
                    if (!key.EndsWith("/" + leaf, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(key, leaf, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (matches > 0) sb.Append(" ; ");
                    sb.Append(key);
                    matches++;
                    if (matches >= 8) break;
                }

                if (matches == 0)
                    sb.Append("(none)");
            }

            Debug.LogWarning(sb.ToString());
        }

        private bool ShouldDeferNow()
        {
            if (!Application.isPlaying) return false;
            if (deferAllSetActiveRequests) return true;
            if (!deferActivationDuringStoryDispatch) return false;
            return StoryEventBus.IsDispatching;
        }

        private bool CanRunHeavyPipelineWork()
        {
            if (!respectNarrativePipelinePhases)
                return true;

            if (!NarrativeTransitionPipeline.IsActive)
                return true;

            return NarrativeTransitionPipeline.IsPhaseReached(NarrativeTransitionPipeline.Phase.HeavyObjectChanges);
        }

        private void EnqueueSetActive(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (!_pendingIds.Add(id)) return;

            _pending.Enqueue(new PendingSetActive { id = id });
        }

        private void ProcessDeferredActivationBudgeted()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Spike candidate: drenado de SetActive diferidos en transición de historia.
            // Coste esperado: activación de objetos + posibles rebuilds indirectos de UI.
            float spikeStart = Time.realtimeSinceStartup;
            using var _scope = _pmProcessDeferredSetActive.Auto();
#endif
            if (_pending.Count == 0) return;
            if (!CanRunHeavyPipelineWork()) return;

            int processed = 0;
            float start = Time.realtimeSinceStartup;

            while (_pending.Count > 0 && processed < maxSetActivesPerFrame)
            {
                var p = _pending.Dequeue();
                var id = p.id;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (!_map.TryGetValue(id, out var go) || go == null)
                {
                    _pendingIds.Remove(id);
                    continue;
                }
                if (!_desiredActive.TryGetValue(id, out var want))
                {
                    _pendingIds.Remove(id);
                    continue;
                }

                // Coste dominante: SetActive real. Si se supera el límite extra, reencola para el próximo frame.
                if (go.activeSelf != want && !CanProcessAnotherDeferredSetActiveThisFrame())
                {
                    _pending.Enqueue(p);
                    break;
                }

                if (go.activeSelf != want)
                    go.SetActive(want);

                // Al activar, re-aplica estados a descendientes registrados sin recorrer toda la escena.
                if (preserveRegisteredChildrenStates && want)
                    ReapplyDesiredToRegisteredDescendantsById(id);

                _pendingIds.Remove(id);
                processed++;

                if (maxSetActiveMsPerFrame > 0f)
                {
                    float elapsedMs = (Time.realtimeSinceStartup - start) * 1000f;
                    if (elapsedMs >= maxSetActiveMsPerFrame)
                        break;
                }
            }

            if (processed > 0 && StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("SceneObjectService.ProcessDeferredSetActive", $"processed={processed} remaining={_pending.Count}");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            LogSpikeIfNeeded("ProcessDeferredActivationBudgeted", spikeStart);
#endif
        }

        public bool Show(string id) => SetActive(id, true);
        public bool Hide(string id) => SetActive(id, false);

        public int SetActiveGroup(string group, bool active)
        {
            if (string.IsNullOrWhiteSpace(group)) return 0;
            if (_map.Count == 0) BuildLookup();

            if (!_groups.TryGetValue(group, out var ids)) return 0;

            int changed = 0;
            for (int i = 0; i < ids.Count; i++)
                if (SetActive(ids[i], active)) changed++;

            return changed;
        }

        public int HideGroup(string group) => SetActiveGroup(group, false);
        public int ShowGroup(string group) => SetActiveGroup(group, true);

        public int SetActiveByPrefix(string prefix, bool active)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return 0;
            if (_map.Count == 0) BuildLookup();

            int changed = 0;
            foreach (var kv in _map)
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (SetActive(kv.Key, active)) changed++;
            }

            return changed;
        }

        // -------------------- Internal --------------------

        private void ReapplyDesiredToRegisteredDescendantsById(string parentId)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Spike candidate: SetActive en descendientes al reactivar padres registrados.
            // Coste esperado: traversal de jerarquía + activación.
            using var _scope = _pmReapplyDescendants.Auto();
#endif
            if (string.IsNullOrWhiteSpace(parentId)) return;
            if (_descendantsById.Count == 0 && _all.Count > 0)
                BuildDescendantTable();

            if (!_descendantsById.TryGetValue(parentId, out var list) || list == null || list.Count == 0)
                return;

            for (int i = 0; i < list.Count; i++)
            {
                var childId = list[i];
                if (string.IsNullOrWhiteSpace(childId)) continue;

                // Si el hijo está pendiente, no lo toques ahora.
                if (_pendingIds.Contains(childId))
                    continue;

                if (!_map.TryGetValue(childId, out var go) || go == null) continue;
                if (!_desiredActive.TryGetValue(childId, out var want)) continue;

                if (go.activeSelf != want)
                    go.SetActive(want);
            }
        }

        private List<SceneObjectId> FindAllSceneObjectIds()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Spike candidate: escaneo de escenas cargadas y roots en cambios de carga/aditivas.
            // Coste esperado: scene traversal.
            using var _scope = _pmFindAllSceneObjectIds.Auto();
#endif
            var result = new List<SceneObjectId>(256);

            if (scanRoots != null && scanRoots.Count > 0)
            {
                bool anyValidRoot = false;
                for (int r = 0; r < scanRoots.Count; r++)
                {
                    var root = scanRoots[r];
                    if (root == null) continue;

                    anyValidRoot = true;
                    root.GetComponentsInChildren(true, result);
                }

                // Si scanRoots está configurado pero no aporta resultados reales,
                // caemos al escaneo global para evitar lookups vacíos.
                if (anyValidRoot && result.Count > 0)
                    return result;
            }

            // IMPORTANT (Unity 6): evitamos FindObjectsByType / Resources.FindObjectsOfTypeAll
            // porque pueden disparar StackOverflowException en proyectos con escenas aditivas.
            // Recorremos SOLO escenas cargadas y sus roots (incluyendo inactivos).
            var rootBuffer = new List<GameObject>(128);

            int sceneCount = SceneManager.sceneCount;
            for (int si = 0; si < sceneCount; si++)
            {
                var sc = SceneManager.GetSceneAt(si);
                if (!sc.IsValid() || !sc.isLoaded) continue;

                rootBuffer.Clear();
#if UNITY_2021_2_OR_NEWER
                sc.GetRootGameObjects(rootBuffer);
#else
                rootBuffer.AddRange(sc.GetRootGameObjects());
#endif

                for (int ri = 0; ri < rootBuffer.Count; ri++)
                {
                    var root = rootBuffer[ri];
                    if (root == null) continue;
                    root.GetComponentsInChildren(true, result);
                }
            }

            if (result.Count > 0)
                return result;

            if (useResourcesFallbackWhenEmpty)
            {
                TryAppendFromResourcesFindAll(result);
                if (result.Count > 0)
                    return result;
            }

            if (_lastEmptyScanWarningFrame != Time.frameCount)
            {
                _lastEmptyScanWarningFrame = Time.frameCount;
                Debug.LogWarning("[SceneObjectService] FindAllSceneObjectIds returned 0. Check SceneObjectId components, scanRoots, and loaded scenes.");
            }

            return result;
        }

        private void TryAppendFromResourcesFindAll(List<SceneObjectId> result)
        {
            if (result == null) return;

            try
            {
                var all = Resources.FindObjectsOfTypeAll<SceneObjectId>();
                if (all == null || all.Length == 0) return;

                var seen = new HashSet<SceneObjectId>();
                for (int i = 0; i < all.Length; i++)
                {
                    var so = all[i];
                    if (so == null) continue;
                    if (!seen.Add(so)) continue;

                    var go = so.gameObject;
                    if (go == null) continue;

                    var sc = go.scene;
                    if (!sc.IsValid() || !sc.isLoaded) continue;

                    result.Add(so);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SceneObjectService] Resources fallback failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => MarkLookupDirty();
        private void OnSceneUnloaded(Scene scene) => MarkLookupDirty();
        private void OnActiveSceneChanged(Scene oldScene, Scene newScene) => MarkLookupDirty();

        private void MarkLookupDirty()
        {
            if (!Application.isPlaying) return;
            RequestLookupRebuild(
                applyDefaultsAfterRebuild: autoApplyDefaultsOnRebuild,
                immediate: false,
                reason: "SceneChange");
        }

        private static string GetPath(Transform t)
        {
            if (t == null) return "";
            var path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = $"{t.name}/{path}";
            }
            return path;
        }
    }
}
