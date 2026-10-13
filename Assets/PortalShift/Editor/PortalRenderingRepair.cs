using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PortalShift.Editor
{
    /// <summary>Explicit Unity 6.5 / URP 17.5 migration. Never runs during import or edits scene content.</summary>
    public static class PortalRenderingRepair
    {
        const string AuthoredScene = "Assets/PortalShift/Scenes/PortalShift.unity";
        const string Package = "Packages/com.unity.render-pipelines.universal";
        const string Folder = "Assets/PortalShift/Rendering";
        const string RendererPath = Folder + "/PortalShift2DRenderer.asset";
        const string PipelinePath = Folder + "/PortalShiftURP.asset";
        const string GlobalPath = Folder + "/PortalShiftURPGlobalSettings.asset";
        const string MaterialPath = "Assets/PortalShift/Art/UnlitSprite.mat";
        const string SpriteShaderName = "Universal Render Pipeline/2D/Sprite-Unlit-Default";
        const string GraphicsPath = "ProjectSettings/GraphicsSettings.asset";
        const string QualityPath = "ProjectSettings/QualitySettings.asset";

        [Serializable] public sealed class Report
        {
            public string timestamp, scene, unityVersion, defaultPipeline, currentPipeline, globalSettings;
            public string renderer, rendererDefaultMaterial, postProcessData, material, materialGuid, shader;
            public string[] qualityOverrides, unexpectedSpriteMaterials, errors;
            public int spriteCount;
            public bool shaderSupported, globalSettingsPresent, rendererResourcesInitialized, qualityInheritsDefault, allSpritesUseSharedMaterial, passed;
            public string scope = "Read-only pipeline, resource and sprite-material checks. No geometry edits, scene save, runtime movement test or visual screenshot verification.";
        }

        [MenuItem("Portal Shift/Repairs/Migrate Sprite Rendering to URP 2D")]
        public static void MigrateToUrp2D()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play before migrating rendering. Play-mode scene edits are not saved by this repair.");
            var scene = SceneManager.GetActiveScene();
            if (scene.path != AuthoredScene)
                throw new InvalidOperationException("Keep the authored PortalShift.unity scene active. This repair does not load or replace scenes.");
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(Package + "/Shaders/2D/Sprite-Unlit-Default.shader");
            if (material == null || shader == null)
                throw new InvalidOperationException("The shared sprite material and imported URP 17.5 sprite shader must exist before repair.");
            var graphicsObject = SettingsObject(GraphicsPath);
            var qualityObject = SettingsObject(QualityPath);
            var quality = new SerializedObject(qualityObject);
            RequireQualityArray(quality);
            string guidBefore = AssetDatabase.AssetPathToGUID(MaterialPath);
            var previousPipeline = GraphicsSettings.defaultRenderPipeline;
            var previousGlobal = EditorGraphicsSettings.GetRenderPipelineGlobalSettingsAsset(typeof(UniversalRenderPipeline));
            string previousQuality = EditorJsonUtility.ToJson(qualityObject);
            bool dirtyBefore = scene.isDirty;
            var transforms = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true))
                .Select(t => new TransformState(t)).ToArray();
            var materialBefore = new Material(material) { name = material.name };
            var beforeStates = new Dictionary<Object, string>();
            foreach (var asset in new Object[] { graphicsObject, qualityObject, material,
                AssetDatabase.LoadAssetAtPath<Renderer2DData>(RendererPath),
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath), previousGlobal })
                if (asset != null && !beforeStates.ContainsKey(asset)) beforeStates.Add(asset, EditorJsonUtility.ToJson(asset));
            string backup = "Verification/RenderingBackups/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            Directory.CreateDirectory(backup);
            // URP's package import can dirty an old-schema packaged renderer during
            // its OnEnable/deserialization upgrade. Saving a scene copy can flush
            // such assets even though this tool never edits them. Immutable package
            // assets must retain their supplied disk bytes; keep their initialized
            // in-memory state but remove that incidental save flag first.
            ClearImmutablePackageDirtyFlags();
            // Save-as-copy preserves current in-memory edits and never saves over the authored scene.
            if (!EditorSceneManager.SaveScene(scene, backup + "/live-reference-before-repair.unity", true))
                throw new IOException("Could not snapshot the in-memory reference; rendering was not changed.");
            var backupPaths = new[] { MaterialPath, MaterialPath + ".meta", GraphicsPath, QualityPath, RendererPath, RendererPath + ".meta", PipelinePath, PipelinePath + ".meta", GlobalPath, GlobalPath + ".meta" }
                .Concat(beforeStates.Keys.Select(AssetDatabase.GetAssetPath).Where(p => !string.IsNullOrEmpty(p)).SelectMany(p => new[] { p, p + ".meta" })).Distinct();
            foreach (string path in backupPaths)
                if (File.Exists(path))
                {
                    string destination = backup + "/" + path;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(path, destination, false);
                }
            File.WriteAllText(backup + "/quality-in-memory.json", previousQuality);
            File.WriteAllText(backup + "/graphics-in-memory.json", EditorJsonUtility.ToJson(graphicsObject));
            var created = new List<string>();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Migrate Portal Shift sprite rendering to URP 2D");
            Undo.RegisterCompleteObjectUndo(new[] { graphicsObject, qualityObject, material }, "Migrate Portal Shift rendering");
            try
            {
                EnsureFolder(Folder);
                var data = AssetDatabase.LoadAssetAtPath<Renderer2DData>(RendererPath);
                if (data == null)
                {
                    RequireUnusedPath(RendererPath);
                    data = ScriptableObject.CreateInstance<Renderer2DData>();
                    AssetDatabase.CreateAsset(data, RendererPath);
                    created.Add(RendererPath);
                }
                else Undo.RegisterCompleteObjectUndo(data, "Configure Portal Shift 2D renderer");
                var dataSerialized = new SerializedObject(data);
                var postProcess = dataSerialized.FindProperty("m_PostProcessData");
                var defaultMaterial = dataSerialized.FindProperty("m_DefaultMaterialType");
                if (postProcess == null || defaultMaterial == null)
                    throw new InvalidOperationException("Renderer2DData fields differ from the inspected URP 17.5 API; no migration will be retained.");
                postProcess.objectReferenceValue = AssetDatabase.LoadAssetAtPath<PostProcessData>(Package + "/Runtime/Data/PostProcessData.asset");
                int unlit = Array.IndexOf(defaultMaterial.enumNames, "Unlit");
                if (postProcess.objectReferenceValue == null || unlit < 0)
                    throw new InvalidOperationException("URP's default post-process resources or Unlit renderer option are unavailable.");
                defaultMaterial.enumValueIndex = unlit;
                dataSerialized.ApplyModifiedProperties();
                // Same initialization sequence as bundled Editor/2D/Renderer2DMenus.cs.
                ResourceReloader.ReloadAllNullIn(data, Package);
                var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
                if (pipeline == null)
                {
                    RequireUnusedPath(PipelinePath);
                    pipeline = UniversalRenderPipelineAsset.Create(data);
                    AssetDatabase.CreateAsset(pipeline, PipelinePath);
                    created.Add(PipelinePath);
                }
                else
                {
                    Undo.RegisterCompleteObjectUndo(pipeline, "Assign Portal Shift 2D renderer");
                    var serializedPipeline = new SerializedObject(pipeline);
                    var renderers = serializedPipeline.FindProperty("m_RendererDataList");
                    var defaultIndex = serializedPipeline.FindProperty("m_DefaultRendererIndex");
                    if (renderers == null || defaultIndex == null) throw new InvalidOperationException("URP renderer configuration fields are unavailable.");
                    renderers.arraySize = 1;
                    renderers.GetArrayElementAtIndex(0).objectReferenceValue = data;
                    defaultIndex.intValue = 0;
                    serializedPipeline.ApplyModifiedProperties();
                }
                var global = previousGlobal;
                if (global == null)
                {
                    global = AssetDatabase.LoadAssetAtPath<RenderPipelineGlobalSettings>(GlobalPath);
                    if (global == null)
                    {
                        RequireUnusedPath(GlobalPath);
                        if (File.Exists("Assets/DefaultVolumeProfile.asset"))
                            throw new InvalidOperationException("An existing unregistered DefaultVolumeProfile.asset must be assigned manually before creating URP global settings; this repair will not overwrite it.");
                        // URP 17.5's concrete global-settings type is internal. The
                        // public factory accepts its Type, populates official graphics
                        // resources, and calls Initialize including its volume profile.
                        var globalType = typeof(UniversalRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineGlobalSettings", true);
                        global = RenderPipelineGlobalSettingsUtils.Create(globalType, GlobalPath);
                        if (global == null) throw new InvalidOperationException("The official URP global-settings factory returned no asset.");
                        created.Add(AssetDatabase.GetAssetPath(global));
                        if (File.Exists("Assets/DefaultVolumeProfile.asset")) created.Add("Assets/DefaultVolumeProfile.asset");
                    }
                    EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset(typeof(UniversalRenderPipeline), global);
                }
                material.shader = shader;
                // Changing the shader keeps the same material asset/GUID. Explicitly
                // preserve compatible sprite tint and texture properties as well.
                PreserveSpriteProperties(materialBefore, material);
                GraphicsSettings.defaultRenderPipeline = pipeline;
                quality.Update();
                var qualities = RequireQualityArray(quality);
                for (int i = 0; i < qualities.arraySize; i++)
                {
                    var renderPipeline = qualities.GetArrayElementAtIndex(i).FindPropertyRelative("customRenderPipeline");
                    if (renderPipeline == null) throw new InvalidOperationException("Quality pipeline override field is unavailable.");
                    renderPipeline.objectReferenceValue = null;
                }
                quality.ApplyModifiedProperties();
                if (guidBefore != AssetDatabase.AssetPathToGUID(MaterialPath)) throw new InvalidOperationException("The shared material GUID changed unexpectedly.");
                if (scene.isDirty != dirtyBefore || transforms.Any(t => !t.Unchanged()))
                    throw new InvalidOperationException("Scene state changed unexpectedly during rendering migration.");
                var report = Inspect();
                WriteReport(report);
                if (!report.passed) throw new InvalidOperationException("Rendering validation failed: " + string.Join("; ", report.errors));
                foreach (var asset in new Object[] { data, pipeline, global, material, graphicsObject, qualityObject })
                    if (asset != null) { EditorUtility.SetDirty(asset); AssetDatabase.SaveAssetIfDirty(asset); }
                Undo.CollapseUndoOperations(group);
                SceneView.RepaintAll();
                EditorApplication.QueuePlayerLoopUpdate();
                Debug.Log("PORTAL_RENDERING_REPAIR_APPLIED URP 2D/unlit. Material GUID preserved: " + guidBefore + ". Scene geometry and dirty state preserved; no authored-scene save. Private backup: " + backup);
            }
            catch (Exception migrationError)
            {
                var rollbackErrors = new List<string>();
                try { Undo.RevertAllDownToGroup(group); }
                catch (Exception error) { rollbackErrors.Add("Undo: " + error.Message); }
                // Restore every pre-existing object, including renderer/pipeline
                // assets that may already have been saved before a later save failed.
                foreach (var pair in beforeStates)
                    try { EditorJsonUtility.FromJsonOverwrite(pair.Value, pair.Key); }
                    catch (Exception error) { rollbackErrors.Add("Restore " + pair.Key.name + ": " + error.Message); }
                try { GraphicsSettings.defaultRenderPipeline = previousPipeline; }
                catch (Exception error) { rollbackErrors.Add("Graphics pipeline: " + error.Message); }
                try { EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset(typeof(UniversalRenderPipeline), previousGlobal); }
                catch (Exception error) { rollbackErrors.Add("Global registration: " + error.Message); }
                foreach (string path in created.AsEnumerable().Reverse())
                    try { AssetDatabase.DeleteAsset(path); }
                    catch (Exception error) { rollbackErrors.Add("Remove " + path + ": " + error.Message); }
                ClearImmutablePackageDirtyFlags();
                foreach (var asset in beforeStates.Keys)
                    try { EditorUtility.SetDirty(asset); AssetDatabase.SaveAssetIfDirty(asset); }
                    catch (Exception error)
                    {
                        string path = asset == graphicsObject ? GraphicsPath : asset == qualityObject ? QualityPath : AssetDatabase.GetAssetPath(asset);
                        string original = backup + "/" + path;
                        // A failing asset-save callback must not leave a partially
                        // migrated disk asset. The original bytes were backed up
                        // before writes; in-memory values have been restored above.
                        try
                        {
                            if (File.Exists(original)) File.Copy(original, path, true);
                            else rollbackErrors.Add("No disk backup for " + path);
                        }
                        catch (Exception copyError) { rollbackErrors.Add("Disk restore " + path + ": " + copyError.Message); }
                        rollbackErrors.Add("Save " + path + ": " + error.Message);
                    }
                SceneView.RepaintAll();
                Debug.LogError("Portal Shift rendering repair rolled back. Private snapshot and asset backups: " + backup);
                if (rollbackErrors.Count > 0)
                    Debug.LogError("Rendering rollback needs review: " + string.Join("; ", rollbackErrors) + ". Original migration error: " + migrationError.Message);
                throw;
            }
            finally { Object.DestroyImmediate(materialBefore); }
        }

        [MenuItem("Portal Shift/Repairs/Validate Sprite Rendering (Read Only)")]
        public static void ValidateRendering()
        {
            var report = Inspect();
            WriteReport(report);
            if (report.passed) Debug.Log("PORTAL_RENDERING_VALIDATION_PASSED sprites=" + report.spriteCount + "; " + report.shader + "; Verification/rendering-validation.json");
            else Debug.LogWarning("PORTAL_RENDERING_VALIDATION_FAILED " + string.Join("; ", report.errors) + "; Verification/rendering-validation.json");
        }

        [MenuItem("Portal Shift/Repairs/Clear Immutable Package Import Save Flags")]
        public static void ClearPackageImportSaveFlags()
        {
            int count = ClearImmutablePackageDirtyFlags();
            Debug.Log("PORTAL_IMMUTABLE_PACKAGE_SAVE_FLAGS_CLEARED " + count + ". No asset contents or disk files were changed; embedded/local package edits are preserved.");
        }

        static int ClearImmutablePackageDirtyFlags()
        {
            int count = 0;
            foreach (var asset in Resources.FindObjectsOfTypeAll<Object>())
            {
                if (asset == null || !EditorUtility.IsDirty(asset)) continue;
                string path = AssetDatabase.GetAssetPath(asset);
                if (!path.StartsWith("Packages/", StringComparison.Ordinal)) continue;
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
                if (package == null) continue;
                var source = package.source;
                if (source != UnityEditor.PackageManager.PackageSource.BuiltIn && source != UnityEditor.PackageManager.PackageSource.Registry &&
                    source != UnityEditor.PackageManager.PackageSource.Git) continue;
                EditorUtility.ClearDirty(asset);
                count++;
            }
            return count;
        }

        public static Report Inspect()
        {
            var errors = new List<string>();
            var scene = SceneManager.GetActiveScene();
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            var pipeline = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            var global = EditorGraphicsSettings.GetRenderPipelineGlobalSettingsAsset(typeof(UniversalRenderPipeline));
            var report = new Report
            {
                timestamp = DateTime.UtcNow.ToString("o"), scene = scene.path, unityVersion = Application.unityVersion,
                defaultPipeline = AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline),
                currentPipeline = AssetDatabase.GetAssetPath(GraphicsSettings.currentRenderPipeline),
                globalSettings = AssetDatabase.GetAssetPath(global), globalSettingsPresent = global != null,
                material = MaterialPath, materialGuid = AssetDatabase.AssetPathToGUID(MaterialPath),
                shader = material != null && material.shader != null ? material.shader.name : "",
                shaderSupported = material != null && material.shader != null && material.shader.isSupported,
                qualityOverrides = Enumerable.Range(0, QualitySettings.names.Length).Select(i => AssetDatabase.GetAssetPath(QualitySettings.GetRenderPipelineAssetAt(i))).ToArray()
            };
            report.qualityInheritsDefault = report.qualityOverrides.All(string.IsNullOrEmpty);
            if (pipeline == null) errors.Add("Graphics default pipeline is not a UniversalRenderPipelineAsset.");
            if (!report.globalSettingsPresent) errors.Add("URP global settings are not registered.");
            if (!report.qualityInheritsDefault) errors.Add("A Quality level overrides the Graphics pipeline.");
            if (report.shader != SpriteShaderName || !report.shaderSupported) errors.Add("Shared material does not have the supported URP 2D unlit sprite shader.");
            if (pipeline != null)
            {
                var serializedPipeline = new SerializedObject(pipeline);
                var renderers = serializedPipeline.FindProperty("m_RendererDataList");
                var index = serializedPipeline.FindProperty("m_DefaultRendererIndex");
                var data = renderers != null && index != null && index.intValue >= 0 && index.intValue < renderers.arraySize
                    ? renderers.GetArrayElementAtIndex(index.intValue).objectReferenceValue as Renderer2DData : null;
                report.renderer = AssetDatabase.GetAssetPath(data);
                if (data != null)
                {
                    var serialized = new SerializedObject(data);
                    var unlit = serialized.FindProperty("m_DefaultMaterialType");
                    var postProcess = serialized.FindProperty("m_PostProcessData");
                    report.rendererDefaultMaterial = unlit != null ? unlit.enumNames[unlit.enumValueIndex] : "";
                    report.postProcessData = postProcess != null ? AssetDatabase.GetAssetPath(postProcess.objectReferenceValue) : "";
                    report.rendererResourcesInitialized = !string.IsNullOrEmpty(report.postProcessData);
                }
                if (data == null || report.rendererDefaultMaterial != "Unlit" || !report.rendererResourcesInitialized)
                    errors.Add("Default renderer must be initialized Renderer2DData with Unlit default and post-process resources.");
            }
            var sprites = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<SpriteRenderer>(true)).ToArray();
            report.spriteCount = sprites.Length;
            report.unexpectedSpriteMaterials = sprites.Where(s => s.sharedMaterial != material).Select(s => ObjectPath(s.transform)).ToArray();
            report.allSpritesUseSharedMaterial = material != null && sprites.Length > 0 && report.unexpectedSpriteMaterials.Length == 0;
            if (!report.allSpritesUseSharedMaterial) errors.Add("Some scene sprites do not use the shared sprite material, or no scene sprites are available.");
            report.errors = errors.ToArray(); report.passed = errors.Count == 0;
            return report;
        }

        static Object SettingsObject(string path)
        {
            var asset = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault();
            if (asset == null) throw new InvalidOperationException("Cannot access serialized project settings at " + path + "; rendering was not changed.");
            return asset;
        }
        static SerializedProperty RequireQualityArray(SerializedObject quality)
        {
            var array = quality.FindProperty("m_QualitySettings");
            if (array == null || !array.isArray) throw new InvalidOperationException("Quality settings layout differs from Unity 6.5; rendering was not changed.");
            return array;
        }
        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/'); string parent = path.Substring(0, slash);
            EnsureFolder(parent); AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }
        static void RequireUnusedPath(string path)
        {
            if (AssetDatabase.LoadMainAssetAtPath(path) != null || File.Exists(path))
                throw new InvalidOperationException("An unexpected asset already occupies " + path + "; it will not be overwritten.");
        }
        static void PreserveSpriteProperties(Material source, Material target)
        {
            foreach (string color in new[] { "_Color", "_RendererColor" })
                if (source.HasProperty(color) && target.HasProperty(color)) target.SetColor(color, source.GetColor(color));
            foreach (string texture in new[] { "_MainTex", "_AlphaTex" })
                if (source.HasProperty(texture) && target.HasProperty(texture))
                {
                    target.SetTexture(texture, source.GetTexture(texture));
                    target.SetTextureScale(texture, source.GetTextureScale(texture));
                    target.SetTextureOffset(texture, source.GetTextureOffset(texture));
                }
            foreach (string value in new[] { "PixelSnap", "_EnableExternalAlpha" })
                if (source.HasProperty(value) && target.HasProperty(value)) target.SetFloat(value, source.GetFloat(value));
        }
        static void WriteReport(Report report)
        {
            Directory.CreateDirectory("Verification");
            File.WriteAllText("Verification/rendering-validation.json", JsonUtility.ToJson(report, true));
        }
        static string ObjectPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null) { transform = transform.parent; path = transform.name + "/" + path; }
            return path;
        }
        sealed class TransformState
        {
            readonly Transform transform;
            readonly Vector3 position, scale;
            readonly Quaternion rotation;
            public TransformState(Transform value) { transform = value; position = value.localPosition; scale = value.localScale; rotation = value.localRotation; }
            public bool Unchanged() => transform != null && transform.localPosition == position && transform.localScale == scale && transform.localRotation == rotation;
        }
    }
}
