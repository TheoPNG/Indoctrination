using System.Collections.Generic;

namespace Indoctrination.Core.Effects
{
    /// <summary>
    /// The lookup from a card id to what that card actually does. Everything the
    /// rules engine knows about a specific card goes through here.
    ///
    /// Cards whose rules still need a decision from the designers are listed in
    /// <see cref="NeedsDesignDecision"/> and return no effect. They are deliberately
    /// separated from cards that simply have nothing to do when activated, so a
    /// card being forgotten never looks the same as a card being skipped on purpose.
    /// </summary>
    public static class CardEffects
    {
        /// <summary>
        /// What a card does when it activates. <paramref name="dieValue"/> is the
        /// roll that woke it; a handful of Units do different things per face.
        /// Returns null when the card has no activated effect.
        /// </summary>
        public static EffectRoutine For(string definitionId, int dieValue)
        {
            switch (definitionId)
            {
                // ------------------------------------------------- Blue Units
                case CardIds.DoubleAgentJapaneseArt: return UnitEffects.DoubleAgent;
                case CardIds.ItWhoConsumes: return UnitEffects.ItWhoConsumes;
                case CardIds.Slanderist: return UnitEffects.Slanderist;
                case CardIds.Ritualist: return UnitEffects.Ritualist;
                case CardIds.WisdomHoarder: return UnitEffects.WisdomHoarder;
                case CardIds.ResearcherOfTheOldWays: return UnitEffects.ResearcherOfTheOldWays;
                case CardIds.HeWhoRemembers: return UnitEffects.HeWhoRemembers;
                case CardIds.HydroPlant: return UnitEffects.HydroPlant;
                case CardIds.SnakeEyes: return UnitEffects.SnakeEyes;
                case CardIds.OneWithTheChildren: return UnitEffects.OneWithTheChildren;
                case CardIds.BaalTheManipulator: return UnitEffects.Baal;
                case CardIds.SoulSwapper: return UnitEffects.SoulSwapper;
                case CardIds.Propaganda: return UnitEffects.Propaganda;
                case CardIds.WorshiperOfTheBoneGod: return UnitEffects.WorshiperOfTheBoneGod;
                case CardIds.Initiate4: return UnitEffects.Initiate4;
                case CardIds.ChiefSacrificer: return UnitEffects.ChiefSacrificer;

                // ------------------------------------------------ Green Units
                case CardIds.JormTrustEater: return UnitEffects.JormTrustEater;
                case CardIds.ManipulatorOfTheMasses: return UnitEffects.ManipulatorOfTheMasses;
                case CardIds.Communist: return UnitEffects.Communist;
                case CardIds.WallBuilder: return UnitEffects.WallBuilder;
                case CardIds.TheBeastmaster: return UnitEffects.TheBeastmaster;
                case CardIds.FilipinaNurse: return UnitEffects.FilipinaNurse;
                case CardIds.Bodyguard: return UnitEffects.Bodyguard;
                case CardIds.ShieldWizard: return UnitEffects.Bodyguard;
                case CardIds.MasterMarketer: return UnitEffects.MasterMarketer;
                case CardIds.AngryDrunkMonkey: return UnitEffects.AngryDrunkMonkey;
                case CardIds.FelineCultist: return UnitEffects.GainOneFollower;
                case CardIds.Celebrity: return UnitEffects.GainOneFollower;
                case CardIds.YouthLeader: return UnitEffects.GainOneFollower;
                case CardIds.SeniorRecruiter: return UnitEffects.GainTwoFollowers;
                case CardIds.Security: return UnitEffects.Security;
                case CardIds.SolarPanels: return UnitEffects.GainGreen;
                case CardIds.TheAllmother: return UnitEffects.TheAllmother;
                case CardIds.WitchDoctor: return UnitEffects.HealOne;
                case CardIds.Grandma: return UnitEffects.HealOne;
                case CardIds.IsHeOnMeth: return UnitEffects.IsHeOnMeth;

                // -------------------------------------------------- Red Units
                case CardIds.ConfusedMan: return c => UnitEffects.ConfusedMan(c, dieValue);
                case CardIds.QuestionableDoctor: return c => UnitEffects.QuestionableDoctor(c, dieValue);
                case CardIds.AxeLicker: return c => UnitEffects.AxeLicker(c, dieValue);
                case CardIds.DrunkenFollower: return UnitEffects.DrunkenFollower;
                case CardIds.TheBeast: return UnitEffects.TheBeast;
                case CardIds.BelleOfTheBall: return UnitEffects.DealTwo;
                case CardIds.CultSPetTigerForArt: return UnitEffects.DealTwo;
                case CardIds.CultSPetSDad: return UnitEffects.DealThree;
                case CardIds.CultSPetSPet: return UnitEffects.DealFour;
                case CardIds.AsherPirozzi: return UnitEffects.DealOne;
                case CardIds.Arsonist: return UnitEffects.Arsonist;
                case CardIds.Vampire: return UnitEffects.Vampire;
                case CardIds.BeekeeperCultist: return UnitEffects.BeekeeperCultist;
                case CardIds.FireBreather: return UnitEffects.FireBreather;
                case CardIds.FriendOfTheBeasts: return UnitEffects.FriendOfTheBeasts;
                case CardIds.Pentagram: return UnitEffects.Pentagram;
                case CardIds.BloodSacrifice: return UnitEffects.GainRed;
                case CardIds.NuclearPlant: return UnitEffects.GainRed;
                case CardIds.Satanist: return UnitEffects.Satanist;
                case CardIds.KoolAid: return UnitEffects.KoolAid;
                case CardIds.BloodCollector: return UnitEffects.BloodCollector;
                case CardIds.HierophantSFavorite: return UnitEffects.HierophantsFavorite;
                case CardIds.Masochist: return UnitEffects.Masochist;
                case CardIds.BloodyMooner: return UnitEffects.BloodyMooner;
                case CardIds.Asmodeus: return UnitEffects.Asmodeus;
                case CardIds.CompoundLandmines: return UnitEffects.CompoundLandmines;

                // ----------------------------------------------- Yellow Units
                case CardIds.Bop: return c => UnitEffects.Bop(c, dieValue);
                case CardIds.AlmostAVampire: return UnitEffects.AlmostAVampire;
                case CardIds.CrystalMine: return UnitEffects.GainBlue;
                case CardIds.MoneyTree: return UnitEffects.GainGreen;
                case CardIds.BloodDiamondMine: return UnitEffects.GainRed;
                case CardIds.GoldMine: return UnitEffects.GainYellow;
                case CardIds.InspiredFool:
                    return c => UnitEffects.GainChosenResources(c, 1, "Inspired Fool");
                case CardIds.Cornucopia:
                    return c => UnitEffects.GainChosenResources(c, 2, "Cornucopia");
                case CardIds.SacrificialLamb: return UnitEffects.SacrificialLamb;
                case CardIds.AlternativeMedicine: return UnitEffects.AlternativeMedicine;
                case CardIds.MasterShaman: return UnitEffects.MasterShaman;
                case CardIds.OminousEye: return UnitEffects.OminousEye;
                case CardIds.SuspiciousChef: return UnitEffects.SuspiciousChef;
                case CardIds.CthuluTheCosmic: return UnitEffects.CthuluTheCosmic;
                case CardIds.Valefar: return UnitEffects.Valefar;
                case CardIds.BeingOfHearthlessness: return UnitEffects.BeingOfHeartlessness;

                // ----------------------------------------------- Blue Rituals
                case CardIds.Siege: return RitualEffects.Siege;
                case CardIds.Bribery: return RitualEffects.Bribery;
                case CardIds.TheSecondComing: return RitualEffects.TheSecondComing;
                case CardIds.CloseEnough: return RitualEffects.CloseEnough;
                case CardIds.VirginSacrifice: return RitualEffects.VirginSacrifice;

                // ---------------------------------------------- Green Rituals
                case CardIds.RadicalTactics: return RitualEffects.RadicalTactics;
                case CardIds.CompoundWalls: return RitualEffects.CompoundWalls;
                case CardIds.Ascension: return RitualEffects.Ascension;
                case CardIds.IntimateGroupConnection: return RitualEffects.GainThreeFollowers;
                case CardIds.Sermon: return RitualEffects.GainTwoFollowers;
                case CardIds.SmearCampaign: return RitualEffects.SmearCampaign;

                // ------------------------------------------------ Red Rituals
                case CardIds.SupernaturalEvent: return RitualEffects.SupernaturalEvent;
                case CardIds.ChemicalWeapons: return RitualEffects.ChemicalWeapons;
                case CardIds.Equality: return RitualEffects.Equality;
                case CardIds.SummonGoneWrong: return RitualEffects.SummonGoneWrong;
                case CardIds.BloodyMargarita: return RitualEffects.BloodyMargarita;
                case CardIds.Assassinate: return RitualEffects.Assassinate;
                case CardIds.ReviveTheForgotten: return RitualEffects.ReviveTheForgotten;

                // --------------------------------------------- Yellow Rituals
                case CardIds.VaccinesForAll: return RitualEffects.VaccinesForAll;

                // -------------------------------------------------- Blessings
                // Almost every Blessing is an always-on rule rather than something
                // that fires, and lives in EffectModifiers instead. Human Zoo is
                // the exception: it rolls a die at the start of each turn.
                case CardIds.HumanZoo: return BlessingEffects.HumanZoo;

                default: return null;
            }
        }

        /// <summary>
        /// What a card does the moment it hits the table - starting counters, the
        /// cursed stones' health penalty, and Double Agent walking off to somebody
        /// else's compound. Returns null for the cards that just sit there.
        /// </summary>
        public static EffectRoutine OnEnterPlay(string definitionId)
        {
            switch (definitionId)
            {
                case CardIds.BaalTheManipulator:
                    return BlessingEffects.StartWithCounters(Counters.Scheme, 1);
                case CardIds.SoulSwapper:
                    return BlessingEffects.StartWithCounters(Counters.Swap, 3);
                case CardIds.BloodCollector:
                    return BlessingEffects.StartWithCounters(Counters.Blood, 1);
                case CardIds.OminousEye:
                    return BlessingEffects.StartWithCounters(Counters.Static, 2);
                case CardIds.SuspiciousChef:
                    return BlessingEffects.StartWithCounters(Counters.Meal, 1);
                case CardIds.BeingOfHearthlessness:
                    return BlessingEffects.StartWithCounters(Counters.Trash, 1);

                // The cursed stones are cheap because they cost you a life.
                case CardIds.CursedMindstone:
                case CardIds.CursedShieldstone:
                case CardIds.CursedBloodstone:
                case CardIds.CursedWealthstone:
                    return BlessingEffects.LoseMaxHealth;

                case CardIds.DoubleAgentJapaneseArt:
                    return BlessingEffects.PlantOnOpponent;
                case CardIds.SufferingFromSuccess:
                    return BlessingEffects.PlantOnOpponent;

                default: return null;
            }
        }

        /// <summary>
        /// Cards left unimplemented on purpose, each with the question that has to
        /// be answered before the rules can be written. Nothing in the game reads
        /// this; it exists so the list cannot drift out of date silently, and so
        /// RulesCheck can report it.
        ///
        /// Empty as of the design pass that settled Soul Swapper, Cthulu the
        /// Cosmic, First Line of Defense, the three draft Blessings, It Who
        /// Consumes, Suspicious Chef, and Baal. Add to it rather than guessing
        /// when a new card's text does not say enough to write rules from.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> NeedsDesignDecision =
            new Dictionary<string, string>();
    }
}
