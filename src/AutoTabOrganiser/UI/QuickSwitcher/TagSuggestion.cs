using AutoTabOrganiser.UI.ViewModels;

namespace AutoTabOrganiser.UI.QuickSwitcher
{
    /// <summary>One row of the Quick Switcher's tag autocomplete strip: a coloured chip plus
    /// how many tabs currently carry the tag.</summary>
    internal sealed class TagSuggestion
    {
        public TagChip Chip { get; }
        public int Count { get; }

        public string Text => Chip.Text;
        public string CountText => Count.ToString();

        public TagSuggestion(TagChip chip, int count)
        {
            Chip = chip;
            Count = count;
        }
    }
}
