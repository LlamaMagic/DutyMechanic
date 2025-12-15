using System.Threading.Tasks;
using TreeSharp;

namespace ff14bot.NeoProfiles.Tags;

/// <summary>
/// Abstract starting point for implementing async Task-based OrderBot tags.
/// </summary>
public abstract class AbstractTaskTag : ProfileBehavior
{
    private bool isDone;

    /// <inheritdoc/>
    public override bool IsDone => isDone;

    /// <inheritdoc/>
    protected override Composite CreateBehavior()  // HACK: Must be protected to work with RB, despite CS0507
    {
        return new PrioritySelector(
            new Decorator(
                r => TreeRoot.IsRunning,
                new PrioritySelector(
                    new ActionRunCoroutine(r => RunAsync()),
                    new Action(r => isDone = true))));
    }

    /// <inheritdoc/>
    protected override void OnResetCachedDone()  // HACK: Must be protected to work with RB, despite CS0507
    {
        isDone = false;
    }

    /// <summary>
    /// Executes OrderBot tag's logic.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    protected abstract Task<bool> RunAsync();
}
