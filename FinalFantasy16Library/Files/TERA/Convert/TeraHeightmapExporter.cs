using FinalFantasy16Library.Files.TERA;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FinalFantasy16Library.Files.TERA.Convert;

/// <summary>
/// Exports a decoded <see cref="TeraFile"/> as a single stitched 16-bit
/// grayscale heightmap PNG, cropped to the populated tile region.
///
/// The .tera file stores only height samples (see <see cref="TeraFile"/>), so a
/// heightmap raster is the natural, lossless representation. Pixel values are
/// the raw u16 samples straight from the file; recover world height with
/// worldY = pixel * <see cref="TeraFile.HeightScale"/> + tera.HeightBias.
///
/// Each tile stores a 137x137 grid, but neighbouring tiles overlap by 9 samples:
/// tile[<see cref="TeraFile.TileStride"/>] equals the next tile's [0] exactly
/// (verified across every tile pair, both axes). So only the first
/// <see cref="TeraFile.TileStride"/> (128) rows/columns of each tile are unique;
/// the trailing skirt duplicates the neighbour and is dropped when stitching.
/// The far (last) tile on each axis contributes its full extent so the outermost
/// edge is not lost. This yields exactly TileStride-spaced samples with no seam
/// and a clean 0.5 m spacing.
///
/// The tile grid is a fixed GridDim x GridDim allocation (64x64 in every
/// observed asset), but smaller terrains only populate a centred sub-block. We
/// crop the output to the bounding box of the populated tiles;
/// <see cref="Result.TileColumn"/> / <see cref="Result.TileRow"/> record where
/// that block sits in the full grid.
/// </summary>
public class TeraHeightmapExporter
{
    public class Result
    {
        /// <summary>Output image width in pixels.</summary>
        public int Width;
        /// <summary>Output image height in pixels.</summary>
        public int Height;
        /// <summary>Left tile column of the populated block within the full grid.</summary>
        public int TileColumn;
        /// <summary>Top tile row of the populated block within the full grid.</summary>
        public int TileRow;
        /// <summary>Populated block width in tiles.</summary>
        public int TileColumns;
        /// <summary>Populated block height in tiles.</summary>
        public int TileRows;
        /// <summary>True when the image was baked for Unreal (vertically flipped).</summary>
        public bool UnrealFriendly;
    }

    /// <summary>
    /// Writes the cropped heightmap PNG to <paramref name="path"/>. Returns a
    /// <see cref="Result"/> describing the image and its placement, or null if
    /// there was nothing to export.
    ///
    /// When <paramref name="unrealFriendly"/> is set, the image is flipped
    /// vertically so it imports onto an Unreal Landscape with a positive X scale
    /// and no yaw. FFXVI terrain, placed the natural way, is mirrored along one
    /// axis relative to Unreal's row->Y / col->X convention; a mirror needs
    /// either a negative scale (which inverts Landscape normals/collision) or a
    /// 180 yaw. Baking one flip into the pixels removes both, so the Landscape
    /// transform becomes plain positive scale, zero rotation.
    /// </summary>
    public static Result Export(TeraFile tera, string path, bool unrealFriendly = false)
    {
        if (tera.Tiles.Count == 0)
            return null;

        // Bounding box of populated tiles within the fixed grid.
        int minCol = int.MaxValue, minRow = int.MaxValue, maxCol = int.MinValue, maxRow = int.MinValue;
        foreach (var tile in tera.Tiles)
        {
            if (tile.Column < minCol) minCol = tile.Column;
            if (tile.Column > maxCol) maxCol = tile.Column;
            if (tile.Row < minRow) minRow = tile.Row;
            if (tile.Row > maxRow) maxRow = tile.Row;
        }

        int dim = TeraFile.HeightMapDim; // 137 samples stored per tile edge
        int stride = TeraFile.TileStride; // 128 unique samples per tile edge
        int tileCols = maxCol - minCol + 1;
        int tileRows = maxRow - minRow + 1;
        // Unique samples across the block, plus the far tile's overlap skirt so
        // the outermost edge is preserved. (dim - stride == 9)
        int width = tileCols * stride + (dim - stride);
        int height = tileRows * stride + (dim - stride);

        using var image = new Image<L16>(width, height, new L16(0));

        image.ProcessPixelRows(accessor =>
        {
            foreach (var tile in tera.Tiles)
            {
                int baseX = (tile.Column - minCol) * stride;
                int baseY = (tile.Row - minRow) * stride;

                // Only the last tile on each axis writes its overlap skirt; every
                // other tile writes just its unique [0..stride) samples, which the
                // next tile's [0] column/row then continues seamlessly.
                int cols = tile.Column == maxCol ? dim : stride;
                int rows = tile.Row == maxRow ? dim : stride;

                for (int z = 0; z < rows; z++)
                {
                    int py = baseY + z;
                    if (py >= height) continue;
                    var rowSpan = accessor.GetRowSpan(py);
                    int srcRow = z * dim;
                    for (int x = 0; x < cols; x++)
                    {
                        int px = baseX + x;
                        if (px >= width) continue;
                        rowSpan[px] = new L16(tile.Heights[srcRow + x]);
                    }
                }
            }
        });

        // Bake the axis mirror into the pixels so Unreal needs no negative scale
        // or yaw. A vertical flip (reverse row order) maps FFXVI's tile layout to
        // Unreal's row->Y / col->X convention.
        if (unrealFriendly)
            image.Mutate(x => x.Flip(FlipMode.Vertical));

        // 16-bit grayscale PNG preserves the raw u16 values exactly.
        var encoder = new PngEncoder
        {
            ColorType = PngColorType.Grayscale,
            BitDepth = PngBitDepth.Bit16,
        };
        image.SaveAsPng(path, encoder);

        return new Result
        {
            Width = width,
            Height = height,
            TileColumn = minCol,
            TileRow = minRow,
            TileColumns = tileCols,
            TileRows = tileRows,
            UnrealFriendly = unrealFriendly,
        };
    }
}
