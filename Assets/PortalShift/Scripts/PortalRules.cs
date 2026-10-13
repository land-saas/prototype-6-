using System;
using System.Collections.Generic;
using UnityEngine;

namespace PortalShift
{
    public enum PortalShape { Triangle, Diamond, Rectangle, Hexagon }
    public enum PortalColor { Yellow, Purple }
    public enum CardinalDirection { Up, Down, Left, Right }
    public enum BoundaryPolicy { Reject, Wrap }

    /// <summary>
    /// Attempt-wide portal rules, independent of scene objects and player movement.
    /// ShapeDirections is ordered Triangle, Diamond, Rectangle, Hexagon.
    /// </summary>
    [Serializable]
    public sealed class PortalRuleState
    {
        [SerializeField] private int seed;
        [SerializeField] private int phase;
        [SerializeField] private CardinalDirection[] shapeDirections;
        [SerializeField] private BoundaryPolicy boundaryPolicy = PortalShift.BoundaryPolicy.Reject;

        public int Seed { get { return seed; } }
        public int Phase { get { return phase; } }
        public int YellowDistance { get { return phase == 0 ? 1 : 2; } }
        public int PurpleDistance { get { return phase == 0 ? 2 : 1; } }
        public BoundaryPolicy BoundaryPolicy
        {
            get { return boundaryPolicy; }
            set { boundaryPolicy = value; }
        }

        // The wrapper prevents callers from modifying the array behind the rules.
        public IReadOnlyList<CardinalDirection> ShapeDirections
        {
            get
            {
                EnsureMapping();
                return Array.AsReadOnly(shapeDirections);
            }
        }

        public PortalRuleState(int seed)
        {
            Reset(seed);
        }

        /// <summary>Starts a fresh attempt; the scene's boundary policy is retained.</summary>
        public void Reset(int newSeed)
        {
            seed = newSeed;
            phase = 0;
            shapeDirections = new[]
            {
                CardinalDirection.Up, CardinalDirection.Down,
                CardinalDirection.Left, CardinalDirection.Right
            };

            // An explicit PRNG keeps a seed reproducible across runtime versions.
            uint randomState = unchecked((uint)newSeed) ^ 0x9E3779B9u;
            if (randomState == 0) randomState = 0xA341316Cu;
            for (int i = shapeDirections.Length - 1; i > 0; i--)
            {
                int other = (int)(NextRandom(ref randomState) % (uint)(i + 1));
                CardinalDirection held = shapeDirections[i];
                shapeDirections[i] = shapeDirections[other];
                shapeDirections[other] = held;
            }
        }

        public CardinalDirection DirectionFor(PortalShape shape)
        {
            if (!IsShape(shape)) throw new ArgumentOutOfRangeException("shape");
            EnsureMapping();
            return shapeDirections[(int)shape];
        }

        public int DistanceFor(PortalColor color)
        {
            if (!IsColor(color)) throw new ArgumentOutOfRangeException("color");
            return color == PortalColor.Yellow ? YellowDistance : PurpleDistance;
        }

        /// <summary>
        /// Optional authoring/test override. Requires one of every cardinal direction,
        /// in shape enum order. Invalid input never changes the current mapping.
        /// </summary>
        public void SetDirections(params CardinalDirection[] directions)
        {
            if (!IsBijection(directions))
                throw new ArgumentException("Supply exactly one of each cardinal direction.", "directions");
            shapeDirections = (CardinalDirection[])directions.Clone();
        }

        /// <summary>
        /// Uses the current magnitude to find the destination. Only a successful
        /// magnitude-two traversal then flips the color assignment and phase once.
        /// Rejection leaves all attempt state unchanged and destination equal to from.
        /// Registered room coordinates may be sparse inside the configured grid.
        /// </summary>
        public bool TryTraverse(Vector2Int from, PortalShape shape, PortalColor color,
            int width, int height, IReadOnlyCollection<Vector2Int> available,
            out Vector2Int destination, out string reason)
        {
            destination = from;
            if (width <= 0 || height <= 0)
            {
                reason = "The room grid must have positive dimensions.";
                return false;
            }
            if (!IsShape(shape) || !IsColor(color) || !IsBijection(shapeDirections) ||
                (phase != 0 && phase != 1))
            {
                reason = "The portal rules are invalid.";
                return false;
            }
            if (boundaryPolicy != PortalShift.BoundaryPolicy.Reject &&
                boundaryPolicy != PortalShift.BoundaryPolicy.Wrap)
            {
                reason = "The boundary policy is invalid.";
                return false;
            }
            if (!InBounds(from, width, height))
            {
                reason = "The current room is outside the room grid.";
                return false;
            }
            if (available == null || !HasRoom(available, from))
            {
                reason = "The current room is not registered.";
                return false;
            }

            int distance = DistanceFor(color);
            Vector2Int step = OffsetFor(shapeDirections[(int)shape]);
            // Long intermediates prevent overflow for grids near int.MaxValue.
            long x = (long)from.x + step.x * distance;
            long y = (long)from.y + step.y * distance;
            if (boundaryPolicy == PortalShift.BoundaryPolicy.Wrap)
            {
                x = PositiveModulo(x, width);
                y = PositiveModulo(y, height);
            }
            else if (x < 0 || y < 0 || x >= width || y >= height)
            {
                reason = "That portal leads beyond the current room grid.";
                return false;
            }

            Vector2Int candidate = new Vector2Int((int)x, (int)y);
            if (!HasRoom(available, candidate))
            {
                reason = "No room is registered at that destination.";
                return false;
            }

            destination = candidate;
            if (distance == 2) phase = 1 - phase;
            reason = string.Empty;
            return true;
        }

        public static Vector2Int OffsetFor(CardinalDirection direction)
        {
            switch (direction)
            {
                case CardinalDirection.Up: return Vector2Int.up;
                case CardinalDirection.Down: return Vector2Int.down;
                case CardinalDirection.Left: return Vector2Int.left;
                case CardinalDirection.Right: return Vector2Int.right;
                default: throw new ArgumentOutOfRangeException("direction");
            }
        }

        private void EnsureMapping()
        {
            if (!IsBijection(shapeDirections))
                throw new InvalidOperationException("Reset the portal rules before reading their mapping.");
        }

        private static bool IsBijection(CardinalDirection[] directions)
        {
            if (directions == null || directions.Length != 4) return false;
            int seen = 0;
            for (int i = 0; i < directions.Length; i++)
            {
                int direction = (int)directions[i];
                if (direction < 0 || direction > 3) return false;
                int bit = 1 << direction;
                if ((seen & bit) != 0) return false;
                seen |= bit;
            }
            return seen == 15;
        }

        private static bool IsShape(PortalShape shape) { return (int)shape >= 0 && (int)shape < 4; }
        private static bool IsColor(PortalColor color) { return color == PortalColor.Yellow || color == PortalColor.Purple; }
        private static bool InBounds(Vector2Int room, int width, int height)
        {
            return room.x >= 0 && room.y >= 0 && room.x < width && room.y < height;
        }

        private static bool HasRoom(IReadOnlyCollection<Vector2Int> rooms, Vector2Int coordinate)
        {
            foreach (Vector2Int room in rooms) if (room == coordinate) return true;
            return false;
        }

        private static long PositiveModulo(long value, int size) { return ((value % size) + size) % size; }
        private static uint NextRandom(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
    }
}
