// Runs the game's rules logic outside Unity so it can be checked quickly.
// Compiles directly against Assets/Scripts/Core, so it always tests the live code.
//
//     ./Tools/RulesCheck/run.sh
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Indoctrination.Core;

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

    static void Main()
    {
        var cards = LoadCards(CardDataPath());
        Console.WriteLine($"Loaded {cards.Count} definitions, {cards.Sum(c => c.Count)} physical cards\n");

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

        Console.WriteLine($"\n{(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED")}");
        Environment.Exit(failures == 0 ? 0 : 1);
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
