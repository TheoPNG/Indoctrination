using System;
using System.Collections.Generic;
using System.Linq;

namespace Indoctrination.Core
{
    /// <summary>
    /// One cult leader: their health and follower tracks, resources, hand,
    /// and the cards they have played into their compound.
    /// </summary>
    public class PlayerState
    {
        public int PlayerId { get; }
        public string Name { get; }

        public int Health { get; private set; } = GameSettings.StartingHealth;
        public int Followers { get; private set; } = GameSettings.StartingFollowers;

        /// <summary>
        /// Lowered by the cursed stones. Healing cannot take you above it.
        /// </summary>
        public int MaxHealth { get; private set; } = GameSettings.MaxHealth;

        /// <summary>
        /// Absorbs incoming damage before health does. Cleared between rounds
        /// unless something says otherwise.
        /// </summary>
        public int Block { get; private set; }

        public ResourcePool Resources { get; } = new();

        public List<CardInstance> Hand { get; } = new();

        /// <summary>Units and Blessings this player has in play.</summary>
        public List<CardInstance> Compound { get; } = new();

        /// <summary>
        /// Counters sitting on the player rather than on a card - flame counters
        /// from Arsonist and Fire Breather are the ones in use.
        /// </summary>
        private readonly Dictionary<string, int> _counters = new();

        /// <summary>
        /// One-turn switches thrown by cards - "prevent all damage you would take
        /// this turn", "you cannot deal damage next turn", and so on. See
        /// <see cref="TurnFlags"/> for the names in use.
        /// </summary>
        private readonly HashSet<string> _flags = new();

        /// <summary>Flags that start applying once the current turn ends.</summary>
        private readonly HashSet<string> _flagsNextTurn = new();

        /// <summary>This turn's primary (blue) die roll; 0 before the first roll.</summary>
        public int PrimaryDie { get; private set; }

        /// <summary>
        /// Extra primary dice from Standardized Uniforms. Units of every player
        /// activate on <see cref="PrimaryDie"/>, but only this player's units
        /// activate on these.
        /// </summary>
        public List<int> PrivateDice { get; } = new();

        public bool IsAlive => Health > 0;
        public bool HasWon => Followers >= GameSettings.FollowersToWin;

        // Turn-scoped tallies. A surprising number of cards ask about what has
        // already happened this turn, so they are counted as they happen rather
        // than reconstructed after the fact.
        public int DamageTakenThisTurn { get; private set; }
        public int DamageDealtThisTurn { get; private set; }
        public int HealthGainedThisTurn { get; private set; }
        public int FollowersGainedThisTurn { get; private set; }
        public int UnitsTriggeredThisTurn { get; set; }
        public int AuxiliaryDiceRolledThisTurn { get; set; }

        public int GetCounter(string name) => _counters.GetValueOrDefault(name);

        public void AddCounter(string name, int amount = 1)
        {
            _counters[name] = Math.Max(0, GetCounter(name) + amount);
        }

        public void ClearCounter(string name) => _counters.Remove(name);

        public bool HasFlag(string name) => _flags.Contains(name);

        public void SetFlag(string name) => _flags.Add(name);

        public void ClearFlag(string name) => _flags.Remove(name);

        /// <summary>
        /// Throws a switch that only takes effect from the next turn onward -
        /// Bribery and Siege both punish you starting the turn after they land.
        /// </summary>
        public void SetFlagNextTurn(string name) => _flagsNextTurn.Add(name);

        public void AddBlock(int amount)
        {
            Block = Math.Max(0, Block + amount);
        }

        public void ClearBlock() => Block = 0;

        public void ModifyMaxHealth(int amount)
        {
            MaxHealth = Math.Max(1, MaxHealth + amount);
            Health = Math.Min(Health, MaxHealth);
        }

        /// <summary>Wipes the per-turn tallies. Called by GameState at end of turn.</summary>
        public void EndTurn()
        {
            DamageTakenThisTurn = 0;
            DamageDealtThisTurn = 0;
            HealthGainedThisTurn = 0;
            FollowersGainedThisTurn = 0;
            UnitsTriggeredThisTurn = 0;
            AuxiliaryDiceRolledThisTurn = 0;
            PrivateDice.Clear();

            _flags.Clear();
            foreach (var flag in _flagsNextTurn)
            {
                _flags.Add(flag);
            }

            _flagsNextTurn.Clear();
        }

        public void RecordDamageDealt(int amount) => DamageDealtThisTurn += amount;

        public PlayerState(int playerId, string name)
        {
            PlayerId = playerId;
            Name = name;
        }

        public void SetPrimaryDie(int value)
        {
            if (value < 1 || value > GameSettings.DieSides)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Die roll must be 1-6.");
            }

            PrimaryDie = value;
        }

        /// <summary>
        /// Applies damage that has already been through the modifier pipeline.
        /// Block soaks it first; only what is left over reaches health.
        /// Go through GameState.DealDamage rather than calling this directly.
        /// </summary>
        public void TakeDamage(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            var absorbed = Math.Min(Block, amount);
            Block -= absorbed;

            var throughToHealth = amount - absorbed;
            Health = Math.Max(0, Health - throughToHealth);
            DamageTakenThisTurn += amount;
        }

        /// <summary>
        /// Health paid rather than health lost to an attack. Block does not stop
        /// it and it is not counted as damage taken, so cards that reward you for
        /// being hurt do not pay out on your own bargains.
        /// </summary>
        public void LoseHealth(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            Health = Math.Max(0, Health - amount);
        }

        /// <summary>
        /// Puts back health that was just taken. Not healing: it skips the heal
        /// modifiers and does not count towards <see cref="HealthGainedThisTurn"/>,
        /// because nothing was gained - First Line of Defense redirected the wound
        /// onto the follower track, so the damage is un-taken rather than repaired.
        /// </summary>
        public void RestoreHealth(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            Health = Math.Min(MaxHealth, Health + amount);
            DamageTakenThisTurn = Math.Max(0, DamageTakenThisTurn - amount);
        }

        public void Heal(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            var before = Health;
            Health = Math.Min(MaxHealth, Health + amount);
            HealthGainedThisTurn += Health - before;
        }

        public void GainFollowers(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            Followers += amount;
            FollowersGainedThisTurn += amount;
        }

        public void LoseFollowers(int amount)
        {
            Followers = Math.Max(GameSettings.MinFollowers, Followers - amount);
        }

        /// <summary>
        /// Units in the compound that activate on the given die value. Blessings
        /// never activate this way, which <see cref="CardInstance.ActivatesOn"/>
        /// already enforces.
        /// </summary>
        public IEnumerable<CardInstance> UnitsActivatingOn(int dieValue)
        {
            return Compound.Where(card => card.ActivatesOn(dieValue));
        }

        /// <summary>Whether this player has the named card in play.</summary>
        public bool HasInPlay(string definitionId)
        {
            return Compound.Any(card => card.Definition.Id == definitionId);
        }

        public CardInstance FindInPlay(string definitionId)
        {
            return Compound.FirstOrDefault(card => card.Definition.Id == definitionId);
        }

        public override string ToString()
        {
            return $"{Name} (HP {Health}, {Followers} followers)";
        }
    }
}
