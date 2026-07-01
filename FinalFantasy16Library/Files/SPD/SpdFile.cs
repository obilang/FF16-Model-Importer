using System.Numerics;

namespace FinalFantasy16Library.Files.SPD;

/// <summary>
/// Parses FFXVI "SPD8" SpeedTree containers (env/speedtree/**/*.spd8).
///
/// These are the cooked runtime form of a SpeedTree asset (the editable .spm
/// project is not shipped). The geometry is stored uncompressed and can be
/// extracted directly.
///
/// Layout (all offsets little-endian, relative to file start):
///
///   0x00  "SPD8" magic
///   0x08  u32  string-table start (texture / atlas base names)
///   0x0C  u32  string-table end
///
/// Several tables follow the header. Their absolute positions vary per asset
/// (SpeedTree writes a variable config block first), so they are located by
/// signature rather than by a fixed offset:
///
///   * Vertex-region table - one 32-byte entry per LOD:
///         +0x00 u32 vertexBlockStart   (absolute)
///         +0x04 u32 vertexBlockEnd     (absolute, == start of this LOD's indices)
///         +0x0C u32 vertexBlockSize    (== end - start)
///         +0x10 u32 indexByteCount     (u16 indices immediately follow the verts)
///     Terminated by an all-zero start/end entry.
///
///   * Draw table - one 0x6C-byte record per draw call:
///         +0x00 float[6] bounding box (minXYZ, maxXYZ)
///         +0x1C u32   vertexCount
///         +0x20 u32   indexCount   (always a multiple of 3, triangle list)
///         +0x58 u32   stream0 stride
///         +0x5C u32   stream0 byteOffset within its LOD vertex block
///         +0x60 u32   stream1 stride
///         +0x64 u32   stream1 byteOffset
///
/// Per LOD the vertex block holds two interleaved 16-byte streams stored as
/// separate sub-blocks (stream0 then stream1), followed by the index block:
///     [ stream0 verts ][ stream1 verts ][ indices ]
/// Indices are draw-relative (each draw's indices restart at 0) and are rebased
/// by the running vertex count when draws are merged into one LOD mesh.
///
/// Vertex stream0 (16-byte stride):
///     +0x00  half[3] position   (LOCAL to the card, see below)
///     +0x06  half    w  (always 1.0)
///     +0x0C  half[2] texcoord0
/// Vertex stream1 (16-byte stride): normal + tangent frame (not needed here).
///
/// IMPORTANT - instanced grass. Large grass fields store geometry as many
/// identical "cards" (5 verts each) all authored at the origin; stream0's
/// position is therefore LOCAL. The per-card world center lives in a separate
/// buffer (one half[4] per card; xyz used). The card-center buffers are listed,
/// one slice per LOD, in a directory of 12-byte rows {offset, byteSize, packed}.
/// Final position = cardCenter[vertexIndex / vertsPerCard] + localOffset.
///
/// Small standalone assets are not instanced: their stream0 position is already
/// in world space and there is no card-center buffer. Both cases are handled.
/// </summary>
public class SpdFile
{
    public const int DrawRecordSize = 0x6C;
    public const int VertexRegionEntrySize = 0x20;
    private const int StreamStride = 16;
    private const int PositionOffset = 0;     // half[3] within stream0 (instanced grass, local)
    private const int BboxPositionOffset = 4; // u16[3] within stream0 (non-instanced, bbox-normalized)
    private const int TexCoordOffset = 12;    // half[2] within stream0
    private const int CardCenterStride = 8; // half[4] per card

    /// <summary>Texture / atlas base names embedded in the header string table.</summary>
    public List<string> TextureNames = [];

    /// <summary>One entry per level of detail, highest detail first.</summary>
    public List<SpdLodModel> Lods = [];

    public SpdFile(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Read(ms.ToArray());
    }

    public SpdFile(byte[] data) => Read(data);

    public class SpdLodModel
    {
        public List<SpdDraw> Draws = [];
        public int VertexCount => Draws.Sum(d => d.Positions.Count);
        public int TriangleCount => Draws.Sum(d => d.Indices.Count) / 3;
    }

    public class SpdDraw
    {
        public Vector3 BoundingMin;
        public Vector3 BoundingMax;
        public List<Vector3> Positions = [];
        public List<Vector2> TexCoords = [];
        public List<ushort> Indices = [];
    }

    private byte[] _data;
    private float Half(long o) => (float)BitConverter.ToHalf(_data, (int)o);
    private ushort U16(long o) => BitConverter.ToUInt16(_data, (int)o);
    private uint U32(long o) => BitConverter.ToUInt32(_data, (int)o);
    private float F32(long o) => BitConverter.ToSingle(_data, (int)o);

    private void Read(byte[] data)
    {
        _data = data;
        if (data.Length < 0x10 || data[0] != (byte)'S' || data[1] != (byte)'P' ||
            data[2] != (byte)'D' || data[3] != (byte)'8')
            throw new InvalidDataException("Not an SPD8 (FFXVI SpeedTree) file.");

        ReadTextureNames(data);

        int vregTable = FindVertexRegionTable(data);
        int drawTable = FindDrawTable(data);
        if (vregTable < 0 || drawTable < 0)
            throw new InvalidDataException(
                "SPD8 contains no extractable geometry (likely a VFX/config-only asset).");

        var regions = ReadVertexRegions(data, vregTable);
        var draws = ReadDrawHeaders(data, drawTable);

        // Per-LOD card-center buffers (instanced grass). May be absent.
        var centerDir = FindCardCenterDirectory(data, drawTable, regions, draws);

        int drawIndex = 0;
        int lodIndex = 0;
        foreach (var (vStart, vEnd, _) in regions)
        {
            long regionVerts = (vEnd - vStart) / 32;
            var lod = new SpdLodModel();

            long indexCursor = vEnd; // indices follow the verts in this region
            long vertsConsumed = 0;
            long cardCursor = 0;
            long centerBuf = centerDir != null && lodIndex < centerDir.Count ? centerDir[lodIndex] : -1;

            while (drawIndex < draws.Count && vertsConsumed < regionVerts)
            {
                var hdr = draws[drawIndex++];
                int vpc = DeriveVertsPerCard(hdr, vStart, vertsConsumed, indexCursor);
                var draw = DecodeDraw(hdr, vStart, vertsConsumed, indexCursor, centerBuf, cardCursor, vpc);
                lod.Draws.Add(draw);

                vertsConsumed += hdr.VertexCount;
                indexCursor += (long)hdr.IndexCount * 2;
                cardCursor += vpc > 0 ? hdr.VertexCount / vpc : 0;
            }

            if (lod.Draws.Count > 0)
                Lods.Add(lod);
            lodIndex++;
        }
    }

    private struct DrawHeader
    {
        public Vector3 BoundingMin;
        public Vector3 BoundingMax;
        public uint VertexCount;
        public uint IndexCount;
        public uint Stream0Stride;
        public uint Stream0Offset;
        public uint Stream1Stride;
        public uint Stream1Offset;
    }

    private SpdDraw DecodeDraw(DrawHeader hdr, long vRegionStart, long vertCursor,
        long indexStart, long centerBuf, long cardCursor, int vertsPerCard)
    {
        var draw = new SpdDraw { BoundingMin = hdr.BoundingMin, BoundingMax = hdr.BoundingMax };

        // Stream0Offset is already absolute within the LOD vertex region (it accounts
        // for prior draws' streams), so it must NOT be further shifted by vertCursor.
        long s0 = vRegionStart + hdr.Stream0Offset;
        long stride = hdr.Stream0Stride > 0 ? hdr.Stream0Stride : StreamStride;
        bool instanced = centerBuf >= 0 && vertsPerCard > 0;

        // Two position encodings are seen:
        //  * Instanced grass: half[3] LOCAL offset at stream0+0, plus a per-card
        //    world centre (added below). The draw bbox is unused (often zero).
        //  * Non-instanced meshes (trees): u16[3] normalised across the per-draw
        //    bbox, located at stream0+4. Detected by a real (non-zero) bbox.
        Vector3 size = hdr.BoundingMax - hdr.BoundingMin;
        bool bboxNormalized = !instanced && size.LengthSquared() > 0f;
        int posOff = bboxNormalized ? BboxPositionOffset : PositionOffset;
        bool hasUv = stride >= TexCoordOffset + 4;

        for (uint v = 0; v < hdr.VertexCount; v++)
        {
            long po = s0 + (long)v * stride;
            if (po + posOff + 6 > _data.Length) break;

            Vector3 pos;
            if (bboxNormalized)
            {
                pos = new Vector3(
                    hdr.BoundingMin.X + U16(po + posOff + 0) / 65535f * size.X,
                    hdr.BoundingMin.Y + U16(po + posOff + 2) / 65535f * size.Y,
                    hdr.BoundingMin.Z + U16(po + posOff + 4) / 65535f * size.Z);
            }
            else
            {
                float lx = Half(po + posOff + 0);
                float ly = Half(po + posOff + 2);
                float lz = Half(po + posOff + 4);
                if (!float.IsFinite(lx)) lx = 0f;
                if (!float.IsFinite(ly)) ly = 0f;
                if (!float.IsFinite(lz)) lz = 0f;
                pos = new Vector3(lx, ly, lz);

                if (instanced)
                {
                    long card = cardCursor + v / (uint)vertsPerCard;
                    long co = centerBuf + card * CardCenterStride;
                    if (co + 6 <= _data.Length)
                    {
                        float cx = Half(co + 0), cy = Half(co + 2), cz = Half(co + 4);
                        if (float.IsFinite(cx) && float.IsFinite(cy) && float.IsFinite(cz))
                            pos += new Vector3(cx, cy, cz);
                    }
                }
            }
            draw.Positions.Add(pos);

            float u = 0f, t = 0f;
            if (hasUv && po + TexCoordOffset + 4 <= _data.Length)
            {
                u = Half(po + TexCoordOffset + 0);
                t = Half(po + TexCoordOffset + 2);
            }
            if (!float.IsFinite(u)) u = 0f;
            if (!float.IsFinite(t)) t = 0f;
            draw.TexCoords.Add(new Vector2(u, t));
        }

        for (uint i = 0; i < hdr.IndexCount; i++)
        {
            long ip = indexStart + (long)i * 2;
            if (ip + 2 > _data.Length) break;
            draw.Indices.Add(U16(ip));
        }

        return draw;
    }

    /// <summary>
    /// Cards are equal-sized runs of vertices. Detect the run length by finding
    /// the smallest N for which vertex N is connected (shares no index-adjacency)
    /// to a new component. In practice this is the size of the first connected
    /// component in the index buffer; returns 0 when the model is not instanced.
    /// </summary>
    private int DeriveVertsPerCard(DrawHeader hdr, long vRegionStart, long vertCursor, long indexStart)
    {
        // Only instanced models need this; detect by whether stream0 card runs are
        // authored at the origin (all card centroids near zero).
        long s0 = vRegionStart + hdr.Stream0Offset;

        // Find the first connected component size from the (draw-local) index buffer.
        int n = (int)Math.Min(hdr.IndexCount, 4096);
        var adj = new Dictionary<int, HashSet<int>>();
        void Link(int a, int b)
        {
            if (!adj.TryGetValue(a, out var s)) adj[a] = s = new HashSet<int>();
            s.Add(b);
        }
        for (int i = 0; i + 2 < n; i += 3)
        {
            long ip = indexStart + (long)i * 2;
            if (ip + 6 > _data.Length) break;
            int a = U16(ip), b = U16(ip + 2), c = U16(ip + 4);
            Link(a, b); Link(b, a); Link(b, c); Link(c, b); Link(c, a); Link(a, c);
        }
        if (adj.Count == 0) return 0;

        // Component containing vertex 0.
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            int u = stack.Pop();
            if (!seen.Add(u)) continue;
            if (adj.TryGetValue(u, out var s))
                foreach (var w in s) if (!seen.Contains(w)) stack.Push(w);
        }
        int compSize = seen.Count;

        // Decide instancing: a card run is small (a handful of verts) and the run's
        // local centroid sits at the origin. Sample the first run.
        if (compSize == 0 || compSize > 64) return 0;

        long stride = hdr.Stream0Stride > 0 ? hdr.Stream0Stride : StreamStride;
        double cx = 0, cz = 0;
        int sample = Math.Min(compSize, (int)hdr.VertexCount);
        for (int v = 0; v < sample; v++)
        {
            long po = s0 + (long)v * stride;
            if (po + 6 > _data.Length) return 0;
            cx += Half(po + 0);
            cz += Half(po + 4);
        }
        cx /= sample; cz /= sample;
        bool atOrigin = Math.Abs(cx) < 0.25 && Math.Abs(cz) < 0.25;
        return atOrigin ? compSize : 0;
    }

    private void ReadTextureNames(byte[] data)
    {
        uint strStart = BitConverter.ToUInt32(data, 0x08);
        uint strEnd = BitConverter.ToUInt32(data, 0x0C);
        if (strStart >= strEnd || strEnd > data.Length) return;

        int p = (int)strStart;
        while (p < strEnd)
        {
            int s = p;
            while (p < strEnd && data[p] != 0) p++;
            if (p > s)
            {
                string str = System.Text.Encoding.ASCII.GetString(data, s, p - s);
                if (str.Length > 1 && str.All(c => c >= 0x20 && c < 0x7F))
                    TextureNames.Add(str);
            }
            p++;
        }
    }

    private static List<(long Start, long End, uint IndexBytes)> ReadVertexRegions(byte[] data, int table)
    {
        var regions = new List<(long, long, uint)>();
        for (int o = table; o + VertexRegionEntrySize <= data.Length; o += VertexRegionEntrySize)
        {
            uint start = BitConverter.ToUInt32(data, o);
            uint end = BitConverter.ToUInt32(data, o + 4);
            uint idxBytes = BitConverter.ToUInt32(data, o + 0x10);
            if (start == 0 && end == 0) break;
            if (start >= end || end > data.Length) break;
            regions.Add((start, end, idxBytes));
        }
        return regions;
    }

    private List<DrawHeader> ReadDrawHeaders(byte[] data, int table)
    {
        var draws = new List<DrawHeader>();
        for (int o = table; o + DrawRecordSize <= data.Length; o += DrawRecordSize)
        {
            if (!IsDrawRecord(data, o)) break;
            draws.Add(new DrawHeader
            {
                BoundingMin = new Vector3(F32(o + 0x00), F32(o + 0x04), F32(o + 0x08)),
                BoundingMax = new Vector3(F32(o + 0x0C), F32(o + 0x10), F32(o + 0x14)),
                VertexCount = U32(o + 0x1C),
                IndexCount = U32(o + 0x20),
                Stream0Stride = U32(o + 0x58),
                Stream0Offset = U32(o + 0x5C),
                Stream1Stride = U32(o + 0x60),
                Stream1Offset = U32(o + 0x64),
            });
        }
        return draws;
    }

    #region Table discovery

    private static int FindVertexRegionTable(byte[] data)
    {
        int limit = Math.Min(0x4000, data.Length - VertexRegionEntrySize);
        for (int o = 0x60; o < limit; o += 4)
        {
            uint start = BitConverter.ToUInt32(data, o);
            uint end = BitConverter.ToUInt32(data, o + 4);
            uint size = BitConverter.ToUInt32(data, o + 0x0C);
            if (start <= 0x100 || end <= start || end > data.Length) continue;
            if (end - start != size || size <= 0x40) continue;
            uint next = BitConverter.ToUInt32(data, o + VertexRegionEntrySize);
            if (next == 0 || (next > start && next <= data.Length))
                return o;
        }
        return -1;
    }

    private static int FindDrawTable(byte[] data)
    {
        int limit = Math.Min(0x6000, data.Length - DrawRecordSize);
        for (int o = 0x60; o < limit; o += 4)
            if (IsDrawRecord(data, o) && IsDrawRecord(data, o + DrawRecordSize))
                return o;
        return -1;
    }

    private static bool IsDrawRecord(byte[] data, int o)
    {
        if (o < 0 || o + DrawRecordSize > data.Length) return false;
        Span<float> bb = stackalloc float[6];
        for (int i = 0; i < 6; i++)
        {
            float f = BitConverter.ToSingle(data, o + i * 4);
            if (!float.IsFinite(f) || Math.Abs(f) > 1e5f) return false;
            bb[i] = f;
        }
        if (bb[0] > bb[3] || bb[1] > bb[4] || bb[2] > bb[5]) return false;
        uint vcount = BitConverter.ToUInt32(data, o + 0x1C);
        uint icount = BitConverter.ToUInt32(data, o + 0x20);
        if (vcount == 0 || vcount >= 3_000_000) return false;
        if (icount == 0 || icount >= 8_000_000 || icount % 3 != 0) return false;

        // Stream0 stride must be a small, sane vertex size. A false match on an
        // unrelated table tends to have a zero or absurd stride here.
        uint stride0 = BitConverter.ToUInt32(data, o + 0x58);
        if (stride0 < 4 || stride0 > 64) return false;

        return true;
    }

    /// <summary>
    /// Locates the per-LOD card-center directory: a run of 12-byte rows
    /// {u32 offset, u32 byteSize, u32 packed} whose offsets point into the upper
    /// portion of the file and whose referenced data are half-float centers that
    /// span the model bounding box. Returns one buffer offset per LOD, or null if
    /// the asset is not instanced.
    /// </summary>
    private List<long> FindCardCenterDirectory(byte[] data, int drawTable,
        List<(long Start, long End, uint IndexBytes)> regions, List<DrawHeader> draws)
    {
        if (draws.Count == 0) return null;

        // The directory's first referenced buffer must contain centers spanning a
        // sizeable spatial range (the field extent). Scan 12-byte rows after the
        // draw table for the first row whose buffer qualifies.
        int half = data.Length / 2;
        for (int o = drawTable; o + 12 <= data.Length - 12; o += 4)
        {
            long off = BitConverter.ToUInt32(data, o);
            long sz = BitConverter.ToUInt32(data, o + 4);
            if (off <= half || off >= data.Length || sz <= 0 || off + sz > data.Length)
                continue;
            if (!BufferLooksLikeCenters(off, sz)) continue;

            // Collect consecutive rows (one per LOD) from here.
            var list = new List<long>();
            int p = o;
            while (list.Count < regions.Count && p + 12 <= data.Length)
            {
                long roff = BitConverter.ToUInt32(data, p);
                long rsz = BitConverter.ToUInt32(data, p + 4);
                if (roff == 0 || roff >= data.Length || roff + rsz > data.Length) break;
                list.Add(roff);
                p += 12;
            }
            return list.Count > 0 ? list : null;
        }
        return null;
    }

    private bool BufferLooksLikeCenters(long off, long sz)
    {
        int count = (int)Math.Min(sz / CardCenterStride, 512);
        if (count < 4) return false;
        float minx = float.MaxValue, maxx = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            long o = off + (long)i * CardCenterStride;
            if (o + 2 > _data.Length) break;
            float x = Half(o);
            if (!float.IsFinite(x)) return false;
            minx = Math.Min(minx, x); maxx = Math.Max(maxx, x);
        }
        // Centers of an instanced field span a real distance; local-only data does not.
        return (maxx - minx) > 0.5f;
    }

    #endregion
}
