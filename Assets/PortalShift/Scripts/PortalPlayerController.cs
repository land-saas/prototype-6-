using UnityEngine;

namespace PortalShift
{
    /// <summary>
    /// Reusable dynamic 2D platform movement. Portal interaction and respawning
    /// belong to the room/game controller, which can call Teleport.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody2D), typeof(CircleCollider2D))]
    public sealed class PortalPlayerController : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField, Min(0f)] private float moveSpeed = 6f;
        [SerializeField, Min(0f)] private float groundAcceleration = 60f;
        [SerializeField, Min(0f)] private float airAcceleration = 38f;
        [SerializeField, Min(0f)] private float deceleration = 70f;
        [SerializeField, Min(0f)] private float jumpSpeed = 11f;
        [SerializeField, Min(0f)] private float gravityScale = 3f;

        [Header("Jump Forgiveness")]
        [SerializeField, Min(0f)] private float coyoteTime = 0.12f;
        [SerializeField, Min(0f)] private float jumpBufferTime = 0.14f;

        [Header("Ground Detection")]
        [SerializeField] private LayerMask groundLayers = ~0;
        [SerializeField, Range(0.01f, 0.15f)] private float groundProbeDistance = 0.045f;
        [SerializeField, Range(0.5f, 1f)] private float minimumGroundNormalY = 0.7f;

        [Header("Input")]
        [Tooltip("Enable for scripted input or automated physics playtests.")]
        [SerializeField] private bool useExternalInput;

        private readonly ContactPoint2D[] contacts = new ContactPoint2D[16];
        private readonly RaycastHit2D[] groundHits = new RaycastHit2D[16];
        private Rigidbody2D body;
        private Collider2D playerCollider;
        private ContactFilter2D groundFilter;
        private float horizontalInput;
        private bool jumpQueued;
        private float coyoteRemaining;
        private float jumpBufferRemaining;

        public bool Grounded { get; private set; }
        public Vector2 Velocity => body != null ? body.linearVelocity : Vector2.zero;

        /// <summary>
        /// External input is opt-in; switching modes clears any pending jump.
        /// </summary>
        public bool UseExternalInput
        {
            get => useExternalInput;
            set
            {
                if (useExternalInput == value)
                    return;

                useExternalInput = value;
                horizontalInput = 0f;
                jumpQueued = false;
                jumpBufferRemaining = 0f;
            }
        }

        private void Awake()
        {
            body = GetComponent<Rigidbody2D>();
            playerCollider = GetComponent<CircleCollider2D>();
            body.bodyType = RigidbodyType2D.Dynamic;
            body.gravityScale = gravityScale;
            body.constraints |= RigidbodyConstraints2D.FreezeRotation;
            body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            body.interpolation = RigidbodyInterpolation2D.Interpolate;
            ConfigureGroundFilter();
        }

        private void OnValidate()
        {
            ConfigureGroundFilter();
        }

        private void ConfigureGroundFilter()
        {
            groundFilter = new ContactFilter2D();
            groundFilter.SetLayerMask(groundLayers);
            groundFilter.useTriggers = false;
        }

        private void Update()
        {
            if (useExternalInput)
                return;

            bool left = Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow);
            bool right = Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow);
            horizontalInput = (right ? 1f : 0f) - (left ? 1f : 0f);

            // Queue in Update so a short key press survives until the physics tick.
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.W) ||
                Input.GetKeyDown(KeyCode.UpArrow))
                jumpQueued = true;
        }

        /// <summary>
        /// Set horizontal input in [-1, 1]. Pass jump=true for a jump press;
        /// a later false call does not discard a press queued for FixedUpdate.
        /// UseExternalInput must be enabled to prevent keyboard input replacing it.
        /// </summary>
        public void SetInput(float horizontal, bool jump)
        {
            horizontalInput = Mathf.Clamp(horizontal, -1f, 1f);
            jumpQueued |= jump;
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            Grounded = DetectGround();
            coyoteRemaining = Grounded ? coyoteTime : Mathf.Max(0f, coyoteRemaining - dt);

            if (jumpQueued)
            {
                // Keep a one-tick buffer even if forgiveness is set to zero.
                jumpBufferRemaining = Mathf.Max(jumpBufferTime, dt);
                jumpQueued = false;
            }

            Vector2 velocity = body.linearVelocity;
            float acceleration = Mathf.Abs(horizontalInput) > 0.01f
                ? (Grounded ? groundAcceleration : airAcceleration)
                : deceleration;
            velocity.x = Mathf.MoveTowards(velocity.x, horizontalInput * moveSpeed, acceleration * dt);

            if (jumpBufferRemaining > 0f && (Grounded || coyoteRemaining > 0f))
            {
                velocity.y = jumpSpeed;
                Grounded = false;
                coyoteRemaining = 0f;
                jumpBufferRemaining = 0f;
            }
            else
            {
                jumpBufferRemaining = Mathf.Max(0f, jumpBufferRemaining - dt);
            }

            body.linearVelocity = velocity;
        }

        private bool DetectGround()
        {
            // A takeoff can retain floor contacts for a tick. It must not grant
            // another jump while ascending or when touching a vertical wall.
            if (body.linearVelocity.y > 0.1f)
                return false;

            int count = playerCollider.GetContacts(groundFilter, contacts);
            for (int i = 0; i < count; i++)
            {
                if (contacts[i].normal.y >= minimumGroundNormalY)
                    return true;
            }

            // A short collider cast covers tiny solver gaps without using a
            // wide overlap that could mistake a side wall for the floor.
            count = playerCollider.Cast(Vector2.down, groundFilter, groundHits, groundProbeDistance);
            for (int i = 0; i < count; i++)
            {
                // Initial-overlap cast hits can report the inverse cast
                // direction even for a side wall. Real floor contact is
                // already covered above, so reject those ambiguous hits.
                if (groundHits[i].distance > 0.001f &&
                    groundHits[i].normal.y >= minimumGroundNormalY)
                    return true;
            }

            return false;
        }

        /// <summary>Move to a safe room spawn and discard momentum and pending jumps.</summary>
        public void Teleport(Vector2 position)
        {
            if (body == null)
            {
                body = GetComponent<Rigidbody2D>();
                playerCollider = GetComponent<CircleCollider2D>();
            }

            body.linearVelocity = Vector2.zero;
            body.angularVelocity = 0f;
            body.position = position;
            transform.position = new Vector3(position.x, position.y, transform.position.z);
            Grounded = false;
            coyoteRemaining = 0f;
            jumpBufferRemaining = 0f;
            jumpQueued = false;
            Physics2D.SyncTransforms();
            body.WakeUp();
        }

        private void OnDisable()
        {
            horizontalInput = 0f;
            jumpQueued = false;
            jumpBufferRemaining = 0f;
            coyoteRemaining = 0f;
            Grounded = false;
        }
    }
}
