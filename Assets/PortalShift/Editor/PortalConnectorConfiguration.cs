using UnityEditor;
using UnityEngine;

namespace PortalShift.Editor
{
    [CreateAssetMenu(menuName = "Portal Shift/Connector Configuration", fileName = "PortalConnectorConfiguration")]
    public sealed class PortalConnectorConfiguration : ScriptableObject
    {
        public int Seed = 42606;
        [Range(2, 4)] public int Width = 2;
        [Range(2, 4)] public int Height = 2;
        [Tooltip("Three crew-editable room prefabs. Each retains four separate shape/color portals.")]
        public GameObject Straight;
        public GameObject Corner;
        public GameObject TJunction;
        [Tooltip("Optional seeded horizontal mirroring of supporting ledges only. Off by default to preserve the reference arrangement.")]
        public bool MirrorPlatformVariants;
        [Min(.1f), Tooltip("Empty world-space gap between complete visual/collider room bounds.")]
        public float Gap = .6f;
        [Tooltip("Historical source snapshot. Generation never reloads or modifies that scene.")]
        public string ReferenceSnapshot;
        [HideInInspector] public string LastGeneratedScene;

        public GameObject TemplateFor(ConnectorKind kind)
        {
            switch (kind)
            {
                case ConnectorKind.Straight: return Straight;
                case ConnectorKind.Corner: return Corner;
                case ConnectorKind.TJunction: return TJunction;
                default: return null;
            }
        }
    }

    [CustomEditor(typeof(PortalConnectorConfiguration))]
    public sealed class PortalConnectorConfigurationInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var config = (PortalConnectorConfiguration)target;
            EditorGUILayout.HelpBox("Sockets describe compatible adjacency; they do not replace portal rules or open solid walls. A bounded 2 × 2 contains four Corners. Use 3 × 3 or 4 × 4 for Straight and T-junction variants. Generation saves a NEW scene and keeps your open reference intact.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Generate Separate Preview Scene")) PortalConnectorGenerator.Generate(config);
            if (!string.IsNullOrEmpty(config.LastGeneratedScene))
            {
                EditorGUILayout.SelectableLabel(config.LastGeneratedScene, GUILayout.Height(18));
                if (GUILayout.Button("Select Last Generated Scene Asset"))
                    Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(config.LastGeneratedScene);
            }
        }
    }
}
