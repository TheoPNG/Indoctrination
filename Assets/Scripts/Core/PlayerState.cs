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

        public ResourcePool Resources { get; } = new();

        public List<CardInstance> Hand { get; } = new();

        /// <summary>Units and Blessings this player has in play.</summary>
        public List<CardInstance> Compound { get; } = new();

        /// <summary>This turn's primary (blue) die roll; 0 before the first roll.</summary>
        public int PrimaryDie { get; private set; }

        public bool IsAlive => Health > 0;
        public bool HasWon => Followers >= GameSettings.FollowersToWin;

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

        public void TakeDamage(int amount)
        {
            Health = Math.Max(0, Health - amount);
        }

        public void Heal(int amount)
        {
            Health += amount;
        }

        public void GainFollowers(int amount)
        {
            Followers += amount;
        }

        public void LoseFollowers(int amount)
        {
            Followers = Math.Max(0, Followers - amount);
        }

        /// <summary>Units in the compound that activate on the given die value.</summary>
        public IEnumerable<CardInstance> UnitsActivatingOn(int dieValue)
        {
            return Compound.Where(card => card.ActivatesOn(dieValue));
        }

        public override string ToString()
        {
            return $"{Name} (HP {Health}, {Followers} followers)";
        }
    }
}
