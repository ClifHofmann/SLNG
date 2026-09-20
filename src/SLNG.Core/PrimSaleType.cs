namespace SLNG.Core;

/// <summary>FEAT-ECON-02: what a for-sale object actually sells.</summary>
/// <remarks>
/// The distinction is the whole of the purchase: buying the ORIGINAL hands over the object that
/// is standing there, a COPY leaves it and delivers a duplicate, and CONTENTS sells what is
/// inside it and leaves the object alone. A buyer who is not told which of the three they are
/// about to do cannot consent to it, which is why the confirmation names it.
///
/// The values are the wire's own (ObjectBuy's SaleType byte), so they can be cast across the
/// LibreMetaverse boundary without a lookup table going stale.
/// </remarks>
public enum PrimSaleType : byte
{
    NotForSale = 0,
    Original = 1,
    Copy = 2,
    Contents = 3,
}
