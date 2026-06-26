using System;
using System.Reflection;
using LibreMetaverse;

class ProbeTerrain {
    static void Main() {
        var props = typeof(Simulator).GetProperties();
        foreach (var p in props) {
            if (p.Name.Contains("Terrain") || p.Name.Contains("Elevation") || p.Name.Contains("Detail") || p.Name.Contains("Texture")) {
                Console.WriteLine($"{p.PropertyType.Name} {p.Name}");
            }
        }
    }
}
