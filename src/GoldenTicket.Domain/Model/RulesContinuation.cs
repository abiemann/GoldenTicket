using System.Collections.Immutable;

namespace GoldenTicket.Domain.Model;

/// <summary>
/// The documented way out of one paused supply state (DESIGN 6.4). Each is a house policy: the
/// printed rules do not specify every software boundary, so the decision is disclosed rather than invented
/// silently, applied only when an operator accepts it, and recorded in the journal with its version.
/// </summary>
public sealed record RulesContinuation(
    string Code,
    string PolicyId,
    string Title,
    string Why,
    string Effect);

/// <summary>
/// The reviewed continuation policies for <c>rulesPolicyVersion 1</c>. Accepting one applies it for
/// the rest of the match, so the same position does not stop play again and again.
/// </summary>
public static class RulesContinuations
{
    /// <summary>
    /// Bumping this means a match saved under an older set must not silently adopt new policies.
    /// It is recorded on every acceptance.
    /// </summary>
    public const int PolicyVersion = 1;

    public const string NoSelectableSecondDraw = "NoSelectableSecondDraw";
    public const string PartialMarketSupply = "PartialMarketSupply";
    public const string MarketResetImpossible = "MarketResetImpossible";
    public const string MarketResetUnstable = "MarketResetUnstable";
    public const string NoLegalAction = "NoLegalAction";

    private static readonly ImmutableDictionary<string, RulesContinuation> Policies =
        new[]
        {
            new RulesContinuation(
                NoSelectableSecondDraw,
                "end-turn-with-one-card",
                "End the turn with one card",
                "The rules say to draw two train cards, but the supply cannot produce a second one " +
                "this seat is allowed to take. Nothing already revealed is taken back.",
                "The turn ends with the single card already drawn, and play passes on."),

            new RulesContinuation(
                PartialMarketSupply,
                "play-on-with-smaller-market",
                "Play on with a smaller market",
                "The rules assume five face-up cards, but they cannot require cards that no longer " +
                "exist. Every revealed card stays exactly where it is.",
                "Play continues with however many face-up cards the supply can fill."),

            new RulesContinuation(
                MarketResetImpossible,
                "stop-applying-locomotive-reset",
                "Stop applying the locomotive reset",
                "The three-locomotive rule exists to stop a locomotive-heavy market persisting. When " +
                "the remaining supply cannot form a market with fewer, replacing it changes nothing.",
                "The market is left as it stands and the reset rule is not applied again this match."),

            new RulesContinuation(
                MarketResetUnstable,
                "stop-applying-locomotive-reset",
                "Stop applying the locomotive reset",
                "The bounded run of replacements did not settle below three locomotives. Continuing " +
                "to reshuffle burns through the supply without reaching a legal market.",
                "The market is left as it stands and the reset rule is not applied again this match."),

            new RulesContinuation(
                NoLegalAction,
                "seat-passes-the-turn",
                "This seat passes",
                "The printed rules do not specify a pass for a seat with no legal action. " +
                "This house policy lets that seat pass without inventing cards or making an illegal move.",
                "The seat's turn ends with no action. If every seat passes in a row, the match goes " +
                "to final scoring, because nothing further can happen."),
        }
        .ToImmutableDictionary(policy => policy.Code);

    public static IEnumerable<RulesContinuation> All => Policies.Values;

    /// <summary>The policy for a paused code, or null when the code has no reviewed way forward.</summary>
    public static RulesContinuation? For(string code) =>
        Policies.TryGetValue(code, out var policy) ? policy : null;
}
