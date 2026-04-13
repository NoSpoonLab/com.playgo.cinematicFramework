using System.Text;
using UnityEngine;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

/// <summary>
/// Traza ligera y transversal del pipeline narrativo.
///
/// Objetivo de rendimiento:
/// - Hacer visible la cascada exacta de trabajo del frame crítico (UI -> bus -> listeners -> teleport -> activaciones).
/// - Correlacionar side effects de sistemas distintos bajo un mismo transitionId sin reescribir el flujo.
/// - Mantener coste casi nulo cuando está desactivada.
/// </summary>
public static class StoryTransitionTrace
{
    public static bool Enabled => _enabled;
    public static int CurrentTransitionId => _transitionId;
    public static int CurrentTransitionStartFrame => _transitionStartFrame;

    private static bool _enabled;
    private static bool _logsEnabled;

    private static int _transitionId;
    private static int _transitionStartFrame = -1;
    private static string _transitionSource = string.Empty;
    private static string _currentEntryId = string.Empty;

    private static readonly StringBuilder _logBuilder = new StringBuilder(256);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private static readonly ProfilerMarker _pmBegin = new ProfilerMarker("StoryTransitionTrace.Begin");
    private static readonly ProfilerMarker _pmMark = new ProfilerMarker("StoryTransitionTrace.Mark");
    private static readonly ProfilerMarker _pmEnd = new ProfilerMarker("StoryTransitionTrace.End");
#endif

    public static void Configure(bool enabled, bool logsEnabled)
    {
        _enabled = enabled;
        _logsEnabled = logsEnabled;
    }

    public static int Begin(string source, string entryId = null, string detail = null)
    {
        if (!_enabled)
            return 0;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmBegin.Auto())
        {
            BeginInternal(source, entryId, detail);
        }
#else
        BeginInternal(source, entryId, detail);
#endif
        return _transitionId;
    }

    public static void Mark(string stage, string detail = null)
    {
        if (!_enabled)
            return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmMark.Auto())
        {
            MarkInternal(stage, detail);
        }
#else
        MarkInternal(stage, detail);
#endif
    }

    public static void MarkEvent(string stage, string eventName, string detail = null)
    {
        if (!_enabled)
            return;

        if (string.IsNullOrEmpty(eventName))
        {
            Mark(stage, detail);
            return;
        }

        if (string.IsNullOrEmpty(detail))
            Mark(stage, "event=" + eventName);
        else
            Mark(stage, "event=" + eventName + " " + detail);
    }

    public static void SetCurrentEntry(string entryId)
    {
        if (!_enabled)
            return;

        _currentEntryId = entryId ?? string.Empty;
    }

    public static void End(string stage, string detail = null)
    {
        if (!_enabled)
            return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmEnd.Auto())
        {
            MarkInternal(stage, detail, isEnd: true);
        }
#else
        MarkInternal(stage, detail, isEnd: true);
#endif
    }

    private static void BeginInternal(string source, string entryId, string detail)
    {
        _transitionId++;
        _transitionStartFrame = Time.frameCount;
        _transitionSource = source ?? string.Empty;
        _currentEntryId = entryId ?? string.Empty;

        WriteLog("BEGIN", source, detail);
    }

    private static void MarkInternal(string stage, string detail, bool isEnd = false)
    {
        if (_transitionId <= 0)
        {
            // Si llega una marca sin Begin explícito, abrimos transición implícita para no perder trazabilidad.
            BeginInternal("Implicit", _currentEntryId, "implicit=true");
        }

        WriteLog(isEnd ? "END" : "MARK", stage, detail);
    }

    private static void WriteLog(string phase, string stage, string detail)
    {
        if (!_logsEnabled)
            return;

        _logBuilder.Clear();
        _logBuilder.Append("[TransitionTrace] #").Append(_transitionId);
        _logBuilder.Append(" f=").Append(Time.frameCount);
        if (_transitionStartFrame >= 0)
            _logBuilder.Append(" df=").Append(Time.frameCount - _transitionStartFrame);
        _logBuilder.Append(" ").Append(phase);
        if (!string.IsNullOrEmpty(stage))
            _logBuilder.Append(" ").Append(stage);
        if (!string.IsNullOrEmpty(_transitionSource))
            _logBuilder.Append(" src=").Append(_transitionSource);
        if (!string.IsNullOrEmpty(_currentEntryId))
            _logBuilder.Append(" entry=").Append(_currentEntryId);
        if (!string.IsNullOrEmpty(detail))
            _logBuilder.Append(" ").Append(detail);

        Debug.Log(_logBuilder.ToString());
    }
}
