namespace SLNG.Core.ChatLogs;

/// <summary>
/// The <c>/me</c> convention. The simulator does not transform it: the line travels verbatim and every
/// viewer formats it locally (llchathistory.cpp, <c>appendMessage</c>) -- the name/body delimiter is
/// dropped and exactly three characters are stripped, so <c>/me waves</c> reads "Name waves" and
/// <c>/me's hat</c> reads "Name's hat". Firestorm's log files keep the raw <c>Name: /me waves</c>, so the
/// same rule has to be applied when a log is read back.
/// </summary>
public static class ChatEmote
{
    public static bool IsEmote(string message) =>
        message.StartsWith("/me ", StringComparison.Ordinal) ||
        message.StartsWith("/me'", StringComparison.Ordinal);

    /// <summary>The text after the <c>/me</c> prefix, leading space kept: the separator between name and body.</summary>
    public static string Body(string message) => message[3..];
}
