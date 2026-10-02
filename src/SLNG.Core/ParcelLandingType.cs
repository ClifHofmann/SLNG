namespace SLNG.Core;

/// <summary>"Teleport Routing" on the Options tab: how a teleport onto the parcel is handled. Numeric
/// values are the wire values, <c>LLParcel::ELandingType</c> (<c>llparcel.h</c>:201); the labels are the
/// combo items of <c>floater_about_land.xml</c>.</summary>
public enum ParcelLandingType
{
    /// <summary>"Blocked" (<c>L_NONE</c>): teleports onto the parcel are refused.</summary>
    Blocked = 0,

    /// <summary>"Landing Point" (<c>L_LANDING_POINT</c>): arrivals are sent to
    /// <see cref="ParcelInfo.LandingPoint"/>.</summary>
    LandingPoint = 1,

    /// <summary>"Anywhere" (<c>L_DIRECT</c>): arrivals land where they asked.</summary>
    Anywhere = 2,
}
