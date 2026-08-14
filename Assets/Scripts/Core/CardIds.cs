namespace Indoctrination.Core
{
    /// <summary>
    /// Every card id, as a constant. Effects and modifiers refer to other
    /// cards by name a lot, and a typo in a bare string is silent - the card
    /// simply never finds the thing it is looking for. RulesCheck confirms
    /// every constant here matches a real card in Cards.json.
    /// </summary>
    public static class CardIds
    {
        // Blessings
        public const string AllPartsOfTheAnimal = "All_Parts_of_the_Animal";
        public const string BlockedByGames = "Blocked_by_Games";
        public const string Bloodstone = "Bloodstone";
        public const string Bloodthirst = "Bloodthirst";
        public const string BoonOf = "Boon_of";
        public const string Casino = "Casino";
        public const string ClownCult = "Clown_Cult";
        public const string CthuluSMaw = "Cthulu_s_Maw";
        public const string CultLeaderSParkingSpot = "Cult_Leader_s_Parking_Spot";
        public const string CursedBloodstone = "Cursed_Bloodstone";
        public const string CursedMindstone = "Cursed_Mindstone";
        public const string CursedShieldstone = "Cursed_Shieldstone";
        public const string CursedWealthstone = "Cursed_Wealthstone";
        public const string FirstLineOfDefense = "First_Line_of_Defense";
        public const string GamblingProblem = "Gambling_Problem";
        public const string HalphasWings = "Halphas_Wings";
        public const string HumanTrap = "Human_Trap";
        public const string HumanZoo = "Human_Zoo";
        public const string KnowledgeIsPower = "Knowledge_is_Power";
        public const string Masochism = "Masochism";
        public const string Meatshield = "Meatshield";
        public const string MedicineCabinet = "Medicine_Cabinet";
        public const string Mindstone = "Mindstone";
        public const string MongolMythology = "Mongol_Mythology";
        public const string Overzealous = "Overzealous";
        public const string PainLovers = "Pain_Lovers";
        public const string PoweredByThePeople = "Powered_by_the_People";
        public const string Resourceful = "Resourceful";
        public const string Shieldstone = "Shieldstone";
        public const string StandardizedUniforms = "Standardized_Uniforms";
        public const string StarEyed = "Star_Eyed";
        public const string SufferingFromSuccess = "Suffering_from_Success";
        public const string ThreeSACrowd = "Three_s_a_Crowd";
        public const string TitanstopperChurchOfWalls = "Titanstopper_Church_of_Walls";
        public const string TryAgain = "Try_again";
        public const string Wealthstone = "Wealthstone";
        public const string WhatAreYouLaughingAt = "What_are_you_laughing_at";
        public const string WhoreSRevenge = "Whore_s_Revenge";
        public const string WondrousBlood = "Wondrous_Blood";

        // Rituals
        public const string Ascension = "Ascension";
        public const string Assassinate = "Assassinate";
        public const string BloodyMargarita = "Bloody_Margarita";
        public const string Bribery = "Bribery";
        public const string ChemicalWeapons = "Chemical_Weapons";
        public const string CloseEnough = "Close_Enough";
        public const string CompoundWalls = "Compound_Walls";
        public const string Equality = "Equality";
        public const string IntimateGroupConnection = "Intimate_group_connection";
        public const string RadicalTactics = "Radical_Tactics";
        public const string ReviveTheForgotten = "Revive_the_Forgotten";
        public const string Sermon = "Sermon";
        public const string Siege = "Siege";
        public const string SmearCampaign = "Smear_Campaign";
        public const string SummonGoneWrong = "Summon_Gone_Wrong";
        public const string SupernaturalEvent = "Supernatural_Event";
        public const string TheSecondComing = "The_Second_Coming";
        public const string VaccinesForAll = "Vaccines_for_All";
        public const string VirginSacrifice = "Virgin_Sacrifice";

        // Units
        public const string AlmostAVampire = "Almost_a_Vampire";
        public const string AlternativeMedicine = "Alternative_Medicine";
        public const string AngryDrunkMonkey = "Angry_Drunk_Monkey";
        public const string Arsonist = "Arsonist";
        public const string AsherPirozzi = "Asher_Pirozzi";
        public const string Asmodeus = "Asmodeus";
        public const string AxeLicker = "Axe_Licker";
        public const string BaalTheManipulator = "Baal_The_Manipulator";
        public const string BeekeeperCultist = "Beekeeper_Cultist";
        public const string BeingOfHearthlessness = "Being_of_Hearthlessness";
        public const string BelleOfTheBall = "Belle_of_the_Ball";
        public const string BloodCollector = "Blood_Collector";
        public const string BloodDiamondMine = "Blood_Diamond_Mine";
        public const string BloodSacrifice = "Blood_Sacrifice";
        public const string BloodyMooner = "Bloody_Mooner";
        public const string Bodyguard = "Bodyguard";
        public const string Bop = "Bop";
        public const string Celebrity = "Celebrity";
        public const string ChiefSacrificer = "Chief_Sacrificer";
        public const string Communist = "Communist";
        public const string CompoundLandmines = "Compound_Landmines";
        public const string ConfusedMan = "Confused_Man";
        public const string Cornucopia = "Cornucopia";
        public const string CrystalMine = "Crystal_Mine";
        public const string CthuluTheCosmic = "Cthulu_The_Cosmic";
        public const string CultSPetTigerForArt = "Cult_s_Pet_Tiger_for_art";
        public const string CultSPetSDad = "Cult_s_Pet_s_Dad";
        public const string CultSPetSPet = "Cult_s_Pet_s_Pet";
        public const string DoubleAgentJapaneseArt = "Double_Agent_Japanese_Art";
        public const string DrunkenFollower = "Drunken_Follower";
        public const string FelineCultist = "Feline_Cultist";
        public const string FilipinaNurse = "Filipina_Nurse";
        public const string FireBreather = "Fire_Breather";
        public const string FriendOfTheBeasts = "Friend_of_the_Beasts";
        public const string GoldMine = "Gold_Mine";
        public const string Grandma = "Grandma";
        public const string HeWhoRemembers = "He_Who_Remembers";
        public const string HierophantSFavorite = "Hierophant_s_Favorite";
        public const string HydroPlant = "Hydro_Plant";
        public const string Initiate4 = "Initiate_4";
        public const string InspiredFool = "Inspired_Fool";
        public const string IsHeOnMeth = "Is_he_on_meth";
        public const string ItWhoConsumes = "It_Who_Consumes";
        public const string JormTrustEater = "Jorm_Trust_Eater";
        public const string JormugandrsFanClub = "Jormugandrs_Fan_Club";
        public const string KoolAid = "Kool_Aid";
        public const string ManipulatorOfTheMasses = "Manipulator_of_the_Masses";
        public const string Masochist = "Masochist";
        public const string MasterMarketer = "Master_Marketer";
        public const string MasterShaman = "Master_Shaman";
        public const string MoneyTree = "Money_Tree";
        public const string NuclearPlant = "Nuclear_Plant";
        public const string OminousEye = "Ominous_Eye";
        public const string OneWithTheChildren = "One_With_the_Children";
        public const string Pentagram = "Pentagram";
        public const string Propaganda = "Propaganda";
        public const string QuestionableDoctor = "Questionable_Doctor";
        public const string ResearcherOfTheOldWays = "Researcher_of_the_Old_Ways";
        public const string Ritualist = "Ritualist";
        public const string SacrificialLamb = "Sacrificial_Lamb";
        public const string Satanist = "Satanist";
        public const string Security = "Security";
        public const string SeniorRecruiter = "Senior_Recruiter";
        public const string ShieldWizard = "Shield_Wizard";
        public const string Slanderist = "Slanderist";
        public const string SnakeEyes = "Snake_Eyes";
        public const string SolarPanels = "Solar_Panels";
        public const string SoulSwapper = "Soul_Swapper";
        public const string SuspiciousChef = "Suspicious_Chef";
        public const string TheAllmother = "The_Allmother";
        public const string TheBeast = "The_Beast";
        public const string TheBeastmaster = "The_Beastmaster";
        public const string Valefar = "Valefar";
        public const string Vampire = "Vampire";
        public const string WallBuilder = "Wall_Builder";
        public const string WisdomHoarder = "Wisdom_Hoarder";
        public const string WitchDoctor = "Witch_Doctor";
        public const string WorshiperOfTheBoneGod = "Worshiper_of_the_Bone_God";
        public const string YouthLeader = "Youth_Leader";
    }
}
