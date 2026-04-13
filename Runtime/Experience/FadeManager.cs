using System.Collections;
using UnityEngine;

[DefaultExecutionOrder(-1000)]
public class FadeManager : MonoBehaviour
{
    public static FadeManager Instance { get; private set; }

    [Header("Refs")]
    [SerializeField] private CanvasGroup fadeCanvasGroup;

    [Header("Durations")]
    [Min(0f)][SerializeField] private float fadeOutDuration = 0.35f; // a negro (alpha 1)
    [Min(0f)][SerializeField] private float fadeInDuration = 0.25f;  // a transparente (alpha 0)

    [Header("Behaviour")]
    [SerializeField] private bool useUnscaledTime = true;
    [SerializeField] private bool disableOverlayWhenFullyTransparent = true;
    [SerializeField, Range(0f, 0.05f)] private float transparentEpsilon = 0.002f;

    [Header("Debug")]
    [SerializeField] private bool verboseLogs = false;

    private Coroutine fadeRoutine;
    private bool isFading;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        if (fadeCanvasGroup == null)
            fadeCanvasGroup = GetComponentInChildren<CanvasGroup>(true);

        if (fadeCanvasGroup != null)
        {
            // Estado inicial: transparente (no bloquea si no quieres)
            fadeCanvasGroup.alpha = 0f;
            fadeCanvasGroup.blocksRaycasts = false;
            RefreshOverlayState();
        }
        else
        {
            Debug.LogError("[FadeManager] No CanvasGroup assigned/found.");
        }
    }

    public IEnumerator FadeOut() => FadeTo(1f, fadeOutDuration, "FadeOut");
    public IEnumerator FadeIn() => FadeTo(0f, fadeInDuration, "FadeIn");

    public void CancelFade(bool snapToCurrentAlpha = true)
    {
        if (fadeRoutine != null)
            StopCoroutine(fadeRoutine);

        fadeRoutine = null;
        isFading = false;

        if (fadeCanvasGroup == null) return;

        if (snapToCurrentAlpha)
            fadeCanvasGroup.alpha = Mathf.Clamp01(fadeCanvasGroup.alpha);

        RefreshOverlayState();
    }

    private IEnumerator FadeTo(float targetAlpha, float duration, string label)
    {
        if (fadeCanvasGroup == null)
            yield break;

        if (fadeRoutine != null)
            StopCoroutine(fadeRoutine);

        fadeRoutine = StartCoroutine(FadeRoutine(targetAlpha, duration, label));
        yield return fadeRoutine;
        fadeRoutine = null;
    }

    private IEnumerator FadeRoutine(float targetAlpha, float duration, string label)
    {
        isFading = true;
        RefreshOverlayState();

        float startAlpha = fadeCanvasGroup.alpha;
        float clampedTarget = Mathf.Clamp01(targetAlpha);
        duration = Mathf.Max(0f, duration);

        if (verboseLogs)
            Debug.Log($"[FadeManager] {label} START a={startAlpha:0.000} -> {clampedTarget:0.000} dur={duration:0.00} ts={Time.timeScale:0.00} unscaled={useUnscaledTime}");

        // Si duration == 0, snap directo.
        if (duration <= 0f)
        {
            fadeCanvasGroup.alpha = clampedTarget;
            fadeCanvasGroup.blocksRaycasts = fadeCanvasGroup.alpha > (0f + transparentEpsilon);
            isFading = false;
            RefreshOverlayState();

            if (verboseLogs)
                Debug.Log($"[FadeManager] {label} END (snap) a={fadeCanvasGroup.alpha:0.000}");

            yield break;
        }

        // Fallback robusto: aunque deltaTime sea 0 (timeScale=0 o editor raro), avanzamos con realtime.
        float startRealtime = Time.realtimeSinceStartup;
        float startRef = useUnscaledTime ? Time.unscaledTime : Time.time;

        while (true)
        {
            float nowRef = useUnscaledTime ? Time.unscaledTime : Time.time;
            float elapsed = nowRef - startRef;

            if (elapsed <= 0f)
                elapsed = Time.realtimeSinceStartup - startRealtime;

            float t = Mathf.Clamp01(elapsed / duration);
            fadeCanvasGroup.alpha = Mathf.Lerp(startAlpha, clampedTarget, t);
            fadeCanvasGroup.blocksRaycasts = fadeCanvasGroup.alpha > (0f + transparentEpsilon);

            if (t >= 1f)
                break;

            yield return null;
        }

        fadeCanvasGroup.alpha = clampedTarget;
        fadeCanvasGroup.blocksRaycasts = fadeCanvasGroup.alpha > (0f + transparentEpsilon);

        isFading = false;
        RefreshOverlayState();

        if (verboseLogs)
            Debug.Log($"[FadeManager] {label} END a={fadeCanvasGroup.alpha:0.000}");
    }

    private void RefreshOverlayState()
    {
        if (!disableOverlayWhenFullyTransparent || fadeCanvasGroup == null)
            return;

        bool shouldBeActive = (fadeCanvasGroup.alpha > (0f + transparentEpsilon)) || isFading;

        if (fadeCanvasGroup.gameObject.activeSelf != shouldBeActive)
            fadeCanvasGroup.gameObject.SetActive(shouldBeActive);
    }
}