using FinalFantasy16Library.Files.TERA;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

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
/// The tile grid is a fixed GridDim x GridDim allocation (64x64 in every
/// observed asset), but smaller terrains only populate a centred sub-block and
/// leave the rest with no data. We crop the output to the bounding box of the
/// populated tiles so empty grids are discarded; <see cref="Result.TileColumn"/>
/// / <see cref="Result.TileRow"/> record where that block sits in the full grid.
///
/// Tiles share edges geometrically: a tile's last sample sits at the same world
/// position as the next tile's first sample, so the stitched image overlaps by
/// one sample per tile boundary. The cropped image is therefore
/// (tileCols * TileQuads + 1) x (tileRows * TileQuads + 1) pixels. Adjacent
/// tiles are independent patches whose shared edges are near-identical but not
/// exactly equal; the higher-indexed tile wins on the shared row/column.
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
    }

    /// <summary>
    /// Writes the cropped heightmap PNG to <paramref name="path"/>. Returns a
    /// <see cref="Result"/> describing the image and its placement, or null if
    /// there was nothing to export.
    /// </summary>
    public static Result Export(TeraFile tera, string path)
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

        int dim = TeraFile.HeightMapDim; // 137 samples per tile edge
        int quads = TeraFile.TileQuads;  // 136
        int tileCols = maxCol - minCol + 1;
        int tileRows = maxRow - minRow + 1;
        int width = tileCols * quads + 1;   // shared-edge stitched size
        int height = tileRows * quads + 1;

        using var image = new Image<L16>(width, height, new L16(0));

        image.ProcessPixelRows(accessor =>
        {
            foreach (var tile in tera.Tiles)
            {
                int baseX = (tile.Column - minCol) * quads;
                int baseY = (tile.Row - minRow) * quads;

                for (int z = 0; z < dim; z++)
                {
                    int py = baseY + z;
                    if (py >= height) continue;
                    var rowSpan = accessor.GetRowSpan(py);
                    int srcRow = z * dim;
                    for (int x = 0; x < dim; x++)
                    {
                        int px = baseX + x;
                        if (px >= width) continue;
                        rowSpan[px] = new L16(tile.Heights[srcRow + x]);
                    }
                }
            }
        });

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
        };
    }
}
