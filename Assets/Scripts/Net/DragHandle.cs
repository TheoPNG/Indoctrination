using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Lets a card be picked up and dropped somewhere else on the board - a unit
    /// carried to a new place in its own compound, or a hand card carried onto
    /// the battlefield to play it.
    ///
    /// A light ghost follows the pointer rather than the real card being pulled
    /// out of its layout group, so nothing about the board's arrangement changes
    /// mid-drag; only on release does anything actually move, and only once the
    /// server has said so.
    /// </summary>
    public class DragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        /// <summary>Builds the floating stand-in shown while dragging.</summary>
        public Func<RectTransform> GhostFactory;

        /// <summary>Where the ghost is drawn - above every panel, so it is never hidden mid-drag.</summary>
        public RectTransform DragLayer;

        /// <summary>Called on release, with the pointer's final screen position.</summary>
        public Action<PointerEventData> OnDropped;

        private RectTransform _ghost;
        private CanvasGroup _dim;

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (GhostFactory == null || DragLayer == null)
            {
                return;
            }

            _ghost = GhostFactory();
            if (_ghost == null)
            {
                return;
            }

            _ghost.SetParent(DragLayer, false);
            _ghost.SetAsLastSibling();

            // Not `?? AddComponent`: Unity's "fake null" for a missing component
            // defeats the C# null-coalescing operator, which uses reference
            // equality and never sees GetComponent's null-looking-but-not-null
            // result as absent.
            var ghostGroup = _ghost.gameObject.GetComponent<CanvasGroup>();
            if (ghostGroup == null)
            {
                ghostGroup = _ghost.gameObject.AddComponent<CanvasGroup>();
            }

            ghostGroup.alpha = 0.88f;
            ghostGroup.blocksRaycasts = false;

            PositionGhost(eventData);

            // The card left behind dims, so it is obvious the real move has not
            // happened yet - only the drop decides that.
            _dim = gameObject.GetComponent<CanvasGroup>();
            if (_dim == null)
            {
                _dim = gameObject.AddComponent<CanvasGroup>();
            }

            _dim.alpha = 0.35f;
        }

        public void OnDrag(PointerEventData eventData)
        {
            PositionGhost(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_dim != null)
            {
                _dim.alpha = 1f;
            }

            if (_ghost != null)
            {
                Destroy(_ghost.gameObject);
                _ghost = null;
            }

            OnDropped?.Invoke(eventData);
        }

        private void PositionGhost(PointerEventData eventData)
        {
            if (_ghost == null)
            {
                return;
            }

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                DragLayer, eventData.position, eventData.pressEventCamera, out var local);
            _ghost.anchoredPosition = local;
        }

        /// <summary>
        /// A dimmed, non-interactive duplicate of a card, sized to match - the
        /// default ghost for any card-shaped drag.
        /// </summary>
        public static RectTransform CardGhost(CardView card, string tag, float width)
        {
            var ghost = BoardCardView.Create(null);
            ghost.Populate(card, tag, null);
            ghost.ScaleTo(width);

            var rect = (RectTransform)ghost.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            var raycaster = ghost.GetComponent<Graphic>();
            if (raycaster != null)
            {
                raycaster.raycastTarget = false;
            }

            var button = ghost.GetComponent<Button>();
            if (button != null)
            {
                button.interactable = false;
            }

            return rect;
        }
    }
}
