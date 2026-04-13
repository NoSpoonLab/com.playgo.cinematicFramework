using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Gestor de localización muy simple basado en LocDatabase.
/// </summary>
public class LocalizationManager : MonoBehaviour
{
    public static LocalizationManager Instance { get; private set; }
    private static bool _duplicateWarned;

    [Header("Data")]
    [Tooltip("Base de datos de localización generada desde LOC.csv.")]
    public LocDatabase locDatabase;

    [Header("Idioma actual")]
    public Enums.Language currentLanguage = Enums.Language.Spanish;

    private Dictionary<string, LocEntry> _dict;
    private bool _warnedMissingDictionary;
    private float _nextMissingDictionaryWarningTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticsForPlayMode()
    {
        Instance = null;
        _duplicateWarned = false;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            if (!_duplicateWarned)
            {
                _duplicateWarned = true;
                Debug.LogWarning("[LocalizationManager] Duplicate instance detected. Destroying newest instance to keep singleton stable.");
            }

            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        ApplyCurrentLanguageFromAppData();
        BuildDictionary();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void BuildDictionary()
    {
        _warnedMissingDictionary = false;
        _nextMissingDictionaryWarningTime = 0f;

        if (locDatabase == null || locDatabase.entries == null)
        {
            _dict = null;
            Debug.LogError("[LocalizationManager] LocDatabase no asignado o entries es null.");
            return;
        }

        _dict = new Dictionary<string, LocEntry>();

        foreach (var entry in locDatabase.entries)
        {
            if (entry == null || string.IsNullOrEmpty(entry.key))
                continue;

            if (_dict.ContainsKey(entry.key))
            {
                Debug.LogWarning($"[LocalizationManager] Key duplicada en LOC: {entry.key}");
                continue;
            }

            _dict.Add(entry.key, entry);
        }
    }


    private void ApplyCurrentLanguageFromAppData()
    {
        currentLanguage = (AppData.language == SystemLanguage.English)
            ? Enums.Language.English
            : Enums.Language.Spanish;
    }

    public void SetLanguage(Enums.Language language)
    {
        currentLanguage = language;
    }

    public bool TryGetText(string key, out string text)
    {
        text = string.Empty;

        if (string.IsNullOrEmpty(key))
            return false;

        if (_dict == null)
        {
            if (!_warnedMissingDictionary || Time.unscaledTime >= _nextMissingDictionaryWarningTime)
            {
                Debug.LogWarning("[LocalizationManager] Diccionario no inicializado. Revisa locDatabase/entries.");
                _warnedMissingDictionary = true;
                _nextMissingDictionaryWarningTime = Time.unscaledTime + 10f;
            }
            return false;
        }

        _warnedMissingDictionary = false;

        if (!_dict.TryGetValue(key, out var entry) || entry == null)
            return false;

        switch (currentLanguage)
        {
            case Enums.Language.English:
                text = string.IsNullOrEmpty(entry.enGB) ? entry.esES : entry.enGB;
                break;

            case Enums.Language.Spanish:
            case Enums.Language.None:
            default:
                text = entry.esES;
                break;
        }

        return !string.IsNullOrEmpty(text);
    }

    public string GetText(string key)
    {
        return TryGetText(key, out var text) ? text : $"#{key}#";
    }
}
