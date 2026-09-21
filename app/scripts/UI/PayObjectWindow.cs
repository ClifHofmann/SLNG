using System;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-ECON-02: "Pay this object" — the vendor/tip-jar half of in-world money.
///
/// <para>Paying is not buying, and the two are not interchangeable. A purchase echoes a price the
/// simulator advertised for an object that is for sale; a payment goes to a script, and the script
/// names its own terms with <c>llSetPayPrice</c> — a default amount and up to four fixed buttons.
/// So the window ASKS (<c>RequestPayPrice</c>) and shows what the object charges, which is what
/// the reference viewer does and what "da bekomme ich eine feste Auswahl mit einem vorgegebenen
/// Preis" is.</para>
///
/// <para>Everything that is not about the object's own terms — the amount, the balance, the
/// shortfall, the confirmation above <c>PaymentCheck.ConfirmAboveAmount</c> — lives in
/// <see cref="PayWindowBase"/>, shared with <see cref="PayAvatarWindow"/>.</para>
/// </summary>
public partial class PayObjectWindow : PayWindowBase
{
    private Guid _objectId;
    /// <summary>The object's own region and local id -- the pay-price question is addressed to the
    /// region the object is in, not the one the agent stands in (BUG-NET-19).</summary>
    private ulong _regionHandle;
    private uint _localId;

    protected override string TitleKey => "ui.pay.title";

    public void Initialize(GridSession session, ulong regionHandle, uint localId, Guid objectId, string objectName)
    {
        _regionHandle = regionHandle;
        _localId = localId;
        _objectId = objectId;
        Build(session, objectName);
    }

    /// <summary>What does this object actually charge? Its script may have said so with
    /// <c>llSetPayPrice</c>, and the viewer asks before showing anything else. The answer arrives
    /// off a network thread, hence the deferred hop in the handler.</summary>
    protected override void AfterBuild()
    {
        Session.PayPriceReceived += OnPayPriceReceived;
        Session.RequestPayPrice(_regionHandle, _localId, _objectId);
    }

    protected override void BeforeClose() => Session.PayPriceReceived -= OnPayPriceReceived;

    protected override bool Send(int amount) => Session.PayObject(_objectId, amount, TargetName);

    private void OnPayPriceReceived(object? sender, SLNG.Core.PayPriceEvent e)
    {
        if (e.ObjectId != _objectId) return; // another object's pay info
        CallDeferred(MethodName.ApplyPayPrice, e.DefaultPrice, e.ButtonPrices);
    }

    /// <summary>Applies what the object's script asked for.</summary>
    /// <remarks>
    /// The rules are the viewer's (LLFloaterPay::processPayPriceReply): a default price of HIDE
    /// takes the free field away entirely — the script wants to be paid one of ITS amounts and
    /// nothing else — DEFAULT leaves the field alone, and any other value pre-fills it. A button
    /// price is shown when it is positive and hidden otherwise, so a script offering two amounts
    /// shows two buttons rather than two of its own and two of ours.
    /// </remarks>
    private void ApplyPayPrice(int defaultPrice, int[] buttonPrices)
    {
        if (defaultPrice == SLNG.Core.PayPrice.Hide)
        {
            HideFreeAmount();
        }
        else if (defaultPrice != SLNG.Core.PayPrice.Default && defaultPrice > 0)
        {
            SetAmount(defaultPrice);
        }

        for (int i = 0; i < QuickButtonCount; i++)
        {
            SetQuickAmount(i, i < buttonPrices.Length ? buttonPrices[i] : SLNG.Core.PayPrice.Hide);
        }

        RefreshAffordable();
    }
}
