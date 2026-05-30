namespace SyncfusionKanban;

public class KanbanCard
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "Open";
    public string Title { get; set; } = "New card";
    public string Summary { get; set; } = "";
    public string Assignee { get; set; } = "";
    public string Priority { get; set; } = "Normal";
    public double Order { get; set; }
}
