using System;
using System.Linq;
using Indoctrination.Core;
using Indoctrination.Core.Effects;

namespace Indoctrination.Net
{
    /// <summary>
    /// Turns the authoritative <see cref="GameState"/> into the filtered picture
    /// one player is allowed to see.
    ///
    /// This is the only thing standing between a hidden hand and every client at
    /// the table, so it is deliberately free of Unity types and lives apart from
    /// the networking - which lets RulesCheck prove, outside the Editor, that a
    /// view built for one player never carries another player's cards.
    /// </summary>
    public static class GameViewBuilder
    {
        public static GameView Build(
            GameState game,
            int viewerPlayerId,
            float phaseSecondsRemaining = 0f,
            float choiceSecondsRemaining = 0f,
            bool timersEnabled = false)
        {
            return new GameView
            {
                viewerPlayerId = viewerPlayerId,
                phase = game.Phase.ToString(),
                turnInRound = game.TurnInRound,
                draftNumber = game.DraftNumber,
                deckCount = game.DeckCount,
                discardCount = game.Discard.Count,
                currentDrafterId = game.CurrentDrafterId ?? -1,
                winnerPlayerId = game.Winner?.PlayerId ?? -1,
                isGameOver = game.Phase == TurnPhase.GameOver,
                isDraw = game.IsDraw,
                diceRolled = game.DiceRolled,
                highRollResourceClaimed = game.HighRollResourceClaimed,
                playersReady = game.PlayersReady.ToArray(),
                phaseSecondsRemaining = phaseSecondsRemaining,
                timersEnabled = timersEnabled,
                firstPlayerId = game.Players.Count == 0
                    ? -1
                    : game.Players[game.FirstDrafterIndex % game.Players.Count].PlayerId,
                choiceSecondsRemaining = choiceSecondsRemaining,
                draftZone = game.DraftZone.Select(ToCardView).ToArray(),
                discardPile = game.Discard.Select(ToCardView).ToArray(),
                lastRitualId = game.LastRitualPlayed?.Definition.Id,
                ritualCount = game.RitualsPlayed,
                draftMarks = game.DraftMarks.Select(mark => new DraftMarkView
                {
                    marker = mark.Key.ToString(),
                    cardInstanceId = mark.Value.CardInstanceId,
                    playerId = mark.Value.PlayerId
                }).ToArray(),
                hasPendingChoice = game.PendingChoice != null,
                pendingChoice = ToChoiceView(game.PendingChoice),
                resolvingDescription = game.ResolvingDescription,
                resolvingCardId = game.ResolvingCardId,
                activationBatch = game.ActivationBatch,
                activationCompletedCount = game.ActivationCompletedCount,
                activations = game.ActivationSequence.Select(entry => new ActivationView
                {
                    sequence = entry.Index,
                    cardInstanceId = entry.Source.InstanceId,
                    definitionId = entry.Source.Definition.Id,
                    controllerPlayerId = entry.Controller.PlayerId,
                    dieValue = entry.DieValue,
                    category = entry.Category.ToString(),
                    completed = entry.Completed,
                    skipped = entry.Skipped
                }).ToArray(),
                players = game.Players.Select(player => new PlayerView
                {
                    playerId = player.PlayerId,
                    name = player.Name,
                    health = player.Health,
                    followers = player.Followers,
                    primaryDie = player.PrimaryDie,
                    privateDice = player.PrivateDice.ToArray(),
                    hasRolled = game.HasRolled(player.PlayerId),
                    isAlive = player.IsAlive,
                    red = player.Resources[ResourceColor.Red],
                    green = player.Resources[ResourceColor.Green],
                    blue = player.Resources[ResourceColor.Blue],
                    yellow = player.Resources[ResourceColor.Yellow],
                    handCount = player.Hand.Count,
                    collectedResources = game.HasCollectedResources(player.PlayerId),
                    resourceAllowance = game.ResourcesPerTurnFor(player.PlayerId),
                    block = player.Block,
                    isReady = game.PlayersReady.Contains(player.PlayerId),
                    hasResigned = game.HasResigned(player.PlayerId),
                    offeringDraw = game.HasOfferedDraw(player.PlayerId),

                    // The whole point of building a view per player: only the
                    // holder is ever sent the contents of their own hand.
                    // Only the holder is sent a hand, and only they are told what
                    // each card would cost them - the discount depends on what is
                    // in their own compound, so it is theirs to know.
                    hand = player.PlayerId == viewerPlayerId
                        ? player.Hand.Select(card => ToPricedCardView(game, player, card)).ToArray()
                        : Array.Empty<CardView>(),

                    compound = player.Compound.Select(ToCardView).ToArray()
                }).ToArray()
            };
        }

        /// <summary>
        /// A card in its holder's hand, priced for them: what it costs after
        /// their discounts, whether that is less than the printed cost, and
        /// whether they can pay it.
        /// </summary>
        private static CardView ToPricedCardView(GameState game, PlayerState player, CardInstance card)
        {
            var view = ToCardView(card);
            var cost = game.CostFor(player, card);

            view.costForYou = cost.ToString();
            view.isDiscounted = !cost.IsSpecial && cost.Total < card.Cost.Total;
            view.canAfford = player.Resources.CanAfford(cost);
            return view;
        }

        private static CardView ToCardView(CardInstance card)
        {
            return new CardView
            {
                instanceId = card.InstanceId,
                definitionId = card.Definition.Id,
                counters = card.CounterNames.Select(name => new CounterView
                {
                    name = name,
                    count = card.GetCounter(name)
                }).ToArray()
            };
        }

        /// <summary>
        /// Mirrors a <see cref="ChoiceRequest"/> into something JsonUtility can send.
        /// Every viewer gets the same copy - the asker's identity is already public,
        /// and legal-option lists (e.g. which opponents can be hit) are not secret.
        /// </summary>
        private static ChoiceView ToChoiceView(ChoiceRequest request)
        {
            if (request == null)
            {
                return null;
            }

            return new ChoiceView
            {
                kind = request.Kind.ToString(),
                prompt = request.Prompt,
                askedOfPlayerId = request.AskedOfPlayerId,
                playerOptions = request.PlayerOptions.ToArray(),
                cardOptions = request.CardOptions.ToArray(),
                colorOptions = request.ColorOptions.Select(c => (int)c).ToArray(),
                options = request.Options.ToArray(),
                minAmount = request.MinAmount,
                maxAmount = request.MaxAmount
            };
        }
    }
}
