using System.Collections.Generic;
using SLNG.Core.ChatLogs;
using SLNG.Core.Services;

namespace SLNG.App.UI;

/// <summary>What the "Import old SLNG logs" button needs from the running session: the logger that
/// owns the per-file locks (so an import never interleaves with live chat), where the logs go now,
/// and the folders the older builds wrote into. Null while nobody is logged in -- the account the
/// history belongs to is not known then.</summary>
public sealed record ChatLogImportContext(
    ChatLogger Logger,
    string Directory,
    ChatLogNaming Naming,
    IReadOnlyList<string> Sources);
