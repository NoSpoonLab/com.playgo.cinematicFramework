using System;
using UnityEngine;

[Serializable]
public class StoryEntry
{
    [Tooltip("ID único de la fila (columna id en STORY.csv).")]
    public string id;

    [Tooltip("Escena lógica a la que pertenece (columna scene).")]
    public string scene;

    [Tooltip("Orden dentro de la escena (columna order).")]
    public int order;

    [Tooltip("Tipo de entrada: LINE, QUESTION (columna type).")]
    public Enums.StoryEntryType type;

    [Tooltip("Quién habla o quién 'emite' esta entrada (columna speaker).")]
    public string speaker;

    [Tooltip("Key de texto principal (columna text_key).")]
    public string textKey;

    [Tooltip("Key del enunciado de pregunta (columna prompt_key).")]
    public string promptKey;

    [Tooltip("Key de la opción A (columna optA_key).")]
    public string optAKey;

    [Tooltip("Key de la opción B (columna optB_key).")]
    public string optBKey;

    [Tooltip("Respuesta correcta (columna correct: 'A' o 'B').")]
    public Enums.StoryAnswerOption correct;

    [Tooltip("Key del feedback si la respuesta es correcta (columna fb_ok_key).")]
    public string fbOkKey;

    [Tooltip("Key del feedback si la respuesta es incorrecta (columna fb_ko_key).")]
    public string fbKoKey;

    [TextArea]
    [Tooltip("Notas de contexto / descripción de la fila (columna Notes de STORY.csv, si existe).")]
    public string notes;

    // ---------------------------------------------------------------------
    // Runtime caches (evita allocs repetidos)
    // ---------------------------------------------------------------------

    [NonSerialized] private string _cachedStartEventName;
    [NonSerialized] private string _cachedEndEventName;

    public string StartEventName
    {
        get
        {
            if (_cachedStartEventName == null)
                _cachedStartEventName = string.IsNullOrEmpty(id) ? string.Empty : string.Concat(id, "_Start");
            return _cachedStartEventName;
        }
    }

    public string EndEventName
    {
        get
        {
            if (_cachedEndEventName == null)
                _cachedEndEventName = string.IsNullOrEmpty(id) ? string.Empty : string.Concat(id, "_End");
            return _cachedEndEventName;
        }
    }
}