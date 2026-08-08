// Plays complete games headlessly with random legal moves, checking after every
// step that the board is still in a state the rules can describe.
//
// A card game's bugs mostly live in the interactions nobody thought to write a
// test for - a card that asks a question with no legal answer, an effect that
// loses a card, a phase that cannot advance. Random play across thousands of
// games reaches those combinations far faster than hand-written cases can.
//
//     ./Tools/RulesCheck/run.sh --fuzz 2000
//
using System;
using System.Collections.Generic;
using System.Linq;
using Indoctrination.Core;
using Indoctrination.Core.Effects;

namespace Indoctrination.Tools
{
    public class FuzzFailure : Exception
    {
        public FuzzFailure(string message) : base(message) { }
    }

    public static class Fuzz
    {
        /// <summary>
        /// A single game should never need anything like this many actions. If
        /// one does, something is looping rather than progressing.
        /// </summary>
        private const int MaxStepsPerGame = 20000;

        public static bool Run(List<CardDefinition> cards, int games, int startingSeed)
        {
            var totalCopies = cards.Sum(c => c.Count);
            var failures = new List<string>();
            var completed = 0;
            var longest = 0;

            // How games actually end, which is worth knowing on its own: a win
            // condition that never fires in fifty thousand games is a design
            // problem the rules checks would never notice.
            var byFollowers = 0;
            var byElimination = 0;
            var draws = 0;
            var totalTurns = 0;

            for (var i = 0; i < games; i++)
            {
                var seed = startingSeed + i;
                var players = 2 + seed % 3;   // 2, 3, and 4-player tables

                try
                {
                    var outcome = PlayOneGame(cards, players, seed, totalCopies);
                    longest = Math.Max(longest, outcome.Steps);
                    totalTurns += outcome.Drafts;
                    completed++;

                    if (outcome.Draw)
                    {
                        draws++;
                    }
                    else if (outcome.WonByFollowers)
                    {
                        byFollowers++;
                    }
                    else
                    {
                        byElimination++;
                    }
                }
                catch (Exception e)
                {
                    // Report the seed - a failing game can be replayed exactly.
                    failures.Add($"seed {seed} ({players}p): {Describe(e)}");
                }
            }

            Console.WriteLine($"\nFuzz ({games} games, seeds {startingSeed}-{startingSeed + games - 1}):");
            Console.WriteLine($"  {completed}/{games} played to a finish, longest {longest} actions");

            if (completed > 0)
            {
                Console.WriteLine(
                    $"  ended by followers {Percent(byFollowers, completed)}, " +
                    $"by elimination {Percent(byElimination, completed)}, " +
                    $"drawn {Percent(draws, completed)}; " +
                    $"{(double)totalTurns / completed:0.0} drafts per game");
            }

            if (failures.Count == 0)
            {
                Console.WriteLine("  PASS  every game reached a legal end state");
                return true;
            }

            // Identical faults are collapsed so one bug hit a thousand times
            // does not bury the others.
            foreach (var group in failures
                         .GroupBy(f => f[(f.IndexOf(": ", StringComparison.Ordinal) + 2)..])
                         .OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"  FAIL  x{group.Count()}  {group.Key}");
                Console.WriteLine($"          first: {group.First()}");
            }

            return false;
        }

        private static string Percent(int part, int whole) => $"{100.0 * part / whole:0.0}%";

        /// <summary>How one game finished, for the summary at the end of a run.</summary>
        private readonly struct Outcome
        {
            public Outcome(int steps, int drafts, bool draw, bool wonByFollowers)
            {
                Steps = steps;
                Drafts = drafts;
                Draw = draw;
                WonByFollowers = wonByFollowers;
            }

            public int Steps { get; }
            public int Drafts { get; }
            public bool Draw { get; }
            public bool WonByFollowers { get; }
        }

        private static string Describe(Exception e) =>
            e is FuzzFailure ? e.Message : $"{e.GetType().Name}: {e.Message}";

        /// <summary>Plays one game to its end and reports how it finished.</summary>
        private static Outcome PlayOneGame(List<CardDefinition> cards, int playerCount, int seed, int totalCopies)
        {
            var random = new Random(seed);
            var names = Enumerable.Range(0, playerCount).Select(i => $"P{i}").ToList();
            var game = new GameState(names, cards, seed) { FirstDrafterIndex = random.Next(playerCount) };

            game.BeginDraft();

            var steps = 0;
            while (game.Phase != TurnPhase.GameOver && steps < MaxStepsPerGame)
            {
                steps++;
                CheckInvariants(game, totalCopies);

                if (game.PendingChoice != null)
                {
                    AnswerPendingChoice(game, random);
                    continue;
                }

                TakePhaseAction(game, random);
            }

            CheckInvariants(game, totalCopies);

            if (steps >= MaxStepsPerGame)
            {
                throw new FuzzFailure(
                    $"game did not finish in {MaxStepsPerGame} actions (stuck in {game.Phase})");
            }

            if (game.Winner == null && !game.IsDraw)
            {
                throw new FuzzFailure("game reported GameOver with no winner and no draw");
            }

            return new Outcome(
                steps,
                game.DraftNumber,
                game.IsDraw,
                game.Winner is { Followers: >= GameSettings.FollowersToWin });
        }

        // ------------------------------------------------------------- Choices

        private static void AnswerPendingChoice(GameState game, Random random)
        {
            var choice = game.PendingChoice;
            var asked = choice.AskedOfPlayerId;

            switch (choice.Kind)
            {
                case ChoiceKind.Player:
                    if (choice.PlayerOptions.Count == 0)
                    {
                        throw new FuzzFailure($"'{choice.Prompt}' offered no players to choose from");
                    }

                    game.AnswerPlayerChoice(asked, Pick(choice.PlayerOptions, random));
                    break;

                case ChoiceKind.Card:
                    if (choice.CardOptions.Count == 0)
                    {
                        throw new FuzzFailure($"'{choice.Prompt}' offered no cards to choose from");
                    }

                    game.AnswerCardChoice(asked, Pick(choice.CardOptions, random));
                    break;

                case ChoiceKind.Color:
                    var colors = choice.ColorOptions.Count > 0
                        ? choice.ColorOptions
                        : (IReadOnlyList<ResourceColor>)EffectContext.AllColors;
                    game.AnswerColorChoice(asked, Pick(colors, random));
                    break;

                case ChoiceKind.Option:
                    if (choice.Options.Count == 0)
                    {
                        throw new FuzzFailure($"'{choice.Prompt}' offered no options to choose from");
                    }

                    game.AnswerOptionChoice(asked, Pick(choice.Options, random));
                    break;

                case ChoiceKind.YesNo:
                    game.AnswerYesNo(asked, random.Next(2) == 0);
                    break;

                case ChoiceKind.Amount:
                    if (choice.MaxAmount < choice.MinAmount)
                    {
                        throw new FuzzFailure(
                            $"'{choice.Prompt}' asked for a number between " +
                            $"{choice.MinAmount} and {choice.MaxAmount}");
                    }

                    game.AnswerAmount(asked, random.Next(choice.MinAmount, choice.MaxAmount + 1));
                    break;

                default:
                    throw new FuzzFailure($"no idea how to answer a {choice.Kind} choice");
            }
        }

        // -------------------------------------------------------------- Actions

        private static void TakePhaseAction(GameState game, Random random)
        {
            switch (game.Phase)
            {
                case TurnPhase.Draft:
                    TakeDraftAction(game, random);
                    break;

                case TurnPhase.Rolling:
                    TakeRollingAction(game, random);
                    break;

                case TurnPhase.Activation:
                    // Activations are queued on entering the phase and resolved by
                    // the engine; there is nothing for a player to do but agree.
                    Advance(game);
                    break;

                case TurnPhase.Resource:
                    TakeResourceAction(game, random);
                    break;

                case TurnPhase.Buy:
                    TakeBuyAction(game, random);
                    break;

                default:
                    throw new FuzzFailure($"no actions defined for the {game.Phase} phase");
            }
        }

        private static void TakeDraftAction(GameState game, Random random)
        {
            if (game.CurrentDrafterId is not int drafter)
            {
                throw new FuzzFailure(
                    $"in the Draft phase with nobody to draft " +
                    $"(zone {game.DraftZone.Count}, deck {game.DeckCount}, draft #{game.DraftNumber})");
            }

            var blocked = game.MarkedInDraft(DraftMarker.Blocked);
            var reserved = game.MarkedInDraft(DraftMarker.Reserved);
            var reservedBy = game.MarkedInDraftBy(DraftMarker.Reserved);

            var takeable = game.DraftZone
                .Where(card => card != blocked && (card != reserved || reservedBy == drafter))
                .ToList();

            if (takeable.Count == 0)
            {
                throw new FuzzFailure(
                    $"player {drafter} has no legal pick from {game.DraftZone.Count} cards left");
            }

            game.DraftCard(drafter, Pick(takeable, random).InstanceId);
        }

        private static void TakeRollingAction(GameState game, Random random)
        {
            if (!game.DiceRolled)
            {
                // Each player presses their own Roll Die, which is the path the
                // real game takes - rolling the whole table at once is only the
                // timeout fallback, and would leave the per-seat rules untested.
                // Sometimes one is left unrolled so that fallback gets exercised too.
                var rollers = game.LivingPlayers.Where(p => !game.HasRolled(p.PlayerId)).ToList();
                var leaveOneOut = rollers.Count > 1 && random.Next(6) == 0;

                foreach (var player in leaveOneOut ? rollers.Skip(1) : rollers)
                {
                    game.RollPrimaryDie(player.PlayerId);

                    // Rolling twice must always be refused.
                    if (random.Next(8) == 0
                        && !Throws(() => game.RollPrimaryDie(player.PlayerId)))
                    {
                        throw new FuzzFailure($"{player.Name} was allowed to roll twice in one turn");
                    }
                }

                if (!leaveOneOut)
                {
                    return;
                }
            }

            // Try again's reroll, when somebody has it and the dice went badly.
            foreach (var player in game.LivingPlayers.ToList())
            {
                if (player.HasInPlay(CardIds.TryAgain) && random.Next(4) == 0)
                {
                    TryAction(() => game.RerollPrimaryDie(player.PlayerId));
                }
            }

            // Baal's Scheme counters.
            foreach (var player in game.LivingPlayers.ToList())
            {
                var baal = player.Compound.FirstOrDefault(
                    c => c.Definition.Id == CardIds.BaalTheManipulator && c.GetCounter(Counters.Scheme) > 0);

                if (baal != null && random.Next(3) == 0)
                {
                    var target = Pick(game.LivingPlayers.ToList(), random);
                    TryAction(() => game.SpendSchemeCounter(
                        player.PlayerId, target.PlayerId, random.Next(1, GameSettings.DieSides + 1)));
                }
            }

            if (!game.HighRollResourceClaimed)
            {
                foreach (var player in game.LivingPlayers.ToList())
                {
                    // Only the actual high roller may claim; everyone tries, and
                    // the rules engine turns the rest away.
                    TryAction(() => game.ClaimHighRollResource(
                        player.PlayerId, Pick(EffectContext.AllColors, random)));
                }
            }

            BuyMealCounters(game, random);
            Advance(game);
        }

        private static void TakeResourceAction(GameState game, Random random)
        {
            foreach (var player in game.LivingPlayers.ToList())
            {
                if (!player.IsAlive || game.HasCollectedResources(player.PlayerId))
                {
                    continue;
                }

                var picks = Enumerable
                    .Range(0, game.ResourcesPerTurnFor(player.PlayerId))
                    .Select(_ => Pick(EffectContext.AllColors, random))
                    .ToList();

                game.CollectResources(player.PlayerId, picks);
            }

            BuyMealCounters(game, random);
            Advance(game);
        }

        private static void TakeBuyAction(GameState game, Random random)
        {
            foreach (var player in game.LivingPlayers.ToList())
            {
                // A few purchases and a few recycles each turn, whatever is affordable.
                for (var attempt = 0; attempt < 3 && player.Hand.Count > 0 && player.IsAlive; attempt++)
                {
                    var card = Pick(player.Hand.ToList(), random);

                    if (random.Next(4) == 0)
                    {
                        TryAction(() => game.RecycleCard(player.PlayerId, card.InstanceId));
                        continue;
                    }

                    // BuyCard throws when it cannot be paid for, which is a legal
                    // answer rather than a bug - the fuzzer is allowed to try.
                    TryAction(() => game.BuyCard(player.PlayerId, card.InstanceId));

                    // A purchase can stop to ask a question, and nothing else may
                    // happen until it is answered.
                    if (game.PendingChoice != null)
                    {
                        return;
                    }
                }
            }

            BuyMealCounters(game, random);
            Advance(game);
        }

        /// <summary>Suspicious Chef's paid meal counter, which is legal in any phase.</summary>
        private static void BuyMealCounters(GameState game, Random random)
        {
            foreach (var player in game.LivingPlayers.ToList())
            {
                foreach (var chef in player.Compound
                             .Where(c => c.Definition.Id == CardIds.SuspiciousChef).ToList())
                {
                    if (random.Next(3) != 0)
                    {
                        continue;
                    }

                    var payment = Enumerable
                        .Range(0, GameSettings.MealCounterCost)
                        .Select(_ => Pick(EffectContext.AllColors, random))
                        .ToList();

                    TryAction(() => game.BuyMealCounter(player.PlayerId, chef.InstanceId, payment));
                }
            }
        }

        /// <summary>
        /// Runs an action the rules are entitled to refuse - buying something
        /// unaffordable, claiming a bonus you did not win. A refusal is the rules
        /// working; anything else is a real fault and is left to propagate.
        /// </summary>
        /// <summary>Whether an action was refused, for checks that expect a refusal.</summary>
        private static bool Throws(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private static void TryAction(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        // ----------------------------------------------------------- Invariants

        private static void CheckInvariants(GameState game, int totalCopies)
        {
            foreach (var player in game.Players)
            {
                if (player.Health < 0 || player.Health > player.MaxHealth)
                {
                    throw new FuzzFailure(
                        $"{player.Name} has {player.Health} health, outside 0-{player.MaxHealth}");
                }

                if (player.Followers < 0)
                {
                    throw new FuzzFailure($"{player.Name} has {player.Followers} followers");
                }

                if (player.Block < 0)
                {
                    throw new FuzzFailure($"{player.Name} has {player.Block} block");
                }

                foreach (var color in EffectContext.AllColors)
                {
                    if (player.Resources[color] < 0)
                    {
                        throw new FuzzFailure($"{player.Name} has {player.Resources[color]} {color}");
                    }
                }

                // A dead player must not still be taking turns.
                if (!player.IsAlive && player.Health != 0)
                {
                    throw new FuzzFailure($"{player.Name} is out but has {player.Health} health");
                }
            }

            CheckEveryCardIsSomewhere(game, totalCopies);

            // The table waits on whoever's pick it is, so it had better be
            // somebody who can actually take one.
            if (game.Phase == TurnPhase.Draft
                && game.CurrentDrafterId is int drafter
                && !game.GetPlayer(drafter).IsAlive)
            {
                throw new FuzzFailure(
                    $"the draft is waiting on {game.GetPlayer(drafter).Name}, who is out " +
                    $"(zone {game.DraftZone.Count}, draft #{game.DraftNumber}, " +
                    $"alive {string.Join("/", game.LivingPlayers.Select(p => p.Name))})");
            }

            // A game with a winner, or with nobody left, must have ended.
            if (game.Phase != TurnPhase.GameOver && !game.LivingPlayers.Any())
            {
                throw new FuzzFailure($"every leader is out but the game is still in {game.Phase}");
            }

            // A choice must always be answerable by the player it was put to.
            var choice = game.PendingChoice;
            if (choice != null && game.Players.All(p => p.PlayerId != choice.AskedOfPlayerId))
            {
                throw new FuzzFailure($"'{choice.Prompt}' was asked of player {choice.AskedOfPlayerId}, who does not exist");
            }

            if (choice != null && !game.GetPlayer(choice.AskedOfPlayerId).IsAlive)
            {
                throw new FuzzFailure($"'{choice.Prompt}' is waiting on a player who is out of the game");
            }
        }

        /// <summary>
        /// Every physical card must be in exactly one place. Catches an effect
        /// that copies a card into a compound without removing it, or drops one
        /// on the floor moving it between zones.
        /// </summary>
        private static void CheckEveryCardIsSomewhere(GameState game, int totalCopies)
        {
            var seen = new Dictionary<int, string>();

            void Account(IEnumerable<CardInstance> zone, string where)
            {
                foreach (var card in zone)
                {
                    if (seen.TryGetValue(card.InstanceId, out var already))
                    {
                        throw new FuzzFailure(
                            $"{card.Title} #{card.InstanceId} is in both {already} and {where}");
                    }

                    seen[card.InstanceId] = where;
                }
            }

            Account(game.Discard, "the discard");
            Account(game.DraftZone, "the draft zone");

            foreach (var player in game.Players)
            {
                Account(player.Hand, $"{player.Name}'s hand");
                Account(player.Compound, $"{player.Name}'s compound");
            }

            var accounted = seen.Count + game.DeckCount;
            if (accounted != totalCopies)
            {
                throw new FuzzFailure(
                    $"{accounted} cards accounted for, but the deck was built with {totalCopies}");
            }
        }

        /// <summary>
        /// Moves the phase on, unless an effect ended the game part-way through
        /// the actions this phase was taking.
        /// </summary>
        private static void Advance(GameState game)
        {
            if (game.Phase != TurnPhase.GameOver && game.PendingChoice == null)
            {
                game.AdvancePhase();
            }
        }

        private static T Pick<T>(IReadOnlyList<T> options, Random random) => options[random.Next(options.Count)];
    }
}
