namespace SLNG.Core;

/// <summary>
/// One entry of the animation list the simulator signals for an animated-mesh prim -- an
/// <c>AnimationList</c> block of the <c>ObjectAnimation</c> message (message_template.msg:7421-7434).
///
/// <para>It is a request to play, not a playing animation: the object's control avatar turns the
/// union over the root and its child prims into actual motions
/// (<c>LLControlAvatar::updateAnimations</c>, llcontrolavatar.cpp:559-607).</para>
/// </summary>
/// <param name="AnimationId">The animation asset's id.</param>
/// <param name="SequenceId">The sim's sequence number for this start. An animation that is already
/// playing is RESTARTED when the same id arrives with a different sequence id
/// (llvoavatar.cpp:6094-6104); the value is signed on the wire and passed through untouched.</param>
public readonly record struct SignaledAnimation(Guid AnimationId, int SequenceId);
