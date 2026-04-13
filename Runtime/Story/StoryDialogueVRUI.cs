using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Oculus.Interaction;
using Random = UnityEngine.Random;
using PlayGo.Project.SanIldefonso.VisualActions;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

public class StoryDialogueVRUI : MonoBehaviour
{
    [Header("Experience Text Preference")]
    [Tooltip("Si show_experience_text = false, mantiene visible el texto crítico de preguntas (prompt y opciones).")]
    public bool keepQuestionPromptVisibleWhenTextHidden = true;

    [Tooltip("Si show_experience_text = false, mantiene visible el speaker en preguntas para contexto.")]
    public bool keepQuestionSpeakerVisibleWhenTextHidden = true;

    [Tooltip("Si show_experience_text = false, oculta el nombre del speaker para reducir texto en pantalla.")]
    public bool hideSpeakerWhenTextHidden = true;

    [Tooltip("Si show_experience_text = false, oculta el ID técnico de la entrada.")]
    public bool hideIdWhenTextHidden = true;

    [Header("Panel Optimization")]
    [Tooltip("Si show_experience_text = false, en entradas LINE oculta todo el panel y desactiva el follow. En QUESTION vuelve a mostrarse.")]
    public bool hideWholePanelForLineWhenTextHidden = true;

    [Tooltip("Componente que sigue la cabeza. Se desactiva cuando el panel completo está oculto.")]
    public VRUIFollowHead uiFollowHead;

    [Tooltip("Si está activo, usa CanvasGroup para ocultar/mostrar paneles sin SetActive repetido.")]
    [SerializeField] private bool useCanvasGroupVisibility = false;

    [Tooltip("En runtime, prioriza CanvasGroup sobre SetActive cuando hay CanvasGroup disponible.")]
    [SerializeField] private bool preferCanvasGroupVisibilityInRuntime = true;

    [Tooltip("CanvasGroup opcional del panel completo. Si no se asigna, se intenta obtener en el root.")]
    [SerializeField] private CanvasGroup wholePanelCanvasGroup;

    [Tooltip("CanvasGroup opcional del grupo de pregunta.")]
    [SerializeField] private CanvasGroup questionGroupCanvasGroup;

    [Tooltip("CanvasGroup opcional del botón Next.")]
    [SerializeField] private CanvasGroup nextButtonCanvasGroup;

    [Header("Aggressive UI Refresh Pipeline")]
    [Tooltip("Coalescea cambios de entry y aplica un único commit visual (reduce rebuilds duplicados en el frame crítico).")]
    [SerializeField] private bool useStagedUiRefreshPipeline = true;

    [Tooltip("Si está activo, descarta commits visuales encolados que ya no corresponden al entry actual.")]
    [SerializeField] private bool discardStaleQueuedRefresh = true;

    [Tooltip("Frames de espera antes del commit visual. 1 separa Next/eventos del rebuild de Canvas/TMP.")]
    [Range(0, 3)]
    [SerializeField] private int stagedUiRefreshDelayFrames = 1;

    [Tooltip("Si está activo, múltiples RefreshUI en el mismo tramo se coalescean al último entry.")]
    [SerializeField] private bool coalesceRefreshRequests = true;

    [Tooltip("Si está activo, el evento semántico QuestionShown se difiere un frame tras reconstruir opciones.")]
    [SerializeField] private bool deferQuestionShownEventOneFrame = true;

    [Header("Refs")]
    public StoryManager storyManager;

    [Header("Startup")]
    [Tooltip("Si está activo, la UI se oculta hasta que empiece la historia.")]
    public bool hideChildrenUntilStoryStarts = true;

    [Header("UI Principal")]
    public TextMeshPro idText;
    public TextMeshPro speakerText;
    public TextMeshPro mainText;

    [Header("UI Pregunta")]
    public GameObject questionGroup;
    public TextMeshPro optionAText;
    public TextMeshPro optionBText;
    public PokeInteractable optionAPoke;
    public PokeInteractable optionBPoke;

    [Header("Opciones - Visual (opcional)")]
    public VRPokeOptionVisual optionAVisual;
    public VRPokeOptionVisual optionBVisual;

    [Header("Botón Next")]
    public GameObject nextButtonRoot;
    public PokeInteractable nextButtonPoke;

    [Header("Salto por ID (opcional)")]
    public TextMeshPro jumpIdText;
    public PokeInteractable jumpIdPoke;

    [Header("Typewriter")]
    [Tooltip("Master switch del typewriter (adicional a useTypewriterEffect).")]
    [SerializeField] private bool enableTypewriter = true;
    public bool useTypewriterEffect = true;
    [Tooltip("Si está activo, el texto principal se muestra de golpe (sin animación por carácter).")]
    public bool forceInstantMainText = false;
    [Range(0.001f, 0.1f)]
    public float typewriterCharInterval = 0.03f;

    [Tooltip("Si está activo, usa tiempo NO escalado (recomendado en XR/menús donde se toca timeScale).")]
    public bool typewriterUseUnscaledTime = true;

    [Tooltip("Evita concentrar el show de botones/opciones en el mismo frame del final del typewriter.")]
    public bool deferAfterTypingActionOneFrame = true;

    [Header("Typewriter Performance")]
    [Tooltip("Si está activo, actualiza maxVisibleCharacters en ticks fijos en vez de cada frame.")]
    public bool useBatchedTypewriterUpdates = true;

    [Tooltip("Número máximo de actualizaciones visuales del typewriter por segundo.")]
    [Range(10, 45)]
    public int typewriterMaxUpdatesPerSecond = 18;

    [Tooltip("Límite duro de pasos visuales por frase. Menos pasos = menos trabajo TMP.")]
    [Range(8, 80)]
    public int typewriterMaxVisualStepsPerEntry = 24;

    [Tooltip("Mínimo de caracteres a revelar por actualización.")]
    [Range(1, 6)]
    public int typewriterMinCharsPerUpdate = 2;

    [Tooltip("Acelera textos largos añadiendo caracteres extra por update.")]
    [Range(0, 8)]
    public int typewriterExtraCharsForLongText = 3;

    [Tooltip("A partir de este tamaño se considera texto largo para acelerar reveal.")]
    [Range(120, 2000)]
    public int typewriterLongTextThreshold = 220;

    [Header("Typewriter Adaptive (FPS)")]
    [Tooltip("Ajusta dinamicamente el typewriter segun FPS: menos updates y mas caracteres por tick cuando cae el rendimiento.")]
    public bool adaptiveTypewriterByFps = true;

    [Tooltip("FPS objetivo para calcular la adaptacion.")]
    [Range(45, 120)]
    public int adaptiveTargetFps = 72;

    [Tooltip("Suavizado del estimador de FPS (EMA). Mayor valor responde mas rapido, menor valor es mas estable.")]
    [Range(0.02f, 0.5f)]
    public float adaptiveFpsSmoothing = 0.12f;

    [Tooltip("La adaptacion empieza cuando el FPS cae por debajo de este ratio del objetivo.")]
    [Range(0.6f, 1f)]
    public float adaptiveStartAtTargetRatio = 0.92f;

    [Tooltip("Caracteres extra maximos por update cuando hay caida de FPS.")]
    [Range(0, 12)]
    public int adaptiveMaxExtraCharsPerUpdate = 6;

    [Tooltip("Factor maximo para espaciar updates visuales cuando cae FPS.")]
    [Range(1f, 3f)]
    public float adaptiveMaxTickScale = 1.8f;

    [Tooltip("Umbral de protección dura. Debajo de este ratio se acelera mucho el reveal para proteger FPS.")]
    [Range(0.5f, 0.95f)]
    public float adaptiveHardProtectRatio = 0.8f;

    [Tooltip("Caracteres extra por update en protección dura.")]
    [Range(0, 20)]
    public int adaptiveHardProtectExtraChars = 8;

    [Tooltip("Escalado adicional de tick en protección dura.")]
    [Range(1f, 4f)]
    public float adaptiveHardProtectTickScale = 2.4f;

    [Header("Typewriter Extreme Optimization")]
    [Tooltip("Para texto plano sin rich-tags ni pares surrogate, evita ForceMeshUpdate y usa string.Length como total visible.")]
    public bool ultraFastPlainTextPath = true;

[Header("TMP Glyph Warmup (Quest)")]
[Tooltip("Si está activo, precalienta glyphs de la fuente (y fallbacks) a partir del texto actual antes de ForceMeshUpdate. Evita spikes enormes tipo 'Save Glyph Vertex Data'.")]
public bool prewarmGlyphsFromCurrentText = true;
    [Tooltip("Master switch de prewarm de glyphs (adicional a prewarmGlyphsFromCurrentText).")]
    [SerializeField] private bool enableGlyphPrewarm = true;

    public enum ForceMeshUpdateMode
    {
        Auto = 0,
        Always = 1,
        Never = 2
    }

    [Tooltip("Controla cuándo llamar ForceMeshUpdate para calcular characterCount.")]
    [SerializeField] private ForceMeshUpdateMode forceMeshUpdateMode = ForceMeshUpdateMode.Auto;

[Tooltip("Incluye fuentes fallback (fallbackFontAssetTable). Recomendado si usas varios idiomas o símbolos.")]
public bool prewarmIncludeFallbackFonts = true;

[Tooltip("Si está activo, también precalienta los textos de las opciones (en entradas tipo QUESTION) antes de mostrarlas.")]
public bool prewarmAlsoOptionTexts = true;

[Tooltip("Tamaño de chunk de caracteres para TryAddCharacters. Cuanto menor, más repartido por frames.")]
[Range(16, 256)]
public int prewarmChunkSize = 64;

[Tooltip("Frames a esperar entre chunks durante el precalentado (para repartir coste). 1 suele ser suficiente.")]
[Range(0, 3)]
public int prewarmYieldFramesBetweenChunks = 1;

    [Header("Voice Gate")]
    public bool gateProgressWithVoice = true;
    public VoiceService voiceService;

    [Header("Typewriter SFX (Loop)")]
    [Tooltip("Activa/desactiva el SFX de tecleo en loop. Si está desactivado, no se inicia audio ni corutinas de fade.")]
    [SerializeField] private bool enableTypewriterLoopSfx = true;
    [SerializeField] private AudioSource typewriterLoopSource;
    [SerializeField] private AudioClip typewriterLoopClip;
    [SerializeField, Range(0f, 1f)] private float typewriterLoopVolume = 0.35f;

    [SerializeField, Min(0f)] private float typewriterLoopFadeIn = 0.03f;
    [SerializeField, Min(0f)] private float typewriterLoopFadeOut = 0.06f;

    [Tooltip("Para que no empiece siempre en el mismo punto del audio.")]
    [SerializeField] private bool randomizeLoopStartTime = true;

    private Coroutine _typewriterLoopRoutine;

    // Estado
    private bool _storyStarted = false;
    private bool _awaitingAnswer = false;
    private bool _hasPendingRefreshRequest = false;
    private int _pendingRefreshVersion = 0;
    private StoryEntry _pendingRefreshEntry;
    private Coroutine _uiRefreshPipelineRoutine;

    // Bloqueo de respuesta
    private bool _answerLocked = false;
    private Enums.StoryAnswerOption _pendingAnswerOption = Enums.StoryAnswerOption.A;
    private string _pendingAnswerVoiceId = string.Empty;
    private bool _pendingAnswerWasLeftButton = false;

    private GameObject[] _children;

    private enum AfterTypingAction { None, ShowNext, ShowQuestionOptions }
    private enum AfterFeedbackAction { None, AdvanceForward, GoToPreviousEntry }

    private AfterTypingAction _afterTypingAction = AfterTypingAction.None;
    private AfterFeedbackAction _afterFeedbackAction = AfterFeedbackAction.None;

    private Coroutine _mainTypewriterCoroutine;
    private bool _isTypingMain = false;
    private string _currentFullText = "";

    // Cache para evitar allocations repetidas
    private static readonly WaitForEndOfFrame _wfeof = StoryYieldCache.EndOfFrame;

// Reutilizados para evitar allocs recurrentes durante el precalentado
private static readonly HashSet<char> _glyphCharSet = new HashSet<char>(512);
private static readonly StringBuilder _glyphChunkSb = new StringBuilder(256);
private static readonly List<TMP_FontAsset> _glyphFonts = new List<TMP_FontAsset>(8);
private static readonly Dictionary<TMP_FontAsset, HashSet<char>> _prewarmedGlyphsByFont = new Dictionary<TMP_FontAsset, HashSet<char>>(8);

    private Coroutine _optionATypewriterCoroutine;
    private Coroutine _optionBTypewriterCoroutine;

    private string _pendingOptionAText;
    private string _pendingOptionBText;

    private Enums.StoryAnswerOption _leftAnswerOption = Enums.StoryAnswerOption.A;
    private Enums.StoryAnswerOption _rightAnswerOption = Enums.StoryAnswerOption.B;

    private AfterTypingAction _deferredAfterTypingAction = AfterTypingAction.None;
    private float _typewriterFpsEma = 72f;

    private bool _cachedShowExperienceText = true;
    private bool _isMainTextSuppressedByPreference = false;
    private string _currentEntryMainLocalizedText = string.Empty;
    private string _expectedMainVoiceId = string.Empty;
    private bool _currentMainTextIsCritical = false;
    private bool _autoAdvanceLineWhenPanelHidden = false;
    private bool _autoAdvanceLineByConfigDelay = false;
    private bool _autoAdvanceFeedbackWhenPanelHidden = false;
    private bool _autoAdvanceFeedbackByConfigDelay = false;
    private bool _explicitAutoAdvanceScheduled = false;
    private Coroutine _hiddenPanelAutoAdvanceRoutine;
    private Coroutine _lineAutoAdvanceRoutine;
    private Coroutine _debugRevealCorrectOptionRoutine;
    private Coroutine _deferredQuestionShownRoutine;
    private bool _lastWholePanelHidden;
    private bool _hasWholePanelHiddenState;
    private bool _lastQuestionGroupVisible;
    private bool _hasQuestionGroupVisibleState;
    private bool _lastNextButtonVisible;
    private bool _hasNextButtonVisibleState;
    private string _lastIdRendered = null;
    private string _lastSpeakerRendered = null;
    private string _lastMainRendered = null;
    private string _lastOptionARendered = null;
    private string _lastOptionBRendered = null;
    private string _lastQuestionSwapEntryId = null;
    private bool _lastQuestionSwapValue = false;
    private string _lastQuestionShownEventEntryId = null;

    [Header("Diagnostics")]
    [Tooltip("Logs opcionales de picos de UI/TMP en cambios de entry (apagado por defecto).")]
    public bool logProfilingSpikes = false;
    [Min(0f)] public float profilingSpikeLogMs = 2.5f;
    [Tooltip("Logs UI de rendimiento más verbosos para aislar trabajo redundante.")]
    [SerializeField] private bool verboseUiPerformanceLogs = false;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    // PERF NOTE:
    // Puntos calientes: RefreshUI, typewriter/TMP y toggles masivos de panel en transiciones.
    private static readonly ProfilerMarker _pmNextPressed = new ProfilerMarker("StoryDialogueVRUI.OnNextPressed");
    private static readonly ProfilerMarker _pmRefreshEnqueue = new ProfilerMarker("StoryDialogueVRUI.RefreshQueue.Enqueue");
    private static readonly ProfilerMarker _pmRefreshCommit = new ProfilerMarker("StoryDialogueVRUI.RefreshQueue.Commit");
    private static readonly ProfilerMarker _pmPlayMainTypewriter = new ProfilerMarker("StoryDialogueVRUI.PlayMainTypewriter");
    private static readonly ProfilerMarker _pmUpdateTexts = new ProfilerMarker("StoryDialogueVRUI.UpdateTexts");
    private static readonly ProfilerMarker _pmRebuildOptions = new ProfilerMarker("StoryDialogueVRUI.RebuildOptions");
    private static readonly ProfilerMarker _pmQuestionShownEvent = new ProfilerMarker("StoryDialogueVRUI.QuestionShownEvent");
    private static readonly ProfilerMarker _pmForceMeshUpdate = new ProfilerMarker("StoryDialogueVRUI.ForceMeshUpdate");
    private static readonly ProfilerMarker _pmApplyPanelVisibility = new ProfilerMarker("StoryDialogueVRUI.ApplyPanelVisibilityMode");
#endif

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    private void LogSpikeIfNeeded(string scope, float startedAtRealtime)
    {
        if (!logProfilingSpikes) return;
        float elapsedMs = (Time.realtimeSinceStartup - startedAtRealtime) * 1000f;
        if (elapsedMs < profilingSpikeLogMs) return;
        Debug.Log($"[StoryDialogueVRUI][Spike] {scope} took {elapsedMs:0.00} ms");
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    private void LogUiPerfVerbose(string message)
    {
        if (!verboseUiPerformanceLogs) return;
        Debug.Log("[StoryDialogueVRUI][Perf] " + message, this);
    }

    // ---------------------------------------------------------------------
    // UNITY
    // ---------------------------------------------------------------------

    private void Awake()
    {
        _cachedShowExperienceText = IsExperienceTextEnabled();
        if (uiFollowHead == null)
            uiFollowHead = GetComponent<VRUIFollowHead>();

        if (ShouldUseCanvasGroupVisibilityNow())
        {
            if (wholePanelCanvasGroup == null)
                wholePanelCanvasGroup = GetComponent<CanvasGroup>();

            if (questionGroupCanvasGroup == null && questionGroup != null)
                questionGroupCanvasGroup = questionGroup.GetComponent<CanvasGroup>();

            if (nextButtonCanvasGroup == null && nextButtonRoot != null)
                nextButtonCanvasGroup = nextButtonRoot.GetComponent<CanvasGroup>();
        }

        // Fallback bajo demanda: solo buscar AudioSource si el SFX está habilitado.
        if (enableTypewriterLoopSfx && typewriterLoopSource == null)
            typewriterLoopSource = GetComponent<AudioSource>();

        int count = transform.childCount;
        _children = new GameObject[count];

        for (int i = 0; i < count; i++)
        {
            _children[i] = transform.GetChild(i).gameObject;
            _children[i].SetActive(false);
        }

        // Siempre oculto al pulsar Play; se activa solo cuando arranca la historia.
        if (uiFollowHead != null)
            uiFollowHead.enabled = false;
    }

    private void OnEnable()
    {
        if (storyManager == null) return;

        storyManager.OnStoryStarted += OnStoryStarted;
        storyManager.OnAnswerEvaluated += OnAnswerEvaluated;

        if (voiceService == null) voiceService = VoiceService.Instance;
        if (voiceService != null)
        {
            voiceService.OnBlockingChanged += OnVoiceBlockingChanged;
            voiceService.OnVoiceEnded += OnVoiceEnded;
        }
    }

    private void OnDisable()
    {
        CancelDebugRevealCorrectOptionRoutine();
        CancelDeferredQuestionShownEvent();
        CancelPendingVisualRefresh();
        CancelLineAutoAdvanceRoutine();

        if (_hiddenPanelAutoAdvanceRoutine != null)
        {
            StopCoroutine(_hiddenPanelAutoAdvanceRoutine);
            _hiddenPanelAutoAdvanceRoutine = null;
        }
        _explicitAutoAdvanceScheduled = false;
        _autoAdvanceLineByConfigDelay = false;
        _autoAdvanceFeedbackByConfigDelay = false;
        StopTypewriterLoop(immediate: true);

        if (storyManager != null)
        {
            storyManager.OnStoryStarted -= OnStoryStarted;
            storyManager.OnEntryChanged -= RefreshUI;
            storyManager.OnAnswerEvaluated -= OnAnswerEvaluated;
        }

        if (voiceService != null)
        {
            voiceService.OnBlockingChanged -= OnVoiceBlockingChanged;
            voiceService.OnVoiceEnded -= OnVoiceEnded;
        }
    }

    private void Update()
    {
        // Permite que la UI reaccione si la preferencia cambia en runtime.
        bool showExperienceText = IsExperienceTextEnabled();
        if (_cachedShowExperienceText == showExperienceText)
            return;

        _cachedShowExperienceText = showExperienceText;
        RequestVisualRefresh(storyManager != null ? storyManager.GetCurrentEntry() : null, "PreferenceChanged");
    }

    // ---------------------------------------------------------------------
    // STORY FLOW
    // ---------------------------------------------------------------------

    private void OnStoryStarted()
    {
        if (_storyStarted)
            return;

        _storyStarted = true;

        if (_children != null)
        {
            foreach (var go in _children)
            {
                if (go != null)
                    go.SetActive(true);
            }
        }

        storyManager.OnEntryChanged += RefreshUI;
        RefreshUI();
    }

    // ---------------------------------------------------------------------
    // BOTONES
    // ---------------------------------------------------------------------

    public void OnNextButton()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using (_pmNextPressed.Auto())
        {
            OnNextButtonInternal();
        }
#else
        OnNextButtonInternal();
#endif
    }

    private void OnNextButtonInternal()
    {
        if (storyManager == null)
            return;

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryDialogueVRUI.OnNextPressed", $"typing={_isTypingMain} awaitingAnswer={_awaitingAnswer} answerLocked={_answerLocked}");

        // Si estamos en proceso de respuesta (lock), no permitir avanzar manualmente.
        if (_answerLocked) return;

        if (_isTypingMain)
        {
            CompleteMainTypewriterInstant();
            return;
        }

        if (_awaitingAnswer)
            return;

        if (_afterFeedbackAction == AfterFeedbackAction.GoToPreviousEntry)
        {
            _afterFeedbackAction = AfterFeedbackAction.None;
            storyManager.TryJumpToPreviousEntry();
            return;
        }

        storyManager.NextEntry();
    }

    public void OnAnswerA()
    {
        HandleAnswerPressed(isLeftButton: true);
    }

    public void OnAnswerB()
    {
        HandleAnswerPressed(isLeftButton: false);
    }

    private void HandleAnswerPressed(bool isLeftButton)
    {
        if (storyManager == null)
            return;

        CancelDebugRevealCorrectOptionRoutine();

        if (!_awaitingAnswer) return;
        if (_answerLocked) return;

        var entry = storyManager.GetCurrentEntry();
        if (entry == null || entry.type != Enums.StoryEntryType.QUESTION)
            return;

        _answerLocked = true;
        _pendingAnswerWasLeftButton = isLeftButton;

        // Determinar qué opción lógica se ha elegido (por el swap)
        _pendingAnswerOption = isLeftButton ? _leftAnswerOption : _rightAnswerOption;

        // Evento semántico de selección lógica (A/B), independiente del swap visual.
        RaiseQuestionSemanticEvent(StoryQuestionEventNames.ForOptionSelected(entry, _pendingAnswerOption), entry);

        // Bloquear seleccionar la otra respuesta
        SetAnswerInteractable(
            leftEnabled: isLeftButton,
            rightEnabled: !isLeftButton
        );

        // Visual persistente
        if (isLeftButton)
        {
            optionAVisual?.SetSelectedLocked();
            optionBVisual?.SetDisabledLocked();
        }
        else
        {
            optionBVisual?.SetSelectedLocked();
            optionAVisual?.SetDisabledLocked();
        }

        // Audio de la respuesta (por KEYS)
        string optionVoiceKey = (_pendingAnswerOption == Enums.StoryAnswerOption.A) ? entry.optAKey : entry.optBKey;
        _pendingAnswerVoiceId = optionVoiceKey ?? string.Empty;

        if (voiceService == null) voiceService = VoiceService.Instance;

        // Si no hay VO para esta opción, saltamos directo al feedback.
        if (voiceService == null || string.IsNullOrEmpty(_pendingAnswerVoiceId))
        {
            ContinueToFeedbackAfterAnswerVoice();
            return;
        }

        // Reproducir VO de la opción elegida
        var req = new VoiceRequest
        {
            voiceId = _pendingAnswerVoiceId,
            speakerId = entry.speaker,
            blocksProgress = true,
            allowSkip = true,
            interruptMode = VoiceInterruptMode.InterruptAndPlay,
            volume = 1f,
            fadeIn = 0f,
            fadeOut = 0.1f
        };

        voiceService.Play(req);
    }

    // ---------------------------------------------------------------------
    // CALLBACKS
    // ---------------------------------------------------------------------

    private void OnAnswerEvaluated(bool isCorrect, StoryEntry entry)
    {
        if (storyManager == null)
            return;

        if (entry != storyManager.GetCurrentEntry())
            return;

        _awaitingAnswer = false;
        _afterFeedbackAction = ResolveAfterQuestionEvaluationAction(isCorrect);

        string fbKey = isCorrect ? entry.fbOkKey : entry.fbKoKey;
        string feedback = LocalizationManager.Instance != null
            ? LocalizationManager.Instance.GetText(fbKey)
            : fbKey;

        _currentMainTextIsCritical = false;
        _expectedMainVoiceId = VoiceIdUtil.ForFeedback(entry, isCorrect);
        SetMainLocalizedText(feedback);

        bool canHideWholePanelAndAutoAdvanceFeedback =
            hideWholePanelForLineWhenTextHidden &&
            !IsExperienceTextEnabled();
        bool autoAdvanceFeedbackByConfigDelay = GetConfiguredAutoNextExtraDelaySeconds() > 0f;
        float fixedDelay;
        bool useFixedAutoNextDelay = TryGetDebugFixedAutoNextDelayOnlySeconds(out fixedDelay);
        bool autoAdvanceFeedback = canHideWholePanelAndAutoAdvanceFeedback || useFixedAutoNextDelay || autoAdvanceFeedbackByConfigDelay;

        _autoAdvanceFeedbackWhenPanelHidden = canHideWholePanelAndAutoAdvanceFeedback;
        _autoAdvanceFeedbackByConfigDelay = autoAdvanceFeedbackByConfigDelay;

        PlayCurrentMainTextRespectingPreference(
            autoAdvanceFeedback ? AfterTypingAction.None : AfterTypingAction.ShowNext,
            null,
            null);

        ApplyPanelVisibilityMode(storyManager != null ? storyManager.GetCurrentEntry() : null);

        if (autoAdvanceFeedback)
        {
            if (useFixedAutoNextDelay)
            {
                _explicitAutoAdvanceScheduled = true;
                QueueHiddenPanelAutoAdvance(ApplyPendingFeedbackActionAndAdvance, explicitDelaySeconds: fixedDelay);
            }
            else if (canHideWholePanelAndAutoAdvanceFeedback)
            {
                if (voiceService == null) voiceService = VoiceService.Instance;
                bool hasExpectedFeedbackVoice = !string.IsNullOrEmpty(_expectedMainVoiceId);
                if (!hasExpectedFeedbackVoice || voiceService == null)
                    QueueHiddenPanelAutoAdvance(ApplyPendingFeedbackActionAndAdvance, applyConfiguredVoiceExtraDelay: true);
            }
            else
            {
                if (voiceService == null) voiceService = VoiceService.Instance;
                bool hasExpectedFeedbackVoice = !string.IsNullOrEmpty(_expectedMainVoiceId);
                bool shouldWaitForVoiceCallbacks = hasExpectedFeedbackVoice && voiceService != null;
                if (!ShouldGateOnVoice() && !shouldWaitForVoiceCallbacks)
                {
                    _autoAdvanceFeedbackByConfigDelay = false;
                    QueueHiddenPanelAutoAdvance(ApplyPendingFeedbackActionAndAdvance, applyConfiguredVoiceExtraDelay: true);
                }
            }
        }

        SetQuestionGroupVisible(false);

        // Reseteo visual (la pregunta ya no se ve, pero dejamos todo limpio)
        ResetAnswerUIToNeutral();
    }

    private void OnVoiceEnded(string voiceId, VoiceEndReason reason)
    {
        if (!string.IsNullOrEmpty(voiceId) &&
            !string.IsNullOrEmpty(_expectedMainVoiceId) &&
            string.Equals(voiceId, _expectedMainVoiceId, StringComparison.OrdinalIgnoreCase))
        {
            if (reason == VoiceEndReason.FailedToLoad && (_autoAdvanceFeedbackWhenPanelHidden || _autoAdvanceFeedbackByConfigDelay))
            {
                _autoAdvanceFeedbackWhenPanelHidden = false;
                _autoAdvanceFeedbackByConfigDelay = false;
                float fixedDelay;
                if (TryGetDebugFixedAutoNextDelayOnlySeconds(out fixedDelay))
                {
                    _explicitAutoAdvanceScheduled = true;
                    QueueHiddenPanelAutoAdvance(ApplyPendingFeedbackActionAndAdvance, explicitDelaySeconds: fixedDelay);
                }
                else
                    QueueHiddenPanelAutoAdvance(ApplyPendingFeedbackActionAndAdvance, applyConfiguredVoiceExtraDelay: true);
                return;
            }

            if (reason == VoiceEndReason.FailedToLoad)
            {
                RevealSuppressedMainTextFallback();
                _autoAdvanceLineWhenPanelHidden = false;
                _autoAdvanceLineByConfigDelay = false;
                _autoAdvanceFeedbackWhenPanelHidden = false;
                _autoAdvanceFeedbackByConfigDelay = false;
                ApplyPanelVisibilityMode(storyManager != null ? storyManager.GetCurrentEntry() : null);

                if (_lineAutoAdvanceRoutine == null)
                    SetNextButtonVisible(true);
            }
            else if (reason == VoiceEndReason.Finished && (_autoAdvanceFeedbackWhenPanelHidden || _autoAdvanceFeedbackByConfigDelay) && !_explicitAutoAdvanceScheduled)
            {
                _autoAdvanceFeedbackWhenPanelHidden = false;
                _autoAdvanceFeedbackByConfigDelay = false;
                float fixedDelay;
                if (TryGetDebugFixedAutoNextDelayOnlySeconds(out fixedDelay))
                {
                    _explicitAutoAdvanceScheduled = true;
                    QueueHiddenPanelAutoAdvance(ApplyPendingFeedbackActionAndAdvance, explicitDelaySeconds: fixedDelay);
                }
                else
                    QueueHiddenPanelAutoAdvance(ApplyPendingFeedbackActionAndAdvance, applyConfiguredVoiceExtraDelay: true);
            }
        }

        if (!_answerLocked) return;
        if (string.IsNullOrEmpty(_pendingAnswerVoiceId)) return;

        if (!string.Equals(voiceId, _pendingAnswerVoiceId, StringComparison.OrdinalIgnoreCase))
            return;

        // Da igual si Finished/Interrupted/FailedToLoad: seguimos el flujo
        ContinueToFeedbackAfterAnswerVoice();
    }

    private void ContinueToFeedbackAfterAnswerVoice()
    {
        var entry = storyManager.GetCurrentEntry();
        if (entry == null || entry.type != Enums.StoryEntryType.QUESTION)
        {
            // Si por lo que sea ya no estamos en pregunta, limpia y sal.
            ResetAnswerUIToNeutral();
            _answerLocked = false;
            _pendingAnswerVoiceId = string.Empty;
            return;
        }

        // Quitar pregunta + respuestas
        SetQuestionGroupVisible(false);

        // Ya no permitimos interacción mientras entra el feedback
        SetAnswerInteractable(false, false);

        // Limpieza de estado de respuesta seleccionada
        _answerLocked = false;
        _pendingAnswerVoiceId = string.Empty;

        // Continuar flujo con feedback (esto dispara VO feedback y evento UI)
        storyManager.EvaluateCurrentAnswer(_pendingAnswerOption);
    }

    private void RefreshUI()
    {
        RequestVisualRefresh(storyManager != null ? storyManager.GetCurrentEntry() : null, "OnEntryChanged");
    }

    private void RequestVisualRefresh(StoryEntry entry, string reason)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using var _scope = _pmRefreshEnqueue.Auto();
#endif
        _pendingRefreshEntry = entry;
        _pendingRefreshVersion++;
        _hasPendingRefreshRequest = true;

        if (verboseUiPerformanceLogs)
            LogUiPerfVerbose($"Enqueue visual refresh reason={reason} version={_pendingRefreshVersion} entry={(entry != null ? entry.id : "<null>")}");

        if (!useStagedUiRefreshPipeline)
        {
            _hasPendingRefreshRequest = false;
            ApplyVisualRefreshInternal(entry, reason + ".Immediate");
            return;
        }

        if (_uiRefreshPipelineRoutine == null)
            _uiRefreshPipelineRoutine = StartCoroutine(UiRefreshPipelineRoutine());
    }

    private void CancelPendingVisualRefresh()
    {
        _hasPendingRefreshRequest = false;
        _pendingRefreshEntry = null;

        if (_uiRefreshPipelineRoutine != null)
        {
            StopCoroutine(_uiRefreshPipelineRoutine);
            _uiRefreshPipelineRoutine = null;
        }
    }

    private IEnumerator UiRefreshPipelineRoutine()
    {
        while (true)
        {
            if (!_hasPendingRefreshRequest)
                break;

            int capturedVersion = _pendingRefreshVersion;
            StoryEntry capturedEntry = _pendingRefreshEntry;
            _hasPendingRefreshRequest = false;

            int delayFrames = Mathf.Max(0, stagedUiRefreshDelayFrames);
            for (int i = 0; i < delayFrames; i++)
                yield return null;

            if (coalesceRefreshRequests && capturedVersion != _pendingRefreshVersion)
                continue;

            if (discardStaleQueuedRefresh && IsQueuedEntryStale(capturedEntry))
                continue;

            ApplyVisualRefreshInternal(capturedEntry, "QueuedCommit");
            yield return null;
        }

        _uiRefreshPipelineRoutine = null;
    }

    private bool IsQueuedEntryStale(StoryEntry queuedEntry)
    {
        if (storyManager == null)
            return false;

        var current = storyManager.GetCurrentEntry();
        if (current == queuedEntry)
            return false;

        if (queuedEntry == null || current == null)
            return queuedEntry != current;

        return !string.Equals(current.id, queuedEntry.id, StringComparison.Ordinal);
    }

    private void ApplyVisualRefreshInternal(StoryEntry entry, string source)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // PERF NOTE:
        // Se mantiene un único commit visual por request coalescida para separar
        // resolución narrativa de rebuilds de Canvas/TMP.
        float spikeStart = Time.realtimeSinceStartup;
        using var _scope = _pmRefreshCommit.Auto();
#endif
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryDialogueVRUI.RefreshUI.Begin", entry != null ? $"entry={entry.id} type={entry.type} source={source}" : $"entry=<null> source={source}");

        CancelDebugRevealCorrectOptionRoutine();
        CancelDeferredQuestionShownEvent();
        CancelPendingHiddenAutoAdvance();
        CancelLineAutoAdvanceRoutine();
        _explicitAutoAdvanceScheduled = false;
        _autoAdvanceLineWhenPanelHidden = false;
        _autoAdvanceLineByConfigDelay = false;
        _autoAdvanceFeedbackWhenPanelHidden = false;
        _autoAdvanceFeedbackByConfigDelay = false;

        // Siempre dejar la UI de respuestas en estado neutral al cambiar de entry.
        // Esto evita que la selección previa fuerce invalidaciones visuales en cadena.
        ResetAnswerUIToNeutral();

        if (entry == null)
        {
            _currentMainTextIsCritical = false;
            _expectedMainVoiceId = string.Empty;
            _lastQuestionShownEventEntryId = null;
            SetMainLocalizedText("Fin de la escena.");
            PlayCurrentMainTextRespectingPreference(AfterTypingAction.None, null, null);
            ApplyHeaderTexts(null);
            SetNextButtonVisible(false);
            SetQuestionGroupVisible(false);
            ApplyPanelVisibilityMode(null);
            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("StoryDialogueVRUI.RefreshUI.End", "entry=<null>");
            return;
        }

        ApplyHeaderTexts(entry);
        _awaitingAnswer = entry.type == Enums.StoryEntryType.QUESTION;

        if (entry.type == Enums.StoryEntryType.LINE)
        {
            _lastQuestionShownEventEntryId = null;
            SetQuestionGroupVisible(false);

            string text = LocalizationManager.Instance.GetText(entry.textKey);
            _currentMainTextIsCritical = false;
            _expectedMainVoiceId = VoiceIdUtil.ForEntry(entry);
            SetMainLocalizedText(text);

            bool canHideWholePanelAndAutoAdvance =
                hideWholePanelForLineWhenTextHidden &&
                !IsExperienceTextEnabled() &&
                !ShouldForceTextFallbackBeforeVoiceStarts();
            bool autoAdvanceByConfigDelay = GetConfiguredAutoNextExtraDelaySeconds() > 0f;
            float fixedDelay;
            bool useFixedAutoNextDelay = TryGetDebugFixedAutoNextDelayOnlySeconds(out fixedDelay);
            bool autoAdvanceLine = canHideWholePanelAndAutoAdvance || useFixedAutoNextDelay || autoAdvanceByConfigDelay;

            _autoAdvanceLineWhenPanelHidden = canHideWholePanelAndAutoAdvance;
            _autoAdvanceLineByConfigDelay = autoAdvanceByConfigDelay;

            PlayCurrentMainTextRespectingPreference(
                autoAdvanceLine ? AfterTypingAction.None : AfterTypingAction.ShowNext,
                null,
                null);

            if (autoAdvanceLine)
            {
                if (useFixedAutoNextDelay)
                {
                    _explicitAutoAdvanceScheduled = true;
                    QueueHiddenPanelAutoAdvance(() => storyManager?.NextEntry(), explicitDelaySeconds: fixedDelay);
                }
                else
                {
                    if (voiceService == null) voiceService = VoiceService.Instance;
                    StartLineAutoAdvanceRoutine(entry, text);
                }
            }
        }
        else
        {
            // Regla UX: una QUESTION nunca debe autoavanzar por una cola pendiente.
            CancelPendingHiddenAutoAdvance();

            string prompt = LocalizationManager.Instance.GetText(entry.promptKey);
            _pendingOptionAText = LocalizationManager.Instance.GetText(entry.optAKey);
            _pendingOptionBText = LocalizationManager.Instance.GetText(entry.optBKey);

            _currentMainTextIsCritical = keepQuestionPromptVisibleWhenTextHidden;
            _expectedMainVoiceId = VoiceIdUtil.ForEntry(entry);
            SetMainLocalizedText(prompt);
            PlayCurrentMainTextRespectingPreference(AfterTypingAction.ShowQuestionOptions, _pendingOptionAText, _pendingOptionBText);
        }

        ApplyPanelVisibilityMode(entry);
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryDialogueVRUI.RefreshUI.End", $"entry={entry.id} type={entry.type}");

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        LogSpikeIfNeeded("RefreshUI.Commit", spikeStart);
#endif
    }

    // ---------------------------------------------------------------------
    // VISUAL HELPERS
    // ---------------------------------------------------------------------

    private static bool SetTextIfChanged(TMP_Text target, string value, ref string lastValue)
    {
        if (target == null) return false;
        string safe = value ?? string.Empty;
        if (string.Equals(lastValue, safe, StringComparison.Ordinal))
            return false;

        target.SetText(safe);
        lastValue = safe;
        return true;
    }

    private static void SetCanvasGroupVisible(CanvasGroup group, bool visible)
    {
        if (group == null) return;
        group.alpha = visible ? 1f : 0f;
        group.interactable = visible;
        group.blocksRaycasts = visible;
    }

    private bool ShouldUseCanvasGroupVisibilityNow()
    {
        if (useCanvasGroupVisibility)
            return true;

        return preferCanvasGroupVisibilityInRuntime && Application.isPlaying;
    }

    private void SetQuestionGroupVisible(bool visible)
    {
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryDialogueVRUI.SetQuestionVisible", "visible=" + visible);

        if (_hasQuestionGroupVisibleState && _lastQuestionGroupVisible == visible)
            return;

        _lastQuestionGroupVisible = visible;
        _hasQuestionGroupVisibleState = true;

        if (ShouldUseCanvasGroupVisibilityNow() && questionGroupCanvasGroup != null)
        {
            SetCanvasGroupVisible(questionGroupCanvasGroup, visible);
            if (questionGroup != null && !questionGroup.activeSelf)
                questionGroup.SetActive(true);
            return;
        }

        if (questionGroup != null && questionGroup.activeSelf != visible)
            questionGroup.SetActive(visible);
    }

    private void SetNextButtonVisible(bool visible)
    {
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryDialogueVRUI.SetNextVisible", "visible=" + visible);

        if (_hasNextButtonVisibleState && _lastNextButtonVisible == visible)
        {
            if (nextButtonPoke != null) nextButtonPoke.enabled = visible;
            return;
        }

        _lastNextButtonVisible = visible;
        _hasNextButtonVisibleState = true;

        if (ShouldUseCanvasGroupVisibilityNow() && nextButtonCanvasGroup != null)
        {
            SetCanvasGroupVisible(nextButtonCanvasGroup, visible);
            if (nextButtonRoot != null && !nextButtonRoot.activeSelf)
                nextButtonRoot.SetActive(true);
        }
        else if (nextButtonRoot != null && nextButtonRoot.activeSelf != visible)
        {
            nextButtonRoot.SetActive(visible);
        }

        if (nextButtonPoke != null)
            nextButtonPoke.enabled = visible;
    }

    private void SetAnswerInteractable(bool leftEnabled, bool rightEnabled)
    {
        if (optionAPoke != null) optionAPoke.enabled = leftEnabled;
        if (optionBPoke != null) optionBPoke.enabled = rightEnabled;
    }

    private void ResetAnswerUIToNeutral()
    {
        _answerLocked = false;
        _pendingAnswerVoiceId = string.Empty;

        // Dejar ambas opciones preparadas para próxima pregunta
        SetAnswerInteractable(true, true);

        // Visual neutral
        optionAVisual?.SetNormal();
        optionBVisual?.SetNormal();
    }

    // ---------------------------------------------------------------------
    // TYPEWRITER (principal + opciones)
    // ---------------------------------------------------------------------

    private void PlayMainTypewriter(string text, AfterTypingAction afterAction, string optA, string optB)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using var _scope = _pmPlayMainTypewriter.Auto();
#endif
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryDialogueVRUI.PlayMainTypewriter", $"chars={(text ?? string.Empty).Length} action={afterAction}");
        _deferredAfterTypingAction = AfterTypingAction.None;

        // Corta typewriter actual
        if (_mainTypewriterCoroutine != null)
        {
            StopCoroutine(_mainTypewriterCoroutine);
            _mainTypewriterCoroutine = null;
        }

        // Por si había loop sonando de antes
        StopTypewriterLoop(immediate: false);

        _currentFullText = text ?? "";
        _afterTypingAction = afterAction;
        _pendingOptionAText = optA;
        _pendingOptionBText = optB;

        SetNextButtonVisible(false);

        if (mainText == null)
            return;

        // IMPORTANT: SetText (mejor para TMP) y NO forzar ForceMeshUpdate en el mismo frame
        // para no concentrar trabajo pesado junto con el cambio de entry / events.
        SetTextIfChanged(mainText, _currentFullText, ref _lastMainRendered);

        // Modo instantáneo: sin typewriter y sin SFX por carácter.
        bool typewriterEnabled = enableTypewriter && useTypewriterEffect;
        if (forceInstantMainText || !typewriterEnabled)
        {
            mainText.maxVisibleCharacters = int.MaxValue;
            _isTypingMain = false;
            HandleAfterTypingAction();
            return;
        }

        mainText.maxVisibleCharacters = 0;

        // Arranca SFX en loop mientras se escribe
        StartTypewriterLoop();

        _mainTypewriterCoroutine = StartCoroutine(MainTypewriterRoutine());
    }

    private IEnumerator MainTypewriterRoutine()
    {
        _isTypingMain = true;
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryDialogueVRUI.MainTypewriter.Begin", "afterAction=" + _afterTypingAction);

        // Deja que TMP procese el SetText de forma normal; luego fuerza una única generación
        // (fuera del frame crítico de entrada) para poder leer characterCount.
        // EndOfFrame suele evitar picos coincidiendo con otros sistemas.
        yield return _wfeof;

        if (mainText == null)
        {
            _isTypingMain = false;
            yield break;
        }

        // En Editor, el prewarm de glyphs dispara eventos de fuente globales de TMP
        // que pueden tocar instancias 3D ya destruidas (MissingReferenceException).
        // Se mantiene activo en build/dispositivo (donde aporta el beneficio de rendimiento).
        bool canPrewarmGlyphs = enableGlyphPrewarm && prewarmGlyphsFromCurrentText && !Application.isEditor;
        if (canPrewarmGlyphs)
            yield return PrewarmGlyphsForCurrentEntry();

        int total;
        bool shouldForceMeshUpdate = forceMeshUpdateMode switch
        {
            ForceMeshUpdateMode.Always => true,
            ForceMeshUpdateMode.Never => false,
            _ => !CanUseUltraFastPlainTextPath(_currentFullText)
        };

        if (shouldForceMeshUpdate)
        {
            // En TMP 3.x la firma es ForceMeshUpdate(bool ignoreInactive).
            // Mantener esta llamada SOLO una vez por entry y solo cuando sea necesario.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using (_pmForceMeshUpdate.Auto())
#endif
            mainText.ForceMeshUpdate(true);
            total = mainText.textInfo.characterCount;
            if (StoryTransitionTrace.Enabled)
                StoryTransitionTrace.Mark("StoryDialogueVRUI.MainTypewriter.ForceMeshUpdate", "chars=" + total);
        }
        else
        {
            total = _currentFullText.Length;
        }

        if (total <= 0)
        {
            mainText.maxVisibleCharacters = 0;
            _isTypingMain = false;
            StopTypewriterLoop(immediate: false);
            HandleAfterTypingAction();
            yield break;
        }

        float interval = Mathf.Max(0.001f, typewriterCharInterval);
        float cps = 1f / interval;
        int currentVisible = 0;

        if (!useBatchedTypewriterUpdates)
        {
            float visibleF = 0f;

            // Camino clásico: puede tocar más veces por segundo el mesh de TMP.
            while (currentVisible < total)
            {
                float dt = typewriterUseUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
                visibleF += cps * dt;

                int target = Mathf.Clamp((int)visibleF, 0, total);
                if (target != currentVisible)
                {
                    currentVisible = target;
                    mainText.maxVisibleCharacters = target;
                }

                yield return null;
            }
        }
        else
        {
            float tick = 1f / Mathf.Max(1, typewriterMaxUpdatesPerSecond);
            float timeAccumulator = 0f;
            float charsAccumulator = 0f;
            int longTextExtra = GetLongTextExtraChars(total);
            int stepsMinChars = Mathf.Max(1, Mathf.CeilToInt((float)total / Mathf.Max(1, typewriterMaxVisualStepsPerEntry)));
            _typewriterFpsEma = Mathf.Max(1f, adaptiveTargetFps);

            // Camino optimizado: limita actualizaciones visuales por segundo.
            while (currentVisible < total)
            {
                float dt = typewriterUseUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
                timeAccumulator += dt;
                charsAccumulator += cps * dt;

                int adaptiveExtra = 0;
                float adaptiveTickScale = 1f;
                bool hardProtect = false;
                if (adaptiveTypewriterByFps)
                    UpdateAdaptiveTypewriterTuning(dt, out adaptiveExtra, out adaptiveTickScale, out hardProtect);

                float currentTick = tick * adaptiveTickScale;
                int minCharsThisUpdate = Mathf.Max(typewriterMinCharsPerUpdate, stepsMinChars) + adaptiveExtra;

                if (hardProtect)
                {
                    minCharsThisUpdate += adaptiveHardProtectExtraChars;
                    currentTick *= Mathf.Max(1f, adaptiveHardProtectTickScale);
                }

                if (timeAccumulator < currentTick && charsAccumulator < minCharsThisUpdate)
                {
                    yield return null;
                    continue;
                }

                int reveal = Mathf.FloorToInt(charsAccumulator);
                if (reveal < minCharsThisUpdate)
                    reveal = minCharsThisUpdate;

                reveal += longTextExtra;
                int remaining = total - currentVisible;
                if (reveal > remaining) reveal = remaining;

                currentVisible += reveal;
                mainText.maxVisibleCharacters = currentVisible;

                charsAccumulator -= reveal;
                if (charsAccumulator < 0f) charsAccumulator = 0f;
                timeAccumulator = 0f;

                yield return null;
            }
        }

        _isTypingMain = false;
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryDialogueVRUI.MainTypewriter.End", "visibleChars=" + total);

        // Parar SFX al terminar
        StopTypewriterLoop(immediate: false);

        HandleAfterTypingAction();
    }

    private void CompleteMainTypewriterInstant()
    {
        // Si hacemos skip, parar SFX
        StopTypewriterLoop(immediate: false);

        if (_mainTypewriterCoroutine != null)
        {
            StopCoroutine(_mainTypewriterCoroutine);
            _mainTypewriterCoroutine = null;
        }

        if (mainText != null)
            mainText.maxVisibleCharacters = int.MaxValue;
        _isTypingMain = false;
        HandleAfterTypingAction();
    }

    private void HandleAfterTypingAction()
    {
        if (_afterTypingAction == AfterTypingAction.ShowNext)
        {
            if (ShouldGateOnVoice())
            {
                _deferredAfterTypingAction = AfterTypingAction.ShowNext;
            }
            else
            {
                if (deferAfterTypingActionOneFrame)
                    StartCoroutine(DeferredShowNext());
                else
                    SetNextButtonVisible(true);
            }
        }
        else if (_afterTypingAction == AfterTypingAction.ShowQuestionOptions)
        {
            if (ShouldGateQuestionOptionsOnVoice())
            {
                _deferredAfterTypingAction = AfterTypingAction.ShowQuestionOptions;
            }
            else
            {
                if (deferAfterTypingActionOneFrame)
                    StartCoroutine(DeferredShowQuestionOptions());
                else
                    ShowQuestionOptionsNow();
            }
        }

        _afterTypingAction = AfterTypingAction.None;
    }

    private bool ShouldGateOnVoice()
    {
        if (!gateProgressWithVoice) return false;
        if (voiceService == null) voiceService = VoiceService.Instance;
        return voiceService != null && voiceService.IsBlockingProgress;
    }

    private bool ShouldGateQuestionOptionsOnVoice()
    {
        // En modo texto oculto priorizamos jugabilidad: mostrar opciones de pregunta sin esperar a VO.
        if (!IsExperienceTextEnabled())
            return false;

        return ShouldGateOnVoice();
    }

    private void OnVoiceBlockingChanged(bool isBlocking, string voiceId)
    {
        if (isBlocking) return;

        // Si ya no bloquea, y teníamos acción pendiente y no estamos escribiendo, ejecuta.
        if (_isTypingMain) return;

        if (_deferredAfterTypingAction == AfterTypingAction.ShowNext)
        {
            _deferredAfterTypingAction = AfterTypingAction.None;
            if (deferAfterTypingActionOneFrame)
                StartCoroutine(DeferredShowNext());
            else
                SetNextButtonVisible(true);
            return;
        }

        if (_deferredAfterTypingAction == AfterTypingAction.ShowQuestionOptions)
        {
            _deferredAfterTypingAction = AfterTypingAction.None;
            if (deferAfterTypingActionOneFrame)
                StartCoroutine(DeferredShowQuestionOptions());
            else
                ShowQuestionOptionsNow();
            return;
        }
    }

    private IEnumerator DeferredShowNext()
    {
        // Un frame de respiro para que TMP/Canvas no se concentre en el mismo frame del final del typewriter.
        yield return null;
        SetNextButtonVisible(true);
    }

    private IEnumerator DeferredShowQuestionOptions()
    {
        yield return null;
        ShowQuestionOptionsNow();
    }

    private void ShowQuestionOptionsNow()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using var _scope = _pmRebuildOptions.Auto();
#endif
        var entry = storyManager != null ? storyManager.GetCurrentEntry() : null;
        string entryId = entry != null ? entry.id : string.Empty;

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryDialogueVRUI.RebuildOptions.Begin", $"optAChars={(_pendingOptionAText ?? string.Empty).Length} optBChars={(_pendingOptionBText ?? string.Empty).Length}");

        // Preparar UI de opciones
        ResetAnswerUIToNeutral();
        SetQuestionGroupVisible(true);

        // PERF NOTE:
        // Refresh redundante del mismo entry puede reconstruir opciones varias veces y
        // provocar TMP/Canvas rebuild sin cambios reales. Se fija el swap por entry.
        bool swap;
        if (!string.IsNullOrEmpty(entryId) && string.Equals(_lastQuestionSwapEntryId, entryId, StringComparison.Ordinal))
            swap = _lastQuestionSwapValue;
        else
            swap = Random.value < 0.5f;

        _lastQuestionSwapEntryId = entryId;
        _lastQuestionSwapValue = swap;

        if (swap)
        {
            _leftAnswerOption = Enums.StoryAnswerOption.B;
            _rightAnswerOption = Enums.StoryAnswerOption.A;
            SetTextIfChanged(optionAText, _pendingOptionBText, ref _lastOptionARendered);
            SetTextIfChanged(optionBText, _pendingOptionAText, ref _lastOptionBRendered);
        }
        else
        {
            _leftAnswerOption = Enums.StoryAnswerOption.A;
            _rightAnswerOption = Enums.StoryAnswerOption.B;
            SetTextIfChanged(optionAText, _pendingOptionAText, ref _lastOptionARendered);
            SetTextIfChanged(optionBText, _pendingOptionBText, ref _lastOptionBRendered);
        }

        // Se dispara cuando la pregunta ya está visible y lista para interacción.
        if (entry != null && entry.type == Enums.StoryEntryType.QUESTION)
            RaiseQuestionShownEvent(entry);

        StartDebugRevealCorrectOptionRoutineIfNeeded();
        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryDialogueVRUI.RebuildOptions.End", $"left={_leftAnswerOption} right={_rightAnswerOption}");
    }

    private void RaiseQuestionShownEvent(StoryEntry entry)
    {
        if (entry == null || entry.type != Enums.StoryEntryType.QUESTION)
            return;

        if (string.Equals(_lastQuestionShownEventEntryId, entry.id, StringComparison.Ordinal))
            return;

        _lastQuestionShownEventEntryId = entry.id;
        if (!deferQuestionShownEventOneFrame)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var _scope = _pmQuestionShownEvent.Auto();
#endif
            RaiseQuestionSemanticEvent(StoryQuestionEventNames.ForQuestionShown(entry), entry);
            return;
        }

        CancelDeferredQuestionShownEvent();
        _deferredQuestionShownRoutine = StartCoroutine(DeferredQuestionShownEventRoutine(entry));
    }

    private IEnumerator DeferredQuestionShownEventRoutine(StoryEntry entry)
    {
        yield return null;

        _deferredQuestionShownRoutine = null;
        if (entry == null)
            yield break;

        var current = storyManager != null ? storyManager.GetCurrentEntry() : null;
        if (current == null || current.type != Enums.StoryEntryType.QUESTION)
            yield break;
        if (!string.Equals(current.id, entry.id, StringComparison.Ordinal))
            yield break;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using var _scope = _pmQuestionShownEvent.Auto();
#endif
        RaiseQuestionSemanticEvent(StoryQuestionEventNames.ForQuestionShown(current), current);
    }

    private void CancelDeferredQuestionShownEvent()
    {
        if (_deferredQuestionShownRoutine == null)
            return;

        StopCoroutine(_deferredQuestionShownRoutine);
        _deferredQuestionShownRoutine = null;
    }

    private static void RaiseQuestionSemanticEvent(string eventName, StoryEntry entry)
    {
        if (string.IsNullOrEmpty(eventName))
            return;

        StoryEventBus.Raise(eventName, entry);
    }

    private void ApplyHeaderTexts(StoryEntry entry)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using var _scope = _pmUpdateTexts.Auto();
#endif
        bool showAllExperienceText = IsExperienceTextEnabled();
        bool isQuestion = entry != null && entry.type == Enums.StoryEntryType.QUESTION;

        if (idText != null)
        {
            bool showId = showAllExperienceText || !hideIdWhenTextHidden;
            SetTextIfChanged(idText, showId ? (entry != null ? entry.id : string.Empty) : string.Empty, ref _lastIdRendered);
        }

        if (speakerText != null)
        {
            bool showSpeaker = showAllExperienceText ||
                               !hideSpeakerWhenTextHidden ||
                               (isQuestion && keepQuestionSpeakerVisibleWhenTextHidden);
            SetTextIfChanged(speakerText, showSpeaker ? (entry != null ? entry.speaker : string.Empty) : string.Empty, ref _lastSpeakerRendered);
        }
    }

    private void SetMainLocalizedText(string localizedText)
    {
        _currentEntryMainLocalizedText = localizedText ?? string.Empty;
    }

    private void PlayCurrentMainTextRespectingPreference(AfterTypingAction afterAction, string optA, string optB)
    {
        string textToRender = GetMainTextToRender();
        _isMainTextSuppressedByPreference = string.IsNullOrEmpty(textToRender) && !string.IsNullOrEmpty(_currentEntryMainLocalizedText);

        // Si estamos ocultando texto pero no hay VO posible, mostramos fallback inmediato.
        if (_isMainTextSuppressedByPreference && ShouldForceTextFallbackBeforeVoiceStarts())
        {
            textToRender = _currentEntryMainLocalizedText;
            _isMainTextSuppressedByPreference = false;
        }

        PlayMainTypewriter(textToRender, afterAction, optA, optB);
    }

    private void ApplyMainTextVisibilityForCurrentState()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        using var _scope = _pmUpdateTexts.Auto();
#endif
        if (mainText == null)
            return;

        string textToRender = GetMainTextToRender();
        _isMainTextSuppressedByPreference = string.IsNullOrEmpty(textToRender) && !string.IsNullOrEmpty(_currentEntryMainLocalizedText);

        if (_isMainTextSuppressedByPreference && ShouldForceTextFallbackBeforeVoiceStarts())
        {
            textToRender = _currentEntryMainLocalizedText;
            _isMainTextSuppressedByPreference = false;
        }

        SetTextIfChanged(mainText, textToRender, ref _lastMainRendered);
        mainText.maxVisibleCharacters = int.MaxValue;
    }

    private string GetMainTextToRender()
    {
        if (IsExperienceTextEnabled())
            return _currentEntryMainLocalizedText;

        if (_currentMainTextIsCritical)
            return _currentEntryMainLocalizedText;

        return string.Empty;
    }

    private bool IsExperienceTextEnabled()
    {
        var dataManager = DataManager.GetInstance();
        if (dataManager != null)
        {
            _cachedShowExperienceText = dataManager.GetShowExperienceText();
            return _cachedShowExperienceText;
        }

        var config = GetExperienceConfig();
        if (config != null)
        {
            _cachedShowExperienceText = config.showExperienceText;
            return _cachedShowExperienceText;
        }

        _cachedShowExperienceText = ExperienceTextPreference.Load();
        return _cachedShowExperienceText;
    }

    private static ExperienceConfig GetExperienceConfig()
    {
        var experienceManager = ExperienceManager.Instance;
        if (experienceManager == null)
            return null;

        return experienceManager.config;
    }

    private AfterFeedbackAction ResolveAfterQuestionEvaluationAction(bool isCorrect)
    {
        // Correct answer always continues to next entry.
        if (isCorrect)
            return AfterFeedbackAction.AdvanceForward;

        return ResolveWrongAnswerAfterFeedbackAction();
    }

    private AfterFeedbackAction ResolveWrongAnswerAfterFeedbackAction()
    {
        // Wrong-answer behavior is configured from ExperienceConfig.
        var config = GetExperienceConfig();
        var wrongAnswerFlowMode = config != null
            ? config.wrongAnswerFlowMode
            : Enums.WrongAnswerFlowMode.GoToPreviousEntry;

        return wrongAnswerFlowMode == Enums.WrongAnswerFlowMode.ContinueForward
            ? AfterFeedbackAction.AdvanceForward
            : AfterFeedbackAction.GoToPreviousEntry;
    }

    private bool ShouldForceTextFallbackBeforeVoiceStarts()
    {
        if (string.IsNullOrEmpty(_currentEntryMainLocalizedText))
            return false;

        if (string.IsNullOrEmpty(_expectedMainVoiceId))
            return true;

        if (voiceService == null)
            voiceService = VoiceService.Instance;

        return voiceService == null;
    }

    private void RevealSuppressedMainTextFallback()
    {
        if (!_isMainTextSuppressedByPreference)
            return;

        if (mainText == null || string.IsNullOrEmpty(_currentEntryMainLocalizedText))
            return;

        _isMainTextSuppressedByPreference = false;
        SetTextIfChanged(mainText, _currentEntryMainLocalizedText, ref _lastMainRendered);
        mainText.maxVisibleCharacters = int.MaxValue;
    }

    private void ApplyPanelVisibilityMode(StoryEntry entry)
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Spike candidate: SetActive masivo de hijos al ocultar/mostrar panel completo.
        // Coste esperado: activación de objetos + potencial rebuild de Canvas.
        using var _scope = _pmApplyPanelVisibility.Auto();
#endif
        bool shouldHideWholePanel =
            hideWholePanelForLineWhenTextHidden &&
            (_autoAdvanceLineWhenPanelHidden || _autoAdvanceFeedbackWhenPanelHidden);

        if (StoryTransitionTrace.Enabled)
            StoryTransitionTrace.Mark("StoryDialogueVRUI.PanelVisibility", "hideWholePanel=" + shouldHideWholePanel);

        if (_hasWholePanelHiddenState && _lastWholePanelHidden == shouldHideWholePanel)
            return;

        _lastWholePanelHidden = shouldHideWholePanel;
        _hasWholePanelHiddenState = true;

        bool useCanvasGroupNow = ShouldUseCanvasGroupVisibilityNow();
        if (verboseUiPerformanceLogs)
            LogUiPerfVerbose($"Panel hidden={shouldHideWholePanel} useCanvasGroup={useCanvasGroupNow}");

        if (useCanvasGroupNow && wholePanelCanvasGroup != null)
        {
            SetCanvasGroupVisible(wholePanelCanvasGroup, !shouldHideWholePanel);

            if (_children != null)
            {
                for (int i = 0; i < _children.Length; i++)
                {
                    if (_children[i] == null) continue;
                    if (!_children[i].activeSelf) _children[i].SetActive(true);
                }
            }
        }
        else if (_children != null)
        {
            for (int i = 0; i < _children.Length; i++)
            {
                if (_children[i] == null) continue;
                bool targetActive = !shouldHideWholePanel;
                if (_children[i].activeSelf == targetActive) continue;
                _children[i].SetActive(targetActive);
            }
        }

        if (uiFollowHead != null)
            uiFollowHead.enabled = !shouldHideWholePanel;
    }

    private void ApplyPendingFeedbackActionAndAdvance()
    {
        if (_afterFeedbackAction == AfterFeedbackAction.GoToPreviousEntry)
        {
            _afterFeedbackAction = AfterFeedbackAction.None;
            storyManager?.TryJumpToPreviousEntry();
            return;
        }

        if (_afterFeedbackAction == AfterFeedbackAction.AdvanceForward)
        {
            _afterFeedbackAction = AfterFeedbackAction.None;
            storyManager?.NextEntry();
            return;
        }
    }

    private void QueueHiddenPanelAutoAdvance(Action advanceAction, bool applyConfiguredVoiceExtraDelay = false, float explicitDelaySeconds = -1f)
    {
        if (advanceAction == null)
            return;

        if (_hiddenPanelAutoAdvanceRoutine != null)
        {
            StopCoroutine(_hiddenPanelAutoAdvanceRoutine);
            _hiddenPanelAutoAdvanceRoutine = null;
        }

        _hiddenPanelAutoAdvanceRoutine = StartCoroutine(HiddenPanelAutoAdvanceRoutine(advanceAction, applyConfiguredVoiceExtraDelay, explicitDelaySeconds));
    }

    private void StartLineAutoAdvanceRoutine(StoryEntry entry, string displayedText)
    {
        if (entry == null || entry.type != Enums.StoryEntryType.LINE)
            return;

        CancelLineAutoAdvanceRoutine();
        _lineAutoAdvanceRoutine = StartCoroutine(LineAutoAdvanceRoutine(entry, displayedText));
    }

    private void CancelLineAutoAdvanceRoutine()
    {
        if (_lineAutoAdvanceRoutine == null)
            return;

        StopCoroutine(_lineAutoAdvanceRoutine);
        _lineAutoAdvanceRoutine = null;
    }

    private IEnumerator LineAutoAdvanceRoutine(StoryEntry entry, string displayedText)
    {
        if (entry == null)
        {
            _lineAutoAdvanceRoutine = null;
            yield break;
        }

        string expectedEntryId = entry.id;
        string expectedVoiceId = _expectedMainVoiceId ?? string.Empty;
        string expectedStartEventName = entry.StartEventName;

        float charDuration = CalculateLineAutoNextDurationFromText(displayedText);
        bool hasVoiceToWait = !string.IsNullOrEmpty(expectedVoiceId) && voiceService != null;
        bool hasGraphToWait = StoryActionGraphRunner.AnyRunnerHasActionsForEvent(expectedStartEventName, entry);

        if (charDuration > 0f)
            yield return new WaitForSecondsRealtime(charDuration);

        if (!IsStillOnExpectedLineEntry(expectedEntryId))
        {
            _lineAutoAdvanceRoutine = null;
            yield break;
        }

        if (hasVoiceToWait)
        {
            while (IsStillOnExpectedLineEntry(expectedEntryId) &&
                   !IsExpectedVoiceCompleted(expectedVoiceId))
            {
                yield return null;
            }
        }

        if (!IsStillOnExpectedLineEntry(expectedEntryId))
        {
            _lineAutoAdvanceRoutine = null;
            yield break;
        }

        if (hasGraphToWait)
        {
            while (IsStillOnExpectedLineEntry(expectedEntryId) &&
                   StoryActionGraphRunner.IsEventInProgress(expectedStartEventName))
            {
                yield return null;
            }
        }

        if (!IsStillOnExpectedLineEntry(expectedEntryId))
        {
            _lineAutoAdvanceRoutine = null;
            yield break;
        }

        _autoAdvanceLineWhenPanelHidden = false;
        _autoAdvanceLineByConfigDelay = false;
        QueueHiddenPanelAutoAdvance(() => storyManager?.NextEntry(), applyConfiguredVoiceExtraDelay: true);
        _lineAutoAdvanceRoutine = null;
    }

    private bool IsStillOnExpectedLineEntry(string expectedEntryId)
    {
        if (storyManager == null)
            return false;

        var current = storyManager.GetCurrentEntry();
        if (current == null || current.type != Enums.StoryEntryType.LINE)
            return false;

        return string.Equals(current.id, expectedEntryId, StringComparison.Ordinal);
    }

    private bool IsExpectedVoiceCompleted(string expectedVoiceId)
    {
        if (string.IsNullOrEmpty(expectedVoiceId))
            return true;

        if (voiceService == null)
            voiceService = VoiceService.Instance;

        if (voiceService == null)
            return true;

        if (string.Equals(voiceService.CurrentVoiceId, expectedVoiceId, StringComparison.OrdinalIgnoreCase) &&
            voiceService.IsVoicePlaying)
            return false;

        return !voiceService.IsBlockingProgress;
    }

    private float CalculateLineAutoNextDurationFromText(string displayedText)
    {
        int characterCount = string.IsNullOrEmpty(displayedText) ? 0 : displayedText.Length;
        float perCharacter = GetConfiguredAutoNextSecondsPerCharacter();
        float minDuration = GetConfiguredAutoNextMinDurationSeconds();

        float estimated = Mathf.Max(0f, characterCount) * perCharacter;
        return Mathf.Max(minDuration, estimated);
    }

    private void CancelPendingHiddenAutoAdvance()
    {
        if (_hiddenPanelAutoAdvanceRoutine == null)
            return;

        StopCoroutine(_hiddenPanelAutoAdvanceRoutine);
        _hiddenPanelAutoAdvanceRoutine = null;
    }

    private IEnumerator HiddenPanelAutoAdvanceRoutine(Action advanceAction, bool applyConfiguredVoiceExtraDelay, float explicitDelaySeconds)
    {
        yield return null;

        float delaySeconds = 0f;
        if (explicitDelaySeconds >= 0f)
        {
            delaySeconds = explicitDelaySeconds;
        }
        else if (applyConfiguredVoiceExtraDelay)
        {
            delaySeconds = GetConfiguredAutoNextExtraDelaySeconds();
        }

        if (delaySeconds > 0f)
        {
            yield return new WaitForSecondsRealtime(delaySeconds);
        }

        _explicitAutoAdvanceScheduled = false;
        _hiddenPanelAutoAdvanceRoutine = null;
        advanceAction?.Invoke();
    }

    private float GetConfiguredAutoNextExtraDelaySeconds()
    {
        var exp = ExperienceManager.Instance;
        if (exp == null || exp.config == null)
            return 0f;

        return Mathf.Max(0f, exp.config.autoNextExtraDelaySeconds);
    }

    private float GetConfiguredAutoNextSecondsPerCharacter()
    {
        var exp = ExperienceManager.Instance;
        if (exp == null || exp.config == null)
            return 0f;

        return Mathf.Max(0f, exp.config.autoNextSecondsPerCharacter);
    }

    private float GetConfiguredAutoNextMinDurationSeconds()
    {
        var exp = ExperienceManager.Instance;
        if (exp == null || exp.config == null)
            return 0f;

        return Mathf.Max(0f, exp.config.autoNextMinDurationSeconds);
    }

    private bool TryGetDebugFixedAutoNextDelayOnlySeconds(out float seconds)
    {
        seconds = 0f;

        var exp = ExperienceManager.Instance;
        if (exp == null || exp.config == null)
            return false;

        if (!exp.config.debugUseFixedAutoNextDelayOnly)
            return false;

        seconds = Mathf.Max(0f, exp.config.debugFixedAutoNextDelaySeconds);
        return true;
    }

    private bool TryGetDebugMarkCorrectOptionDelaySeconds(out float seconds)
    {
        seconds = 0f;

        var exp = ExperienceManager.Instance;
        if (exp == null || exp.config == null)
            return false;

        if (!exp.config.debugMarkCorrectOptionAfterDelay)
            return false;

        seconds = Mathf.Max(0f, exp.config.debugMarkCorrectOptionDelaySeconds);
        return true;
    }

    private void StartDebugRevealCorrectOptionRoutineIfNeeded()
    {
        CancelDebugRevealCorrectOptionRoutine();

        float delaySeconds;
        if (!TryGetDebugMarkCorrectOptionDelaySeconds(out delaySeconds))
            return;

        var entry = storyManager != null ? storyManager.GetCurrentEntry() : null;
        if (entry == null || entry.type != Enums.StoryEntryType.QUESTION)
            return;

        _debugRevealCorrectOptionRoutine = StartCoroutine(DebugRevealCorrectOptionRoutine(delaySeconds, entry.id));
    }

    private void CancelDebugRevealCorrectOptionRoutine()
    {
        if (_debugRevealCorrectOptionRoutine == null)
            return;

        StopCoroutine(_debugRevealCorrectOptionRoutine);
        _debugRevealCorrectOptionRoutine = null;
    }

    private IEnumerator DebugRevealCorrectOptionRoutine(float delaySeconds, string expectedEntryId)
    {
        if (delaySeconds > 0f)
            yield return new WaitForSecondsRealtime(delaySeconds);

        _debugRevealCorrectOptionRoutine = null;

        if (_answerLocked || !_awaitingAnswer)
            yield break;

        var currentEntry = storyManager != null ? storyManager.GetCurrentEntry() : null;
        if (currentEntry == null || currentEntry.type != Enums.StoryEntryType.QUESTION)
            yield break;

        if (!string.Equals(currentEntry.id, expectedEntryId, StringComparison.Ordinal))
            yield break;

        bool correctIsLeft = currentEntry.correct == _leftAnswerOption;
        if (correctIsLeft)
        {
            optionAVisual?.SetSelectedLocked();
            optionBVisual?.SetNormal();
        }
        else
        {
            optionBVisual?.SetSelectedLocked();
            optionAVisual?.SetNormal();
        }

        // En debug, además de marcar visualmente, selecciona la opción correcta.
        HandleAnswerPressed(correctIsLeft);
    }

    
// ---------------------------------------------------------------------
// TMP GLYPH PREWARM (para evitar spikes en Quest/Android)
// ---------------------------------------------------------------------

private IEnumerator PrewarmGlyphsForCurrentEntry()
{
    if (mainText == null) yield break;

    var rootFont = mainText.font;
    if (rootFont == null) yield break;

    // Solo tiene sentido si el atlas es dinámico.
    if (rootFont.atlasPopulationMode != AtlasPopulationMode.Dynamic)
        yield break;

    // 1) Construir set de caracteres a partir del texto actual (+ opciones si aplica)
    _glyphCharSet.Clear();

    AddVisibleCharsToSet(_currentFullText);

    // Si vamos a mostrar opciones al final, precalentamos también sus textos
    if (prewarmAlsoOptionTexts && _afterTypingAction == AfterTypingAction.ShowQuestionOptions)
    {
        AddVisibleCharsToSet(_pendingOptionAText);
        AddVisibleCharsToSet(_pendingOptionBText);
    }

    if (_glyphCharSet.Count == 0)
        yield break;

    // 2) Reunir fonts: root + fallbacks (si aplica)
    _glyphFonts.Clear();
    CollectFont(rootFont, includeFallbacks: prewarmIncludeFallbackFonts);

    // 3) TryAddCharacters en chunks con yields para repartir el coste
    int chunk = Mathf.Max(16, prewarmChunkSize);
    int yields = Mathf.Max(0, prewarmYieldFramesBetweenChunks);
    bool didAnyPrewarmWork = false;

    for (int f = 0; f < _glyphFonts.Count; f++)
    {
        var font = _glyphFonts[f];
        if (font == null) continue;
        if (font.atlasPopulationMode != AtlasPopulationMode.Dynamic) continue;

        if (!_prewarmedGlyphsByFont.TryGetValue(font, out var warmedChars))
        {
            warmedChars = new HashSet<char>(256);
            _prewarmedGlyphsByFont[font] = warmedChars;
        }

        _glyphChunkSb.Clear();
        bool didWorkForFont = false;

        foreach (var ch in _glyphCharSet)
        {
            if (warmedChars.Contains(ch))
                continue;

            didWorkForFont = true;
            _glyphChunkSb.Append(ch);
            if (_glyphChunkSb.Length < chunk)
                continue;

            TryPrewarmGlyphChunk(font, warmedChars, _glyphChunkSb);
            for (int y = 0; y < yields; y++)
                yield return null;
        }

        if (_glyphChunkSb.Length > 0)
        {
            TryPrewarmGlyphChunk(font, warmedChars, _glyphChunkSb);
            for (int y = 0; y < yields; y++)
                yield return null;
        }

        if (didWorkForFont)
        {
            didAnyPrewarmWork = true;
            // Respiro entre fonts solo si hubo trabajo real.
            yield return null;
        }
    }

    if (!didAnyPrewarmWork)
        yield break;
}

private static void TryPrewarmGlyphChunk(TMP_FontAsset font, HashSet<char> warmedChars, StringBuilder chunkBuilder)
{
    if (font == null || chunkBuilder == null || chunkBuilder.Length == 0)
        return;

    int count = chunkBuilder.Length;
    string chunk = chunkBuilder.ToString();

    try
    {
        font.TryAddCharacters(chunk, out _);
    }
    catch
    {
        // No bloqueamos el juego por problemas de atlas lleno o fonts raras.
    }

    // Marcamos el chunk como ya intentado para no repetir trabajo en entradas futuras.
    for (int i = 0; i < count; i++)
        warmedChars.Add(chunkBuilder[i]);

    chunkBuilder.Clear();
}

private static void AddVisibleCharsToSet(string s)
{
    if (string.IsNullOrEmpty(s)) return;

    bool inTag = false;
    for (int i = 0; i < s.Length; i++)
    {
        char c = s[i];

        if (inTag)
        {
            if (c == '>')
                inTag = false;
            continue;
        }

        if (c == '<')
        {
            inTag = true;
            continue;
        }

        if (char.IsWhiteSpace(c))
            continue;

        _glyphCharSet.Add(c);
    }
}

private bool CanUseUltraFastPlainTextPath(string s)
{
    if (!ultraFastPlainTextPath) return false;
    if (string.IsNullOrEmpty(s)) return true;

    for (int i = 0; i < s.Length; i++)
    {
        char c = s[i];

        // Si hay tags rich-text o surrogate pairs, necesitamos el conteo real de TMP.
        if (c == '<' || char.IsSurrogate(c))
            return false;
    }

    return true;
}

private int GetLongTextExtraChars(int totalChars)
{
    if (typewriterExtraCharsForLongText <= 0)
        return 0;

    int threshold = Mathf.Max(1, typewriterLongTextThreshold);
    if (totalChars <= threshold)
        return 0;

    int tiers = totalChars / threshold;
    int extra = tiers * typewriterExtraCharsForLongText;
    return Mathf.Clamp(extra, 0, 12);
}

private void UpdateAdaptiveTypewriterTuning(float dt, out int extraChars, out float tickScale, out bool hardProtect)
{
    extraChars = 0;
    tickScale = 1f;
    hardProtect = false;

    if (dt <= 0f)
        return;

    float instantFps = 1f / dt;
    float alpha = Mathf.Clamp01(adaptiveFpsSmoothing);
    _typewriterFpsEma = Mathf.Lerp(_typewriterFpsEma, instantFps, alpha);

    float target = Mathf.Max(1f, adaptiveTargetFps);
    float startAt = target * Mathf.Clamp(adaptiveStartAtTargetRatio, 0.6f, 1f);
    float hardAt = target * Mathf.Clamp(adaptiveHardProtectRatio, 0.5f, 0.95f);

    if (_typewriterFpsEma < hardAt)
        hardProtect = true;

    if (_typewriterFpsEma >= startAt)
        return;

    float deficit01 = Mathf.Clamp01((startAt - _typewriterFpsEma) / startAt);
    extraChars = Mathf.Clamp(Mathf.CeilToInt(deficit01 * adaptiveMaxExtraCharsPerUpdate), 0, adaptiveMaxExtraCharsPerUpdate);

    float maxScale = Mathf.Max(1f, adaptiveMaxTickScale);
    tickScale = Mathf.Lerp(1f, maxScale, deficit01);
}

private static void CollectFont(TMP_FontAsset f, bool includeFallbacks)
{
    if (f == null) return;
    if (_glyphFonts.Contains(f)) return;
    _glyphFonts.Add(f);

    if (!includeFallbacks) return;
    var fallbacks = f.fallbackFontAssetTable;
    if (fallbacks == null) return;
    for (int i = 0; i < fallbacks.Count; i++)
        CollectFont(fallbacks[i], includeFallbacks: true);
}

// ---------------------------------------------------------------------
    // TYPEWRITER LOOP SFX
    // ---------------------------------------------------------------------

    public void SetTypewriterLoopSfxEnabled(bool enabled)
    {
        if (enableTypewriterLoopSfx == enabled)
            return;

        enableTypewriterLoopSfx = enabled;

        if (!enableTypewriterLoopSfx)
        {
            // Al apagar en runtime, cortar cualquier reproducción/fade en curso.
            StopTypewriterLoop(immediate: true);
            return;
        }

        // Si se vuelve a activar y no hay source asignado, resolverlo una sola vez.
        if (typewriterLoopSource == null)
            typewriterLoopSource = GetComponent<AudioSource>();
    }

    private void StartTypewriterLoop()
    {
        if (!enableTypewriterLoopSfx) return;

        if (typewriterLoopSource == null)
            typewriterLoopSource = GetComponent<AudioSource>();

        if (typewriterLoopSource == null) return;
        if (typewriterLoopClip == null) return;

        // Si ya está sonando el mismo clip, no reiniciar
        if (typewriterLoopSource.isPlaying && typewriterLoopSource.clip == typewriterLoopClip)
            return;

        // Cancela rutina previa
        if (_typewriterLoopRoutine != null)
        {
            StopCoroutine(_typewriterLoopRoutine);
            _typewriterLoopRoutine = null;
        }

        typewriterLoopSource.clip = typewriterLoopClip;
        typewriterLoopSource.loop = true;

        if (randomizeLoopStartTime && typewriterLoopClip.length > 0.05f)
            typewriterLoopSource.time = Random.Range(0f, Mathf.Max(0f, typewriterLoopClip.length - 0.05f));

        typewriterLoopSource.volume = 0f;
        typewriterLoopSource.Play();

        _typewriterLoopRoutine = StartCoroutine(FadeAudio(typewriterLoopSource, typewriterLoopVolume, typewriterLoopFadeIn));
    }

    private void StopTypewriterLoop(bool immediate)
    {
        if (typewriterLoopSource == null) return;

        if (_typewriterLoopRoutine != null)
        {
            StopCoroutine(_typewriterLoopRoutine);
            _typewriterLoopRoutine = null;
        }

        if (!typewriterLoopSource.isPlaying) return;

        if (immediate || typewriterLoopFadeOut <= 0f)
        {
            typewriterLoopSource.Stop();
            typewriterLoopSource.clip = null;
            typewriterLoopSource.volume = typewriterLoopVolume;
            return;
        }

        _typewriterLoopRoutine = StartCoroutine(StopWithFade(typewriterLoopSource, typewriterLoopFadeOut));
    }

    private IEnumerator FadeAudio(AudioSource src, float targetVol, float seconds)
    {
        if (src == null) yield break;

        if (seconds <= 0f)
        {
            src.volume = targetVol;
            _typewriterLoopRoutine = null;
            yield break;
        }

        float start = src.volume;
        float t = 0f;
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            src.volume = Mathf.Lerp(start, targetVol, t / seconds);
            yield return null;
        }

        src.volume = targetVol;
        _typewriterLoopRoutine = null;
    }

    private IEnumerator StopWithFade(AudioSource src, float seconds)
    {
        if (src == null) yield break;

        float start = src.volume;
        float t = 0f;
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            src.volume = Mathf.Lerp(start, 0f, t / seconds);
            yield return null;
        }

        src.Stop();
        src.clip = null;
        src.volume = typewriterLoopVolume;
        _typewriterLoopRoutine = null;
    }
}
