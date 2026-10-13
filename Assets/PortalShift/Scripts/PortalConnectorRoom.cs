using UnityEngine;

namespace PortalShift
{
    /// <summary>
    /// Editable adjacency sockets for a connector template. They are authoring
    /// metadata, independent of ShiftPortal shapes, colors and teleport rules.
    /// A socket does not remove a wall or create a walking door.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PortalRoom))]
    public sealed class PortalConnectorRoom : MonoBehaviour
    {
        public ConnectorKind Kind = ConnectorKind.Corner;
        public ConnectorSockets Sockets = ConnectorSockets.North | ConnectorSockets.East;
        [Tooltip("Room-local socket markers. These do not cut holes in colliders.")]
        public Vector2 NorthSocket;
        public Vector2 EastSocket;
        public Vector2 SouthSocket;
        public Vector2 WestSocket;
        [Tooltip("Only supporting platform X positions were mirrored; gravity and portal identities are unchanged.")]
        public bool MirroredPlatforms;
        [Tooltip("Historical provenance of the template, not a runtime scene dependency.")]
        public string CapturedFrom;

        public Vector2 SocketPosition(ConnectorSockets socket)
        {
            switch (socket)
            {
                case ConnectorSockets.North: return NorthSocket;
                case ConnectorSockets.East: return EastSocket;
                case ConnectorSockets.South: return SouthSocket;
                case ConnectorSockets.West: return WestSocket;
                default: return Vector2.zero;
            }
        }

        void OnDrawGizmosSelected()
        {
            foreach (var socket in new[] { ConnectorSockets.North, ConnectorSockets.East, ConnectorSockets.South, ConnectorSockets.West })
            {
                bool connected = (Sockets & socket) != 0;
                Gizmos.color = connected ? new Color(.3f, .95f, .7f) : new Color(.5f, .5f, .5f, .4f);
                var center = transform.TransformPoint(SocketPosition(socket));
                Gizmos.DrawWireSphere(center, .22f);
#if UNITY_EDITOR
                if (connected) UnityEditor.Handles.Label(center + Vector3.up * .3f, socket + " socket (metadata)");
#endif
            }
        }
    }
}
