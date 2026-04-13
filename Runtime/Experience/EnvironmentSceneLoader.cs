using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using PlayGo.SceneObjects;

public class EnvironmentSceneLoader : MonoBehaviour
{
    [Serializable]
    public class Mapping
    {
        public string logicalSceneId;    // ej: S1_HORNO
        public string unitySceneName;    // ej: FabricaDeVidrio
    }

    [Header("Refs")]
    [SerializeField] private StoryManager storyManager;
    [SerializeField] private FadeManager fadeManager;

    [Header("Mapping (Logical Scene -> Unity Scene)")]
    [SerializeField] private List<Mapping> mappings = new List<Mapping>();

    [Header("Initial")]
    [SerializeField] private bool loadInitialOnStart = true;

    [Header("Behaviour")]
    [SerializeField] private bool alsoListenLogicalSceneChanged = true;
    [SerializeField] private bool unloadPreviousBeforeLoad = true;
    [SerializeField] private bool setEnvironmentAsActiveScene = true;
    [SerializeField, Min(0)] private int extraBlackFrames = 0;

    [Header("Safety")]
    [SerializeField, Min(0.1f)] private float fadeTimeoutSeconds = 2.0f;
    [SerializeField, Min(0.5f)] private float loadTimeoutSeconds = 25.0f;
    [SerializeField, Min(0.5f)] private float unloadTimeoutSeconds = 25.0f;

    [Header("After Load")]
    [SerializeField] private bool rebuildSceneObjectService = true;
    [SerializeField] private bool rebuildStoryboardFlowCache = true;

    [Header("Debug")]
    [SerializeField] private bool verboseLogs = true;

    public bool IsBusy => _switchRoutine != null;
    public string CurrentEnvironmentSceneName => _currentEnvSceneName;

    public event Action<string, string> OnEnvironmentWillSwitch; // (from,to)
    public event Action<string> OnEnvironmentSwitched;           // (to)

    private Coroutine _switchRoutine;
    private int _generation;
    private string _currentEnvSceneName = null;
    private string _pendingEnvSceneName = null;

    private void Awake()
    {
        if (storyManager == null)
        {
#if UNITY_2023_1_OR_NEWER
            storyManager = FindFirstObjectByType<StoryManager>();
#else
            storyManager = FindObjectOfType<StoryManager>();
#endif
        }

        if (fadeManager == null)
            fadeManager = FadeManager.Instance;
    }

    private void OnEnable()
    {
        if (storyManager != null && alsoListenLogicalSceneChanged)
            storyManager.OnLogicalSceneChanged += HandleLogicalSceneChanged;
    }

    private void OnDisable()
    {
        if (storyManager != null && alsoListenLogicalSceneChanged)
            storyManager.OnLogicalSceneChanged -= HandleLogicalSceneChanged;
    }

    private void Start()
    {
        if (!loadInitialOnStart || storyManager == null)
            return;

        // Carga inicial según currentSceneId (sin esperar a StartStory)
        var target = ResolveUnitySceneName(storyManager.currentSceneId);
        if (!string.IsNullOrEmpty(target))
            RequestSwitch(target, $"Initial({storyManager.currentSceneId})");
    }

    private void HandleLogicalSceneChanged(string logicalSceneId)
    {
        var target = ResolveUnitySceneName(logicalSceneId);
        if (string.IsNullOrEmpty(target))
            return;

        RequestSwitch(target, $"Changed({logicalSceneId})");
    }

    public bool TryRequestSwitchByLogicalSceneId(string logicalSceneId, string reason)
    {
        if (string.IsNullOrWhiteSpace(logicalSceneId))
            return true;

        var target = ResolveUnitySceneName(logicalSceneId);
        if (string.IsNullOrWhiteSpace(target))
        {
            Log($"WARN: No mapping found for logicalSceneId='{logicalSceneId}'.");
            return false;
        }

        RequestSwitch(target, reason);
        return true;
    }

    public bool IsReadyForLogicalScene(string logicalSceneId)
    {
        if (string.IsNullOrWhiteSpace(logicalSceneId))
            return true;

        var target = ResolveUnitySceneName(logicalSceneId);
        if (string.IsNullOrWhiteSpace(target))
            return false;

        if (IsBusy)
            return false;

        return string.Equals(_currentEnvSceneName, target, StringComparison.OrdinalIgnoreCase);
    }

    public void RequestSwitch(string targetUnityScene, string reason)
    {
        if (string.IsNullOrEmpty(targetUnityScene))
            return;

        // Si ya estamos en esa escena, no hagas nada.
        if (string.Equals(_currentEnvSceneName, targetUnityScene, StringComparison.OrdinalIgnoreCase))
        {
            Log($"RequestSwitch ignored (already current) -> '{targetUnityScene}' reason='{reason}'");
            return;
        }

        // Si ya hay un swap en curso HACIA ese destino, no reinicies (esto evita el caos).
        if (_switchRoutine != null && string.Equals(_pendingEnvSceneName, targetUnityScene, StringComparison.OrdinalIgnoreCase))
        {
            Log($"RequestSwitch ignored (already switching to same target) -> '{targetUnityScene}' reason='{reason}'");
            return;
        }

        _generation++;
        _pendingEnvSceneName = targetUnityScene;

        if (_switchRoutine != null)
            StopCoroutine(_switchRoutine);

        _switchRoutine = StartCoroutine(SwitchRoutine(_generation, targetUnityScene, reason));
    }

    private IEnumerator SwitchRoutine(int gen, string target, string reason)
    {
        string prev = _currentEnvSceneName;
        OnEnvironmentWillSwitch?.Invoke(prev, target);

        Log($"Worker START gen={gen} '{prev ?? "null"}' -> '{target}' reason='{reason}'");
        StoryEventBus.BeginGate();
        Log($"Gate OPEN (gen={gen})");

        try
        {
            // FadeOut (con timeout)
            if (fadeManager != null)
                yield return RunWithTimeout(fadeManager.FadeOut(), fadeTimeoutSeconds, $"FadeOut gen={gen}");
            else
                Log($"WARN: FadeManager null (gen={gen}), skipping FadeOut.");

            for (int i = 0; i < extraBlackFrames; i++)
                yield return null;

            // Unload previous
            if (unloadPreviousBeforeLoad && !string.IsNullOrEmpty(prev))
            {
                var prevScene = SceneManager.GetSceneByName(prev);
                if (prevScene.IsValid() && prevScene.isLoaded)
                {
                    Log($"Unload START '{prev}' (gen={gen})");
                    var op = SceneManager.UnloadSceneAsync(prev);
                    yield return WaitAsyncOp(op, unloadTimeoutSeconds, $"Unload '{prev}' gen={gen}");
                    Log($"Unload END '{prev}' (gen={gen})");
                }
            }

            // Load target if not already loaded
            var targetScene = SceneManager.GetSceneByName(target);
            if (!targetScene.IsValid() || !targetScene.isLoaded)
            {
                Log($"Load START '{target}' (gen={gen})");
                var op = SceneManager.LoadSceneAsync(target, LoadSceneMode.Additive);
                yield return WaitAsyncOp(op, loadTimeoutSeconds, $"Load '{target}' gen={gen}");
                Log($"Load END '{target}' (gen={gen})");
            }
            else
            {
                Log($"Load skipped '{target}' already loaded (gen={gen})");
            }

            // Si nos han pisado con otra request, corta aquí (pero pasa por finally para cerrar gate).
            if (gen != _generation)
            {
                Log($"Worker ABORT (newer gen exists) mine={gen} current={_generation}");
                yield break;
            }

            // Set active scene for lighting / RenderSettings
            if (setEnvironmentAsActiveScene)
            {
                targetScene = SceneManager.GetSceneByName(target);
                if (targetScene.IsValid() && targetScene.isLoaded)
                {
                    SceneManager.SetActiveScene(targetScene);
                    Log($"ActiveScene = '{target}' (gen={gen})");
                }
                else
                {
                    Log($"WARN: Cannot SetActiveScene '{target}' (not loaded/invalid) gen={gen}");
                }
            }

            _currentEnvSceneName = target;

            // Rebuild registries after additive load
            if (rebuildSceneObjectService && SceneObjectService.Instance != null)
            {
                // PERF: evita BuildLookup + ApplyDefaults por separado (duplicaban trabajo en transiciones).
                SceneObjectService.Instance.RequestRebuild(applyDefaultsAfterRebuild: true, immediate: true);
                Log($"SceneObjectService rebuild requested (apply defaults) (gen={gen})");
            }

            if (rebuildStoryboardFlowCache)
            {
#if UNITY_2023_1_OR_NEWER
                var flow = FindFirstObjectByType<StoryboardFlowController>();
#else
                var flow = FindObjectOfType<StoryboardFlowController>();
#endif
                if (flow != null && storyManager != null)
                {
                    flow.RebuildCache(storyManager.currentSceneId);
                    Log($"StoryboardFlowController cache rebuilt for '{storyManager.currentSceneId}' (gen={gen})");
                }
            }

            // FadeIn (con timeout)
            if (fadeManager != null)
                yield return RunWithTimeout(fadeManager.FadeIn(), fadeTimeoutSeconds, $"FadeIn gen={gen}");
            else
                Log($"WARN: FadeManager null (gen={gen}), skipping FadeIn.");

            Log($"Worker END gen={gen} current='{_currentEnvSceneName}'");
            OnEnvironmentSwitched?.Invoke(_currentEnvSceneName);
        }
        finally
        {
            // Pase lo que pase: cerrar gate SIEMPRE.
            StoryEventBus.EndGate();
            Log($"Gate CLOSE (gen={gen})");

            // Solo limpia si seguimos siendo el “dueño” de la rutina actual
            if (_switchRoutine != null && gen == _generation)
            {
                _pendingEnvSceneName = null;
                _switchRoutine = null;
            }
        }
    }

    private string ResolveUnitySceneName(string logicalSceneId)
    {
        if (string.IsNullOrEmpty(logicalSceneId) || mappings == null)
            return null;

        for (int i = 0; i < mappings.Count; i++)
        {
            var m = mappings[i];
            if (m == null) continue;
            if (string.Equals(m.logicalSceneId, logicalSceneId, StringComparison.OrdinalIgnoreCase))
                return m.unitySceneName;
        }

        return null;
    }

    private IEnumerator WaitAsyncOp(AsyncOperation op, float timeout, string label)
    {
        if (op == null)
        {
            Log($"ERROR: AsyncOperation null in {label}");
            yield break;
        }

        float start = Time.realtimeSinceStartup;
        while (!op.isDone)
        {
            if (Time.realtimeSinceStartup - start > timeout)
            {
                Log($"ERROR: TIMEOUT in {label} (>{timeout:0.0}s) progress={op.progress:0.00}");
                yield break;
            }
            yield return null;
        }
    }

    private IEnumerator RunWithTimeout(IEnumerator routine, float timeout, string label)
    {
        if (routine == null)
            yield break;

        bool done = false;

        IEnumerator Wrapper()
        {
            yield return routine;
            done = true;
        }

        var c = StartCoroutine(Wrapper());
        float start = Time.realtimeSinceStartup;

        while (!done)
        {
            if (Time.realtimeSinceStartup - start > timeout)
            {
                Log($"WARN: TIMEOUT in {label} (>{timeout:0.0}s). Forcing continue.");
                // Si es un fade, cancelamos para no dejarlo colgado
                fadeManager?.CancelFade(snapToCurrentAlpha: true);
                StopCoroutine(c);
                yield break;
            }
            yield return null;
        }
    }

    private void Log(string msg)
    {
        if (!verboseLogs) return;
        Debug.Log($"[EnvironmentSceneLoader] {msg}");
    }
}
