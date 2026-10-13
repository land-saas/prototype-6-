using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PortalShift.Editor
{
    /// <summary>Read-only checks. Connector adjacency and portal rules are separate graphs.</summary>
    public static class PortalConnectorChecks
    {
        [Serializable]
        public sealed class StageReport
        {
            public bool passed;
            public int assertions;
            public int roomCount;
            public int portalCount;
            public int mappingsChecked;
            public int minimumReachableRooms;
            public int minimumReachableStates;
            public int maximumReachableStates;
            public string scene;
            public string[] failures;
            public string[] physicalAccessWarnings;
            public string scope = "Read-only authored geometry and reference checks plus logical portal room/phase graph. No physics simulation or human keyboard play.";
        }

        [Serializable]
        sealed class Report
        {
            public bool passed;
            public int assertions;
            public int[] seeds;
            public int topologyLayouts;
            public int portalGraphs;
            public string checkedAtUtc;
            public string unityVersion;
            public string[] completedGroups;
            public string failure;
            public StageReport openScene;
            public string scope = "Automated pure connector and portal-state graph tests. Socket reachability is logical metadata; it does not prove traversable doorways, physical portal access or puzzle solvability.";
        }

        [Serializable]
        sealed class IntegrationCase
        {
            public int seed, width, height;
            public string scene;
            public bool savedSceneExists, sourceFingerprintPreserved;
            public StageReport validation;
            public PortalConnectorGenerator.GenerationReport generation;
        }

        [Serializable]
        sealed class IntegrationReport
        {
            public bool passed, sourceWasDirty, sourceStillDirty, sourceRemainedLoaded;
            public bool sourceFingerprintPreserved, activeScenePreserved, selectionPreserved, configurationPreserved;
            public string checkedAtUtc, unityVersion, referenceScene, configuration, failure;
            public int assertions, stageAssertions;
            public IntegrationCase[] cases;
            public string scope = "Generated and reopened four separate saved preview scenes from actual connector prefabs. Structural geometry, local references and logical portal reachability checked. No runtime physics, human keyboard play, walk-through doorway or puzzle-solvability claim.";
        }

        sealed class Checks
        {
            public int Count;
            public int Layouts;
            public int Graphs;
            public readonly List<string> Groups = new List<string>();
            public void Assert(bool ok, string message)
            {
                Count++;
                if (!ok) throw new InvalidOperationException("Portal connector check failed: " + message);
            }
            public void Group(string name, Action body) { body(); Groups.Add(name); }
        }

        sealed class Node
        {
            public Vector2Int Coordinate;
            public PortalRuleState Rules;
        }

        struct GraphResult
        {
            public int Rooms;
            public int States;
        }

        static readonly int[] Seeds = { 0, 1, 2, 17, 42606, -1, int.MinValue, int.MaxValue };
        static readonly int[] SocketBits = { 1, 2, 4, 8 };
        static readonly int[] OppositeBits = { 4, 8, 1, 2 };
        static readonly Vector2Int[] SocketSteps = { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };

        [MenuItem("Portal Shift/Run Connector Checks")]
        public static void RunFromMenu() { Run(); }

        /// <summary>Pure fixed-seed tests; also inspects an already-open scene without loading or changing it.</summary>
        public static int Run()
        {
            var checks = new Checks();
            var report = new Report { seeds = Seeds, checkedAtUtc = DateTime.UtcNow.ToString("o"), unityVersion = Application.unityVersion };
            try
            {
                checks.Group("Fixed-seed 2x2 and 4x4 topology, determinism and independent reciprocal connectivity", () => CheckSeeds(checks));
                checks.Group("Invalid topologies and dimensions reject safely", () => CheckInvalid(checks));
                checks.Group("All 24 mappings and all starts reach each room using original portal rules", () => CheckPortalGraphs(checks));
                var stage = UnityEngine.Object.FindObjectsByType<PortalStage>(FindObjectsInactive.Include)
                    .FirstOrDefault(s => s.gameObject.scene.IsValid() && s.gameObject.scene.isLoaded);
                if (stage != null) report.openScene = ValidateStage(stage);
                report.passed = true;
                Debug.Log("PORTAL_CONNECTOR_CHECKS_PASSED " + checks.Count + " pure assertions, " + checks.Layouts + " generated topologies, " + checks.Graphs + " logical portal graphs. " + ReportPath());
                return checks.Count;
            }
            catch (Exception error) { report.failure = error.Message; Debug.LogError(error); throw; }
            finally
            {
                report.assertions = checks.Count; report.topologyLayouts = checks.Layouts; report.portalGraphs = checks.Graphs;
                report.completedGroups = checks.Groups.ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath()));
                File.WriteAllText(ReportPath(), JsonUtility.ToJson(report, true));
            }
        }

        static string ReportPath() => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Verification", "connector-checks.json");

        [MenuItem("Portal Shift/Connectors/Run Connector Integration Checks")]
        public static void RunConnectorIntegrationChecks()
        {
            const string configurationPath = "Assets/PortalShift/Connectors/PortalConnectorConfiguration.asset";
            string output = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Verification", "connector-integration-checks.json");
            var source = SceneManager.GetActiveScene();
            var selection = Selection.objects;
            var config = AssetDatabase.LoadAssetAtPath<PortalConnectorConfiguration>(configurationPath);
            var results = new List<IntegrationCase>();
            var report = new IntegrationReport
            {
                checkedAtUtc = DateTime.UtcNow.ToString("o"), unityVersion = Application.unityVersion,
                referenceScene = source.path, configuration = configurationPath, sourceWasDirty = source.isDirty
            };
            string sourceBefore = source.IsValid() && source.isLoaded ? SceneFingerprint(source) : "";
            string configBefore = config != null ? EditorJsonUtility.ToJson(config) : "";
            bool configDirtyBefore = config != null && EditorUtility.IsDirty(config);
            Action<bool, string> check = (ok, message) =>
            {
                report.assertions++;
                if (!ok) throw new InvalidOperationException("Portal connector integration check failed: " + message);
            };
            try
            {
                check(!EditorApplication.isPlayingOrWillChangePlaymode, "Stop Play before checking generated assets.");
                check(source.path == "Assets/PortalShift/Scenes/PortalShift.unity", "Keep the authored reference scene active.");
                check(config != null, "The connector configuration asset must exist.");
                foreach (int size in new[] { 2, 4 }) foreach (int seed in new[] { 42606, 42607 })
                {
                    var testConfig = UnityEngine.Object.Instantiate(config);
                    testConfig.hideFlags = HideFlags.HideAndDontSave;
                    testConfig.Width = size; testConfig.Height = size; testConfig.Seed = seed;
                    Scene generated = default;
                    var result = new IntegrationCase { width = size, height = size, seed = seed };
                    results.Add(result);
                    try
                    {
                        result.scene = PortalConnectorGenerator.Generate(testConfig);
                        result.savedSceneExists = !string.IsNullOrEmpty(result.scene) && File.Exists(result.scene) && AssetDatabase.LoadAssetAtPath<SceneAsset>(result.scene) != null;
                        check(result.savedSceneExists, "The generated scene is saved as an asset.");
                        string generationPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Verification", "connector-generation.json");
                        result.generation = JsonUtility.FromJson<PortalConnectorGenerator.GenerationReport>(File.ReadAllText(generationPath));
                        check(result.generation.generatedScene == result.scene && result.generation.seed == seed && result.generation.width == size && result.generation.height == size && result.generation.topologyValid,
                            "Generation report matches the actual requested fixed-seed asset.");
                        // Reopen only the newly saved preview additively. The dirty authored scene stays loaded and active.
                        generated = EditorSceneManager.OpenScene(result.scene, OpenSceneMode.Additive);
                        SceneManager.SetActiveScene(source);
                        var stages = generated.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<PortalStage>(true)).ToArray();
                        check(stages.Length == 1, "The saved preview has exactly one registered stage.");
                        result.validation = ValidateStage(stages[0], true);
                        report.stageAssertions += result.validation.assertions;
                        check(result.validation.passed, "Saved preview structure: " + string.Join("; ", result.validation.failures));
                        check(result.validation.roomCount == size * size && result.validation.portalCount == size * size * 4 && result.validation.mappingsChecked == 24,
                            "Saved preview retains all registered rooms, four portals per room and 24 checked mappings.");
                        result.sourceFingerprintPreserved = source.isLoaded && SceneFingerprint(source) == sourceBefore;
                        check(result.sourceFingerprintPreserved, "The in-memory authored scene remains unchanged after generation and saved-scene validation.");
                        check(source.isDirty == report.sourceWasDirty, "The authored scene's original dirty state remains unchanged.");
                        check(EditorJsonUtility.ToJson(config) == configBefore && EditorUtility.IsDirty(config) == configDirtyBefore,
                            "The persistent configuration, its default dimensions and seed remain unchanged.");
                    }
                    finally
                    {
                        if (generated.IsValid() && generated.isLoaded) EditorSceneManager.CloseScene(generated, true);
                        if (source.IsValid() && source.isLoaded) SceneManager.SetActiveScene(source);
                        Selection.objects = selection.Where(o => o != null).ToArray();
                        UnityEngine.Object.DestroyImmediate(testConfig);
                    }
                }
                report.passed = true;
                Debug.Log("PORTAL_CONNECTOR_INTEGRATION_PASSED " + results.Count + " separate saved scenes; " + report.stageAssertions + " saved-scene structural assertions. " + output);
            }
            catch (Exception error) { report.failure = error.Message; Debug.LogError(error); throw; }
            finally
            {
                if (source.IsValid() && source.isLoaded) SceneManager.SetActiveScene(source);
                Selection.objects = selection.Where(o => o != null).ToArray();
                report.sourceStillDirty = source.IsValid() && source.isLoaded && source.isDirty;
                report.sourceRemainedLoaded = source.IsValid() && source.isLoaded;
                report.sourceFingerprintPreserved = report.sourceRemainedLoaded && SceneFingerprint(source) == sourceBefore;
                report.activeScenePreserved = SceneManager.GetActiveScene() == source;
                report.selectionPreserved = Selection.objects.SequenceEqual(selection.Where(o => o != null));
                report.configurationPreserved = config != null && EditorJsonUtility.ToJson(config) == configBefore && EditorUtility.IsDirty(config) == configDirtyBefore;
                report.cases = results.ToArray();
                report.passed &= report.sourceFingerprintPreserved && report.activeScenePreserved && report.selectionPreserved && report.configurationPreserved && report.sourceStillDirty == report.sourceWasDirty;
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.WriteAllText(output, JsonUtility.ToJson(report, true));
            }
        }

        static string SceneFingerprint(Scene scene)
        {
            var parts = new List<string>();
            foreach (var root in scene.GetRootGameObjects().OrderBy(r => r.transform.GetSiblingIndex()))
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    parts.Add(EditorJsonUtility.ToJson(transform.gameObject));
                    foreach (var component in transform.GetComponents<Component>())
                        if (component != null) parts.Add(EditorJsonUtility.ToJson(component));
                }
            return string.Join("\n", parts);
        }

        static void CheckSeeds(Checks c)
        {
            var largeSignatures = new HashSet<string>();
            var kinds = new HashSet<ConnectorKind>();
            foreach (int size in new[] { 2, 4 }) foreach (int seed in Seeds)
            {
                ConnectorCell[] first, second; string error;
                c.Assert(PortalConnectorRules.TryGenerate(size, size, seed, out first, out error), size + "x" + size + " seed " + seed + ": " + error);
                c.Assert(PortalConnectorRules.TryGenerate(size, size, seed, out second, out error), "repeated generation succeeds");
                c.Assert(Signature(first) == Signature(second), "seed determinism");
                c.Assert(PortalConnectorRules.ValidateTopology(size, size, first, out error), "generated topology validates: " + error);
                CheckTopologyIndependently(c, size, size, first);
                if (size == 2) c.Assert(first.All(cell => cell.Kind == ConnectorKind.Corner), "a bounded 2x2 requires four corner rooms");
                else { largeSignatures.Add(Signature(first)); foreach (var cell in first) kinds.Add(cell.Kind); }
                c.Layouts++;
            }
            c.Assert(largeSignatures.Count > 1, "4x4 seeds produce different compatible layouts");
            c.Assert(kinds.Count == 3, "the fixed-seed 4x4 sample covers all three connector archetypes");
        }

        static string Signature(ConnectorCell[] cells) => string.Join("|", cells.OrderBy(c => c.Y).ThenBy(c => c.X).Select(c => c.X + "," + c.Y + ":" + (int)c.Sockets + ":" + (int)c.Kind));

        static void CheckTopologyIndependently(Checks c, int width, int height, ConnectorCell[] cells)
        {
            c.Assert(cells.Length == width * height, "one cell per coordinate");
            var map = new Dictionary<Vector2Int, ConnectorCell>();
            foreach (var cell in cells)
            {
                var coordinate = new Vector2Int(cell.X, cell.Y);
                c.Assert(cell.X >= 0 && cell.Y >= 0 && cell.X < width && cell.Y < height, "coordinate in bounds");
                c.Assert(!map.ContainsKey(coordinate), "unique coordinates"); map.Add(coordinate, cell);
                int mask = (int)cell.Sockets;
                int degree = SocketBits.Count(bit => (mask & bit) != 0);
                c.Assert((mask & ~15) == 0 && (degree == 2 || degree == 3), "degree is two or three cardinal sockets");
                var kind = degree == 3 ? ConnectorKind.TJunction : mask == 5 || mask == 10 ? ConnectorKind.Straight : ConnectorKind.Corner;
                c.Assert(cell.Kind == kind, "independent archetype classification");
            }
            foreach (var pair in map) for (int d = 0; d < 4; d++)
            {
                bool open = (((int)pair.Value.Sockets) & SocketBits[d]) != 0;
                ConnectorCell neighbor;
                bool exists = map.TryGetValue(pair.Key + SocketSteps[d], out neighbor);
                c.Assert(exists ? open == ((((int)neighbor.Sockets) & OppositeBits[d]) != 0) : !open, "reciprocal edge or closed boundary");
            }
            var visited = new HashSet<Vector2Int>(); var queue = new Queue<Vector2Int>();
            queue.Enqueue(Vector2Int.zero); visited.Add(Vector2Int.zero);
            while (queue.Count > 0)
            {
                var coordinate = queue.Dequeue();
                for (int d = 0; d < 4; d++) if ((((int)map[coordinate].Sockets) & SocketBits[d]) != 0)
                {
                    var next = coordinate + SocketSteps[d];
                    if (visited.Add(next)) queue.Enqueue(next);
                }
            }
            c.Assert(visited.Count == cells.Length, "independent BFS connects every room");
        }

        static void CheckInvalid(Checks c)
        {
            ConnectorCell[] cells; string error;
            foreach (var dimension in new[] { new Vector2Int(0, 2), new Vector2Int(2, 0), new Vector2Int(-1, 2), new Vector2Int(1, 4), new Vector2Int(4, 1), new Vector2Int(int.MaxValue, int.MaxValue) })
            {
                c.Assert(!PortalConnectorRules.TryGenerate(dimension.x, dimension.y, 42, out cells, out error), "unsupported dimensions reject");
                c.Assert(cells == null && !string.IsNullOrEmpty(error), "rejection supplies no partial map and explains why");
            }
            c.Assert(PortalConnectorRules.TryGenerate(2, 2, 42, out cells, out error), "valid fixture");
            c.Assert(!PortalConnectorRules.ValidateTopology(2, 2, null, out error), "null topology rejected");
            var bad = (ConnectorCell[])cells.Clone(); bad[1] = bad[0];
            c.Assert(!PortalConnectorRules.ValidateTopology(2, 2, bad, out error), "duplicate coordinate rejected");
            bad = (ConnectorCell[])cells.Clone(); bad[0].Sockets |= ConnectorSockets.West;
            bad[0].Kind = ConnectorKind.TJunction;
            c.Assert(!PortalConnectorRules.ValidateTopology(2, 2, bad, out error), "out-of-bounds socket rejected");
            bad = (ConnectorCell[])cells.Clone(); bad[0].Kind = ConnectorKind.Straight;
            c.Assert(!PortalConnectorRules.ValidateTopology(2, 2, bad, out error), "incorrect archetype rejected");
            ConnectorCell[] larger;
            c.Assert(PortalConnectorRules.TryGenerate(4, 4, 42, out larger, out error), "larger reciprocal fixture");
            bad = (ConnectorCell[])larger.Clone();
            bad[5].Sockets = ConnectorSockets.North | ConnectorSockets.South;
            bad[5].Kind = ConnectorKind.Straight;
            // Its neighbors retain the original sockets, so changing a legal interior mask breaks reciprocity.
            if (bad[5].Sockets == larger[5].Sockets) bad[5].Sockets = ConnectorSockets.East | ConnectorSockets.West;
            c.Assert(!PortalConnectorRules.ValidateTopology(4, 4, bad, out error), "non-reciprocal socket rejected");
            Array.Reverse(cells);
            c.Assert(PortalConnectorRules.ValidateTopology(2, 2, cells, out error), "validator accepts a reordered complete map");
        }

        static void CheckPortalGraphs(Checks c)
        {
            foreach (int size in new[] { 2, 4 })
            {
                var portals = new Dictionary<Vector2Int, PortalColor[]>();
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                {
                    var colors = new[] { PortalColor.Yellow, PortalColor.Yellow, PortalColor.Yellow, PortalColor.Yellow };
                    colors[(x + y) % 2 == 0 ? 0 : 1] = PortalColor.Purple;
                    portals.Add(new Vector2Int(x, y), colors);
                }
                foreach (var mapping in Mappings()) foreach (var start in portals.Keys)
                {
                    var result = EvaluatePortalGraph(size, size, start, portals, mapping, BoundaryPolicy.Reject);
                    c.Assert(result.Rooms == size * size, "all rooms logically reachable for each shape mapping and starting coordinate");
                    c.Assert(size != 2 || result.States == 4, "bounded 2x2 preserves phase zero because exact two-cell destinations are outside");
                    c.Graphs++;
                }
            }
        }

        static IEnumerable<CardinalDirection[]> Mappings()
        {
            for (int a = 0; a < 4; a++) for (int b = 0; b < 4; b++) for (int c = 0; c < 4; c++) for (int d = 0; d < 4; d++)
                if (a != b && a != c && a != d && b != c && b != d && c != d)
                    yield return new[] { (CardinalDirection)a, (CardinalDirection)b, (CardinalDirection)c, (CardinalDirection)d };
        }

        static GraphResult EvaluatePortalGraph(int width, int height, Vector2Int start,
            Dictionary<Vector2Int, PortalColor[]> portals, CardinalDirection[] mapping, BoundaryPolicy boundary)
        {
            var initial = new PortalRuleState(42606) { BoundaryPolicy = boundary }; initial.SetDirections(mapping);
            var queue = new Queue<Node>(); queue.Enqueue(new Node { Coordinate = start, Rules = initial });
            var states = new HashSet<string> { StateKey(start, 0) }; var rooms = new HashSet<Vector2Int> { start };
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                for (int shape = 0; shape < 4; shape++)
                {
                    // Clone the serialized pure rule state, including its current phase. No scene traversal or synthetic phase override.
                    var nextRules = JsonUtility.FromJson<PortalRuleState>(JsonUtility.ToJson(node.Rules));
                    Vector2Int destination; string reason;
                    if (!nextRules.TryTraverse(node.Coordinate, (PortalShape)shape, portals[node.Coordinate][shape], width, height, portals.Keys, out destination, out reason)) continue;
                    if (states.Add(StateKey(destination, nextRules.Phase)))
                    { rooms.Add(destination); queue.Enqueue(new Node { Coordinate = destination, Rules = nextRules }); }
                }
            }
            return new GraphResult { Rooms = rooms.Count, States = states.Count };
        }

        static string StateKey(Vector2Int coordinate, int phase) => coordinate.x + "," + coordinate.y + ":" + phase;

        /// <summary>Inspect the provided scene only. Warnings are known physical limitations, separate from structural validity.</summary>
        public static StageReport ValidateStage(PortalStage stage, bool requireSeparatedRooms = false)
        {
            var report = new StageReport { scene = stage != null ? stage.gameObject.scene.path : "", minimumReachableRooms = int.MaxValue, minimumReachableStates = int.MaxValue };
            var failures = new List<string>(); var warnings = new List<string>();
            Action<bool, string> check = (ok, message) => { report.assertions++; if (!ok) failures.Add(message); };
            check(stage != null, "A PortalStage is required.");
            if (stage == null) return Finish(report, failures, warnings);
            check(stage.Player != null && stage.RoomCamera != null, "Stage Player and RoomCamera references are assigned.");
            var circle = stage.Player != null ? stage.Player.GetComponent<CircleCollider2D>() : null;
            check(circle != null, "Player retains its CircleCollider2D.");
            float radius = circle != null ? circle.radius * Mathf.Max(Mathf.Abs(circle.transform.lossyScale.x), Mathf.Abs(circle.transform.lossyScale.y)) : .42f;
            var rooms = stage.Rooms ?? Array.Empty<PortalRoom>(); report.roomCount = rooms.Length;
            check(rooms.Length == stage.Width * stage.Height, "Every grid coordinate has a registered room.");
            var coordinates = new HashSet<Vector2Int>(); var colorsByRoom = new Dictionary<Vector2Int, PortalColor[]>();
            var contentBounds = new List<Bounds>();
            foreach (var room in rooms)
            {
                check(room != null, "Registered room is non-null."); if (room == null) continue;
                check(coordinates.Add(room.Coordinate), room.name + ": unique coordinate.");
                check(room.Coordinate.x >= 0 && room.Coordinate.y >= 0 && room.Coordinate.x < stage.Width && room.Coordinate.y < stage.Height, room.name + ": coordinate inside grid.");
                check(room.Spawn != null && room.Spawn.IsChildOf(room.transform), room.name + ": local spawn reference.");
                check(room.CameraAnchor != null && room.CameraAnchor.IsChildOf(room.transform), room.name + ": local camera anchor reference.");
                var boxes = room.GetComponentsInChildren<BoxCollider2D>(true).Where(b => b.enabled && !b.isTrigger).ToArray();
                var portals = room.GetComponentsInChildren<ShiftPortal>(true); report.portalCount += portals.Length;
                check(portals.Length == 4 && portals.Select(p => p.Shape).Distinct().Count() == 4, room.name + ": four distinct portal shapes retained.");
                check(portals.Count(p => p.Color == PortalColor.Yellow) == 3 && portals.Count(p => p.Color == PortalColor.Purple) == 1, room.name + ": three yellow and one purple portal.");
                foreach (var portal in portals)
                {
                    var trigger = portal.GetComponent<BoxCollider2D>();
                    check(portal.Room == room && portal.enabled && portal.gameObject.activeSelf && trigger != null && trigger.enabled && trigger.isTrigger, room.name + "/" + portal.name + ": functioning local trigger setup.");
                    check(portal.ShapeRenderer != null && portal.ShapeRenderer.transform.IsChildOf(room.transform) && portal.ShapeRenderer.sprite != null, room.name + "/" + portal.name + ": local visible renderer reference.");
                    check(portal.WallAttachment != null && portal.WallAttachment.transform.IsChildOf(room.transform), room.name + "/" + portal.name + ": local attachment reference.");
                    check(portal.DistancePips != null && portal.DistancePips.Length == 2 && portal.DistancePips.All(p => p != null && p.transform.IsChildOf(room.transform)), room.name + "/" + portal.name + ": two local non-color distance cues.");
                    check(portal.ShapeSprites != null && portal.ShapeSprites.Length == 4 && portal.ShapeSprites.All(s => s != null), room.name + "/" + portal.name + ": complete shape artwork references.");
                    if (trigger != null && room.Spawn != null) check(SquaredDistance(BoxBounds(trigger), room.SpawnPosition) > portal.InteractionReach * portal.InteractionReach, room.name + "/" + portal.name + ": arrival does not touch portal interaction range.");
                }
                if (portals.Length == 4 && portals.Select(p => p.Shape).Distinct().Count() == 4 && !colorsByRoom.ContainsKey(room.Coordinate))
                    colorsByRoom.Add(room.Coordinate, portals.OrderBy(p => (int)p.Shape).Select(p => p.Color).ToArray());
                if (room.Spawn != null) CheckSpawn(check, room, boxes, radius);
                var ceiling = room.transform.Find("Ceiling Wall");
                var roof = ceiling != null ? ceiling.GetComponent<BoxCollider2D>() : null;
                if (roof != null)
                {
                    var roofBounds = BoxBounds(roof);
                    foreach (var portal in portals)
                    {
                        var trigger = portal.GetComponent<BoxCollider2D>(); if (trigger == null) continue;
                        var triggerBounds = BoxBounds(trigger);
                        if (portal.transform.position.y > roofBounds.max.y && triggerBounds.min.y - portal.InteractionReach > roofBounds.min.y - radius)
                            warnings.Add(room.name + "/" + portal.name + ": artwork and interaction range are above the solid ceiling; cannot be reached from this room's interior. Explicit above-ceiling placement preserved.");
                    }
                }
                var bounds = ContentBounds(room); contentBounds.Add(bounds);
                var cameraBounds = new Bounds(room.CameraPosition, new Vector3(stage.MapRoomWidth, stage.MapRoomHeight, 100f));
                check(ContainsXY(cameraBounds, ContentBounds(room, false)), room.name + ": baked room camera bounds include all authored artwork and solid colliders.");
            }
            check(coordinates.Contains(stage.StartCoordinate), "StartCoordinate is registered.");
            if (requireSeparatedRooms) for (int i = 0; i < contentBounds.Count; i++) for (int j = i + 1; j < contentBounds.Count; j++)
                check(!OverlapXY(contentBounds[i], contentBounds[j]), "Generated rooms " + i + " and " + j + " have non-overlapping authored content bounds.");
            if (colorsByRoom.Count == rooms.Length && coordinates.Contains(stage.StartCoordinate))
            {
                foreach (var mapping in Mappings())
                {
                    var graph = EvaluatePortalGraph(stage.Width, stage.Height, stage.StartCoordinate, colorsByRoom, mapping, stage.Boundary);
                    report.mappingsChecked++;
                    report.minimumReachableRooms = Mathf.Min(report.minimumReachableRooms, graph.Rooms);
                    report.minimumReachableStates = Mathf.Min(report.minimumReachableStates, graph.States);
                    report.maximumReachableStates = Mathf.Max(report.maximumReachableStates, graph.States);
                    check(graph.Rooms == rooms.Length, "Logical portal graph reaches all rooms for mapping " + string.Join(",", mapping) + ".");
                }
            }
            return Finish(report, failures, warnings);
        }

        static StageReport Finish(StageReport report, List<string> failures, List<string> warnings)
        {
            report.failures = failures.ToArray(); report.physicalAccessWarnings = warnings.ToArray(); report.passed = failures.Count == 0;
            if (report.minimumReachableRooms == int.MaxValue) report.minimumReachableRooms = 0;
            if (report.minimumReachableStates == int.MaxValue) report.minimumReachableStates = 0;
            return report;
        }

        static void CheckSpawn(Action<bool, string> check, PortalRoom room, BoxCollider2D[] boxes, float radius)
        {
            Vector2 spawn = room.SpawnPosition;
            bool clear = boxes.All(b => SquaredDistance(BoxBounds(b), spawn) >= (radius - .01f) * (radius - .01f));
            check(clear, room.name + ": spawn circle does not penetrate a solid collider.");
            bool support = boxes.Select(BoxBounds).Any(b => spawn.x >= b.min.x + radius * .5f && spawn.x <= b.max.x - radius * .5f &&
                spawn.y - radius >= b.max.y - .02f && spawn.y - radius - b.max.y <= 1f);
            check(support, room.name + ": a nearby upward landing supports the spawn after gravity settles it (geometry only).");
        }

        static Bounds BoxBounds(BoxCollider2D box)
        {
            var bounds = new Bounds(box.transform.TransformPoint(box.offset), Vector3.zero);
            foreach (int x in new[] { -1, 1 }) foreach (int y in new[] { -1, 1 })
                bounds.Encapsulate(box.transform.TransformPoint(box.offset + Vector2.Scale(box.size * .5f, new Vector2(x, y))));
            return bounds;
        }

        static Bounds ContentBounds(PortalRoom room, bool includeTriggers = true)
        {
            var bounds = new Bounds(room.transform.position, Vector3.zero);
            foreach (var box in room.GetComponentsInChildren<BoxCollider2D>(true).Where(b => b.enabled && (includeTriggers || !b.isTrigger))) bounds.Encapsulate(BoxBounds(box));
            foreach (var renderer in room.GetComponentsInChildren<SpriteRenderer>(true).Where(r => r.sprite != null))
            {
                var local = renderer.sprite.bounds;
                foreach (int x in new[] { -1, 1 }) foreach (int y in new[] { -1, 1 })
                    bounds.Encapsulate(renderer.transform.TransformPoint(local.center + Vector3.Scale(local.extents, new Vector3(x, y, 0))));
            }
            return bounds;
        }

        static float SquaredDistance(Bounds bounds, Vector2 point)
        {
            float x = Mathf.Max(bounds.min.x - point.x, 0f, point.x - bounds.max.x);
            float y = Mathf.Max(bounds.min.y - point.y, 0f, point.y - bounds.max.y);
            return x * x + y * y;
        }
        static bool ContainsXY(Bounds outer, Bounds inner) => inner.min.x >= outer.min.x - .025f && inner.max.x <= outer.max.x + .025f && inner.min.y >= outer.min.y - .025f && inner.max.y <= outer.max.y + .025f;
        static bool OverlapXY(Bounds a, Bounds b) => Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x) > .01f && Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y) > .01f;
    }
}
