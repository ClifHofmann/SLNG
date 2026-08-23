using System;
using System.Linq;
using LibreMetaverse;
class Program
{
    static void Main()
    {
        var methods = typeof(InventoryManager).GetMethods().Where(m => m.Name.Contains("Update") || m.Name.Contains("Rename")).Select(m => m.Name + ": " + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)));
        foreach (var m in methods) Console.WriteLine(m);
    }
}
