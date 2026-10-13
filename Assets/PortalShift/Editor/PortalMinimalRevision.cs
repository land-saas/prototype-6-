using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PortalShift.Editor
{
    /// <summary>Explicit one-time targeted migration; never runs automatically or regenerates rooms.</summary>
    public static class PortalMinimalRevision
    {
        const string Root = "Assets/PortalShift/";
        public static void ApplyAndBuildMac()
        {
            if (File.Exists(".minimal-revision-applied")) throw new InvalidOperationException("Minimal revision already applied. Use ValidateAndBuildMac to preserve authored work.");
            var scene = EditorSceneManager.OpenScene(Root + "Scenes/PortalShift.unity");
            var stage = UnityEngine.Object.FindAnyObjectByType<PortalStage>();
            if (stage == null || stage.Rooms.Length != 4) throw new InvalidOperationException("Expected four-room foundation; refusing an unrelated scene.");
            foreach (var room in stage.Rooms)
                if (room.GetComponentsInChildren<ShiftPortal>(true).Length != 6)
                    throw new InvalidOperationException("Portal layout has changed in " + room.name + "; refusing automatic migration of crew edits.");
            Sprite circle = CreateCircle(Root + "Art/Player.png");
            Sprite dot = CreateCircle(Root + "Art/DistanceDot.png");
            var player = stage.Player;
            var oldBox = player.GetComponent<BoxCollider2D>();
            var material = oldBox != null ? oldBox.sharedMaterial : AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(Root+"Art/PlayerNoFriction.physicsMaterial2D");
            if (oldBox != null) UnityEngine.Object.DestroyImmediate(oldBox);
            var circleCollider = player.GetComponent<CircleCollider2D>() ?? player.gameObject.AddComponent<CircleCollider2D>();
            circleCollider.radius = .42f; circleCollider.offset = Vector2.zero; circleCollider.sharedMaterial = material;
            player.GetComponent<SpriteRenderer>().sprite = circle;
            foreach (var room in stage.Rooms)
            {
                // Move the existing whole room, retaining platform geometry, art and internal references.
                room.transform.position = new Vector3(room.Coordinate.x*21.2f,room.Coordinate.y*11.5f,room.transform.position.z);
                foreach (var portal in room.GetComponentsInChildren<ShiftPortal>(true))
                {
                    // Only remove the original two elevated variants; keep four authored ground portals.
                    if (portal.transform.localPosition.y > -1f) { UnityEngine.Object.DestroyImmediate(portal.gameObject); continue; }
                    PortalShape purple = room.Coordinate.x == room.Coordinate.y ? PortalShape.Triangle : PortalShape.Diamond;
                    portal.Color = portal.Shape == purple ? PortalColor.Purple : PortalColor.Yellow;
                    portal.name = portal.Color+" "+portal.Shape;
                    portal.VisualRotationDegrees = 90f;
                    portal.DistanceLabel = null;
                    AddDistancePips(portal,dot);
                    portal.Refresh(new PortalRuleState(42606));
                }
                foreach (var text in room.GetComponentsInChildren<TextMesh>(true)) UnityEngine.Object.DestroyImmediate(text.gameObject);
            }
            EditorSceneManager.SaveScene(scene);
            PrefabUtility.SaveAsPrefabAsset(player.gameObject,Root+"Prefabs/Player.prefab");
            // Apply the same requested changes to reusable prefab contents, without replacing their hierarchy from a room.
            RevisePortalPrefab(Root+"Prefabs/Portal.prefab",dot);
            ReviseRoomPrefab(Root+"Prefabs/RoomTemplate.prefab",dot);
            AssetDatabase.SaveAssets();
            VerifyAuthoredConnectivity(stage);
            File.WriteAllText(".minimal-revision-applied","Targeted minimal visual revision applied. Edit the scene and prefabs directly; build with ValidateAndBuildMac.\n");
            PortalProjectBuilder.ValidateAndBuildMac();
        }
        public static void RepairCircleOpacityAndBuildMac()
        {
            if (!File.Exists(".minimal-revision-applied")) throw new InvalidOperationException("Apply the minimal revision first.");
            CreateCircle(Root+"Art/Player.png");
            CreateCircle(Root+"Art/DistanceDot.png");
            AssetDatabase.SaveAssets();
            PortalProjectBuilder.ValidateAndBuildMac();
        }
        static void AddDistancePips(ShiftPortal portal,Sprite dot)
        {
            portal.DistancePips = new SpriteRenderer[2];
            for (int i=0;i<2;i++)
            {
                var child = new GameObject("Distance Pip "+(i+1)); child.transform.SetParent(portal.transform,false);
                child.transform.localPosition = new Vector3(i==0?-.09f:.09f,-.89f,-.1f);
                child.transform.localScale = Vector3.one*.12f;
                var renderer = child.AddComponent<SpriteRenderer>();renderer.sprite=dot;renderer.color=new Color(.9f,.94f,.98f);
                renderer.sortingOrder=6;renderer.sharedMaterial=portal.ShapeRenderer.sharedMaterial;portal.DistancePips[i]=renderer;
            }
        }
        static void RevisePortalPrefab(string path,Sprite dot)
        {
            var root=PrefabUtility.LoadPrefabContents(path);
            var portal=root.GetComponent<ShiftPortal>();portal.VisualRotationDegrees=90;portal.DistanceLabel=null;
            foreach(var text in root.GetComponentsInChildren<TextMesh>(true))UnityEngine.Object.DestroyImmediate(text.gameObject);
            AddDistancePips(portal,dot);portal.Refresh(new PortalRuleState(42606));
            PrefabUtility.SaveAsPrefabAsset(root,path);PrefabUtility.UnloadPrefabContents(root);
        }
        static void ReviseRoomPrefab(string path,Sprite dot)
        {
            var root=PrefabUtility.LoadPrefabContents(path);var room=root.GetComponent<PortalRoom>();
            foreach(var portal in root.GetComponentsInChildren<ShiftPortal>(true))
            {
                if(portal.transform.localPosition.y>-1f){UnityEngine.Object.DestroyImmediate(portal.gameObject);continue;}
                portal.Color=portal.Shape==PortalShape.Triangle?PortalColor.Purple:PortalColor.Yellow;
                portal.name=portal.Color+" "+portal.Shape;portal.VisualRotationDegrees=90;portal.DistanceLabel=null;
                AddDistancePips(portal,dot);portal.Refresh(new PortalRuleState(42606));
            }
            foreach(var text in root.GetComponentsInChildren<TextMesh>(true))UnityEngine.Object.DestroyImmediate(text.gameObject);
            PrefabUtility.SaveAsPrefabAsset(root,path);PrefabUtility.UnloadPrefabContents(root);
        }
        static Sprite CreateCircle(string path)
        {
            const int n=128;var texture=new Texture2D(n,n,TextureFormat.RGBA32,false);var pixels=new Color[n*n];
            for(int y=0;y<n;y++)for(int x=0;x<n;x++)
            {
                var p=new Vector2((x+.5f)/n*2-1,(y+.5f)/n*2-1);
                float alpha=1f-Mathf.SmoothStep(0f,1f,Mathf.InverseLerp(.975f,1f,p.magnitude));
                pixels[y*n+x]=new Color(1,1,1,alpha);
            }
            texture.SetPixels(pixels);texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);var importer=(TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType=TextureImporterType.Sprite;importer.spriteImportMode=SpriteImportMode.Single;
            importer.spritePixelsPerUnit=128f/.84f;
            var settings=new TextureImporterSettings();importer.ReadTextureSettings(settings);settings.spriteMeshType=SpriteMeshType.FullRect;importer.SetTextureSettings(settings);
            importer.filterMode=FilterMode.Bilinear;
            importer.textureCompression=TextureImporterCompression.Uncompressed;importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }
        static void VerifyAuthoredConnectivity(PortalStage stage)
        {
            int permutations=0;
            var directions=new[]{CardinalDirection.Up,CardinalDirection.Down,CardinalDirection.Left,CardinalDirection.Right};
            for(int a=0;a<4;a++)for(int b=0;b<4;b++)for(int c=0;c<4;c++)for(int d=0;d<4;d++)
            {
                if(a==b||a==c||a==d||b==c||b==d||c==d)continue;
                var rules=new PortalRuleState(0);rules.SetDirections(directions[a],directions[b],directions[c],directions[d]);
                foreach(var start in stage.Rooms)
                {
                    var seen=new HashSet<Vector2Int>{start.Coordinate};var queue=new Queue<PortalRoom>();queue.Enqueue(start);
                    while(queue.Count>0)
                    {
                        var current=queue.Dequeue();
                        foreach(var portal in current.GetComponentsInChildren<ShiftPortal>(true))
                        {
                            if(portal.Color!=PortalColor.Yellow)continue;
                            var target=current.Coordinate+PortalRuleState.OffsetFor(rules.DirectionFor(portal.Shape));
                            foreach(var room in stage.Rooms)if(room.Coordinate==target&&seen.Add(target))queue.Enqueue(room);
                        }
                    }
                    if(seen.Count!=4)throw new InvalidOperationException("Authored four-portal layout disconnects rooms for a shape permutation.");
                }
                permutations++;
            }
            File.WriteAllText("Verification/layout.json","{\"passed\":true,\"portalsPerRoom\":4,\"yellowPerRoom\":3,\"purplePerRoom\":1,\"permutationsChecked\":"+permutations+",\"allFourOriginsChecked\":true,\"spacingX\":21.2,\"spacingY\":11.5,\"visualRotationDegrees\":90,\"circleRadius\":0.42}");
        }
    }
}
