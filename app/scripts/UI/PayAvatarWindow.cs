using System;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// MVP5-2: paying another person.
///
/// <para>The same window as <see cref="PayObjectWindow"/> minus the one thing a person does not
/// have: a script. An object can name its own terms with <c>llSetPayPrice</c>, so its window asks
/// first; a person cannot, so there is nothing to wait for and the viewer's own 1/5/10/20 plus a
/// free field is the whole offer (llfloaterpay.cpp's <c>payDirectly</c>, which — unlike
/// <c>payViaObject</c> — sends no <c>RequestPayPrice</c> at all).</para>
///
/// <para>This replaced an inline row in the profile window that had a spinbox and a Send button
/// and nothing else: no balance, no shortfall, no confirmation. That is less care than the client
/// took over a L$ 199 purchase, for a transfer to a person that cannot be taken back.</para>
/// </summary>
public partial class PayAvatarWindow : PayWindowBase
{
    private Guid _agentId;

    protected override string TitleKey => "ui.pay_avatar.title";

    /// <summary>Says who is being paid in words, because a display name alone does not
    /// distinguish "give this person money" from the object case the same layout serves.</summary>
    protected override string? SubtitleKey => "ui.pay_avatar.subtitle";

    public void Initialize(GridSession session, Guid agentId, string agentName)
    {
        _agentId = agentId;
        Build(session, agentName);
    }

    protected override bool Send(int amount) => Session.PayAvatar(_agentId, amount);
}
