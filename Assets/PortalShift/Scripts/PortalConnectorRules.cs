using System;

namespace PortalShift
{
    public enum ConnectorKind { Straight, Corner, TJunction }

    /// <summary>Physical room sockets; these do not identify or remap ShiftPortal objects.</summary>
    [Flags]
    public enum ConnectorSockets
    {
        None = 0,
        North = 1,
        East = 2,
        South = 4,
        West = 8
    }

    [Serializable]
    public struct ConnectorCell
    {
        public int X;
        public int Y;
        public ConnectorSockets Sockets;
        public ConnectorKind Kind;

        public ConnectorCell(int x, int y, ConnectorSockets sockets)
        {
            X = x;
            Y = y;
            Sockets = sockets;
            Kind = PortalConnectorRules.Classify(sockets);
        }
    }

    /// <summary>
    /// Seeded, bounded connector topology, independent of Unity objects and portal rules.
    /// Every cell has two or three reciprocal sockets and every cell is connected.
    /// Results are row-major (Y * width + X), with North increasing Y.
    /// A bounded 2 x 2 grid necessarily contains four Corners; it cannot contain a
    /// Straight or T-junction without an out-of-bounds socket.
    /// </summary>
    public static class PortalConnectorRules
    {
        public const int MaxRoomCount = 256;
        public const int MaxSearchNodes = 262144;

        private const int AllSockets = 15;
        private static readonly int[] Masks = { 3, 5, 9, 6, 10, 12, 7, 11, 13, 14 };
        private static readonly int[] Directions = { 1, 2, 4, 8 };
        private static readonly int[] Opposites = { 4, 8, 1, 2 };
        private static readonly int[] OffsetX = { 0, 1, 0, -1 };
        private static readonly int[] OffsetY = { 1, 0, -1, 0 };

        public static bool TryGenerate(int width, int height, int seed,
            out ConnectorCell[] cells, out string error)
        {
            cells = null;
            if (!ValidDimensions(width, height, out error)) return false;
            if (width < 2 || height < 2)
            {
                error = "A bounded grid needs at least two rows and two columns for every room to have two or three sockets.";
                return false;
            }

            Search search = new Search(width, height, seed);
            int[] domains = search.InitialDomains();
            int[] result;
            if (!search.TrySolve(domains, out result))
            {
                error = search.LimitReached
                    ? "The connector search reached its bounded search limit. Try another seed or a smaller grid."
                    : "No connected topology with two or three sockets per room exists for these dimensions.";
                return false;
            }

            ConnectorCell[] generated = new ConnectorCell[width * height];
            for (int i = 0; i < generated.Length; i++)
                generated[i] = new ConnectorCell(i % width, i / width, (ConnectorSockets)Masks[SingleIndex(result[i])]);

            // Keep validation at the API boundary, even if the search is changed later.
            if (!ValidateTopology(width, height, generated, out error)) return false;
            cells = generated;
            return true;
        }

        /// <summary>Validates any ordering of cells without changing the supplied array.</summary>
        public static bool ValidateTopology(int width, int height, ConnectorCell[] cells,
            out string error)
        {
            if (!ValidDimensions(width, height, out error)) return false;
            int count = width * height;
            if (cells == null || cells.Length != count)
            {
                error = "Supply exactly one connector cell for every room coordinate.";
                return false;
            }

            int[] masks = new int[count];
            bool[] seen = new bool[count];
            for (int i = 0; i < cells.Length; i++)
            {
                ConnectorCell cell = cells[i];
                if (!InBounds(cell.X, cell.Y, width, height))
                {
                    error = "A connector cell is outside the bounded grid.";
                    return false;
                }
                int index = cell.Y * width + cell.X;
                if (seen[index])
                {
                    error = "More than one connector cell occupies (" + cell.X + ", " + cell.Y + ").";
                    return false;
                }
                int mask = (int)cell.Sockets;
                int socketCount = GetSocketCount(cell.Sockets);
                if ((mask & ~AllSockets) != 0 || (socketCount != 2 && socketCount != 3))
                {
                    error = "Room (" + cell.X + ", " + cell.Y + ") must have exactly two or three cardinal sockets.";
                    return false;
                }
                if (cell.Kind != Classify(cell.Sockets))
                {
                    error = "Room (" + cell.X + ", " + cell.Y + ") has an archetype that does not match its sockets.";
                    return false;
                }
                masks[index] = mask;
                seen[index] = true;
            }

            for (int i = 0; i < count; i++)
            {
                int x = i % width;
                int y = i / width;
                for (int d = 0; d < Directions.Length; d++)
                {
                    bool open = (masks[i] & Directions[d]) != 0;
                    int nx = x + OffsetX[d];
                    int ny = y + OffsetY[d];
                    if (!InBounds(nx, ny, width, height))
                    {
                        if (!open) continue;
                        error = "Room (" + x + ", " + y + ") has an out-of-bounds " + (ConnectorSockets)Directions[d] + " socket.";
                        return false;
                    }
                    bool reciprocal = (masks[ny * width + nx] & Opposites[d]) != 0;
                    if (open == reciprocal) continue;
                    error = "The sockets between (" + x + ", " + y + ") and (" + nx + ", " + ny + ") do not match.";
                    return false;
                }
            }

            if (!ConnectedMasks(width, height, masks))
            {
                error = "The connector topology contains disconnected groups of rooms.";
                return false;
            }
            error = string.Empty;
            return true;
        }

        public static ConnectorKind Classify(ConnectorSockets sockets)
        {
            int mask = (int)sockets;
            int count = GetSocketCount(sockets);
            if ((mask & ~AllSockets) != 0 || (count != 2 && count != 3))
                throw new ArgumentException("A connector archetype requires two or three cardinal sockets.", "sockets");
            if (count == 3) return ConnectorKind.TJunction;
            return mask == 5 || mask == 10 ? ConnectorKind.Straight : ConnectorKind.Corner;
        }

        public static int GetSocketCount(ConnectorSockets sockets)
        {
            uint mask = unchecked((uint)(int)sockets);
            int count = 0;
            while (mask != 0)
            {
                mask &= mask - 1;
                count++;
            }
            return count;
        }

        public static bool HasSocket(ConnectorSockets sockets, ConnectorSockets socket)
        {
            return socket != ConnectorSockets.None && (sockets & socket) == socket;
        }

        public static ConnectorSockets Opposite(ConnectorSockets socket)
        {
            switch (socket)
            {
                case ConnectorSockets.North: return ConnectorSockets.South;
                case ConnectorSockets.East: return ConnectorSockets.West;
                case ConnectorSockets.South: return ConnectorSockets.North;
                case ConnectorSockets.West: return ConnectorSockets.East;
                default: throw new ArgumentException("Supply one cardinal socket.", "socket");
            }
        }

        private static bool ValidDimensions(int width, int height, out string error)
        {
            if (width <= 0 || height <= 0 || (long)width * height > MaxRoomCount)
            {
                error = "Connector dimensions must be positive and contain no more than " + MaxRoomCount + " rooms.";
                return false;
            }
            error = string.Empty;
            return true;
        }

        private static bool InBounds(int x, int y, int width, int height)
        {
            return x >= 0 && y >= 0 && x < width && y < height;
        }

        private static int SingleIndex(int bits)
        {
            int index = 0;
            while ((bits & 1) == 0) { bits >>= 1; index++; }
            return index;
        }

        private static bool ConnectedMasks(int width, int height, int[] masks)
        {
            bool[] visited = new bool[masks.Length];
            int[] queue = new int[masks.Length];
            int read = 0;
            int write = 1;
            visited[0] = true;
            queue[0] = 0;
            while (read < write)
            {
                int index = queue[read++];
                int x = index % width;
                int y = index / width;
                for (int d = 0; d < Directions.Length; d++)
                {
                    if ((masks[index] & Directions[d]) == 0) continue;
                    int nx = x + OffsetX[d];
                    int ny = y + OffsetY[d];
                    if (!InBounds(nx, ny, width, height)) continue;
                    int other = ny * width + nx;
                    if ((masks[other] & Opposites[d]) == 0 || visited[other]) continue;
                    visited[other] = true;
                    queue[write++] = other;
                }
            }
            return write == masks.Length;
        }

        private sealed class Search
        {
            private readonly int width;
            private readonly int height;
            private readonly int count;
            private readonly int[][] candidateOrder;
            private readonly int[] cellOrder;
            private int nodes;
            public bool LimitReached { get; private set; }

            public Search(int width, int height, int seed)
            {
                this.width = width;
                this.height = height;
                count = width * height;
                candidateOrder = new int[count][];
                cellOrder = new int[count];
                uint state = unchecked((uint)seed) ^ 0x85EBCA6Bu;
                if (state == 0) state = 0xA341316Cu;
                for (int i = 0; i < count; i++)
                {
                    cellOrder[i] = i;
                    candidateOrder[i] = new int[Masks.Length];
                    for (int m = 0; m < Masks.Length; m++) candidateOrder[i][m] = m;
                    Shuffle(candidateOrder[i], ref state);
                }
                Shuffle(cellOrder, ref state);
            }

            public int[] InitialDomains()
            {
                int[] domains = new int[count];
                for (int i = 0; i < count; i++)
                {
                    int allowed = AllSockets;
                    int x = i % width;
                    int y = i / width;
                    if (y == height - 1) allowed &= ~1;
                    if (x == width - 1) allowed &= ~2;
                    if (y == 0) allowed &= ~4;
                    if (x == 0) allowed &= ~8;
                    for (int m = 0; m < Masks.Length; m++)
                        if ((Masks[m] & ~allowed) == 0) domains[i] |= 1 << m;
                }
                return domains;
            }

            public bool TrySolve(int[] domains, out int[] result)
            {
                result = null;
                if (++nodes > MaxSearchNodes)
                {
                    LimitReached = true;
                    return false;
                }
                if (!Propagate(domains) || !PotentiallyConnected(domains)) return false;

                int chosen = -1;
                int smallestDomain = Masks.Length + 1;
                for (int c = 0; c < cellOrder.Length; c++)
                {
                    int index = cellOrder[c];
                    int size = CountBits(domains[index]);
                    if (size <= 1 || size >= smallestDomain) continue;
                    chosen = index;
                    smallestDomain = size;
                }
                if (chosen < 0)
                {
                    result = domains;
                    return true;
                }

                int[] order = candidateOrder[chosen];
                for (int c = 0; c < order.Length; c++)
                {
                    int bit = 1 << order[c];
                    if ((domains[chosen] & bit) == 0) continue;
                    int[] trial = (int[])domains.Clone();
                    trial[chosen] = bit;
                    if (TrySolve(trial, out result)) return true;
                    if (LimitReached) return false;
                }
                return false;
            }

            // Arc consistency propagates both open and closed socket requirements.
            private bool Propagate(int[] domains)
            {
                bool changed;
                do
                {
                    changed = false;
                    for (int i = 0; i < count; i++)
                    {
                        if (domains[i] == 0) return false;
                        int x = i % width;
                        int y = i / width;
                        for (int d = 0; d < Directions.Length; d++)
                        {
                            int nx = x + OffsetX[d];
                            int ny = y + OffsetY[d];
                            if (!InBounds(nx, ny, width, height)) continue;
                            int neighborDomain = domains[ny * width + nx];
                            bool canOpen = false;
                            bool canClose = false;
                            for (int m = 0; m < Masks.Length; m++)
                            {
                                if ((neighborDomain & (1 << m)) == 0) continue;
                                if ((Masks[m] & Opposites[d]) != 0) canOpen = true;
                                else canClose = true;
                            }
                            int before = domains[i];
                            for (int m = 0; m < Masks.Length; m++)
                            {
                                int bit = 1 << m;
                                if ((domains[i] & bit) == 0) continue;
                                bool open = (Masks[m] & Directions[d]) != 0;
                                if ((open && !canOpen) || (!open && !canClose)) domains[i] &= ~bit;
                            }
                            if (domains[i] == 0) return false;
                            if (domains[i] != before) changed = true;
                        }
                    }
                } while (changed);
                return true;
            }

            private bool PotentiallyConnected(int[] domains)
            {
                int[] possibleMasks = new int[count];
                for (int i = 0; i < count; i++)
                    for (int m = 0; m < Masks.Length; m++)
                        if ((domains[i] & (1 << m)) != 0) possibleMasks[i] |= Masks[m];
                return ConnectedMasks(width, height, possibleMasks);
            }

            private static int CountBits(int bits)
            {
                int count = 0;
                while (bits != 0) { bits &= bits - 1; count++; }
                return count;
            }

            private static void Shuffle(int[] values, ref uint state)
            {
                for (int i = values.Length - 1; i > 0; i--)
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    int other = (int)(state % (uint)(i + 1));
                    int held = values[i];
                    values[i] = values[other];
                    values[other] = held;
                }
            }
        }
    }
}
