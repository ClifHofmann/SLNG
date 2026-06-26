using System;
using System.Reflection;
using LibreMetaverse;

class ProbeTerrain {
    static void Main() {
        var props = typeof(GridClient).GetProperties();
        foreach (var p in props) {
            Console.WriteLine($"GridClient Property: {p.PropertyType.Name} {p.Name}");
        }
        var fields = typeof(GridClient).GetFields();
        foreach (var p in fields) {
            Console.WriteLine($"GridClient Field: {p.FieldType.Name} {p.Name}");
        }
    }
}
