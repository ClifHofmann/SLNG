namespace SLNG.Core;

/// <summary>SL's basic prim shapes offered when rezzing a new object -- mirrors
/// LibreMetaverse's PrimType at the SLNG.Net boundary without leaking that type into app/.</summary>
public enum BasicPrimType
{
    Box, Cylinder, Prism, Sphere, Torus, Tube, Ring
}
