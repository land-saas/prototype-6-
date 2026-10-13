using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PortalShift
{
    /// <summary>
    /// Opt-in checks against the actual scene, physics, and room controller.
    /// Launch a player with --portal-shift-checks DIRECTORY to write evidence and quit.
    /// Normal game launches never create the runner or replace keyboard input.
    /// </summary>
    public static class PortalPlayChecks
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfRequested()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (arguments[i] != "--portal-shift-checks") continue;
                string directory = i + 1 < arguments.Length
                    ? arguments[i + 1]
                    : Path.Combine(Application.persistentDataPath, "PortalShiftChecks");
                var runner = new GameObject("Portal Shift Automated Play Checks").AddComponent<PortalPlayCheckRunner>();
                runner.Begin(directory);
                return;
            }
        }
    }

    internal sealed class PortalPlayCheckRunner : MonoBehaviour
    {
        [Serializable]
        private sealed class CheckResult
        {
            public string name;
            public bool passed;
            public string evidence;
        }

        [Serializable]
        private sealed class CheckReport
        {
            public string startedUtc;
            public string finishedUtc;
            public string unityVersion;
            public string scene;
            public string method = "Automated runtime checks using real FixedUpdate physics and the stage interaction API. Wall-access checks and the multi-room route use ordinary scripted movement and jumps from authored room spawns; route portal positions are reached without teleporting to gates. Isolated wall-contact, cooldown and state fixtures use Teleport or Respawn to arrange their starting positions. These checks are not human keyboard play.";
            public int attemptSeed;
            public bool passed;
            public int passedCount;
            public int failedCount;
            public string screenshot;
            public string fullMapScreenshot;
            public string fourRoomRoute;
            public List<CheckResult> checks = new List<CheckResult>();
        }

        private readonly CheckReport report = new CheckReport();
        private PortalStage stage;
        private PortalPlayerController player;
        private string outputDirectory;
        private bool finished;
        private bool settled;
        private sealed class LayoutItem
        {
            public Transform transform;
            public string path;
            public Vector3 position;
            public Vector3 scale;
            public Quaternion rotation;
            public bool ignorePosition;
            public bool ignoreRotation;
            public BoxCollider2D collider;
            public Vector2 colliderSize;
            public Vector2 colliderOffset;
            public bool trigger;
            public bool effector;
        }
        private readonly Dictionary<PortalRoom, List<LayoutItem>> authoredLayouts = new Dictionary<PortalRoom, List<LayoutItem>>();

        public void Begin(string directory)
        {
            // A connector or screenshot tool can briefly take focus. Only this
            // opt-in test launch must continue advancing its physics in background.
            Application.runInBackground = true;
            outputDirectory = Path.GetFullPath(directory);
            report.startedUtc = DateTime.UtcNow.ToString("o");
            report.unityVersion = Application.unityVersion;
            report.scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            Directory.CreateDirectory(outputDirectory);
            StartCoroutine(Guarded(RunChecks()));
        }

        // Flatten nested test coroutines so any unexpected exception still produces
        // an evidence file and a failing exit code instead of hanging a test launch.
        private IEnumerator Guarded(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            while (stack.Count > 0)
            {
                bool moved = false;
                object yielded = null;
                Exception exception = null;
                try
                {
                    moved = stack.Peek().MoveNext();
                    if (moved) yielded = stack.Peek().Current;
                }
                catch (Exception error) { exception = error; }
                if (exception != null)
                {
                    Record("Unexpected runtime exception", false, exception.ToString());
                    Finish();
                    yield break;
                }
                if (!moved) { stack.Pop(); continue; }
                if (yielded is IEnumerator nested) stack.Push(nested);
                else yield return yielded;
            }
        }

        private IEnumerator RunChecks()
        {
            yield return null;
            stage = FindAnyObjectByType<PortalStage>();
            Record("Serialized scene has a stage", stage != null, report.scene);
            if (stage == null) { Finish(); yield break; }
            player = stage.Player;
            Record("Stage has a dynamic player", player != null && player.GetComponent<Rigidbody2D>() != null,
                player != null ? player.name : "Player reference is missing.");
            if (player == null) { Finish(); yield break; }
            player.UseExternalInput = true;
            player.SetInput(0f, false);
            CaptureAuthoredLayouts();

            // Reuse the launched attempt's seed, so the test is reproducible without
            // changing the shipped scene's serialized random-attempt setting.
            stage.UseFixedSeed = true;
            stage.ResetAttempt();
            report.attemptSeed = stage.AttemptSeed;
            string initialMapping = Mapping();
            Record("Four-room baseline uses bounded coordinates",
                stage.Width == 2 && stage.Height == 2 && stage.Boundary == BoundaryPolicy.Reject &&
                stage.Rooms != null && stage.Rooms.Length == 4,
                "Grid " + stage.Width + " x " + stage.Height + "; boundary " + stage.Boundary +
                "; registered rooms " + (stage.Rooms != null ? stage.Rooms.Length : 0));
            Record("Default whole-map view is playable and renders all four rooms",
                stage.StartInWholeMap && stage.MapOpen && ActiveRoomCount() == RegisteredRoomCount() &&
                player.enabled && player.GetComponent<Rigidbody2D>().simulated,
                MapRestorationEvidence());
            CheckVisualConfiguration();
            CheckMatchingLayouts();
            CheckLayoutPersistence("reset");
            bool safeStart = SafeArrival(out string startEvidence);
            Record("Start spawn stays clear of walls and portal triggers", safeStart, startEvidence);

            yield return Settle(3f);
            Record("Player settles on the start platform", settled && Mathf.Abs(player.Velocity.y) < .15f,
                PositionEvidence() + "; grounded " + player.Grounded);
            yield return CheckMovement();
            yield return CheckJump();
            yield return CheckGateAndWall();
            yield return PortalWallAccessChecks.Run(stage, Record);

            player.SetInput(0f, false);
            stage.ResetAttempt();
            yield return Settle(3f);
            yield return WaitForCooldown();
            yield return CheckFourRoomRoute(initialMapping);
            yield return CheckBlockedMagnitudeTwo();
            yield return CheckRespawn();
            yield return CheckFullMap();
            CheckLayoutPersistence("room route and camera view changes");

            player.SetInput(0f, false);
            stage.ResetAttempt();
            bool resetPosition = Vector2.Distance(player.transform.position, stage.CurrentRoom.SpawnPosition) < .02f;
            Record("Reset restores the start and initial distance assignment",
                stage.CurrentRoom.Coordinate == stage.StartCoordinate && resetPosition && stage.TraversalCount == 0 &&
                stage.MapOpen && ActiveRoomCount() == RegisteredRoomCount() &&
                player.enabled && player.GetComponent<Rigidbody2D>().simulated &&
                stage.Rules.Phase == 0 && stage.Rules.DistanceFor(PortalColor.Yellow) == 1 &&
                stage.Rules.DistanceFor(PortalColor.Purple) == 2 && Mapping() == initialMapping,
                "Room " + stage.CurrentRoom.Coordinate + "; at spawn " + resetPosition +
                "; traversals " + stage.TraversalCount + "; phase " + stage.Rules.Phase +
                "; yellow " + stage.Rules.DistanceFor(PortalColor.Yellow) +
                "; purple " + stage.Rules.DistanceFor(PortalColor.Purple));
            bool safeReset = SafeArrival(out string resetEvidence);
            Record("Reset restores a clear spawn outside portal interaction reach", safeReset, resetEvidence);
            CheckLayoutPersistence("final reset");
            yield return Settle(3f);
            yield return new WaitForSeconds(3f);
            yield return CaptureEvidence();
            Finish();
        }

        private void CheckVisualConfiguration()
        {
            var circle = player.GetComponent<CircleCollider2D>();
            var playerArt = player.GetComponent<SpriteRenderer>();
            bool circleCollider = circle != null && player.GetComponents<Collider2D>().Length == 1 &&
                player.GetComponent<BoxCollider2D>() == null &&
                Mathf.Abs(circle.radius - .42f) < .002f && circle.offset.sqrMagnitude < .0001f;
            Record("Player has one centered circular collider matching the authored radius", circleCollider,
                "Circle radius " + (circle != null ? F(circle.radius) : "missing") +
                "; box collider " + (player.GetComponent<BoxCollider2D>() != null));
            bool circularArt = circleCollider && playerArt != null && playerArt.sprite != null &&
                Mathf.Abs(playerArt.bounds.size.x - playerArt.bounds.size.y) < .02f &&
                Mathf.Abs(playerArt.bounds.size.x - circle.bounds.size.x) < .035f;
            Record("Circle art and physics use the same diameter", circularArt,
                "Art bounds " + (playerArt != null ? playerArt.bounds.size.ToString() : "missing") +
                "; collider bounds " + (circle != null ? circle.bounds.size.ToString() : "missing"));

            Vector3 origin = stage.CurrentRoom.transform.position;
            Vector2Int originCoordinate = stage.CurrentRoom.Coordinate;
            foreach (var room in stage.Rooms)
            {
                if (room == null) continue;
                var portals = room.GetComponentsInChildren<ShiftPortal>(true);
                int yellowCount = 0;
                int purpleCount = 0;
                var shapes = new HashSet<PortalShape>();
                bool colorsAuthored = true;
                bool mountedArt = true;
                bool uprightTriggers = true;
                bool distancePips = true;
                bool expectedMounts = true;
                bool nearMountingSurfaces = true;
                var mounts = new HashSet<PortalMount>();
                var sourceMounts = new HashSet<PortalMount>();
                foreach (var sourcePortal in stage.CurrentRoom.GetComponentsInChildren<ShiftPortal>(true))
                    sourceMounts.Add(sourcePortal.Mount);
                PortalShape expectedPurple = room.Coordinate.x == room.Coordinate.y
                    ? PortalShape.Triangle : PortalShape.Diamond;
                foreach (var portal in portals)
                {
                    shapes.Add(portal.Shape);
                    if (portal.Color == PortalColor.Yellow) yellowCount++;
                    if (portal.Color == PortalColor.Purple)
                    {
                        purpleCount++;
                        colorsAuthored &= portal.Shape == expectedPurple;
                    }
                    ShiftPortal sourcePortal = null;
                    foreach (var candidate in stage.CurrentRoom.GetComponentsInChildren<ShiftPortal>(true))
                        if (candidate.Shape == portal.Shape) sourcePortal = candidate;
                    PortalMount expectedMount = sourcePortal != null ? sourcePortal.Mount : PortalMount.Freestanding;
                    float expectedRotation = sourcePortal != null ? sourcePortal.VisualRotationDegrees : 0f;
                    mounts.Add(portal.Mount);
                    expectedMounts &= sourcePortal != null && portal.Mount == expectedMount;
                    mountedArt &= portal.ShapeRenderer != null &&
                        Mathf.Abs(Mathf.DeltaAngle(portal.VisualRotationDegrees, expectedRotation)) < .02f &&
                        Mathf.Abs(Mathf.DeltaAngle(portal.ShapeRenderer.transform.localEulerAngles.z, expectedRotation)) < .02f;
                    var trigger = portal.GetComponent<BoxCollider2D>();
                    Vector2 expectedTriggerSize = sourcePortal != null
                        ? sourcePortal.GetComponent<BoxCollider2D>().size : Vector2.zero;
                    uprightTriggers &= trigger != null && trigger.isTrigger &&
                        Vector2.Distance(trigger.size, expectedTriggerSize) < .02f &&
                        Mathf.Abs(Mathf.DeltaAngle(portal.transform.eulerAngles.z, 0f)) < .02f;
                    nearMountingSurfaces &= IsNearMountingSurface(portal, room);
                    int activePips = 0;
                    if (portal.DistancePips == null || portal.DistancePips.Length != 2) distancePips = false;
                    else
                    {
                        foreach (var pip in portal.DistancePips)
                        {
                            if (pip == null || pip.sprite == null) distancePips = false;
                            else if (pip.gameObject.activeSelf && pip.enabled) activePips++;
                        }
                        distancePips &= activePips == stage.Rules.DistanceFor(portal.Color);
                    }
                }
                Record("Room " + room.Coordinate + " has exactly four editable portal objects",
                    portals.Length == 4 && shapes.Count == 4 && yellowCount == 3 && purpleCount == 1 && colorsAuthored,
                    "Portals " + portals.Length + "; distinct shapes " + shapes.Count +
                    "; yellow " + yellowCount + "; purple " + purpleCount +
                    "; purple shape " + expectedPurple + "; expected colors " + colorsAuthored);
                Record("Room " + room.Coordinate + " preserves the source-authored physical mounts",
                    expectedMounts && mounts.Count == sourceMounts.Count && nearMountingSurfaces,
                    "Distinct mounts " + mounts.Count + "; source " + sourceMounts.Count +
                    "; shape mount assignments " + expectedMounts +
                    "; nearby solid mounting surfaces " + nearMountingSurfaces);
                Record("Room " + room.Coordinate + " orients portal artwork without rotating trigger roots",
                    mountedArt && uprightTriggers,
                    "Artwork rotations match serialized source settings " + mountedArt +
                    "; source-layout trigger sizes and axis-aligned roots " + uprightTriggers);
                Record("Room " + room.Coordinate + " shows distance with non-color dots", distancePips,
                    "Each portal has two authored sprite dots; active dots match its current magnitude " + distancePips);
                int textCount = room.GetComponentsInChildren<TextMesh>(true).Length;
                Record("Room " + room.Coordinate + " contains no text overlays", textCount == 0,
                    "TextMesh components under the room " + textCount);
                Vector2Int delta = room.Coordinate - originCoordinate;
                Vector3 expectedPosition = origin + new Vector3(delta.x * 21.2f, delta.y * 11.5f, 0f);
                Record("Room " + room.Coordinate + " belongs to the compact map layout",
                    Vector3.Distance(room.transform.position, expectedPosition) < .02f,
                    "Position " + room.transform.position + "; expected " + expectedPosition +
                    "; coordinate-independent room extents " + F(stage.MapRoomWidth) + " x " + F(stage.MapRoomHeight));
            }
        }

        private void CaptureAuthoredLayouts()
        {
            authoredLayouts.Clear();
            foreach (var room in stage.Rooms)
            {
                if (room == null) continue;
                var pipTransforms = new HashSet<Transform>();
                var shapeTransforms = new HashSet<Transform>();
                foreach (var portal in room.GetComponentsInChildren<ShiftPortal>(true))
                {
                    if (portal.ShapeRenderer != null) shapeTransforms.Add(portal.ShapeRenderer.transform);
                    if (portal.DistancePips != null)
                        foreach (var pip in portal.DistancePips) if (pip != null) pipTransforms.Add(pip.transform);
                }
                var items = new List<LayoutItem>();
                foreach (var transform in room.GetComponentsInChildren<Transform>(true))
                {
                    var box = transform.GetComponent<BoxCollider2D>();
                    items.Add(new LayoutItem
                    {
                        transform = transform,
                        path = LayoutPath(transform, room.transform),
                        position = transform.localPosition,
                        scale = transform.localScale,
                        rotation = transform.localRotation,
                        ignorePosition = pipTransforms.Contains(transform),
                        ignoreRotation = shapeTransforms.Contains(transform),
                        collider = box,
                        colliderSize = box != null ? box.size : Vector2.zero,
                        colliderOffset = box != null ? box.offset : Vector2.zero,
                        trigger = box != null && box.isTrigger,
                        effector = box != null && box.usedByEffector
                    });
                }
                authoredLayouts.Add(room, items);
            }
        }

        private static string LayoutPath(Transform item, Transform root)
        {
            if (item == root) return ".";
            var parts = new List<string>();
            for (var current = item; current != null && current != root; current = current.parent)
            {
                var portal = current.GetComponent<ShiftPortal>();
                parts.Add(portal != null ? "Portal-" + portal.Shape : current.name);
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static bool LayoutEquivalent(LayoutItem a, LayoutItem b)
        {
            return a.ignorePosition == b.ignorePosition && a.ignoreRotation == b.ignoreRotation &&
                (a.ignorePosition || Vector3.Distance(a.position, b.position) < .002f) &&
                (a.ignoreRotation || Quaternion.Angle(a.rotation, b.rotation) < .02f) &&
                Vector3.Distance(a.scale, b.scale) < .002f &&
                (a.collider != null) == (b.collider != null) &&
                Vector2.Distance(a.colliderSize, b.colliderSize) < .002f &&
                Vector2.Distance(a.colliderOffset, b.colliderOffset) < .002f &&
                a.trigger == b.trigger && a.effector == b.effector;
        }

        private void CheckMatchingLayouts()
        {
            List<LayoutItem> source = null;
            foreach (var pair in authoredLayouts)
                if (pair.Key.Coordinate == stage.StartCoordinate) source = pair.Value;
            if (source == null)
            {
                Record("Copied layout has a registered source room", false, "Start coordinate has no captured authored layout.");
                return;
            }
            foreach (var pair in authoredLayouts)
            {
                var target = new Dictionary<string, LayoutItem>();
                foreach (var item in pair.Value) if (item.path != ".") target.Add(item.path, item);
                bool matches = target.Count == source.Count - 1;
                string difference = "";
                foreach (var item in source)
                {
                    if (item.path == ".") continue;
                    if (!target.TryGetValue(item.path, out var corresponding) || !LayoutEquivalent(item, corresponding))
                    {
                        matches = false;
                        if (difference.Length == 0) difference = item.path;
                    }
                }
                Record("Room " + pair.Key.Coordinate + " matches the copied starting-room layout", matches,
                    "Authored child transforms " + target.Count + "; source " + (source.Count - 1) +
                    "; first mismatch " + (difference.Length == 0 ? "none" : difference) +
                    ". Room coordinates and portal colors remain independent; runtime pip positions and shape rotation are excluded.");
            }
        }

        private void CheckLayoutPersistence(string checkpoint)
        {
            foreach (var pair in authoredLayouts)
            {
                bool preserved = pair.Key.GetComponentsInChildren<Transform>(true).Length == pair.Value.Count;
                string difference = "";
                foreach (var item in pair.Value)
                {
                    bool same = item.transform != null && LayoutPath(item.transform, pair.Key.transform) == item.path &&
                        item.transform.GetComponent<BoxCollider2D>() == item.collider &&
                        (item.ignorePosition || Vector3.Distance(item.transform.localPosition, item.position) < .002f) &&
                        (item.ignoreRotation || Quaternion.Angle(item.transform.localRotation, item.rotation) < .02f) &&
                        Vector3.Distance(item.transform.localScale, item.scale) < .002f;
                    if (item.collider != null)
                        same &= Vector2.Distance(item.collider.size, item.colliderSize) < .002f &&
                            Vector2.Distance(item.collider.offset, item.colliderOffset) < .002f &&
                            item.collider.isTrigger == item.trigger && item.collider.usedByEffector == item.effector;
                    if (!same) { preserved = false; if (difference.Length == 0) difference = item.path; }
                }
                Record("Authored layout remains unchanged after " + checkpoint + " in room " + pair.Key.Coordinate,
                    preserved, "Captured transforms " + pair.Value.Count + "; first mismatch " +
                    (difference.Length == 0 ? "none" : difference) +
                    ". Snapshot begins after scene Awake; editor evidence verifies the initial saved layout.");
            }
        }

        private static Bounds AuthoredBounds(BoxCollider2D solid)
        {
            var bounds = new Bounds(solid.transform.TransformPoint(solid.offset), Vector3.zero);
            foreach (float x in new[] { -.5f, .5f })
            foreach (float y in new[] { -.5f, .5f })
                bounds.Encapsulate(solid.transform.TransformPoint(solid.offset + new Vector2(x * solid.size.x, y * solid.size.y)));
            return bounds;
        }

        private static bool IsNearMountingSurface(ShiftPortal portal, PortalRoom room)
        {
            string surfaceName;
            switch (portal.Mount)
            {
                case PortalMount.Left: surfaceName = "Left Wall"; break;
                case PortalMount.Right: surfaceName = "Right Wall"; break;
                case PortalMount.Floor: surfaceName = "Main Floor"; break;
                case PortalMount.Ceiling: surfaceName = "Ceiling Wall"; break;
                default: return false;
            }
            foreach (var solid in room.GetComponentsInChildren<BoxCollider2D>(true))
            {
                if (solid.isTrigger || solid.name.IndexOf(surfaceName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                // Collider.bounds is empty for disabled rooms. Reconstruct the
                // actual authored box's transformed corners without activating it.
                var bounds = new Bounds(solid.transform.TransformPoint(solid.offset), Vector3.zero);
                foreach (float x in new[] { -.5f, .5f })
                foreach (float y in new[] { -.5f, .5f })
                    bounds.Encapsulate(solid.transform.TransformPoint(solid.offset + new Vector2(x * solid.size.x, y * solid.size.y)));
                Vector3 position = portal.transform.position;
                float separation;
                bool alongsideSurface;
                switch (portal.Mount)
                {
                    case PortalMount.Left:
                        separation = position.x - bounds.max.x;
                        alongsideSurface = position.y >= bounds.min.y && position.y <= bounds.max.y;
                        break;
                    case PortalMount.Right:
                        separation = bounds.min.x - position.x;
                        alongsideSurface = position.y >= bounds.min.y && position.y <= bounds.max.y;
                        break;
                    case PortalMount.Floor:
                        separation = position.y - bounds.max.y;
                        alongsideSurface = position.x >= bounds.min.x && position.x <= bounds.max.x;
                        break;
                    default:
                        separation = position.y >= bounds.max.y
                            ? position.y - bounds.max.y : bounds.min.y - position.y;
                        alongsideSurface = position.x >= bounds.min.x && position.x <= bounds.max.x;
                        break;
                }
                return alongsideSurface && separation >= -.02f && separation <= .65f;
            }
            return false;
        }

        private Vector2 ApproachPosition(ShiftPortal portal)
        {
            // Only isolated cooldown/rejection fixtures use this positioning.
            // Main route approaches always use normal movement and jumps.
            float x = portal.transform.position.x;
            float supportTop = float.NegativeInfinity;
            foreach (var solid in portal.Room.GetComponentsInChildren<BoxCollider2D>(true))
            {
                if (solid.isTrigger) continue;
                Bounds bounds = AuthoredBounds(solid);
                if (x >= bounds.min.x && x <= bounds.max.x && bounds.max.y < portal.transform.position.y)
                    supportTop = Mathf.Max(supportTop, bounds.max.y);
            }
            if (float.IsNegativeInfinity(supportTop)) return portal.transform.position;
            return new Vector2(x, supportTop + player.GetComponent<Collider2D>().bounds.extents.y + .005f);
        }

        private bool SafeArrival(out string evidence)
        {
            Physics2D.SyncTransforms();
            var collider = player.GetComponent<Collider2D>();
            int solidOverlaps = 0;
            int interactablePortals = 0;
            foreach (var room in stage.Rooms)
            {
                if (room == null || !room.gameObject.activeInHierarchy) continue;
                foreach (var solid in room.GetComponentsInChildren<Collider2D>())
                {
                    if (solid.isTrigger || !solid.enabled) continue;
                    ColliderDistance2D separation = collider.Distance(solid);
                    if (separation.isValid && separation.isOverlapped && separation.distance < -.005f) solidOverlaps++;
                }
            }
            foreach (var portal in stage.CurrentRoom.GetComponentsInChildren<ShiftPortal>())
                if (portal.CanInteract(player)) interactablePortals++;
            bool atSpawn = Vector2.Distance(player.transform.position, stage.CurrentRoom.SpawnPosition) < .02f;
            evidence = "At spawn " + atSpawn + "; solid overlaps " + solidOverlaps +
                "; portals within interaction reach " + interactablePortals + "; active rooms " + ActiveRoomCount();
            return atSpawn && solidOverlaps == 0 && interactablePortals == 0 &&
                ActiveRoomCount() == (stage.MapOpen ? RegisteredRoomCount() : 1);
        }

        private IEnumerator CheckMovement()
        {
            Vector2 origin = player.transform.position;
            player.SetInput(1f, false);
            yield return FixedSteps(12);
            float rightDistance = player.transform.position.x - origin.x;
            float rightVelocity = player.Velocity.x;
            Record("Right movement responds to scripted D/right input", rightDistance > .7f && rightVelocity > 1f,
                "Distance " + F(rightDistance) + "; horizontal velocity " + F(rightVelocity));

            float rightX = player.transform.position.x;
            player.SetInput(-1f, false);
            yield return FixedSteps(18);
            float leftDistance = player.transform.position.x - rightX;
            Record("Left movement responds to scripted A/left input", leftDistance < -.7f && player.Velocity.x < -1f,
                "Distance " + F(leftDistance) + "; horizontal velocity " + F(player.Velocity.x));

            player.SetInput(0f, false);
            yield return FixedSteps(12);
            Record("Releasing movement brakes the player", Mathf.Abs(player.Velocity.x) < .15f,
                "Horizontal velocity " + F(player.Velocity.x));
            stage.Respawn();
            yield return Settle(3f);
        }

        private IEnumerator CheckJump()
        {
            float startY = player.transform.position.y;
            player.SetInput(0f, true);
            yield return FixedSteps(8);
            bool airborne = !player.Grounded;
            float rise = player.transform.position.y - startY;
            Record("Jump rises from solid ground", airborne && rise > .6f && player.Velocity.y > 0f,
                "Rise " + F(rise) + "; vertical velocity " + F(player.Velocity.y) + "; grounded " + player.Grounded);

            bool noExtraImpulse = true;
            float previousVelocity = player.Velocity.y;
            float greatestImpulse = 0f;
            for (int i = 0; i < 8; i++)
            {
                player.SetInput(0f, true);
                yield return new WaitForFixedUpdate();
                float impulse = player.Velocity.y - previousVelocity;
                greatestImpulse = Mathf.Max(greatestImpulse, impulse);
                if (impulse > .75f || player.Grounded) noExtraImpulse = false;
                previousVelocity = player.Velocity.y;
            }
            Record("Repeated jump presses cannot add a midair jump", airborne && noExtraImpulse,
                "Largest upward velocity change during eight airborne presses " + F(greatestImpulse));
            player.SetInput(0f, false);
            yield return Settle(3f);
            Record("Jump lands and regains grounding", settled && Mathf.Abs(player.transform.position.y - startY) < .15f,
                PositionEvidence() + "; original grounded height " + F(startY));
        }

        private IEnumerator CheckGateAndWall()
        {
            Collider2D gate = null;
            foreach (var collider in stage.CurrentRoom.GetComponentsInChildren<Collider2D>())
            {
                if (!collider.isTrigger && collider.name.IndexOf("Gate", StringComparison.OrdinalIgnoreCase) >= 0)
                { gate = collider; break; }
            }
            Record("Start room contains a physical jump gate", gate != null,
                gate != null ? gate.name + "; bounds " + gate.bounds : "No solid collider with Gate in its name.");
            if (gate == null) yield break;

            stage.Respawn();
            yield return Settle(3f);
            player.SetInput(1f, false);
            yield return FixedSteps(55);
            Vector2 blockedPosition = player.transform.position;
            Record("Walking alone cannot pass the jump gate",
                blockedPosition.x < gate.bounds.min.x && blockedPosition.x > stage.CurrentRoom.SpawnPosition.x + 1f &&
                Mathf.Abs(player.Velocity.x) < .25f && player.Grounded,
                PositionEvidence() + "; gate left edge " + F(gate.bounds.min.x));
            player.SetInput(0f, false);

            var playerCollider = player.GetComponent<Collider2D>();
            float playerHalfWidth = playerCollider.bounds.extents.x;
            float floorTop = float.NegativeInfinity;
            foreach (var solid in stage.CurrentRoom.GetComponentsInChildren<BoxCollider2D>())
                if (!solid.isTrigger && solid.name == "Main Floor") floorTop = AuthoredBounds(solid).max.y;
            float wallY = Mathf.Max(gate.bounds.center.y, floorTop + playerCollider.bounds.extents.y + .3f);
            Vector2 wallPosition = new Vector2(gate.bounds.min.x - playerHalfWidth + .01f, wallY);
            player.Teleport(wallPosition);
            player.SetInput(1f, false);
            yield return FixedSteps(3);
            var wallContacts = new ContactPoint2D[16];
            int contactCount = playerCollider.GetContacts(wallContacts);
            bool touchingWall = false;
            for (int i = 0; i < contactCount; i++)
                if (Mathf.Abs(wallContacts[i].normal.x) > .7f) touchingWall = true;
            Record("Airborne contact with a vertical wall does not count as ground",
                touchingWall && !player.Grounded && player.Velocity.y < 0f,
                "Wall contact " + touchingWall + "; grounded " + player.Grounded + "; " + PositionEvidence());

            player.SetInput(0f, false);
            player.Teleport(blockedPosition);
            yield return Settle(3f);
            player.SetInput(1f, true);
            float highestY = player.transform.position.y;
            for (int i = 0; i < 44; i++)
            {
                yield return new WaitForFixedUpdate();
                highestY = Mathf.Max(highestY, player.transform.position.y);
            }
            Record("A moving jump clears the gate",
                player.transform.position.x > gate.bounds.max.x + playerHalfWidth &&
                highestY - playerCollider.bounds.extents.y > gate.bounds.max.y - .12f,
                "End x " + F(player.transform.position.x) + "; gate right edge " + F(gate.bounds.max.x) +
                "; highest player bottom " + F(highestY - playerCollider.bounds.extents.y) +
                "; gate top " + F(gate.bounds.max.y));
            player.SetInput(0f, false);
            yield return Settle(3f);
        }

        private IEnumerator CheckFourRoomRoute(string initialMapping)
        {
            stage.SetMapOpen(true);
            stage.FitMapCamera();
            List<Vector2Int> route = FindFourRoomRoute();
            Record("Authored one-room portals support a route through all four rooms", route != null,
                route != null ? "Computed from this attempt's actual mapping and authored yellow portals." :
                "No safe one-room route can visit all registered rooms and return to start.");
            if (route == null) yield break;
            var path = new List<string> { stage.CurrentRoom.Coordinate.ToString() };
            foreach (Vector2Int coordinate in route) path.Add(coordinate.ToString());
            report.fourRoomRoute = string.Join(" -> ", path);
            var roomAccessChecked = new HashSet<Vector2Int> { stage.StartCoordinate };
            foreach (Vector2Int target in route)
            {
                Vector2Int from = stage.CurrentRoom.Coordinate;
                Vector2Int step = target - from;
                ShiftPortal portal = FindPortal(PortalColor.Yellow, step);
                Record("Route has an authored one-room portal " + from + " -> " + target, portal != null,
                    portal != null ? "Shape " + portal.Shape + "; direction " + stage.Rules.DirectionFor(portal.Shape) : "No matching portal.");
                if (portal == null) yield break;

                player.SetInput(0f, false);
                int remoteCount = stage.TraversalCount;
                bool remoteNearby = portal.CanInteract(player);
                bool remoteAccepted = stage.TryUsePortal(portal);
                Record("A distant portal cannot be used remotely " + from + " -> " + target,
                    !remoteNearby && !remoteAccepted && stage.CurrentRoom.Coordinate == from && stage.TraversalCount == remoteCount,
                    "Can interact from spawn " + remoteNearby + "; accepted " + remoteAccepted +
                    "; room " + stage.CurrentRoom.Coordinate + "; traversals " + stage.TraversalCount);
                yield return WaitForCooldown();
                yield return PortalWallAccessChecks.Approach(stage, portal, Record);
                bool nearTrigger = portal.CanInteract(player);
                Record("Normal movement reaches the route portal " + from + " -> " + target,
                    nearTrigger, PositionEvidence() + "; mount " + portal.Mount + "; in interaction reach " + nearTrigger);
                if (!nearTrigger) yield break;
                int countBefore = stage.TraversalCount;
                if (player.Grounded) yield return FixedSteps(12);
                Record("Entering a portal trigger waits for interaction " + from,
                    nearTrigger && stage.CurrentRoom.Coordinate == from && stage.TraversalCount == countBefore,
                    "Can interact " + nearTrigger + "; room " + stage.CurrentRoom.Coordinate + "; traversals " + stage.TraversalCount);

                Vector3 fullCameraPosition = stage.RoomCamera.transform.position;
                float fullCameraSize = stage.RoomCamera.orthographicSize;
                bool traversed = stage.TryUsePortal(portal);
                bool atSpawn = Vector2.Distance(player.transform.position, stage.CurrentRoom.SpawnPosition) < .02f;
                bool cameraCorrect = stage.RoomCamera != null && stage.MapOpen &&
                    Vector3.Distance(stage.RoomCamera.transform.position, fullCameraPosition) < .002f &&
                    Mathf.Abs(stage.RoomCamera.orthographicSize - fullCameraSize) < .002f;
                int activeRooms = 0;
                foreach (var room in stage.Rooms) if (room != null && room.gameObject.activeSelf) activeRooms++;
                Record("Interaction traverses " + from + " -> " + target,
                    traversed && nearTrigger && stage.CurrentRoom.Coordinate == target &&
                    stage.TraversalCount == countBefore + 1 && atSpawn && cameraCorrect &&
                    activeRooms == RegisteredRoomCount() && player.enabled &&
                    player.GetComponent<Rigidbody2D>().simulated && player.Velocity.sqrMagnitude < .01f,
                    "Accepted " + traversed + "; room " + stage.CurrentRoom.Coordinate +
                    "; at spawn " + atSpawn + "; camera correct " + cameraCorrect +
                    "; active rooms " + activeRooms + "; traversals " + stage.TraversalCount);
                CheckLayoutPersistence("portal traversal " + from + " -> " + target);
                Record("Whole-map camera stays fixed during portal transition " + from + " -> " + target,
                    cameraCorrect, "Whole map " + stage.MapOpen + "; camera position " + stage.RoomCamera.transform.position +
                    "; before " + fullCameraPosition + "; camera size " + F(stage.RoomCamera.orthographicSize) +
                    "; before " + F(fullCameraSize));
                bool safeArrival = SafeArrival(out string arrivalEvidence);
                Record("Arrival stays clear of walls and portal triggers " + target, safeArrival, arrivalEvidence);
                Record("One-room traversal keeps global phase and mapping " + target,
                    stage.Rules.Phase == 0 && stage.Rules.DistanceFor(PortalColor.Yellow) == 1 && Mapping() == initialMapping,
                    "Phase " + stage.Rules.Phase + "; yellow distance " + stage.Rules.DistanceFor(PortalColor.Yellow));
                if (!traversed) yield break;

                // A room can omit the shape needed to return directly. Test the
                // cooldown against any valid one-room portal in the newly active
                // room rather than depending on a reverse edge or the old source.
                ShiftPortal nextPortal = FindSafeOneRoomPortal(stage.CurrentRoom);
                if (nextPortal != null)
                {
                    player.Teleport(ApproachPosition(nextPortal));
                    bool nextNear = nextPortal.CanInteract(player);
                    bool repeated = stage.TryUsePortal(nextPortal);
                    Record("Immediate repeated interaction is suppressed " + target,
                        nextNear && !repeated && stage.CurrentRoom.Coordinate == target && stage.TraversalCount == countBefore + 1,
                        "Inside a valid active-room trigger " + nextNear + "; repeated accepted " + repeated +
                        "; room " + stage.CurrentRoom.Coordinate);
                }
                else Record("Immediate repeated interaction is suppressed " + target, false, "No safe one-room portal to test a valid interaction.");
                player.Teleport(stage.CurrentRoom.SpawnPosition);
                yield return Settle(3f);
                yield return WaitForCooldown();
                Record("Leaving and arriving do not cause an automatic trigger loop " + target,
                    stage.CurrentRoom.Coordinate == target && stage.TraversalCount == countBefore + 1,
                    "Room " + stage.CurrentRoom.Coordinate + "; traversals " + stage.TraversalCount);
                Record("Whole-map framing remains stable after arrival physics " + target,
                    stage.MapOpen && Vector3.Distance(stage.RoomCamera.transform.position, fullCameraPosition) < .002f &&
                    Mathf.Abs(stage.RoomCamera.orthographicSize - fullCameraSize) < .002f,
                    "Camera position " + stage.RoomCamera.transform.position +
                    "; camera size " + F(stage.RoomCamera.orthographicSize));
                if (roomAccessChecked.Add(target))
                    yield return CheckAllRoomMounts(target);
            }
            bool allVisited = true;
            foreach (var room in stage.Rooms) if (room != null && !stage.IsVisited(room.Coordinate)) allVisited = false;
            Record("A real route visits all four registered rooms and returns to start",
                allVisited && stage.CurrentRoom.Coordinate == stage.StartCoordinate && stage.TraversalCount == route.Count,
                "All four visited " + allVisited + "; final room " + stage.CurrentRoom.Coordinate +
                "; route traversals " + stage.TraversalCount + "; actual route " + report.fourRoomRoute +
                "; each route portal was reached by normal movement and jumps from its room spawn.");
        }

        private IEnumerator CheckAllRoomMounts(Vector2Int coordinate)
        {
            int phase = stage.Rules.Phase;
            int traversals = stage.TraversalCount;
            string mapping = Mapping();
            Vector3 cameraPosition = stage.RoomCamera.transform.position;
            float cameraSize = stage.RoomCamera.orthographicSize;
            foreach (PortalShape shape in Enum.GetValues(typeof(PortalShape)))
            {
                ShiftPortal portal = null;
                foreach (var candidate in stage.CurrentRoom.GetComponentsInChildren<ShiftPortal>())
                    if (candidate.Shape == shape) { portal = candidate; break; }
                Record("Copied room has an authored " + shape + " portal " + coordinate, portal != null,
                    portal != null ? "Mount " + portal.Mount : "Portal is missing from the current room.");
                if (portal == null) continue;
                yield return WaitForCooldown();
                yield return PortalWallAccessChecks.Approach(stage, portal, Record);
                bool reached = portal.CanInteract(player);
                Record("Normal movement reaches the copied " + portal.Mount + " gate in room " + coordinate,
                    reached && player.Grounded,
                    PositionEvidence() + "; grounded " + player.Grounded + "; in interaction reach " + reached);
                Record("Copied " + portal.Mount + " gate approach preserves rules and full-map framing " + coordinate,
                    AttemptStateMatches(coordinate, phase, traversals, mapping) && stage.MapOpen &&
                    ActiveRoomCount() == RegisteredRoomCount() &&
                    Vector3.Distance(stage.RoomCamera.transform.position, cameraPosition) < .002f &&
                    Mathf.Abs(stage.RoomCamera.orthographicSize - cameraSize) < .002f,
                    "Room " + stage.CurrentRoom.Coordinate + "; phase " + stage.Rules.Phase +
                    "; traversals " + stage.TraversalCount + "; camera position " + stage.RoomCamera.transform.position +
                    "; camera size " + F(stage.RoomCamera.orthographicSize));
            }
            player.SetInput(0f, false);
            stage.Respawn();
            bool safeSpawn = SafeArrival(out string spawnEvidence);
            Record("Copied-room gate checks restore a safe route spawn " + coordinate, safeSpawn, spawnEvidence);
            yield return Settle(3f);
            yield return WaitForCooldown();
            Record("Copied-room gate checks settle before the route continues " + coordinate,
                settled && AttemptStateMatches(coordinate, phase, traversals, mapping),
                PositionEvidence() + "; grounded " + player.Grounded + "; phase " + stage.Rules.Phase +
                "; traversals " + stage.TraversalCount);
            CheckLayoutPersistence("all four gate approaches in " + coordinate);
        }

        private sealed class RouteNode
        {
            public PortalRoom room;
            public int visited;
            public List<Vector2Int> path;
        }

        private List<Vector2Int> FindFourRoomRoute()
        {
            // This finds a verification route from authored content. It never
            // modifies the game mapping, authored portal colors, or player hints.
            var rooms = new Dictionary<Vector2Int, PortalRoom>();
            var bits = new Dictionary<Vector2Int, int>();
            foreach (var room in stage.Rooms)
            {
                if (room == null) continue;
                if (rooms.Count >= 30 || rooms.ContainsKey(room.Coordinate)) return null;
                bits.Add(room.Coordinate, 1 << rooms.Count);
                rooms.Add(room.Coordinate, room);
            }
            if (rooms.Count == 0 || stage.CurrentRoom == null) return null;
            int allVisited = (1 << rooms.Count) - 1;
            var queue = new Queue<RouteNode>();
            var searched = new HashSet<string>();
            var first = new RouteNode
            {
                room = stage.CurrentRoom,
                visited = bits[stage.CurrentRoom.Coordinate],
                path = new List<Vector2Int>()
            };
            queue.Enqueue(first);
            searched.Add(first.room.Coordinate + ":" + first.visited);
            while (queue.Count > 0)
            {
                RouteNode node = queue.Dequeue();
                if (node.visited == allVisited && node.room.Coordinate == stage.StartCoordinate && node.path.Count > 0)
                    return node.path;
                foreach (var portal in node.room.GetComponentsInChildren<ShiftPortal>(true))
                {
                    if (portal.Color != PortalColor.Yellow || stage.Rules.DistanceFor(portal.Color) != 1) continue;
                    Vector2Int destination = node.room.Coordinate +
                        PortalRuleState.OffsetFor(stage.Rules.DirectionFor(portal.Shape));
                    if (destination.x < 0 || destination.x >= stage.Width ||
                        destination.y < 0 || destination.y >= stage.Height ||
                        !rooms.TryGetValue(destination, out var target)) continue;
                    int visited = node.visited | bits[destination];
                    string key = destination + ":" + visited;
                    if (!searched.Add(key)) continue;
                    var path = new List<Vector2Int>(node.path) { destination };
                    queue.Enqueue(new RouteNode { room = target, visited = visited, path = path });
                }
            }
            return null;
        }

        private ShiftPortal FindSafeOneRoomPortal(PortalRoom room)
        {
            if (room == null) return null;
            foreach (var portal in room.GetComponentsInChildren<ShiftPortal>(true))
            {
                if (stage.Rules.DistanceFor(portal.Color) != 1) continue;
                Vector2Int destination = room.Coordinate + PortalRuleState.OffsetFor(stage.Rules.DirectionFor(portal.Shape));
                if (destination.x < 0 || destination.x >= stage.Width || destination.y < 0 || destination.y >= stage.Height) continue;
                foreach (var target in stage.Rooms)
                    if (target != null && target.Coordinate == destination) return portal;
            }
            return null;
        }

        private IEnumerator CheckBlockedMagnitudeTwo()
        {
            ShiftPortal portal = null;
            foreach (var candidate in stage.CurrentRoom.GetComponentsInChildren<ShiftPortal>())
                if (stage.Rules.DistanceFor(candidate.Color) == 2) { portal = candidate; break; }
            Record("Current room exposes an authored magnitude-two portal", portal != null,
                portal != null ? portal.Color + " " + portal.Shape : "No magnitude-two portal exists.");
            if (portal == null) yield break;
            player.Teleport(ApproachPosition(portal));
            yield return Settle(3f);
            yield return WaitForCooldown();
            Vector2Int coordinate = stage.CurrentRoom.Coordinate;
            int phase = stage.Rules.Phase;
            int count = stage.TraversalCount;
            int yellow = stage.Rules.DistanceFor(PortalColor.Yellow);
            int purple = stage.Rules.DistanceFor(PortalColor.Purple);
            bool nearby = portal.CanInteract(player);
            bool accepted = stage.TryUsePortal(portal);
            Record("Bounded 2 x 2 rejects a two-room destination without flipping phase",
                nearby && !accepted && stage.CurrentRoom.Coordinate == coordinate && stage.Rules.Phase == phase &&
                stage.TraversalCount == count && stage.Rules.DistanceFor(PortalColor.Yellow) == yellow &&
                stage.Rules.DistanceFor(PortalColor.Purple) == purple,
                "Can interact " + nearby + "; accepted " + accepted + "; room " + stage.CurrentRoom.Coordinate +
                "; phase " + stage.Rules.Phase + "; yellow " + stage.Rules.DistanceFor(PortalColor.Yellow) +
                "; purple " + stage.Rules.DistanceFor(PortalColor.Purple) +
                ". Successful magnitude-two flips require extra registered coordinates; this baseline does not demonstrate them.");
        }

        private IEnumerator CheckRespawn()
        {
            Vector2Int room = stage.CurrentRoom.Coordinate;
            int phase = stage.Rules.Phase;
            int count = stage.TraversalCount;
            player.Teleport(stage.CurrentRoom.SpawnPosition + Vector2.up * 3f);
            player.SetInput(1f, true);
            yield return FixedSteps(3);
            player.SetInput(0f, false);
            stage.Respawn();
            Record("Respawn clears momentum and preserves attempt state",
                Vector2.Distance(player.transform.position, stage.CurrentRoom.SpawnPosition) < .02f &&
                player.Velocity.sqrMagnitude < .01f && stage.CurrentRoom.Coordinate == room &&
                stage.Rules.Phase == phase && stage.TraversalCount == count,
                PositionEvidence() + "; room " + room + "; phase " + stage.Rules.Phase + "; traversals " + stage.TraversalCount);
            player.Teleport((Vector2)stage.CurrentRoom.transform.position + Vector2.down * 8f);
            yield return null;
            yield return null;
            bool nearSpawn = Vector2.Distance(player.transform.position, stage.CurrentRoom.SpawnPosition) < .3f;
            Record("Falling below a room returns to a safe spawn",
                nearSpawn && stage.CurrentRoom.Coordinate == room && stage.Rules.Phase == phase && stage.TraversalCount == count,
                PositionEvidence() + "; near spawn " + nearSpawn);
            yield return Settle(3f);
        }

        private IEnumerator CheckFullMap()
        {
            var body = player.GetComponent<Rigidbody2D>();
            Camera camera = stage.RoomCamera;
            player.SetInput(0f, false);
            stage.SetMapOpen(true);
            stage.Respawn();
            yield return Settle(3f);
            yield return WaitForCooldown();

            Vector2Int coordinate = stage.CurrentRoom.Coordinate;
            int phase = stage.Rules.Phase;
            int traversals = stage.TraversalCount;
            string mapping = Mapping();
            Vector3 fullCameraPosition = camera.transform.position;
            float fullCameraSize = camera.orthographicSize;
            Record("Whole-map play keeps every room visible and physics active",
                stage.MapOpen && ActiveRoomCount() == RegisteredRoomCount() && player.enabled && body.simulated,
                MapRestorationEvidence());

            Vector2 movementOrigin = player.transform.position;
            player.SetInput(1f, false);
            yield return FixedSteps(12);
            float distance = player.transform.position.x - movementOrigin.x;
            Record("Movement remains responsive while the whole map is visible",
                stage.MapOpen && player.enabled && body.simulated && distance > .7f && player.Velocity.x > 1f &&
                AttemptStateMatches(coordinate, phase, traversals, mapping) &&
                Vector3.Distance(camera.transform.position, fullCameraPosition) < .002f &&
                Mathf.Abs(camera.orthographicSize - fullCameraSize) < .002f,
                "Scripted right movement " + F(distance) + "; " + MapRestorationEvidence());
            Vector2 movingPosition = player.transform.position;
            Vector2 movingVelocity = player.Velocity;
            stage.SetMapOpen(false);
            Record("Optional zoom preserves live movement momentum",
                movingVelocity.x > 1f &&
                ViewStateMatches(false, coordinate, phase, traversals, mapping, movingPosition, movingVelocity,
                    camera.transform.position, camera.orthographicSize),
                MapRestorationEvidence());
            stage.SetMapOpen(true);
            Record("Whole-map return preserves live movement momentum",
                ViewStateMatches(true, coordinate, phase, traversals, mapping, movingPosition, movingVelocity,
                    fullCameraPosition, fullCameraSize),
                MapRestorationEvidence());
            player.SetInput(0f, false);
            yield return FixedSteps(12);
            stage.Respawn();
            yield return Settle(3f);
            player.SetInput(0f, true);
            yield return FixedSteps(8);
            Vector2 airbornePosition = player.transform.position;
            Vector2 airborneVelocity = player.Velocity;
            bool airborne = !player.Grounded && airborneVelocity.y > 0f;
            stage.SetMapOpen(false);
            Record("Optional zoom preserves an airborne jump",
                airborne && ViewStateMatches(false, coordinate, phase, traversals, mapping, airbornePosition, airborneVelocity,
                    camera.transform.position, camera.orthographicSize),
                MapRestorationEvidence());
            stage.SetMapOpen(true);
            Record("Whole-map return preserves an airborne jump",
                airborne && ViewStateMatches(true, coordinate, phase, traversals, mapping, airbornePosition, airborneVelocity,
                    fullCameraPosition, fullCameraSize),
                MapRestorationEvidence());
            player.SetInput(0f, false);
            yield return Settle(3f);
            stage.FitMapCamera();
            fullCameraPosition = camera.transform.position;
            fullCameraSize = camera.orthographicSize;
            yield return CaptureFullMapEvidence();

            Vector2 playerPosition = player.transform.position;
            Vector2 playerVelocity = player.Velocity;
            stage.SetMapOpen(false);
            Vector3 zoomCameraPosition = camera.transform.position;
            float zoomCameraSize = camera.orthographicSize;
            Record("Optional room zoom changes framing and visible rooms while preserving play",
                ViewStateMatches(false, coordinate, phase, traversals, mapping, playerPosition, playerVelocity,
                    zoomCameraPosition, zoomCameraSize) &&
                Vector2.Distance(camera.transform.position, stage.CurrentRoom.CameraPosition) < .002f &&
                camera.orthographicSize < fullCameraSize &&
                Vector3.Distance(camera.transform.position, fullCameraPosition) > .02f,
                MapRestorationEvidence());
            stage.SetMapOpen(true);
            Record("Returning to whole-map play preserves the player and global attempt",
                ViewStateMatches(true, coordinate, phase, traversals, mapping, playerPosition, playerVelocity,
                    fullCameraPosition, fullCameraSize),
                MapRestorationEvidence());

            for (int cycle = 1; cycle <= 3; cycle++)
            {
                // Compare immediately on each side of the view change. Physics
                // continues running; a view toggle must not save/restore momentum
                // or teleport the player while normal frame simulation advances.
                playerPosition = player.transform.position;
                playerVelocity = player.Velocity;
                stage.ToggleMap();
                Record("Repeated optional zoom preserves position and momentum " + cycle,
                    ViewStateMatches(false, coordinate, phase, traversals, mapping, playerPosition, playerVelocity,
                        zoomCameraPosition, zoomCameraSize),
                    MapRestorationEvidence());
                stage.ToggleMap();
                Record("Repeated whole-map return preserves position and momentum " + cycle,
                    ViewStateMatches(true, coordinate, phase, traversals, mapping, playerPosition, playerVelocity,
                        fullCameraPosition, fullCameraSize),
                    MapRestorationEvidence());
                yield return null;
            }

            float[] aspects = { 16f / 9f, 4f / 3f, 9f / 16f };
            string[] aspectNames = { "16:9", "4:3", "9:16" };
            for (int i = 0; i < aspects.Length; i++)
            {
                camera.aspect = aspects[i];
                stage.FitMapCamera();
                yield return null;
                float minX = float.PositiveInfinity;
                float minY = float.PositiveInfinity;
                float maxX = float.NegativeInfinity;
                float maxY = float.NegativeInfinity;
                bool inFront = true;
                foreach (var room in stage.Rooms)
                {
                    if (room == null) continue;
                    foreach (float x in new[] { -stage.MapRoomWidth * .5f, stage.MapRoomWidth * .5f })
                    foreach (float y in new[] { -stage.MapRoomHeight * .5f, stage.MapRoomHeight * .5f })
                    {
                        Vector3 point = room.CameraPosition + new Vector3(x, y, 0f);
                        Vector3 viewport = camera.WorldToViewportPoint(point);
                        minX = Mathf.Min(minX, viewport.x);
                        maxX = Mathf.Max(maxX, viewport.x);
                        minY = Mathf.Min(minY, viewport.y);
                        maxY = Mathf.Max(maxY, viewport.y);
                        inFront &= viewport.z > 0f;
                    }
                }
                Record("Whole-map camera contains all room layouts at aspect " + aspectNames[i],
                    stage.MapOpen && player.enabled && body.simulated && inFront &&
                    ActiveRoomCount() == RegisteredRoomCount() && minX >= .028f && maxX <= .972f &&
                    minY >= .02f && maxY <= 1f - 40f / Screen.height - .02f,
                    "Simulated camera aspect " + F(camera.aspect) + "; viewport x[" + F(minX) + "," + F(maxX) +
                    "] y[" + F(minY) + "," + F(maxY) +
                    "]; room extents checked around every camera anchor (+/-" + F(stage.MapRoomWidth * .5f) +
                    ", +/-" + F(stage.MapRoomHeight * .5f) +
                    "); 40-pixel top margin, no footer. This is camera projection testing, not a resized human play session.");

                playerPosition = player.transform.position;
                playerVelocity = player.Velocity;
                Vector3 aspectFullPosition = camera.transform.position;
                float aspectFullSize = camera.orthographicSize;
                stage.SetMapOpen(false);
                Record("Aspect test keeps optional zoom playable " + aspectNames[i],
                    ViewStateMatches(false, coordinate, phase, traversals, mapping, playerPosition, playerVelocity,
                        camera.transform.position, camera.orthographicSize) &&
                    Vector2.Distance(camera.transform.position, stage.CurrentRoom.CameraPosition) < .002f &&
                    camera.orthographicSize < aspectFullSize,
                    MapRestorationEvidence());
                stage.SetMapOpen(true);
                Record("Aspect test returns to unchanged whole-map play " + aspectNames[i],
                    ViewStateMatches(true, coordinate, phase, traversals, mapping, playerPosition, playerVelocity,
                        aspectFullPosition, aspectFullSize),
                    MapRestorationEvidence());
            }

            camera.ResetAspect();
            stage.FitMapCamera();
            fullCameraPosition = camera.transform.position;
            fullCameraSize = camera.orthographicSize;
            stage.SetMapOpen(false);
            stage.ResetAttempt();
            Record("Restart returns to a playable whole-map start",
                stage.MapOpen && ActiveRoomCount() == RegisteredRoomCount() && stage.CurrentRoom.Coordinate == stage.StartCoordinate &&
                player.enabled && body.simulated && stage.TraversalCount == 0 && stage.Rules.Phase == 0 &&
                Vector3.Distance(camera.transform.position, fullCameraPosition) < .002f &&
                Mathf.Abs(camera.orthographicSize - fullCameraSize) < .002f,
                MapRestorationEvidence() + "; traversals " + stage.TraversalCount + "; phase " + stage.Rules.Phase);
            player.SetInput(0f, false);
            yield return Settle(3f);
        }

        private bool AttemptStateMatches(Vector2Int coordinate, int phase, int traversals, string mapping)
        {
            return stage.CurrentRoom.Coordinate == coordinate && stage.Rules.Phase == phase &&
                stage.TraversalCount == traversals && Mapping() == mapping;
        }

        private bool MapStateMatches(Vector2Int coordinate, int phase, int traversals, string mapping, Vector2 position)
        {
            return AttemptStateMatches(coordinate, phase, traversals, mapping) &&
                Vector2.Distance(player.transform.position, position) < .002f;
        }

        private bool ViewStateMatches(bool wholeMap, Vector2Int coordinate, int phase, int traversals, string mapping,
            Vector2 position, Vector2 velocity, Vector3 cameraPosition, float cameraSize)
        {
            return stage.MapOpen == wholeMap && ActiveRoomCount() == (wholeMap ? RegisteredRoomCount() : 1) &&
                stage.CurrentRoom.gameObject.activeSelf && player.enabled && player.GetComponent<Rigidbody2D>().simulated &&
                MapStateMatches(coordinate, phase, traversals, mapping, position) &&
                Vector2.Distance(player.Velocity, velocity) < .002f &&
                Vector3.Distance(stage.RoomCamera.transform.position, cameraPosition) < .002f &&
                Mathf.Abs(stage.RoomCamera.orthographicSize - cameraSize) < .002f;
        }

        private int ActiveRoomCount()
        {
            int count = 0;
            foreach (var room in stage.Rooms) if (room != null && room.gameObject.activeSelf) count++;
            return count;
        }

        private int RegisteredRoomCount()
        {
            int count = 0;
            foreach (var room in stage.Rooms) if (room != null) count++;
            return count;
        }

        private string MapRestorationEvidence()
        {
            return "Whole-map view " + stage.MapOpen + "; active rooms " + ActiveRoomCount() +
                "; controller enabled " + player.enabled + "; body simulated " + player.GetComponent<Rigidbody2D>().simulated +
                "; camera position " + stage.RoomCamera.transform.position +
                "; camera size " + F(stage.RoomCamera.orthographicSize) + "; " + PositionEvidence();
        }

        private ShiftPortal FindPortal(PortalColor color, Vector2Int step)
        {
            foreach (var portal in stage.CurrentRoom.GetComponentsInChildren<ShiftPortal>())
                if (portal.Color == color && PortalRuleState.OffsetFor(stage.Rules.DirectionFor(portal.Shape)) == step)
                    return portal;
            return null;
        }

        private IEnumerator Settle(float seconds)
        {
            settled = false;
            int maximumSteps = Mathf.CeilToInt(seconds / Time.fixedDeltaTime);
            int consecutiveGrounded = 0;
            for (int step = 0; step < maximumSteps; step++)
            {
                yield return new WaitForFixedUpdate();
                if (player.Grounded && Mathf.Abs(player.Velocity.y) < .15f) consecutiveGrounded++;
                else consecutiveGrounded = 0;
                if (consecutiveGrounded >= 4) { settled = true; yield break; }
            }
        }

        private static IEnumerator FixedSteps(int count)
        {
            for (int i = 0; i < count; i++) yield return new WaitForFixedUpdate();
        }

        private IEnumerator WaitForCooldown()
        {
            float deadline = Time.unscaledTime + stage.InteractionCooldown + .06f;
            while (Time.unscaledTime < deadline) yield return null;
        }

        private IEnumerator CaptureEvidence()
        {
            if (Application.isBatchMode)
            {
                report.screenshot = "Unavailable in batch mode; run the graphical player to capture the rendered HUD.";
                yield break;
            }
            string path = Path.Combine(outputDirectory, "portal-shift-play-checks.png");
            if (File.Exists(path)) File.Delete(path);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!File.Exists(path) && Time.realtimeSinceStartup < deadline) yield return null;
            report.screenshot = File.Exists(path) ? path : "Screenshot was requested but no file was written.";
            Record("Graphical evidence captures the rendered scene and HUD", File.Exists(path), report.screenshot);
            // Give the asynchronous image writer and display one extra frame.
            yield return null;
        }

        private IEnumerator CaptureFullMapEvidence()
        {
            if (Application.isBatchMode)
            {
                report.fullMapScreenshot = "Unavailable in batch mode; run the graphical player to capture the full map.";
                yield break;
            }
            string path = Path.Combine(outputDirectory, "portal-shift-full-map.png");
            if (File.Exists(path)) File.Delete(path);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!File.Exists(path) && Time.realtimeSinceStartup < deadline) yield return null;
            report.fullMapScreenshot = File.Exists(path) ? path : "Full-map screenshot was requested but no file was written.";
            Record("Graphical evidence captures the playable full matrix", File.Exists(path), report.fullMapScreenshot);
            yield return null;
        }

        private string Mapping()
        {
            string value = "";
            foreach (PortalShape shape in Enum.GetValues(typeof(PortalShape)))
                value += shape + "=" + stage.Rules.DirectionFor(shape) + ";";
            return value;
        }

        private string PositionEvidence()
        {
            return "Position (" + F(player.transform.position.x) + ", " + F(player.transform.position.y) +
                "); velocity (" + F(player.Velocity.x) + ", " + F(player.Velocity.y) + ")";
        }

        private static string F(float value) => value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

        private void Record(string name, bool passed, string evidence)
        {
            report.checks.Add(new CheckResult { name = name, passed = passed, evidence = evidence });
            if (passed) report.passedCount++;
            else report.failedCount++;
            Debug.Log("PORTAL_SHIFT_CHECK " + (passed ? "PASS " : "FAIL ") + name + ": " + evidence);
        }

        private void Finish()
        {
            if (finished) return;
            finished = true;
            if (player != null) player.SetInput(0f, false);
            report.finishedUtc = DateTime.UtcNow.ToString("o");
            report.passed = report.failedCount == 0 && report.passedCount > 0;
            string path = Path.Combine(outputDirectory, "portal-shift-play-checks.json");
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log("PORTAL_SHIFT_PLAY_CHECKS " + (report.passed ? "PASS" : "FAIL") +
                " passed=" + report.passedCount + " failed=" + report.failedCount + " report=" + path);
            Application.Quit(report.passed ? 0 : 1);
        }
    }
}
