using System.Collections;
using UnityEngine;

/// <summary>
/// Caché de yield instructions para evitar allocs en coroutines.
/// Útil en VR/Quest para minimizar GC spikes.
/// </summary>
public static class StoryYieldCache
{
    /// <summary>Yield a final de frame (instancia reutilizada, sin alloc).</summary>
    public static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();

    /// <summary>Yield un frame.</summary>
    public static readonly object NextFrame = null; // equivalente a 'yield return null'

    /// <summary>
    /// Espera N frames sin alloc adicional.
    /// </summary>
    public static IEnumerator WaitFrames(int frames)
    {
        for (int i = 0; i < frames; i++)
            yield return null;
    }
}
