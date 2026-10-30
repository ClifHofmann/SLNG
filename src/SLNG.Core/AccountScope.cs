namespace SLNG.Core;

/// <summary>
/// BUG-UI-43: the one rule for "this belongs to one login" -- a login is a tenant. A resident exists on one grid under one
/// agent id, so the pair (<see cref="GridIdentity.Slug"/> of the login URI, agent id) names the tenant: the same avatar name
/// on another grid is another tenant, and so is every other avatar on the same grid. Data that is per account in the
/// reference viewer (<c>gSavedPerAccountSettings</c>, <c>settings_per_account.xml</c>) is stored under
/// <c>&lt;prefix&gt;_&lt;grid&gt;_&lt;agentId&gt;</c> in <c>preferences.cfg</c>, never under a bare <c>&lt;prefix&gt;</c>.
///
/// <para>The key is only known once the login has succeeded (the agent id comes with the response), so a store keyed by it is
/// loaded after the login and dropped at logout; before that <see cref="Key"/> has nothing to answer and returns null, and a
/// null key means "do not read, do not write" -- never "fall back to a shared section".</para>
/// </summary>
public static class AccountScope
{
    /// <summary>The tenant key, or null when the grid or the agent is not known yet (or the agent id is the empty UUID).</summary>
    public static string? Key(string? gridSlug, string? agentId)
    {
        if (string.IsNullOrWhiteSpace(gridSlug) || string.IsNullOrWhiteSpace(agentId))
            return null;

        if (Guid.TryParse(agentId, out var guid) && guid == Guid.Empty)
            return null;

        return $"{gridSlug.Trim().ToLowerInvariant()}_{agentId.Trim().ToLowerInvariant()}";
    }

    /// <summary>The config section <c>&lt;prefix&gt;&lt;key&gt;</c> for this tenant (the prefix carries its own trailing
    /// underscore), or null when <see cref="Key"/> is null.</summary>
    public static string? Section(string prefix, string? gridSlug, string? agentId)
    {
        string? key = Key(gridSlug, agentId);
        return key == null ? null : prefix + key;
    }
}
