# Portal Shift — Prototype 6

Unity **6000.5.10f1** foundation and seeded connector-layout tools. Open **`Assets/PortalShift/Scenes/PortalShift.unity`**. The authored reference is editable; generation creates separate scene assets without replacing it. Prototype 5/Crumble is untouched. No final puzzle, target room, or win condition is authored.

Source repository: [land-saas/prototype-6-](https://github.com/land-saas/prototype-6-).

## Play

**A/D or Left/Right** move, **Space/W** jump, **E** interacts near a portal, **R** resets, and **M** switches whole-map/current-room views. Both views keep movement, physics, and interaction active. The default whole-map camera fits every room anchor; portal traversal does not snap it. The HUD has only zoom/reset icons. Portal one/two white dots show distance.

The circular player has collider radius **0.42**, dynamic physics, and no-friction material. Arrival uses a safe room spawn and clears momentum; entering a trigger does not automatically traverse. Jump is an ordinary button press.

## Create connector previews

1. Stop Play and keep **`PortalShift.unity`** active. The registered Threshold room at coordinate (0,0) is the capture reference. Its current in-memory edits are captured, including unsaved changes; the tool preserves a local snapshot without saving over the reference.
2. Choose **Portal Shift → Connectors → Create Templates from Open Reference**. This creates new **Straight**, **Corner**, and **T-junction** room prefabs under `Assets/PortalShift/Connectors/Prefabs/`, plus a configuration asset. Existing scene objects and the older `Prefabs/RoomTemplate.prefab` remain unchanged. Repeated capture uses unique asset names.
3. In the selected configuration, set **Seed**, **Width**, **Height**, **Straight**, **Corner**, **TJunction**, **Gap**, and **Mirror Platform Variants**. Dimensions range from 2 to 4. Gap is empty space between complete room bounds, minimum 0.1. Mirroring defaults off and mirrors supporting ledge X positions only; it never rotates whole rooms or changes gravity/portal identities.
4. Click **Generate Separate Preview Scene**, or use the matching Connectors menu. The new asset is saved under `Assets/PortalShift/Connectors/Generated/`; its path appears in the configuration. The reference remains active and loaded. Save your reference before switching scenes to inspect the generated asset.
5. Use **Connectors → Select Configuration** to return to the latest configuration. Keep the authored reference active for subsequent generation. Edit the new connector prefabs directly for future variations; generation uses their assigned versions.

A bounded **2×2 consists of four Corners**. Use **4×4** for Straight, Corner, and T-junction arrangements. Seeded generation validates reciprocal sockets, closed boundaries, and connected topology. Generated previews use a fixed seed for reproducible layout and portal mapping.

**Sockets are adjacency metadata, not doorways.** `PortalConnectorRoom` exposes Kind, Sockets, and North/East/South/West marker positions. Its selected gizmos display them. Solid walls remain solid; generation does not carve passages. Every template retains the four independent shape/color portals and their original teleport rules. Logical socket connectivity does not establish physical access or puzzle solvability.

## Current reference and manual edits

The current user reference retains:

| Object | Local position |
| --- | --- |
| Main Floor | Y = -4 |
| Ceiling Wall | Y = 4.1 |
| Step One | (3.7, -3.3) |
| Upper Shelf | (6.4, -1.65) |
| Ceiling Step 1 | (2, -0.275) |
| Ceiling Step 2 | (0, 1.5) |
| Ceiling Step 3 | Deleted |
| Yellow Rectangle | Y = 5.15, Mount = Ceiling |

**The Rectangle is explicitly above the solid ceiling and inaccessible from the room interior.** Capture and generation preserve that placement. For default preview seed 42606, Rectangle is the rightward portal, so the player starting in the left column cannot reach the right column. The default preview therefore does not provide a playable route through all four rooms. Fixing access requires an explicit layout decision.

Expand a room root, select a platform parent rather than its `Landing Edge`, and edit **Transform → Position X/Y** or **Scale X/Y**. **Cmd+D** duplicates its collider, effector, and artwork. One-way ledges retain BoxCollider2D **Is Trigger off**, **Used By Effector on**, and PlatformEffector2D **Use One Way on**. Floor/walls remain solid. No Unity Layer changes are needed. Save with **Cmd+S outside Play**.

Move the entire portal parent; preserve its trigger and Shape Renderer, Wall Attachment, Distance Pips, and Shape Sprites references. `Mount` controls physical cue placement; `VisualRotationDegrees` rotates artwork. Neither assigns logical direction or automatically relocates/resizes the trigger. Original checkerboard colors remain three yellow/one purple: Triangle purple on even coordinate parity, Diamond purple on odd parity. Manual platform edits and existing crew scripts survive capture.

## Rules, checks, and source handoff

Shapes map bijectively to cardinal directions once per attempt and remain stable. Phase 0 starts yellow = one room, purple = two. Successful traversal uses **current distance first**, then flips phase/assignments once if that distance was two, whichever color it belongs to. Rejection preserves state. **Reject remains the boundary policy:** bounded 2×2 two-cell moves fail safely; distances are never shortened.

Use **Portal Shift → Run Rule Checks**, **Run Connector Checks**, and **Connectors → Run Connector Integration Checks**. The integration command generates and reopens separate saved 2×2 and 4×4 scenes using seeds 42606 and 42607, and verifies that the active reference, selection and persistent configuration stay unchanged.

Unity 6000.5.10f1 passed **2,366 automated connector assertions**, covering 16 seeded topologies and 480 logical portal graphs. The current reference passed **165 structural assertions**; the first generated 2×2 scene passed **171**. These checks inspect geometry, references and logical reachability without simulating physics. Four above-ceiling Rectangle access warnings are retained. Saved-scene integration and fresh runtime results are pending. Earlier 152-check results and screenshots precede the current reference and do not verify it; geometry-based route helpers also require a fresh run.

**Build Mac Preview** validates/builds the saved authored scene without regeneration; it does not automatically select a generated connector scene. Keep Assets/`.meta`, Packages, and ProjectSettings together. Verification reports, snapshots, and screenshots remain local and ignored; no screenshot or Library image upload is part of this source handoff. Do not run historical migration helpers after crew edits.
