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

        /// <summary>
        /// Reports the live pointer while a card is carried, for destinations
        /// that light up before the player releases it.
        /// </summary>
        public Action<PointerEventData> OnDragMoved;

        /// <summary>
        /// Raised however the drag ends - dropped, or abandoned because the card
        /// stopped existing. Anything switched on for the duration of a drag has
        /// to be switched off from here, not from <see cref="OnDropped"/>, or it
        /// stays on forever when the drag is abandoned.
        /// </summary>
        public Action OnDragFinished;

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
            OnDragMoved?.Invoke(eventData);

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
            OnDragMoved?.Invoke(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            ClearGhost();
            OnDropped?.Invoke(eventData);
        }

        /// <summary>
        /// The ghost is destroyed from here as well as from OnEndDrag, because
        /// OnEndDrag is not guaranteed to run at all.
        ///
        /// The board destroys and rebuilds its cards whenever the server sends a
        /// new view, which can easily happen mid-drag. Unity does not deliver
        /// OnEndDrag to a destroyed object, so the ghost - which lives on the
        /// flight layer, not on the card - was orphaned there permanently: a
        /// half-transparent card sitting over the board for the rest of the
        /// session, surviving even a return to the connect screen, because the
        /// flight layer is a sibling of the board and never hidden with it.
        ///
        /// Tying cleanup to this component's own lifetime is what makes it
        /// unconditional. Anything parented outside the object that created it
        /// needs the same treatment.
        /// </summary>
        private void OnDisable() => ClearGhost();

        private void OnDestroy() => ClearGhost();

        /// <summary>Safe to call repeatedly, and from any of the ways a drag can end.</summary>
        private void ClearGhost()
        {
            if (_dim != null)
            {
                _dim.alpha = 1f;
            }

            if (_ghost != null)
            {
                var doomed = _ghost.gameObject;
                _ghost = null;

                if (Application.isPlaying)
                {
                    Destroy(doomed);
                }
                else
                {
                    DestroyImmediate(doomed);
                }

                // Only announced when there was actually a drag in progress, so
                // a plain disable does not fire it.
                OnDragFinished?.Invoke();
            }
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
