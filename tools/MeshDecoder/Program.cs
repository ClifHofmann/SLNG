using System;
using System.IO;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Rendering;
using Path = System.IO.Path;

class Program {
    static void Main() {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Godot\app_userdata\SLNG");
        if (!Directory.Exists(dir)) { Console.WriteLine("Dir not found"); return; }
        var files = Directory.GetFiles(dir, "*.mesh");
        foreach(var file in files) {
            byte[] bytes = File.ReadAllBytes(file);
            var asset = new AssetMesh(new UUID(Path.GetFileNameWithoutExtension(file)), bytes);
            var prim = new Primitive { Scale = Vector3.One };
            if (FacetedMesh.TryDecodeFromAsset(prim, asset, DetailLevel.Highest, out var faceted) && faceted != null) {
                Console.WriteLine($"Mesh: {Path.GetFileNameWithoutExtension(file)}");
                if (faceted.SkinData != null && faceted.SkinData.JointNames.Length > 0) {
                    var skin = faceted.SkinData;
                    var bs = skin.BindShapeMatrix;
                    Console.WriteLine($"[RIGGED] JointCount: {skin.JointNames.Length}");
                    Console.WriteLine($"BindShape: {bs[0]:F4}, {bs[1]:F4}, {bs[2]:F4}, {bs[3]:F4}");
                } else {
                    Console.WriteLine("[UNRIGGED]");
                }
                
                System.Numerics.Vector3 min = new System.Numerics.Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                System.Numerics.Vector3 max = new System.Numerics.Vector3(float.MinValue, float.MinValue, float.MinValue);
                foreach (var face in faceted.Faces) {
                    for (int i = 0; i < face.Vertices.Count; i++) {
                        var p = face.Vertices[i].Position;
                        if (p.X < min.X) min.X = p.X; if (p.Y < min.Y) min.Y = p.Y; if (p.Z < min.Z) min.Z = p.Z;
                        if (p.X > max.X) max.X = p.X; if (p.Y > max.Y) max.Y = p.Y; if (p.Z > max.Z) max.Z = p.Z;
                    }
                }
                Console.WriteLine($"Min: {min.X:F4}, {min.Y:F4}, {min.Z:F4}");
                Console.WriteLine($"Max: {max.X:F4}, {max.Y:F4}, {max.Z:F4}");
                Console.WriteLine();
            } else {
                Console.WriteLine($"Mesh: {Path.GetFileNameWithoutExtension(file)} - FAILED TO DECODE");
            }
        }
    }
}
