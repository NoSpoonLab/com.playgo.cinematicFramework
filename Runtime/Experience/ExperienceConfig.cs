using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(
    fileName = "ExperienceConfig",
    menuName = "PlayAndGo/Experience/Config"
)]
public class ExperienceConfig : ScriptableObject
{
    [Header("Player Placements (Allowed Destinations)")]
    public List<PlayerPlacement> playerPlacements = new();

    [Header("Localization")]
    public Enums.Language startLanguage = Enums.Language.None;

    [Header("Experience Text")]
    [Tooltip("Controla si se muestra texto durante la experiencia.")]
    public bool showExperienceText = true;

    [Header("Audio")]
    [Range(0f, 1f)]
    public float masterVolume = 1f;

    [Header("Story Auto Next")]
    [Tooltip("Segundos estimados por carácter para calcular la permanencia mínima de entradas LINE automáticas.")]
    [Min(0f)]
    public float autoNextSecondsPerCharacter = 0.035f;

    [Tooltip("Duración mínima base para entradas LINE automáticas, aunque el texto sea corto.")]
    [Min(0f)]
    public float autoNextMinDurationSeconds = 1.2f;

    [Tooltip("Cuando hay auto-next por VO, espera duración del audio + este extra (segundos).")]
    [Min(0f)]
    public float autoNextExtraDelaySeconds = 0.35f;

    [Header("Story Debug")]
    [Tooltip("Si está activo, el auto-next usa SOLO un delay fijo (ignora duración de audio).")]
    public bool debugUseFixedAutoNextDelayOnly = false;

    [Tooltip("Delay fijo del auto-next en modo debug.")]
    [Min(0f)]
    public float debugFixedAutoNextDelaySeconds = 1.0f;

    [Tooltip("Si está activo, marca y selecciona automáticamente la opción correcta en preguntas tras X segundos.")]
    public bool debugMarkCorrectOptionAfterDelay = false;

    [Tooltip("Segundos de espera antes de marcar la opción correcta en preguntas.")]
    [Min(0f)]
    public float debugMarkCorrectOptionDelaySeconds = 2.0f;

    [Header("Wrong Answer Flow")]
    [Tooltip("Defines which narrative flow to use when the user answers a question incorrectly.")]
    public Enums.WrongAnswerFlowMode wrongAnswerFlowMode = Enums.WrongAnswerFlowMode.GoToPreviousEntry;

    public bool TryGetPlacementID(
        Enums.PlacementContext context,
        out string placementPointID
    )
    {
        foreach (var placement in playerPlacements)
        {
            if (placement.context == context &&
                !string.IsNullOrWhiteSpace(placement.placementPointID))
            {
                placementPointID = placement.placementPointID;
                return true;
            }
        }

        placementPointID = null;
        return false;
    }

    public bool ContainsPlacementID(string placementID)
    {
        foreach (var placement in playerPlacements)
        {
            if (placement.placementPointID == placementID)
                return true;
        }

        return false;
    }
}
