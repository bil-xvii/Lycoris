using Lycoris.Core;
using Lycoris.Modules.Restore;
using Xunit;

namespace Lycoris.Tests;

/// <summary>Obvious UI for the Restore page: batches collapsed by default, one state vocabulary.</summary>
public class RestoreUxTests
{
    private static BatchItem Batch(int records)
    {
        var batch = new ChangeBatch { Title = "Пакет" };
        var list = Enumerable.Range(0, records).Select(i => new ChangeRecord { BatchId = batch.Id, Title = "r" + i }).ToList();
        var group = new JournalGroup(batch, list, list.Count, list.Count);
        return new BatchItem(group, r => new RecordItem(r));
    }

    [Fact]
    public void Batches_are_collapsed_by_default_and_the_button_text_follows_the_state()
    {
        var item = Batch(3);
        Assert.False(item.IsExpanded);
        Assert.Equal("ПОКАЗАТЬ ЗАПИСИ · 3", item.ExpandText);

        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        item.IsExpanded = true;
        Assert.Equal("СКРЫТЬ ЗАПИСИ · 3", item.ExpandText);
        Assert.Contains(nameof(BatchItem.ExpandText), changed);
    }

    [Fact]
    public void Active_record_uses_the_shared_state_vocabulary()
    {
        var item = new RecordItem(new ChangeRecord { Title = "A" });
        Assert.Equal("ИЗМЕНЕНО Lycoris", item.StatusText);
        Assert.True(item.CanRevert);
    }
}
