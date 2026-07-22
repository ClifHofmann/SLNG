using System;
using System.Collections.Generic;

class Program {
    static void Main() {
        var overrides = new Dictionary<string, System.Numerics.Vector3>();
        overrides["mFootLeft"] = new System.Numerics.Vector3(0.1f, 0.2f, 0.3f);
        
        var toAdd = new Dictionary<string, System.Numerics.Vector3>();
        foreach (var kvp in overrides)
        {
            string name = kvp.Key;
            var pos = kvp.Value;
            
            if (name.EndsWith("Left"))
            {
                string rightName = name.Substring(0, name.Length - 4) + "Right";
                if (!overrides.ContainsKey(rightName))
                {
                    toAdd[rightName] = new System.Numerics.Vector3(pos.X, -pos.Y, pos.Z);
                }
            }
            else if (name.EndsWith("Right"))
            {
                string leftName = name.Substring(0, name.Length - 5) + "Left";
                if (!overrides.ContainsKey(leftName))
                {
                    toAdd[leftName] = new System.Numerics.Vector3(pos.X, -pos.Y, pos.Z);
                }
            }
        }
        
        foreach (var kvp in toAdd)
        {
            overrides[kvp.Key] = kvp.Value;
        }

        Console.WriteLine($"mFootLeft: {overrides["mFootLeft"]}");
        Console.WriteLine($"mFootRight: {overrides["mFootRight"]}");
    }
}
