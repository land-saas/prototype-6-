using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PortalShift.Editor
{
    /// <summary>Captures live authored content, then generates into a separate additive scene.</summary>
    public static class PortalConnectorGenerator
    {
        const string AuthoredScene = "Assets/PortalShift/Scenes/PortalShift.unity";
        const string AssetFolder = "Assets/PortalShift/Connectors";
        const string ConfigurationPreference = "PortalShift.LastConnectorConfiguration";

        [Serializable] public sealed class GenerationReport
        {
            public string generatedScene, referenceScene, referenceSnapshot, timestamp;
            public int seed, width, height, roomCount, straightCount, cornerCount, tJunctionCount;
            public bool sourceWasDirty, sourceStillDirty, sourceSceneRemainedLoaded, topologyValid;
            public Vector2 spacing, roomBoundsSize;
            public string[] preservedSourceChildren, notes;
        }

        static PortalStage RequireLiveReference()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play before capturing or generating connector rooms.");
            var scene = SceneManager.GetActiveScene();
            if (scene.path != AuthoredScene)
                throw new InvalidOperationException("Keep the authored PortalShift.unity scene active. Generation does not load or replace it.");
            var stage = Resources.FindObjectsOfTypeAll<PortalStage>().Single(s => s.gameObject.scene == scene);
            if (stage.Rooms == null || !stage.Rooms.Any(r => r != null && r.Coordinate == Vector2Int.zero))
                throw new InvalidOperationException("The open reference needs its registered Threshold room at (0,0).");
            return stage;
        }

        static void EnsureAssetFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = path.Substring(0, path.LastIndexOf('/'));
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(path.LastIndexOf('/') + 1));
        }

        static string Snapshot(Scene scene, string reason)
        {
            string folder = "Verification/ConnectorSnapshots/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            Directory.CreateDirectory(folder);
            string path = folder + "/" + reason + ".unity";
            if (!EditorSceneManager.SaveScene(scene, path, true))
                throw new IOException("Could not preserve a copy of the current in-memory reference scene.");
            return path;
        }

        [MenuItem("Portal Shift/Connectors/Create Templates from Open Reference")]
        public static void CreateTemplatesFromOpenReference()
        {
            var stage = RequireLiveReference();
            var reference = stage.gameObject.scene;
            var source = stage.Rooms.Single(r => r.Coordinate == Vector2Int.zero);
            string snapshot = Snapshot(reference, "before-template-capture");
            EnsureAssetFolder(AssetFolder + "/Prefabs");
            var oldSelection = Selection.objects;
            var temporary = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            PortalConnectorConfiguration config = null;
            try
            {
                config = ScriptableObject.CreateInstance<PortalConnectorConfiguration>();
                config.Seed = stage.AttemptSeed;
                config.ReferenceSnapshot = snapshot;
                foreach (var kind in new[] { ConnectorKind.Straight, ConnectorKind.Corner, ConnectorKind.TJunction })
                {
                    var copy = Object.Instantiate(source.gameObject);
                    SceneManager.MoveGameObjectToScene(copy, temporary);
                    copy.name = "Connector " + kind;
                    copy.transform.SetParent(null);
                    copy.transform.position = Vector3.zero;
                    copy.SetActive(true);
                    var room = copy.GetComponent<PortalRoom>();
                    room.Coordinate = Vector2Int.zero;
                    var connector = copy.GetComponent<PortalConnectorRoom>();
                    if (connector == null) connector = copy.AddComponent<PortalConnectorRoom>();
                    connector.Kind = kind;
                    connector.Sockets = CanonicalSockets(kind);
                    connector.MirroredPlatforms = false;
                    connector.CapturedFrom = reference.path + " / " + source.name + " at " + DateTime.UtcNow.ToString("o");
                    SetSocketMarkers(room, connector);
                    string path = AssetDatabase.GenerateUniqueAssetPath(AssetFolder + "/Prefabs/Connector" + kind + ".prefab");
                    var prefab = PrefabUtility.SaveAsPrefabAsset(copy, path);
                    if (prefab == null) throw new IOException("Could not create " + kind + " connector prefab.");
                    if (kind == ConnectorKind.Straight) config.Straight = prefab;
                    if (kind == ConnectorKind.Corner) config.Corner = prefab;
                    if (kind == ConnectorKind.TJunction) config.TJunction = prefab;
                    Object.DestroyImmediate(copy);
                }
                string configurationPath = AssetDatabase.GenerateUniqueAssetPath(AssetFolder + "/PortalConnectorConfiguration.asset");
                AssetDatabase.CreateAsset(config, configurationPath);
                AssetDatabase.SaveAssetIfDirty(config);
                EditorPrefs.SetString(ConfigurationPreference, configurationPath);
                Debug.Log("PORTAL_CONNECTOR_TEMPLATES_CREATED " + configurationPath + ". Three socket archetypes retain four wired portal objects and the current authored layout. Reference snapshot: " + snapshot);
            }
            finally
            {
                EditorSceneManager.CloseScene(temporary, true);
                SceneManager.SetActiveScene(reference);
                Selection.objects = oldSelection.Where(o => o != null).ToArray();
            }
            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
        }

        static ConnectorSockets CanonicalSockets(ConnectorKind kind)
        {
            if (kind == ConnectorKind.Straight) return ConnectorSockets.East | ConnectorSockets.West;
            if (kind == ConnectorKind.Corner) return ConnectorSockets.North | ConnectorSockets.East;
            return ConnectorSockets.North | ConnectorSockets.East | ConnectorSockets.West;
        }

        [MenuItem("Portal Shift/Connectors/Select Configuration")]
        public static void SelectConfiguration()
        {
            var config = FindConfiguration();
            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
        }

        static PortalConnectorConfiguration FindConfiguration()
        {
            if (Selection.activeObject is PortalConnectorConfiguration selected) return selected;
            string path = EditorPrefs.GetString(ConfigurationPreference, AssetFolder + "/PortalConnectorConfiguration.asset");
            var config = AssetDatabase.LoadAssetAtPath<PortalConnectorConfiguration>(path);
            if (config == null) throw new InvalidOperationException("Create connector templates from the open reference, then select the configuration asset.");
            return config;
        }

        [MenuItem("Portal Shift/Connectors/Generate Separate Preview Scene")]
        public static void GenerateSelectedConfiguration() => Generate(FindConfiguration());

        public static string Generate(PortalConnectorConfiguration config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var sourceStage = RequireLiveReference();
            if (config.Width < 2 || config.Height < 2 || config.Width > 4 || config.Height > 4)
                throw new InvalidOperationException("Connector preview dimensions must each be between 2 and 4.");
            if (!PortalConnectorRules.TryGenerate(config.Width, config.Height, config.Seed, out var cells, out string error))
                throw new InvalidOperationException(error);
            foreach (var kind in new[] { ConnectorKind.Straight, ConnectorKind.Corner, ConnectorKind.TJunction })
            {
                var template = config.TemplateFor(kind);
                if (template == null || template.GetComponent<PortalRoom>() == null || template.GetComponent<PortalConnectorRoom>() == null)
                    throw new InvalidOperationException("Assign a PortalRoom prefab with PortalConnectorRoom for " + kind + ".");
                var portals = template.GetComponentsInChildren<ShiftPortal>(true);
                if (portals.Length != 4 || portals.Select(p => p.Shape).Distinct().Count() != 4)
                    throw new InvalidOperationException(kind + " template must retain one of each of the four portal shapes.");
            }
            var reference = sourceStage.gameObject.scene;
            bool sourceDirty = reference.isDirty;
            var sourceThreshold = sourceStage.Rooms.Single(r => r.Coordinate == Vector2Int.zero);
            string snapshot = Snapshot(reference, "before-generation");
            var oldSelection = Selection.objects;
            EnsureAssetFolder(AssetFolder + "/Generated");
            string scenePath = AssetDatabase.GenerateUniqueAssetPath(AssetFolder + "/Generated/PortalShift_" + config.Width + "x" + config.Height + "_Seed" + config.Seed + ".unity");
            var temporary = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            string generatedPath = null;
            try
            {
                var objectMap = new Dictionary<Object, Object>();
                var roots = new[] { sourceStage.transform.root.gameObject, sourceStage.Player.transform.root.gameObject, sourceStage.RoomCamera.transform.root.gameObject }.Distinct().ToArray();
                foreach (var root in roots)
                {
                    if (root.GetComponentsInChildren<PortalRoom>(true).Length != 0)
                        throw new InvalidOperationException("Keep Stage, Player and Camera outside room roots before generating.");
                    var copy = Object.Instantiate(root);
                    copy.name = root.name;
                    SceneManager.MoveGameObjectToScene(copy, temporary);
                    MapTree(root.transform, copy.transform, objectMap);
                }
                var stage = (PortalStage)objectMap[sourceStage];
                var parent = new GameObject("Generated Rooms");
                SceneManager.MoveGameObjectToScene(parent, temporary);
                var roomBounds = UnionTemplateBounds(config);
                float gap = Mathf.Max(.1f, config.Gap);
                Vector2 spacing = new Vector2(roomBounds.size.x + gap, roomBounds.size.y + gap);
                var rooms = new PortalRoom[cells.Length];
                foreach (var cell in cells)
                {
                    var template = config.TemplateFor(cell.Kind);
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(template, temporary);
                    instance.transform.SetParent(parent.transform, false);
                    instance.transform.localPosition = new Vector3(cell.X * spacing.x, cell.Y * spacing.y, 0f);
                    instance.transform.localRotation = Quaternion.identity;
                    instance.name = "Room " + cell.X + "," + cell.Y + " - " + cell.Kind;
                    instance.SetActive(true);
                    var room = instance.GetComponent<PortalRoom>();
                    room.Coordinate = new Vector2Int(cell.X, cell.Y);
                    room.DisplayName = cell.Kind.ToString();
                    if (room.CameraAnchor != null) room.CameraAnchor.localPosition = roomBounds.center;
                    var connector = instance.GetComponent<PortalConnectorRoom>();
                    connector.Kind = cell.Kind;
                    connector.Sockets = cell.Sockets;
                    bool mirror = config.MirrorPlatformVariants && ShouldMirror(config.Seed, cell.X, cell.Y);
                    connector.MirroredPlatforms = mirror;
                    if (mirror) MirrorSupportingLedges(room);
                    ConfigureColors(room);
                    SetSocketMarkers(room, connector);
                    foreach (var component in instance.GetComponentsInChildren<Component>(true))
                        if (component != null) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                    rooms[cell.Y * config.Width + cell.X] = room;
                    var original = sourceStage.Rooms.FirstOrDefault(r => r.Coordinate == room.Coordinate);
                    if (original != null) MapMatchingTree(original.transform, room.transform, objectMap);
                }
                // Cross-root references (HUD.Stage, Stage.Player/Camera, UnityEvents)
                // must point at generated objects rather than the live reference.
                foreach (var root in temporary.GetRootGameObjects()) RemapObjectReferences(root, objectMap);
                stage.Rooms = rooms;
                stage.Player = (PortalPlayerController)objectMap[sourceStage.Player];
                stage.RoomCamera = (Camera)objectMap[sourceStage.RoomCamera];
                stage.Width = config.Width;
                stage.Height = config.Height;
                stage.StartCoordinate = Vector2Int.zero;
                stage.Boundary = BoundaryPolicy.Reject;
                stage.UseFixedSeed = true;
                stage.AttemptSeed = config.Seed;
                stage.StartInWholeMap = true;
                stage.MapRoomWidth = roomBounds.size.x;
                stage.MapRoomHeight = roomBounds.size.y;
                stage.RoomViewOrthographicSize = roomBounds.size.y / (2f * .94f);
                stage.Player.Teleport(rooms[0].SpawnPosition);
                FitSavedCamera(stage);
                Physics2D.SyncTransforms();
                var stageReport = PortalConnectorChecks.ValidateStage(stage, true);
                Directory.CreateDirectory("Verification");
                File.WriteAllText("Verification/connector-generated-stage.json", JsonUtility.ToJson(stageReport, true));
                if (!stageReport.passed)
                    throw new InvalidOperationException("Generated scene failed structural checks: " + string.Join("; ", stageReport.failures));
                if (!EditorSceneManager.SaveScene(temporary, scenePath)) throw new IOException("Could not save generated scene.");
                generatedPath = scenePath;
                var report = new GenerationReport
                {
                    generatedScene = scenePath, referenceScene = reference.path, referenceSnapshot = snapshot,
                    timestamp = DateTime.UtcNow.ToString("o"), seed = config.Seed, width = config.Width, height = config.Height,
                    roomCount = rooms.Length, straightCount = cells.Count(c => c.Kind == ConnectorKind.Straight),
                    cornerCount = cells.Count(c => c.Kind == ConnectorKind.Corner), tJunctionCount = cells.Count(c => c.Kind == ConnectorKind.TJunction),
                    sourceWasDirty = sourceDirty, sourceStillDirty = reference.isDirty, sourceSceneRemainedLoaded = reference.isLoaded,
                    topologyValid = PortalConnectorRules.ValidateTopology(config.Width, config.Height, cells, out error),
                    spacing = spacing, roomBoundsSize = roomBounds.size,
                    preservedSourceChildren = sourceThreshold.transform.Cast<Transform>().Select(t => t.name).ToArray(),
                    notes = new[]
                    {
                        "The reference remains loaded and untouched; no scene is regenerated in place.",
                        "Connector sockets are editable adjacency metadata, independent of four shape/color portals; solid walls stay solid.",
                        "Whole-room transforms are never rotated. Optional variation mirrors supporting ledges horizontally only.",
                        "The above-ceiling Rectangle from the reference is preserved. Its interior physical access requires a separate design decision.",
                        "Topology connectivity is not portal-state reachability or puzzle solvability. Bounded two-cell traversal is still rejected in 2x2."
                    }
                };
                Directory.CreateDirectory("Verification");
                File.WriteAllText("Verification/connector-generation.json", JsonUtility.ToJson(report, true));
                Undo.RecordObject(config, "Record generated connector scene");
                config.LastGeneratedScene = scenePath;
                EditorUtility.SetDirty(config);
                AssetDatabase.SaveAssetIfDirty(config);
                Debug.Log("PORTAL_CONNECTOR_SCENE_GENERATED " + scenePath + "; rooms=" + rooms.Length + "; socket topology valid=" + report.topologyValid + ". Reference remains open. Rectangle above ceiling remains an access limitation.");
            }
            finally
            {
                EditorSceneManager.CloseScene(temporary, true);
                SceneManager.SetActiveScene(reference);
                Selection.objects = oldSelection.Where(o => o != null).ToArray();
                var view = SceneView.lastActiveSceneView;
                if (view != null) view.Repaint();
            }
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(generatedPath));
            return generatedPath;
        }

        static Bounds UnionTemplateBounds(PortalConnectorConfiguration config)
        {
            var bounds = LocalContentBounds(config.Straight.GetComponent<PortalRoom>());
            bounds.Encapsulate(LocalContentBounds(config.Corner.GetComponent<PortalRoom>()));
            bounds.Encapsulate(LocalContentBounds(config.TJunction.GetComponent<PortalRoom>()));
            return bounds;
        }

        static Bounds LocalContentBounds(PortalRoom room)
        {
            bool first = true;
            var result = new Bounds(Vector3.zero, Vector3.zero);
            // Bounds on prefab assets can be stale. Derive corners from local
            // geometry rather than querying physics/world renderer bounds.
            foreach (var box in room.GetComponentsInChildren<BoxCollider2D>(true))
                EncapsulateLocalRectangle(room.transform, box.transform, box.offset, box.size, ref result, ref first);
            foreach (var sprite in room.GetComponentsInChildren<SpriteRenderer>(true))
                if (sprite.sprite != null)
                    EncapsulateLocalRectangle(room.transform, sprite.transform, sprite.sprite.bounds.center, sprite.sprite.bounds.size, ref result, ref first);
            if (first) throw new InvalidOperationException("Connector template has no visual/collider geometry.");
            return result;
        }

        static void EncapsulateLocalRectangle(Transform room, Transform item, Vector2 center, Vector2 size, ref Bounds bounds, ref bool first)
        {
            foreach (float x in new[] { -.5f, .5f }) foreach (float y in new[] { -.5f, .5f })
            {
                Vector3 corner = room.InverseTransformPoint(item.TransformPoint(center + Vector2.Scale(size, new Vector2(x, y))));
                corner.z = 0f;
                if (first) { bounds = new Bounds(corner, Vector3.zero); first = false; } else bounds.Encapsulate(corner);
            }
        }

        static void SetSocketMarkers(PortalRoom room, PortalConnectorRoom connector)
        {
            var left = room.transform.Find("Left Wall");
            var right = room.transform.Find("Right Wall");
            var floor = room.transform.Find("Main Floor");
            var roof = room.transform.Find("Ceiling Wall");
            if (left == null || right == null || floor == null || roof == null)
                throw new InvalidOperationException("Keep named Main Floor, Ceiling Wall, Left Wall and Right Wall boundaries in connector templates.");
            float xCenter = (left.localPosition.x + right.localPosition.x) * .5f;
            float yCenter = (floor.localPosition.y + roof.localPosition.y) * .5f;
            connector.NorthSocket = new Vector2(xCenter, roof.localPosition.y);
            connector.SouthSocket = new Vector2(xCenter, floor.localPosition.y);
            connector.EastSocket = new Vector2(right.localPosition.x, yCenter);
            connector.WestSocket = new Vector2(left.localPosition.x, yCenter);
        }

        static bool ShouldMirror(int seed, int x, int y)
        {
            uint bits = unchecked((uint)seed ^ (uint)(x * 73856093) ^ (uint)(y * 19349663));
            bits ^= bits << 13; bits ^= bits >> 17; bits ^= bits << 5;
            return (bits & 1) != 0;
        }

        static void MirrorSupportingLedges(PortalRoom room)
        {
            foreach (var box in room.GetComponentsInChildren<BoxCollider2D>(true))
                if (box.usedByEffector && !box.isTrigger)
                {
                    var position = box.transform.localPosition;
                    position.x = -position.x;
                    box.transform.localPosition = position;
                }
        }

        static void ConfigureColors(PortalRoom room)
        {
            var purple = ((room.Coordinate.x + room.Coordinate.y) & 1) == 0 ? PortalShape.Triangle : PortalShape.Diamond;
            var rules = new PortalRuleState(0);
            foreach (var portal in room.GetComponentsInChildren<ShiftPortal>(true))
            {
                portal.Color = portal.Shape == purple ? PortalColor.Purple : PortalColor.Yellow;
                portal.Refresh(rules);
            }
        }

        static void MapTree(Transform original, Transform copy, Dictionary<Object, Object> map)
        {
            map[original.gameObject] = copy.gameObject;
            var a = original.GetComponents<Component>(); var b = copy.GetComponents<Component>();
            for (int i = 0; i < Mathf.Min(a.Length, b.Length); i++) if (a[i] != null && b[i] != null) map[a[i]] = b[i];
            for (int i = 0; i < original.childCount; i++) MapTree(original.GetChild(i), copy.GetChild(i), map);
        }

        static void MapMatchingTree(Transform original, Transform copy, Dictionary<Object, Object> map)
        {
            map[original.gameObject] = copy.gameObject;
            foreach (var component in original.GetComponents<Component>())
                if (component != null)
                {
                    var match = copy.GetComponent(component.GetType());
                    if (match != null) map[component] = match;
                }
            foreach (Transform child in original)
            {
                var match = copy.Find(child.name);
                if (match == null && child.GetComponent<ShiftPortal>() != null)
                    match = copy.GetComponentsInChildren<ShiftPortal>(true).FirstOrDefault(p => p.Shape == child.GetComponent<ShiftPortal>().Shape)?.transform;
                if (match != null) MapMatchingTree(child, match, map);
            }
        }

        static void RemapObjectReferences(GameObject root, Dictionary<Object, Object> map)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                var serialized = new SerializedObject(component);
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null &&
                        map.TryGetValue(property.objectReferenceValue, out var replacement)) property.objectReferenceValue = replacement;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void FitSavedCamera(PortalStage stage)
        {
            var bounds = new Bounds(stage.Rooms[0].CameraPosition, new Vector3(stage.MapRoomWidth, stage.MapRoomHeight, 0f));
            foreach (var room in stage.Rooms) bounds.Encapsulate(new Bounds(room.CameraPosition, new Vector3(stage.MapRoomWidth, stage.MapRoomHeight, 0f)));
            const float aspect = 16f / 9f;
            const float top = 40f / 720f;
            float size = Mathf.Max(bounds.size.y / (2f * (1f - top - .06f)), bounds.size.x / (2f * aspect * .94f));
            stage.RoomCamera.orthographicSize = size;
            stage.RoomCamera.transform.position = new Vector3(bounds.center.x, bounds.center.y + top * size, -10f);
        }
    }
}
