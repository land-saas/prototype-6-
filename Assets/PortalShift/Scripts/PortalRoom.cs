using UnityEngine;

namespace PortalShift
{
    /// <summary>One editable screen of world content. Coordinates belong to the stage, not world positions.</summary>
    public sealed class PortalRoom : MonoBehaviour
    {
        public Vector2Int Coordinate;
        public string DisplayName = "Chamber";
        public Transform Spawn;
        public Transform CameraAnchor;
        public Vector2 SpawnPosition => Spawn != null ? (Vector2)Spawn.position : (Vector2)transform.position;
        public Vector3 CameraPosition => CameraAnchor != null ? CameraAnchor.position : transform.position;
    }
}
