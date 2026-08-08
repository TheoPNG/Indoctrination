using System;
using System.Collections.Generic;
using System.Linq;

namespace Indoctrination.Core.Effects
{
    public enum ChoiceKind
    {
        /// <summary>Pick a player, usually to be damaged or robbed.</summary>
        Player,

        /// <summary>Pick a resource colour.</summary>
        Color,

        /// <summary>Pick a card, by instance id.</summary>
        Card,

        /// <summary>Accept or decline an optional part of an effect.</summary>
        YesNo,

        /// <summary>Pick a number in a range, e.g. how many followers to spend.</summary>
        Amount,

        /// <summary>
        /// Pick one of a named list. Used where the thing being chosen is not a
        /// player, card, or colour - Cthulu, the Cosmic picks a counter type.
        /// </summary>
        Option
    }

    /// <summary>
    /// A question an effect needs answered before it can carry on. The effect
    /// yields one of these; the server asks the player; the answer is written
    /// back onto the same object and the effect resumes reading it.
    ///
    /// Carrying the answer on the request is what lets an effect ask, branch on
    /// the reply, and ask again - which cards like The Beast need, since how many
    /// targets it wants depends on dice rolled part-way through.
    /// </summary>
    public class ChoiceRequest
    {
        public ChoiceKind Kind { get; }
        public string Prompt { get; }

        /// <summary>Which player is being asked. Usually the card's controller.</summary>
        public int AskedOfPlayerId { get; }

        /// <summary>Legal player ids, for <see cref="ChoiceKind.Player"/>.</summary>
        public IReadOnlyList<int> PlayerOptions { get; private set; } = Array.Empty<int>();

        /// <summary>Legal card instance ids, for <see cref="ChoiceKind.Card"/>.</summary>
        public IReadOnlyList<int> CardOptions { get; private set; } = Array.Empty<int>();

        /// <summary>
        /// Legal colours, for <see cref="ChoiceKind.Color"/>. Empty means any of
        /// the four; Valefar restricts it to what the victim actually holds.
        /// </summary>
        public IReadOnlyList<ResourceColor> ColorOptions { get; private set; } = Array.Empty<ResourceColor>();

        /// <summary>Legal answers, for <see cref="ChoiceKind.Option"/>.</summary>
        public IReadOnlyList<string> Options { get; private set; } = Array.Empty<string>();

        public int MinAmount { get; private set; }
        public int MaxAmount { get; private set; }

        public bool Answered { get; private set; }

        public int ChosenPlayerId { get; private set; } = -1;
        public int ChosenCardId { get; private set; } = -1;
        public ResourceColor ChosenColor { get; private set; }
        public bool ChoseYes { get; private set; }
        public int ChosenAmount { get; private set; }
        public string ChosenOption { get; private set; }

        private ChoiceRequest(ChoiceKind kind, string prompt, int askedOf)
        {
            Kind = kind;
            Prompt = prompt;
            AskedOfPlayerId = askedOf;
        }

        public static ChoiceRequest Player(string prompt, int askedOf, IReadOnlyList<int> options)
        {
            return new ChoiceRequest(ChoiceKind.Player, prompt, askedOf) { PlayerOptions = options };
        }

        public static ChoiceRequest Color(string prompt, int askedOf, IReadOnlyList<ResourceColor> options = null)
        {
            return new ChoiceRequest(ChoiceKind.Color, prompt, askedOf)
            {
                ColorOptions = options ?? Array.Empty<ResourceColor>()
            };
        }

        public static ChoiceRequest Card(string prompt, int askedOf, IReadOnlyList<int> options)
        {
            return new ChoiceRequest(ChoiceKind.Card, prompt, askedOf) { CardOptions = options };
        }

        public static ChoiceRequest YesNo(string prompt, int askedOf)
        {
            return new ChoiceRequest(ChoiceKind.YesNo, prompt, askedOf);
        }

        public static ChoiceRequest Amount(string prompt, int askedOf, int min, int max)
        {
            return new ChoiceRequest(ChoiceKind.Amount, prompt, askedOf) { MinAmount = min, MaxAmount = max };
        }

        public static ChoiceRequest Option(string prompt, int askedOf, IReadOnlyList<string> options)
        {
            return new ChoiceRequest(ChoiceKind.Option, prompt, askedOf) { Options = options };
        }

        public void AnswerPlayer(int playerId)
        {
            Require(ChoiceKind.Player);
            if (PlayerOptions.Count > 0 && !Contains(PlayerOptions, playerId))
            {
                throw new ArgumentException($"Player {playerId} is not a legal choice here.");
            }

            ChosenPlayerId = playerId;
            Answered = true;
        }

        public void AnswerCard(int cardInstanceId)
        {
            Require(ChoiceKind.Card);
            if (CardOptions.Count > 0 && !Contains(CardOptions, cardInstanceId))
            {
                throw new ArgumentException($"Card {cardInstanceId} is not a legal choice here.");
            }

            ChosenCardId = cardInstanceId;
            Answered = true;
        }

        public void AnswerColor(ResourceColor color)
        {
            Require(ChoiceKind.Color);
            if (ColorOptions.Count > 0 && !ColorOptions.Contains(color))
            {
                throw new ArgumentException($"{color} is not a legal choice here.");
            }

            ChosenColor = color;
            Answered = true;
        }

        public void AnswerYesNo(bool yes)
        {
            Require(ChoiceKind.YesNo);
            ChoseYes = yes;
            Answered = true;
        }

        public void AnswerOption(string option)
        {
            Require(ChoiceKind.Option);
            if (!Options.Contains(option))
            {
                throw new ArgumentException($"'{option}' is not a legal choice here.");
            }

            ChosenOption = option;
            Answered = true;
        }

        public void AnswerAmount(int amount)
        {
            Require(ChoiceKind.Amount);
            if (amount < MinAmount || amount > MaxAmount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(amount), amount, $"Must be between {MinAmount} and {MaxAmount}.");
            }

            ChosenAmount = amount;
            Answered = true;
        }

        private void Require(ChoiceKind kind)
        {
            if (Kind != kind)
            {
                throw new InvalidOperationException($"This is a {Kind} choice, not {kind}.");
            }
        }

        private static bool Contains(IReadOnlyList<int> options, int value)
        {
            for (var i = 0; i < options.Count; i++)
            {
                if (options[i] == value)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
