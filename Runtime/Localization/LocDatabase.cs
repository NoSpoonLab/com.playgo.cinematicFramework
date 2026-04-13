using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Entrada de localización: una key y sus textos en distintos idiomas.
/// </summary>
[Serializable]
public class LocEntry
{
    [Tooltip("Identificador único de la cadena (coincide con la columna Key de LOC.csv).")]
    public string key;

    [TextArea]
    [Tooltip("Texto en español (es-ES).")]
    public string esES;

    [TextArea]
    [Tooltip("Texto en inglés (en-GB).")]
    public string enGB;

    [TextArea]
    [Tooltip("Notas de contexto, uso, escena, etc. (columna Notes de LOC.csv, solo para referencia).")]
    public string notes;
}

/// <summary>
/// Base de datos de localización que se rellenará desde LOC.csv.
/// Contiene TODAS las claves de todas las escenas.
/// </summary>
[CreateAssetMenu(
    fileName = "LocDatabase",
    menuName = "Play and Go/Localization/Loc Database")]
public class LocDatabase : ScriptableObject
{
    [Tooltip("Lista de todas las entradas de localización del proyecto.")]
    public List<LocEntry> entries = new List<LocEntry>();
}