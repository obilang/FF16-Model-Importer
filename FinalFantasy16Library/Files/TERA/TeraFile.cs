using System.IO.Compression;

namespace FinalFantasy16Library.Files.TERA;

/// <summary>
/// Parses FFXVI ".tera" terrain files (env/terrain/**/*.tera).
///
/// The public 010-editor template (FF16_tera_Terrain.bt) is explicitly
/// unfinished and does not describe the geometry; the layout below was
/// reverse-engineered from the shipped assets.
///
/// IMPORTANT: the file stores a *heightmap raster*, not mesh vertices. Each tile
/// holds only a grid of 16-bit height samples; the X/Z of every sample is
/// implicit (its grid index times a fixed spacing, plus the tile's position in
/// the terrain grid). Only height is stored, so the natural export is a 16-bit
/// heightmap image.
///
/// Layout (little-endian, offsets relative to file start):
///
///   0x000  "TERA" magic
///   0x008  TextureBuffer array (splat/normal/etc). The heightmap is NOT one of
///          these - terrain height lives in the per-tile buffers described below.
///   0x394  float  heightBias  (world Y = rawHeight * 0.02 + heightBias)
///   0x37C  uint   tileTableOffset (points at the tile record table; 0x1578 in
///          every observed asset)
///   0x454  short  tileGridDim (N) - the terrain is an N x N grid of tiles
///          (N == 64 in every observed asset -> 4096 tiles), stored row-major
///          (index = row * N + col; col advances +X, row advances +Z).
///
/// Tile record (stride 0x118, one per tile):
///   +0x00  uint   dataOffset   (absolute offset to a "ZLIB" container)
///   +0x04  uint   compressedBlockSize
///   +0x08  ...    inline low-resolution data + tail (not needed for the raster)
///
/// "ZLIB" container:
///   +0x00  "ZLIB" magic
///   +0x04  uint   headerSize (0x1C)
///   +0x08  uint   decompressedSize
///   +0x0C  uint   (== 3)
///   +0x10  uint   chunkCount
///   +0x14  uint[chunkCount] chunkCompressedSizes
///   +headerSize   the concatenated zlib streams (RFC 1950, one per 64 KiB of
///          output). NOTE: this is *standard zlib*, not the GDeflate used by the
///          PAC/TEX/MDL buffers.
///
/// Decompressed tile buffer (~92344 bytes):
///   +0x0000  ushort[137*137]  height grid   (HeightMapDim x HeightMapDim)
///   +0x92A2  ushort[129*129]  secondary grid (splat/normal weights; often 0)
///   +0x138B8 ushort[64*64]    coverage mask  (usually 0xFFFF)
///   trailing zero padding
///
/// Height decode: worldY = rawHeight * <see cref="HeightScale"/> + HeightBias.
/// </summary>
public class TeraFile
{
    /// <summary>Samples stored per tile edge in the height grid (137).</summary>
    public const int HeightMapDim = 137;
    /// <summary>
    /// Unique samples per tile edge before the overlap skirt. Neighbouring tiles
    /// overlap by (HeightMapDim - TileStride) samples: tile[TileStride] on one
    /// axis equals neighbour[0] exactly, verified across every tile pair. So the
    /// last 9 rows/columns (indices 128..136) duplicate the next tile's leading
    /// samples and must be dropped when stitching (except at the far edge).
    /// </summary>
    public const int TileStride = 128;
    /// <summary>World units spanned by one tile stride on X/Z (128 samples).</summary>
    public const float TileWorldSpan = 64.0f;
    /// <summary>World units between adjacent samples (64 / 128 = 0.5 m).</summary>
    public const float SampleSpacing = TileWorldSpan / TileStride;
    /// <summary>Raw height unit -> world units.</summary>
    public const float HeightScale = 0.02f;

    private const int TileRecordStride = 0x118;
    private const int TileTableOffsetPos = 0x37C;
    private const int GridDimPos = 0x454;
    private const int HeightBiasPos = 0x394;
    private const int HeightGridBytes = HeightMapDim * HeightMapDim * 2;

    /// <summary>Number of tiles along each axis of the terrain grid.</summary>
    public int GridDim;

    /// <summary>Constant added to scaled raw heights to obtain world Y.</summary>
    public float HeightBias;

    /// <summary>Decoded tiles, row-major (index = row * GridDim + col).</summary>
    public List<TeraTile> Tiles = [];

    public class TeraTile
    {
        /// <summary>Column (X) of this tile within the terrain grid.</summary>
        public int Column;
        /// <summary>Row (Z) of this tile within the terrain grid.</summary>
        public int Row;
        /// <summary>Raw HeightMapDim x HeightMapDim u16 samples, row-major.</summary>
        public ushort[] Heights = [];
        /// <summary>True when every height sample is identical (flat patch).</summary>
        public bool IsFlat;
    }

    public TeraFile(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Read(ms.ToArray());
    }

    public TeraFile(byte[] data) => Read(data);

    private byte[] _data = null!;
    private uint U32(long o) => BitConverter.ToUInt32(_data, (int)o);
    private short I16(long o) => BitConverter.ToInt16(_data, (int)o);
    private float F32(long o) => BitConverter.ToSingle(_data, (int)o);

    private void Read(byte[] data)
    {
        _data = data;
        if (data.Length < 0x460 || data[0] != (byte)'T' || data[1] != (byte)'E' ||
            data[2] != (byte)'R' || data[3] != (byte)'A')
            throw new InvalidDataException("Not a TERA (FFXVI terrain) file.");

        GridDim = I16(GridDimPos);
        HeightBias = F32(HeightBiasPos);
        if (GridDim <= 0 || GridDim > 256)
            throw new InvalidDataException($"Unexpected terrain grid dimension {GridDim}.");

        long tileTable = U32(TileTableOffsetPos);
        int tileCount = GridDim * GridDim;

        for (int i = 0; i < tileCount; i++)
        {
            long rec = tileTable + (long)i * TileRecordStride;
            if (rec + 8 > data.Length)
                break;

            uint dataOffset = U32(rec);
            if (dataOffset == 0 || dataOffset + 4 > data.Length)
                continue;
            // Must be a ZLIB container.
            if (data[dataOffset] != (byte)'Z' || data[dataOffset + 1] != (byte)'L' ||
                data[dataOffset + 2] != (byte)'I' || data[dataOffset + 3] != (byte)'B')
                continue;

            byte[] tileBuf = DecompressZlibContainer(dataOffset);
            if (tileBuf.Length < HeightGridBytes)
                continue;

            int col = i % GridDim;
            int row = i / GridDim;
            Tiles.Add(DecodeTile(tileBuf, col, row));
        }
    }

    private static TeraTile DecodeTile(byte[] buf, int col, int row)
    {
        var heights = new ushort[HeightMapDim * HeightMapDim];
        ushort first = BitConverter.ToUInt16(buf, 0);
        bool flat = true;
        for (int i = 0; i < heights.Length; i++)
        {
            ushort raw = BitConverter.ToUInt16(buf, i * 2);
            heights[i] = raw;
            if (raw != first) flat = false;
        }
        return new TeraTile { Column = col, Row = row, Heights = heights, IsFlat = flat };
    }

    /// <summary>
    /// Decompresses a "ZLIB" container (standard zlib streams, chunked per
    /// 64 KiB of output) at the given absolute offset.
    /// </summary>
    private byte[] DecompressZlibContainer(long p)
    {
        uint headerSize = U32(p + 4);
        uint decompSize = U32(p + 8);
        uint chunkCount = U32(p + 0x10);

        byte[] outBuf = new byte[decompSize];
        long dataPos = p + headerSize;
        int outPos = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            uint chunkSize = U32(p + 0x14 + i * 4);
            using var src = new MemoryStream(_data, (int)dataPos, (int)chunkSize);
            using var zs = new ZLibStream(src, CompressionMode.Decompress);
            int remaining = (int)decompSize - outPos;
            int chunkDecomp = Math.Min(65536, remaining);
            int got = 0;
            while (got < chunkDecomp)
            {
                int n = zs.Read(outBuf, outPos + got, chunkDecomp - got);
                if (n == 0) break;
                got += n;
            }
            outPos += got;
            dataPos += chunkSize;
        }
        return outBuf;
    }
}
