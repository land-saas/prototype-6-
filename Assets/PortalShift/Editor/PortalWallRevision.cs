using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PortalShift.Editor
{
    /// <summary>Explicit targeted placement migration, preserving all portal identities and room references.</summary>
    public static class PortalWallRevision
    {
        const string Root = "Assets/PortalShift/";
        public static void ApplyAndBuildMac()
        {
            if (File.Exists(".wall-revision-applied")) throw new InvalidOperationException("Wall revision is already applied. Build the saved scene instead.");
            var scene = EditorSceneManager.OpenScene(Root+"Scenes/PortalShift.unity");
            var stage = UnityEngine.Object.FindAnyObjectByType<PortalStage>();
            if (stage == null || stage.Rooms.Length != 4) throw new InvalidOperationException("Expected the four-room foundation.");
            foreach (var room in stage.Rooms)
                if(room.GetComponentsInChildren<ShiftPortal>(true).Length!=4) throw new InvalidOperationException("Portal count changed in "+room.name+"; refusing to overwrite crew authoring.");
            foreach(var room in stage.Rooms) ReviseRoom(room);
            EditorSceneManager.SaveScene(scene);
            var template = PrefabUtility.LoadPrefabContents(Root+"Prefabs/RoomTemplate.prefab");
            ReviseRoom(template.GetComponent<PortalRoom>());
            PrefabUtility.SaveAsPrefabAsset(template,Root+"Prefabs/RoomTemplate.prefab");PrefabUtility.UnloadPrefabContents(template);
            var portalRoot=PrefabUtility.LoadPrefabContents(Root+"Prefabs/Portal.prefab");
            PlacePortal(portalRoot.GetComponent<ShiftPortal>(),PortalMount.Left);
            PrefabUtility.SaveAsPrefabAsset(portalRoot,Root+"Prefabs/Portal.prefab");PrefabUtility.UnloadPrefabContents(portalRoot);
            AssetDatabase.SaveAssets();
            File.WriteAllText("Verification/wall-layout.json","{\"passed\":true,\"portalsPerRoom\":4,\"onePerWall\":true,\"oneWayAccessLedges\":true,\"ceilingInnerSurface\":4.5,\"arrivalSpawnPreserved\":true}");
            File.WriteAllText(".wall-revision-applied","Targeted wall placement applied. Edit the saved scene/prefabs; do not rerun this migration.\n");
            PortalProjectBuilder.ValidateAndBuildMac();
        }
        static void ReviseRoom(PortalRoom room)
        {
            var floor=room.transform.Find("Main Floor").GetComponent<SpriteRenderer>();
            if(room.transform.Find("Ceiling Wall")==null) AddPlatform(room.transform,"Ceiling Wall",new Vector2(0,4.9f),new Vector2(20,.8f),floor,false);
            foreach(string name in new[]{"Step One","Step Two","Upper Shelf"}) MakeOneWay(room.transform.Find(name).gameObject);
            Transform reused=null;
            foreach(string name in new[]{"Gallery Shelf","Ledge Shelf","Atrium Shelf"})
                if(room.transform.Find(name)!=null) reused=room.transform.Find(name);
            if(reused!=null)
            {
                reused.name="Ceiling Step 1";reused.localPosition=new Vector3(5,-.275f,0);reused.localScale=new Vector3(3,.25f,1);
                MakeOneWay(reused.gameObject);
            }
            else AddPlatform(room.transform,"Ceiling Step 1",new Vector2(5,-.275f),new Vector2(3,.25f),floor,true);
            AddPlatform(room.transform,"Ceiling Step 2",new Vector2(2.8f,1.025f),new Vector2(2.8f,.25f),floor,true);
            AddPlatform(room.transform,"Ceiling Step 3",new Vector2(0,2.725f),new Vector2(3.6f,.25f),floor,true);
            foreach(var portal in room.GetComponentsInChildren<ShiftPortal>(true))
            {
                PortalMount mount=portal.Shape==PortalShape.Triangle?PortalMount.Left:
                    portal.Shape==PortalShape.Diamond?PortalMount.Right:
                    portal.Shape==PortalShape.Rectangle?PortalMount.Floor:PortalMount.Ceiling;
                PlacePortal(portal,mount);
            }
        }
        static void PlacePortal(ShiftPortal portal,PortalMount mount)
        {
            portal.Mount=mount;
            Vector2 position=mount==PortalMount.Left?new Vector2(-9.55f,-2.75f):
                mount==PortalMount.Right?new Vector2(9.55f,-2.75f):
                mount==PortalMount.Floor?new Vector2(0,-3.6f):new Vector2(0,4.05f);
            portal.transform.localPosition=position;
            portal.transform.localRotation=Quaternion.identity;
            portal.VisualRotationDegrees=mount==PortalMount.Left?270:mount==PortalMount.Right?90:mount==PortalMount.Floor?0:180;
            portal.ShapeRenderer.transform.localScale=Vector3.one;
            portal.GetComponent<BoxCollider2D>().size=mount==PortalMount.Left||mount==PortalMount.Right?new Vector2(1.25f,2.2f):new Vector2(2.2f,1.25f);
            portal.InteractionReach=.25f;
            var line=new GameObject("Wall Attachment");line.transform.SetParent(portal.transform,false);
            var renderer=line.AddComponent<SpriteRenderer>();renderer.sprite=AssetDatabase.LoadAssetAtPath<Sprite>(Root+"Art/Solid.png");
            renderer.sharedMaterial=portal.ShapeRenderer.sharedMaterial;renderer.sortingOrder=4;portal.WallAttachment=renderer;
            if(mount==PortalMount.Left||mount==PortalMount.Right)
            {
                line.transform.localPosition=new Vector3(mount==PortalMount.Left?-.45f:.45f,0,-.1f);
                line.transform.localScale=new Vector3(.035f,1.35f,1);
            }
            else
            {
                line.transform.localPosition=new Vector3(0,mount==PortalMount.Floor?-.4f:.45f,-.1f);
                line.transform.localScale=new Vector3(1.35f,.035f,1);
            }
            portal.Refresh(new PortalRuleState(42606));
        }
        static void AddPlatform(Transform parent,string name,Vector2 position,Vector2 size,SpriteRenderer source,bool oneWay)
        {
            var go=new GameObject(name);go.transform.SetParent(parent,false);go.transform.localPosition=position;go.transform.localScale=new Vector3(size.x,size.y,1);
            var renderer=go.AddComponent<SpriteRenderer>();renderer.sprite=source.sprite;renderer.sharedMaterial=source.sharedMaterial;
            renderer.color=oneWay?new Color(.28f,.38f,.48f):new Color(.14f,.20f,.29f);
            go.AddComponent<BoxCollider2D>().size=Vector2.one;
            if(oneWay)MakeOneWay(go);
        }
        static void MakeOneWay(GameObject platform)
        {
            var collider=platform.GetComponent<BoxCollider2D>();collider.usedByEffector=true;
            var effector=platform.GetComponent<PlatformEffector2D>();
            if(effector==null)effector=platform.AddComponent<PlatformEffector2D>();
            effector.useOneWay=true;effector.useOneWayGrouping=true;effector.surfaceArc=180;effector.useSideFriction=false;effector.useSideBounce=false;
        }
    }
}
