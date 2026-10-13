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
    /// <summary>Operates on the open authored scene, including unsaved edits. Never reloads or regenerates it.</summary>
    public static class PortalLiveLayoutTools
    {
        const string ScenePath = "Assets/PortalShift/Scenes/PortalShift.unity";
        [Serializable] public class Item
        {
            public string path; public Vector3 position, scale; public Vector2 colliderSize, colliderOffset;
            public bool trigger, effector; public string shape, color, mount; public float rotation, reach;
        }
        [Serializable] public class RoomData { public string name; public Vector2Int coordinate; public Item[] items; }
        [Serializable] public class Report
        {
            public string timestamp, scene, snapshot; public bool originalSceneDirty; public RoomData[] rooms;
            public float mapWidth, mapHeight; public Vector3 cameraCenter; public string[] changes;
        }
        static PortalStage RequireStage()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play before editing layout.");
            var scene=SceneManager.GetActiveScene();
            if (scene.path!=ScenePath) throw new InvalidOperationException("The open active scene must be the authored PortalShift.unity scene.");
            var stage=Resources.FindObjectsOfTypeAll<PortalStage>().Single(s=>s.gameObject.scene==scene);
            if(stage.Rooms.Length!=4)throw new InvalidOperationException("Expected the four registered rooms.");
            return stage;
        }
        [MenuItem("Portal Shift/Inspect Live Layout and Snapshot")]
        public static void InspectLive()
        {
            var stage=RequireStage();var scene=stage.gameObject.scene;
            var stamp=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string dir="Verification/LiveLayoutSnapshots/"+stamp;Directory.CreateDirectory(dir);
            string snapshot=dir+"/before-match.unity";bool dirty=scene.isDirty;
            if(!EditorSceneManager.SaveScene(scene,snapshot,true))throw new IOException("Could not save the live-scene snapshot.");
            var report=Capture(stage);report.snapshot=Path.GetFullPath(snapshot);report.originalSceneDirty=dirty;
            File.WriteAllText("Verification/live-layout-before.json",JsonUtility.ToJson(report,true));
            Debug.Log("PORTAL_LIVE_LAYOUT_SNAPSHOT "+report.snapshot);
        }
        static Report Capture(PortalStage stage)
        {
            return new Report {timestamp=DateTime.UtcNow.ToString("o"),scene=stage.gameObject.scene.path,
                mapWidth=stage.MapRoomWidth,mapHeight=stage.MapRoomHeight,
                rooms=stage.Rooms.Select(r=>new RoomData{name=r.name,coordinate=r.Coordinate,
                    items=r.GetComponentsInChildren<Transform>(true).Where(t=>t!=r.transform).Select(t=>
                    {
                        var b=t.GetComponent<BoxCollider2D>();var p=t.GetComponent<ShiftPortal>();
                        return new Item {path=RelativePath(r.transform,t),position=t.localPosition,scale=t.localScale,
                            colliderSize=b!=null?b.size:Vector2.zero,colliderOffset=b!=null?b.offset:Vector2.zero,
                            trigger=b!=null&&b.isTrigger,effector=b!=null&&b.usedByEffector,
                            shape=p!=null?p.Shape.ToString():null,color=p!=null?p.Color.ToString():null,
                            mount=p!=null?p.Mount.ToString():null,rotation=p!=null?p.VisualRotationDegrees:0,reach=p!=null?p.InteractionReach:0};
                    }).ToArray()}).ToArray()};
        }
        static string RelativePath(Transform root,Transform child)
        {
            string result=child.name;while(child.parent!=root){child=child.parent;result=child.name+"/"+result;}return result;
        }
        [MenuItem("Portal Shift/Match Rooms to Live Threshold Layout")]
        public static void MatchRooms()
        {
            var stage=RequireStage();
            // Snapshot the latest state again immediately before applying any edit.
            InspectLive();var source=stage.Rooms.Single(r=>r.Coordinate==Vector2Int.zero);
            var changes=new List<string>();Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Match rooms to user Threshold layout");
            try
            {
                AdaptBoundaries(source,stage,changes);
                foreach(var target in stage.Rooms)if(target!=source)CopyRoomLayout(source,target,changes);
                SetCameraBounds(stage,source,changes);
                Physics2D.SyncTransforms();
                EditorSceneManager.MarkSceneDirty(stage.gameObject.scene);
                var report=Capture(stage);report.changes=changes.ToArray();
                File.WriteAllText("Verification/live-layout-after.json",JsonUtility.ToJson(report,true));
                Undo.CollapseUndoOperations(group);
                if(!EditorSceneManager.SaveScene(stage.gameObject.scene))throw new IOException("Could not save matched authored scene.");
                FrameLayout(stage);
                Debug.Log("PORTAL_LIVE_LAYOUT_MATCHED "+string.Join("; ",changes));
            }
            catch {Undo.RevertAllDownToGroup(group);throw;}
        }
        [MenuItem("Portal Shift/Move Square Above Ceiling and Match Rooms")]
        public static void MoveSquareAboveCeiling()
        {
            var stage=RequireStage();InspectLive();
            var source=stage.Rooms.Single(r=>r.Coordinate==Vector2Int.zero);
            var square=source.GetComponentsInChildren<ShiftPortal>(true).Single(p=>p.Shape==PortalShape.Rectangle);
            var ceiling=source.transform.Find("Ceiling Wall").GetComponent<BoxCollider2D>();
            Physics2D.SyncTransforms();var changes=new List<string>();
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Move square above ceiling and match rooms");
            try
            {
                float rise=ceiling.bounds.max.y+.15f-square.ShapeRenderer.bounds.min.y;
                var p=square.transform.localPosition;p.y+=rise;
                SetTransform(square.transform,p,square.transform.localScale,source.name+"/"+square.name,changes);
                Undo.RecordObject(square,"Mount square above ceiling");square.Mount=PortalMount.Ceiling;
                var a=square.WallAttachment.transform;var ap=a.localPosition;ap.y=ceiling.bounds.max.y-square.transform.position.y;
                SetTransform(a,ap,a.localScale,source.name+"/"+square.name+"/Wall Attachment",changes);
                foreach(var target in stage.Rooms)if(target!=source)CopyRoomLayout(source,target,changes);
                RefreshEditorPortals(stage);
                SetCameraBounds(stage,source,changes,true);
                Physics2D.SyncTransforms();EditorSceneManager.MarkSceneDirty(stage.gameObject.scene);
                var report=Capture(stage);report.changes=changes.ToArray();
                File.WriteAllText("Verification/reference-layout-after.json",JsonUtility.ToJson(report,true));
                Undo.CollapseUndoOperations(group);
                if(!EditorSceneManager.SaveScene(stage.gameObject.scene))throw new IOException("Could not save the reference-matched scene.");
                FrameLayout(stage);
                Debug.Log("PORTAL_REFERENCE_LAYOUT_MATCHED "+string.Join("; ",changes)+". Rectangle is above the solid ceiling and cannot be reached from the interior with its current trigger.");
            }
            catch {Undo.RevertAllDownToGroup(group);throw;}
        }
        static void RefreshEditorPortals(PortalStage stage)
        {
            var rules=new PortalRuleState(stage.AttemptSeed);
            foreach(var room in stage.Rooms)foreach(var p in room.GetComponentsInChildren<ShiftPortal>(true))
            {
                foreach(var pip in p.DistancePips)if(pip!=null){Undo.RecordObject(pip,"Refresh portal distance cue");Undo.RecordObject(pip.transform,"Refresh portal distance cue");}
                if(p.ShapeRenderer!=null){Undo.RecordObject(p.ShapeRenderer,"Refresh portal artwork");Undo.RecordObject(p.ShapeRenderer.transform,"Refresh portal artwork");}
                if(p.WallAttachment!=null)Undo.RecordObject(p.WallAttachment,"Refresh portal attachment tint");
                p.Refresh(rules);
            }
        }
        static void SetTransform(Transform t,Vector3 position,Vector3 scale,string label,List<string> changes)
        {
            var before=t.localPosition;var oldScale=t.localScale;Undo.RecordObject(t,"Align "+label);
            t.localPosition=position;t.localScale=scale;
            if(before!=position||oldScale!=scale)changes.Add(label+": position "+before+" -> "+position+", scale "+oldScale+" -> "+scale);
        }
        static void AdaptBoundaries(PortalRoom room,PortalStage stage,List<string> changes)
        {
            var floor=room.transform.Find("Main Floor").GetComponent<BoxCollider2D>();
            var ceiling=room.transform.Find("Ceiling Wall").GetComponent<BoxCollider2D>();
            Physics2D.SyncTransforms();float bottom=floor.bounds.max.y-room.transform.position.y;
            float top=ceiling.bounds.min.y-room.transform.position.y;
            if(top-bottom<3f)throw new InvalidOperationException("Edited room height is too small for the player.");
            float center=(floor.transform.localPosition.y+ceiling.transform.localPosition.y)*.5f;
            float wallHeight=ceiling.transform.localPosition.y-floor.transform.localPosition.y;
            foreach(string name in new[]{"Left Wall","Right Wall"})
            {
                var t=room.transform.Find(name);var b=t.GetComponent<BoxCollider2D>();var pos=t.localPosition;pos.y=center;
                var scale=t.localScale;scale.y=wallHeight/b.size.y;SetTransform(t,pos,scale,room.name+"/"+name,changes);
            }
            var floorPortal=room.GetComponentsInChildren<ShiftPortal>(true).Single(p=>p.Mount==PortalMount.Floor);
            // Preserve the original glyph's room-side clearance when the floor has moved through it.
            var glyph=floorPortal.ShapeRenderer.bounds;
            if(glyph.min.y < floor.bounds.max.y-.03f)
            {
                var pos=floorPortal.transform.localPosition;pos.y+=floor.bounds.max.y-glyph.min.y+.025f;
                SetTransform(floorPortal.transform,pos,floorPortal.transform.localScale,room.name+"/"+floorPortal.name,changes);
            }
            foreach(var portal in room.GetComponentsInChildren<ShiftPortal>(true))
            {
                if(portal.WallAttachment==null)continue;
                var attachment=portal.WallAttachment.transform;var pos=attachment.localPosition;
                if(portal.Mount==PortalMount.Floor)pos.y=bottom-portal.transform.localPosition.y;
                if(portal.Mount==PortalMount.Ceiling)pos.y=top-portal.transform.localPosition.y;
                if(portal.Mount==PortalMount.Left)pos.x=room.transform.Find("Left Wall").GetComponent<BoxCollider2D>().bounds.max.x-portal.transform.position.x;
                if(portal.Mount==PortalMount.Right)pos.x=room.transform.Find("Right Wall").GetComponent<BoxCollider2D>().bounds.min.x-portal.transform.position.x;
                SetTransform(attachment,pos,attachment.localScale,room.name+"/"+portal.name+"/Wall Attachment",changes);
                if(portal.Mount==PortalMount.Ceiling)
                {
                    // Keep the user's removed top ledge removed. Extend only the interaction trigger down to the highest retained landing.
                    var ledges=room.GetComponentsInChildren<BoxCollider2D>().Where(b=>!b.isTrigger&&b.usedByEffector);
                    float highest=ledges.Max(b=>b.bounds.max.y);float standing=highest+stage.Player.GetComponent<CircleCollider2D>().radius*stage.Player.transform.lossyScale.y;
                    var trigger=portal.GetComponent<BoxCollider2D>();float needed=2f*Mathf.Max(0,portal.transform.position.y-(standing+portal.InteractionReach-.05f));
                    if(needed>trigger.size.y){var old=trigger.size;Undo.RecordObject(trigger,"Extend ceiling portal reach");trigger.size=new Vector2(trigger.size.x,needed);changes.Add(room.name+"/"+portal.name+" trigger size "+old+" -> "+trigger.size);}
                }
            }
        }
        static void CopyRoomLayout(PortalRoom source,PortalRoom target,List<string> changes)
        {
            var sourceOrdinary=source.transform.Cast<Transform>().Where(t=>t.GetComponent<ShiftPortal>()==null).ToDictionary(t=>t.name);
            foreach(var targetChild in target.transform.Cast<Transform>().ToArray())
                if(targetChild.GetComponent<ShiftPortal>()==null&&!sourceOrdinary.ContainsKey(targetChild.name))
                {changes.Add(target.name+"/"+targetChild.name+": removed to match exemplar");Undo.DestroyObjectImmediate(targetChild.gameObject);}
            foreach(var pair in sourceOrdinary)
            {
                var targetChild=target.transform.Find(pair.Key);
                if(targetChild==null)
                {
                    var clone=UnityEngine.Object.Instantiate(pair.Value.gameObject,target.transform);clone.name=pair.Key;
                    Undo.RegisterCreatedObjectUndo(clone,"Add exemplar child");targetChild=clone.transform;changes.Add(target.name+"/"+pair.Key+": added from exemplar");
                }
                CopyTree(pair.Value,targetChild,target.name+"/"+pair.Key,changes);
            }
            foreach(var from in source.GetComponentsInChildren<ShiftPortal>(true))
            {
                var to=target.GetComponentsInChildren<ShiftPortal>(true).Single(p=>p.Shape==from.Shape);
                CopyTree(from.transform,to.transform,target.name+"/"+to.name,changes);
                Undo.RecordObject(to,"Match portal physical settings");to.Mount=from.Mount;to.VisualRotationDegrees=from.VisualRotationDegrees;
                to.InteractionReach=from.InteractionReach;to.YellowTint=from.YellowTint;to.PurpleTint=from.PurpleTint;
                // Keep each room's shape/color identity and local renderer/pip references.
            }
            Undo.RecordObject(target,"Keep matched spawn and camera references");target.Spawn=target.transform.Find(source.Spawn.name);target.CameraAnchor=target.transform.Find(source.CameraAnchor.name);
        }
        static void CopyTree(Transform from,Transform to,string label,List<string> changes)
        {
            SetTransform(to,from.localPosition,from.localScale,label,changes);Undo.RecordObject(to,"Match artwork rotation");to.localRotation=from.localRotation;
            var fromBox=from.GetComponent<BoxCollider2D>();var toBox=to.GetComponent<BoxCollider2D>();
            if(fromBox!=null&&toBox!=null){Undo.RecordObject(toBox,"Match collider");EditorUtility.CopySerialized(fromBox,toBox);}
            var fromEffector=from.GetComponent<PlatformEffector2D>();var toEffector=to.GetComponent<PlatformEffector2D>();
            if(fromEffector!=null&&toEffector!=null){Undo.RecordObject(toEffector,"Match one-way settings");EditorUtility.CopySerialized(fromEffector,toEffector);}
            var fromSprite=from.GetComponent<SpriteRenderer>();var toSprite=to.GetComponent<SpriteRenderer>();
            // Portal colors/sprites refresh from each target's own rule identity; platform visuals may be copied.
            if(fromSprite!=null&&toSprite!=null&&from.GetComponentInParent<ShiftPortal>()==null){Undo.RecordObject(toSprite,"Match platform artwork");EditorUtility.CopySerialized(fromSprite,toSprite);}
            foreach(Transform child in from){var match=to.Find(child.name);if(match!=null)CopyTree(child,match,label+"/"+child.name,changes);}
        }
        static void SetCameraBounds(PortalStage stage,PortalRoom source,List<string> changes,bool includePortalArtwork=false)
        {
            Physics2D.SyncTransforms();var bounds=new Bounds();bool first=true;
            foreach(string n in new[]{"Main Floor","Left Wall","Right Wall","Ceiling Wall"})
            {var b=source.transform.Find(n).GetComponent<BoxCollider2D>().bounds;if(first){bounds=b;first=false;}else bounds.Encapsulate(b);}
            if(includePortalArtwork)foreach(var portal in source.GetComponentsInChildren<ShiftPortal>(true))if(portal.ShapeRenderer!=null)bounds.Encapsulate(portal.ShapeRenderer.bounds);
            Undo.RecordObject(stage,"Fit authored room dimensions");stage.MapRoomWidth=bounds.size.x;stage.MapRoomHeight=bounds.size.y;
            Vector3 center=bounds.center-source.transform.position;
            foreach(var room in stage.Rooms){var t=room.CameraAnchor;SetTransform(t,center,t.localScale,room.name+"/Camera Center",changes);}
            stage.RoomViewOrthographicSize=bounds.size.y*.5f/.94f;
            Undo.RecordObject(stage.RoomCamera.transform,"Fit saved whole-map camera");Undo.RecordObject(stage.RoomCamera,"Fit saved whole-map camera");
            var map=new Bounds(stage.Rooms[0].CameraPosition,new Vector3(stage.MapRoomWidth,stage.MapRoomHeight,0));
            foreach(var room in stage.Rooms)map.Encapsulate(new Bounds(room.CameraPosition,new Vector3(stage.MapRoomWidth,stage.MapRoomHeight,0)));
            float aspect=16f/9f;float margin=40f/720f;float size=Mathf.Max(map.size.y/(2f*(1f-margin-.06f)),map.size.x/(2f*aspect*.94f));
            stage.RoomCamera.orthographicSize=size;stage.RoomCamera.transform.position=new Vector3(map.center.x,map.center.y+margin*size,-10);
            changes.Add("Room camera bounds -> "+stage.MapRoomWidth+" x "+stage.MapRoomHeight+", local center "+center+", room zoom minimum "+stage.RoomViewOrthographicSize);
        }
        [MenuItem("Portal Shift/Frame Live Layout in 2D")]
        public static void FrameLive()=>FrameLayout(RequireStage());
        [MenuItem("Portal Shift/Capture Current Scene Camera Preview")]
        public static void CapturePreview()
        {
            var stage=RequireStage();var camera=stage.RoomCamera;var previous=camera.targetTexture;
            var active=RenderTexture.active;float aspect=camera.aspect;
            var target=new RenderTexture(1280,720,24);var pixels=new Texture2D(1280,720,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture=target;camera.aspect=16f/9f;camera.Render();RenderTexture.active=target;
                pixels.ReadPixels(new Rect(0,0,1280,720),0,0);pixels.Apply();
                File.WriteAllBytes("Verification/portal-shift-current-layout.png",pixels.EncodeToPNG());
                Debug.Log("PORTAL_CURRENT_SCENE_PREVIEW_CAPTURED (edit-mode camera render; not a runtime play check)");
            }
            finally
            {
                camera.targetTexture=previous;camera.aspect=aspect;RenderTexture.active=active;
                target.Release();UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(pixels);
            }
        }
        static void FrameLayout(PortalStage stage)
        {
            var view=SceneView.lastActiveSceneView;if(view==null)view=EditorWindow.GetWindow<SceneView>();
            view.in2DMode=true;var bounds=new Bounds(stage.Rooms[0].CameraPosition,new Vector3(stage.MapRoomWidth,stage.MapRoomHeight,0));
            foreach(var room in stage.Rooms)bounds.Encapsulate(new Bounds(room.CameraPosition,new Vector3(stage.MapRoomWidth,stage.MapRoomHeight,0)));
            view.Frame(bounds,true);view.Focus();
        }
    }
}
