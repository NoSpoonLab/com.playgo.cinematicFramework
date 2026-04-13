#if UNITY_EDITOR
using System;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace PlayGo.SceneObjects.Editor
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(SceneObjectId))]
    public sealed class SceneObjectIdEditor : UnityEditor.Editor
    {
        private static readonly Regex InvalidIdCharsRegex = new("[^a-zA-Z0-9_./-]", RegexOptions.Compiled);

        private SerializedProperty _idProp;
        private SerializedProperty _groupProp;
        private SerializedProperty _applyDefaultProp;
        private SerializedProperty _defaultActiveProp;

        private string _copyFeedback;
        private double _copyFeedbackUntil;
        private double _pathAmbiguityWarningUntil;

        private void OnEnable()
        {
            _idProp = serializedObject.FindProperty("id");
            _groupProp = serializedObject.FindProperty("group");
            _applyDefaultProp = serializedObject.FindProperty("applyDefaultOnStart");
            _defaultActiveProp = serializedObject.FindProperty("defaultActive");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_idProp);
            DrawIdValidationHelp();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Use Name"))
                {
                    ApplyIdToSelection(sceneObj => sceneObj != null ? sceneObj.gameObject.name.Trim() : string.Empty, "SceneObjectId: Use Name");
                }

                if (GUILayout.Button("Use Path"))
                {
                    ApplyIdToSelection(sceneObj => GetPath(sceneObj != null ? sceneObj.transform : null, includeSiblingIndex: false), "SceneObjectId: Use Path");
                    ShowDuplicateSiblingNameWarning();
                }

                if (GUILayout.Button("Use Path + Index"))
                {
                    ApplyIdToSelection(sceneObj => GetPath(sceneObj != null ? sceneObj.transform : null, includeSiblingIndex: true), "SceneObjectId: Use Path + Index");
                }

                if (GUILayout.Button("Sanitize"))
                {
                    ApplyIdToSelection(sceneObj => SanitizeId(GetIdFrom(sceneObj)), "SceneObjectId: Sanitize ID");
                }

                if (GUILayout.Button("Copy ID"))
                    CopyCurrentId();
            }

            DrawPathAmbiguityFeedback();
            DrawCopyFeedback();

            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(_groupProp);

            EditorGUILayout.Space(10);
            EditorGUILayout.PropertyField(_applyDefaultProp);
            using (new EditorGUI.DisabledScope(!_applyDefaultProp.boolValue))
                EditorGUILayout.PropertyField(_defaultActiveProp);

            if (!_idProp.hasMultipleDifferentValues && string.IsNullOrWhiteSpace(_idProp.stringValue))
                EditorGUILayout.HelpBox("ID is empty. SceneObjectService will ignore this object.", MessageType.Warning);

            serializedObject.ApplyModifiedProperties();
        }

        private void ApplyIdToSelection(Func<SceneObjectId, string> idSelector, string undoLabel)
        {
            if (idSelector == null)
                return;

            Undo.RecordObjects(targets, undoLabel);

            foreach (UnityEngine.Object obj in targets)
            {
                if (obj is not SceneObjectId sceneObj)
                    continue;

                var so = new SerializedObject(sceneObj);
                SerializedProperty idProp = so.FindProperty("id");
                if (idProp == null)
                    continue;

                idProp.stringValue = idSelector(sceneObj) ?? string.Empty;
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.RecordPrefabInstancePropertyModifications(sceneObj);
                EditorUtility.SetDirty(sceneObj);
            }

            serializedObject.Update();
        }

        private void DrawIdValidationHelp()
        {
            if (_idProp.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox("Multiple IDs selected. Validation/Sanitize applies to all selected objects.", MessageType.Info);
                return;
            }

            string id = _idProp.stringValue ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (id.Contains(' '))
                EditorGUILayout.HelpBox("ID contains spaces. Prefer '_' or '-' to avoid fragile lookups.", MessageType.Warning);

            if (InvalidIdCharsRegex.IsMatch(id))
                EditorGUILayout.HelpBox("ID contains uncommon/special characters. Allowed: a-z A-Z 0-9 _ . / -", MessageType.Warning);
        }

        private void ShowDuplicateSiblingNameWarning()
        {
            bool hasDuplicates = false;
            foreach (UnityEngine.Object obj in targets)
            {
                if (obj is not SceneObjectId sceneObj || sceneObj.transform == null || sceneObj.transform.parent == null)
                    continue;

                int sameNameCount = 0;
                string targetName = sceneObj.transform.name;
                Transform parent = sceneObj.transform.parent;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    if (child != null && child.name == targetName)
                        sameNameCount++;
                }

                if (sameNameCount > 1)
                {
                    hasDuplicates = true;
                    break;
                }
            }

            if (hasDuplicates)
            {
                _pathAmbiguityWarningUntil = EditorApplication.timeSinceStartup + 4d;
                Repaint();
            }
        }


        private void DrawPathAmbiguityFeedback()
        {
            if (EditorApplication.timeSinceStartup > _pathAmbiguityWarningUntil)
                return;

            EditorGUILayout.HelpBox("Duplicate sibling names detected. 'Use Path' can be ambiguous across reordered siblings. Prefer 'Use Path + Index' for stable IDs.", MessageType.Warning);
        }

        private void CopyCurrentId()
        {
            if (_idProp.hasMultipleDifferentValues)
            {
                _copyFeedback = "Copy aborted: multiple different IDs selected.";
                _copyFeedbackUntil = EditorApplication.timeSinceStartup + 2.5d;
                Debug.LogWarning("[SceneObjectIdEditor] Copy ID aborted: multiple different IDs selected.");
                return;
            }

            string id = _idProp.stringValue;
            if (string.IsNullOrWhiteSpace(id))
            {
                _copyFeedback = "Copy aborted: ID is empty.";
                _copyFeedbackUntil = EditorApplication.timeSinceStartup + 2.5d;
                Debug.LogWarning("[SceneObjectIdEditor] Copy ID aborted: ID is null/empty.");
                return;
            }

            EditorGUIUtility.systemCopyBuffer = id;
            _copyFeedback = "ID copied to clipboard.";
            _copyFeedbackUntil = EditorApplication.timeSinceStartup + 2.5d;
            Debug.Log($"[SceneObjectIdEditor] ID copied: {id}");
        }

        private void DrawCopyFeedback()
        {
            if (string.IsNullOrEmpty(_copyFeedback))
                return;

            if (EditorApplication.timeSinceStartup > _copyFeedbackUntil)
            {
                _copyFeedback = null;
                return;
            }

            EditorGUILayout.HelpBox(_copyFeedback, MessageType.Info);
        }

        private static string GetPath(Transform t, bool includeSiblingIndex)
        {
            if (t == null)
                return string.Empty;

            var sb = new StringBuilder();
            AppendSegment(sb, t, includeSiblingIndex);

            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, '/');
                var segmentBuilder = new StringBuilder();
                AppendSegment(segmentBuilder, t, includeSiblingIndex);
                sb.Insert(0, segmentBuilder);
            }

            return sb.ToString();
        }

        private static void AppendSegment(StringBuilder sb, Transform t, bool includeSiblingIndex)
        {
            if (t == null)
                return;

            sb.Append(t.name);
            if (includeSiblingIndex)
                sb.Append('[').Append(t.GetSiblingIndex()).Append(']');
        }

        private static string GetIdFrom(SceneObjectId sceneObj)
        {
            if (sceneObj == null)
                return string.Empty;

            var so = new SerializedObject(sceneObj);
            SerializedProperty idProp = so.FindProperty("id");
            return idProp?.stringValue ?? string.Empty;
        }

        private static string SanitizeId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return string.Empty;

            string trimmed = id.Trim().Replace(' ', '_');
            return InvalidIdCharsRegex.Replace(trimmed, "_");
        }
    }
}
#endif
