using UnityEngine;

/// <summary>
/// Marca un root de contenido que pertenece a una "escena lógica" (sceneId del guion).
/// El StoryboardFlowController lo activa/desactiva automáticamente.
/// </summary>
public class StoryboardContentRoot : MonoBehaviour
{
    [Tooltip("ID lógico de la escena del guion. Ej: S1_HORNO")]
    public string sceneId;

    [Tooltip("Si se asigna, se activará/desactivará este objeto en lugar del GameObject donde está el componente.")]
    public GameObject rootOverride;

    [Tooltip("Si está activo, este root nunca se desactiva (útil para cosas compartidas).")]
    public bool keepAlwaysActive = false;

    public void SetActive(bool active)
    {
        if (keepAlwaysActive)
            return;

        var target = rootOverride != null ? rootOverride : gameObject;
        if (target != null && target.activeSelf != active)
            target.SetActive(active);
    }
}