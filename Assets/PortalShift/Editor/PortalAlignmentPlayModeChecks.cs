using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PortalShift.Editor
{
    /// <summary>
    /// Explicit Play Mode checks against the currently open, possibly unsaved
    /// authored scene. Never saves, repairs, rebuilds, or reloads an asset.
    /// </summary>
    public static class PortalAlignmentPlayModeChecks
    {
        const string ScenePath = "Assets/PortalShift/Scenes/PortalShift.unity";
        const string PendingKey = "PortalShift.AlignmentPlay.Pending";
        const string BaselineKey = "PortalShift.AlignmentPlay.Baseline";
        const string ReportKey = "PortalShift.AlignmentPlay.Report";
        const string ReturningKey = "PortalShift.AlignmentPlay.Returning";
        const string ReportPath = "Verification/portal-alignment-play-mode.json";

        [Serializable] sealed class Pose
        {
            public string path;
            public Vector3 position, scale;
            public Quaternion rotation;
            public Pose() { }
            public Pose(Transform t)
            { path = ObjectPath(t); position = t.localPosition; scale = t.localScale; rotation = t.localRotation; }
            public bool Matches(Transform t) => t != null && Vector3.Distance(position, t.localPosition) < .00001f &&
                Vector3.Distance(scale, t.localScale) < .00001f && Quaternion.Angle(rotation, t.localRotation) < .001f;
        }
        [Serializable] sealed class Baseline
        {
            public string scene, savedHash, createdUtc;
            public bool dirty;
            public Pose[] roomTransforms;
            public string[] displacedPortals;
        }
        [Serializable] sealed class Check
        {
            public string name, evidence;
            public bool passed;
        }
        [Serializable] sealed class Report
        {
            public string startedUtc, finishedUtc, scene, unityVersion, savedHashBefore, savedHashAfter;
            public string method = "Explicit automated Editor Play Mode checks on the open authored scene. Uses actual FixedUpdate physics, real ShiftPortal.CanInteract and PortalStage.TryUsePortal; Teleport arranges isolated interaction and arrival probes. Phase-cue tests pass isolated rule states to the actual live portal components. No human keyboard-play, complete-puzzle or new-build claim. No scene or prefab is saved by this harness.";
            public int passedCount, failedCount, fixedTickObservations, fixtureSeed;
            public bool passed, sourceWasDirty, sourceStillDirty, savedSceneUnchanged, editTransformsPreserved;
            public List<Check> checks = new List<Check>();
        }

        static readonly Stack<IEnumerator> routines = new Stack<IEnumerator>();
        static Baseline baseline;
        static Report report;
        static PortalStage stage;
        static PortalPlayerController player;
        static Rigidbody2D body;
        static bool running, waitingFixed, waitingFrame, settled, previousRunInBackground;
        static float lastFixedTime;
        static int lastFrame;
        static double deadline;

        [InitializeOnLoadMethod]
        static void Register()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem("Portal Shift/Repairs/Run Live Portal Alignment Play Mode Checks")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play Mode before explicitly starting these checks.");
            var scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
                throw new InvalidOperationException("Keep the authored PortalShift.unity scene active; this harness never opens or reloads it.");
            var authoredStage = SceneStage(scene);
            if (authoredStage == null || authoredStage.Player == null)
                throw new InvalidOperationException("The active scene needs its authored stage and player.");
            var portals = authoredStage.Rooms.Where(r => r != null)
                .SelectMany(r => r.GetComponentsInChildren<ShiftPortal>(true)).ToArray();
            baseline = new Baseline
            {
                scene = scene.path, savedHash = Hash(ScenePath), dirty = scene.isDirty, createdUtc = DateTime.UtcNow.ToString("o"),
                roomTransforms = authoredStage.Rooms.Where(r => r != null)
                    .SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => new Pose(t)).ToArray(),
                displacedPortals = portals.Where(p => p.ShapeRenderer != null &&
                    Vector2.Distance(p.ShapeRenderer.transform.position, p.transform.position) > 1.25f)
                    .Select(p => ObjectPath(p.transform)).ToArray()
            };
            SessionState.SetString(BaselineKey, JsonUtility.ToJson(baseline));
            SessionState.SetBool(PendingKey, true);
            SessionState.SetBool(ReturningKey, false);
            Debug.Log("PORTAL_ALIGNMENT_PLAY_CHECKS_REQUESTED: testing the current live scene without saving it.");
            EditorApplication.EnterPlaymode();
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(PendingKey, false))
            {
                SessionState.SetBool(PendingKey, false);
                baseline = JsonUtility.FromJson<Baseline>(SessionState.GetString(BaselineKey, ""));
                report = new Report { startedUtc = DateTime.UtcNow.ToString("o"), scene = baseline.scene,
                    unityVersion = Application.unityVersion, savedHashBefore = baseline.savedHash, sourceWasDirty = baseline.dirty };
                previousRunInBackground = Application.runInBackground;
                Application.runInBackground = true;
                routines.Clear(); routines.Push(Checks());
                waitingFixed = waitingFrame = false;
                running = true; deadline = EditorApplication.timeSinceStartup + 120;
                EditorApplication.update -= Pump; EditorApplication.update += Pump;
            }
            if (state == PlayModeStateChange.ExitingPlayMode && running)
            {
                Record("Play Mode completed without interruption", false, "Play Mode ended before the check routine completed.");
                Finish(false);
            }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(ReturningKey, false))
            {
                SessionState.SetBool(ReturningKey, false);
                baseline = JsonUtility.FromJson<Baseline>(SessionState.GetString(BaselineKey, ""));
                report = JsonUtility.FromJson<Report>(SessionState.GetString(ReportKey, ""));
                var scene = SceneManager.GetActiveScene();
                report.savedHashAfter = Hash(ScenePath);
                report.savedSceneUnchanged = report.savedHashAfter == baseline.savedHash;
                report.sourceStillDirty = scene.isDirty;
                string changed = scene.path == baseline.scene ? "" : "The active scene changed while returning from Play Mode.";
                report.editTransformsPreserved = scene.path == baseline.scene && PosesMatch(SceneStage(scene), out changed);
                Record("Returning to Edit Mode preserves authored room transforms", report.editTransformsPreserved, changed);
                Record("Saved source scene and live dirty state are preserved", report.savedSceneUnchanged && scene.isDirty == baseline.dirty,
                    "Saved SHA256 " + report.savedHashAfter + "; source dirty before " + baseline.dirty + "; after " + scene.isDirty);
                SaveReport();
                Debug.Log("PORTAL_ALIGNMENT_PLAY_CHECKS_" + (report.passed ? "PASSED" : "FAILED") +
                    " passed=" + report.passedCount + " failed=" + report.failedCount + " report=" + ReportPath);
            }
        }

        // Editor-only coroutine pump: waits for real Play Mode physics ticks;
        // no MonoBehaviour or persistent test object is added to the user's scene.
        static void Pump()
        {
            if (!running || !EditorApplication.isPlaying) return;
            EditorApplication.QueuePlayerLoopUpdate();
            if (EditorApplication.timeSinceStartup > deadline)
            { Record("Play Mode check timeout", false, "No completion within120 seconds."); Finish(true); return; }
            if (waitingFixed)
            {
                if (Time.fixedTime <= lastFixedTime + .000001f) return;
                waitingFixed = false; report.fixedTickObservations++;
            }
            if (waitingFrame)
            { if (Time.frameCount <= lastFrame) return; waitingFrame = false; }
            try
            {
                for (int operations = 0; operations < 1000 && routines.Count > 0; operations++)
                {
                    if (!routines.Peek().MoveNext()) { routines.Pop(); continue; }
                    object yielded = routines.Peek().Current;
                    if (yielded is IEnumerator nested) { routines.Push(nested); continue; }
                    if (yielded is WaitForFixedUpdate)
                    { waitingFixed = true; lastFixedTime = Time.fixedTime; return; }
                    waitingFrame = true; lastFrame = Time.frameCount; return;
                }
                if (routines.Count == 0) Finish(true);
            }
            catch (Exception error) { Record("Unexpected Play Mode exception", false, error.ToString()); Finish(true); }
        }

        static IEnumerator Checks()
        {
            yield return null;
            stage = SceneStage(SceneManager.GetActiveScene());
            if (stage == null || stage.Player == null) throw new InvalidOperationException("Live Play Mode stage/player is missing.");
            player = stage.Player; body = player.GetComponent<Rigidbody2D>();
            player.UseExternalInput = true; player.SetInput(0, false);
            stage.SetMapOpen(true);
            Record("Play Mode Awake preserves authored artwork and distance dots", PosesMatch(stage, out string changed), changed);
            var allPortals = stage.Rooms.Where(r => r != null).SelectMany(r => r.GetComponentsInChildren<ShiftPortal>(true)).ToArray();
            var targets = allPortals.Where(p => baseline.displacedPortals.Contains(ObjectPath(p.transform))).ToArray();
            Record("All four displaced saved glyphs are exercised", targets.Length == 4,
                "Displaced glyph count " + targets.Length + "; target identities captured before entering Play Mode.");
            body.simulated = false;
            foreach (var portal in targets)
            {
                player.Teleport(portal.ShapeRenderer.transform.position); Physics2D.SyncTransforms();
                var trigger = portal.GetComponent<BoxCollider2D>();
                bool atGlyph = portal.CanInteract(player);
                bool centered = Vector2.Distance(trigger.bounds.center, portal.ShapeRenderer.transform.position) < .001f;
                player.Teleport(portal.transform.position); Physics2D.SyncTransforms();
                bool atOldParent = portal.CanInteract(player);
                Record("Live interaction follows artwork: " + ObjectPath(portal.transform), atGlyph && centered && !atOldParent,
                    "At visible glyph " + atGlyph + "; trigger centered on glyph " + centered +
                    "; stale parent accepted " + atOldParent + "; offset " + trigger.offset);
            }
            foreach (var room in stage.Rooms.Where(r => r != null))
            {
                player.Teleport(room.SpawnPosition); body.simulated = true; Physics2D.SyncTransforms();
                bool safe = SafeSpawn(room, out string evidence);
                Record("Authored arrival spawn is clear: " + room.Coordinate, safe, evidence);
                body.simulated = false;
            }

            var initial = new PortalRuleState(stage.AttemptSeed);
            var flipped = FlippedRules(stage.AttemptSeed);
            bool cues = true;
            for (int cycle = 0; cycle < 20; cycle++)
                foreach (var rules in new[] { flipped, initial })
                    foreach (var portal in allPortals)
                    {
                        portal.Refresh(rules);
                        cues &= portal.DistancePips != null && portal.DistancePips.Length >= 2 &&
                            portal.DistancePips[0] != null && portal.DistancePips[0].enabled &&
                            portal.DistancePips[1] != null && portal.DistancePips[1].enabled == (rules.DistanceFor(portal.Color) == 2);
                    }
            Record("Phase refresh keeps the first dot visible and toggles only the second", cues,
                "Twenty isolated phase/reset refresh cycles on every actual live portal.");
            Record("Repeated phase refresh preserves all artwork, attachments and dot poses", PosesMatch(stage, out changed), changed);

            // A disclosed, runtime-only seed gives the displaced yellow hexagon
            // a valid rightward step and the yellow rectangle a valid return.
            int seed = Enumerable.Range(0, 10000).First(s =>
            {
                var rules = new PortalRuleState(s);
                return rules.DirectionFor(PortalShape.Hexagon) == CardinalDirection.Right &&
                    rules.DirectionFor(PortalShape.Rectangle) == CardinalDirection.Left;
            });
            report.fixtureSeed = seed;
            stage.UseFixedSeed = true; stage.AttemptSeed = seed; stage.ResetAttempt();
            body.simulated = true;
            Record("Actual reset preserves every first dot and artwork pose", PosesMatch(stage, out changed) &&
                allPortals.All(p => p.DistancePips != null && p.DistancePips.Length > 0 && p.DistancePips[0] != null && p.DistancePips[0].enabled), changed);
            yield return Settle(150);
            Record("Actual physics settles on the authored starting platform", settled && player.Grounded && Mathf.Abs(player.Velocity.y) < .15f,
                PlayerEvidence());
            Vector2 movementStart = player.transform.position;
            player.SetInput(1, false); yield return Steps(12);
            float rightDelta = player.transform.position.x - movementStart.x;
            Record("Actual controller moves right after alignment repair", rightDelta > .4f && player.Velocity.x > .5f,
                "Rightward displacement " + F(rightDelta) + "; " + PlayerEvidence());
            float rightX = player.transform.position.x;
            player.SetInput(-1, false); yield return Steps(18);
            float leftDelta = player.transform.position.x - rightX;
            Record("Actual controller moves left after alignment repair", leftDelta < -.4f && player.Velocity.x < -.5f,
                "Leftward displacement " + F(leftDelta) + "; " + PlayerEvidence());
            player.SetInput(0, false); stage.Respawn(); yield return Settle(150);
            float groundedY = player.transform.position.y;
            player.SetInput(0, true); yield return Steps(8);
            Record("Actual jump rises without losing interaction alignment", !player.Grounded && player.transform.position.y > groundedY + .5f,
                PlayerEvidence());
            player.SetInput(0, false); yield return Settle(150);
            Record("Actual jump lands and restores grounding", settled && player.Grounded, PlayerEvidence());

            yield return Steps(Mathf.CeilToInt(stage.InteractionCooldown / Time.fixedDeltaTime) + 3);
            var source = stage.CurrentRoom.GetComponentsInChildren<ShiftPortal>().First(p => p.Shape == PortalShape.Hexagon);
            Vector2Int from = stage.CurrentRoom.Coordinate;
            int beforeCount = stage.TraversalCount;
            body.simulated = false; player.Teleport(source.ShapeRenderer.transform.position);
            yield return Steps(4);
            Record("Standing inside the moved glyph does not auto-traverse", stage.CurrentRoom.Coordinate == from && stage.TraversalCount == beforeCount,
                "Before explicit interaction: room " + stage.CurrentRoom.Coordinate + "; traversals " + stage.TraversalCount);
            bool entered = stage.TryUsePortal(source);
            Record("Explicit interaction uses the moved visible glyph", entered && stage.CurrentRoom.Coordinate == from + Vector2Int.right &&
                stage.TraversalCount == beforeCount + 1 && stage.Rules.Phase == 0,
                "Runtime-only fixture seed " + seed + "; accepted " + entered + "; destination " + stage.CurrentRoom.Coordinate);
            body.simulated = true; Physics2D.SyncTransforms();
            bool arrivalSafe = SafeSpawn(stage.CurrentRoom, out string arrivalEvidence);
            Record("A real room transition arrives at a clear safe spawn", arrivalSafe, arrivalEvidence);
            if (entered)
            {
                body.simulated = false;
                var returnPortal = stage.CurrentRoom.GetComponentsInChildren<ShiftPortal>().First(p => p.Shape == PortalShape.Rectangle);
                player.Teleport(returnPortal.ShapeRenderer.transform.position);
                bool nearReturn = returnPortal.CanInteract(player);
                bool repeated = stage.TryUsePortal(returnPortal);
                Record("Cooldown rejects immediate reuse of a valid active return portal", nearReturn && !repeated && stage.TraversalCount == beforeCount + 1,
                    "Near return glyph " + nearReturn + "; repeated interaction accepted " + repeated);
                yield return Steps(Mathf.CeilToInt(stage.InteractionCooldown / Time.fixedDeltaTime) + 3);
                bool returned = stage.TryUsePortal(returnPortal);
                Record("The same valid return works after cooldown", returned && stage.CurrentRoom.Coordinate == from && stage.TraversalCount == beforeCount + 2,
                    "Accepted " + returned + "; room " + stage.CurrentRoom.Coordinate + "; traversals " + stage.TraversalCount);
            }
            body.simulated = true; player.SetInput(0, false);
            int settledCount = stage.TraversalCount;
            yield return Settle(150); yield return Steps(24);
            Record("Arrival and departure cannot cause a repeated trigger loop", stage.TraversalCount == settledCount && player.Grounded,
                "Traversals remained " + stage.TraversalCount + "; " + PlayerEvidence());
            stage.ResetAttempt(); yield return Settle(150);
            Record("Final reset preserves every authored room and portal transform", PosesMatch(stage, out changed), changed);
            Record("Saved authored scene was never written during Play Mode", Hash(ScenePath) == baseline.savedHash,
                "SHA256 " + Hash(ScenePath));
        }

        static bool SafeSpawn(PortalRoom room, out string evidence)
        {
            var collider = player.GetComponent<Collider2D>();
            var overlaps = new List<string>();
            foreach (var solid in stage.Rooms.Where(r => r != null && r.gameObject.activeInHierarchy)
                .SelectMany(r => r.GetComponentsInChildren<Collider2D>()))
            {
                if (!solid.enabled || solid.isTrigger) continue;
                ColliderDistance2D distance = collider.Distance(solid);
                if (!distance.isValid) overlaps.Add("invalid distance: " + solid.name);
                else if (distance.isOverlapped && distance.distance < -.005f) overlaps.Add(solid.name);
            }
            var portalsInRoom = room.GetComponentsInChildren<ShiftPortal>();
            int portals = portalsInRoom.Count(p => p.CanInteract(player));
            int triggerOverlaps = 0;
            foreach (var portal in portalsInRoom)
            {
                ColliderDistance2D distance = collider.Distance(portal.GetComponent<BoxCollider2D>());
                if (!distance.isValid || (distance.isOverlapped && distance.distance < -.005f)) triggerOverlaps++;
            }
            bool atSpawn = Vector2.Distance(player.transform.position, room.SpawnPosition) < .02f;
            evidence = "At authored spawn " + atSpawn + "; solid penetrations " + overlaps.Count +
                "; portal trigger penetrations " + triggerOverlaps + "; portals within interaction reach " + portals +
                (overlaps.Count > 0 ? "; " + string.Join(", ", overlaps) : "");
            return atSpawn && overlaps.Count == 0 && triggerOverlaps == 0 && portals == 0;
        }

        static PortalRuleState FlippedRules(int seed)
        {
            var rules = new PortalRuleState(seed);
            var shape = Enumerable.Range(0, 4).Select(i => (PortalShape)i).First(s => rules.DirectionFor(s) == CardinalDirection.Up);
            var rooms = new HashSet<Vector2Int>();
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) rooms.Add(new Vector2Int(x, y));
            if (!rules.TryTraverse(Vector2Int.zero, shape, PortalColor.Purple, 4, 4, rooms, out _, out string reason))
                throw new InvalidOperationException(reason);
            return rules;
        }

        static IEnumerator Steps(int count)
        { for (int i = 0; i < count; i++) yield return new WaitForFixedUpdate(); }

        static IEnumerator Settle(int maximumTicks)
        {
            settled = false; int stable = 0;
            for (int i = 0; i < maximumTicks; i++)
            {
                yield return new WaitForFixedUpdate();
                if (player.Grounded && Mathf.Abs(player.Velocity.y) < .15f) stable++; else stable = 0;
                if (stable >= 4) { settled = true; yield break; }
            }
        }

        static bool PosesMatch(PortalStage authoredStage, out string evidence)
        {
            if (authoredStage == null) { evidence = "Stage missing."; return false; }
            var live = authoredStage.Rooms.Where(r => r != null).SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                .ToDictionary(ObjectPath);
            string[] changed = baseline.roomTransforms.Where(p => !live.TryGetValue(p.path, out Transform t) || !p.Matches(t))
                .Select(p => p.path).ToArray();
            evidence = "Protected room transforms " + baseline.roomTransforms.Length + "; changed " + changed.Length +
                (changed.Length > 0 ? "; " + string.Join("; ", changed.Take(12)) : "");
            return changed.Length == 0 && live.Count == baseline.roomTransforms.Length;
        }

        static PortalStage SceneStage(Scene scene) => scene.IsValid() && scene.isLoaded
            ? scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PortalStage>(true)).FirstOrDefault() : null;
        static string ObjectPath(Transform t) => t.parent == null ? t.name : ObjectPath(t.parent) + "/" + t.name;
        static string F(float value) => value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        static string PlayerEvidence() => "Position " + player.transform.position + "; velocity " + player.Velocity + "; grounded " + player.Grounded;
        static string Hash(string path)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }

        static void Record(string name, bool passed, string evidence)
        {
            report.checks.Add(new Check { name = name, passed = passed, evidence = evidence });
            if (passed) report.passedCount++; else report.failedCount++;
            Debug.Log("PORTAL_ALIGNMENT_PLAY_CHECK " + (passed ? "PASS " : "FAIL ") + name + ": " + evidence);
        }

        static void SaveReport()
        {
            report.finishedUtc = DateTime.UtcNow.ToString("o");
            report.passed = report.failedCount == 0 && report.passedCount > 0;
            Directory.CreateDirectory("Verification"); File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            SessionState.SetString(ReportKey, JsonUtility.ToJson(report));
        }

        static void Finish(bool exitPlay)
        {
            if (!running) return;
            running = false; EditorApplication.update -= Pump; routines.Clear();
            if (player != null) player.SetInput(0, false);
            if (body != null) body.simulated = true;
            Application.runInBackground = previousRunInBackground;
            SaveReport(); SessionState.SetBool(ReturningKey, true);
            if (exitPlay) EditorApplication.ExitPlaymode();
        }
    }
}
