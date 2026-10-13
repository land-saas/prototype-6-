using UnityEngine;

namespace PortalShift
{
    public enum PortalMount { Freestanding, Left, Right, Floor, Ceiling }

    [RequireComponent(typeof(BoxCollider2D))]
    public sealed class ShiftPortal : MonoBehaviour
    {
        public PortalShape Shape;
        public PortalColor Color;
        [Tooltip("Distance beyond the trigger edge at which E can interact.")]
        public float InteractionReach = 0.25f;
        public SpriteRenderer ShapeRenderer;
        [Tooltip("Physical mounting surface; independent of the hidden shape-to-room direction.")]
        public PortalMount Mount = PortalMount.Freestanding;
        public SpriteRenderer WallAttachment;
        [HideInInspector] public TextMesh DistanceLabel;
        [Tooltip("Quarter-turn artwork only; shape-to-direction rules never use this angle.")]
        public float VisualRotationDegrees = 90f;
        [Tooltip("One or two white dots show the current distance without room text.")]
        public SpriteRenderer[] DistancePips;
        [Tooltip("Order: Triangle, Diamond, Rectangle, Hexagon. Keeps scene and prefab authoring editable.")]
        public Sprite[] ShapeSprites;
        public Color YellowTint = new Color(1f, .77f, .25f);
        public Color PurpleTint = new Color(.74f, .49f, 1f);
        BoxCollider2D trigger;
        public PortalRoom Room => GetComponentInParent<PortalRoom>();
        public bool CanInteract(PortalPlayerController player)
        {
            if (!isActiveAndEnabled || player == null) return false;
            if (trigger == null) trigger = GetComponent<BoxCollider2D>();
            return trigger.bounds.SqrDistance(player.transform.position) <= InteractionReach * InteractionReach;
        }
        public void Refresh(PortalRuleState rules)
        {
            if (ShapeRenderer != null)
            {
                if (ShapeSprites != null && ShapeSprites.Length == 4) ShapeRenderer.sprite = ShapeSprites[(int)Shape];
                ShapeRenderer.color = Color == PortalColor.Yellow ? YellowTint : PurpleTint;
            }
            if (DistanceLabel != null) DistanceLabel.text = rules.DistanceFor(Color).ToString();
            UpdatePips(rules.DistanceFor(Color));
            if (WallAttachment != null) WallAttachment.color = Color == PortalColor.Yellow ? YellowTint : PurpleTint;
            if (ShapeRenderer != null) ShapeRenderer.transform.localRotation = Quaternion.Euler(0,0,VisualRotationDegrees);
        }
        void UpdatePips(int distance)
        {
            if (DistancePips == null) return;
            for (int i=0;i<DistancePips.Length;i++)
                if (DistancePips[i] != null)
                {
                    DistancePips[i].enabled = i < distance;
                    float spread = distance==1?0:(i==0?-.09f:.09f);
                    Vector2 offset = Mount==PortalMount.Left?new Vector2(.6f,spread):
                        Mount==PortalMount.Right?new Vector2(-.6f,spread):
                        Mount==PortalMount.Floor?new Vector2(spread,.6f):
                        Mount==PortalMount.Ceiling?new Vector2(spread,-.6f):new Vector2(spread,-.89f);
                    DistancePips[i].transform.localPosition = new Vector3(offset.x,offset.y,-.1f);
                }
        }
        void OnValidate()
        {
            var box = GetComponent<BoxCollider2D>();
            if (box != null) box.isTrigger = true;
            if (ShapeRenderer != null && ShapeSprites != null && ShapeSprites.Length == 4)
            {
                ShapeRenderer.sprite = ShapeSprites[(int)Shape];
                ShapeRenderer.color = Color == PortalColor.Yellow ? YellowTint : PurpleTint;
                ShapeRenderer.transform.localRotation = Quaternion.Euler(0,0,VisualRotationDegrees);
            }
            UpdatePips(Color == PortalColor.Yellow ? 1 : 2);
            if (WallAttachment != null) WallAttachment.color = Color == PortalColor.Yellow ? YellowTint : PurpleTint;
        }
    }
}
