namespace SLNG.App.UI;

/// <summary>One account the Chat logs tab can set a folder for: a saved login profile, or the account
/// logged in right now when it was never saved. <paramref name="Key"/> is
/// <c>ChatLogAccountKey.Of(grid, first, last)</c>; <paramref name="Label"/> reads like the login
/// screen's list ("Clifton Howlett - Second Life").</summary>
public sealed record ChatLogAccount(string Key, string FirstName, string LastName, string GridUri, string Label);
