using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Base de datos STORY con todas las filas de STORY.csv para todas las escenas.
/// </summary>
[CreateAssetMenu(
    fileName = "StoryDatabase",
    menuName = "Play and Go/Localization/Story Database")]
public class StoryDatabase : ScriptableObject
{
    [Tooltip("Lista de todas las entradas de STORY del proyecto.")]
    public List<StoryEntry> entries = new List<StoryEntry>();
}