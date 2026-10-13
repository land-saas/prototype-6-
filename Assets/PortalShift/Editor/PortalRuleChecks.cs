using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PortalShift.Editor
{
    /// <summary>Editor/batch checks requiring no scene, player, or physics simulation.</summary>
    public static class PortalRuleChecks
    {
        [Serializable]
        private sealed class Report
        {
            public bool passed;
            public int assertions;
            public string checkedAtUtc;
            public string unityVersion;
            public string scope = "Automated pure-rule checks; not human keyboard play.";
            public string[] completedGroups;
            public string failure;
        }

        private sealed class Checks
        {
            public int Count;
            public readonly List<string> Groups = new List<string>();
            public void Assert(bool condition, string message)
            {
                Count++;
                if (!condition) throw new InvalidOperationException("Portal rule check failed: " + message);
            }
            public void Group(string name, Action check)
            {
                check();
                Groups.Add(name);
            }
        }

        [MenuItem("Portal Shift/Run Rule Checks")]
        private static void RunFromMenu() { Run(); }

        /// <summary>Returns the passed assertion count; writes rules.json and throws on failure.</summary>
        public static int Run()
        {
            Checks checks = new Checks();
            Report report = new Report
            {
                checkedAtUtc = DateTime.UtcNow.ToString("o"),
                unityVersion = Application.unityVersion
            };
            try
            {
                checks.Group("Seeded bijection and attempt stability", () => CheckMapping(checks));
                checks.Group("All bounded 2x2 one-room and two-room moves", () => CheckTwoByTwo(checks));
                checks.Group("Magnitude is read before phase flips, including yellow", () => CheckPhaseOrder(checks));
                checks.Group("Rejected destinations and invalid input preserve state", () => CheckRejections(checks));
                checks.Group("Optional wrapping preserves magnitude and flips once", () => CheckWrapping(checks));
                checks.Group("Room holes, reset, mapping copy, and large grid coordinates", () => CheckAuthoring(checks));
                report.passed = true;
                Debug.Log("Portal Shift rule checks passed (" + checks.Count + " assertions). " + ReportPath());
                return checks.Count;
            }
            catch (Exception error)
            {
                report.failure = error.Message;
                Debug.LogError(error);
                throw;
            }
            finally
            {
                report.assertions = checks.Count;
                report.completedGroups = checks.Groups.ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath()));
                File.WriteAllText(ReportPath(), JsonUtility.ToJson(report, true));
            }
        }

        private static string ReportPath()
        {
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), "Verification", "rules.json");
        }

        private static PortalRuleState FixedRules()
        {
            PortalRuleState state = new PortalRuleState(42);
            state.SetDirections(CardinalDirection.Up, CardinalDirection.Down,
                CardinalDirection.Left, CardinalDirection.Right);
            return state;
        }

        private static HashSet<Vector2Int> Grid(int width, int height)
        {
            HashSet<Vector2Int> rooms = new HashSet<Vector2Int>();
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++) rooms.Add(new Vector2Int(x, y));
            return rooms;
        }

        private static void CheckMapping(Checks c)
        {
            int[] seeds = { 0, 1, 2, 17, -1, int.MinValue, int.MaxValue, 908172 };
            HashSet<string> mappings = new HashSet<string>();
            foreach (int seed in seeds)
            {
                PortalRuleState a = new PortalRuleState(seed);
                PortalRuleState b = new PortalRuleState(seed);
                HashSet<CardinalDirection> directions = new HashSet<CardinalDirection>();
                string signature = string.Empty;
                c.Assert(a.Seed == seed && a.Phase == 0, "initial seed and phase");
                c.Assert(a.YellowDistance == 1 && a.PurpleDistance == 2, "initial color magnitudes");
                c.Assert(a.BoundaryPolicy == BoundaryPolicy.Reject, "reject is the default boundary policy");
                for (int i = 0; i < 4; i++)
                {
                    PortalShape shape = (PortalShape)i;
                    CardinalDirection direction = a.DirectionFor(shape);
                    directions.Add(direction);
                    signature += (int)direction;
                    c.Assert(direction == b.DirectionFor(shape), "same seed reproduces shape direction");
                    c.Assert(direction == a.DirectionFor(shape), "reading does not alter mapping");
                    c.Assert(direction == a.ShapeDirections[i], "mapping API uses shape enum order");
                }
                c.Assert(directions.Count == 4, "every mapping is a cardinal bijection");
                mappings.Add(signature);
            }
            c.Assert(mappings.Count > 1, "different seeds can produce different mappings");
        }

        private static void CheckTwoByTwo(Checks c)
        {
            HashSet<Vector2Int> rooms = Grid(2, 2);
            Vector2Int[] expectedSteps =
            {
                new Vector2Int(0, 1), new Vector2Int(0, -1),
                new Vector2Int(-1, 0), new Vector2Int(1, 0)
            };
            foreach (Vector2Int from in rooms)
            {
                for (int shapeIndex = 0; shapeIndex < 4; shapeIndex++)
                {
                    for (int colorIndex = 0; colorIndex < 2; colorIndex++)
                    {
                        PortalRuleState state = FixedRules();
                        PortalShape shape = (PortalShape)shapeIndex;
                        PortalColor color = (PortalColor)colorIndex;
                        Vector2Int expected = from + expectedSteps[shapeIndex] * (color == PortalColor.Yellow ? 1 : 2);
                        bool expectedSuccess = rooms.Contains(expected);
                        Vector2Int destination;
                        string reason;
                        bool success = state.TryTraverse(from, shape, color, 2, 2, rooms, out destination, out reason);
                        c.Assert(success == expectedSuccess, "bounded 2x2 move accepts only the exact in-grid destination");
                        c.Assert(destination == (success ? expected : from), "bounded 2x2 destination preserves distance");
                        c.Assert(state.Phase == 0 && state.YellowDistance == 1 && state.PurpleDistance == 2,
                            "one-room success and two-room rejection do not flip phase");
                        c.Assert(success ? reason.Length == 0 : reason.Length > 0, "traversal reason reports rejection");
                        if (color == PortalColor.Purple) c.Assert(!success, "two cells cannot reach a different bounded 2x2 room");
                    }
                }
            }
        }

        private static void CheckPhaseOrder(Checks c)
        {
            PortalRuleState state = FixedRules();
            HashSet<Vector2Int> rooms = Grid(4, 4);
            CardinalDirection[] before = new CardinalDirection[4];
            for (int i = 0; i < 4; i++) before[i] = state.DirectionFor((PortalShape)i);
            Vector2Int destination;
            string reason;
            c.Assert(state.TryTraverse(Vector2Int.zero, PortalShape.Triangle, PortalColor.Purple,
                4, 4, rooms, out destination, out reason), "purple's initial two-room traversal succeeds");
            c.Assert(destination == new Vector2Int(0, 2), "purple moves two rooms before flipping");
            c.Assert(state.Phase == 1 && state.YellowDistance == 2 && state.PurpleDistance == 1, "purple flips once after arrival");
            c.Assert(state.TryTraverse(destination, PortalShape.Hexagon, PortalColor.Purple,
                4, 4, rooms, out destination, out reason), "purple now traverses one room");
            c.Assert(destination == new Vector2Int(1, 2) && state.Phase == 1, "purple's new one-room traversal does not flip");
            c.Assert(state.TryTraverse(destination, PortalShape.Diamond, PortalColor.Yellow,
                4, 4, rooms, out destination, out reason), "yellow's current two-room traversal succeeds");
            c.Assert(destination == new Vector2Int(1, 0), "yellow moves two rooms before flipping");
            c.Assert(state.Phase == 0 && state.YellowDistance == 1 && state.PurpleDistance == 2, "yellow flips assignment back once");
            c.Assert(state.TryTraverse(destination, PortalShape.Hexagon, PortalColor.Yellow,
                4, 4, rooms, out destination, out reason), "yellow now traverses one room");
            c.Assert(destination == new Vector2Int(2, 0) && state.Phase == 0, "yellow one-room traversal does not flip");
            for (int i = 0; i < 4; i++) c.Assert(state.DirectionFor((PortalShape)i) == before[i], "traversal preserves attempt mapping");
        }

        private static void CheckRejections(Checks c)
        {
            PortalRuleState state = FixedRules();
            HashSet<Vector2Int> rooms = Grid(4, 4);
            AssertRejected(c, state, Vector2Int.zero, PortalShape.Diamond, PortalColor.Purple, 4, 4, rooms);
            AssertRejected(c, state, Vector2Int.zero, (PortalShape)99, PortalColor.Purple, 4, 4, rooms);
            AssertRejected(c, state, Vector2Int.zero, PortalShape.Triangle, (PortalColor)(-1), 4, 4, rooms);
            AssertRejected(c, state, Vector2Int.zero, PortalShape.Triangle, PortalColor.Purple, 0, 4, rooms);
            AssertRejected(c, state, Vector2Int.zero, PortalShape.Triangle, PortalColor.Purple, 4, -1, rooms);
            AssertRejected(c, state, new Vector2Int(-1, 0), PortalShape.Triangle, PortalColor.Purple, 4, 4, rooms);
            AssertRejected(c, state, new Vector2Int(4, 0), PortalShape.Triangle, PortalColor.Purple, 4, 4, rooms);
            AssertRejected(c, state, Vector2Int.zero, PortalShape.Triangle, PortalColor.Purple, 4, 4, null);
            Vector2Int destination;
            string reason;
            state.TryTraverse(Vector2Int.zero, PortalShape.Triangle, PortalColor.Purple, 4, 4, rooms, out destination, out reason);
            AssertRejected(c, state, destination, PortalShape.Triangle, PortalColor.Yellow, 4, 4, rooms);
            c.Assert(state.Phase == 1, "rejecting yellow's two-room move preserves the flipped assignment");
            state.BoundaryPolicy = (BoundaryPolicy)100;
            AssertRejected(c, state, destination, PortalShape.Hexagon, PortalColor.Purple, 4, 4, rooms);
        }

        private static void AssertRejected(Checks c, PortalRuleState state, Vector2Int from,
            PortalShape shape, PortalColor color, int width, int height, IReadOnlyCollection<Vector2Int> rooms)
        {
            int phase = state.Phase;
            int yellow = state.YellowDistance;
            int purple = state.PurpleDistance;
            int seed = state.Seed;
            Vector2Int destination;
            string reason;
            c.Assert(!state.TryTraverse(from, shape, color, width, height, rooms, out destination, out reason), "invalid traversal rejected");
            c.Assert(destination == from && !string.IsNullOrEmpty(reason), "rejection has safe destination and explanation");
            c.Assert(state.Phase == phase && state.YellowDistance == yellow && state.PurpleDistance == purple && state.Seed == seed,
                "rejection preserves global state");
        }

        private static void CheckWrapping(Checks c)
        {
            HashSet<Vector2Int> rooms = Grid(2, 2);
            for (int i = 0; i < 4; i++)
            {
                PortalRuleState state = FixedRules();
                state.BoundaryPolicy = BoundaryPolicy.Wrap;
                Vector2Int destination;
                string reason;
                c.Assert(state.TryTraverse(Vector2Int.zero, (PortalShape)i, PortalColor.Purple,
                    2, 2, rooms, out destination, out reason), "optional wrap permits a two-room traversal");
                c.Assert(destination == Vector2Int.zero, "two-room wrap returns to the same 2x2 coordinate");
                c.Assert(state.Phase == 1, "a successful wrap-to-self flips exactly once");
                c.Assert(state.TryTraverse(Vector2Int.zero, (PortalShape)i, PortalColor.Yellow,
                    2, 2, rooms, out destination, out reason), "yellow now has wrapping magnitude two");
                c.Assert(destination == Vector2Int.zero && state.Phase == 0, "yellow wrap restores assignment exactly once");
            }
            PortalRuleState negativeWrap = FixedRules();
            negativeWrap.BoundaryPolicy = BoundaryPolicy.Wrap;
            Vector2Int wrapped;
            string why;
            c.Assert(negativeWrap.TryTraverse(Vector2Int.zero, PortalShape.Rectangle, PortalColor.Yellow,
                2, 2, rooms, out wrapped, out why), "negative-direction wrapping succeeds");
            c.Assert(wrapped == new Vector2Int(1, 0) && negativeWrap.Phase == 0, "negative wrapping lands correctly without changing one-room phase");
        }

        private static void CheckAuthoring(Checks c)
        {
            PortalRuleState state = FixedRules();
            HashSet<Vector2Int> rooms = Grid(4, 4);
            rooms.Remove(new Vector2Int(0, 2));
            AssertRejected(c, state, Vector2Int.zero, PortalShape.Triangle, PortalColor.Purple, 4, 4, rooms);
            rooms.Remove(Vector2Int.zero);
            AssertRejected(c, state, Vector2Int.zero, PortalShape.Hexagon, PortalColor.Yellow, 4, 4, rooms);
            // A jump of two uses its destination; the intermediate chamber need not exist.
            rooms = new HashSet<Vector2Int> { Vector2Int.zero, new Vector2Int(0, 2) };
            Vector2Int destination;
            string reason;
            c.Assert(state.TryTraverse(Vector2Int.zero, PortalShape.Triangle, PortalColor.Purple,
                4, 4, rooms, out destination, out reason), "magnitude-two traversal does not require an intermediate room");
            state.BoundaryPolicy = BoundaryPolicy.Wrap;
            state.Reset(1234);
            PortalRuleState matching = new PortalRuleState(1234);
            c.Assert(state.Phase == 0 && state.Seed == 1234 && state.YellowDistance == 1 && state.PurpleDistance == 2, "reset starts a fresh assignment");
            c.Assert(state.BoundaryPolicy == BoundaryPolicy.Wrap, "reset preserves scene boundary configuration");
            for (int i = 0; i < 4; i++) c.Assert(state.DirectionFor((PortalShape)i) == matching.DirectionFor((PortalShape)i), "reset uses requested seed");
            CardinalDirection[] mapping = { CardinalDirection.Up, CardinalDirection.Down, CardinalDirection.Left, CardinalDirection.Right };
            state.SetDirections(mapping);
            mapping[0] = CardinalDirection.Left;
            c.Assert(state.DirectionFor(PortalShape.Triangle) == CardinalDirection.Up, "author mapping is copied");
            bool invalidMappingRejected = false;
            try { state.SetDirections(CardinalDirection.Up, CardinalDirection.Up, CardinalDirection.Left, CardinalDirection.Right); }
            catch (ArgumentException) { invalidMappingRejected = true; }
            c.Assert(invalidMappingRejected && state.DirectionFor(PortalShape.Diamond) == CardinalDirection.Down, "invalid author mapping cannot replace valid state");
            state.BoundaryPolicy = BoundaryPolicy.Reject;
            Vector2Int edge = new Vector2Int(int.MaxValue - 1, 0);
            rooms = new HashSet<Vector2Int> { edge };
            AssertRejected(c, state, edge, PortalShape.Hexagon, PortalColor.Purple, int.MaxValue, 1, rooms);
        }
    }
}
