namespace AdamE.MauiRouter.Planning;

/// <summary>
/// Controls how a contextual stack mutation applies the target route when the current stack is eligible.
/// </summary>
public enum ContextualStackPushBehavior
{
    AppendTail,
    ReplaceWithCanonicalStack
}
