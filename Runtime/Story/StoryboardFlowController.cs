using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

/// <summary>
/// Define el orden de "escenas lógicas" (sceneId del guion) dentro de una MISMA escena Unity,
/// y gestiona la activación del contenido asociado a cada sceneId (StoryboardContentRoot).
///
/// FIX CRÍTICO:
/// - Evita StackOverflow por recursión RebuildCache <-> ActivateContentForScene.
/// - Evita FindObjectsByType(Include) (problemático en Unity 6 con additivas) recorriendo escenas cargadas.
/// </summary>
public class StoryboardFlowController : MonoBehaviour
{
    [Header("Orden de capítulos (opcional)")]
    [Tooltip("Si se rellena, se usará ESTE orden. Si está vacío, se auto-detecta desde StoryDatabase.")]
    public List<string> customSceneOrder = new List<string>();

    [Header("Contenido por capítulo")]
    [Tooltip("Si está activo, se activará/desactivará automáticamente el contenido (StoryboardContentRoot) según el sceneId actual.")]
    public bool autoToggleContentRoots = true;

    [Tooltip("Si se cargan/descargan escenas aditivas, se marca la caché como sucia.")]
    public bool markCacheDirtyOnSceneLoad = true;

    [Tooltip("Si no encuentra roots para el sceneId, fuerza rebuild de caché.")]
    public bool forceRebuildIfSceneMissing = true;

    [Tooltip("Eventos Unity opcionales para transiciones (fade, audio, etc.)")]
    public UnityEvent onBeforeSceneChange;
    public UnityEvent onAfterSceneChange;

    [Header("Debug")]
    public bool verboseLogs = false;

    private readonly List<string> _runtimeOrder = new List<string>();
    private StoryDatabase _builtForDatabase;

    private readonly Dictionary<string, List<StoryboardContentRoot>> _rootsByScene =
        new Dictionary<string, List<StoryboardContentRoot>>(StringComparer.OrdinalIgnoreCase);

    private StoryManager _storyManager;

    private bool _cacheDirty = true;

    // Guards contra recursión
    private bool _isRebuilding;
    private bool _isApplying;

    private void Awake()
    {
#if UNITY_2023_1_OR_NEWER
        _storyManager = FindFirstObjectByType<StoryManager>();
#else
        _storyManager = FindObjectOfType<StoryManager>();
#endif

        if (_storyManager != null)
        {
            _storyManager.OnLogicalSceneWillChange += HandleWillChange;
            _storyManager.OnLogicalSceneChanged += HandleChanged;
        }

        if (markCacheDirtyOnSceneLoad)
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        // No construimos caché aquí sí o sí, porque puede que todavía no estén cargadas las additivas.
        // La construiremos en el primer Activate / Rebuild.
        _cacheDirty = true;
    }

    private void OnDestroy()
    {
        if (_storyManager != null)
        {
            _storyManager.OnLogicalSceneWillChange -= HandleWillChange;
            _storyManager.OnLogicalSceneChanged -= HandleChanged;
        }

        if (markCacheDirtyOnSceneLoad)
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }
    }

    private void OnSceneLoaded(Scene s, LoadSceneMode m)
    {
        _cacheDirty = true;
        if (verboseLogs) Debug.Log($"[StoryboardFlowController] sceneLoaded '{s.name}' -> cacheDirty");
    }

    private void OnSceneUnloaded(Scene s)
    {
        _cacheDirty = true;
        if (verboseLogs) Debug.Log($"[StoryboardFlowController] sceneUnloaded '{s.name}' -> cacheDirty");
    }

    private void HandleWillChange(string from, string to)
    {
        onBeforeSceneChange?.Invoke();

        if (autoToggleContentRoots)
            ActivateContentForScene(to);
    }

    private void HandleChanged(string to)
    {
        // También se llama al inicio de la historia (sin WillChange)
        if (autoToggleContentRoots)
            ActivateContentForScene(to);

        onAfterSceneChange?.Invoke();
    }

    // ---------------------------------------------------------------------
    // API usada por StoryManager
    // ---------------------------------------------------------------------

    public bool TryGetNextSceneId(string currentSceneId, StoryDatabase db, out string nextSceneId)
    {
        nextSceneId = null;
        EnsureOrderBuilt(db);

        if (string.IsNullOrEmpty(currentSceneId))
            return false;

        int idx = _runtimeOrder.FindIndex(s => string.Equals(s, currentSceneId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return false;

        int nextIdx = idx + 1;
        if (nextIdx >= _runtimeOrder.Count)
            return false;

        nextSceneId = _runtimeOrder[nextIdx];
        return !string.IsNullOrEmpty(nextSceneId);
    }

    public string GetFirstSceneIdFromDatabase(StoryDatabase db)
    {
        EnsureOrderBuilt(db);
        return _runtimeOrder.Count > 0 ? _runtimeOrder[0] : null;
    }

    // ---------------------------------------------------------------------
    // ORDEN
    // ---------------------------------------------------------------------

    private void EnsureOrderBuilt(StoryDatabase db)
    {
        if (db == null)
            return;

        if (_builtForDatabase == db && _runtimeOrder.Count > 0)
            return;

        _runtimeOrder.Clear();
        _builtForDatabase = db;

        // 1) custom
        if (customSceneOrder != null && customSceneOrder.Count > 0)
        {
            for (int i = 0; i < customSceneOrder.Count; i++)
            {
                var id = customSceneOrder[i];
                if (string.IsNullOrEmpty(id)) continue;
                if (_runtimeOrder.Exists(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))) continue;
                _runtimeOrder.Add(id);
            }
            return;
        }

        // 2) auto
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (db.entries != null)
        {
            for (int i = 0; i < db.entries.Count; i++)
            {
                var e = db.entries[i];
                if (e == null) continue;
                if (string.IsNullOrEmpty(e.scene)) continue;

                if (unique.Add(e.scene))
                    _runtimeOrder.Add(e.scene);
            }
        }

        _runtimeOrder.Sort(CompareSceneIdsNatural);
    }

    private static int CompareSceneIdsNatural(string a, string b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return 1;
        if (b == null) return -1;

        int na = ExtractFirstNumber(a);
        int nb = ExtractFirstNumber(b);

        if (na != nb)
            return na.CompareTo(nb);

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static int ExtractFirstNumber(string s)
    {
        if (string.IsNullOrEmpty(s))
            return int.MaxValue;

        var m = Regex.Match(s, @"\d+");
        if (!m.Success) return int.MaxValue;

        if (int.TryParse(m.Value, out int n))
            return n;

        return int.MaxValue;
    }

    // ---------------------------------------------------------------------
    // CONTENIDO (activar/desactivar)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Rebuild externo usado por otros sistemas (EnvironmentSceneLoader, etc.).
    /// OJO: NO llama a ActivateContentForScene para evitar recursión.
    /// </summary>
    public void RebuildCache(string applySceneId)
    {
        CacheContentRootsInternal();

        // Aplica si procede, pero usando método interno que NO rebuild.
        if (!string.IsNullOrEmpty(applySceneId))
            ApplyContentForSceneInternal(applySceneId);
    }

    [ContextMenu("Rebuild Cache (No Apply)")]
    public void RebuildCache()
    {
        CacheContentRootsInternal();
    }

    public void ActivateContentForScene(string sceneId)
    {
        if (string.IsNullOrEmpty(sceneId))
            return;

        // Evita reentradas (por ejemplo, si activar/desactivar dispara un evento externo)
        if (_isApplying)
            return;

        if (_cacheDirty || _rootsByScene.Count == 0)
            CacheContentRootsInternal();

        if (forceRebuildIfSceneMissing && !_rootsByScene.ContainsKey(sceneId))
            CacheContentRootsInternal();

        ApplyContentForSceneInternal(sceneId);
    }

    /// <summary>
    /// Construye la caché SIN usar FindObjectsByType/Resources.FindObjectsOfTypeAll.
    /// Recorre escenas cargadas y sus root objects.
    /// </summary>
    private void CacheContentRootsInternal()
    {
        if (_isRebuilding) return;
        _isRebuilding = true;

        try
        {
            _cacheDirty = false;
            _rootsByScene.Clear();

            // Recorremos escenas cargadas
            int sceneCount = SceneManager.sceneCount;
            for (int si = 0; si < sceneCount; si++)
            {
                var scene = SceneManager.GetSceneAt(si);
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;

                var roots = scene.GetRootGameObjects();
                for (int ri = 0; ri < roots.Length; ri++)
                {
                    var go = roots[ri];
                    if (go == null) continue;

                    // Obtenemos todos los StoryboardContentRoot (incluye inactivos)
                    var contentRoots = go.GetComponentsInChildren<StoryboardContentRoot>(true);
                    if (contentRoots == null || contentRoots.Length == 0) continue;

                    for (int i = 0; i < contentRoots.Length; i++)
                    {
                        var r = contentRoots[i];
                        if (r == null) continue;
                        if (string.IsNullOrEmpty(r.sceneId)) continue;

                        if (!_rootsByScene.TryGetValue(r.sceneId, out var list))
                        {
                            list = new List<StoryboardContentRoot>(4);
                            _rootsByScene.Add(r.sceneId, list);
                        }

                        list.Add(r);
                    }
                }
            }

            if (verboseLogs)
            {
                int total = 0;
                foreach (var kv in _rootsByScene) total += kv.Value.Count;
                Debug.Log($"[StoryboardFlowController] Cache rebuilt. sceneKeys={_rootsByScene.Count} totalRoots={total}");
            }
        }
        finally
        {
            _isRebuilding = false;
        }
    }

    /// <summary>
    /// Aplica activación/desactivación. NO rebuild.
    /// </summary>
    private void ApplyContentForSceneInternal(string sceneId)
    {
        _isApplying = true;
        try
        {
            foreach (var kv in _rootsByScene)
            {
                bool shouldBeActive = string.Equals(kv.Key, sceneId, StringComparison.OrdinalIgnoreCase);
                var roots = kv.Value;

                for (int i = 0; i < roots.Count; i++)
                {
                    var r = roots[i];
                    if (r == null) continue;
                    r.SetActive(shouldBeActive);
                }
            }
        }
        finally
        {
            _isApplying = false;
        }
    }
}
