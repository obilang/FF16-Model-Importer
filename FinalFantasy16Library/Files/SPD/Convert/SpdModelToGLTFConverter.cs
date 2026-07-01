using System.Numerics;

using IONET;
using IONET.Core;
using IONET.Core.Model;

namespace FinalFantasy16Library.Files.SPD.Convert;

/// <summary>
/// Exports decoded SPD8 (SpeedTree) geometry to a mesh format via IONET.
/// Supported output: .dae, .obj, .gltf, .glb
/// </summary>
public class SpdModelToGLTFConverter
{
    /// <summary>
    /// Exports a single LOD of the given SPD file to <paramref name="path"/>.
    /// </summary>
    public static void Convert(SpdFile spd, string path, int lod = 0)
    {
        if (lod < 0 || lod >= spd.Lods.Count)
            throw new ArgumentOutOfRangeException(nameof(lod), $"LOD {lod} not present (file has {spd.Lods.Count}).");

        IOModel iomodel = new IOModel();
        IOMaterial material = new IOMaterial() { Name = "speedtree", Label = "speedtree" };

        var lodModel = spd.Lods[lod];
        for (int d = 0; d < lodModel.Draws.Count; d++)
        {
            var draw = lodModel.Draws[d];
            if (draw.Positions.Count == 0)
                continue;

            // Some assets (notably trees) use per-draw vertex formats this parser
            // does not yet decode; that produces wildly out-of-range coordinates.
            // Reject decodes that fall far outside the draw's bounding box, or that
            // are absurd in absolute terms (a SpeedTree asset is at most a few tens
            // of metres), rather than emitting garbage geometry.
            if (draw.BoundingMin != draw.BoundingMax && !PositionsWithinBounds(draw))
            {
                Console.WriteLine($"  Draw {d} positions fall outside its bounding box (unsupported vertex format) - skipping.");
                continue;
            }
            if (!PositionsAreSane(draw))
            {
                Console.WriteLine($"  Draw {d} positions are out of range (unsupported vertex format) - skipping.");
                continue;
            }

            Console.WriteLine($"[{d + 1}/{lodModel.Draws.Count}] Loading Draw {d} LOD {lod} ({draw.Positions.Count} verts, {draw.Indices.Count / 3} tris)");

            IOMesh iomesh = new IOMesh() { Name = $"LOD{lod}_Draw{d}" };

            for (int v = 0; v < draw.Positions.Count; v++)
            {
                IOVertex iovertex = new IOVertex { Position = draw.Positions[v] };
                if (v < draw.TexCoords.Count)
                    iovertex.SetUV(draw.TexCoords[v].X, draw.TexCoords[v].Y, 0);
                iomesh.Vertices.Add(iovertex);
            }

            IOPolygon poly = new IOPolygon { MaterialName = material.Name };
            int vcount = draw.Positions.Count;
            // Emit only whole triangles whose indices are in range (variant
            // buffers can include trailing/degenerate data).
            for (int t = 0; t + 2 < draw.Indices.Count; t += 3)
            {
                int a = draw.Indices[t], b = draw.Indices[t + 1], c = draw.Indices[t + 2];
                if (a >= vcount || b >= vcount || c >= vcount)
                    continue;
                poly.Indicies.Add(a);
                poly.Indicies.Add(b);
                poly.Indicies.Add(c);
            }

            if (poly.Indicies.Count == 0)
            {
                Console.WriteLine($"  Draw {d} has no in-range triangles - skipping.");
                continue;
            }

            iomesh.Polygons.Add(poly);
            iomodel.Meshes.Add(iomesh);
        }

        if (iomodel.Meshes.Count == 0)
            throw new InvalidDataException($"LOD {lod} produced no meshes.");

        Console.WriteLine("Exporting Scene");

        IOScene scene = new IOScene();
        scene.Models.Add(iomodel);
        scene.Materials.Add(material);

        IOManager.ExportScene(scene, path, new ExportSettings()
        {
            Optimize = false,
        });
    }

    /// <summary>
    /// Returns true when the decoded positions sit reasonably inside the draw's
    /// declared bounding box (allowing generous slack). Used to reject draws whose
    /// vertex format this parser cannot yet decode.
    /// </summary>
    private static bool PositionsWithinBounds(SpdFile.SpdDraw draw)
    {
        var min = draw.BoundingMin;
        var max = draw.BoundingMax;
        var size = max - min;
        float slack = Math.Max(MathF.Max(size.X, MathF.Max(size.Y, size.Z)), 1f) * 4f;
        var lo = min - new System.Numerics.Vector3(slack);
        var hi = max + new System.Numerics.Vector3(slack);

        int outside = 0, checks = 0;
        int step = Math.Max(1, draw.Positions.Count / 256);
        for (int i = 0; i < draw.Positions.Count; i += step)
        {
            var p = draw.Positions[i];
            checks++;
            if (p.X < lo.X || p.X > hi.X || p.Y < lo.Y || p.Y > hi.Y || p.Z < lo.Z || p.Z > hi.Z)
                outside++;
        }
        return checks == 0 || outside <= checks * 0.05;
    }

    /// <summary>
    /// Rejects draws whose decoded coordinates are absurd in absolute terms. A
    /// SpeedTree asset spans at most a few tens of metres, so anything beyond a
    /// generous cap indicates an unsupported vertex format.
    /// </summary>
    private static bool PositionsAreSane(SpdFile.SpdDraw draw)
    {
        const float cap = 1000f;
        int outside = 0, checks = 0;
        int step = Math.Max(1, draw.Positions.Count / 256);
        for (int i = 0; i < draw.Positions.Count; i += step)
        {
            var p = draw.Positions[i];
            checks++;
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) ||
                MathF.Abs(p.X) > cap || MathF.Abs(p.Y) > cap || MathF.Abs(p.Z) > cap)
                outside++;
        }
        return checks == 0 || outside <= checks * 0.05;
    }
}
