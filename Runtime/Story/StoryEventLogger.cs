using UnityEngine;

/// <summary>
/// Logger de eventos de historia.
///
/// v2 PERF:
/// - Por defecto NO loguea en device aunque debugLogs esté activado en inspector.
/// - Para habilitar logs en Quest/Android, activa allowDeviceLogs.
/// </summary>
public class StoryEventLogger : MonoBehaviour
{
    [Header("Debug")]
    [Tooltip("Activa logs (en Editor siempre). En device requiere allowDeviceLogs.")]
    public bool debugLogs;

    [Tooltip("Permite logs en device (Quest/Android). OJO: Debug.Log puede meter spikes.")]
    public bool allowDeviceLogs = false;

    private void OnEnable()
    {
        StoryEventBus.OnStoryEvent += Handle;
    }

    private void OnDisable()
    {
        StoryEventBus.OnStoryEvent -= Handle;
    }

    private void Handle(string eventName, StoryEntry entry)
    {
        if (!debugLogs)
            return;

        // En device, a menos que lo permitas explícitamente, no logueamos.
        if (!Application.isEditor && !allowDeviceLogs)
            return;

        // Evita string interpolation si no es necesario.
        string id = (entry != null && !string.IsNullOrEmpty(entry.id)) ? entry.id : "null";
        Debug.Log("[StoryEventLogger] " + eventName + " (entry: " + id + ")");
    }
}
