namespace DBMonitor.Models.ViewModels;

public class SqlExecuteRequest
{
    public string?  Sql              { get; set; }
    public int?     TimeoutSeconds   { get; set; }
    public int?     MaxRows          { get; set; }
    public bool     AllowDestructive { get; set; }
}
