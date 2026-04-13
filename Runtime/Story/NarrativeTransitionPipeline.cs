using UnityEngine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

/// <summary>
/// Orquestador global de fases para transiciones narrativas.
///
/// Diseño:
/// - StoryManager abre/cierra la transición y avanza fases explícitas.
/// - Sistemas pesados pueden consultar IsActive/CurrentPhase para decidir cuándo ejecutar.
/// - Evita delays mágicos: la secuencia y los cortes por frame están declarados.
/// </summary>
public static class NarrativeTransitionPipeline
{
    public enum Phase
    {
        None = 0,
        ResolveEntry = 10,          // Fase A
        VisualRefresh = 20,         // Fase B
        NarrativeEventDispatch = 30,// Fase C
        NonCriticalActions = 40,    // Fase D
        HeavyObjectChanges = 50,    // Fase E
        TeleportAndPlacement = 60   // Fase F
    }

    public static bool IsActive => _isActive;
    public static Phase CurrentPhase => _currentPhase;
    public static int TransitionId => _transitionId;
    public static int PhaseFrame => _phaseFrame;

    private static bool _isActive;
    private static Phase _currentPhase = Phase.None;
    private static int _transitionId;
    private static int _phaseFrame = -1;
    private static string _source = string.Empty;

    private static bool _verboseLogs;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private static readonly ProfilerMarker _pmBegin = new ProfilerMarker("NarrativePipeline.Begin");
    private static readonly ProfilerMarker _pmEnd = new ProfilerMarker("NarrativePipeline.End");
    private static readonly ProfilerMarker _pmPhase = new ProfilerMarker("NarrativePipeline.EnterPhase");
#endif

    public static void Configure(bool verboseLogs)
    {
        _verboseLogs = verboseLogs;
    }

    public static int Begin(string source, string detail = null)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmBegin.Auto())
        {
            return BeginInternal(source, detail);
        }
#else
        return BeginInternal(source, detail);
#endif
    }

    public static void EnterPhase(Phase phase, string detail = null)
    {
        if (!_isActive)
            return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmPhase.Auto())
        {
            EnterPhaseInternal(phase, detail);
        }
#else
        EnterPhaseInternal(phase, detail);
#endif
    }

    public static void End(string detail = null)
    {
        if (!_isActive)
            return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmEnd.Auto())
        {
            EndInternal(detail);
        }
#else
        EndInternal(detail);
#endif
    }

    public static bool IsPhaseReached(Phase phase)
    {
        return _isActive && _currentPhase >= phase;
    }

    private static int BeginInternal(string source, string detail)
    {
        _transitionId++;
        _isActive = true;
        _source = source ?? string.Empty;
        _currentPhase = Phase.None;
        _phaseFrame = Time.frameCount;
        Log($"BEGIN #{_transitionId} src={_source} {detail}");
        return _transitionId;
    }

    private static void EnterPhaseInternal(Phase phase, string detail)
    {
        if (phase <= _currentPhase)
            return;

        _currentPhase = phase;
        _phaseFrame = Time.frameCount;
        Log($"PHASE #{_transitionId} phase={phase} frame={_phaseFrame} {detail}");
    }

    private static void EndInternal(string detail)
    {
        Log($"END #{_transitionId} phase={_currentPhase} frame={Time.frameCount} {detail}");
        _isActive = false;
        _currentPhase = Phase.None;
        _phaseFrame = -1;
        _source = string.Empty;
    }

    private static void Log(string msg)
    {
        if (!_verboseLogs)
            return;

        Debug.Log("[NarrativePipeline] " + msg);
    }
}
