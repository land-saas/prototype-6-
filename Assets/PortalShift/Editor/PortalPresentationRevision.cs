using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PortalShift.Editor
{
    public static class PortalPresentationRevision
    {
        const string Root="Assets/PortalShift/";
        public static void ApplyAndBuildMac()
        {
            if(File.Exists(".playable-map-revision-applied"))throw new InvalidOperationException("Playable map revision already applied; build the saved scene.");
            var scene=EditorSceneManager.OpenScene(Root+"Scenes/PortalShift.unity");
            var stage=UnityEngine.Object.FindAnyObjectByType<PortalStage>();
            if(stage==null||stage.Rooms.Length!=4)throw new InvalidOperationException("Expected the authored four-room foundation.");
            stage.StartInWholeMap=true;
            stage.RoomCamera.backgroundColor=new Color(.060f,.079f,.084f);
            foreach(var room in stage.Rooms){StyleRoom(room);room.gameObject.SetActive(true);}
            // Save the editor scene already showing the complete playable matrix.
            float aspect=16f/9f;
            var first=stage.Rooms[0].CameraPosition;
            var bounds=new Bounds(first,new Vector3(stage.MapRoomWidth,stage.MapRoomHeight,0));
            foreach(var room in stage.Rooms)bounds.Encapsulate(new Bounds(room.CameraPosition,new Vector3(stage.MapRoomWidth,stage.MapRoomHeight,0)));
            float size=Mathf.Max(bounds.size.y/(2f*(1f-40f/720f-.06f)),bounds.size.x/(2f*aspect*.94f));
            stage.RoomCamera.orthographicSize=size;
            stage.RoomCamera.transform.position=new Vector3(bounds.center.x,bounds.center.y+40f/720f*size,-10);
            EditorSceneManager.SaveScene(scene);
            var root=PrefabUtility.LoadPrefabContents(Root+"Prefabs/RoomTemplate.prefab");
            StyleRoom(root.GetComponent<PortalRoom>());PrefabUtility.SaveAsPrefabAsset(root,Root+"Prefabs/RoomTemplate.prefab");PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            File.WriteAllText("Verification/presentation.json","{\"defaultWholeMap\":true,\"wholeMapPlayable\":true,\"mirroredOffDiagonalLedges\":true,\"chargeJumpAdded\":false,\"reference\":\"Nexile official Steam gameplay screenshot: clear landing edges, varied ledge widths and open central space. No reference art copied.\"}");
            File.WriteAllText(".playable-map-revision-applied","Playable whole-map presentation applied; edit scene/prefabs normally and build without regeneration.\n");
            PortalProjectBuilder.ValidateAndBuildMac();
        }
        static void StyleRoom(PortalRoom room)
        {
            foreach(var sprite in room.GetComponentsInChildren<SpriteRenderer>(true))
                if(sprite.gameObject.name=="Wall Seam")UnityEngine.Object.DestroyImmediate(sprite.gameObject);
            var redundant=room.transform.Find("Step Two");if(redundant!=null)UnityEngine.Object.DestroyImmediate(redundant.gameObject);
            bool mirrored=room.Coordinate.x!=room.Coordinate.y;
            float sign=mirrored?-1f:1f;
            SetLedge(room,"Step One",sign*3.7f,room.Coordinate.y==0?1.8f:2f);
            SetLedge(room,"Upper Shelf",sign*6.4f,mirrored?3.8f:4.2f);
            SetLedge(room,"Ceiling Step 1",sign*5f,mirrored?3.1f:3f);
            SetLedge(room,"Ceiling Step 2",sign*2.8f,room.Coordinate.x==0?2.8f:3f);
            SetLedge(room,"Ceiling Step 3",0,mirrored?3.4f:3.8f);
            foreach(var box in room.GetComponentsInChildren<BoxCollider2D>(true))
            {
                if(box.isTrigger)continue;
                var sprite=box.GetComponent<SpriteRenderer>();if(sprite==null)continue;
                bool wall=box.name=="Left Wall"||box.name=="Right Wall"||box.name=="Ceiling Wall";
                sprite.color=wall?new Color(.13f,.17f,.18f):new Color(.24f,.30f,.30f);
                if(wall)continue;
                AddLandingEdge(sprite);
            }
        }
        static void SetLedge(PortalRoom room,string name,float x,float width)
        {
            var t=room.transform.Find(name);if(t==null)throw new InvalidOperationException("Missing authored ledge "+name);
            var p=t.localPosition;p.x=x;t.localPosition=p;
            var s=t.localScale;s.x=width;t.localScale=s;
        }
        static void AddLandingEdge(SpriteRenderer platform)
        {
            var go=new GameObject("Landing Edge");go.transform.SetParent(platform.transform,false);
            float height=.05f/Mathf.Abs(platform.transform.localScale.y);
            go.transform.localPosition=new Vector3(0,.5f-height*.5f,-.02f);go.transform.localScale=new Vector3(1,height,1);
            var sprite=go.AddComponent<SpriteRenderer>();sprite.sprite=platform.sprite;sprite.sharedMaterial=platform.sharedMaterial;
            sprite.color=new Color(.43f,.51f,.48f);sprite.sortingOrder=platform.sortingOrder+1;
        }
    }
}
