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
        }

        Console.WriteLine("\nStarting stats:");
        var game = new GameState(new[] { "Teddy", "Asher" }, cards, randomSeed: 7);
        Check("health starts at 19", game.Players.All(p => p.Health == 19));
        Check("followers start at 1", game.Players.All(p => p.Followers == 1));
        Check("nobody has won yet", game.Winner == null);

        Console.WriteLine("\nTurn loop (3 turns, then back to draft):");
        game.BeginDraft();
        while (game.CurrentDrafterId is int d) game.DraftCard(d, game.DraftZone[0].InstanceId);

        var phasesSeen = new List<string>();
        for (var turn = 0; turn < 3; turn++)
        {
            phasesSeen.Add($"T{game.TurnInRound}");
            game.RollPrimaryDice();
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

        var roller = g6.RollPrimaryDice();
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
        CheckEndStates(cards);

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
        Check("gaining 2 followers under Clown Cult loses 3",
              clowns.Players[0].Followers == Math.Max(0, GameSettings.StartingFollowers - 3),
              $"{clowns.Players[0].Followers} followers");

        // Block soaks damage before health does.
        var walls = new GameState(new[] { "A", "B" }, cards, randomSeed: 27);
        walls.GainBlock(walls.Players[1], 2);
        walls.DealDamage(walls.Players[0], walls.Players[1], 3);
        Check("Block absorbs first",
              walls.Players[1].Health == GameSettings.StartingHealth - 1 && walls.Players[1].Block == 0,
              $"{walls.Players[1].Health} health, {walls.Players[1].Block} block");

        // The stones make cards cheaper.
        var stones = new GameState(new[] { "A", "B" }, cards, randomSeed: 28);
        var pricey = new CardInstance(-5, cards.First(c => c.id == CardIds.Ritualist));   // BBRR
        var before = stones.CostFor(stones.Players[0], pricey).Total;
        stones.Players[0].Compound.Add(
            new CardInstance(-6, cards.First(c => c.id == CardIds.Mindstone)));
        Check("Mindstone knocks a Blue off the price",
              stones.CostFor(stones.Players[0], pricey).Total == before - 1,
              $"{before} -> {stones.CostFor(stones.Players[0], pricey).Total}");

        // A runaway effect must stop rather than hang the server.
        var loop = new GameState(new[] { "A", "B" }, cards, randomSeed: 29);
        loop.EnqueueEffect(null, loop.Players[0], Forever, "runaway");
        loop.ResolveEffects();
        Check("a runaway effect is cut off instead of hanging", true);
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
        cosmic.AnswerYesNo(0, true);                      // add rather than remove
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
        FinishDraft(scheme);

        Check("Baal cannot act before the dice are rolled",
              Throws(() => scheme.SpendSchemeCounter(0, 1, 6)));

        scheme.RollPrimaryDice();
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

        consume.Players[0].Compound.Add(Card(-22, CardIds.AsherPirozzi));       // a Unit
        consume.Players[0].Compound.Add(Card(-23, CardIds.WondrousBlood));      // a Blessing
        var ritual = Card(-24, CardIds.Sermon);
        consume.Players[0].Hand.Add(ritual);

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
    /// Two Units belonging to different players, activating on the same die
    /// roll: one grants Block, the other deals damage aimed (by the house rule)
    /// at whoever its controller chooses. If the table resolved seat by seat,
    /// whichever player went second would have their Block queued after the
    /// damage that was supposed to be reduced by it. Grouping by
    /// ActivationCategory instead means Block always lands first.
    /// </summary>
    static void CheckActivationOrder(List<CardDefinition> cards)
    {
        Console.WriteLine("\nActivation order (Block before Damage, same roll):");

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

        var game = new GameState(new[] { "A", "B" }, cards, randomSeed: 40);
        FinishDraft(game);

        // A deals damage, aimed by the house rule at its only opponent, B - who
        // holds the Block. Seat order alone would resolve A (seat 0) before B
        // (seat 1), so if the table only grouped by category and not truly
        // across seats, this is the arrangement that would still let the damage
        // through first. Both players roll the same face, so each Unit fires
        // twice (RolledValues has two 3s), doubling the stakes if the order is wrong.
        game.Players[0].Compound.Add(new CardInstance(-30, ActivatingOn(CardIds.AsherPirozzi, 3)));
        game.Players[1].Compound.Add(new CardInstance(-31, ActivatingOn(CardIds.WallBuilder, 3)));

        game.RollPrimaryDice();
        game.SetPrimaryDie(game.Players[0], 3);
        game.SetPrimaryDie(game.Players[1], 3);
        game.AdvancePhase();   // Rolling -> Activation, queues and resolves both

        Check("nothing was left waiting on a choice",
              game.PendingChoice == null,
              game.PendingChoice?.Prompt ?? "none");
        Check("Block absorbed both hits rather than letting them through",
              game.Players[1].Health == GameSettings.StartingHealth && game.Players[1].Block == 0,
              $"{game.Players[1].Health} health, {game.Players[1].Block} block");
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

        game.SetReady(0, true);
        return game.SetReady(1, true);
    }

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }
}
