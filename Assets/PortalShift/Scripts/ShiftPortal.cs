using UnityEngine;

namespace PortalShift
{
    public enum PortalMount
    {
        Freestanding,
        Left,
        Right,
        Floor,
        Ceiling
    }

    [RequireComponent(typeof(BoxCollider2D))]
    public sealed class ShiftPortal : MonoBehaviour
    {
        [Header("Portal rules")]
        public CardinalDirection Direction = CardinalDirection.Left;
        public PortalColor Color;

        [Header("Visual appearance")]
        public PortalShape Shape;
        public SpriteRenderer ShapeRenderer;

        [Tooltip("Physical mounting surface; independent of travel direction.")]
        public PortalMount Mount = PortalMount.Freestanding;

        public SpriteRenderer WallAttachment;

        [HideInInspector] public TextMesh DistanceLabel;

        [Tooltip("Rotation of the portal artwork only.")]
        public float VisualRotationDegrees = 90f;

        public SpriteRenderer[] DistancePips;

        [Tooltip("Order: Triangle, Diamond, Rectangle, Hexagon.")]
        public Sprite[] ShapeSprites;

        public Color YellowTint = new Color(1f, .77f, .25f);
        public Color PurpleTint = new Color(.74f, .49f, 1f);

        public float InteractionReach = 0.25f;

        BoxCollider2D trigger;

        public PortalRoom Room => GetComponentInParent<PortalRoom>();

        void Awake()
        {
            AlignInteractionToArtwork();
        }

        public void AlignInteractionToArtwork()
        {
            if (trigger == null)
                trigger = GetComponent<BoxCollider2D>();

            if (trigger == null || ShapeRenderer == null)
                return;

            Vector3 center = transform.InverseTransformPoint(
                ShapeRenderer.transform.position);

            Vector2 offset = new Vector2(center.x, center.y);

            if ((trigger.offset - offset).sqrMagnitude > .00000001f)
                trigger.offset = offset;
        }

        public bool CanInteract(PortalPlayerController player)
        {
            if (!isActiveAndEnabled || player == null)
                return false;

            AlignInteractionToArtwork();

            if (trigger == null)
                return false;

            return trigger.bounds.SqrDistance(
                player.transform.position)
                <= InteractionReach * InteractionReach;
        }

        public void Refresh(PortalRuleState rules)
        {
            if (rules == null)
                return;

            RefreshAppearance();
            int distance = rules.DistanceFor(Color);

            if (DistanceLabel != null)
                DistanceLabel.text = distance.ToString();

            UpdatePips(distance);
        }

        public void RefreshAppearance()
        {
            if (ShapeRenderer != null)
            {
                if (ShapeSprites != null &&
                    ShapeSprites.Length == 4)
                {
                    ShapeRenderer.sprite =
                        ShapeSprites[(int)Shape];
                }

                ShapeRenderer.color =
                    Color == PortalColor.Yellow
                    ? YellowTint
                    : PurpleTint;

                ShapeRenderer.transform.localRotation =
                    Quaternion.Euler(
                        0, 0, VisualRotationDegrees);
            }

            if (WallAttachment != null)
            {
                WallAttachment.color =
                    Color == PortalColor.Yellow
                    ? YellowTint
                    : PurpleTint;
            }

            AlignInteractionToArtwork();
        }

        void UpdatePips(int distance)
        {
            if (DistancePips == null)
                return;

            for (int i = 0; i < DistancePips.Length; i++)
            {
                if (DistancePips[i] != null)
                    DistancePips[i].enabled = i < distance;
            }
        }

        void OnValidate()
        {
            var box = GetComponent<BoxCollider2D>();

            if (box != null)
                box.isTrigger = true;

            RefreshAppearance();

            UpdatePips(
                Color == PortalColor.Yellow ? 1 : 2);
        }
    }
}