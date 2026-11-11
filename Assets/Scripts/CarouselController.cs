using System.Collections.Generic;
using UnityEngine;

public class CarouselController : MonoBehaviour
{
    [Header("Rail")]
    [SerializeField] private RectTransform rail;

    [SerializeField] private List<RectTransform> items = new List<RectTransform>();

    [Header("Layout")]
    [SerializeField] private float itemWidth = 650f;

    [Header("Motion")]
    [SerializeField] private float smoothTime = 0.25f;

    private const float PositionEpsilon = 0.01f;

    private int railIndex = 1;
    private int displayIndex = 0;
    private float targetX = 0f;
    private float velocityX = 0f;
    private bool waitingToSettle = false;
    private bool clampOnly = false;

    private readonly List<RectTransform> realItems = new List<RectTransform>();
    private readonly List<Vector2> realItemPosition = new List<Vector2>();

    public int CurrentIndex => displayIndex;
    public int ItemCount => items != null ? items.Count : 0;

    void Awake() => ResetState();

    void Start() => BuildRail();

    void OnDisable()
    {
        if (waitingToSettle)
            SnapToRealItem();
    }

    void Update()
    {
        if (rail == null)
            return;

        float newX = Mathf.SmoothDamp(
            rail.anchoredPosition.x, targetX, ref velocityX, smoothTime);
        rail.anchoredPosition = new Vector2(newX, rail.anchoredPosition.y);

        if (waitingToSettle &&
            Mathf.Abs(rail.anchoredPosition.x - targetX) <= PositionEpsilon)
        {
            SnapToRealItem();
        }
    }

    public void Next() => MoveBy(1);
    public void Previous() => MoveBy(-1);

    public void ResetToStart()
    {
        if (rail == null)
            return;

        displayIndex = 0;
        railIndex = 1;
        waitingToSettle = false;
        RepositionRail(railIndex, instant: true);

        Debug.Log($"Carousel reset to index 0 (rail slot {railIndex})");
    }

    private void ResetState()
    {
        railIndex = 1;
        displayIndex = 0;
        targetX = 0f;
        velocityX = 0f;
        waitingToSettle = false;
    }

    private void MoveBy(int step)
    {
        if (clampOnly || ItemCount < 2)
        {
            int clamped = Mathf.Clamp(displayIndex + step, 0, ItemCount - 1);
            if (clamped == displayIndex)
            {
                Debug.Log($"Carousel: already at index {displayIndex} " +
                          $"(of {ItemCount}), not moving.");
                return;
            }
            displayIndex = clamped;
            railIndex = clamped + 1;
        }
        else
        {
            railIndex = Mathf.Clamp(railIndex + step, 1, ItemCount);
            displayIndex = railIndex - 1;
        }

        Debug.Log($"Carousel index: {displayIndex} (rail slot {railIndex})");
        ApplyMotion();
    }

    private void ApplyMotion()
    {
        if (rail == null)
            return;

        if (waitingToSettle)
            SnapToRealItem();

        waitingToSettle = true;
        RepositionRail(railIndex, instant: false);
    }

    private void BuildRail()
    {
        if (items == null || items.Count == 0)
        {
            Debug.LogWarning("CarouselController: no items assigned.");
            return;
        }

        if (rail == null)
            rail = items[0] != null ? items[0].parent as RectTransform : null;

        if (rail == null)
        {
            Debug.LogError("CarouselController: no rail and no item parent.");
            return;
        }

        if (itemWidth <= 0f)
        {
            Debug.LogError(
                $"CarouselController: itemWidth must be > 0 (got {itemWidth}).");
            return;
        }

        int count = items.Count;

        realItems.Clear();
        realItemPosition.Clear();
        for (int i = 0; i < count; i++)
        {
            if (items[i] == null)
            {
                Debug.LogError($"CarouselController: items[{i}] is empty.");
                return;
            }
            realItems.Add(items[i]);
            realItemPosition.Add(items[i].anchoredPosition);
        }

        SortRealItemsBySiblingIndex();

        float railY = rail.anchoredPosition.y;

        if (count < 2)
        {
            clampOnly = true;
            SetItemPosition(realItems[0], 1);
            railIndex = 1;
            displayIndex = 0;
            RepositionRail(railIndex, instant: true);
            Debug.LogWarning(
                $"CarouselController: {count} item(s); wrapping needs 2+, " +
                "clamping instead.");
            return;
        }

        WarnIfSpacingLooksWrong(count);

        for (int i = 0; i < count; i++)
            realItems[i].SetSiblingIndex(i);

        for (int i = 0; i < count; i++)
            SetItemPosition(realItems[i], i + 1);

        railIndex = 1;
        displayIndex = 0;
        RepositionRail(railIndex, instant: true, railY: railY);
    }

    private void SnapToRealItem()
    {
        if (rail == null)
            return;

        velocityX = 0f;
        waitingToSettle = false;
    }

    private float SlotX(int slot) => -(slot - 1) * itemWidth;

    private void RepositionRail(int slot, bool instant, float? railY = null)
    {
        targetX = SlotX(slot);
        velocityX = 0f;
        if (instant)
        {
            waitingToSettle = false;
            rail.anchoredPosition = new Vector2(
                targetX, railY ?? rail.anchoredPosition.y);
        }
    }

    private void SetItemPosition(RectTransform item, int index)
    {
        item.anchoredPosition = new Vector2(
            (index - 1) * itemWidth, item.anchoredPosition.y);
    }

    private void SortRealItemsBySiblingIndex()
    {
        int count = realItems.Count;
        for (int i = 1; i < count; i++)
        {
            RectTransform item = realItems[i];
            Vector2 position = realItemPosition[i];
            int index = item.GetSiblingIndex();
            int j = i - 1;

            while (j >= 0 && realItems[j].GetSiblingIndex() > index)
            {
                realItems[j + 1] = realItems[j];
                realItemPosition[j + 1] = realItemPosition[j];
                j--;
            }
            realItems[j + 1] = item;
            realItemPosition[j + 1] = position;
        }
    }

    private void WarnIfSpacingLooksWrong(int count)
    {
        float expected = (count - 1) * itemWidth;
        float actual = realItemPosition[count - 1].x - realItemPosition[0].x;

        if (Mathf.Abs(expected - actual) > PositionEpsilon)
        {
            Debug.LogWarning(
                $"CarouselController: itemWidth ({itemWidth}) does not match " +
                $"actual spacing ({actual / (count - 1)} between neighbours, " +
                $"from {realItems[0].name}@{realItemPosition[0].x} to " +
                $"{realItems[count - 1].name}@{realItemPosition[count - 1].x}).");
        }
    }
}