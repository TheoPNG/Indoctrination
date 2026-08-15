// Runs the game's rules logic outside Unity so it can be checked quickly.
// Compiles directly against Assets/Scripts/Core, so it always tests the live code.
//
//     ./Tools/RulesCheck/run.sh
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Indoctrination.Core;
using Indoctrination.Core.Effects;
using Indoctrination.Net;

static class RulesCheck
{
    static int failures = 0;

    /// <summary>Locates Cards.json relative to this source file.</summary>
    static string CardDataPath([CallerFilePath] string thisFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        return Path.Combine(repoRoot, "Assets", "Resources", "Data", "Cards.json");
    }

    static void Check(string label, bool condition, string detail = "")
    {
        Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}  {label}{(detail == "" ? "" : $"  [{detail}]")}");
        if (!condition) failures++;
    }

    static List<CardDefinition> LoadCards(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<CardDefinition>();
        foreach (var e in doc.RootElement.GetProperty("cards").EnumerateArray())
        {
            var c = new CardDefinition
            {
                id = e.GetProperty("id").GetString(),
                title = e.GetProperty("title").GetString(),
                type = e.GetProperty("type").GetString(),
                costRaw = e.GetProperty("costRaw").GetString(),
                color = e.GetProperty("color").GetString(),
                effect = e.GetProperty("effect").GetString(),
                count = e.GetProperty("count").GetInt32(),
                activationNumbers = e.GetProperty("activationNumbers").EnumerateArray().Select(n => n.GetInt32()).ToArray()
            };
            list.Add(c);
        }
        return list;
    }

    static void Main(string[] args)
    {
        var cards = LoadCards(CardDataPath());
        Console.WriteLine($"Loaded {cards.Count} definitions, {cards.Sum(c => c.Count)} physical cards\n");

        // --fuzz [n] plays whole games at random instead of running the rule
        // checks, for shaking out interactions no hand-written case covers.
        var fuzzIndex = Array.IndexOf(args, "--fuzz");
        if (fuzzIndex >= 0)
        {
            var games = fuzzIndex + 1 < args.Length && int.TryParse(args[fuzzIndex + 1], out var n) ? n : 500;
            var seed = Array.IndexOf(args, "--seed") is var s && s >= 0 && s + 1 < args.Length
                       && int.TryParse(args[s + 1], out var parsedSeed)
                ? parsedSeed
                : 1000;

            Environment.Exit(Indoctrination.Tools.Fuzz.Run(cards, games, seed) ? 0 : 1);
        }

        Console.WriteLine("Card data parses:");
        var parsed = 0;
        foreach (var c in cards) { _ = c.Type; _ = c.Color; _ = c.Cost; parsed++; }
        Check("every card's type/color/cost parses", parsed == cards.Count, $"{parsed}");
        var bop = cards.First(c => c.title == "Bop");
        Check("free cost is zero, not special", !bop.Cost.IsSpecial && bop.Cost.Total == 0);
        var consume = cards.First(c => c.title == "It Who Consumes");
        Check("'*' cost is special", consume.Cost.IsSpecial);
        var doctor = cards.First(c => c.title == "Questionable Doctor");
        Check("Questionable Doctor activates on 3,4", doctor.ActivationNumbers.SequenceEqual(new[] { 3, 4 }));
        var brainWasher = cards.First(c => c.id == CardIds.HydroPlant);
        Check("Hydro Plant is printed as Brain Washer", brainWasher.Title == "Brain Washer");
        var yyrg = CardCost.Parse("YYRG");
        Check("YYRG = 2Y 1R 1G", yyrg.Amounts[ResourceColor.Yellow] == 2 && yyrg.Amounts[ResourceColor.Red] == 1
                                 && yyrg.Amounts[ResourceColor.Green] == 1 && yyrg.Total == 4);

        Console.WriteLine("\nDraft zone sizing:");
        Check("2 players -> 9", GameSettings.DraftZoneSize(2) == 9);
        Check("3 players -> 12", GameSettings.DraftZoneSize(3) == 12);
        Check("4 players -> 15", GameSettings.DraftZoneSize(4) == 15);

        foreach (var playerCount in new[] { 2, 3, 4 })
        {
            Console.WriteLine($"\nSnake draft with {playerCount} players:");
            var names = Enumerable.Range(0, playerCount).Select(i => $"P{i}").ToList();
            var g = new GameState(names, cards, randomSeed: 42);
            g.BeginDraft();
            Check("draft zone filled", g.DraftZone.Count == GameSettings.DraftZoneSize(playerCount), $"{g.DraftZone.Count}");

            var order = new List<int>();
            while (g.CurrentDrafterId is int drafter)
            {
                order.Add(drafter);
                g.DraftCard(drafter, g.DraftZone[0].InstanceId);
            }

            var expectedFirstPass = Enumerable.Range(0, playerCount).ToList();
            var expectedSecondPass = Enumerable.Range(0, playerCount).Reverse().ToList();
            Check("first pass runs forward", order.Take(playerCount).SequenceEqual(expectedFirstPass),
                  string.Join(",", order));
            Check("second pass reverses", order.Skip(playerCount).Take(playerCount).SequenceEqual(expectedSecondPass));
            Check("every player drafted 3 cards", g.Players.All(p => p.Hand.Count == 3));
            Check("3 undrafted cards discarded", g.Discard.Count == 3, $"{g.Discard.Count}");
            Check("draft zone emptied", g.DraftZone.Count == 0);
            Check("play begins in Rolling", g.Phase == TurnPhase.Rolling);
            Check("players begin Rolling with their own roll still available",
                  !g.DiceRolled && g.Players.All(p => !g.HasRolled(p.PlayerId)));
        }

        Console.WriteLine("\nStarting stats:");
        var game = new GameState(new[] { "Teddy", "Asher" }, cards, randomSeed: 7);
        Check($"health starts at {GameSettings.StartingHealth}", game.Players.All(p => p.Health == GameSettings.StartingHealth));
        Check("followers start at 1", game.Players.All(p => p.Followers == 1));
        Check("nobody has won yet", game.Winner == null);

        Console.WriteLine("\nTurn loop (3 turns, then back to draft):");
        game.BeginDraft();
        while (game.CurrentDrafterId is int d) game.DraftCard(d, game.DraftZone[0].InstanceId);

        var phasesSeen = new List<string>();
        for (var turn = 0; turn < 3; turn++)
        {
            phasesSeen.Add($"T{game.TurnInRound}");
            foreach (var player in game.LivingPlayers.ToList())
                game.RollPrimaryDie(player.PlayerId);
            Check($"  turn {turn + 1}: all dice in 1-6",
                  game.Players.All(p => p.PrimaryDie >= 1 && p.PrimaryDie <= 6),
                  string.Join(",", game.Players.Select(p => p.PrimaryDie)));
            game.AdvancePhase();                       // Rolling -> Activation
            game.AdvancePhase();                       // Activation -> Resource
            foreach (var p in game.Players)
                game.CollectResources(p.PlayerId, new[] { ResourceColor.Red, ResourceColor.Blue });
            game.AdvancePhase();                       // Resource -> Buy
            game.AdvancePhase();                       // Buy -> next turn or Draft
        }
        Check("three turns then Draft", game.Phase == TurnPhase.Draft, string.Join(" ", phasesSeen));
        Check("resources accumulated (3 turns x 2)", game.Players.All(p => p.Resources.Total == 6),
              string.Join(" | ", game.Players.Select(p => p.Resources.ToString())));

        Console.WriteLine("\nRecycling and buying:");
        var g2 = new GameState(new[] { "A", "B" }, cards, randomSeed: 3);
        g2.BeginDraft();
        while (g2.CurrentDrafterId is int d2) g2.DraftCard(d2, g2.DraftZone[0].InstanceId);
        var pa = g2.Players[0];
        g2.AdvancePhase(); g2.AdvancePhase(); g2.AdvancePhase(); // Rolling->...->Buy
        Check("reached Buy phase", g2.Phase == TurnPhase.Buy, g2.Phase.ToString());
        var toRecycle = pa.Hand[0];
        var recycleColor = toRecycle.Color;
        var handBefore = pa.Hand.Count;
        g2.RecycleCard(pa.PlayerId, toRecycle.InstanceId);
        Check("recycle removes card from hand", pa.Hand.Count == handBefore - 1);
        Check("recycle grants that card's colour", pa.Resources[recycleColor] == 1, recycleColor.ToString());

        Console.WriteLine("\nOnce-per-turn limits:");
        var g6 = new GameState(new[] { "A", "B" }, cards, randomSeed: 11);
        g6.BeginDraft();
        while (g6.CurrentDrafterId is int d6) g6.DraftCard(d6, g6.DraftZone[0].InstanceId);

        g6.RollPrimaryDie(0);
        Check("one player rolling does not roll for the table",
              g6.HasRolled(0) && !g6.HasRolled(1) && !g6.DiceRolled);
        g6.RollPrimaryDie(1);
        var highest = g6.LivingPlayers.Max(p => p.PrimaryDie);
        var highestRollers = g6.LivingPlayers.Where(p => p.PrimaryDie == highest).ToList();
        var roller = highestRollers.Count == 1 ? highestRollers[0] : null;
        Check("cannot roll the dice twice", Throws(() => g6.RollPrimaryDice()));

        if (roller != null)
        {
            g6.ClaimHighRollResource(roller.PlayerId, ResourceColor.Red);
            Check("high roll bonus is one resource, not unlimited",
                  Throws(() => g6.ClaimHighRollResource(roller.PlayerId, ResourceColor.Red)));
            Check("high roll bonus granted exactly one", roller.Resources.Total == 1,
                  roller.Resources.ToString());
        }
        else
        {
            Check("tied roll grants nobody the bonus",
                  Throws(() => g6.ClaimHighRollResource(0, ResourceColor.Red)));
        }

        g6.AdvancePhase();                                  // Rolling -> Activation
        g6.AdvancePhase();                                  // Activation -> Resource
        var twoReds = new[] { ResourceColor.Red, ResourceColor.Red };
        g6.CollectResources(0, twoReds);
        Check("cannot collect free resources twice",
              Throws(() => g6.CollectResources(0, twoReds)));
        Check("the other player can still collect", !Throws(() => g6.CollectResources(1, twoReds)));
        Check("must take exactly two",
              Throws(() => g6.CollectResources(0, new[] { ResourceColor.Red })));

        Console.WriteLine("\nPhase advances only when everyone is ready:");
        var g7 = new GameState(new[] { "A", "B", "C" }, cards, randomSeed: 13);
        g7.BeginDraft();
        while (g7.CurrentDrafterId is int d7) g7.DraftCard(d7, g7.DraftZone[0].InstanceId);
        g7.RollPrimaryDice();

        Check("one of three ready is not enough", !g7.SetReady(0, true));
        Check("two of three is not enough", !g7.SetReady(1, true));
        Check("un-readying takes it back", !g7.SetReady(0, false));
        Check("re-readying still waits on the third", !g7.SetReady(0, true));
        Check("all three ready lets it through", g7.SetReady(2, true));
        var phaseBeforeAdvance = g7.Phase;
        g7.AdvancePhase();
        Check("phase moved on", g7.Phase != phaseBeforeAdvance, $"{phaseBeforeAdvance} -> {g7.Phase}");
        Check("ready flags cleared for the new phase", g7.PlayersReady.Count == 0);
        Check("a dead player is not waited on", DeadPlayersAreSkipped(cards));
        Check("the draft has no ready check", Throws(() =>
        {
            var g = new GameState(new[] { "A", "B" }, cards, randomSeed: 17);
            g.BeginDraft();
            g.SetReady(0, true);
        }));

        Console.WriteLine("\nHealth and follower limits:");
        var limits = new GameState(new[] { "A", "B" }, cards, randomSeed: 51);
        limits.Heal(limits.Players[0], 50);
        Check($"healing stops at {GameSettings.MaxHealth}",
              limits.Players[0].Health == GameSettings.MaxHealth, $"{limits.Players[0].Health}");
        limits.ChangeFollowers(limits.Players[0], -50);
        Check($"followers never fall below {GameSettings.MinFollowers}",
              limits.Players[0].Followers == GameSettings.MinFollowers, $"{limits.Players[0].Followers}");
        limits.DealDamage(null, limits.Players[1], 500);
        Check("health still floors at zero", limits.Players[1].Health == 0);

        Console.WriteLine("\nDraft rotation, discounts, and the hand limit:");
        var rotate = new GameState(new[] { "A", "B", "C" }, cards, randomSeed: 61) { FirstDrafterIndex = 0 };
        var firstPickers = new List<int>();
        for (var round = 0; round < 3; round++)
        {
            if (round > 0)
            {
                while (rotate.Phase != TurnPhase.Draft) rotate.AdvancePhase();
            }
            else
            {
                rotate.BeginDraft();
            }

            firstPickers.Add(rotate.CurrentDrafterId ?? -1);
            while (rotate.CurrentDrafterId is int picker)
                rotate.DraftCard(picker, rotate.DraftZone[0].InstanceId);
        }

        Check("the first pick moves round the table each draft",
              firstPickers.SequenceEqual(new[] { 0, 1, 2 }), string.Join(",", firstPickers));

        // A stone should discount a Ritual just as much as a Unit.
        var stones = new GameState(new[] { "A", "B" }, cards, randomSeed: 62);
        var ritual = cards.First(c => c.Type == CardType.Ritual
                                      && c.Cost.Amounts.GetValueOrDefault(ResourceColor.Yellow) > 0);
        var pricedRitual = new CardInstance(-60, ritual);
        var beforeStone = stones.CostFor(stones.Players[0], pricedRitual).Total;
        stones.Players[0].Compound.Add(new CardInstance(-61, cards.First(c => c.id == CardIds.Wealthstone)));
        Check("Wealthstone discounts a Ritual, not just Units",
              stones.CostFor(stones.Players[0], pricedRitual).Total == beforeStone - 1,
              $"{beforeStone} -> {stones.CostFor(stones.Players[0], pricedRitual).Total}");

        // Going over the hand limit costs the excess at the end of the turn.
        var hoard = new GameState(new[] { "A", "B" }, cards, randomSeed: 63);
        FinishDraft(hoard);
        while (hoard.Phase != TurnPhase.Buy) hoard.AdvancePhase();
        while (hoard.Players[0].Hand.Count < GameSettings.HandLimit + 3)
            hoard.DrawCard(0);

        var overLimit = hoard.Players[0].Hand.Count;
        hoard.AdvancePhase();
        while (hoard.PendingChoice != null) hoard.AnswerPendingChoiceWithDefault();

        Check($"a hand over {GameSettings.HandLimit} is cut back as the turn closes",
              hoard.Players[0].Hand.Count == GameSettings.HandLimit,
              $"{overLimit} -> {hoard.Players[0].Hand.Count}");

        Console.WriteLine("\nWin conditions:");
        var g3 = new GameState(new[] { "A", "B" }, cards, randomSeed: 1);
        g3.Players[0].GainFollowers(19);
        Check("20 followers wins", g3.Winner?.Name == "A", $"{g3.Players[0].Followers} followers");
        var g4 = new GameState(new[] { "A", "B" }, cards, randomSeed: 1);
        g4.Players[1].TakeDamage(19);
        Check("damage floors at 0", g4.Players[1].Health == 0);
        Check("last leader standing wins", g4.Winner?.Name == "A");

        Console.WriteLine("\nRule enforcement (these should be rejected):");
        var g5 = new GameState(new[] { "A", "B" }, cards, randomSeed: 5);
        g5.BeginDraft();
        var wrongPlayer = g5.CurrentDrafterId == 0 ? 1 : 0;
        Check("cannot draft out of turn",
              Throws(() => g5.DraftCard(wrongPlayer, g5.DraftZone[0].InstanceId)));
        Check("cannot buy during draft",
              Throws(() => g5.BuyCard(0, g5.DraftZone[0].InstanceId)));
        Check("cannot afford card with no resources",
              !new ResourcePool().CanAfford(CardCost.Parse("RRR")));
        Check("special cost is never affordable with resources alone",
              !new ResourcePool().CanAfford(CardCost.Parse("*")));
        Check("5 players rejected",
              Throws(() => new GameState(new[] { "A", "B", "C", "D", "E" }, cards, 1)));

        CheckCardCoverage(cards);
        CheckEffectResolution(cards);
        CheckSettledCards(cards);
        CheckActivationOrder(cards);
        CheckStandardizedUniforms(cards);
        CheckChoicesSpeakForThemselves(cards);
        CheckSuspiciousChefCount(cards);
        CheckTryAgainHasAWindow(cards);
        CheckFollowerCosts(cards);
        CheckConcessions(cards);
        CheckEndStates(cards);
        CheckPerPlayerViews(cards);

        Console.WriteLine($"\n{(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED")}");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// Every card must be accounted for: either it has an effect, or it is an
    /// always-on Blessing, or it is on the list of things the designers still
    /// need to decide. Anything else has been forgotten.
    /// </summary>
    static void CheckCardCoverage(List<CardDefinition> cards)
    {
        Console.WriteLine("\nCard coverage:");

        var realIds = cards.Select(c => c.id).ToHashSet();
        var constants = typeof(CardIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue())
            .ToList();

        var phantom = constants.Where(id => !realIds.Contains(id)).ToList();
        Check($"all {constants.Count} CardIds constants name a real card",
              phantom.Count == 0, string.Join(", ", phantom));
        Check("every card has a constant", constants.Count == realIds.Count);

        var pending = CardEffects.NeedsDesignDecision.Keys.ToHashSet();
        var missing = new List<string>();
        var implemented = 0;

        foreach (var card in cards)
        {
            // Units and Rituals do something when they resolve. Blessings are
            // mostly standing rules, so they are allowed to have no routine.
            var routine = CardEffects.For(card.id, card.ActivationNumbers.FirstOrDefault())
                          ?? CardEffects.OnEnterPlay(card.id);

            if (routine != null) implemented++;
            else if (card.Type != CardType.Blessing && !pending.Contains(card.id)) missing.Add(card.id);
        }

        Check($"{implemented} cards have logic; none silently missing",
              missing.Count == 0, string.Join(", ", missing));

        var stale = pending.Where(id => !realIds.Contains(id)).ToList();
        Check($"{pending.Count} open design questions all name real cards",
              stale.Count == 0, string.Join(", ", stale));

        var everyUnitActivates = cards
            .Where(c => c.Type == CardType.Unit && c.ActivationNumbers.Count == 0)
            .Select(c => c.id).ToList();
        Check("every Unit has at least one activation number",
              everyUnitActivates.Count == 0, string.Join(", ", everyUnitActivates));

        // Cards with no copies are still fully defined and fully implemented -
        // they simply never reach the deck. That is how a card is taken out
        // without deleting the work, and it is completely invisible in play, so
        // it is reported every run rather than left to be rediscovered.
        var benched = cards.Where(c => c.Count <= 0).Select(c => c.title).ToList();
        Console.WriteLine(benched.Count == 0
            ? "  ----  every card is in the deck"
            : $"  ----  BENCHED, not in the deck: {string.Join(", ", benched)}");
    }

    /// <summary>
    /// Drives the effect engine end to end: a card that asks a question must stop
    /// and wait, and answering it must let the rest of the effect finish.
    /// </summary>
    static void CheckEffectResolution(List<CardDefinition> cards)
    {
        Console.WriteLine("\nEffect resolution:");

        // Three players so that "choose an opponent" is a real question.
        var game = new GameState(new[] { "A", "B", "C" }, cards, randomSeed: 23);
        var attacker = game.Players[0];
        var supernatural = cards.First(c => c.id == CardIds.SupernaturalEvent);

        game.EnqueueEffect(new CardInstance(-1, supernatural), attacker,
                           CardEffects.For(CardIds.SupernaturalEvent, 0), "Supernatural Event");
        game.ResolveEffects();

        Check("an unaimed damage effect stops to ask for a target",
              game.PendingChoice != null && game.PendingChoice.Kind == ChoiceKind.Player,
              game.PendingChoice?.Prompt ?? "nothing pending");
        Check("the controller is the one asked",
              game.PendingChoice?.AskedOfPlayerId == 0);
        Check("only opponents are offered",
              game.PendingChoice != null && !game.PendingChoice.PlayerOptions.Contains(0),
              string.Join(",", game.PendingChoice?.PlayerOptions ?? new List<int>()));
        Check("answering with a player who was not offered is rejected",
              Throws(() => game.AnswerPlayerChoice(0, 0)));
        Check("another player cannot answer for you",
              Throws(() => game.AnswerPlayerChoice(1, 2)));

        game.AnswerPlayerChoice(0, 2);
        Check("the chosen player took the damage",
              game.Players[2].Health == GameSettings.StartingHealth - 3,
              $"{game.Players[2].Health} health");
        Check("the player who was not chosen is untouched",
              game.Players[1].Health == GameSettings.StartingHealth);
        Check("the queue is empty afterwards", !game.HasEffectsPending);

        // With one opponent left there is nothing to ask, so it should not stop.
        var duel = new GameState(new[] { "A", "B" }, cards, randomSeed: 24);
        duel.EnqueueEffect(new CardInstance(-2, supernatural), duel.Players[0],
                           CardEffects.For(CardIds.SupernaturalEvent, 0), "Supernatural Event");
        duel.ResolveEffects();
        Check("a forced choice does not pester the player", duel.PendingChoice == null);
        Check("and resolves against the only opponent",
              duel.Players[1].Health == GameSettings.StartingHealth - 3,
              $"{duel.Players[1].Health} health");

        // Blessings change the numbers without ever activating.
        var wings = new GameState(new[] { "A", "B" }, cards, randomSeed: 25);
        wings.Players[1].Compound.Add(
            new CardInstance(-3, cards.First(c => c.id == CardIds.HalphasWings)));
        wings.DealDamage(wings.Players[0], wings.Players[1], 3);
        Check("Halphas Wings blunts a big hit",
              wings.Players[1].Health == GameSettings.StartingHealth - 2,
              $"{wings.Players[1].Health} health");
        wings.DealDamage(wings.Players[0], wings.Players[1], 1);
        Check("but leaves a single point alone",
              wings.Players[1].Health == GameSettings.StartingHealth - 3);

        // Clown Cult reads every follower change backwards.
        var clowns = new GameState(new[] { "A", "B" }, cards, randomSeed: 26);
        clowns.Players[0].Compound.Add(
            new CardInstance(-4, cards.First(c => c.id == CardIds.ClownCult)));
        clowns.ChangeFollowers(clowns.Players[0], 2);
        Check("gaining 2 followers under Clown Cult loses 3, down to the floor of one",
              clowns.Players[0].Followers
              == Math.Max(GameSettings.MinFollowers, GameSettings.StartingFollowers - 3),
              $"{clowns.Players[0].Followers} followers");

        // Block soaks damage before health does.
        var walls = new GameState(new[] { "A", "B" }, cards, randomSeed: 27);
        walls.GainBlock(walls.Players[1], 2);
        walls.DealDamage(walls.Players[0], walls.Players[1], 3);
        Check("Block absorbs first",
              walls.Players[1].Health == GameSettings.StartingHealth - 1 && walls.Players[1].Block == 0,
              $"{walls.Players[1].Health} health, {walls.Players[1].Block} block");

        // The stones make cards cheaper. All eight of them, each on its own
        // colour, and each checked against a card that actually carries that
        // colour - a stone that discounts nothing is indistinguishable from a
        // stone that is not wired up at all.
        var stoneColours = new (string Stone, ResourceColor Colour)[]
        {
            (CardIds.Mindstone, ResourceColor.Blue),
            (CardIds.CursedMindstone, ResourceColor.Blue),
            (CardIds.Shieldstone, ResourceColor.Green),
            (CardIds.CursedShieldstone, ResourceColor.Green),
            (CardIds.Bloodstone, ResourceColor.Red),
            (CardIds.CursedBloodstone, ResourceColor.Red),
            (CardIds.Wealthstone, ResourceColor.Yellow),
            (CardIds.CursedWealthstone, ResourceColor.Yellow)
        };

        var stoneId = -5;
        foreach (var (stone, colour) in stoneColours)
        {
            var table = new GameState(new[] { "A", "B" }, cards, randomSeed: 28);
            var priced = cards.FirstOrDefault(c =>
                c.Cost is { IsSpecial: false } cost && cost.Amounts.GetValueOrDefault(colour) > 0);

            if (priced == null)
            {
                Check($"{stone} has something to discount", false, $"no card costs {colour}");
                continue;
            }

            var card = new CardInstance(stoneId--, priced);
            var full = table.CostFor(table.Players[0], card).Total;
            table.Players[0].Compound.Add(
                new CardInstance(stoneId--, cards.First(c => c.id == stone)));

            var discounted = table.CostFor(table.Players[0], card).Total;
            Check($"{stone} knocks a {colour} off the price",
                  discounted == full - 1,
                  $"{priced.title} {full} -> {discounted}");
        }

        // A runaway effect must stop rather than hang the server.
        var loop = new GameState(new[] { "A", "B" }, cards, randomSeed: 29);
        loop.EnqueueEffect(null, loop.Players[0], Forever, "runaway");
        loop.ResolveEffects();
        Check("a runaway effect is cut off instead of hanging", true);
    }

    /// <summary>
    /// One card as its holder is shown it, straight out of the real view
    /// builder - the same object the board reads, so a check here is a check on
    /// what a player actually sees.
    /// </summary>
    static CardView PricedView(GameState game, int playerId, CardInstance card)
    {
        return GameViewBuilder.Build(game, playerId, 0f, 0f)
            .players.First(p => p.playerId == playerId)
            .hand.First(c => c.instanceId == card.InstanceId);
    }

    /// <summary>An effect that never finishes, to prove the safety valve works.</summary>
    static IEnumerator<ChoiceRequest> Forever(EffectContext context)
    {
        while (true) yield return null;
    }

    /// <summary>
    /// The nine cards whose rules were open questions until the designers
    /// answered them. Several are reachable only through the draft or the damage
    /// pipeline rather than through CardEffects.For, so the coverage check above
    /// cannot see them - these drive the real code paths instead.
    /// </summary>
    static void CheckSettledCards(List<CardDefinition> cards)
    {
        Console.WriteLine("\nCards settled by the design pass:");

        CardInstance Card(int id, string definitionId) =>
            new(id, cards.First(c => c.id == definitionId));

        // ------------------------------------------------ Draft Blessings
        var draft = new GameState(new[] { "A", "B" }, cards, randomSeed: 31);
        draft.Players[0].Compound.Add(Card(-10, CardIds.BlockedByGames));
        draft.Players[1].Compound.Add(Card(-11, CardIds.CultLeaderSParkingSpot));
        draft.BeginDraft();

        Check("Blocked by Games stops to pick a card",
              draft.PendingChoice is { Kind: ChoiceKind.Card, AskedOfPlayerId: 0 },
              draft.PendingChoice?.Prompt ?? "nothing pending");

        var blocked = draft.PendingChoice!.CardOptions[0];
        draft.AnswerCardChoice(0, blocked);

        Check("then the Parking Spot picks, and cannot re-use the blocked card",
              draft.PendingChoice is { Kind: ChoiceKind.Card, AskedOfPlayerId: 1 }
              && !draft.PendingChoice.CardOptions.Contains(blocked));

        var reserved = draft.PendingChoice!.CardOptions[0];
        draft.AnswerCardChoice(1, reserved);
        Check("the draft can begin once both marks are set", draft.PendingChoice == null);

        var firstDrafter = draft.CurrentDrafterId!.Value;
        Check("nobody may draft the blocked card",
              Throws(() => draft.DraftCard(firstDrafter, blocked)));
        Check("a player may not draft somebody else's reserved card",
              firstDrafter == 1 || Throws(() => draft.DraftCard(firstDrafter, reserved)));

        // ------------------------------------------------------ Human Trap
        var trap = new GameState(new[] { "A", "B" }, cards, randomSeed: 32);
        trap.Players[0].Compound.Add(Card(-12, CardIds.HumanTrap));
        trap.BeginDraft();

        var bait = trap.PendingChoice!.CardOptions[0];
        trap.AnswerCardChoice(0, bait);

        // Draft everything except the bait, so it is certain to be left behind.
        while (trap.CurrentDrafterId is int drafter)
            trap.DraftCard(drafter, trap.DraftZone.First(c => c.InstanceId != bait).InstanceId);

        Check("Human Trap hits the opponent when its bait goes undrafted",
              trap.Players[1].Health == GameSettings.StartingHealth - GameSettings.HumanTrapDamage,
              $"{trap.Players[1].Health} health");
        Check("and does not hit the trapper", trap.Players[0].Health == GameSettings.StartingHealth);

        // --------------------------------------------- First Line of Defense
        var defense = new GameState(new[] { "A", "B" }, cards, randomSeed: 33);
        defense.Players[1].Compound.Add(Card(-13, CardIds.FirstLineOfDefense));
        defense.Players[1].GainFollowers(6);
        var followersBefore = defense.Players[1].Followers;

        defense.DealDamage(defense.Players[0], defense.Players[1], 2);
        defense.ResolveEffects();

        Check("First Line of Defense offers the swap on the hit",
              defense.PendingChoice is { Kind: ChoiceKind.YesNo, AskedOfPlayerId: 1 },
              defense.PendingChoice?.Prompt ?? "nothing pending");

        defense.AnswerYesNo(1, true);
        Check("taking it puts the health back",
              defense.Players[1].Health == GameSettings.StartingHealth,
              $"{defense.Players[1].Health} health");
        Check("and costs twice the wound in followers",
              defense.Players[1].Followers == followersBefore - 4,
              $"{followersBefore} -> {defense.Players[1].Followers}");

        // Nobody is offered a bargain they cannot pay for.
        var broke = new GameState(new[] { "A", "B" }, cards, randomSeed: 34);
        broke.Players[1].Compound.Add(Card(-14, CardIds.FirstLineOfDefense));
        broke.DealDamage(broke.Players[0], broke.Players[1], 5);
        broke.ResolveEffects();
        Check("but not when the followers are not there to pay",
              broke.PendingChoice == null && broke.Players[1].Health == GameSettings.StartingHealth - 5);

        // ----------------------------------------------------- Soul Swapper
        var swap = new GameState(new[] { "A", "B" }, cards, randomSeed: 35);
        var swapper = Card(-15, CardIds.SoulSwapper);
        swapper.AddCounter(Counters.Swap, GameSettings.SoulSwapperBaseCounters);
        swap.Players[0].Compound.Add(swapper);

        var prize = Card(-16, CardIds.AsherPirozzi);   // a cheap Red Unit
        swap.Players[1].Compound.Add(prize);

        swap.EnqueueEffect(swapper, swap.Players[0], CardEffects.For(CardIds.SoulSwapper, 6), "Soul Swapper");
        swap.ResolveEffects();
        Check("Soul Swapper asks before trading places",
              swap.PendingChoice is { Kind: ChoiceKind.YesNo });

        swap.AnswerYesNo(0, true);
        Check("the two cards changed compounds",
              swap.Players[0].Compound.Contains(prize) && swap.Players[1].Compound.Contains(swapper));
        Check("and its counters reset",
              swapper.GetCounter(Counters.Swap) == GameSettings.SoulSwapperBaseCounters,
              $"{swapper.GetCounter(Counters.Swap)} counters");

        // ------------------------------------------------ Cthulu, the Cosmic
        var cosmic = new GameState(new[] { "A", "B" }, cards, randomSeed: 36);
        var cthulu = Card(-17, CardIds.CthuluTheCosmic);
        var chef = Card(-18, CardIds.SuspiciousChef);
        chef.AddCounter(Counters.Meal, 1);
        cosmic.Players[0].Compound.Add(cthulu);
        cosmic.Players[0].Compound.Add(chef);

        cosmic.EnqueueEffect(cthulu, cosmic.Players[0], CardEffects.For(CardIds.CthuluTheCosmic, 1), "Cthulu");
        cosmic.ResolveEffects();

        cosmic.AnswerCardChoice(0, chef.InstanceId);      // target the Chef

        // Named actions rather than yes/no: "no" here removes a counter, which
        // is a move of its own and not a refusal to make one.
        cosmic.AnswerOptionChoice(0, "Add a counter");
        Check("Cthulu can only offer counters that already exist",
              cosmic.PendingChoice == null || cosmic.PendingChoice.Options.All(o => o == Counters.Meal),
              string.Join(",", cosmic.PendingChoice?.Options ?? new List<string>()));
        Check("a second meal counter landed",
              chef.GetCounter(Counters.Meal) == 2, $"{chef.GetCounter(Counters.Meal)} meal");

        // ------------------------------------------ Suspicious Chef's action
        var kitchen = new GameState(new[] { "A", "B" }, cards, randomSeed: 37);
        var paid = Card(-19, CardIds.SuspiciousChef);
        kitchen.Players[0].Compound.Add(paid);
        var wallet = new[] { ResourceColor.Red, ResourceColor.Red, ResourceColor.Blue };

        Check("a meal counter cannot be bought without the resources",
              Throws(() => kitchen.BuyMealCounter(0, paid.InstanceId, wallet)));

        foreach (var color in wallet) kitchen.Players[0].Resources.Add(color);
        kitchen.BuyMealCounter(0, paid.InstanceId, wallet);
        Check("paying 3 of any colour buys a meal counter",
              paid.GetCounter(Counters.Meal) == 1 && kitchen.Players[0].Resources.Total == 0);

        foreach (var color in wallet) kitchen.Players[0].Resources.Add(color);
        Check("but only once a turn",
              Throws(() => kitchen.BuyMealCounter(0, paid.InstanceId, wallet)));

        // ------------------------------------------------- Baal's Scheme counter
        var scheme = new GameState(new[] { "A", "B" }, cards, randomSeed: 38);
        var baal = Card(-20, CardIds.BaalTheManipulator);
        baal.AddCounter(Counters.Scheme, 1);
        scheme.Players[0].Compound.Add(baal);

        Check("Baal cannot act outside the Rolling phase",
              Throws(() => scheme.SpendSchemeCounter(0, 1, 6)));

        FinishDraft(scheme);
        Check("Baal waits until every player has rolled",
              Throws(() => scheme.SpendSchemeCounter(0, 1, 4)));
        scheme.RollPrimaryDie(0);
        scheme.RollPrimaryDie(1);
        scheme.SpendSchemeCounter(0, 1, 4);
        Check("spending a Scheme counter sets a die",
              scheme.Players[1].PrimaryDie == 4 && baal.GetCounter(Counters.Scheme) == 0,
              $"die {scheme.Players[1].PrimaryDie}");
        Check("and it cannot be spent twice",
              Throws(() => scheme.SpendSchemeCounter(0, 1, 2)));

        // ---------------------------------------------------- It Who Consumes
        var consume = new GameState(new[] { "A", "B" }, cards, randomSeed: 39);
        var it = Card(-21, CardIds.ItWhoConsumes);
        AdvanceToBuy(consume);

        // The draft filled the hand with whatever came off the top. Clearing it
        // keeps the choices below down to the cards this check is about.
        consume.Players[0].Hand.Clear();
        consume.Players[0].Hand.Add(it);

        Check("It Who Consumes cannot be bought with nothing to sacrifice",
              Throws(() => consume.BuyCard(0, it.InstanceId)));

        // The board decides what to light up and what to let go of by asking
        // the view whether a card is affordable. A "*" cost is paid in cards
        // rather than resources, so the resource pool answers no to it however
        // ready the player is - which made this card impossible to buy at all
        // once the board started gating on that answer.
        Check("and the board is told it cannot be afforded yet",
              !PricedView(consume, 0, it).canAfford);

        consume.Players[0].Compound.Add(Card(-22, CardIds.AsherPirozzi));       // a Unit
        consume.Players[0].Compound.Add(Card(-23, CardIds.WondrousBlood));      // a Blessing
        var ritual = Card(-24, CardIds.Sermon);
        consume.Players[0].Hand.Add(ritual);

        Check("and told it can once there is a Unit, a Blessing and a Ritual",
              PricedView(consume, 0, it).canAfford);

        consume.BuyCard(0, it.InstanceId);
        while (consume.PendingChoice != null)
        {
            var pending = consume.PendingChoice;
            consume.AnswerCardChoice(pending.AskedOfPlayerId, pending.CardOptions[0]);
        }

        Check("paying its cost eats a Unit, a Blessing, and a Ritual",
              consume.Players[0].Compound.Count(c => c.Type == CardType.Unit) == 1
              && consume.Players[0].Compound.All(c => c.Type != CardType.Blessing)
              && !consume.Players[0].Hand.Contains(ritual));
        Check("and the eaten Ritual's effect never fires",
              consume.Players[0].Followers == GameSettings.StartingFollowers,
              $"{consume.Players[0].Followers} followers");
    }

    /// <summary>
    /// Activation goes round the table starting from whoever drafts first, and
    /// within each player in the order they have arranged their own compound.
    /// A player whose units are spent is skipped rather than stalling the round.
    ///
    /// The trade is worth stating plainly: a Block from one player can now land
    /// after another player's Damage in the same round. Grouping the whole table
    /// by what each card did - which this replaced - guaranteed it never could.
    /// </summary>
    static void CheckActivationOrder(List<CardDefinition> cards)
    {
        Console.WriteLine("\nActivation order (round the table, in each player's own order):");

        CardDefinition ActivatingOn(string id, int face) => new()
        {
            id = id,
            title = id,
            type = "Unit",
            costRaw = "R",
            color = "Red",
            effect = "test",
            count = 1,
            activationNumbers = new[] { face }
        };

        // Player 1 drafts first, so player 1's units go first.
        var game = new GameState(new[] { "A", "B" }, cards, randomSeed: 40) { FirstDrafterIndex = 1 };
        FinishDraft(game);

        var first = game.Players[game.FirstDrafterIndex].PlayerId;

        // Resource gains, so nothing stops to ask anybody a question part-way.
        var early = new CardInstance(-30, ActivatingOn(CardIds.SolarPanels, 3));
        var late = new CardInstance(-31, ActivatingOn(CardIds.CrystalMine, 3));
        game.Players[first].Compound.Add(early);
        game.Players[first].Compound.Add(late);
        game.Players[1 - first].Compound.Add(new CardInstance(-32, ActivatingOn(CardIds.MoneyTree, 3)));

        foreach (var player in game.LivingPlayers.ToList()) game.RollPrimaryDie(player.PlayerId);
        game.SetPrimaryDie(game.Players[0], 3);
        game.SetPrimaryDie(game.Players[1], 3);

        var order = new List<(int Player, string Card)>();
        game.EffectQueued += (card, controller) => order.Add((controller.PlayerId, card?.Definition.Id));

        game.AdvancePhase();   // Rolling -> Activation

        Check("the player who drafts first activates first",
              order.Count > 0 && order[0].Player == first,
              order.Count == 0 ? "nothing activated" : $"player {order[0].Player} went first");

        Check("their own order decides which of their units goes first",
              order.Count > 0 && order[0].Card == CardIds.SolarPanels,
              order.Count == 0 ? "nothing activated" : order[0].Card);

        // Both dice show 3, so each unit fires twice - and takes both firings
        // together before the table moves on. One card doing its thing twice is
        // one turn at the table, not two separated by an opponent.
        Check("a unit woken twice takes both firings before passing on",
              order.Count > 1 && order[1].Player == first && order[1].Card == order[0].Card,
              string.Join(",", order.Select(e => $"{e.Player}:{e.Card}")));

        Check("then it passes to the next player",
              order.Count > 2 && order[2].Player != first,
              string.Join(",", order.Select(e => e.Player)));

        // Two units against one, both dice showing the same face: the player who
        // runs out is skipped rather than the round stalling.
        Check("a player with nothing left is skipped, not waited for",
              order.Count == 6 && order.Count(e => e.Player == first) == 4,
              $"{order.Count} activations, {order.Count(e => e.Player == first)} from the first player");

        Check("nothing was left waiting on a choice", game.PendingChoice == null);

        // A live table opts into paced delivery. The plan must still be complete
        // and public immediately, but no Unit may resolve before its own beat.
        var paced = new GameState(new[] { "A", "B" }, cards, randomSeed: 41)
        {
            FirstDrafterIndex = 1,
            PaceActivations = true
        };
        FinishDraft(paced);
        paced.Players[1].Compound.Add(new CardInstance(-40, ActivatingOn(CardIds.SolarPanels, 3)));
        paced.Players[0].Compound.Add(new CardInstance(-41, ActivatingOn(CardIds.MoneyTree, 3)));
        foreach (var player in paced.LivingPlayers.ToList()) paced.RollPrimaryDie(player.PlayerId);
        paced.SetPrimaryDie(paced.Players[0], 3);
        paced.SetPrimaryDie(paced.Players[1], 3);
        paced.AdvancePhase();

        // Duplicate dice still fire the Unit twice - that is the rule - but both
        // firings are taken together. The board shows one card striking twice
        // rather than the same card coming back after the opponent's turn, which
        // is what a player reads as "it activated twice" instead of "it appeared
        // twice for no reason".
        Check("a Unit woken twice takes both firings before the table moves on",
              paced.ActivationSequence.Count == 4
              && paced.ActivationSequence.Select(entry => entry.Controller.PlayerId)
                  .SequenceEqual(new[] { 1, 1, 0, 0 }),
              string.Join(",", paced.ActivationSequence.Select(entry => entry.Controller.PlayerId)));
        Check("paced play resolves nothing before its presentation beat",
              paced.ActivationCompletedCount == 0 && paced.HasEffectsPending);

        paced.ResolveNextActivation();
        Check("one presentation beat resolves exactly one Unit",
              paced.ActivationCompletedCount == 1 && paced.HasEffectsPending);
        Check("paced Activation cannot be skipped while Units remain",
              Throws(() => paced.AdvancePhase()));

        var pacedView = GameViewBuilder.Build(paced, viewerPlayerId: 0);
        Check("every client receives the authoritative activation cursor",
              pacedView.activations.Length == 4
              && pacedView.activationCompletedCount == 1
              && pacedView.activations[0].completed
              && !pacedView.activations[1].completed);

        // --- Re-ordering changes which unit fires first.
        game.ReorderUnit(first, late.InstanceId, 0);
        Check("moving a unit to the front puts it ahead of the one that was there",
              game.Players[first].Compound.IndexOf(late) < game.Players[first].Compound.IndexOf(early));

        Check("moving it to the front again is a no-op, not an error",
              DoesNotThrow(() => game.ReorderUnit(first, late.InstanceId, 0)));

        Check("and only your own units can be moved",
              Throws(() => game.ReorderUnit(1 - first, late.InstanceId, 0)));
    }

    /// <summary>
    /// Standardized Uniforms buys a second die that only its owner's units
    /// answer to. Checked end to end - granted on the roll, visible in the view,
    /// and actually waking a unit that the shared dice missed - because a die
    /// nobody can see is indistinguishable from a card that does nothing.
    /// </summary>
    static void CheckStandardizedUniforms(List<CardDefinition> cards)
    {
        Console.WriteLine("\nStandardized Uniforms (the extra die):");

        var game = new GameState(new[] { "A", "B" }, cards, randomSeed: 71);
        FinishDraft(game);

        var owner = game.Players[0];
        owner.Compound.Add(new CardInstance(-70, cards.First(c => c.id == CardIds.StandardizedUniforms)));

        foreach (var player in game.LivingPlayers.ToList())
        {
            game.RollPrimaryDie(player.PlayerId);
        }

        Check("its owner is dealt a second die", owner.PrivateDice.Count == 1,
              $"{owner.PrivateDice.Count} private dice");
        Check("and nobody else is", game.Players[1].PrivateDice.Count == 0);

        // Pin both shared dice away from the private one, so anything that
        // activates can only have been woken by the private die.
        var privateFace = owner.PrivateDice[0];
        var otherFace = privateFace == 1 ? 2 : 1;
        game.SetPrimaryDie(game.Players[0], otherFace);
        game.SetPrimaryDie(game.Players[1], otherFace);

        var onlyOnPrivate = new CardDefinition
        {
            id = CardIds.SolarPanels, title = "test", type = "Unit", costRaw = "R",
            color = "Red", effect = "test", count = 1, activationNumbers = new[] { privateFace }
        };

        var mine = new CardInstance(-71, onlyOnPrivate);
        var theirs = new CardInstance(-72, onlyOnPrivate);
        owner.Compound.Add(mine);
        game.Players[1].Compound.Add(theirs);

        var woken = new List<int>();
        game.EffectQueued += (card, controller) =>
        {
            if (card != null && card.InstanceId is -71 or -72)
            {
                woken.Add(card.InstanceId);
            }
        };

        game.AdvancePhase();   // Rolling -> Activation

        Check("the private die wakes its owner's unit",
              woken.Contains(mine.InstanceId), string.Join(",", woken));

        Check("but not an opponent's identical unit",
              !woken.Contains(theirs.InstanceId),
              "the extra die is supposed to be private");

        // The whole point of the card is a second number to plan around, so the
        // client has to actually be told about it.
        var view = GameViewBuilder.Build(game, owner.PlayerId);
        var seen = view.players.First(p => p.playerId == owner.PlayerId).privateDice;
        Check("and the owner's view carries it, or the card is invisible in play",
              seen != null && seen.Length == 1 && seen[0] == privateFace,
              seen == null ? "no privateDice field" : string.Join(",", seen));
    }

    /// <summary>
    /// A card that offers two different things has to name both of them.
    ///
    /// The board shows a card's question as its options and nothing else - no
    /// prompt, because the card is on screen at full size saying what it does.
    /// That only works while the options carry their own meaning. "Yes" and
    /// "No" carry none, and are only ever correct for an offer that can simply
    /// be declined.
    /// </summary>
    static void CheckChoicesSpeakForThemselves(List<CardDefinition> cards)
    {
        Console.WriteLine("\nChoices that have to speak for themselves:");

        var game = new GameState(new[] { "A", "B" }, cards, randomSeed: 52);
        var pentagram = new CardInstance(-80, cards.First(c => c.id == CardIds.Pentagram));
        game.Players[0].Compound.Add(pentagram);

        game.EnqueueEffect(
            pentagram, game.Players[0], CardEffects.For(CardIds.Pentagram, 4), "Pentagram");
        game.ResolveEffects();

        Check("Pentagram offers its two outcomes by name",
              game.PendingChoice is { Kind: ChoiceKind.Option }
              && game.PendingChoice.Options.Count == 2
              && game.PendingChoice.Options.Any(o => o.Contains("follower"))
              && game.PendingChoice.Options.Any(o => o.Contains("damage")),
              game.PendingChoice == null
                  ? "nothing was asked"
                  : $"{game.PendingChoice.Kind}: {string.Join(" / ", game.PendingChoice.Options)}");

        // And picking the follower option does that, rather than the other.
        var followers = game.Players[0].Followers;
        game.AnswerOptionChoice(0, game.PendingChoice.Options.First(o => o.Contains("follower")));

        Check("and taking the follower option gains one",
              game.Players[0].Followers == followers + 1,
              $"{followers} -> {game.Players[0].Followers}");
    }

    /// <summary>
    /// Suspicious Chef: starts on one meal counter, deals damage equal to its
    /// counters, and its counters may be bought up once a turn. Traced end to
    /// end because "the count seems odd" could be the printed counter, the
    /// purchase, the damage, or what the board is told - and they are four
    /// different bugs.
    /// </summary>
    static void CheckSuspiciousChefCount(List<CardDefinition> cards)
    {
        Console.WriteLine("\nSuspicious Chef (meal counters):");

        var game = new GameState(new[] { "A", "B" }, cards, randomSeed: 61);
        var owner = game.Players[0];
        var victim = game.Players[1];

        var chef = new CardInstance(-90, cards.First(c => c.id == CardIds.SuspiciousChef));
        owner.Compound.Add(chef);

        // Entering play is what prints the first counter.
        game.EnqueueEffect(
            chef, owner, CardEffects.OnEnterPlay(CardIds.SuspiciousChef), "Chef enters play");
        game.ResolveEffects();

        Check("it comes into play carrying one meal counter",
              chef.GetCounter(Counters.Meal) == 1, $"{chef.GetCounter(Counters.Meal)}");

        // Activating deals damage equal to the counters - one, at this point.
        var before = victim.Health;
        game.EnqueueEffect(chef, owner, CardEffects.For(CardIds.SuspiciousChef, 5), "Chef");
        game.ResolveEffects();

        if (game.PendingChoice != null)
        {
            game.AnswerPlayerChoice(0, victim.PlayerId);
        }

        Check("and deals damage equal to that count",
              victim.Health == before - 1, $"{before} -> {victim.Health}");

        // Buying one takes it to two, and costs exactly what it says.
        foreach (var color in EffectContext.AllColors)
        {
            owner.Resources.Add(color, 10);
        }

        var paid = owner.Resources.Total;
        var payment = Enumerable.Repeat(ResourceColor.Yellow, GameSettings.MealCounterCost).ToList();
        game.BuyMealCounter(0, chef.InstanceId, payment);

        Check("buying a meal counter adds exactly one",
              chef.GetCounter(Counters.Meal) == 2, $"{chef.GetCounter(Counters.Meal)}");
        Check("and charges exactly its printed cost",
              owner.Resources.Total == paid - GameSettings.MealCounterCost,
              $"{paid} -> {owner.Resources.Total}");

        Check("but only once a turn",
              Throws(() => game.BuyMealCounter(0, chef.InstanceId, payment)));

        // The bigger count has to reach the board, or a card that is working
        // still looks broken.
        var seen = GameViewBuilder.Build(game, 0)
            .players.First(p => p.playerId == 0)
            .compound.First(c => c.instanceId == chef.InstanceId);

        var meal = seen.counters.FirstOrDefault(counter => counter.name == Counters.Meal);
        Check("and the board is told the new count",
              meal != null && meal.count == 2,
              meal == null ? "no meal counter in the view" : $"{meal.count}");

        // And the damage follows the counter up.
        var second = victim.Health;
        game.EnqueueEffect(chef, owner, CardEffects.For(CardIds.SuspiciousChef, 5), "Chef");
        game.ResolveEffects();

        if (game.PendingChoice != null)
        {
            game.AnswerPlayerChoice(0, victim.PlayerId);
        }

        Check("a second counter means two damage, not one",
              victim.Health == second - 2, $"{second} -> {victim.Health}");
    }

    /// <summary>
    /// Try again has to have a moment in which it can be used: after every die
    /// is down, and before the units look at the results.
    ///
    /// That window is the whole card. The reroll only becomes legal once every
    /// die has landed, and the Rolling phase used to close on exactly that
    /// event - so the card was unusable by construction, and the rules alone
    /// looked correct throughout. This checks the window is open, not merely
    /// that the reroll works once you are in it.
    /// </summary>
    static void CheckTryAgainHasAWindow(List<CardDefinition> cards)
    {
        Console.WriteLine("\nTry again (the reroll window):");

        var game = new GameState(new[] { "A", "B" }, cards, randomSeed: 64);
        FinishDraft(game);

        var owner = game.Players[0];
        owner.Compound.Add(new CardInstance(-95, cards.First(c => c.id == CardIds.TryAgain)));

        Check("no reroll is offered before the dice are down", !game.CanReroll(0));

        foreach (var player in game.LivingPlayers.ToList())
        {
            game.RollPrimaryDie(player.PlayerId);
        }

        Check("once every die has landed, the reroll is open", game.CanReroll(0));
        Check("but only to whoever actually holds the card", !game.CanReroll(1));

        var before = owner.PrimaryDie;
        game.RerollPrimaryDie(0);

        Check("taking it closes the offer", !game.CanReroll(0));
        Check("and it cannot be taken twice in a turn",
              Throws(() => game.RerollPrimaryDie(0)));

        // The die may legitimately land on the same face, so this checks the
        // reroll happened rather than that the number changed.
        Check("the die was rerolled",
              owner.PrimaryDie >= 1 && owner.PrimaryDie <= GameSettings.DieSides,
              $"{before} -> {owner.PrimaryDie}");

        // And the phase is still Rolling, so there was somewhere to use it.
        Check("the phase is still open at that point",
              game.Phase == TurnPhase.Rolling, game.Phase.ToString());
    }

    /// <summary>
    /// Costs priced partly in followers, and the card that introduced them.
    ///
    /// Followers are the win condition, so spending them is spending progress -
    /// which makes "can I afford this" a different question from the resource
    /// one, and worth checking as its own thing rather than trusting the
    /// resource path to have covered it.
    /// </summary>
    static void CheckFollowerCosts(List<CardDefinition> cards)
    {
        Console.WriteLine("\nFollower costs, and Jormugandr's Fan Club:");

        // --- Parsing round-trips.
        var mixed = CardCost.Parse("G+7F");
        Check("a mixed cost parses both halves",
              mixed.Followers == 7 && mixed.Amounts[ResourceColor.Green] == 1 && mixed.Total == 1,
              $"{mixed.Total} resources, {mixed.Followers} followers");
        Check("and prints back to something Parse accepts",
              CardCost.Parse(mixed.ToString()).Followers == 7, mixed.ToString());
        Check("plain resource costs are untouched by the new syntax",
              CardCost.Parse("YYG").Followers == 0 && CardCost.Parse("YYG").Total == 3);
        Check("a malformed follower cost is refused rather than silently ignored",
              Throws(() => CardCost.Parse("G+F")) && Throws(() => CardCost.Parse("G+xF")));

        // Discounting is a resource mechanic and must leave followers alone.
        Check("the stones discount resources without touching followers",
              mixed.Reduced(ResourceColor.Green, 1).Followers == 7);

        // --- Affording it.
        var game = new GameState(new[] { "A", "B" }, cards, randomSeed: 77);
        AdvanceToBuy(game);

        var buyer = game.Players[0];
        var fanClub = new CardInstance(-96, cards.First(c => c.id == CardIds.JormugandrsFanClub));
        buyer.Hand.Add(fanClub);
        buyer.Resources.Add(ResourceColor.Green, 5);

        Check("a card priced in followers is unaffordable without them",
              !buyer.CanAfford(fanClub.Cost), $"{buyer.Followers} followers");

        // Enough to pay seven and still sit on the game's floor afterwards.
        buyer.GainFollowers(7 + GameSettings.MinFollowers);
        Check("and affordable once they are there", buyer.CanAfford(fanClub.Cost),
              $"{buyer.Followers} followers");

        var followersBefore = buyer.Followers;
        var greenBefore = buyer.Resources[ResourceColor.Green];
        game.BuyCard(0, fanClub.InstanceId);

        Check("buying it charges the followers as well as the resources",
              buyer.Followers == followersBefore - 7
              && buyer.Resources[ResourceColor.Green] == greenBefore - 1,
              $"{followersBefore} -> {buyer.Followers} followers, "
              + $"{greenBefore} -> {buyer.Resources[ResourceColor.Green]} green");

        Check("and it is in play", buyer.HasInPlay(CardIds.JormugandrsFanClub));

        // --- What it does.
        var victim = game.Players[1];
        victim.GainFollowers(10);
        var victimFollowers = victim.Followers;
        var victimHealth = victim.Health;

        game.DealDamage(buyer, victim, 3);

        Check("an opponent taking damage loses that many followers",
              victim.Followers == victimFollowers - (victimHealth - victim.Health),
              $"{victimHealth - victim.Health} damage, "
              + $"{victimFollowers} -> {victim.Followers} followers");

        // Its owner is not caught by their own card.
        var ownFollowers = buyer.Followers;
        game.DealDamage(victim, buyer, 2);

        Check("but its owner does not lose followers to their own card",
              buyer.Followers == ownFollowers,
              $"{ownFollowers} -> {buyer.Followers}");
    }

    static bool DoesNotThrow(Action action)
    {
        try { action(); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Each player is sent a separately-built picture of the game, and the whole
    /// point is that it carries no card they are not entitled to see. A leak here
    /// would be invisible in play and would quietly ruin every hidden-information
    /// card in the game, so it is checked directly.
    /// </summary>
    static void CheckPerPlayerViews(List<CardDefinition> cards)
    {
        Console.WriteLine("\nPer-player views (hidden information):");

        var game = new GameState(new[] { "A", "B", "C" }, cards, randomSeed: 44);
        FinishDraft(game);

        // Give one player something in play, so compounds are non-empty too.
        game.Players[1].Compound.Add(new CardInstance(-50, cards.First(c => c.id == CardIds.WondrousBlood)));

        var everyHandIsPrivate = true;
        var ownHandIsVisible = true;
        var compoundsArePublic = true;

        foreach (var viewer in game.Players)
        {
            var view = GameViewBuilder.Build(game, viewer.PlayerId);

            foreach (var seen in view.players)
            {
                var actual = game.GetPlayer(seen.playerId);

                if (seen.playerId == viewer.PlayerId)
                {
                    if (seen.hand.Length != actual.Hand.Count) ownHandIsVisible = false;
                }
                else if (seen.hand.Length != 0)
                {
                    everyHandIsPrivate = false;
                }

                // Counts are public even when contents are not, and so is the board.
                if (seen.handCount != actual.Hand.Count) everyHandIsPrivate = false;
                if (seen.compound.Length != actual.Compound.Count) compoundsArePublic = false;
            }
        }

        Check("no view carries another player's hand", everyHandIsPrivate);
        Check("but each player sees their own", ownHandIsVisible,
              $"{game.Players[0].Hand.Count} cards");
        Check("hand sizes and compounds stay public", compoundsArePublic);

        // A question is public - everyone needs to know the table is waiting, and
        // on whom - but it must still name the right player.
        var supernatural = cards.First(c => c.id == CardIds.SupernaturalEvent);
        game.EnqueueEffect(new CardInstance(-51, supernatural), game.Players[2],
                           CardEffects.For(CardIds.SupernaturalEvent, 0), "Supernatural Event");
        game.ResolveEffects();

        var asked = game.Players.Select(p => GameViewBuilder.Build(game, p.PlayerId)).ToList();
        Check("every player is told a decision is pending",
              asked.All(v => v.pendingChoice != null));
        Check("and all agree who owes it",
              asked.All(v => v.pendingChoice.askedOfPlayerId == 2));

        // Dead players are still rendered, so the table can see who is out.
        var over = new GameState(new[] { "A", "B" }, cards, randomSeed: 45);
        FinishDraft(over);
        over.DealDamage(null, over.Players[1], GameSettings.StartingHealth);
        over.ResolveEffects();
        var finalView = GameViewBuilder.Build(over, 0);
        Check("a finished game reports itself as over", finalView.isGameOver && !finalView.isDraw);
        Check("with the survivor named as winner", finalView.winnerPlayerId == 0,
              $"winner {finalView.winnerPlayerId}");
        Check("and the player who is out still shown",
              finalView.players.Length == 2 && !finalView.players[1].isAlive);
    }

    /// <summary>
    /// The ways a game can stop, and the ways it must not stop: a table wiped
    /// out together is a draw rather than a game that runs forever, a dead
    /// leader takes no more actions, and an abandoned question answers itself.
    /// </summary>
    static void CheckEndStates(List<CardDefinition> cards)
    {
        Console.WriteLine("\nEnd states and abandoned decisions:");

        // Everyone dying at once still has to end the game. Nothing is left to
        // roll, draft, or ask, so play cannot continue.
        var wipe = new GameState(new[] { "A", "B" }, cards, randomSeed: 41);
        FinishDraft(wipe);
        foreach (var player in wipe.Players) wipe.DealDamage(null, player, GameSettings.StartingHealth);
        wipe.ResolveEffects();
        Check("a total wipe ends the game", wipe.Phase == TurnPhase.GameOver, wipe.Phase.ToString());
        Check("and reports a draw rather than a winner", wipe.IsDraw && wipe.Winner == null);

        // A knocked-out leader stops acting, and stops being waited on.
        var out1 = new GameState(new[] { "A", "B", "C" }, cards, randomSeed: 42);
        FinishDraft(out1);
        out1.DealDamage(null, out1.Players[2], GameSettings.StartingHealth);
        out1.ResolveEffects();
        Check("the game continues while two remain", out1.Phase != TurnPhase.GameOver);
        Check("a dead leader cannot collect, buy, or ready up",
              Throws(() => out1.SetReady(2, true)));
        out1.RollPrimaryDice();
        Check("and the living can still finish the phase",
              out1.SetReady(0, true) == false && out1.SetReady(1, true));

        // A dead player must not be handed a draft pick - the table would wait forever.
        while (out1.Phase != TurnPhase.Draft) out1.AdvancePhase();
        Check("the next draft skips the leader who is out",
              out1.CurrentDrafterId != 2 && out1.DraftZone.Count > 0,
              $"drafter {out1.CurrentDrafterId}, zone {out1.DraftZone.Count}");

        // An abandoned question resolves itself rather than stopping the table.
        var quiet = new GameState(new[] { "A", "B", "C" }, cards, randomSeed: 43);
        var supernatural = cards.First(c => c.id == CardIds.SupernaturalEvent);
        quiet.EnqueueEffect(new CardInstance(-40, supernatural), quiet.Players[0],
                            CardEffects.For(CardIds.SupernaturalEvent, 0), "Supernatural Event");
        quiet.ResolveEffects();
        Check("a card is waiting on a decision", quiet.PendingChoice != null);
        quiet.AnswerPendingChoiceWithDefault();
        Check("which answers itself when nobody responds", quiet.PendingChoice == null);
        Check("and the effect still resolved",
              quiet.Players.Skip(1).Any(p => p.Health < GameSettings.StartingHealth),
              string.Join(",", quiet.Players.Select(p => p.Health)));
    }

    /// <summary>
    /// The two ways to stop playing, which follow opposite rules: resigning is
    /// one player's decision and nobody else is asked, while a draw is a result
    /// the whole table has to accept.
    /// </summary>
    static void CheckConcessions(List<CardDefinition> cards)
    {
        Console.WriteLine("\nResigning and draws:");

        // --- Resignation needs nobody's agreement.
        var quit = new GameState(new[] { "A", "B", "C" }, cards, randomSeed: 61);
        FinishDraft(quit);

        quit.Resign(2);
        Check("resigning takes that player out", !quit.Players[2].IsAlive);
        Check("and is recorded as giving up, not being knocked out", quit.HasResigned(2));
        Check("the game continues while two remain", quit.Phase != TurnPhase.GameOver);
        Check("a player who resigned cannot act", Throws(() => quit.SetReady(2, true)));
        Check("and cannot resign twice", Throws(() => quit.Resign(2)));

        // Down to one leader, resigning ends it.
        quit.Resign(1);
        Check("the last resignation leaves a winner", quit.Phase == TurnPhase.GameOver);
        Check("who is the player still standing", quit.Winner?.PlayerId == 0,
              quit.Winner?.Name ?? "nobody");

        // --- A draw needs everybody.
        var talks = new GameState(new[] { "A", "B", "C" }, cards, randomSeed: 62);
        FinishDraft(talks);

        talks.SetDrawOffer(0, true);
        Check("one player offering a draw does not end anything",
              talks.Phase != TurnPhase.GameOver);

        talks.SetDrawOffer(1, true);
        Check("nor does a majority", talks.Phase != TurnPhase.GameOver);

        talks.SetDrawOffer(1, false);
        talks.SetDrawOffer(2, true);
        Check("and an offer can be taken back", !talks.HasOfferedDraw(1));
        Check("so the table is still playing", talks.Phase != TurnPhase.GameOver);

        talks.SetDrawOffer(1, true);
        Check("everybody agreeing ends the game", talks.Phase == TurnPhase.GameOver);
        Check("with no winner", talks.Winner == null && talks.IsDraw);

        // --- A player who is already out is not waited on for a draw.
        var short_handed = new GameState(new[] { "A", "B", "C" }, cards, randomSeed: 63);
        FinishDraft(short_handed);
        short_handed.Resign(2);

        short_handed.SetDrawOffer(0, true);
        short_handed.SetDrawOffer(1, true);
        Check("a draw only needs the players still in it",
              short_handed.Phase == TurnPhase.GameOver && short_handed.IsDraw);

        // --- Resigning must not leave a question nobody can answer.
        var mid = new GameState(new[] { "A", "B", "C" }, cards, randomSeed: 64);
        var supernatural = cards.First(c => c.id == CardIds.SupernaturalEvent);
        mid.EnqueueEffect(new CardInstance(-60, supernatural), mid.Players[0],
                          CardEffects.For(CardIds.SupernaturalEvent, 0), "Supernatural Event");
        mid.ResolveEffects();

        Check("a card is waiting on player 0", mid.PendingChoice?.AskedOfPlayerId == 0);
        mid.Resign(0);
        Check("resigning mid-question does not strand the table", mid.PendingChoice == null);
        Check("and the game carries on", mid.Phase != TurnPhase.GameOver);
    }

    /// <summary>Runs a whole draft off, leaving the game in the Rolling phase.</summary>
    static void FinishDraft(GameState game)
    {
        game.BeginDraft();
        while (game.CurrentDrafterId is int drafter)
            game.DraftCard(drafter, game.DraftZone[0].InstanceId);
    }

    /// <summary>Walks a fresh game through its first draft and on to the Buy phase.</summary>
    static void AdvanceToBuy(GameState game)
    {
        FinishDraft(game);
        while (game.Phase != TurnPhase.Buy) game.AdvancePhase();
    }

    /// <summary>
    /// A player who has been knocked out must not hold up the table, so the
    /// living players alone should be enough to move the phase on.
    /// </summary>
    static bool DeadPlayersAreSkipped(List<CardDefinition> cards)
    {
        var game = new GameState(new[] { "A", "B", "C" }, cards, randomSeed: 19);
        game.BeginDraft();
        while (game.CurrentDrafterId is int drafter)
            game.DraftCard(drafter, game.DraftZone[0].InstanceId);

        game.Players[2].TakeDamage(GameSettings.StartingHealth);
        game.RollPrimaryDice();

        game.SetReady(0, true);
        return game.SetReady(1, true);
    }

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }
}
