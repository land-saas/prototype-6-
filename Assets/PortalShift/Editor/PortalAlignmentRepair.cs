using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PortalShift.Editor
{
    /// <summary>Explicit portal maintenance. Artwork is preserved; both cues are authored in front of it.</summary>
    public static class PortalAlignmentRepair
    {
        const string ScenePath = "Assets/PortalShift/Scenes/PortalShift.unity";
        const string PlayerPath = "Assets/PortalShift/Prefabs/Player.prefab";
        [Serializable] public sealed class Pose
        {
            public Vector3 position, scale;
            public Quaternion rotation;
            public Pose(Transform t) { if (t != null) { position = t.localPosition; scale = t.localScale; rotation = t.localRotation; } }
            public bool Matches(Transform t) => t != null && position == t.localPosition && scale == t.localScale && rotation == t.localRotation;
        }
        [Serializable] public sealed class PortalState
        {
            public string path, shape, color, mount;
            public Pose root, artwork, attachment, firstPip, secondPip;
            public Vector2 triggerOffset, triggerSize;
            public float visualRotation;
            public int firstPipOrder, secondPipOrder, firstPipLayer, secondPipLayer;
        }
        [Serializable] public sealed class AssetReport
        {
            public string path;
            public int portalCount, offsetsChanged, mountsChanged, cuePairsChanged;
            public PortalState[] before, after;
            public string[] errors, warnings;
            public bool protectedAuthoringUnchanged;
        }
        [Serializable] public sealed class Report
        {
            public string timestamp, snapshot, sceneSavedHashBefore, sceneSavedHashAfter;
            public bool sourceWasDirty, sourceLeftUnsaved, passed;
            public string[] errors, warnings;
            public AssetReport[] assets;
            public string scope = "Explicit collider-offset/mount and front-of-gate cue placement. Both cue positions and sorting are deliberately updated. Scene artwork, attachments, roots, colors, rotations, geometry and portal rules are preserved. CanInteract is exercised on temporary clones, not through human keyboard play.";
        }

        static Scene RequireScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play before repairing authored portals.");
            var scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath) throw new InvalidOperationException("Keep the current authored PortalShift.unity scene active. This repair never reloads it.");
            return scene;
        }

        [MenuItem("Portal Shift/Repairs/Align Portal Triggers and Distance Cues")]
        public static void Repair()
        {
            var scene = RequireScene();
            var report = new Report { timestamp = DateTime.UtcNow.ToString("o"), sourceWasDirty = scene.isDirty, sceneSavedHashBefore = Hash(ScenePath) };
            string folder = "Verification/PortalAlignmentBackups/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            Directory.CreateDirectory(folder);
            PortalRenderingRepair.ClearPackageImportSaveFlags();
            if (!EditorSceneManager.SaveScene(scene, folder + "/live-before-repair.unity", true)) throw new IOException("Could not snapshot the live scene. No portal changes applied.");
            report.snapshot = folder + "/live-before-repair.unity";
            var assets = new List<AssetReport>();
            var errors = new List<string>(); var warnings = new List<string>();
            var changedPrefabPaths = new List<string>();
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Align portal triggers and place distance cues in front of gates");
            try
            {
                assets.Add(RepairPortals(scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<ShiftPortal>(true)).ToArray(), ScenePath, true));
                foreach (string path in PrefabPaths())
                {
                    BackupFile(path, folder); BackupFile(path + ".meta", folder);
                    var root = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        var portals = root.GetComponentsInChildren<ShiftPortal>(true);
                        if (portals.Length == 0) continue;
                        var result = RepairPortals(portals, path, false); assets.Add(result);
                        if (result.errors.Length > 0) throw new InvalidOperationException(path + ": " + string.Join("; ", result.errors));
                        if (result.offsetsChanged + result.mountsChanged + result.cuePairsChanged > 0)
                        {
                            if (PrefabUtility.SaveAsPrefabAsset(root, path) == null) throw new IOException("Could not save " + path);
                            changedPrefabPaths.Add(path);
                        }
                    }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                }
                foreach (var asset in assets) { errors.AddRange(asset.errors.Select(e => asset.path + ": " + e)); warnings.AddRange(asset.warnings.Select(w => asset.path + ": " + w)); }
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
                Physics2D.SyncTransforms();
                ValidateSceneSafety(scene, errors, warnings);
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
                RepairGeneratedCues(folder, changedPrefabPaths, warnings);
                ValidateGeneratedScenes(assets, warnings);
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
                if (report.sceneSavedHashBefore != Hash(ScenePath)) throw new InvalidOperationException("The authored scene file changed unexpectedly during repair.");
                EditorSceneManager.MarkSceneDirty(scene);
                report.sourceLeftUnsaved = true;
                Undo.CollapseUndoOperations(group);
                SceneView.RepaintAll();
                report.passed = true;
                Debug.Log("PORTAL_ALIGNMENT_REPAIR_APPLIED scene offsets=" + assets[0].offsetsChanged + ", mounts=" + assets[0].mountsChanged + ", front-facing cue pairs=" + assets[0].cuePairsChanged + ". Artwork, attachments and geometry preserved. Both distance cues placed in front of actual gate artwork with visible sorting. Scene left UNSAVED for review; Cmd+S saves it. Existing prefab/generated assets repaired in place. Verification/portal-alignment-repair.json");
            }
            catch (Exception error)
            {
                Undo.RevertAllDownToGroup(group);
                foreach (string path in changedPrefabPaths)
                    try { File.Copy(folder + "/" + path, path, true); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate); }
                    catch (Exception restore) { errors.Add("Prefab rollback " + path + ": " + restore.Message); }
                errors.Add(error.Message);
                Debug.LogError("Portal alignment repair rolled back; snapshot retained at " + report.snapshot + ". " + error.Message);
                throw;
            }
            finally
            {
                report.sceneSavedHashAfter = Hash(ScenePath);
                report.assets = assets.ToArray(); report.errors = errors.ToArray(); report.warnings = warnings.ToArray();
                Directory.CreateDirectory("Verification"); File.WriteAllText("Verification/portal-alignment-repair.json", JsonUtility.ToJson(report, true));
                SceneManager.SetActiveScene(scene);
            }
        }

        static AssetReport RepairPortals(ShiftPortal[] portals, string path, bool undo)
        {
            var report = new AssetReport { path = path, portalCount = portals.Length, before = portals.Select(Capture).ToArray() };
            var errors = new List<string>(); var warnings = new List<string>();
            var oldOverlaps = OverlapPairs(portals);
            foreach (var portal in portals)
            {
                var box = portal.GetComponent<BoxCollider2D>();
                if (box == null || portal.ShapeRenderer == null) { errors.Add(ObjectPath(portal.transform) + " has missing trigger/artwork references."); continue; }
                Vector2 desired = portal.transform.InverseTransformPoint(portal.ShapeRenderer.transform.position);
                bool misaligned = (box.offset - desired).sqrMagnitude > .000001f;
                if (misaligned)
                {
                    if (undo) Undo.RecordObject(box, "Align interaction box to authored portal artwork");
                    portal.AlignInteractionToArtwork(); report.offsetsChanged++;
                }
                // Match physical floor metadata even if a runtime probe already
                // aligned the box. Other mount identities remain authored.
                if (portal.WallAttachment != null)
                {
                    Vector2 delta = portal.WallAttachment.transform.position - portal.ShapeRenderer.transform.position;
                    if (portal.Mount != PortalMount.Floor && delta.y < -.15f && Mathf.Abs(delta.x) < .8f)
                    {
                        if (undo) Undo.RecordObject(portal, "Match floor mount to authored attachment");
                        portal.Mount = PortalMount.Floor; report.mountsChanged++;
                    }
                }
                if (HasPips(portal)) { if (PlaceFrontCues(portal, undo)) report.cuePairsChanged++; }
                else errors.Add(ObjectPath(portal.transform) + " needs two local distance-cue references.");
                if (undo) { EditorUtility.SetDirty(box); EditorUtility.SetDirty(portal); PrefabUtility.RecordPrefabInstancePropertyModifications(box); PrefabUtility.RecordPrefabInstancePropertyModifications(portal); }
            }
            report.after = portals.Select(Capture).ToArray();
            report.protectedAuthoringUnchanged = portals.Select((p, i) => ProtectedMatches(report.before[i], p)).All(ok => ok);
            if (!report.protectedAuthoringUnchanged) errors.Add("Protected artwork/attachment/root authoring changed.");
            foreach (string overlap in OverlapPairs(portals).Except(oldOverlaps)) errors.Add("New overlapping portal trigger pair: " + overlap);
            foreach (string overlap in OverlapPairs(portals).Intersect(oldOverlaps)) warnings.Add("Pre-existing overlapping trigger pair retained: " + overlap);
            foreach (var portal in portals) if (HasPips(portal) && portal.ShapeRenderer != null) ValidateFrontCues(portal, errors);
            ValidatePortalClones(portals, errors);
            report.errors = errors.ToArray(); report.warnings = warnings.ToArray(); return report;
        }

        [MenuItem("Portal Shift/Repairs/Validate Portal Alignment (Read Only)")]
        public static void Validate()
        {
            var scene = RequireScene(); bool dirty = scene.isDirty; string hash = Hash(ScenePath);
            var assets = new List<AssetReport>(); var warnings = new List<string>(); var errors = new List<string>();
            assets.Add(InspectPortals(scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<ShiftPortal>(true)).ToArray(), ScenePath));
            foreach (string path in PrefabPaths())
            {
                var root = PrefabUtility.LoadPrefabContents(path);
                try { var portals = root.GetComponentsInChildren<ShiftPortal>(true); if (portals.Length > 0) assets.Add(InspectPortals(portals, path)); }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            ValidateGeneratedScenes(assets, warnings);
            ValidateSceneSafety(scene, errors, warnings);
            errors.AddRange(assets.SelectMany(a => a.errors.Select(e => a.path + ": " + e)));
            if (scene.isDirty != dirty || Hash(ScenePath) != hash) errors.Add("Read-only validation unexpectedly changed authored scene state.");
            var report = new Report { timestamp = DateTime.UtcNow.ToString("o"), sourceWasDirty = dirty, sourceLeftUnsaved = scene.isDirty,
                sceneSavedHashBefore = hash, sceneSavedHashAfter = Hash(ScenePath), assets = assets.ToArray(), errors = errors.ToArray(), warnings = warnings.ToArray(), passed = errors.Count == 0 };
            Directory.CreateDirectory("Verification"); File.WriteAllText("Verification/portal-alignment-validation.json", JsonUtility.ToJson(report, true));
            if (report.passed) Debug.Log("PORTAL_ALIGNMENT_VALIDATION_PASSED " + assets.Sum(a => a.portalCount) + " portals; artwork, trigger alignment and actual CanInteract clone probes checked. Verification/portal-alignment-validation.json");
            else Debug.LogWarning("PORTAL_ALIGNMENT_VALIDATION_FAILED " + string.Join("; ", report.errors));
        }

        static AssetReport InspectPortals(ShiftPortal[] portals, string path)
        {
            var errors = new List<string>(); var warnings = new List<string>();
            foreach (var portal in portals)
            {
                var box = portal.GetComponent<BoxCollider2D>();
                if (box == null || portal.ShapeRenderer == null) { errors.Add(ObjectPath(portal.transform) + ": missing artwork/trigger."); continue; }
                Vector2 desired = portal.transform.InverseTransformPoint(portal.ShapeRenderer.transform.position);
                if ((box.offset - desired).sqrMagnitude > .000001f) errors.Add(ObjectPath(portal.transform) + ": trigger offset differs from artwork center.");
                if (!box.isTrigger) errors.Add(ObjectPath(portal.transform) + ": collider is not a trigger.");
                if (!HasPips(portal)) errors.Add(ObjectPath(portal.transform) + ": distance cues missing.");
                else ValidateFrontCues(portal, errors);
            }
            ValidatePortalClones(portals, errors);
            return new AssetReport { path = path, portalCount = portals.Length, before = portals.Select(Capture).ToArray(), after = portals.Select(Capture).ToArray(),
                protectedAuthoringUnchanged = true, errors = errors.ToArray(), warnings = warnings.ToArray() };
        }

        static void ValidateGeneratedScenes(List<AssetReport> assets, List<string> warnings)
        {
            foreach (string path in AssetDatabase.FindAssets("t:Scene", new[] { "Assets/PortalShift/Connectors/Generated" }).Select(AssetDatabase.GUIDToAssetPath))
            {
                var preview = EditorSceneManager.OpenPreviewScene(path);
                try
                {
                    var result = InspectPortals(preview.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<ShiftPortal>(true)).ToArray(), path);
                    // Generated assets are inspected but never rewritten by this repair.
                    foreach (string error in result.errors) warnings.Add(path + " requires an explicit generated-scene repair: " + error);
                    assets.Add(result);
                }
                finally { EditorSceneManager.ClosePreviewScene(preview); }
            }
        }

        static void RepairGeneratedCues(string folder, List<string> changedPaths, List<string> notes)
        {
            var active = SceneManager.GetActiveScene();
            foreach (string path in AssetDatabase.FindAssets("t:Scene", new[] { "Assets/PortalShift/Connectors/Generated" }).Select(AssetDatabase.GUIDToAssetPath))
            {
                if (SceneManager.GetSceneByPath(path).isLoaded)
                {
                    notes.Add(path + ": already open; generated-scene cue repair skipped to preserve live editing.");
                    continue;
                }
                BackupFile(path, folder); BackupFile(path + ".meta", folder);
                var generated = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                try
                {
                    int count = 0;
                    var portals = generated.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<ShiftPortal>(true)).ToArray();
                    var protectedBefore = portals.Select(Capture).ToArray();
                    foreach (var portal in portals)
                    {
                        if (!HasPips(portal)) continue;
                        if (PlaceFrontCues(portal, false)) { notes.Add(path + "/" + ObjectPath(portal.transform) + ": both cues placed in front of artwork; artwork/attachment preserved."); count++; }
                    }
                    if (!portals.Select((p, i) => ProtectedMatches(protectedBefore[i], p)).All(ok => ok))
                        throw new InvalidOperationException("Generated scene protected authoring changed unexpectedly: " + path);
                    if (count > 0)
                    {
                        PortalRenderingRepair.ClearPackageImportSaveFlags();
                        if (!EditorSceneManager.SaveScene(generated, path)) throw new IOException("Could not save generated front-cue repair: " + path);
                        changedPaths.Add(path);
                    }
                }
                finally { EditorSceneManager.CloseScene(generated, true); SceneManager.SetActiveScene(active); }
            }
        }

        static void ValidatePortalClones(ShiftPortal[] portals, List<string> errors)
        {
            var probeRoot = PrefabUtility.LoadPrefabContents(PlayerPath);
            try
            {
                var player = probeRoot.GetComponent<PortalPlayerController>();
                if (player == null) { errors.Add("Player prefab lacks PortalPlayerController for CanInteract probes."); return; }
                foreach (var original in portals)
                {
                    if (original.ShapeRenderer == null) continue;
                    var cloneRoot = Object.Instantiate(original.gameObject);
                    SceneManager.MoveGameObjectToScene(cloneRoot, probeRoot.scene);
                    try
                    {
                        cloneRoot.SetActive(true); var clone = cloneRoot.GetComponent<ShiftPortal>();
                        clone.enabled = true; cloneRoot.transform.localScale = original.transform.lossyScale;
                        player.transform.position = clone.ShapeRenderer.transform.position; Physics2D.SyncTransforms();
                        if (!clone.CanInteract(player)) errors.Add(ObjectPath(original.transform) + ": CanInteract failed at the visual portal center.");
                        if (original.Room != null && original.Room.Spawn != null)
                        {
                            player.transform.position = original.Room.SpawnPosition; Physics2D.SyncTransforms();
                            if (clone.CanInteract(player)) errors.Add(ObjectPath(original.transform) + ": arrival spawn is inside portal interaction reach.");
                        }
                    }
                    finally { Object.DestroyImmediate(cloneRoot); }
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(probeRoot); }
        }

        static void ValidateSceneSafety(Scene scene, List<string> errors, List<string> warnings)
        {
            var stage = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PortalStage>(true)).Single();
            float radius = stage.Player.GetComponent<CircleCollider2D>().radius * Mathf.Abs(stage.Player.transform.lossyScale.x);
            foreach (var room in stage.Rooms)
            {
                Vector2 spawn = room.SpawnPosition;
                var solids = room.GetComponentsInChildren<BoxCollider2D>(true).Where(b => b.enabled && !b.isTrigger).ToArray();
                // The authored spawn intentionally settles onto Starting Platform:
                // y=-2.7 with r=.42 puts its feet at -3.12, .38 above the -3.5 top.
                // Accept a short, clear vertical drop instead of requiring contact
                // before the first physics step. Do not move the spawn or platform.
                var landings = solids.Select(b => new { Box = b, Bounds = BoundsOf(b) })
                    .Where(b => spawn.x - radius >= b.Bounds.min.x - .03f && spawn.x + radius <= b.Bounds.max.x + .03f &&
                        spawn.y - radius - b.Bounds.max.y >= -.035f && spawn.y - radius - b.Bounds.max.y <= 1f)
                    .OrderByDescending(b => b.Bounds.max.y).ToArray();
                bool support = false;
                foreach (var landing in landings)
                {
                    float landingCenter = landing.Bounds.max.y + radius;
                    bool blocked = solids.Where(b => b != landing.Box).Any(b =>
                    {
                        var bounds = BoundsOf(b);
                        return spawn.x > bounds.min.x - radius + .025f && spawn.x < bounds.max.x + radius - .025f &&
                            Mathf.Min(spawn.y, landingCenter) < bounds.max.y + radius - .025f && Mathf.Max(spawn.y, landingCenter) > bounds.min.y - radius + .025f;
                    });
                    if (blocked) continue;
                    support = true;
                    float drop = spawn.y - landingCenter;
                    if (drop > .17f) warnings.Add(room.name + ": safe authored spawn settles " + drop.ToString("0.###") + " units onto " + landing.Box.name + "; spawn and geometry preserved.");
                    break;
                }
                if (!support) errors.Add(room.name + ": no clear, supported spawn drop within one world unit.");
                foreach (var box in solids)
                {
                    var b = BoundsOf(box); Vector2 closest = new Vector2(Mathf.Clamp(spawn.x, b.min.x, b.max.x), Mathf.Clamp(spawn.y, b.min.y, b.max.y));
                    if (Vector2.Distance(spawn, closest) < radius - .035f) errors.Add(room.name + ": spawn overlaps solid " + box.name);
                }
            }
        }

        struct CueFrame
        {
            public Vector3 First, Second;
            public int Layer, Order;
        }

        static CueFrame FrontCueFrame(ShiftPortal portal)
        {
            var artwork = portal.ShapeRenderer;
            var bounds = SpriteBounds(artwork);
            Vector2 direction = portal.WallAttachment != null ? (Vector2)(bounds.center - portal.WallAttachment.transform.position) : Vector2.zero;
            if (direction.sqrMagnitude < .0001f)
            {
                Vector2 local = portal.Mount == PortalMount.Left ? Vector2.right : portal.Mount == PortalMount.Right ? Vector2.left :
                    portal.Mount == PortalMount.Ceiling ? Vector2.down : Vector2.up;
                direction = portal.transform.TransformDirection(local);
            }
            // Slightly off-center hand placement must not tilt the cue pair.
            // The wall-to-artwork vector identifies the physical room-facing side;
            // it is independent of the hidden shape-to-room traversal direction.
            Vector2 normal = Mathf.Abs(direction.x) > Mathf.Abs(direction.y)
                ? new Vector2(Mathf.Sign(direction.x), 0) : new Vector2(0, Mathf.Sign(direction.y));
            Vector2 tangent = Mathf.Abs(normal.y) > .5f ? Vector2.right : Vector2.up;
            float edge = Mathf.Abs(normal.x) * bounds.extents.x + Mathf.Abs(normal.y) * bounds.extents.y;
            float distance = Mathf.Max(.6f, edge + .18f);
            Vector3 first = bounds.center + (Vector3)(normal * distance);
            first.z = bounds.center.z - .01f;

            var room = portal.Room;
            var renderers = room != null ? room.GetComponentsInChildren<SpriteRenderer>(true) : portal.GetComponentsInChildren<SpriteRenderer>(true);
            var allPortals = room != null ? room.GetComponentsInChildren<ShiftPortal>(true) : new[] { portal };
            var cues = new HashSet<SpriteRenderer>(allPortals.Where(p => p.DistancePips != null).SelectMany(p => p.DistancePips).Where(p => p != null));
            var otherArt = renderers.Where(r => !cues.Contains(r)).ToArray();
            int layer = otherArt.OrderByDescending(r => SortingLayer.GetLayerValueFromID(r.sortingLayerID)).Select(r => r.sortingLayerID).FirstOrDefault();
            int order = Mathf.Min(32767, otherArt.Select(r => r.sortingOrder).DefaultIfEmpty(0).Max() + 2);
            return new CueFrame { First = first, Second = first + (Vector3)(tangent * .18f), Layer = layer, Order = order };
        }

        static bool PlaceFrontCues(ShiftPortal portal, bool undo)
        {
            var frame = FrontCueFrame(portal);
            bool changed = false;
            for (int i = 0; i < 2; i++)
            {
                var cue = portal.DistancePips[i]; var desired = i == 0 ? frame.First : frame.Second;
                bool needsChange = Vector3.Distance(cue.transform.position, desired) > .0001f || cue.sortingLayerID != frame.Layer ||
                    cue.sortingOrder != frame.Order || cue.color != Color.white || !cue.gameObject.activeSelf;
                if (!needsChange) continue;
                if (undo) Undo.RecordObjects(new Object[] { cue, cue.transform, cue.gameObject }, "Place white distance cue in front of gate");
                cue.transform.position = desired;
                cue.sortingLayerID = frame.Layer; cue.sortingOrder = frame.Order; cue.color = Color.white;
                cue.gameObject.SetActive(true);
                PrefabUtility.RecordPrefabInstancePropertyModifications(cue.transform);
                PrefabUtility.RecordPrefabInstancePropertyModifications(cue);
                PrefabUtility.RecordPrefabInstancePropertyModifications(cue.gameObject);
                changed = true;
            }
            return changed;
        }

        static void ValidateFrontCues(ShiftPortal portal, List<string> errors)
        {
            var frame = FrontCueFrame(portal);
            for (int i = 0; i < 2; i++)
            {
                var cue = portal.DistancePips[i]; var desired = i == 0 ? frame.First : frame.Second;
                if (Vector3.Distance(cue.transform.position, desired) > .02f)
                    errors.Add(ObjectPath(portal.transform) + ": cue " + (i + 1) + " is not in front of the actual gate artwork.");
                if (cue.sortingLayerID != frame.Layer || cue.sortingOrder < frame.Order)
                    errors.Add(ObjectPath(portal.transform) + ": cue " + (i + 1) + " sorting does not clear gate/attachment/platform artwork.");
                if (cue.color != Color.white || !cue.gameObject.activeSelf)
                    errors.Add(ObjectPath(portal.transform) + ": cue " + (i + 1) + " is not an active white distance cue.");
            }
        }

        static Bounds SpriteBounds(SpriteRenderer renderer)
        {
            if (renderer.sprite == null) return new Bounds(renderer.transform.position, Vector3.zero);
            var local = renderer.sprite.bounds;
            var bounds = new Bounds(renderer.transform.TransformPoint(local.center), Vector3.zero);
            foreach (float x in new[] { -.5f, .5f }) foreach (float y in new[] { -.5f, .5f })
                bounds.Encapsulate(renderer.transform.TransformPoint(local.center + Vector3.Scale(local.size, new Vector3(x, y, 0))));
            return bounds;
        }
        static bool HasPips(ShiftPortal p) => p.DistancePips != null && p.DistancePips.Length >= 2 && p.DistancePips[0] != null && p.DistancePips[1] != null;
        static PortalState Capture(ShiftPortal p)
        {
            var box = p.GetComponent<BoxCollider2D>();
            return new PortalState { path = ObjectPath(p.transform), shape = p.Shape.ToString(), color = p.Color.ToString(), mount = p.Mount.ToString(),
                root = new Pose(p.transform), artwork = p.ShapeRenderer != null ? new Pose(p.ShapeRenderer.transform) : null,
                attachment = p.WallAttachment != null ? new Pose(p.WallAttachment.transform) : null,
                firstPip = HasPips(p) ? new Pose(p.DistancePips[0].transform) : null, secondPip = HasPips(p) ? new Pose(p.DistancePips[1].transform) : null,
                triggerOffset = box != null ? box.offset : Vector2.zero, triggerSize = box != null ? box.size : Vector2.zero, visualRotation = p.VisualRotationDegrees,
                firstPipOrder = HasPips(p) ? p.DistancePips[0].sortingOrder : 0, secondPipOrder = HasPips(p) ? p.DistancePips[1].sortingOrder : 0,
                firstPipLayer = HasPips(p) ? p.DistancePips[0].sortingLayerID : 0, secondPipLayer = HasPips(p) ? p.DistancePips[1].sortingLayerID : 0 };
        }
        static bool ProtectedMatches(PortalState before, ShiftPortal p) => before.root.Matches(p.transform) &&
            before.shape == p.Shape.ToString() && before.color == p.Color.ToString() && before.visualRotation == p.VisualRotationDegrees &&
            before.artwork != null && before.artwork.Matches(p.ShapeRenderer.transform) && before.attachment != null && before.attachment.Matches(p.WallAttachment.transform) &&
            before.triggerSize == p.GetComponent<BoxCollider2D>().size;
        static HashSet<string> OverlapPairs(ShiftPortal[] portals)
        {
            var result = new HashSet<string>();
            for (int a = 0; a < portals.Length; a++) for (int b = a + 1; b < portals.Length; b++)
            {
                if (portals[a].Room != portals[b].Room) continue;
                var x = portals[a].GetComponent<BoxCollider2D>(); var y = portals[b].GetComponent<BoxCollider2D>(); if (x == null || y == null) continue;
                var bx = BoundsOf(x); var by = BoundsOf(y);
                if (bx.min.x < by.max.x && bx.max.x > by.min.x && bx.min.y < by.max.y && bx.max.y > by.min.y)
                    result.Add(ObjectPath(portals[a].transform) + " / " + ObjectPath(portals[b].transform));
            }
            return result;
        }
        static Bounds BoundsOf(BoxCollider2D box)
        {
            var result = new Bounds(box.transform.TransformPoint(box.offset), Vector3.zero);
            foreach (float x in new[] { -.5f, .5f }) foreach (float y in new[] { -.5f, .5f }) result.Encapsulate(box.transform.TransformPoint(box.offset + Vector2.Scale(box.size, new Vector2(x, y))));
            return result;
        }
        static IEnumerable<string> PrefabPaths() => AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/PortalShift" }).Select(AssetDatabase.GUIDToAssetPath);
        static string ObjectPath(Transform t) { string result = t.name; while (t.parent != null) { t = t.parent; result = t.name + "/" + result; } return result; }
        static void BackupFile(string path, string folder) { if (!File.Exists(path)) return; string destination = folder + "/" + path; Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(path, destination, false); }
        static string Hash(string path) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant(); }
    }
}
