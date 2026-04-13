using System;
using UnityEngine;

namespace PlayGo.SceneObjects
{
    /// <summary>
    /// Identificador colocable en cualquier GameObject para registrarlo automáticamente
    /// en SceneObjectService (sin listas manuales).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SceneObjectId : MonoBehaviour
    {
        [Header("ID")]
        [SerializeField] private string id;

        [Header("Grouping (opcional)")]
        [SerializeField] private string group;

        [Header("Default State (opcional)")]
        [Tooltip("Si está activo, al iniciar SceneObjectService aplicará target.SetActive(defaultActive).")]
        public bool applyDefaultOnStart = false;

        public bool defaultActive = true;

        public string Id => id;
        public string Group => group;

        private void OnValidate()
        {
            if (id != null) id = id.Trim();
            if (group != null) group = group.Trim();
        }

        [ContextMenu("Set ID = GameObject name")]
        private void SetIdToName()
        {
            id = gameObject.name.Trim();
        }

        [ContextMenu("Set ID = Full Hierarchy Path")]
        private void SetIdToPath()
        {
            id = GetPath(transform);
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