using FinalFantasy16Library.Files.ANMB;
using FinalFantasy16Library.Files.MDL;
using FinalFantasy16Library.Files.MDL.Convert;
using FinalFantasy16Library.Files.MTL;
using FinalFantasy16Library.Files.PAC;
using FinalFantasy16Library.Files.PZDF;
using FinalFantasy16Library.Files.SKL;
using FinalFantasy16Library.Files.SPD;
using FinalFantasy16Library.Files.SPD.Convert;
using FinalFantasy16Library.Files.TERA;
using FinalFantasy16Library.Files.TERA.Convert;
using FinalFantasy16Library.Files.TEX;

// TERA terrain exports as a 16-bit heightmap image; see TeraHeightmapExporter.

using Newtonsoft.Json;

using SixLabors.ImageSharp;

namespace MdlConverter;

public class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("FF16-Model-Importer by KillzXGaming, Nenkai, Maybri, CybersoulXIII");
            Console.WriteLine("Usage:");
            Console.WriteLine("----------------------------------------");
            Console.WriteLine("- Extract model to .gltf (pac is needed for skeleton):");
            Console.WriteLine("     MdlConverter.exe <model_file_path> <pac_file_path> [skeleton_name]");
            Console.WriteLine("     Optional skeleton_name: Specify a specific .skl file name from the PAC (e.g., body_base.skl)");
            Console.WriteLine("- Import model to .mdl (Requires all LODs in the body folder to be named body_LODx with x as a number):");
            Console.WriteLine("     MdlConverter.exe <model_folder>");
            Console.WriteLine("- Import GLTF (animation) to ANMB (Havok):");
            Console.WriteLine("     MdlConverter.exe animation.glb body_base.skl");
            Console.WriteLine("- Export ANMB (Havok) to GLTF(animation):");
            Console.WriteLine("     MdlConverter.exe animation.anmb body_base.skl");
            Console.WriteLine("----------------------------------------");
            Console.WriteLine("- Export SpeedTree (.spd8) geometry to .glb (one folder, one file per LOD):");
            Console.WriteLine("     MdlConverter.exe tree.spd8");
            Console.WriteLine("----------------------------------------");
            Console.WriteLine("- Export terrain (.tera) to a 16-bit grayscale heightmap .png:");
            Console.WriteLine("     MdlConverter.exe e_e00200.tera [-unreal]");
            Console.WriteLine("       Pixel values are the raw u16 heights.");
            Console.WriteLine("       Decode world height: worldY = pixel * 0.02 + bias");
            Console.WriteLine("       (the bias is printed on export).");
            Console.WriteLine("       -unreal  flip vertically so it imports onto an Unreal");
            Console.WriteLine("                Landscape with positive X scale and no yaw.");
            Console.WriteLine("----------------------------------------");
            Console.WriteLine("- Export MTL to JSON:");
            Console.WriteLine("     MdlConverter.exe material.mtl");
            Console.WriteLine("- Import JSON to MTL (will overwrite if exists):");
            Console.WriteLine("     MdlConverter.exe material.mtl.json ");
            Console.WriteLine("----------------------------------------");
            Console.WriteLine("- Export PZD to XML:");
            Console.WriteLine("     MdlConverter.exe text.pzd");
            Console.WriteLine("- Import XML to PZD (will overwrite if exists):");
            Console.WriteLine("     MdlConverter.exe text.pzd.xml");
        }

        foreach (string arg in args)
        {
            // Option flags (consumed by the handlers that need them).
            if (arg.StartsWith("-"))
                continue;

            if (arg.EndsWith(".tex"))
                HandleTexToImageConversion(arg);
            else if (arg.EndsWith(".tex.png"))
                HandleImageToTexConversion(arg);
            else if(Directory.Exists(arg)) //folder to compile back as
                HandleModelFolderToModelConversion(arg);
            else if(arg.EndsWith(".mdl"))
                ExportModelToGLTF(args, arg);
            else if(arg.EndsWith(".spd8"))
                ExportSpeedTreeToGLTF(arg);
            else if(arg.EndsWith(".tera"))
                ExportTerrainToHeightmap(args, arg);
            else if(arg.EndsWith(".mtl"))
                ConvertMtlToMaterialJson(arg);
            else if(arg.EndsWith(".mtl.json"))
                ConvertJsonMaterialToMtl(arg);
            else if(arg.EndsWith(".pzd"))
            {
                PzdFile pzdFile = new PzdFile(File.OpenRead(arg));
                File.WriteAllText(arg + ".xml", pzdFile.ToXml());
            }
            else if(arg.EndsWith(".pzd.xml"))
            {
                string name = Path.GetFileName(arg).Replace(".pzd.xml", "");
                string dir = Path.GetDirectoryName(arg);

                PzdFile pzdFile = new PzdFile();
                pzdFile.FromXML(File.ReadAllText(arg));
                pzdFile.Save(arg.Replace(".xml", ""));
            }
            else if(Directory.Exists(arg))
            {
                foreach (var f in Directory.GetFiles(arg))
                {
                    if (!f.EndsWith(".pzd.xml"))
                        continue;

                    PzdFile pzdFile = new PzdFile(File.OpenRead(f));
                    string name = Path.GetFileName(f);
                    pzdFile.Save(f.Replace(".xml", ""));
                }
            }
            else if(arg.EndsWith(".glb") || arg.EndsWith(".gltf"))
            {
                ImportAnimFromGLTF(args);
            }
            else if(arg.EndsWith(".anmb"))
            {
                ExportAnimToGLTF(args);
            }
            else
            {
                Console.WriteLine($"Unrecognized input/file/folder: {arg}");
            }
        }
    }

    private static void ConvertJsonMaterialToMtl(string arg)
    {
        JsonSerializerSettings settings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new TextureConstantConverter() },
            Formatting = Formatting.Indented
        };

        MtlFile mtlFile = JsonConvert.DeserializeObject<MtlFile>(File.ReadAllText(arg), settings);
        mtlFile.Save(arg.Replace(".json", ""));
    }

    private static void ConvertMtlToMaterialJson(string arg)
    {
        JsonSerializerSettings settings = new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new TextureConstantConverter() },
            Formatting = Formatting.Indented
        };

        MtlFile mtlFile = new MtlFile(File.OpenRead(arg), arg);
        File.WriteAllText(arg + ".json", JsonConvert.SerializeObject(mtlFile, settings));
    }

    private static void ExportModelToGLTF(string[] args, string arg)
    {
        string fullPath = Path.GetFullPath(arg);
        string dir = Path.GetDirectoryName(fullPath);
        string modelFileName = Path.GetFileNameWithoutExtension(arg);

        List<SklFile> skeletons = [];

        var pacFile = args.FirstOrDefault(x => x.EndsWith(".pac"));
        if (!string.IsNullOrEmpty(pacFile))
        {
            //Get skeleton from given pac argument
            PacFile pac = new PacFile(File.OpenRead(pacFile));

            // Check if a specific skeleton name was provided (3rd argument)
            var skelNameArg = args.Skip(2).FirstOrDefault(x => !x.EndsWith(".mdl") && !x.EndsWith(".pac"));
            
            if (!string.IsNullOrEmpty(skelNameArg))
            {
                // Use only the specified skeleton
                var specificSkelFile = pac.Files.FirstOrDefault(x => 
                    x.FileName.EndsWith(".skl") && 
                    (x.FileName.Equals(skelNameArg, StringComparison.OrdinalIgnoreCase) || 
                     x.FileName.EndsWith($"/{skelNameArg}", StringComparison.OrdinalIgnoreCase) ||
                     x.FileName.EndsWith($"\\{skelNameArg}", StringComparison.OrdinalIgnoreCase)));
                
                if (specificSkelFile != null)
                {
                    Console.WriteLine($"Using specified skeleton: {specificSkelFile.FileName}");
                    SklFile skel = SklFile.Open(specificSkelFile.Data);
                    skeletons.Add(skel);
                }
                else
                {
                    Console.WriteLine($"WARNING: Specified skeleton '{skelNameArg}' not found in PAC file.");
                    Console.WriteLine("Available skeletons:");
                    foreach (var sklFile in pac.Files.Where(x => x.FileName.EndsWith(".skl")))
                    {
                        Console.WriteLine($"  - {sklFile.FileName}");
                    }
                }
            }
            else
            {
                //Multiple skeletons
                foreach (var file in pac.Files.Where(x => x.FileName.EndsWith(".skl")).OrderByDescending(g => g.FileName.Contains("body.skl")))
                {
                    SklFile skel = SklFile.Open(file.Data);
                    skeletons.Add(skel);
                }
            }
        }

        string outDir = Path.Combine(dir, modelFileName);
        if (!Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);

        MdlFile mdlFile = new MdlFile(File.OpenRead(arg));

        for (int i = 0; i < mdlFile.LODModels.Count; i++)
            FaithModelToGLTFConverter.Convert(mdlFile, skeletons, Path.Combine(outDir, $"{modelFileName}_LOD{i}.glb"), i);
    }

    private static void ExportSpeedTreeToGLTF(string arg)
    {
        string fullPath = Path.GetFullPath(arg);
        string dir = Path.GetDirectoryName(fullPath);
        string modelFileName = Path.GetFileNameWithoutExtension(arg);

        SpdFile spd;
        try
        {
            spd = new SpdFile(File.OpenRead(arg));
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"'{modelFileName}': {ex.Message}");
            return;
        }

        if (spd.Lods.Count == 0)
        {
            Console.WriteLine($"'{modelFileName}' contains no extractable geometry (VFX/config-only asset).");
            return;
        }

        string outDir = Path.Combine(dir, modelFileName);
        if (!Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);

        Console.WriteLine($"SpeedTree '{modelFileName}': {spd.Lods.Count} LOD(s)");
        for (int i = 0; i < spd.Lods.Count; i++)
        {
            try
            {
                SpdModelToGLTFConverter.Convert(spd, Path.Combine(outDir, $"{modelFileName}_LOD{i}.glb"), i);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN: Failed to export LOD{i}: {ex.Message}");
            }
        }
    }

    private static void ExportTerrainToHeightmap(string[] args, string arg)
    {
        string fullPath = Path.GetFullPath(arg);
        string dir = Path.GetDirectoryName(fullPath);
        string modelFileName = Path.GetFileNameWithoutExtension(arg);

        // -unreal: bake the axis mirror into the pixels so the Unreal Landscape
        // needs no negative scale and no yaw.
        bool unreal = args.Any(x => x.Equals("-unreal", StringComparison.OrdinalIgnoreCase));

        TeraFile tera;
        try
        {
            tera = new TeraFile(File.OpenRead(arg));
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"'{modelFileName}': {ex.Message}");
            return;
        }

        if (tera.Tiles.Count == 0)
        {
            Console.WriteLine($"'{modelFileName}' contains no extractable terrain tiles.");
            return;
        }

        Console.WriteLine($"Terrain '{modelFileName}': {tera.GridDim}x{tera.GridDim} tiles ({tera.Tiles.Count} decoded)");

        string outPath = Path.Combine(dir, $"{modelFileName}.png");
        try
        {
            var result = TeraHeightmapExporter.Export(tera, outPath, unreal);
            Console.WriteLine($"Heightmap: {result.Width}x{result.Height} 16-bit grayscale" +
                              (unreal ? " (Unreal-friendly: vertically flipped)" : ""));
            Console.WriteLine($"Populated region: {result.TileColumns}x{result.TileRows} tiles " +
                              $"at grid col {result.TileColumn}, row {result.TileRow} " +
                              $"(of {tera.GridDim}x{tera.GridDim})");
            Console.WriteLine($"Height decode: worldY = pixel * {TeraFile.HeightScale} + {tera.HeightBias}");

            PrintUnrealTransform(tera, result, unreal);

            Console.WriteLine($"File saved as '{outPath}'");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARN: Failed to export terrain heightmap: {ex.Message}");
        }
    }

    /// <summary>
    /// Prints the Unreal Landscape transform that reproduces FFXVI world scale.
    /// ScaleZ and LocationZ are exact; ScaleXY is exact (0.5 m/sample -> 50 UU).
    /// The XY *location* is the terrain's placement in the level (from the map
    /// binary), so only its scale/rotation/Z are emitted as absolutes.
    /// </summary>
    private static void PrintUnrealTransform(TeraFile tera, TeraHeightmapExporter.Result result, bool unreal)
    {
        // Unreal: WorldZ = (pixel - 32768) * (ScaleZ / 128) + LocationZ.
        // FFXVI:  WorldZ_UU = pixel * (HeightScale * 100) + HeightBias * 100.
        // Match slope: ScaleZ = HeightScale * 100 * 128 = 256. Then:
        //   LocationZ = HeightBias * 100 + 32768 * (ScaleZ / 128)
        const float scaleZ = TeraFile.HeightScale * 100f * 128f;      // 256
        const float uuPerMeter = 100f;
        float locationZ = tera.HeightBias * uuPerMeter + 32768f * (scaleZ / 128f);
        float scaleXY = TeraFile.SampleSpacing * uuPerMeter;          // 0.5 m -> 50 UU

        Console.WriteLine("Unreal Landscape transform (import PNG, fill data):");
        if (unreal)
        {
            Console.WriteLine($"  RelativeScale3D  = (X={scaleXY:0.######}, Y={scaleXY:0.######}, Z={scaleZ:0.######})");
            Console.WriteLine($"  RelativeRotation = (Pitch=0, Yaw=0, Roll=0)");
        }
        else
        {
            Console.WriteLine($"  RelativeScale3D  = (X=-{scaleXY:0.######}, Y={scaleXY:0.######}, Z={scaleZ:0.######})");
            Console.WriteLine($"  RelativeRotation = (Pitch=0, Yaw=180, Roll=0)");
            Console.WriteLine($"  (or re-export with -unreal for positive X scale and no yaw)");
        }
        Console.WriteLine($"  RelativeLocation = (X=<place>, Y=<place>, Z={locationZ:0.######})");
        Console.WriteLine($"  Location XY is the terrain's placement in the level (map binary);");
        Console.WriteLine($"  ScaleXY {scaleXY:0.###}, ScaleZ {scaleZ:0.###} and Z {locationZ:0.###} are exact.");
    }

    private static void HandleModelFolderToModelConversion(string arg)
    {
        Console.WriteLine("Input Type: Model Folder");

        string name = Path.GetFileNameWithoutExtension(arg);
        string fullPath = Path.GetFullPath(arg);
        string parent = Path.GetDirectoryName(fullPath);
        string baseModel = Path.Combine(parent, $"{name}.mdl");
        if (!File.Exists(baseModel))
        {
            Console.WriteLine($"ERROR: Base model '{baseModel}' missing.");
            Console.WriteLine("Converting a folder with models requires a base model to use.");
            return;
        }

        Console.WriteLine($"Loading base model: '{baseModel}'");
        MdlFile mdlFile = new MdlFile(File.OpenRead(baseModel));

        GLTFToFaithModelConverter.ClearMeshes(mdlFile);

        var converter = new GLTFToFaithModelConverter();
        for (int i = 0; i < 8; i++)
        {
            string filePathGLTF = Path.Combine(fullPath, $"{name}_LOD{i}.gltf");
            string filePathDAE = Path.Combine(fullPath, $"{name}_LOD{i}.dae");
            string filePathGLB = Path.Combine(fullPath, $"{name}_LOD{i}.glb");
            string filePathOBJ = Path.Combine(fullPath, $"{name}_LOD{i}.obj");

            string inputPath = "";
            if (File.Exists(filePathGLTF))
            {
                inputPath = filePathGLTF;
                Console.WriteLine($"Importing LOD{i} (GLTF)");
            }
            else if (File.Exists(filePathGLB))
            {
                inputPath = filePathGLB;
                Console.WriteLine($"Importing LOD{i} (DAE)");
            }
            else if (File.Exists(filePathDAE))
            {
                inputPath = filePathDAE;
                Console.WriteLine($"Importing LOD{i} (GLB)");
            }
            else if (File.Exists(filePathOBJ))
            {
                inputPath = filePathOBJ;
                Console.WriteLine($"Importing LOD{i} (OBJ)");
            }

            if (!string.IsNullOrEmpty(inputPath))
            {
                //Import LOD level
                converter.AddLOD(mdlFile, inputPath, clearExistingMeshes: false);

                // Prepare generated joint info for extra bones not found in base MDL file
                mdlFile.SetGeneratedJoints(converter.GeneratedJoints);
            }
            else
            {
                if (i == 0)
                    Console.WriteLine($"ERROR: No main lod! Attempted to load LOD{i} with name {name}_LOD{i} but no suitable file was found (gltf/dae/glb/obj)");
                else
                    Console.WriteLine($"Attempted to load LOD{i} with name {name}_LOD{i} but no suitable file was found (gltf/dae/glb/obj) - skipping.");
            }

        }

        string outputModelFile = $"{fullPath}NEW.mdl";
        Console.WriteLine("Saving model file...");
        mdlFile.Save(outputModelFile);

        Console.WriteLine($"File saved as '{outputModelFile}'");
    }

    private static void HandleImageToTexConversion(string arg)
    {
        TexFile texFile = new TexFile(File.OpenRead(arg.Replace(".png", "")));
        texFile.Textures[0].Replace(arg);
        texFile.Save(arg.Replace(".png", ""));
    }

    private static void HandleTexToImageConversion(string arg)
    {
        TexFile texFile = new TexFile(File.OpenRead(arg));
        foreach (var tex in texFile.Textures)
        {
            tex.Export(arg + ".png");
        }
    }

    private static void ImportAnimFromGLTF(string[] args) 
    { 
        if (args[0].EndsWith(".glb") || args[0].EndsWith(".gltf")) 
        {
            if (args[1].EndsWith(".skl"))
            {
                SklFile skel = SklFile.Open(File.OpenRead(args[1]));
                var importer = new AnimationUtils();
                if (args.Length > 2)
                {
                    importer.Import(skel, args[0], float.Parse(args[2]));
                }
                else
                {
                    importer.Import(skel, args[0]);
                }
            }
            else
            {
                Console.WriteLine($"ERROR: Havok skeleton file missing.");
            }
        }
    }

    private static void ExportAnimToGLTF(string[] args) 
    { 
        if (args[0].EndsWith(".anmb")) 
        {
            if (args[1].EndsWith(".skl"))
            {
                SklFile skel = SklFile.Open(File.OpenRead(args[1]));
                var exporter = new AnimationUtils();
                exporter.Export(skel, args[0]);
            }
            else
            {
                Console.WriteLine($"ERROR: Havok skeleton file missing.");
            }
        }
    }
}
