using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-INV-15: when a rez uses the item up (RemoveItem), per dropObject
/// (lltooldraganddrop.cpp:1870-1878) and LLPermissions::allowOperationBy (llpermissions.cpp:475-499).</summary>
public class RezRulesTests
{
    private const uint Copy = RezRules.PermCopy;
    private const uint ModifyTransfer = 0x4000 | 0x2000;
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();

    [Fact]
    public void Copy_bit_is_the_wire_value_of_PERM_COPY()
    {
        Assert.Equal(0x8000u, RezRules.PermCopy);
    }

    [Fact]
    public void An_owner_who_may_copy_keeps_the_item()
    {
        bool canCopy = RezRules.AgentCanCopy(_agent, _agent, Guid.Empty, false, Copy | ModifyTransfer, 0);
        Assert.True(canCopy);
        Assert.False(RezRules.ShouldRemoveItem(canCopy));
    }

    [Fact]
    public void An_owner_whose_mask_lacks_copy_uses_the_item_up()
    {
        bool canCopy = RezRules.AgentCanCopy(_agent, _agent, Guid.Empty, false, ModifyTransfer, 0);
        Assert.False(canCopy);
        Assert.True(RezRules.ShouldRemoveItem(canCopy));
    }

    [Fact]
    public void The_owner_is_held_to_the_owner_mask_even_when_everyone_may_copy()
    {
        Assert.False(RezRules.AgentCanCopy(_agent, _agent, Guid.Empty, false, ModifyTransfer, Copy));
    }

    [Fact]
    public void Someone_elses_item_is_held_to_the_everyone_mask()
    {
        // A Library item: owned by the Library owner, copyable only if "everyone" may copy.
        Assert.True(RezRules.AgentCanCopy(_agent, _other, Guid.Empty, false, 0, Copy));
        Assert.False(RezRules.AgentCanCopy(_agent, _other, Guid.Empty, false, Copy, 0));
    }

    [Fact]
    public void A_group_owned_item_is_held_to_the_owner_mask_for_the_group_only()
    {
        var group = Guid.NewGuid();
        Assert.True(RezRules.AgentCanCopy(group, _other, group, true, Copy, 0));
        Assert.False(RezRules.AgentCanCopy(_agent, _agent, group, true, Copy, 0)); // not the group, not "owner"
    }

    [Fact]
    public void No_agent_means_the_system_which_can_always_copy()
    {
        Assert.True(RezRules.AgentCanCopy(Guid.Empty, _other, Guid.Empty, false, 0, 0));
    }
}
