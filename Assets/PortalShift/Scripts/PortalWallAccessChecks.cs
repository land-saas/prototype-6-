using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PortalShift
{
    /// <summary>
    /// Opt-in wall-portal access checks. Each path begins at the authored spawn
    /// using Respawn, then uses ordinary controller movement and jump presses.
    /// No path teleports the player to a portal or changes the attempt rules.
    /// </summary>
    public static class PortalWallAccessChecks
    {
        private sealed class PathResult
        {
            public bool reached;
            public string evidence;
        }

        public static IEnumerator Run(PortalStage stage, Action<string, bool, string> record)
        {
            if (stage == null || stage.Player == null || stage.CurrentRoom == null)
            {
                record("Wall access checks have a current room and player", false, "Stage, current room, or player is missing.");
                yield break;
            }

            var player = stage.Player;
            var circle = player.GetComponent<CircleCollider2D>();
            if (circle == null)
            {
                record("Wall access paths use the circular player", false, "CircleCollider2D is missing.");
                yield break;
            }

            bool previousExternalInput = player.UseExternalInput;
            player.UseExternalInput = true;
            Vector2 origin = stage.CurrentRoom.transform.position;
            Vector2Int coordinate = stage.CurrentRoom.Coordinate;
            int phase = stage.Rules.Phase;
            int traversals = stage.TraversalCount;
            var left = FindPortal(stage.CurrentRoom, PortalShape.Triangle);
            var right = FindPortal(stage.CurrentRoom, PortalShape.Diamond);
            var floor = FindPortal(stage.CurrentRoom, PortalShape.Rectangle);
            var ceiling = FindPortal(stage.CurrentRoom, PortalShape.Hexagon);

            record("Wall access paths find all four geometric portals",
                left != null && right != null && floor != null && ceiling != null,
                "Triangle left " + (left != null) + "; diamond right " + (right != null) +
                "; rectangle floor " + (floor != null) + "; hexagon ceiling " + (ceiling != null));

            // Walk directly into the left wall. The circle must stop on the
            // room side of its solid inner surface while remaining in E reach.
            yield return FromSpawn(stage);
            player.SetInput(-1f, false);
            yield return FixedSteps(100);
            player.SetInput(0f, false);
            var leftWall = FindSolidCollider(stage.CurrentRoom, "Left Wall");
            float leftLimit = leftWall != null ? leftWall.bounds.max.x + circle.bounds.extents.x : float.NaN;
            bool leftContact = TouchesNormal(circle, Vector2.right);
            record("Normal walking reaches the left wall portal",
                left != null && left.CanInteract(player) && player.Grounded,
                Evidence(player, origin) + "; in interaction reach " + (left != null && left.CanInteract(player)));
            record("The left wall blocks the circular player",
                leftWall != null && leftContact && player.transform.position.x >= leftLimit - .08f &&
                player.transform.position.x <= leftLimit + .12f && Mathf.Abs(player.Velocity.x) < .25f,
                Evidence(player, origin) + "; expected center limit " + F(leftLimit - origin.x) +
                "; solid side contact " + leftContact);

            // Clear the actual jump gate, then walk along the floor. One-way
            // staircase colliders permit the same normal floor route to both
            // floor and right-wall portals without needing a physics bypass.
            var gatePath = new PathResult();
            yield return ClearGateFromSpawn(stage, origin, gatePath);
            var floorPath = new PathResult();
            if (gatePath.reached)
                yield return WalkTo(player, origin.x, 2f, floorPath);
            record("Normal walking and jumping reach the floor portal",
                gatePath.reached && floorPath.reached && floor != null && floor.CanInteract(player) && player.Grounded,
                gatePath.evidence + "; " + floorPath.evidence + "; " + Evidence(player, origin) +
                "; in interaction reach " + (floor != null && floor.CanInteract(player)));

            gatePath = new PathResult();
            yield return ClearGateFromSpawn(stage, origin, gatePath);
            if (gatePath.reached)
            {
                player.SetInput(1f, false);
                yield return FixedSteps(140);
                player.SetInput(0f, false);
            }
            var rightWall = FindSolidCollider(stage.CurrentRoom, "Right Wall");
            float rightLimit = rightWall != null ? rightWall.bounds.min.x - circle.bounds.extents.x : float.NaN;
            bool rightContact = TouchesNormal(circle, Vector2.left);
            record("Normal walking and jumping reach the right wall portal",
                gatePath.reached && right != null && right.CanInteract(player) && player.Grounded,
                gatePath.evidence + "; " + Evidence(player, origin) +
                "; in interaction reach " + (right != null && right.CanInteract(player)));
            record("The right wall blocks the circular player",
                rightWall != null && rightContact && player.transform.position.x <= rightLimit + .08f &&
                player.transform.position.x >= rightLimit - .12f && Mathf.Abs(player.Velocity.x) < .25f,
                Evidence(player, origin) + "; expected center limit " + F(rightLimit - origin.x) +
                "; solid side contact " + rightContact);

            var ceilingPath = new PathResult();
            yield return CeilingFromSpawn(stage, origin, ceilingPath, record);
            bool ceilingReach = ceilingPath.reached && ceiling != null && ceiling.CanInteract(player) && player.Grounded;
            record("Normal staircase jumps reach the ceiling portal", ceilingReach,
                Evidence(player, origin) + "; in interaction reach " + (ceiling != null && ceiling.CanInteract(player)) +
                "; no gate positioning teleports used");

            if (ceilingPath.reached)
            {
                player.SetInput(0f, true);
                float highestY = player.transform.position.y;
                bool ceilingContact = false;
                bool falseGroundAtHead = false;
                for (int i = 0; i < 45; i++)
                {
                    yield return new WaitForFixedUpdate();
                    highestY = Mathf.Max(highestY, player.transform.position.y);
                    if (TouchesNormal(circle, Vector2.down))
                    {
                        ceilingContact = true;
                        falseGroundAtHead |= player.Grounded;
                    }
                }
                player.SetInput(0f, false);
                yield return WaitGrounded(player, 2f);
                var ceilingWall = FindSolidCollider(stage.CurrentRoom, "Ceiling Wall");
                float topLimit = ceilingWall != null ? ceilingWall.bounds.min.y - circle.bounds.extents.y : float.NaN;
                record("The ceiling blocks jumping without counting head contact as ground",
                    ceilingWall != null && ceilingContact && !falseGroundAtHead && highestY <= topLimit + .08f &&
                    highestY >= topLimit - .12f && player.Grounded,
                    "Highest local center y " + F(highestY - origin.y) +
                    "; expected ceiling limit " + F(topLimit - origin.y) +
                    "; ceiling contact " + ceilingContact + "; false head grounding " + falseGroundAtHead +
                    "; " + Evidence(player, origin));
            }
            else
                record("The ceiling blocks jumping without counting head contact as ground", false,
                    "Ceiling collision check skipped because the scripted normal staircase path did not reach the top.");

            record("Walking into all four wall gates never traverses without E",
                stage.CurrentRoom.Coordinate == coordinate && stage.Rules.Phase == phase &&
                stage.TraversalCount == traversals,
                "Current room " + stage.CurrentRoom.Coordinate + "; phase " + stage.Rules.Phase +
                "; traversal count " + stage.TraversalCount + "; scripted movement only, no portal use calls");

            player.SetInput(0f, false);
            stage.Respawn();
            yield return WaitGrounded(player, 3f);
            player.UseExternalInput = previousExternalInput;
        }

        /// <summary>
        /// Reach one authored wall gate from the current room's safe spawn,
        /// using only normal movement and jump input. Leaves the player at the
        /// gate for the caller's subsequent E interaction. Does not traverse,
        /// reset the attempt, change camera view, or teleport to any gate.
        /// </summary>
        public static IEnumerator Approach(PortalStage stage, ShiftPortal portal, Action<string, bool, string> record)
        {
            if (stage == null || stage.Player == null || stage.CurrentRoom == null ||
                portal == null || portal.Room != stage.CurrentRoom)
            {
                record("Normal movement has a valid current-room portal to approach", false,
                    "Stage, player, current room, or matching portal is missing.");
                yield break;
            }

            var player = stage.Player;
            bool previousExternalInput = player.UseExternalInput;
            player.UseExternalInput = true;
            Vector2 origin = stage.CurrentRoom.transform.position;
            Vector2Int coordinate = stage.CurrentRoom.Coordinate;
            int phase = stage.Rules.Phase;
            int traversals = stage.TraversalCount;
            var path = new PathResult();

            switch (portal.Mount)
            {
                case PortalMount.Left:
                    yield return FromSpawn(stage);
                    player.SetInput(-1f, false);
                    yield return FixedSteps(100);
                    player.SetInput(0f, false);
                    path.reached = portal.CanInteract(player) && player.Grounded;
                    path.evidence = "Walked left for 100 fixed ticks from the safe spawn";
                    break;
                case PortalMount.Floor:
                    var floorGate = new PathResult();
                    yield return ClearGateFromSpawn(stage, origin, floorGate);
                    if (floorGate.reached)
                        yield return WalkTo(player, portal.transform.position.x, 2f, path);
                    path.reached &= floorGate.reached;
                    path.evidence = floorGate.evidence + "; " + path.evidence;
                    break;
                case PortalMount.Right:
                    var rightGate = new PathResult();
                    yield return ClearGateFromSpawn(stage, origin, rightGate);
                    if (rightGate.reached)
                    {
                        player.SetInput(1f, false);
                        yield return FixedSteps(140);
                        player.SetInput(0f, false);
                        path.reached = portal.CanInteract(player) && player.Grounded;
                    }
                    path.evidence = rightGate.evidence + "; walked right for 140 fixed ticks";
                    break;
                case PortalMount.Ceiling:
                    yield return CeilingFromSpawn(stage, origin, path, null);
                    break;
                default:
                    path.evidence = "This check supports authored wall mounts; mount is " + portal.Mount;
                    break;
            }

            player.SetInput(0f, false);
            bool unchanged = stage.CurrentRoom.Coordinate == coordinate && stage.Rules.Phase == phase &&
                stage.TraversalCount == traversals;
            record("Normal movement reaches " + portal.Mount + " portal in room " + coordinate,
                path.reached && portal.CanInteract(player) && player.Grounded && unchanged,
                "Shape " + portal.Shape + "; " + path.evidence + "; " + Evidence(player, origin) +
                "; E reach " + portal.CanInteract(player) + "; attempt state unchanged " + unchanged +
                "; safe-spawn Respawn only; no gate positioning teleport");
            player.UseExternalInput = previousExternalInput;
        }

        private static IEnumerator CeilingFromSpawn(PortalStage stage, Vector2 origin, PathResult result,
            Action<string, bool, string> record)
        {
            // Follow only the retained, authored one-way ledges. Read their
            // actual world collider bounds rather than assume a missing roof
            // platform or old transform coordinates still exist.
            var ledges = new List<Collider2D>();
            foreach (var collider in stage.CurrentRoom.GetComponentsInChildren<Collider2D>())
                if (!collider.isTrigger && collider.usedByEffector) ledges.Add(collider);
            ledges.Sort((a, b) => a.bounds.max.y.CompareTo(b.bounds.max.y));
            if (ledges.Count == 0)
            {
                result.evidence = "No retained authored one-way ledges were found.";
                record?.Invoke("Ceiling route walks to the authored staircase", false, result.evidence);
                yield break;
            }

            var player = stage.Player;
            float radius = player.GetComponent<CircleCollider2D>().bounds.extents.y;
            var gate = new PathResult();
            yield return ClearGateFromSpawn(stage, origin, gate);
            var approach = new PathResult();
            float firstX = InsideX(ledges[0].bounds, player.transform.position.x, radius);
            if (gate.reached)
                yield return WalkTo(player, firstX, 3f, approach);
            bool stairsReached = gate.reached && approach.reached;
            string trace = gate.evidence + "; " + approach.evidence + "; retained ledges " + ledges.Count;
            record?.Invoke("Ceiling route walks to the authored staircase",
                stairsReached && player.Grounded, trace + "; " + Evidence(player, origin));

            for (int i = 0; i < ledges.Count; i++)
            {
                var edgeWalk = new PathResult { reached = i == 0, evidence = "Initial floor approach" };
                if (stairsReached && i > 0)
                {
                    // Move along the current ledge toward the destination's
                    // center before jumping. This shortens gaps while keeping
                    // the circle center safely inside the authored footprint.
                    float launchX = InsideX(ledges[i - 1].bounds, ledges[i].bounds.center.x, radius);
                    yield return WalkTo(player, launchX, 3f, edgeWalk);
                    stairsReached &= edgeWalk.reached && player.Grounded;
                }
                var bounds = ledges[i].bounds;
                Vector2 target = new Vector2(InsideX(bounds, player.transform.position.x, radius),
                    bounds.max.y + radius);
                var hop = new PathResult();
                if (stairsReached)
                    yield return HopTo(player, target, hop);
                stairsReached &= hop.reached;
                string evidence = "Authored " + ledges[i].name + "; target local " + (target - origin) +
                    "; " + edgeWalk.evidence + "; " + hop.evidence + "; " + Evidence(player, origin);
                record?.Invoke("Ceiling route reaches ledge " + (i + 1) + " with normal jumps", hop.reached, evidence);
                trace += "; " + ledges[i].name + " " + hop.reached + " (" + edgeWalk.evidence + "; " + hop.evidence + ")";
            }
            var ceilingPortal = FindMountedPortal(stage.CurrentRoom, PortalMount.Ceiling);
            var finalWalk = new PathResult();
            if (stairsReached && ceilingPortal != null)
                yield return WalkTo(player, ceilingPortal.transform.position.x, 3f, finalWalk);
            stairsReached &= finalWalk.reached && player.Grounded && ceilingPortal != null && ceilingPortal.CanInteract(player);
            trace += "; ceiling alignment " + finalWalk.evidence + "; standing in E reach " + stairsReached;
            result.reached = stairsReached;
            result.evidence = trace;
        }

        private static float InsideX(Bounds bounds, float towardX, float radius)
        {
            float inset = Mathf.Min(radius * .5f, bounds.size.x * .2f);
            return Mathf.Clamp(towardX, bounds.min.x + inset, bounds.max.x - inset);
        }

        private static IEnumerator FromSpawn(PortalStage stage)
        {
            stage.Player.SetInput(0f, false);
            stage.Respawn();
            yield return WaitGrounded(stage.Player, 3f);
        }

        private static IEnumerator ClearGateFromSpawn(PortalStage stage, Vector2 origin, PathResult result)
        {
            yield return FromSpawn(stage);
            var gate = FindSolidCollider(stage.CurrentRoom, "Jump Gate");
            var floor = FindSolidCollider(stage.CurrentRoom, "Main Floor");
            var circle = stage.Player.GetComponent<CircleCollider2D>();
            if (gate == null || floor == null || circle == null)
            {
                result.evidence = "Authored Jump Gate, Main Floor, or circular player collider is missing.";
                yield break;
            }
            float approachX = gate.bounds.min.x - circle.bounds.extents.x - .08f;
            Vector2 landing = new Vector2(gate.bounds.max.x + circle.bounds.extents.x + 1f,
                floor.bounds.max.y + circle.bounds.extents.y);
            var approach = new PathResult();
            yield return WalkTo(stage.Player, approachX, 2f, approach);
            var jump = new PathResult();
            if (approach.reached && stage.Player.Grounded)
                yield return HopTo(stage.Player, landing, jump);
            result.reached = approach.reached && jump.reached &&
                stage.Player.transform.position.x > gate.bounds.max.x + circle.bounds.extents.x + .08f;
            result.evidence = "Gate approach " + approach.reached + "; target local " + (landing - origin) +
                "; jump " + jump.reached + "; " + jump.evidence;
        }

        private static IEnumerator WalkTo(PortalPlayerController player, float targetX, float seconds, PathResult result)
        {
            int maximumSteps = Mathf.CeilToInt(seconds / Time.fixedDeltaTime);
            int stableSteps = 0;
            for (int i = 0; i < maximumSteps; i++)
            {
                float delta = targetX - player.transform.position.x;
                player.SetInput(Steer(delta), false);
                yield return new WaitForFixedUpdate();
                if (Mathf.Abs(targetX - player.transform.position.x) <= .16f && Mathf.Abs(player.Velocity.x) < .25f)
                    stableSteps++;
                else stableSteps = 0;
                if (stableSteps >= 3)
                {
                    result.reached = true;
                    result.evidence = "Walk reached x " + F(targetX) + " in " + (i + 1) + " fixed ticks";
                    break;
                }
            }
            player.SetInput(0f, false);
            if (!result.reached)
                result.evidence = "Bounded walk failed: target x " + F(targetX) + "; actual x " + F(player.transform.position.x);
        }

        private static IEnumerator HopTo(PortalPlayerController player, Vector2 target, PathResult result)
        {
            float highestY = player.transform.position.y;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                player.SetInput(0f, false);
                yield return WaitGrounded(player, 2f);
                if (!player.Grounded) break;
                float initialY = player.transform.position.y;
                player.SetInput(Steer(target.x - player.transform.position.x), true);
                bool airborne = false;
                int stableSteps = 0;
                int maximumSteps = Mathf.CeilToInt(2f / Time.fixedDeltaTime);
                for (int i = 0; i < maximumSteps; i++)
                {
                    yield return new WaitForFixedUpdate();
                    highestY = Mathf.Max(highestY, player.transform.position.y);
                    airborne |= !player.Grounded && player.transform.position.y > initialY + .08f;
                    float delta = target.x - player.transform.position.x;
                    player.SetInput(Steer(delta), false);
                    if (airborne && player.Grounded && Mathf.Abs(player.Velocity.y) < .15f)
                        stableSteps++;
                    else stableSteps = 0;
                    if (stableSteps < 4) continue;
                    bool nearX = Mathf.Abs(delta) <= .23f;
                    bool highEnough = player.transform.position.y >= target.y - .17f;
                    if (nearX && highEnough)
                    {
                        result.reached = true;
                        result.evidence = "Physics hop " + (attempt + 1) + " landed after " + (i + 1) +
                            " fixed ticks; highest world y " + F(highestY);
                    }
                    break;
                }
                if (result.reached) break;
            }
            player.SetInput(0f, false);
            if (!result.reached)
                result.evidence = "Bounded physics hops failed: target " + target + "; actual " +
                    (Vector2)player.transform.position + "; highest world y " + F(highestY) + "; grounded " + player.Grounded;
        }

        private static float Steer(float delta)
        {
            // Slow near a landing center instead of oscillating full-speed
            // across narrow platforms. This is ordinary horizontal input.
            if (Mathf.Abs(delta) <= .065f) return 0f;
            return Mathf.Clamp(delta * 2.5f, -1f, 1f);
        }

        private static IEnumerator WaitGrounded(PortalPlayerController player, float seconds)
        {
            int maximumSteps = Mathf.CeilToInt(seconds / Time.fixedDeltaTime);
            int stableSteps = 0;
            for (int i = 0; i < maximumSteps; i++)
            {
                yield return new WaitForFixedUpdate();
                if (player.Grounded && Mathf.Abs(player.Velocity.y) < .15f) stableSteps++;
                else stableSteps = 0;
                if (stableSteps >= 4) yield break;
            }
        }

        private static IEnumerator FixedSteps(int count)
        {
            for (int i = 0; i < count; i++) yield return new WaitForFixedUpdate();
        }

        private static bool TouchesNormal(Collider2D collider, Vector2 direction)
        {
            var contacts = new ContactPoint2D[16];
            int count = collider.GetContacts(contacts);
            for (int i = 0; i < count; i++)
                if (Vector2.Dot(contacts[i].normal, direction) > .7f) return true;
            return false;
        }

        private static ShiftPortal FindPortal(PortalRoom room, PortalShape shape)
        {
            foreach (var portal in room.GetComponentsInChildren<ShiftPortal>())
                if (portal.Shape == shape) return portal;
            return null;
        }

        private static ShiftPortal FindMountedPortal(PortalRoom room, PortalMount mount)
        {
            foreach (var portal in room.GetComponentsInChildren<ShiftPortal>())
                if (portal.Mount == mount) return portal;
            return null;
        }

        private static Collider2D FindSolidCollider(PortalRoom room, string name)
        {
            foreach (var collider in room.GetComponentsInChildren<Collider2D>())
                if (!collider.isTrigger && collider.name == name) return collider;
            return null;
        }

        private static string Evidence(PortalPlayerController player, Vector2 origin)
        {
            Vector2 local = (Vector2)player.transform.position - origin;
            return "Local position (" + F(local.x) + ", " + F(local.y) + "); velocity (" +
                F(player.Velocity.x) + ", " + F(player.Velocity.y) + "); grounded " + player.Grounded;
        }

        private static string F(float value) => value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }
}
