using UnityEditor;
using UnityEngine;

namespace PlayGo.TestPackage.Editor
{
    public static class TestPackageMenu
    {
        [MenuItem("PlayGo/Test Package/Log Message")]
        public static void LogMessage()
        {
            Debug.Log("El paquete PlayGo Test Package está instalado correctamente.");
        }
    }
}