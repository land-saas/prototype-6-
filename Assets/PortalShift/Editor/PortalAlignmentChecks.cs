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
    /// <summary>Explicit isolated checks. Never repairs or saves an authored scene or prefab.</summary>
    public static class PortalAlignmentChecks
    {
        [Serializable]
        sealed class PortalAudit
        {
            public string path, mountWarning;
            public Vector2 storedOffset, requiredOffset;
            public float pipSeparation;
            public bool triggerAligned;
        }
        [Serializable]
        sealed class Report
        {
            public bool passed, authoredTransformsPreserved, activeScenePreserved, sourceWasDirty, sourceStillDirty;
            public int assertions;
            public string checkedAtUtc, unityVersion, failure;
            public PortalAudit[] currentScene;
            public string scope = "Isolated synthetic Editor objects exercise real ShiftPortal refresh and interaction geometry, phase/reset and transform preservation. Open scene is audited read-only. No physical movement, four-room route, runtime build or human keyboard play claim.";
        }
        sealed class Pose
        {
            public Transform Transform;
            public Vector3 Position, Scale;
            public Quaternion Rotation;
            public Pose(Transform value) { Transform = value; Position = value.localPosition; Scale = value.localScale; Rotation = value.localRotation; }
            public bool Unchanged => Transform != null && Transform.localPosition == Position && Transform.localScale == Scale && Transform.localRotation == Rotation;
        }
        sealed class Fixture
        {
            public GameObject Root;
            public ShiftPortal Portal;
            public BoxCollider2D Trigger;
            public SpriteRenderer Shape, Attachment;
            public SpriteRenderer[] Pips;
        }

        [MenuItem("Portal Shift/Repairs/Run Portal Alignment Checks")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Run isolated alignment checks outside Play Mode.");
            var source = SceneManager.GetActiveScene(); var selection = Selection.objects;
            var authored = source.IsValid() && source.isLoaded
                ? source.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => new Pose(t)).ToArray()
                : Array.Empty<Pose>();
            var report = new Report { checkedAtUtc = DateTime.UtcNow.ToString("o"), unityVersion = Application.unityVersion, sourceWasDirty = source.isDirty };
            var temporary = EditorSceneManager.NewPreviewScene();
            Action<bool, string> check = (ok, message) =>
            { report.assertions++; if (!ok) throw new InvalidOperationException("Portal alignment check failed: " + message); };
            try
            {
                var fixture = CreateFixture(temporary);
                var playerObject = new GameObject("Synthetic alignment player");
                SceneManager.MoveGameObjectToScene(playerObject, temporary);
                playerObject.AddComponent<Rigidbody2D>().simulated = false;
                playerObject.AddComponent<CircleCollider2D>();
                var player = playerObject.AddComponent<PortalPlayerController>();
                var poses = fixture.Root.GetComponentsInChildren<Transform>(true).Select(t => new Pose(t)).ToArray();
                var boxSize = fixture.Trigger.size;
                var yellow = new PortalRuleState(42606);
                var flipped = FlippedRules();
                string initialRules = JsonUtility.ToJson(yellow), flippedRules = JsonUtility.ToJson(flipped);

                InvokeLifecycle(fixture.Portal, "Awake");
                CheckAlignment(check, fixture, "Awake");
                player.transform.position = fixture.Shape.transform.position;
                Physics2D.SyncTransforms();
                check(fixture.Portal.CanInteract(player), "interaction succeeds at displaced visible glyph");
                player.transform.position = fixture.Root.transform.position;
                Physics2D.SyncTransforms();
                check(!fixture.Portal.CanInteract(player), "interaction rejects the old parent location");

                fixture.Portal.Refresh(yellow);
                check(fixture.Pips[0].enabled && !fixture.Pips[1].enabled, "yellow magnitude one displays one authored pip");
                CheckAlignment(check, fixture, "initial refresh");
                fixture.Portal.Refresh(flipped);
                check(fixture.Pips.All(p => p.enabled), "yellow magnitude two displays two separated authored pips");
                check(Vector3.Distance(fixture.Pips[0].transform.position, fixture.Pips[1].transform.position) > .1f, "paired pip cue stays separated");
                check(poses.All(p => p.Unchanged), "phase changes preserve every authored transform including rotation and scale");

                for (int i = 0; i < 40; i++)
                {
                    fixture.Portal.Refresh(flipped); fixture.Portal.Refresh(yellow);
                    check(poses.All(p => p.Unchanged), "repeated refresh has no accumulated position drift, cycle " + i);
                }
                fixture.Portal.Color = PortalColor.Purple;
                fixture.Portal.Refresh(yellow);
                check(fixture.Pips.All(p => p.enabled), "purple initially displays two pips");
                fixture.Portal.Refresh(flipped);
                check(fixture.Pips[0].enabled && !fixture.Pips[1].enabled, "purple displays one pip after phase flip");
                fixture.Portal.Color = PortalColor.Yellow;
                flipped.Reset(42606); fixture.Portal.Refresh(flipped);
                check(flipped.Phase == 0 && fixture.Pips[0].enabled && !fixture.Pips[1].enabled, "reset restores initial distance cue");
                check(poses.All(p => p.Unchanged) && fixture.Trigger.size == boxSize, "reset preserves artwork, attachments, pips and trigger size");
                check(JsonUtility.ToJson(yellow) == initialRules, "refresh never changes portal-rule state");
                check(initialRules != flippedRules, "phase fixture differs from initial assignment");

                // A later child edit must become interactive without a reset, while leaving the edited child untouched.
                fixture.Shape.transform.localPosition += new Vector3(1.2f, 3.1f, 0f);
                var editedPose = new Pose(fixture.Shape.transform);
                player.transform.position = fixture.Shape.transform.position;
                Physics2D.SyncTransforms();
                check(fixture.Portal.CanInteract(player), "CanInteract follows a child moved after refresh");
                CheckAlignment(check, fixture, "live child movement");
                check(editedPose.Unchanged, "interaction never moves the edited artwork");
                fixture.Root.transform.localRotation = Quaternion.Euler(0, 0, 23);
                fixture.Root.transform.localScale = new Vector3(1.4f, .8f, 1f);
                var scaledPoses = fixture.Root.GetComponentsInChildren<Transform>(true).Select(t => new Pose(t)).ToArray();
                fixture.Portal.Refresh(yellow); Physics2D.SyncTransforms();
                CheckAlignment(check, fixture, "scaled and rotated parent");
                check(scaledPoses.All(p => p.Unchanged), "scaled/rotated authored transforms remain intact");

                InvokeLifecycle(fixture.Portal, "OnValidate");
                check(scaledPoses.All(p => p.Unchanged), "OnValidate does not rewrite authored positions");
                fixture.Portal.DistancePips = null;
                fixture.Portal.Refresh(yellow);
                check(fixture.Trigger.size == boxSize, "missing optional pip array does not break interaction geometry");
                fixture.Portal.ShapeRenderer = null;
                var offsetBefore = fixture.Trigger.offset;
                fixture.Portal.AlignInteractionToArtwork();
                check(fixture.Trigger.offset == offsetBefore, "missing artwork retains an explicitly authored collider offset");
                report.currentScene = Audit(source);
                check(authored.All(p => p.Unchanged), "all open scene authored transforms remain unchanged");
                report.passed = true;
                Debug.Log("PORTAL_ALIGNMENT_CHECKS_PASSED " + report.assertions + " synthetic assertions. Current authored scene audited without saving or repairing it.");
            }
            catch (Exception error) { report.failure = error.Message; Debug.LogError(error); throw; }
            finally
            {
                EditorSceneManager.ClosePreviewScene(temporary);
                if (source.IsValid() && source.isLoaded) SceneManager.SetActiveScene(source);
                Selection.objects = selection.Where(o => o != null).ToArray();
                report.authoredTransformsPreserved = authored.All(p => p.Unchanged);
                report.activeScenePreserved = SceneManager.GetActiveScene() == source;
                report.sourceStillDirty = source.IsValid() && source.isLoaded && source.isDirty;
                report.passed &= report.authoredTransformsPreserved && report.activeScenePreserved && report.sourceStillDirty == report.sourceWasDirty;
                string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Verification", "portal-alignment-checks.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, JsonUtility.ToJson(report, true));
            }
        }

        static Fixture CreateFixture(Scene scene)
        {
            var root = new GameObject("Synthetic moved-art portal"); SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.position = new Vector3(1000, 1000, 0);
            var box = root.AddComponent<BoxCollider2D>(); box.isTrigger = true; box.size = new Vector2(2.2f, 1.25f);
            var portal = root.AddComponent<ShiftPortal>();
            var shape = Child(root, "Shape", new Vector3(.28f, -6.33f, 0));
            shape.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/PortalShift/Art/Hexagon.png");
            shape.transform.localRotation = Quaternion.Euler(0, 0, 180);
            shape.transform.localScale = new Vector3(.9f, 1.1f, 1);
            var attachment = Child(root, "Wall Attachment", new Vector3(.34f, -6.73f, -.1f));
            var pips = new[] { Child(root, "Pip 1", new Vector3(.32f, -5.78f, -.1f)), Child(root, "Pip 2", new Vector3(.50f, -5.78f, -.1f)) };
            portal.Shape = PortalShape.Hexagon; portal.Color = PortalColor.Yellow; portal.Mount = PortalMount.Ceiling;
            portal.ShapeRenderer = shape; portal.WallAttachment = attachment; portal.DistancePips = pips; portal.VisualRotationDegrees = 180;
            return new Fixture { Root = root, Portal = portal, Trigger = box, Shape = shape, Attachment = attachment, Pips = pips };
        }

        static SpriteRenderer Child(GameObject root, string name, Vector3 position)
        {
            var child = new GameObject(name); child.transform.SetParent(root.transform, false); child.transform.localPosition = position;
            return child.AddComponent<SpriteRenderer>();
        }

        static void InvokeLifecycle(ShiftPortal portal, string method)
        {
            // Preview-scene behaviours are intentionally not running. Invoke
            // the managed method directly instead of asking Unity to dispatch
            // a behaviour message, which asserts on non-running previews.
            typeof(ShiftPortal).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(portal, null);
        }

        static void CheckAlignment(Action<bool, string> check, Fixture fixture, string label)
        {
            Vector2 wanted = fixture.Root.transform.InverseTransformPoint(fixture.Shape.transform.position);
            check(Vector2.Distance(fixture.Trigger.offset, wanted) < .0002f, label + ": trigger offset follows artwork in parent-local coordinates");
            Physics2D.SyncTransforms();
            check(Vector2.Distance(fixture.Trigger.bounds.center, fixture.Shape.transform.position) < .001f, label + ": effective trigger bounds center follows the visible child");
        }

        static PortalRuleState FlippedRules()
        {
            var rules = new PortalRuleState(42606);
            var upShape = Enumerable.Range(0, 4).Select(i => (PortalShape)i).First(s => rules.DirectionFor(s) == CardinalDirection.Up);
            var rooms = new HashSet<Vector2Int>(); for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) rooms.Add(new Vector2Int(x, y));
            Vector2Int target; string reason;
            if (!rules.TryTraverse(Vector2Int.zero, upShape, PortalColor.Purple, 4, 4, rooms, out target, out reason)) throw new InvalidOperationException(reason);
            return rules;
        }

        static PortalAudit[] Audit(Scene source)
        {
            if (!source.IsValid() || !source.isLoaded) return Array.Empty<PortalAudit>();
            return source.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<ShiftPortal>(true)).Select(portal =>
            {
                var box = portal.GetComponent<BoxCollider2D>();
                Vector2 wanted = portal.ShapeRenderer != null ? (Vector2)portal.transform.InverseTransformPoint(portal.ShapeRenderer.transform.position) : box.offset;
                float separation = portal.DistancePips != null && portal.DistancePips.Length > 1 && portal.DistancePips[0] != null && portal.DistancePips[1] != null
                    ? Vector3.Distance(portal.DistancePips[0].transform.position, portal.DistancePips[1].transform.position) : -1;
                string warning = "";
                if (portal.ShapeRenderer != null && portal.WallAttachment != null && portal.Mount == PortalMount.Ceiling && portal.WallAttachment.transform.position.y < portal.ShapeRenderer.transform.position.y)
                    warning = "Authored artwork/attachment appear floor-mounted while Mount remains Ceiling. Geometry is preserved; inspect mount metadata separately.";
                return new PortalAudit { path = portal.Room != null ? portal.Room.name + "/" + portal.name : portal.name,
                    storedOffset = box.offset, requiredOffset = wanted, triggerAligned = Vector2.Distance(box.offset, wanted) < .0002f, pipSeparation = separation, mountWarning = warning };
            }).ToArray();
        }
    }
}
