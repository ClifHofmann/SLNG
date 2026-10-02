namespace SLNG.Core;

/// <summary>The parcel's search category, the Options tab's category combo. Numeric values are the
/// wire values, <c>LLParcel::ECategory</c> (<c>llparcel.h</c>:166). The viewer's combo offers None,
/// Linden, Arts, Business, Educational, Gaming, Hangout, Newcomer, Park, Residential, Shopping, Rental
/// and Other; <see cref="Adult"/> and <see cref="Stage"/> are valid wire values it has no item for.</summary>
public enum ParcelCategory
{
    /// <summary>"Any Category" (<c>C_NONE</c>).</summary>
    None = 0,
    Linden = 1,
    Adult = 2,
    /// <summary>"Arts and Culture".</summary>
    Arts = 3,
    Business = 4,
    Educational = 5,
    Gaming = 6,
    Hangout = 7,
    /// <summary>"Newcomer Friendly".</summary>
    Newcomer = 8,
    /// <summary>"Parks and Nature".</summary>
    Park = 9,
    Residential = 10,
    Shopping = 11,
    Stage = 12,
    Other = 13,
    Rental = 14,
}
