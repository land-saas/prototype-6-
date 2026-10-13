using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace PortalShift
{
    public sealed class PortalStage : MonoBehaviour
    {
        [Header("Stage layout (expand to 4 x 4 by adding coordinates)")]
        [Min(1)] public int Width = 2;
        [Min(1)] public int Height = 2;
        public BoundaryPolicy Boundary = BoundaryPolicy.Reject;
        public Vector2Int StartCoordinate = Vector2Int.zero;
        public PortalRoom[] Rooms;
        public PortalPlayerController Player;
        public Camera RoomCamera;
        [Header("Playable camera view")]
        [Tooltip("Show the whole room matrix at the start of each attempt. Movement and portal interaction stay active in either view.")]
        public bool StartInWholeMap = true;
        [Tooltip("Minimum camera half-height for optional single-room zoom; independent of the saved whole-map camera frame.")]
        [Min(.5f)] public float RoomViewOrthographicSize = 5.625f;
        [Tooltip("World-space width of one room's visible screen; used to fit the map.")]
        [Min(1)] public float MapRoomWidth = 20f;
        [Min(1)] public float MapRoomHeight = 11.25f;
        /// <summary>True shows the whole matrix; false uses the current room's camera. Neither view pauses play.</summary>
        public bool MapOpen { get; private set; }
        float initialRoomCameraSize;
        [Header("Attempt rules")]
        [Tooltip("False produces a fresh shape permutation once per attempt; true reproduces AttemptSeed.")]
        public bool UseFixedSeed;
        public int AttemptSeed = 42606;
        [Min(0)] public float InteractionCooldown = .35f;
        [Header("Crew hooks")]
        public UnityEvent OnRoomChanged;
        public UnityEvent OnPhaseChanged;
        public UnityEvent OnAttemptReset;
        public PortalRuleState Rules { get; private set; }
        public PortalRoom CurrentRoom { get; private set; }
        public string Status { get; private set; } = "";
        public int TraversalCount { get; private set; }
        readonly Dictionary<Vector2Int, PortalRoom> roomMap = new Dictionary<Vector2Int, PortalRoom>();
        readonly HashSet<Vector2Int> visited = new HashSet<Vector2Int>();
        float nextInteraction;
        float statusUntil;
        public bool IsVisited(Vector2Int coordinate) => visited.Contains(coordinate);
        public ShiftPortal NearestPortal { get; private set; }

        void Awake()
        {
            initialRoomCameraSize = RoomViewOrthographicSize;
            foreach (var room in Rooms)
            {
                if (room == null) continue;
                if (roomMap.ContainsKey(room.Coordinate)) throw new InvalidOperationException("Duplicate room coordinate " + room.Coordinate);
                roomMap.Add(room.Coordinate, room);
            }
            ResetAttempt();
        }
        void Update()
        {
            if (!Player.UseExternalInput && Input.GetKeyDown(KeyCode.M)) ToggleMap();
            if (!Player.UseExternalInput && Input.GetKeyDown(KeyCode.R)) ResetAttempt();
            NearestPortal = null;
            if (CurrentRoom != null)
            {
                float nearest = float.MaxValue;
                foreach (var portal in CurrentRoom.GetComponentsInChildren<ShiftPortal>())
                {
                    float d = ((Vector2)portal.transform.position - (Vector2)Player.transform.position).sqrMagnitude;
                    if (portal.CanInteract(Player) && d < nearest) { nearest = d; NearestPortal = portal; }
                }
                if (Player.transform.position.y < CurrentRoom.transform.position.y - 7f) Respawn();
            }
            if (!Player.UseExternalInput)
            {
                if (Input.GetKeyDown(KeyCode.E))
                {
                    if (NearestPortal != null) TryUsePortal(NearestPortal);
                    else Say("Move near a portal to interact.");
                }

            }
            if (Time.unscaledTime > statusUntil) Status = "";
        }
        public void ResetAttempt()
        {
            MapOpen = StartInWholeMap;
            int seed = UseFixedSeed ? AttemptSeed : unchecked(Environment.TickCount ^ Guid.NewGuid().GetHashCode());
            AttemptSeed = seed;
            Rules = new PortalRuleState(seed) { BoundaryPolicy = Boundary };
            visited.Clear();
            TraversalCount = 0;
            nextInteraction = Time.unscaledTime + InteractionCooldown;
            if (!roomMap.TryGetValue(StartCoordinate, out var start)) throw new InvalidOperationException("Start coordinate has no room.");
            EnterRoom(start);
            RefreshPortals();
            Status = "";
            OnAttemptReset?.Invoke();
        }
        public bool TryUsePortal(ShiftPortal portal)
        {
            if (Time.unscaledTime < nextInteraction) return false;
            if (portal == null || portal.Room != CurrentRoom || !portal.CanInteract(Player)) return false;
            nextInteraction = Time.unscaledTime + InteractionCooldown;
            Rules.BoundaryPolicy = Boundary;
            int oldPhase = Rules.Phase;
            if (!Rules.TryTraverse(CurrentRoom.Coordinate, portal.Shape, portal.Color, Width, Height, roomMap.Keys, out var destination, out var reason))
            {
                Say("No chamber at that destination.");
                return false;
            }
            EnterRoom(roomMap[destination]);
            TraversalCount++;
            RefreshPortals();
            if (oldPhase != Rules.Phase) { Say("Phase shifted."); OnPhaseChanged?.Invoke(); }
            else Say("Chamber entered.");
            return true;
        }
        void EnterRoom(PortalRoom target)
        {
            CurrentRoom = target;
            ApplyRoomVisibility();
            visited.Add(target.Coordinate);
            Player.Teleport(target.SpawnPosition);
            FitCamera();
            OnRoomChanged?.Invoke();
        }
        public void ToggleMap() => SetMapOpen(!MapOpen);
        public void SetMapOpen(bool open)
        {
            if (MapOpen == open || CurrentRoom == null) return;
            MapOpen = open;
            ApplyRoomVisibility();
            FitCamera();
            Physics2D.SyncTransforms();
        }
        void ApplyRoomVisibility()
        {
            // All rooms are active in the whole-map view. Room-specific crew
            // behaviour should compare against CurrentRoom rather than rely on OnEnable.
            foreach (var room in Rooms)
                if (room != null) room.gameObject.SetActive(MapOpen || room == CurrentRoom);
        }
        void LateUpdate() => FitCamera();
        void FitCamera()
        {
            if (MapOpen) { FitMapCamera(); return; }
            if (CurrentRoom == null) return;
            RoomCamera.orthographicSize = Mathf.Max(initialRoomCameraSize, MapRoomWidth / (2f * Mathf.Max(.01f, RoomCamera.aspect)));
            var center = CurrentRoom.CameraPosition;
            RoomCamera.transform.position = new Vector3(center.x, center.y, -10f);
        }
        public void FitMapCamera()
        {
            if (!MapOpen) return;
            // Derive the frame from every authored anchor, so traversing a
            // portal cannot recenter or resize the whole-map camera.
            var bounds = new Bounds();
            bool hasBounds = false;
            foreach (var room in Rooms)
            {
                if (room == null) continue;
                var roomBounds = new Bounds(room.CameraPosition, new Vector3(MapRoomWidth, MapRoomHeight, 0));
                if (hasBounds) bounds.Encapsulate(roomBounds);
                else { bounds = roomBounds; hasBounds = true; }
            }
            if (!hasBounds) return;
            float top = 40f / Mathf.Max(1, Screen.height);
            float safeHeight = Mathf.Max(.5f, 1f - top - .06f);
            float size = Mathf.Max(bounds.size.y / (2f * safeHeight), bounds.size.x / (2f * Mathf.Max(.01f, RoomCamera.aspect) * .94f));
            RoomCamera.orthographicSize = size;
            RoomCamera.transform.position = new Vector3(bounds.center.x, bounds.center.y + top * size, -10f);
        }
        public void Respawn()
        {
            Player.Teleport(CurrentRoom.SpawnPosition);
            Say("Back on solid ground.");
        }
        void RefreshPortals()
        {
            foreach (var room in Rooms)
                if (room != null)
                    foreach (var portal in room.GetComponentsInChildren<ShiftPortal>(true)) portal.Refresh(Rules);
        }
        void Say(string text) { Status = text; statusUntil = Time.unscaledTime + 2.5f; }
    }
}
