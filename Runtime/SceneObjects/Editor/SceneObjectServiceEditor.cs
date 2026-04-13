#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PlayGo.SceneObjects.Editor
{
    [CustomEditor(typeof(SceneObjectService))]
    public sealed class SceneObjectServiceEditor : UnityEditor.Editor
    {
        private const double RescanDebounceSeconds = 0.20d;
        private const float RowHeightEstimate = 74f;

        private SerializedProperty _scanRootsProp;
        private SerializedProperty _rescanOnStartProp;

        private string _search = string.Empty;
        private string _groupFilter = "All";
        private bool _onlyDuplicates;
        private bool _onlyMissingId;
        private bool _allowEditModeToggles;
        private bool _respectScanRoots = true;

        private bool _foldSummary = true;
        private bool _foldList = true;

        private Vector2 _scroll;

        private readonly List<Row> _rows = new(256);
        private readonly List<Row> _filteredRows = new(256);
        private readonly HashSet<string> _groups = new(StringComparer.OrdinalIgnoreCase);

        private int _totalCount;
        private int _missingCount;
        private int _duplicateCount;

        private GUIStyle _tagStyle;
        private GUIStyle _rowBox;

        private static readonly Color TintDup = new(1f, 0.35f, 0.35f, 0.18f);
        private static readonly Color TintMissing = new(1f, 0.85f, 0.2f, 0.18f);

        private SearchField _searchField;
        private bool _rescanQueued;
        private double _lastRescanRequestTime;

        private sealed class Row
        {
            public SceneObjectId comp;
            public GameObject go;
            public string id;
            public string group;
            public string path;
            public bool isMissingId;
            public bool isDuplicate;
        }

        private void OnEnable()
        {
            _scanRootsProp = serializedObject.FindProperty("scanRoots");
            _rescanOnStartProp = serializedObject.FindProperty("rescanOnStart");

            EnsureStyles();
            Rescan();
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            _searchField = new SearchField();
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

            if (_rescanQueued)
            {
                EditorApplication.delayCall -= DebouncedRescanTick;
                _rescanQueued = false;
            }
        }

        private void OnPlayModeStateChanged(PlayModeStateChange _)
        {
            RequestRescan(immediate: true);
        }

        private void OnHierarchyChanged()
        {
            RequestRescan(immediate: false);
        }

        private void RequestRescan(bool immediate)
        {
            _lastRescanRequestTime = EditorApplication.timeSinceStartup;

            if (immediate)
            {
                if (_rescanQueued)
                {
                    EditorApplication.delayCall -= DebouncedRescanTick;
                    _rescanQueued = false;
                }

                Rescan();
                Repaint();
                return;
            }

            if (_rescanQueued)
                return;

            _rescanQueued = true;
            EditorApplication.delayCall += DebouncedRescanTick;
        }

        private void DebouncedRescanTick()
        {
            EditorApplication.delayCall -= DebouncedRescanTick;

            if (this == null || target == null)
            {
                _rescanQueued = false;
                return;
            }

            double elapsed = EditorApplication.timeSinceStartup - _lastRescanRequestTime;
            if (elapsed < RescanDebounceSeconds)
            {
                EditorApplication.delayCall += DebouncedRescanTick;
                return;
            }

            _rescanQueued = false;
            Rescan();
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            EnsureStyles();
            serializedObject.Update();

            DrawToolbar();

            EditorGUILayout.Space(6);
            DrawSettings();

            EditorGUILayout.Space(8);
            DrawSummary();

            EditorGUILayout.Space(8);
            DrawList();

            serializedObject.ApplyModifiedProperties();
        }

        private void EnsureStyles()
        {
            _tagStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(6, 6, 2, 2),
                fontStyle = FontStyle.Bold
            };

            _rowBox ??= new GUIStyle("HelpBox")
            {
                padding = new RectOffset(8, 8, 6, 6),
                margin = new RectOffset(0, 0, 2, 2)
            };
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("SceneObjectService", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();

                string newSearch = _searchField.OnToolbarGUI(_search ?? string.Empty);
                if (!string.Equals(newSearch, _search, StringComparison.Ordinal))
                {
                    _search = newSearch;
                    RebuildFilteredCache();
                }

                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50)))
                {
                    _search = string.Empty;
                    GUI.FocusControl(null);
                    RebuildFilteredCache();
                }

                GUILayout.Space(6);

                if (GUILayout.Button("Scan", EditorStyles.toolbarButton, GUILayout.Width(44)))
                    RequestRescan(immediate: true);

                if (GUILayout.Button("Ping", EditorStyles.toolbarButton, GUILayout.Width(44)))
                    EditorGUIUtility.PingObject(target);
            }
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope("HelpBox"))
            {
                if (_rescanOnStartProp != null)
                    EditorGUILayout.PropertyField(_rescanOnStartProp);

                if (_scanRootsProp != null)
                {
                    EditorGUILayout.PropertyField(_scanRootsProp, includeChildren: true);
                    bool newRespect = EditorGUILayout.ToggleLeft("Respect scanRoots in this inspector", _respectScanRoots);
                    if (newRespect != _respectScanRoots)
                    {
                        _respectScanRoots = newRespect;
                        RebuildFilteredCache();
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "No 'scanRoots' field found in SceneObjectService. (OK) Inspector will scan all loaded scenes.",
                        MessageType.Info);
                    _respectScanRoots = false;
                }

                _allowEditModeToggles = EditorGUILayout.ToggleLeft(
                    "Allow toggles in Edit Mode (SetActive + Undo)",
                    _allowEditModeToggles);

                EditorGUILayout.Space(6);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Rescan (Editor)"))
                        RequestRescan(immediate: true);

                    GUI.enabled = Application.isPlaying;
                    if (GUILayout.Button("Runtime: Rebuild Lookup"))
                        ((SceneObjectService)target).BuildLookup();
                    GUI.enabled = true;
                }
            }
        }

        private void DrawSummary()
        {
            _foldSummary = EditorGUILayout.Foldout(_foldSummary, "Summary", true);
            if (!_foldSummary)
                return;

            using (new EditorGUILayout.VerticalScope("HelpBox"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawTag($"Total: {_totalCount}");
                    DrawTag($"Missing ID: {_missingCount}");
                    DrawTag($"Duplicates: {_duplicateCount}");
                    DrawTag($"Filtered: {_filteredRows.Count}");
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(Application.isPlaying ? "Play Mode" : "Edit Mode", EditorStyles.miniBoldLabel);
                }

                EditorGUILayout.Space(6);

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool newOnlyMissing = GUILayout.Toggle(_onlyMissingId, "Only Missing", "Button", GUILayout.Width(120));
                    bool newOnlyDup = GUILayout.Toggle(_onlyDuplicates, "Only Duplicates", "Button", GUILayout.Width(120));
                    if (newOnlyMissing != _onlyMissingId || newOnlyDup != _onlyDuplicates)
                    {
                        _onlyMissingId = newOnlyMissing;
                        _onlyDuplicates = newOnlyDup;
                        RebuildFilteredCache();
                    }

                    GUILayout.Space(8);

                    if (GUILayout.Button("Select Missing", GUILayout.Width(120)))
                        Selection.objects = BuildSelectionArray(onlyMissing: true, onlyDuplicates: false);

                    if (GUILayout.Button("Select Duplicates", GUILayout.Width(140)))
                        Selection.objects = BuildSelectionArray(onlyMissing: false, onlyDuplicates: true);

                    GUILayout.FlexibleSpace();
                }

                EditorGUILayout.Space(6);
                DrawGroupFilter();
            }
        }

        private UnityEngine.Object[] BuildSelectionArray(bool onlyMissing, bool onlyDuplicates)
        {
            var selected = new List<UnityEngine.Object>();
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (r == null || r.go == null)
                    continue;

                if (onlyMissing && !r.isMissingId)
                    continue;

                if (onlyDuplicates && !r.isDuplicate)
                    continue;

                selected.Add(r.go);
            }

            return selected.ToArray();
        }

        private void DrawGroupFilter()
        {
            var groups = new List<string>(_groups.Count + 1) { "All" };
            foreach (var g in _groups)
                groups.Add(g);

            groups.Sort(StringComparer.OrdinalIgnoreCase);

            int idx = Mathf.Max(0, groups.FindIndex(g => string.Equals(g, _groupFilter, StringComparison.OrdinalIgnoreCase)));
            int newIdx = EditorGUILayout.Popup("Group Filter", idx, groups.ToArray());
            string newGroup = groups[Mathf.Clamp(newIdx, 0, groups.Count - 1)];
            if (!string.Equals(newGroup, _groupFilter, StringComparison.OrdinalIgnoreCase))
            {
                _groupFilter = newGroup;
                RebuildFilteredCache();
            }
        }

        private void DrawList()
        {
            _foldList = EditorGUILayout.Foldout(_foldList, "Objects", true);
            if (!_foldList)
                return;

            using (new EditorGUILayout.VerticalScope("HelpBox"))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Show Filtered"))
                        BulkSetActive(true);

                    if (GUILayout.Button("Hide Filtered"))
                        BulkSetActive(false);

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Scan"))
                        RequestRescan(immediate: true);
                }

                EditorGUILayout.Space(6);

                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(260));
                DrawVirtualizedFilteredRows();
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawVirtualizedFilteredRows()
        {
            if (_filteredRows.Count == 0)
            {
                EditorGUILayout.HelpBox("No objects match current filters/search.", MessageType.Info);
                return;
            }

            const float viewHeight = 360f;
            int firstVisible = Mathf.Max(0, Mathf.FloorToInt(_scroll.y / RowHeightEstimate) - 2);
            int visibleCount = Mathf.CeilToInt(viewHeight / RowHeightEstimate) + 6;
            int endExclusive = Mathf.Min(_filteredRows.Count, firstVisible + visibleCount);

            GUILayout.Space(firstVisible * RowHeightEstimate);
            for (int i = firstVisible; i < endExclusive; i++)
                DrawRow(_filteredRows[i]);

            GUILayout.Space((_filteredRows.Count - endExclusive) * RowHeightEstimate);
        }

        private void DrawTag(string text)
        {
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 1f, 1f, 0.10f);
            GUILayout.Label(text, _tagStyle, GUILayout.Height(18));
            GUI.backgroundColor = prev;
        }

        private void DrawRow(Row row)
        {
            if (row == null || row.go == null)
                return;

            Color prev = GUI.backgroundColor;
            if (row.isDuplicate)
                GUI.backgroundColor = TintDup;
            else if (row.isMissingId)
                GUI.backgroundColor = TintMissing;

            using (new EditorGUILayout.VerticalScope(_rowBox))
            {
                GUI.backgroundColor = prev;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(row.isDuplicate ? "DUP" : row.isMissingId ? "ID?" : "OK", _tagStyle, GUILayout.Width(46));

                    if (row.comp != null)
                    {
                        var so = new SerializedObject(row.comp);
                        var idProp = so.FindProperty("id");
                        var groupProp = so.FindProperty("group");

                        so.Update();
                        EditorGUI.BeginChangeCheck();
                        EditorGUILayout.PropertyField(idProp, GUIContent.none, GUILayout.Width(220));
                        EditorGUILayout.PropertyField(groupProp, GUIContent.none, GUILayout.Width(140));
                        if (EditorGUI.EndChangeCheck())
                        {
                            so.ApplyModifiedProperties();
                            RequestRescan(immediate: true);
                        }
                        else
                        {
                            so.ApplyModifiedPropertiesWithoutUndo();
                        }

                        if (GUILayout.Button("Name", GUILayout.Width(50)))
                        {
                            Undo.RecordObject(row.comp, "Set SceneObjectId to Name");
                            idProp.stringValue = row.go.name.Trim();
                            so.ApplyModifiedProperties();
                            RequestRescan(immediate: true);
                        }

                        if (GUILayout.Button("Path", GUILayout.Width(50)))
                        {
                            Undo.RecordObject(row.comp, "Set SceneObjectId to Path");
                            idProp.stringValue = row.path;
                            so.ApplyModifiedProperties();
                            RequestRescan(immediate: true);
                        }
                    }
                    else
                    {
                        GUILayout.Label("(Missing SceneObjectId)", EditorStyles.miniLabel);
                    }

                    GUILayout.FlexibleSpace();

                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.ObjectField(row.go, typeof(GameObject), true, GUILayout.Width(220));
                    EditorGUI.EndDisabledGroup();

                    bool canToggle = Application.isPlaying || _allowEditModeToggles;
                    EditorGUI.BeginDisabledGroup(!canToggle);
                    if (GUILayout.Button("Show", GUILayout.Width(56))) SetActive(row.go, true);
                    if (GUILayout.Button("Hide", GUILayout.Width(56))) SetActive(row.go, false);
                    if (GUILayout.Button("Tgl", GUILayout.Width(44))) SetActive(row.go, !row.go.activeSelf);
                    EditorGUI.EndDisabledGroup();

                    if (GUILayout.Button("Ping", GUILayout.Width(50)))
                        EditorGUIUtility.PingObject(row.go);

                    if (GUILayout.Button("Sel", GUILayout.Width(45)))
                        Selection.activeObject = row.go;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Path", row.path, EditorStyles.miniLabel);
                    if (GUILayout.Button("Copy", GUILayout.Width(55)))
                        EditorGUIUtility.systemCopyBuffer = row.path;
                }
            }
        }

        private void SetActive(GameObject go, bool active)
        {
            if (go == null)
                return;

            if (!Application.isPlaying)
                Undo.RecordObject(go, "SceneObjectService Toggle");

            go.SetActive(active);

            if (!Application.isPlaying)
            {
                EditorUtility.SetDirty(go);
                if (go.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(go.scene);
            }
        }

        private void BulkSetActive(bool active)
        {
            if (_filteredRows.Count == 0)
                return;

            if (Application.isPlaying)
            {
                int changedRuntime = 0;
                for (int i = 0; i < _filteredRows.Count; i++)
                {
                    var r = _filteredRows[i];
                    if (r?.go == null || r.go.activeSelf == active)
                        continue;

                    r.go.SetActive(active);
                    changedRuntime++;
                }

                if (changedRuntime > 0)
                    RequestRescan(immediate: true);

                return;
            }

            var changedObjects = new List<GameObject>(_filteredRows.Count);
            var dirtyScenes = new HashSet<Scene>();

            for (int i = 0; i < _filteredRows.Count; i++)
            {
                var r = _filteredRows[i];
                if (r?.go == null || r.go.activeSelf == active)
                    continue;

                changedObjects.Add(r.go);
                if (r.go.scene.IsValid())
                    dirtyScenes.Add(r.go.scene);
            }

            if (changedObjects.Count == 0)
                return;

            Undo.SetCurrentGroupName(active ? "Show Filtered SceneObjects" : "Hide Filtered SceneObjects");
            Undo.RecordObjects(changedObjects.ToArray(), active ? "Show Filtered SceneObjects" : "Hide Filtered SceneObjects");

            for (int i = 0; i < changedObjects.Count; i++)
            {
                changedObjects[i].SetActive(active);
                EditorUtility.SetDirty(changedObjects[i]);
            }

            foreach (var scene in dirtyScenes)
                EditorSceneManager.MarkSceneDirty(scene);

            RequestRescan(immediate: true);
        }

        private void RebuildFilteredCache()
        {
            _filteredRows.Clear();
            List<Transform> roots = null;
            bool useRoots = _respectScanRoots && _scanRootsProp != null && _scanRootsProp.arraySize > 0;
            if (useRoots)
                roots = GetScanRoots();

            string searchTrim = string.IsNullOrWhiteSpace(_search) ? null : _search.Trim();
            bool filterByGroup = !string.IsNullOrWhiteSpace(_groupFilter) && !string.Equals(_groupFilter, "All", StringComparison.OrdinalIgnoreCase);

            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (r == null || r.go == null)
                    continue;

                if (_onlyMissingId && !r.isMissingId)
                    continue;

                if (_onlyDuplicates && !r.isDuplicate)
                    continue;

                if (filterByGroup && !string.Equals(r.group, _groupFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (searchTrim != null)
                {
                    bool matches =
                        (!string.IsNullOrEmpty(r.id) && r.id.IndexOf(searchTrim, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(r.group) && r.group.IndexOf(searchTrim, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(r.path) && r.path.IndexOf(searchTrim, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (r.go.name.IndexOf(searchTrim, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!matches)
                        continue;
                }

                if (useRoots && !IsUnderAnyRoot(r.go.transform, roots))
                    continue;

                _filteredRows.Add(r);
            }
        }

        private void Rescan()
        {
            _rows.Clear();
            _groups.Clear();
            _totalCount = 0;
            _missingCount = 0;
            _duplicateCount = 0;

            var ids = CollectSceneObjectIds();
            var duplicateCounter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < ids.Count; i++)
            {
                var comp = ids[i];
                if (comp == null)
                    continue;

                var id = (comp.Id ?? string.Empty).Trim();
                var group = (comp.Group ?? string.Empty).Trim();
                var row = new Row
                {
                    comp = comp,
                    go = comp.gameObject,
                    id = id,
                    group = group,
                    path = GetPath(comp.transform),
                    isMissingId = string.IsNullOrWhiteSpace(id),
                    isDuplicate = false
                };

                if (!string.IsNullOrWhiteSpace(group))
                    _groups.Add(group);

                if (!row.isMissingId)
                {
                    duplicateCounter.TryGetValue(id, out int count);
                    duplicateCounter[id] = count + 1;
                }

                _rows.Add(row);
            }

            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (!r.isMissingId && duplicateCounter.TryGetValue(r.id, out int count) && count > 1)
                    r.isDuplicate = true;

                _totalCount++;
                if (r.isMissingId)
                    _missingCount++;
                if (r.isDuplicate)
                    _duplicateCount++;
            }

            if (_groupFilter != "All" && !_groups.Contains(_groupFilter))
                _groupFilter = "All";

            RebuildFilteredCache();
        }

        private List<SceneObjectId> CollectSceneObjectIds()
        {
            if (_scanRootsProp != null && _scanRootsProp.arraySize > 0)
            {
                var roots = GetScanRoots();
                var found = new List<SceneObjectId>(256);
                var seen = new HashSet<SceneObjectId>();

                for (int i = 0; i < roots.Count; i++)
                {
                    var t = roots[i];
                    if (t == null) continue;

                    var components = t.GetComponentsInChildren<SceneObjectId>(true);
                    for (int c = 0; c < components.Length; c++)
                    {
                        var comp = components[c];
                        if (comp != null && seen.Add(comp))
                            found.Add(comp);
                    }
                }

                return found;
            }

            var result = new List<SceneObjectId>(512);
            var unique = new HashSet<SceneObjectId>();

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;

                var roots = scene.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    var root = roots[r];
                    if (root == null) continue;

                    var components = root.GetComponentsInChildren<SceneObjectId>(true);
                    for (int c = 0; c < components.Length; c++)
                    {
                        var comp = components[c];
                        if (comp != null && unique.Add(comp))
                            result.Add(comp);
                    }
                }
            }

            return result;
        }

        private List<Transform> GetScanRoots()
        {
            var list = new List<Transform>(_scanRootsProp.arraySize);
            for (int i = 0; i < _scanRootsProp.arraySize; i++)
            {
                var el = _scanRootsProp.GetArrayElementAtIndex(i);
                if (el.objectReferenceValue is Transform tr)
                    list.Add(tr);
            }

            return list;
        }

        private static bool IsUnderAnyRoot(Transform t, List<Transform> roots)
        {
            if (roots == null || roots.Count == 0)
                return true;

            while (t != null)
            {
                for (int i = 0; i < roots.Count; i++)
                    if (t == roots[i])
                        return true;

                t = t.parent;
            }

            return false;
        }

        private static string GetPath(Transform t)
        {
            if (t == null)
                return string.Empty;

            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = $"{t.name}/{path}";
            }

            return path;
        }
    }
}
#endif
